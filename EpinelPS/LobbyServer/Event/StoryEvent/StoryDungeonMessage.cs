using EpinelPS.LobbyServer.Mission;
using Google.Protobuf;

namespace EpinelPS.LobbyServer.Event.StoryEvent;

public abstract class StoryDungeonMessage : LobbyMessage
{
    private protected string? PacketKey(string operation) => ctx?.Items["Nonce"] is byte[] { Length: > 0 } nonce
        ? $"{operation}/{Convert.ToHexString(nonce)}" : null;

    private protected string? BattleKey(string operation, int stage, int team, long? clientTime) =>
        clientTime > 0 ? $"{operation}/{stage}/{team}/{clientTime}" : PacketKey(operation);

    private protected async Task Reply<T>(int eventId, Func<EventStoryHelper, T> action) where T : IMessage, new()
    {
        var user = GetUser(UserId);
        if (user == null)
        {
            ctx!.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        T response;
        try
        {
            response = MissionProgressDelivery.Apply(user, () => action(new EventStoryHelper(user, eventId)));
        }
        catch (BadHttpRequestException error)
        {
            ctx!.Response.StatusCode = error.StatusCode;
            return;
        }
        await WriteDataAsync(response);
    }
}
