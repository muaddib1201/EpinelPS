using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer.Intercept;
using EpinelPS.Utils;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.LobbyServer.Controllers;

[ApiController]
[Route("/v1/intercept")]
public class InterceptionController(IUserService users, TimeProvider? clock = null) : ControllerBase
{
    [HttpPost("get")]
    public ActionResult<ResGetInterceptData> Get([FromBodyProtobuf] ReqGetInterceptData req) => Run(s => s.Get());

    [HttpPost("check")]
    public ActionResult<ResCheckClearInterceptToday> Check([FromBodyProtobuf] ReqCheckClearInterceptToday req) =>
        Run(s => new ResCheckClearInterceptToday { Clear = s.HasRecord(req.InterceptType, req.InterceptId) });

    [HttpPost("clear")]
    public ActionResult<ResClearIntercept> Clear([FromBodyProtobuf] ReqClearIntercept req) => Run(s => s.Clear(req));

    [HttpPost("fastclear")]
    public ActionResult<ResFastClearIntercept> FastClear([FromBodyProtobuf] ReqFastClearIntercept req) => Run(s => s.FastClear(req));

    private ActionResult<T> Run<T>(Func<InterceptionService, T> action)
    {
        var user = users.GetUser();
        if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(user.ID) != user) return Problem(statusCode: 409, type: NetUtils.InvalidSessionErrorType);
            try
            {
                user.ResetDataIfNeeded();
                var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
                var nonce = HttpContext.Items[nameof(PacketDecryptResponse.Nonce)] as byte[];
                return action(new InterceptionService(user, now, nonce));
            }
            catch (BadHttpRequestException ex) { return Problem(statusCode: ex.StatusCode, detail: ex.Message); }
        }
    }
}
