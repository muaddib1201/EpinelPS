using EpinelPS.Data;

namespace EpinelPS.LobbyServer.LobbyUser.Tutorial;

[GameRequest("/tutorial/set")]
public class SetTutorial : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqSetTutorial req = await ReadData<ReqSetTutorial>();
        User user = GetUser();

        var cleared = GameData.Instance.GetTutorialDataById(req.LastClearedTid);
        TutorialProgress.Record(user, cleared);

        ResSetTutorial response = new();
        await WriteDataAsync(response);
    }
}
