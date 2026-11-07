namespace EpinelPS.LobbyServer.Friend;

// These integer fields are untyped in the shipped protobuf schema. Keep client
// compatibility values together; their visual rendering needs game acceptance.
internal static class FriendshipWire
{
    internal const int Accepted = 1;
    internal const int Normal = 0;
    internal const int NoGift = 0;
    internal const int Available = 1;
    internal const int Claimed = 2;
}
