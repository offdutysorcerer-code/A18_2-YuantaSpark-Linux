namespace A18.Realtime.Core;

public interface IMarketDataProvider
{
    string Name { get; }
    bool IsReady { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task MaintainSessionAsync(CancellationToken cancellationToken);
    Task PrepareRequiredSubscriptionsAsync(CancellationToken cancellationToken);
    Task SubscribeAsync(string market, string symbol, CancellationToken cancellationToken);
    IReadOnlyCollection<SubscriptionStatus> GetSubscriptions();
    MarketDataSessionStatus GetSessionStatus();
    NormalizedTick? GetLatest(string symbol);
    IReadOnlyList<NormalizedTick> GetTicks(string symbol, DateOnly tradeDate);
    MarketDataReadinessStatus GetReadiness(IReadOnlyCollection<string>? symbols = null);
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
    string AckState,
    string BackfillState,
    string BootId,
    long SessionGeneration,
    DateOnly TradeDate,
    DateTimeOffset RequestedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? AckConfirmedAt,
    DateTimeOffset? FirstReceivedAt,
    DateTimeOffset? LastReceivedAt,
    long CallbackCount,
    long BackfillTickCount,
    string? LastError,
    string? BackfillError);

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


public sealed record MarketDataReadinessStatus(
    string Provider,
    string BootId,
    long SessionGeneration,
    DateOnly CurrentTradeDate,
    bool SessionCurrent,
    bool PreopenPrepared,
    bool ChannelHasFreshLive,
    bool ReadyForRequestedSymbols,
    int RequestedCount,
    int SubmittedCount,
    int LiveObservedCount,
    int FreshCount,
    string[] MissingSubmitted,
    string[] MissingLive,
    string[] StaleLive,
    DateTimeOffset At);
