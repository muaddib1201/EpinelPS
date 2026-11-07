using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Inventory;

[GameRequest("/inventory/usepiece")]
public class UsePiece : LobbyMessage
{
    private static readonly Random random = new();

    protected override async Task HandleAsync()
    {
        // TODO: If this process takes too long, consIder to avoId using function chain.
        /*
         * Req Contains:
         * Isn: long value, the item serial number of the piece
         * Count: int value, how many time
         */
        ReqUsePiece req = await ReadData<ReqUsePiece>();
        User user = GetUser();
        ResUsePiece response = new();

        DbItemData piece = user.Items.FirstOrDefault(x => x.Isn == req.Isn) ?? throw new InvalidDataException("cannot find piece with isn " + req.Isn);
        if (req.Count <= 0 || (long)req.Count * 50 > piece.Count) throw new Exception("count mismatch");

        piece.Count -= req.Count * 50;
        if (piece.Count == 0) user.Items.Remove(piece);

        ItemPieceRecord? pItem = GameData.Instance.PieceItems
            .FirstOrDefault(x => x.Value.Id == piece.ItemType).Value
            ?? throw new Exception("cannot find piece Id " + piece.ItemType);

        // Load the character probability list for the mold
        IEnumerable<GachaListProbRecord> probList = GameData.Instance.GachaGradeProb
            .Where(gradeProb => gradeProb.Key == pItem.UseId)
            .SelectMany(grade => GameData.Instance.GachaListProb.Where(list => list.Value.GroupId == grade.Value.GachaListId))
            .Select(i => i.Value);

        NetRewardData reward = new();
        IEnumerable<CharacterRecord> selectedCharacters = Enumerable.Range(1, req.Count)
            .Select(_ => SelectRandomCharacter(probList));

        foreach (CharacterRecord character in selectedCharacters)
        {
            reward.MergeFrom(CharacterRewardHelper.GrantCharacter(user, character, 1));

            // Character acquisition can satisfy a room condition independently
            // of the current ObtainCharacter trigger. Reconcile only fully
            // eligible openers after the new character and its triggers exist.
            MessengerMessageCreator.CreateAllEligibleOpeners(user);

            user.AddTrigger(Trigger.GachaCharacter, 0, 0);
        }

        reward.UserItems.Add(NetUtils.UserItemDataToNet(piece));

        response.Reward = reward;

        JsonDb.Save();

        await WriteDataAsync(response);
    }

    private CharacterRecord SelectRandomCharacter(IEnumerable<GachaListProbRecord> charProbs)
    {
        // Changed this to use the Gacha list probability table instead since it contains the probabilities without the need for splitting the groups.
        // This should work no matter how many character are added and no matter what their probabilities are.

        // Create the probability table
        int maxCharProb = 0;

        Dictionary<int, (int minProbInc, int maxProbEx)> charProbsTable = new Dictionary<int, (int minProbInc, int maxProbEx)>();

        foreach(GachaListProbRecord charProb in charProbs){
            charProbsTable.Add(charProb.Id, new (maxCharProb, maxCharProb + charProb.Prob));
            maxCharProb += charProb.Prob;
        }

        // Now, do the pull
        int charRoll = (int)random.NextInt64(maxCharProb);

        GachaListProbRecord selectedCharacter = charProbs.Where(charP => charP.Id == charProbsTable.Where( p => charRoll >= p.Value.minProbInc && charRoll < p.Value.maxProbEx).Select( p=> p.Key).First()).First();

        return GameData.Instance.CharacterTable[selectedCharacter.GachaId];
    }


}

internal record PieceGradeProb(double RProb, double SRProb, double SSRProb);
