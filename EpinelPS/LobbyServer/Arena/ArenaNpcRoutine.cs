using EpinelPS.Data;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Arena;

internal sealed partial class ArenaService
{
    // Autonomous actions reuse normal begin/result settlement without a synthetic HTTP request nonce.
    internal int RunNpcRoutine()
    {
        if (user.ManagedNpc == null || !RewardMutationScope.DefersSave) throw new InvalidOperationException("NPC phase transaction required.");
        if (!ArenaTeams.Unlocked(user, false)) return 0;
        return Execute(() =>
        {
            int played = 0;
            foreach (bool special in new[] { false, true })
            {
                if (!ArenaTeams.Unlocked(user, special) || user.Arena == null ||
                    (special ? Own.SpecialJoinedUtc == null : Own.RookieJoinedUtc == null) || Own.Pending != null) continue;
                int limit = special ? SpecialConfig?.FreePlayCount ?? 2 : 5;
                while ((special ? Own.SpecialCount : Own.RookieCount) < limit)
                {
                    var enemy = Participants(special).Where(u => u.ID != user.ID && u.ManagedNpc is { Enabled: true })
                        .OrderBy(u => Math.Abs((long)Combat(u, special) - Combat(user, special))).ThenBy(u => u.ID).FirstOrDefault();
                    if (enemy == null) break;
                    var battle = Begin((long)enemy.ID, special);
                    bool won = Combat(user, special) >= Combat(enemy, special);
                    if (special)
                        FinishSpecial(new ReqSpecialArenaResult { EnemyUsn = (long)enemy.ID,
                            Result = { new NetSpecialArenaRoundResult { Round = 1, IsWin = won }, new NetSpecialArenaRoundResult { Round = 2, IsWin = won } } });
                    else FinishRookie(new ReqArenaResult { EnemyUsn = (long)enemy.ID, TeamNumber = battle.AttackTeams[0], IsWin = won });
                    played++;
                }
                if (special) ObtainReward(new ReqObtainSpecialArenaReward());
            }
            return played;
        });
    }
}
