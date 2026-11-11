using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Liberate;

[GameRequest("/liberate/get")]
public class GetLiberateData : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetLiberateData req = await ReadData<ReqGetLiberateData>();
        User user = GetUser();
        ResGetLiberateData response = new();

        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (user.LiberateDatas.TryGetValue(user.CurCharacterIdId, out var liberateData))
            {
                var repaired = LiberateHelper.ReconcileMissions(liberateData);
                if (!ReferenceEquals(repaired, liberateData))
                {
                    user.LiberateDatas[user.CurCharacterIdId] = repaired;
                    try { JsonDb.Save(); }
                    catch
                    {
                        user.LiberateDatas[user.CurCharacterIdId] = liberateData;
                        throw;
                    }
                }
                response.LiberateData = repaired.Clone();
                response.OpenLiberateTypeIdList.AddRange(user.OpenLiberateTypeIdList);
            }
        }
        await WriteDataAsync(response);
    }
}
