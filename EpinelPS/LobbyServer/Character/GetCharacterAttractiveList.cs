namespace EpinelPS.LobbyServer.Character;

[GameRequest("/character/attractive/get")]
public class GetCharacterAttractiveList : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetAttractiveList req = await ReadData<ReqGetAttractiveList>();
        User user = GetUser();

        ResGetAttractiveList response = new();

        lock (user)
        {
            response.CounselAvailableCount = user.ResetableData.GetAvailableCounselCount();
            foreach (NetUserAttractiveData item in user.BondInfo)
            {
                item.CanCounselToday = true;
                response.Attractives.Add(item.Clone());
            }
        }

        // TODO: Validate response from real server and pull info from user info
        await WriteDataAsync(response);
    }
}
