using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer.Arena;
using EpinelPS.Utils;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.LobbyServer.Controllers;

[ApiController]
[Route("/v1/arena")]
public sealed class ArenaController(IUserService users, TimeProvider? clock = null) : ControllerBase
{
    [HttpPost("get")]
    public ActionResult<ResGetArena> Get([FromBodyProtobuf] ReqGetArena req) => Run(s => s.Get());
    [HttpPost("getenemylist")]
    public ActionResult<ResGetArenaEnemyList> Enemies([FromBodyProtobuf] ReqGetArenaEnemyList req) => Run(s => s.Enemies());
    [HttpPost("getenemydata")]
    public ActionResult<ResGetArenaEnemyData> Enemy([FromBodyProtobuf] ReqGetArenaEnemyData req) => Run(s => s.EnemyData(req.EnemyUsn));
    [HttpPost("toprank")]
    public ActionResult<ResGetArenaTopRank> Top([FromBodyProtobuf] ReqGetArenaTopRank req) => Run(s => s.Top());
    [HttpPost("start")]
    public ActionResult<ResArenaStart> Start([FromBodyProtobuf] ReqArenaStart req) => Run(s => s.Start(req));
    [HttpPost("result")]
    public ActionResult<ResArenaResult> Result([FromBodyProtobuf] ReqArenaResult req) => Run(s => s.Result(req));
    [HttpPost("/v1/wallet/buyticket")]
    public ActionResult<ResBuyTicket> BuyTicket([FromBodyProtobuf] ReqBuyTicket req) => Run(s => s.BuyTicket(req));
    [HttpPost("getrecordlist")]
    public ActionResult<ResGetArenaRecordList> Records([FromBodyProtobuf] ReqGetArenaRecordList req) => Run(s => s.Records());
    [HttpPost("special/get")]
    public ActionResult<ResGetSpecialArena> Special([FromBodyProtobuf] ReqGetSpecialArena req) => Run(s => s.GetSpecial());
    [HttpPost("special/getenemylist")]
    public ActionResult<ResGetSpecialArenaEnemyList> SpecialEnemies([FromBodyProtobuf] ReqGetSpecialArenaEnemyList req) => Run(s => s.SpecialEnemies());
    [HttpPost("special/getenemydata")]
    public ActionResult<ResGetSpecialArenaEnemyData> SpecialEnemy([FromBodyProtobuf] ReqGetSpecialArenaEnemyData req) => Run(s => s.SpecialEnemyData(req.EnemyUsn));
    [HttpPost("special/toprank")]
    public ActionResult<ResGetSpecialArenaTopRank> SpecialTop([FromBodyProtobuf] ReqGetSpecialArenaTopRank req) => Run(s => s.SpecialTop());
    [HttpPost("special/getprofile")]
    public ActionResult<ResGetSpecialArenaProfile> SpecialProfile([FromBodyProtobuf] ReqGetSpecialArenaProfile req) => Run(s => s.SpecialProfile(req.TargetUsn));
    [HttpPost("special/start")]
    public ActionResult<ResSpecialArenaStart> SpecialStart([FromBodyProtobuf] ReqSpecialArenaStart req) => Run(s => s.SpecialStart(req));
    [HttpPost("special/result")]
    public ActionResult<ResSpecialArenaResult> SpecialResult([FromBodyProtobuf] ReqSpecialArenaResult req) => Run(s => s.SpecialResult(req));
    [HttpPost("special/getrecordlist")]
    public ActionResult<ResGetSpecialArenaRecordList> SpecialRecords([FromBodyProtobuf] ReqGetSpecialArenaRecordList req) => Run(s => s.SpecialRecords());
    [HttpPost("special/showreward")]
    public ActionResult<ResShowSpecialArenaReward> ShowReward([FromBodyProtobuf] ReqShowSpecialArenaReward req) => Run(s => s.ShowReward());
    [HttpPost("special/obtainreward")]
    public ActionResult<ResObtainSpecialArenaReward> ObtainReward([FromBodyProtobuf] ReqObtainSpecialArenaReward req) => Run(s => s.ObtainReward(req));
    [HttpPost("special/getrewardhistory")]
    public ActionResult<ResGetSpecialArenaRewardHistory> RewardHistory([FromBodyProtobuf] ReqGetSpecialArenaRewardHistory req) => Run(s => s.RewardHistory(req.LastSeq));
    [HttpPost("special/getpointranklist")]
    public ActionResult<ResGetSpecialArenaPointRankList> PointRank([FromBodyProtobuf] ReqGetSpecialArenaPointRankList req) => Run(s => s.PointRank());
    [HttpPost("getbaninfo")]
    public ActionResult<ResGetArenaBanInfo> BanInfo([FromBodyProtobuf] ReqGetArenaBanInfo req) => Run(_ => new ResGetArenaBanInfo {
        ChampionArenaBanInfo = ChampionArenaAvailability.BanInfo() });
    [HttpPost("replay")]
    public ActionResult<ResGetArenaReplay> Replay([FromBodyProtobuf] ReqGetArenaReplay req) => Run(_ => new ResGetArenaReplay());
    [HttpPost("special/getreplaypopup")]
    public ActionResult<ResGetSpecialArenaReplayPopup> SpecialReplay([FromBodyProtobuf] ReqGetSpecialArenaReplayPopup req) => Run(_ => new ResGetSpecialArenaReplayPopup { ReplayState = NetSpecialArenaReplayState.None });
    [HttpPost("botstart")]
    public ActionResult<ResArenaBotStart> BotStart([FromBodyProtobuf] ReqArenaBotStart req) => Unsupported<ResArenaBotStart>();
    [HttpPost("botresult")]
    public ActionResult<ResArenaBotResult> BotResult([FromBodyProtobuf] ReqArenaBotResult req) => Unsupported<ResArenaBotResult>();
    [HttpPost("special/botstart")]
    public ActionResult<ResSpecialArenaBotStart> SpecialBotStart([FromBodyProtobuf] ReqSpecialArenaBotStart req) => Unsupported<ResSpecialArenaBotStart>();
    [HttpPost("special/botresult")]
    public ActionResult<ResSpecialArenaBotResult> SpecialBotResult([FromBodyProtobuf] ReqSpecialArenaBotResult req) => Unsupported<ResSpecialArenaBotResult>();
    [HttpPost("special/simulation/result")]
    public ActionResult<ResSpecialArenaResultV2> SimulationResult([FromBodyProtobuf] ReqSpecialArenaResultV2 req) => Unsupported<ResSpecialArenaResultV2>();
    [HttpPost("special/simulation/botresult")]
    public ActionResult<ResSpecialArenaBotResultV2> SimulationBotResult([FromBodyProtobuf] ReqSpecialArenaBotResultV2 req) => Unsupported<ResSpecialArenaBotResultV2>();

    private ActionResult<T> Unsupported<T>() => Run<T>(_ => throw new BadHttpRequestException("This server uses actual player opponents and client battle simulation.", 400));
    private ActionResult<T> Run<T>(Func<ArenaService, T> action)
    {
        var user = users.GetUser();
        if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(user.ID) != user) return Problem(statusCode: 409, type: NetUtils.InvalidSessionErrorType);
            var service = new ArenaService(user, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime, HttpContext.Items[nameof(PacketDecryptResponse.Nonce)] as byte[]);
            try
            {
                service.FlushTriggers();
                var response = service.Execute(() => action(service));
                service.FlushTriggers();
                return response;
            }
            catch (BadHttpRequestException e) { return Problem(statusCode: e.StatusCode, detail: e.Message); }
        }
    }
}
