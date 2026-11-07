using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.Utils;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.LobbyServer.Controllers;

[ApiController]
[Route("/v1/shootingrange")]
public class ShootingRangeController(IUserService users) : ControllerBase
{
    [HttpPost("v2/get")]
    public ActionResult<ResGetShootingRangeRankingV2> Rankings([FromBodyProtobuf] ReqGetShootingRangeRankingV2 req)
    {
        lock (JsonDb.SaveLock)
        {
            var user = users.GetUser();
            if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
            return GuildDirectory.Find(user.Guild.guildId ?? 0) is { } guild
                ? ShootingRangeService.Rankings(user, guild)
                : new ResGetShootingRangeRankingV2 { Result = ShootingRangeRankingResult.GetShootingRangeRankingResultNotMember };
        }
    }

    [HttpPost("v2/set")]
    public ActionResult<ResSetShootingRangeScoreV2> Submit([FromBodyProtobuf] ReqSetShootingRangeScoreV2 req)
    {
        var user = users.GetUser();
        if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(user.ID) != user) return Problem(statusCode: 409, type: NetUtils.InvalidSessionErrorType);
            try
            {
                var nonce = HttpContext.Items[nameof(PacketDecryptResponse.Nonce)] as byte[];
                return ShootingRangeService.Submit(user, req, nonce);
            }
            catch (BadHttpRequestException ex) { return Problem(statusCode: ex.StatusCode, detail: ex.Message); }
        }
    }

    [HttpPost("getbattlelog")]
    public ActionResult<ResGetShootingRangeBattleLog> History([FromBodyProtobuf] ReqGetShootingRangeBattleLog req)
    {
        lock (JsonDb.SaveLock)
        {
            var user = users.GetUser();
            if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
            if (GuildDirectory.Find(user.Guild.guildId ?? 0) == null) return Problem(statusCode: 403);
            return ShootingRangeService.History(user);
        }
    }
}
