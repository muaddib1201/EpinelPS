using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Arena;

internal static class ArenaTeams
{
    internal static int Type(bool special, bool offense) => special ? (offense ? 13 : 12) : (offense ? 4 : 3);
    internal static bool Unlocked(User user, bool special)
    {
        if (user.IsBanned) return false;
        var row = GameData.Instance.ContentsOpenTable.GetValueOrDefault((ContentsOpen)(special ? 12 : 11));
        return row == null || row.OpenCondition.All(c => (int)c.OpenConditionType switch
        {
            0 => true,
            3 => user.LastNormalStageCleared >= c.OpenConditionValue,
            1 => user.userPointData.UserLevel >= c.OpenConditionValue,
            _ => false
        });
    }

    internal static bool Ensure(User user, bool special, bool offense)
    {
        int type = Type(special, offense);
        if (user.UserTeams.TryGetValue(type, out var existing))
        {
            try { Validate(user, existing); return true; } catch (BadHttpRequestException) { return false; }
        }
        var pool = user.Characters.Where(c => GameData.Instance.CharacterTable.ContainsKey(c.Tid))
            .GroupBy(c => GameData.Instance.CharacterTable[c.Tid].NameCode)
            .Select(g => g.OrderByDescending(c => FormulaUtils.CalculateCP(user, c.Csn)).First())
            .OrderByDescending(c => FormulaUtils.CalculateCP(user, c.Csn)).ThenBy(c => c.Csn).ToList();
        if (pool.Count < (special ? 15 : 5)) return false;
        var data = new NetUserTeamData { Type = type, LastContentsTeamNumber = 1 };
        for (int round = 1; round <= (special ? 3 : 1); round++)
        {
            var team = new NetTeamData { TeamNumber = round };
            var chosen = new List<CharacterModel>();
            if (round == 1 && user.ManagedNpc != null && user.RepresentationTeamDataNew.Length == 5 &&
                user.RepresentationTeamDataNew.All(csn => pool.Any(c => c.Csn == csn)))
            {
                chosen.AddRange(user.RepresentationTeamDataNew.Select(csn => pool.Single(c => c.Csn == csn)));
                pool.RemoveAll(c => chosen.Contains(c));
            }
            foreach (var burst in new[] { BurstStep.Step1, BurstStep.Step2, BurstStep.Step3 })
            {
                if (chosen.Count == 5) break;
                var c = pool.FirstOrDefault(c => GameData.Instance.CharacterTable[c.Tid].UseBurstSkill == burst);
                if (c != null) { chosen.Add(c); pool.Remove(c); }
            }
            while (chosen.Count < 5) { chosen.Add(pool[0]); pool.RemoveAt(0); }
            for (int slot = 0; slot < 5; slot++) team.Slots.Add(new NetTeamSlot { Slot = slot + 1, Value = chosen[slot].Csn });
            data.Teams.Add(team);
        }
        user.UserTeams[type] = data;
        return true;
    }

    internal static void Validate(User user, NetUserTeamData data)
    {
        bool special = data.Type is 12 or 13;
        int max = data.Type == 4 ? 5 : special ? 3 : 1;
        if (data.Type is not (3 or 4 or 12 or 13) || data.Teams.Count < 1 || data.Teams.Count > max ||
            data.Teams.Any(t => t.TeamNumber < 1 || t.TeamNumber > max) || data.Teams.Select(t => t.TeamNumber).Distinct().Count() != data.Teams.Count ||
            (special && data.Teams.Count != 3)) throw new BadHttpRequestException("Invalid arena team numbers.", 400);
        var allNames = new HashSet<int>();
        foreach (var team in data.Teams)
        {
            if (team.Slots.Count != 5 || !team.Slots.Select(s => s.Slot).Order().SequenceEqual(Enumerable.Range(1, 5)))
                throw new BadHttpRequestException("Arena teams require five distinct slots.", 400);
            var names = new HashSet<int>();
            foreach (var slot in team.Slots)
            {
                var c = user.GetCharacterBySerialNumber(slot.Value);
                if (slot.ValueType != 0 || c == null || !GameData.Instance.CharacterTable.TryGetValue(c.Tid, out var row) ||
                    !names.Add(row.NameCode) || (special && !allNames.Add(row.NameCode)))
                    throw new BadHttpRequestException("Arena requires distinct owned characters without support units.", 400);
            }
        }
    }

