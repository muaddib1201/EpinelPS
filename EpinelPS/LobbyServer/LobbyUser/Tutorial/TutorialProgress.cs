using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.LobbyServer.LobbyUser.Tutorial;

internal static class TutorialProgress
{
    private const int SynchroGroup = 2500;

    internal static void Restore(User user, NetUserData snapshot)
    {
        lock (JsonDb.SaveLock)
        {
            if (CompletedSynchroStep(user) is { } step)
                Store(user, step);

            foreach (var (group, saved) in user.ClearedTutorialDataNew)
                snapshot.Tutorials.Add(new NetTutorialData { GroupId = group, LastClearedTid = saved.Id, LastClearedVersion = saved.VersionGroup });
        }
    }

    internal static void Record(User user, ContentsTutorialRecord step)
    {
        lock (JsonDb.SaveLock)
        {
            if (step.GroupId == SynchroGroup)
                step = CompletedSynchroStep(user) ?? step;
            Store(user, step);
        }
    }

    private static ContentsTutorialRecord? CompletedSynchroStep(User user)
    {
        // Five level-200 anchors open the upgrade view directly. That view never
        // fires EnterSynchroDeviceView, which the basic device tutorial waits for.
        if (!user.SynchroDeviceUpgraded && user.Characters.Count(c => c.Level >= 200) < 5)
            return null;

        var final = GameData.Instance?.TutorialTable.Values
            .Where(t => t.GroupId == SynchroGroup && t.SaveTutorial && t.NextId == 0)
            .MaxBy(t => t.VersionGroup);
        if (final == null || (user.LastNormalStageCleared < final.ClearedStageId && !user.ClearedTutorialDataNew.ContainsKey(SynchroGroup)))
            return null;
        return final;
    }

    private static void Store(User user, ContentsTutorialRecord step)
    {
        var checkpoints = user.ClearedTutorialDataNew;
        checkpoints.TryGetValue(step.GroupId, out var previous);
        if (previous?.VersionGroup == step.VersionGroup && previous.Id == step.Id)
            return;
        if (step.GroupId == SynchroGroup && previous != null)
        {
            if (previous.VersionGroup > step.VersionGroup)
                return;
            if (previous.VersionGroup == step.VersionGroup &&
                GameData.Instance.TutorialTable.TryGetValue(previous.Id, out var savedStep) &&
                savedStep.GroupId == SynchroGroup && savedStep.SaveTutorial && savedStep.NextId == 0)
                return;
        }

        checkpoints[step.GroupId] = new ClearedTutorialData { Id = step.Id, VersionGroup = step.VersionGroup };
        try { JsonDb.Save(); }
        catch
        {
            if (previous == null) checkpoints.Remove(step.GroupId);
            else checkpoints[step.GroupId] = previous;
            throw;
        }
    }
}
