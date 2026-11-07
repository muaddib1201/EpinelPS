using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.LobbyUser.Mail;

[GameRequest("/mail/obtain2")]
public class Obtain2 : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqObtainMail2 req = await ReadData<ReqObtainMail2>();
        User user = GetUser();
        var result = MailRewardClaims.Claim(user, req.Msn);
        ResObtainMail2 response = new() { Data = user.MailDatas[req.Msn].Clone(), Result = ObtainMailResult.Success, Reward = result.Reward };
        await WriteDataAsync(response);
    }
}
