using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Soloraid;

[GameRequest("/soloraid/practice/setdamage")]
public class SetDamagePractice : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqSetSoloRaidPracticeDamage>();
        var user = GetUser();
        ResSetSoloRaidPracticeDamage response = new();

        try
        {
            SoloRaidHelper.SetDamagePractice(user, ref response, req);
        }
        catch (SoloRaidPeriodException) { response.PeriodResult = SoloRaidPeriodResult.Failure; }
        catch (Exception ex)
        {
            Logging.WriteLine($"SetDamagePractice Error: {ex.Message}", LogType.Error);
        }

        if (response.PeriodResult == SoloRaidPeriodResult.Success) JsonDb.Save();
        await WriteDataAsync(response);
    }
}
