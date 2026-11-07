using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.Services;

internal static class AccountRegistrationService
{
    internal static SdkUser Register(GameContext db, string email, string passwordHash)
    {
        lock (JsonDb.SaveLock)
        {
            if (db.SdkUsers.Any(u => u.Email == email)) throw new ArgumentException("Account already exists.");
            ulong uid;
            do { uid = (ulong)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue); }
            while (JsonDb.Instance.Users.Any(u => u.ID == uid) || JsonDb.Instance.NpcManagement?.PendingUsers.Any(u => u.ID == uid) == true ||
                db.Users.Any(u => u.ID == uid) || db.SdkUsers.Any(u => u.ID == uid));
            var account = new SdkUser { ID = uid, Email = email, PasswordHash = passwordHash,
                RegisterTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), IsAdmin = !db.SdkUsers.Any(), PlayerName = "Player_" + Rng.RandomString(8) };
            var previous = JsonDb.Instance.Users;
            using var transaction = db.Database.BeginTransaction();
            try
            {
                db.SdkUsers.Add(account); db.Users.Add(new GameUser { ID = uid }); db.SaveChanges();
                JsonDb.Instance.Users = [.. previous, new User { ID = uid }];
                JsonDb.Save(); transaction.Commit();
                return account;
            }
            catch
            {
                JsonDb.Instance.Users = previous; db.ChangeTracker.Clear();
                JsonDb.Save(); throw;
            }
        }
    }
}
