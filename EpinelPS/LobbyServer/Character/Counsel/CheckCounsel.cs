using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Character.Counsel;

[GameRequest("/character/counsel/check")]
public class CheckCounsel : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqCounseledBefore req = await ReadData<ReqCounseledBefore>();

        ResCounseledBefore response = new();

        var user = GetUser();
        lock (user)
        lock (JsonDb.SaveLock)
        {
            // A failed concurrent save can replace the locked user with its snapshot.
            var currentUser = GetUser();
            response.IsCounseledBefore = GameData.Instance.AttractiveCounselTable.TryGetValue(req.CounselTid, out var counsel) &&
                currentUser.BondInfo.Any(bond => bond.NameCode == counsel.NameCode && bond.CounselDialogCompleteIds.Contains(req.CounselTid));
        }

        await WriteDataAsync(response);
    }
}
