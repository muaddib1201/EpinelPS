using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer.Friend;
using EpinelPS.Utils;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.LobbyServer.Controllers;

[ApiController]
[Route("/v1/friend")]
public class FriendController(IUserService users, GameContext db) : ControllerBase
{
    [HttpPost("get")]
    public ActionResult<ResGetFriendData> Get([FromBodyProtobuf] ReqGetFriendData request) => Read(user => FriendshipService.Get(user, db, DateTime.UtcNow));

    [HttpPost("sendfriendship")]
    public ActionResult<ResSendFriendshipPoint> Send([FromBodyProtobuf] ReqSendFriendshipPoint request) =>
        Read(user => FriendshipService.Send(user, request.Fusn > 0 ? (ulong)request.Fusn : 0, DateTime.UtcNow, Nonce));

    [HttpPost("obtainfriendship")]
    public ActionResult<ResObtainFriendshipPoint> Obtain([FromBodyProtobuf] ReqObtainFriendshipPoint request) =>
        Read(user => FriendshipService.Obtain(user, request.Fusn > 0 ? (ulong)request.Fusn : 0, DateTime.UtcNow, Nonce));

    [HttpPost("allfriendship")]
    public ActionResult<ResAllFriendshipPoint> All([FromBodyProtobuf] ReqAllFriendshipPoint request) => Read(user => FriendshipService.All(user, db, DateTime.UtcNow, Nonce));

    [HttpPost("delete")]
    public ActionResult<ResDeleteFriend> Delete([FromBodyProtobuf] ReqDeleteFriend request) =>
        Read(user => FriendshipService.Delete(user, request.Fusn > 0 ? (ulong)request.Fusn : 0, DateTime.UtcNow, Nonce));

    private byte[]? Nonce => HttpContext.Items[nameof(PacketDecryptResponse.Nonce)] as byte[];

    private ActionResult<T> Read<T>(Func<User, T> action)
    {
        var user = users.GetUser();
        if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(user.ID) != user) return Problem(statusCode: 409, type: NetUtils.InvalidSessionErrorType);
            return action(user);
        }
    }
}
