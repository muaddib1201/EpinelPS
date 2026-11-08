using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace EpinelPS.LobbyServer.TriggerController;

[GameRequest("/trigger/sync")]
public class TriggerSync : LobbyMessage
{
    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> SyncGates = new();

    protected override async Task HandleAsync()
    {
        ReqSyncTrigger req = await ReadData<ReqSyncTrigger>();
        User user = GetUser();
        MissionProgressDelivery.Flush(user);

        // This request is responsible for fetching a log for
        // daily, weekly, challenge mission completion.
        // This endpoint also returns the entire "history" for the account when 
        // Seq = 0, which the client does when it is started for the first time, or when 
        // the "Clear Cache" option is invoked. 
        // When Seq = 0, the server limits the responses to 2000 items,
        // and HasRemainData is set to true.
        // TODO: Is it necessary to store the entire account history each time a stage
        // is cleared, why does the official server do this?

        ResSyncTrigger response = new();
        Logging.WriteLine($"[TriggerSync] User {user.ID} requested trigger sync from seq {req.Seq}", LogType.Debug);

        var gate = SyncGates.GetOrAdd(user.ID, _ => new SemaphoreSlim(1));
        await gate.WaitAsync();
        try
        {
            string? packetKey = ctx?.Items["Nonce"] is byte[] { Length: > 0 } nonce
                ? Convert.ToHexString(SHA256.HashData([.. nonce, .. req.ToByteArray()])) : null;
            bool refreshClock = false;
            lock (user)
            {
                AliceDiaryProgress.Reconcile(user);
                if (packetKey != null && user.TriggerSyncClockReceipt is { } receipt && receipt.PacketKey == packetKey)
                {
                    response = ResSyncTrigger.Parser.ParseFrom(receipt.Response);
                }
                else
                {
                    long maxId = GameContext.Triggers
                        .Where(x => x.UserId == user.ID)
                        .Select(x => (long?)x.Id)
                        .Max() ?? 0;

                    long effectiveSeq = req.Seq;
                    refreshClock = user.TriggerSyncClockVersion < 1;
                    if (refreshClock && effectiveSeq > 0)
                    {
                        // Cached history contains UTC+9 timestamps. Replay it through the corrected
                        // wire conversion once; subsequent pages must continue from their last ID.
                        response.Restart = true;
                        effectiveSeq = 0;
                        Logging.WriteLine($"[TriggerSync] Refreshing UTC mission history for user {user.ID}", LogType.Info);
                    }
                    if (effectiveSeq > maxId)
                    {
                        // The client's cached sequence is ahead of the database (e.g. after trigger cleanup or save restore).
                        // Request the client to restart trigger sync from 0.
                        Logging.WriteLine($"[TriggerSync] Client seq ({req.Seq}) > maxId ({maxId}) for user {user.ID}; requesting restart from 0", LogType.Info);
                        response.Restart = true;
                        effectiveSeq = 0;
                    }

                    // Look for triggers past that amount in ascending ID order
                    TriggerModelNew[] newTriggers = [.. GameContext.Triggers
                        .Where(x => x.Id > effectiveSeq && x.UserId == user.ID)
                        .OrderBy(x => x.Id)];

                    // Return triggers (paged up to 2000)
                    int triggerCount = 0;
                    foreach (TriggerModelNew item in newTriggers)
                    {
                        triggerCount++;

                        response.Triggers.Add(item.ToNet());

                        if (triggerCount >= 2000)
                        {
                            response.HasRemainData = true;
                            break;
                        }
                    }
                }
            }
            await WriteDataAsync(response);
            if (refreshClock)
            {
                lock (user)
                {
                    int previous = user.TriggerSyncClockVersion;
                    var previousReceipt = user.TriggerSyncClockReceipt;
                    user.TriggerSyncClockVersion = 1;
                    user.TriggerSyncClockReceipt = packetKey == null ? null : new TriggerClockSyncReceipt
                    {
                        PacketKey = packetKey, Response = response.ToByteArray()
                    };
                    try { JsonDb.Save(); }
                    catch { user.TriggerSyncClockVersion = previous; user.TriggerSyncClockReceipt = previousReceipt; throw; }
                }
            }
        }
        finally { gate.Release(); }
    }
}

