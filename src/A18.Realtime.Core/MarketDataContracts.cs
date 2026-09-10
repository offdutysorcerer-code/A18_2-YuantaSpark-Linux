namespace A18.Realtime.Core;

public interface IMarketDataProvider
{
    string Name { get; }
    bool IsReady { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task MaintainSessionAsync(CancellationToken cancellationToken);
    Task SubscribeAsync(string market, string symbol, CancellationToken cancellationToken);
    IReadOnlyCollection<SubscriptionStatus> GetSubscriptions();
    MarketDataSessionStatus GetSessionStatus();
    NormalizedTick? GetLatest(string symbol);
}

public sealed record MarketDataSessionStatus(
    string Provider,
    string BootId,
    long SessionGeneration,
    DateOnly? SessionTradeDate,
    DateTimeOffset? ConnectedAt,
    DateOnly CurrentTaipeiDate,
    bool Connected,
    bool SessionCurrent,
    bool Ready,
    int DesiredSubscriptionCount,
    int CurrentSubscriptionCount);

public sealed record SubscriptionStatus(
    string Symbol,
    string Market,
    string State,
    string BootId,
    long SessionGeneration,
    DateOnly TradeDate,
    DateTimeOffset RequestedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? FirstReceivedAt,
    DateTimeOffset? LastReceivedAt,
    long CallbackCount,
    string? LastError);

public sealed record NormalizedTick(
    string Symbol,
    string Market,
    decimal Price,
    long Quantity,
    decimal Bid,
    decimal Ask,
    long Sequence,
    DateTimeOffset ExchangeAt,
    DateTimeOffset ReceivedAt,
    string Source);
