using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer.Event;
using EpinelPS.Utils;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.LobbyServer.Controllers;

[ApiController]
[Route("/v1/event/challengestage")]
public class ChallengeStageController(IUserService users, TimeProvider? clock = null) : ControllerBase
{
    [HttpPost("get")]
    public ActionResult<ResChallengeEventStageData> Get([FromBodyProtobuf] ReqChallengeEventStageData req) =>
        Run(req.EventId, stage => stage.Get());

    [HttpPost("enter")]
    public ActionResult<ResEnterChallengeEventStage> Enter([FromBodyProtobuf] ReqEnterChallengeEventStage req) =>
        Run(req.EventId, stage => stage.Enter(req));

    [HttpPost("clear")]
    public ActionResult<ResClearChallengeEventStage> Clear([FromBodyProtobuf] ReqClearChallengeEventStage req) =>
        Run(req.EventId, stage => stage.Clear(req));

    [HttpPost("fastclear")]
    public ActionResult<ResFastClearChallengeEventStage> FastClear([FromBodyProtobuf] ReqFastClearChallengeEventStage req) =>
        Run(req.EventId, stage => stage.FastClear(req));

    private ActionResult<T> Run<T>(int eventId, Func<ChallengeStage, T> action)
    {
        var user = users.GetUser();
        if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
        lock (user)
        lock (JsonDb.SaveLock)
        {
            try
            {
                var nonce = HttpContext.Items[nameof(PacketDecryptResponse.Nonce)] as byte[];
                var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
                var stage = new ChallengeStage(user, eventId, now, nonce);
                stage.Save();
                var result = action(stage);
                stage.Save();
                return result;
            }
            catch (BadHttpRequestException error)
            {
                return Problem(detail: error.Message, statusCode: error.StatusCode, type: NetUtils.InvalidSessionErrorType);
            }
        }
    }
}
