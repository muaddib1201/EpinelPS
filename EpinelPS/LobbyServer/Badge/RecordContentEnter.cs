using EpinelPS.LobbyServer.Arena;

namespace EpinelPS.LobbyServer.Badge;

[GameRequest("/badge/record/content")]
public sealed class RecordContentEnter : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqRecordContentEnter>();
        var user = GetUser();
        if (request.ContentType == NetEnteredContentType.Arena)
        {
            var clock = ctx!.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            ArenaService.RecordArenaOverview(user, clock.GetUtcNow().UtcDateTime);
        }
        await WriteDataAsync(new ResRecordContentEnter());
    }
}
