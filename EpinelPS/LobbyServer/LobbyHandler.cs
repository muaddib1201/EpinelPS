using ASodium;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.LobbyServer.LobbyUser.Tutorial;
using EpinelPS.Utils;
using Google.Protobuf;
using Paseto;
using Paseto.Builder;
using System.Text.Json;

namespace EpinelPS.LobbyServer;

public static class LobbyHandler
{
    public static readonly Dictionary<string, LobbyMessage> Handlers = [];
    static LobbyHandler()
    {
        foreach (System.Type type in typeof(LobbyMessage).Assembly.GetTypes())
        {
            if (type.GetCustomAttributes(typeof(GameRequestAttribute), true).Length > 0)
            {
                GameRequestAttribute? attrib = (GameRequestAttribute?)Attribute.GetCustomAttribute(type, typeof(GameRequestAttribute));
                if (attrib == null)
                {
                    Logging.WriteLine("WARNING: Failed to get attribute for " + type.FullName, LogType.Warning);
                    continue;
                }


                object? instance = Activator.CreateInstance(type);
                if (instance is LobbyMessage handler)
                {
                    Handlers.Add(attrib.Url, handler);
                }
                else
                {
                    Logging.WriteLine($"WARNING: Type {type.FullName} has PacketPathAttribute but does not implement LobbyMsgHandler", LogType.Warning);
                }
            }
        }
    }
    public static async Task DispatchSingle(HttpContext ctx)
    {
        string fullPath = ctx.Request.Path.Value ?? throw new Exception();
        string path = fullPath.Replace("/v1", "");


        // handle authentication
        if (ctx.Request.Headers.ContainsKey("Authorization"))
        {
            try
            {
                PasetoTokenValidationResult encryptionToken = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
                           .WithKey(JsonDb.Instance.LauncherTokenKey, Encryption.SymmetricKey)
                           .Decode(ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", ""), new PasetoTokenValidationParameters() { ValidateLifetime = true });

                if (encryptionToken.IsValid)
                {
                    var id = ((System.Text.Json.JsonElement)encryptionToken.Paseto.Payload["userId"]).GetUInt64();

                    if (id == 0) throw new Exception("403");

                    ctx.Items["UserID"] = id;
                }
            }
            catch
            {

            }
        }

        var progressionUser = ctx.Items["UserID"] is ulong actor ? JsonDb.GetUser(actor) : null;
        int? progressionStamp = progressionUser == null ? null : Ranking.RankingService.ProgressStamp(progressionUser);

        if (!Handlers.TryGetValue(path, out var registered))
        {
            Logging.WriteLine($"[LobbyHandler] No handler for: {path}", LogType.Error);
            //ctx.Response.StatusCode = 404;

            // to prevent "reloading" of the game for now, return empty response
            // this may cause more problems later on

            await new EmptyHandler().HandleAsync(ctx);
        }
        else
        {
            // LobbyMessage stores the current HTTP context and user ID on the instance.
            var handler = (LobbyMessage)Activator.CreateInstance(registered.GetType())!;
            await handler.HandleAsync(ctx);
            // Observe after the action releases its player/save locks. This also
            // covers character grants from mail, shops and events, not just gacha.
            var current = progressionUser == null ? null : JsonDb.GetUser(progressionUser.ID);
            if (current != null && progressionStamp != Ranking.RankingService.ProgressStamp(current))
                Ranking.RankingService.ObserveSafely(current);
        }
    }

    /// <summary>
    /// Private key, Token
    /// </summary>
    /// <param name="publicKey"></param>
    /// <returns></returns>
    public static GameClientInfo GenGameClientTok(ByteString publicKey, ulong userId)
    {
        string token = Rng.RandomString(381);

        GameClientInfo info = new() { ClientPublicKey = [.. publicKey] };

        SodiumKeyExchangeSharedSecretBox box = SodiumKeyExchange.CalculateServerSharedSecret(JsonDb.ServerPublicKey, JsonDb.ServerPrivateKey, [.. publicKey]);

        info.Keys = box;
        info.ClientAuthToken = token;

        if (userId == 0)
            throw new Exception("expected user account");

        info.UserId = userId;

        return info;
    }

    public static GameClientInfo? GetInfo(string token)
    {
        PasetoTokenValidationResult encryptionToken = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
                         .WithKey(JsonDb.Instance.LauncherTokenKey, Encryption.SymmetricKey)
                         .Decode(token, new PasetoTokenValidationParameters() { ValidateLifetime = true }) ?? throw new Exception("failed to decrypt");
        JsonElement elem = (encryptionToken.Paseto.Payload["data"] as System.Text.Json.JsonElement?) ?? throw new Exception("expected data field in auth token");

        string p = elem.GetString() ?? throw new Exception("auth token cannot be null");

        return JsonSerializer.Deserialize<GameClientInfo>(p ?? throw new Exception("data cannot be null"));
    }

    public static void Init()
    {
        // By calling this function, we force .NET to initialize handler dictanary to catch errors early on.
    }

    public static NetUserData CreateNetUserDataFromUser(User user, DateTime? nowUtc = null)
    {
        NetUserData ret = new()
        {
            Lv = user.userPointData.UserLevel,
            Exp = user.userPointData.ExperiencePoint,
            CostumeLv = 1,
            Frame = user.ProfileFrame,
            GuildLeaveAt = user.Guild.LeaveAt ?? 0,
            Icon = user.ProfileIconId,
            IconPrism = user.ProfileIconIsPrism,
            InfraCoreExp = user.InfraCoreExp,
            InfraCoreLv = user.InfraCoreLvl,
            Desc = user.Description
        };


        TutorialProgress.Restore(user, ret);

        lock (user)
        {
            ret.CounselCount = user.ResetableData.GetAvailableCounselCount();
            // User/Get runs after arena settlement and must preserve the consumed attempts.
            int day = int.Parse((nowUtc ?? DateTime.UtcNow).AddHours(-JsonDb.Instance.ResetHourUtcTime)
                .ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
            // ArenaService also preserves a later saved day when the system clock moves back.
            if (user.Arena is { } arena && arena.Day >= day)
            {
                ret.ArenaCount = arena.RookieCount;
                ret.SpecialArenaCount = arena.SpecialCount;
            }
        }
        ret.OutpostFastBattleCount = user.ResetableData.WipeoutCount;        
        return ret;
    }
    public static NetWholeUserData CreateWholeUserDataFromDbUser(User user)
    {
        using var ctx1 = GameContext.CreateNew();
        var userDB = ctx1.Users.Find((ulong)user.ID);
        NetWholeUserData ret = new()
        {
            Lv = user.userPointData.UserLevel,
            Frame = user.ProfileFrame,
            Icon = user.ProfileIconId,
            IconPrism = user.ProfileIconIsPrism,
            UserTitleId = user.TitleId,
            Nickname = userDB.Nickname,
            GuildName = GuildDirectory.Membership(user)?.Name ?? "",
            Usn = (long)user.ID,
            LastActionAt = user.ManagedNpc?.LastActionUtc.Ticks ?? DateTimeOffset.UtcNow.Ticks,
            Server = 1001
        };

        return ret;
    }

    public static NetWholeUserData CreateWholeUserDataFromDbUser(ulong id)
    {
        using var ctx2 = GameContext.CreateNew();
        var userDB = ctx2.Users.Find((ulong)id);
        var user = JsonDb.Instance.Users.Where(x=>x.ID == id).FirstOrDefault();
        NetWholeUserData ret = new()
        {
            Lv = user.userPointData.UserLevel,
            Frame = user.ProfileFrame,
            Icon = user.ProfileIconId,
            IconPrism = user.ProfileIconIsPrism,
            UserTitleId = user.TitleId,
            Nickname = userDB.Nickname,
            GuildName = GuildDirectory.Membership(user)?.Name ?? "",
            Usn = (long)user.ID,
            LastActionAt = user.ManagedNpc?.LastActionUtc.Ticks ?? DateTimeOffset.UtcNow.Ticks,
            Server = 1001
        };

        return ret;
    }
}

public class GameClientInfo
{
    /// <summary>
    /// Client public key generated by game client
    /// </summary>
    public byte[] ClientPublicKey { get; set; } = [];
    /// <summary>
    /// Authentication token
    /// </summary>
    public string ClientAuthToken { get; set; } = "";
    /// <summary>
    /// Rx/Tx key pair
    /// </summary>
    public SodiumKeyExchangeSharedSecretBox Keys { get; set; } = new();
    /// <summary>
    /// User ID of the user
    /// </summary>
    public ulong UserId { get; set; }
}
