using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using Org.BouncyCastle.Asn1.Pkcs;

namespace EpinelPS.LobbyServer.LobbyUser.Mail;

[GameRequest("/mail/allobtain")]
public class AllObtain : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqAllObtainMail req = await ReadData<ReqAllObtainMail>();
        User user = GetUser();
        var result = MailRewardClaims.Claim(user, null);
        ResAllObtainMail response = new() { Reward = result.Reward,
            SucceedMails = { result.Claimed.Select(m => new EpinelPS.Mail { Msn = m.Msn, Type = m.Type }) } };
        await WriteDataAsync(response);
    }
}
