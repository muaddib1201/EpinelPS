using EpinelPS.Data;

using EpinelPS.LobbyServer.Guild;

namespace EpinelPS.LobbyServer.LobbyUser;

[GameRequest("/User/GetProfile")]
public class GetUserProfile : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetProfileData req = await ReadData<ReqGetProfileData>();
        GetUser();
        User? user = GetUser((ulong)req.TargetUsn);
        ResGetProfileData response = new();

        if (user != null)
        {
            response.Data = new NetProfileData
            {
                User = LobbyHandler.CreateWholeUserDataFromDbUser(user),
                Guild = GuildDirectory.Membership(user),
                LastActionAt = user.ManagedNpc?.LastActionUtc.Ticks ?? DateTimeOffset.UtcNow.Ticks,
            };
            response.Data.CharacterCount.Add(new NetCharacterCount() { Count = user.Characters.Count });
            response.Data.CostumeCount = user.CostumeList.Count;
            response.Data.InfraCoreLv = user.InfraCoreLvl;
            response.Data.LastCampaignNormalStageId = user.LastNormalStageCleared;
            response.Data.LastCampaignHardStageId = user.LastHardStageCleared;
            response.Data.OutpostOpenState = user.MainQuestData.ContainsKey(25);
            response.Data.Desc = GameContext.Users.First(u => u.ID == user.ID).Description;
            response.Data.SynchroLv = user.GetSynchroLevel();
            response.Data.SynchroSlotCount = user.SynchroSlots.Count(x => x.CharacterSerialNumber != 0);
            for (int i = 0; i < user.RepresentationTeamDataNew.Length; i++)
            {
                long csn = user.RepresentationTeamDataNew[i];
                CharacterModel? c = user.GetCharacterBySerialNumber(csn);

                if (c != null)
                {
                    response.Data.ProfileTeam.Add(new NetProfileTeamSlot() { Slot = i + 1, Default = new() { CostumeId = c.CostumeId, Csn = c.Csn, Grade = c.Grade, Lv = user.ManagedNpc == null ? c.Level : user.GetCharacterLevel(c.Csn, c.Level), Skill1Lv = c.Skill1Lvl, Skill2Lv = c.Skill2Lvl, Tid = c.Tid, UltiSkillLv = c.UltimateLevel } });
                }
            }
            foreach (var research in user.ResearchProgress)
            {
                response.Data.Recycle.Add(new NetUserRecycleRoomData
                {
                    Tid = research.Key,
                    Lv = research.Value.Level
                });
            }

            if (user.CompletedTacticAcademyLessons.Count > 0)
                response.Data.LastTacticAcademyClass = GameData.Instance.GetTacticAcademyLesson(user.CompletedTacticAcademyLessons.Last()).GroupId;
            if (user.CompletedTacticAcademyLessons.Count > 0)
                response.Data.LastTacticAcademyLesson = user.CompletedTacticAcademyLessons.Last();

            //if(user.Memorial.Count > 0)
            //    response.Data.MemorialCount.Add(new NetMemorialCount { Count = user.Memorial.Count });
            response.Data.JukeboxCount = user.JukeboxBgm?.Count ?? 0;
        }

        await WriteDataAsync(response);
    }
}
