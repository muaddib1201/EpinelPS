using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.LobbyServer.LobbyUser;

[GameRequest("/lobby/usertitle/get")]
public class GetUserTitle : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        await ReadData<ReqGetUserTitleList>();
        var user = GetUser();
        var catalogue = GameData.Instance.userTitleRecords;
        ResGetUserTitleList response = new();
        lock (JsonDb.SaveLock)
        {
            response.CurrentUserTitle = user.TitleId;
            IEnumerable<int> titles = user.ManagedNpc == null ? catalogue.Keys : user.TitleList.Append(1).Distinct().Where(catalogue.ContainsKey);
            foreach (int titleId in titles)
                response.UserTitleList.Add(new ResGetUserTitleList.Types.NetUserTitle { UserTitleId = titleId });
        }

        await WriteDataAsync(response);
    }
}
