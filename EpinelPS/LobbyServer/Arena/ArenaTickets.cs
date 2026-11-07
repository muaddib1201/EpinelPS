using EpinelPS.Data;

namespace EpinelPS.LobbyServer.Arena;

internal sealed partial class ArenaService
{
    internal ResBuyTicket BuyTicket(ReqBuyTicket req)
    {
        string key = Key("ticket/buy", req)
            ?? throw new BadHttpRequestException("Arena ticket purchases require a transport nonce to identify retries.", 400);
        if (Replay<ResBuyTicket>(key) is { } receipt) return receipt;
        if (req.CurrencyType is not (6001 or 6002) || req.Count <= 0)
            throw new BadHttpRequestException("Invalid arena ticket type or quantity.", 400);

        bool special = req.CurrencyType == 6002;
        Require(special);
        var ticket = (CurrencyType)req.CurrencyType;
        var payment = (CurrencyType)(special ? SpecialConfig?.TicketBuyCurrencyType ?? 99 : Config("ArenaTicketBuyCurrencyType", 99));
        int price = special ? SpecialConfig?.TicketBuyCurrencyValue ?? 100 : Config("ArenaTicketBuyCurrencyValue", 100);
        if (price <= 0 || payment == ticket)
            throw new BadHttpRequestException("Arena ticket purchase configuration is unavailable.", 400);

        long cost = (long)price * req.Count;
        long balance = user.Currency.GetValueOrDefault(payment);
        long first = Math.Min(Math.Max(0, balance), cost);
        long remaining = cost - first;
        if (remaining > 0 && (payment != CurrencyType.FreeCash || user.Currency.GetValueOrDefault(CurrencyType.ChargeCash) < remaining))
            throw new BadHttpRequestException("Insufficient gems to purchase arena tickets.", 400);
        long tickets = user.Currency.GetValueOrDefault(ticket);
        if (tickets > long.MaxValue - req.Count)
            throw new BadHttpRequestException("Arena ticket balance is too large.", 400);

        user.Currency[payment] = balance - first;
        if (remaining > 0) user.Currency[CurrencyType.ChargeCash] -= remaining;
        user.Currency[ticket] = tickets + req.Count;
        var types = payment == CurrencyType.FreeCash ? new[] { payment, CurrencyType.ChargeCash, ticket } : new[] { payment, ticket };
        return Remember(key, new ResBuyTicket { Currencies = { types.Distinct().Select(type =>
            new NetUserCurrencyData { Type = (int)type, Value = user.Currency.GetValueOrDefault(type) }) } });
    }
}
