using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Soloraid;

[GameRequest("/soloraid/practice/open")]
public class OpenPractice : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        // ReqOpenSoloRaidPractice Fields:
        //  int RaidLevel
        //  SoloRaidDifficultyType DifficultyType
        var req = await ReadData<ReqOpenSoloRaidPractice>();
        User user = GetUser();
        ResOpenSoloRaidPractice response = new()
        {
            PeriodResult = SoloRaidPeriodResult.Success,
        };

        try
        {
            SoloRaidHelper.OpenSoloRaid(user, 0, req.RaidLevel, type: SoloRaidType.Practice);
        }
        catch (SoloRaidPeriodException) { response.PeriodResult = SoloRaidPeriodResult.Failure; }
        catch (Exception ex)
        {
            Logging.WriteLine($"OpenSoloRaid error: {ex.Message}", LogType.Error);
        }

        if (response.PeriodResult == SoloRaidPeriodResult.Success) JsonDb.Save();
        await WriteDataAsync(response);
    }
}
