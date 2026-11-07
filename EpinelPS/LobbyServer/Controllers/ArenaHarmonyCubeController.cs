using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer.Arena;
using EpinelPS.Utils;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.LobbyServer.Controllers;

[ApiController]
[Route("/v1/inventory/harmonycube/arena")]
public class ArenaHarmonyCubeController(IUserService users) : ControllerBase
{
    [HttpPost("wear")]
    public ActionResult<ResWearArenaHarmonyCube> Wear([FromBodyProtobuf] ReqWearArenaHarmonyCube request) =>
        Update(user => ArenaHarmonyCubeService.Wear(user, request));

    [HttpPost("clear")]
    public ActionResult<ResClearArenaHarmonyCube> Clear([FromBodyProtobuf] ReqClearArenaHarmonyCube request) =>
        Update(user => ArenaHarmonyCubeService.Clear(user, request));

    [HttpPost("management")]
    public ActionResult<ResManagementArenaHarmonyCube> Manage([FromBodyProtobuf] ReqManagementArenaHarmonyCube request) =>
        Update(user => ArenaHarmonyCubeService.Manage(user, request));

    private ActionResult<T> Update<T>(Func<User, T> update)
    {
        var user = users.GetUser();
        if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(user.ID) != user) return Problem(statusCode: 409, type: NetUtils.InvalidSessionErrorType);
            try { return update(user); }
            catch (BadHttpRequestException exception) { return Problem(statusCode: exception.StatusCode, detail: exception.Message); }
        }
    }
}
