using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Team;

namespace EpinelPS.LobbyServer.Stage;

[GameRequest("/stage/fastclearstage")]
public class FastClear : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqFastClearCampaignStage req = await ReadData<ReqFastClearCampaignStage>();

        User user = GetUser();
        bool firstClear = !user.IsStageCompleted(req.CampaignStageId);
        int teamNumber = req.TeamNumber > 0 ? req.TeamNumber
            : user.UserTeams.GetValueOrDefault((int)TeamType.Default)?.LastContentsTeamNumber ?? 1;
        if (firstClear)
            SupportCharacterHelper.BeginBattle(user, TeamType.Default, req.CampaignStageId, teamNumber);

        Console.WriteLine($"Stage " + req.CampaignStageId + " completed using quick battle");

        ResClearStage rsp = ClearStage.CompleteStage(user, req.CampaignStageId, save: false);
        if (firstClear)
        {
            SupportCharacterHelper.FinishBattle(user, TeamType.Default, req.CampaignStageId, teamNumber, 1);
        }
        JsonDb.Save();

        ResFastClearCampaignStage response = new()
        {
            OutpostBattle = rsp.OutpostBattle,
            OutpostBattleLevelReward = rsp.OutpostBattleLevelReward,
            StageClearReward = rsp.StageClearReward,
            UserLevelUpReward = rsp.UserLevelUpReward,
            OutpostTimeRewardBuff = new()
        };

        if (rsp.OutpostTimeRewardBuff != null)
        {
            response.OutpostTimeRewardBuff.TimeRewardBuffs.AddRange(rsp.OutpostTimeRewardBuff.TimeRewardBuffs);
        }

        await WriteDataAsync(response);
    }
}