    internal static ResSetTeam Save(User user, ReqSetTeam req)
    {
        if (JsonDb.GetUser(user.ID) != user) throw new BadHttpRequestException("Stale arena session.", 409);
        var previous = user.UserTeams.GetValueOrDefault(req.Type);
        var data = previous?.Clone() ?? new NetUserTeamData { Type = req.Type };
        if (req.Teams.Count == 0) throw new BadHttpRequestException("Arena teams cannot be empty.", 400);
        if (req.Teams.Select(t => t.TeamNumber).Distinct().Count() != req.Teams.Count) throw new BadHttpRequestException("Duplicate team number.", 400);
        foreach (var team in req.Teams)
        {
            var old = data.Teams.FirstOrDefault(t => t.TeamNumber == team.TeamNumber);
            if (old != null) data.Teams.Remove(old);
            data.Teams.Add(team.Clone());
        }
        data.LastContentsTeamNumber = req.ContentsId > 0 ? req.ContentsId : req.Teams.Last().TeamNumber;
        Validate(user, data);
        if (!data.Teams.Any(t => t.TeamNumber == data.LastContentsTeamNumber)) throw new BadHttpRequestException("Unknown active arena team.", 400);
        user.UserTeams[req.Type] = data;
        try { JsonDb.Save(); } catch { if (previous == null) user.UserTeams.Remove(req.Type); else user.UserTeams[req.Type] = previous; throw; }
        return new ResSetTeam { Type = req.Type, Teams = { req.Teams.Select(t => t.Clone()) } };
    }

    internal static NetWholeUserTeamData Whole(User user, NetTeamData team, int type)
    {
        var result = new NetWholeUserTeamData { TeamNumber = team.TeamNumber, Type = type };
        foreach (var slot in team.Slots)
        {
            var c = user.GetCharacterBySerialNumber(slot.Value)!;
            result.Slots.Add(new NetWholeTeamSlot { Slot = slot.Slot, Csn = c.Csn, Tid = c.Tid,
                Lv = user.GetCharacterLevel(c.Csn, c.Level), CostumeId = c.CostumeId });
            result.TeamCombat += FormulaUtils.CalculateCP(user, c.Csn);
        }
        return result;
    }

    internal static NetArenaEnemyData Enemy(User user, bool special)
    {
        var data = new NetArenaEnemyData();
        var teams = user.UserTeams[Type(special, false)].Teams.OrderBy(t => t.TeamNumber);
        var characters = teams.SelectMany(t => t.Slots).Select(s => s.Value).ToHashSet();
        foreach (var team in teams)
        foreach (var slot in team.Slots)
        {
            var c = user.GetCharacterBySerialNumber(slot.Value)!;
            data.Character.Add(new NetArenaUserCharacter { Round = team.TeamNumber, Slot = slot.Slot,
                Character = new NetUserCharacterData { Default = new NetUserCharacterDefaultData { Csn = c.Csn, Tid = c.Tid,
                    Lv = user.GetCharacterLevel(c.Csn, c.Level), Grade = c.Grade, CostumeId = c.CostumeId,
                    Skill1Lv = c.Skill1Lvl, Skill2Lv = c.Skill2Lvl, UltiSkillLv = c.UltimateLevel }, IsSynchro = user.GetSynchro(c.Csn) } });
        }
        data.Items.AddRange(user.Items.Where(i => characters.Contains(i.Csn) && !GameData.Instance.ItemHarmonyCubeTable.ContainsKey(i.ItemType)).Select(NetUtils.ToNet));
        data.Attractives.AddRange(user.BondInfo.Select(b => b.Clone()));
        data.HarmonyCubes.AddRange(ArenaHarmonyCubeService.Snapshot(user));
        data.Recycle.AddRange(user.ResearchProgress.Select(r => new NetUserRecycleRoomData { Tid = r.Key, Lv = r.Value.Level }));
        data.Awakenings.AddRange(user.EquipmentAwakenings.Select(a => new NetEquipmentAwakening { Isn = a.Isn, Option = a.Option }));
        data.FavoriteItems.AddRange(user.FavoriteItems.Select(f => f.Clone()));
        return data;
    }
}
