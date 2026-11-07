namespace EpinelPS.LobbyServer.Team;

[GameRequest("/team/support-character/list-used-count")]
public class ListSupportCharacterCount : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqListSupportCharacterUsedCount req = await ReadData<ReqListSupportCharacterUsedCount>();

        ResListSupportCharacterUsedCount response = new();

        response.SupportCharacterUsedCountList.AddRange(
            SupportCharacterHelper.GetUsedCounts(GetUser(), req.TeamTypeList));
        await WriteDataAsync(response);
    }
}
