using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using A18.Realtime.Core;
using Microsoft.Extensions.Options;
using YuantaOneAPI;

namespace A18.YuantaSpark;

public sealed record DailyKlineCandle(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

public sealed class YuantaSparkMarketDataProvider : IMarketDataProvider, IDisposable
{
    private static readonly TimeZoneInfo TaipeiTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly YuantaSparkOptions _options;
    private readonly object _sdkGate = new();
    private readonly object _tickGate = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _backfillGate = new(1, 1);
    private readonly SemaphoreSlim _dailyKlineGate = new(1, 1);
    private TaskCompletionSource<IReadOnlyList<DailyKlineCandle>>? _pendingDailyKline;
    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _desiredSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NormalizedTick> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingBackfill> _pendingBackfills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<NormalizedTick>> _ticks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<long>> _seenSequences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<NormalizedTick> _tickChannel = Channel.CreateUnbounded<NormalizedTick>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _writerCts = new();
    private readonly Task _writerTask;
    private readonly string _bootId = Guid.NewGuid().ToString("N");

    private TaskCompletionSource<string> _loginReady = NewLoginCompletion();
    private YuantaSparkAPITrader? _api;
    private string? _sessionAccount;
    private long _sessionGeneration;
    private DateOnly? _sessionTradeDate;
    private DateTimeOffset? _connectedAt;
    private DateTimeOffset? _lastLiveReceivedAt;
    private volatile bool _connected;

    public YuantaSparkMarketDataProvider(IOptions<YuantaSparkOptions> options)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _options = options.Value;
        Directory.CreateDirectory(_options.DataDirectory);
        _writerTask = Task.Run(() => TickWriterLoopAsync(_writerCts.Token));
    }

    public string Name => "YuantaSpark";

    public bool IsReady => GetReadiness().ChannelHasFreshLive;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var today = DateOnly.FromDateTime(TaipeiNow().Date);
            if (_connected && _sessionTradeDate == today) return;
            if (_connected) DisconnectCore(markDeactivated: true);
            await ConnectCoreAsync(cancellationToken);
        }
        finally { _sessionGate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try { DisconnectCore(markDeactivated: true); }
        finally { _sessionGate.Release(); }
    }

    public async Task MaintainSessionAsync(CancellationToken cancellationToken)
    {
        var now = TaipeiNow();
        var today = DateOnly.FromDateTime(now.Date);
        var rebuildAt = new TimeSpan(Math.Clamp(_options.SessionRebuildHour, 0, 23), Math.Clamp(_options.SessionRebuildMinute, 0, 59), 0);

        if (!_connected)
        {
            if (now.TimeOfDay >= rebuildAt)
            {
                await ConnectAsync(cancellationToken);
                await PrepareRequiredSubscriptionsAsync(cancellationToken);
            }
            return;
        }

        if (_sessionTradeDate == today)
        {
            if (IsLiveChannelStale(now))
            {
                await RebuildSessionAsync(cancellationToken);
                return;
            }

            bool sessionStartedBeforeDailyRebuild = _connectedAt.HasValue
                && _connectedAt.Value.Date == now.Date
                && _connectedAt.Value.TimeOfDay < rebuildAt
                && now.TimeOfDay >= rebuildAt;
            if (!sessionStartedBeforeDailyRebuild)
            {
                RearmSubmittedWithoutLive(now);
                // Idempotent for healthy subscriptions; FAILED/REARM_REQUIRED symbols are retried with pacing.
                await PrepareRequiredSubscriptionsAsync(cancellationToken);
                return;
            }
        }
        if (now.TimeOfDay < rebuildAt) return;

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            now = TaipeiNow();
            today = DateOnly.FromDateTime(now.Date);
            if (_sessionTradeDate != today)
            {
                DisconnectCore(markDeactivated: true);
                await ConnectCoreAsync(cancellationToken);
            }
        }
        finally { _sessionGate.Release(); }

        await PrepareRequiredSubscriptionsAsync(cancellationToken);
    }

    public async Task PrepareRequiredSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var targets = new Dictionary<string, string>(_desiredSubscriptions, StringComparer.OrdinalIgnoreCase);
        foreach (var item in LoadRequiredSubscriptions()) targets[item.Symbol] = item.Market;

        int max = Math.Max(1, _options.MaxSubscriptions);
        if (targets.Count > max)
            throw new InvalidOperationException($"required/desired subscription set contains {targets.Count} symbols, exceeding limit {max}.");

        foreach (var item in targets.OrderBy(x => x.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SubscribeAsync(item.Value, item.Key, cancellationToken);
                if (_options.SubscriptionSubmitDelayMilliseconds > 0)
                    await Task.Delay(_options.SubscriptionSubmitDelayMilliseconds, cancellationToken);
            }
            catch (InvalidOperationException) { /* state remains FAILED/missing; next maintenance pass retries */ }
        }

        if (_options.AckVerifyDelayMilliseconds > 0)
            await Task.Delay(_options.AckVerifyDelayMilliseconds, cancellationToken);
        VerifySubscriptionAcks();
    }

    public async Task SubscribeAsync(string market, string symbol, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        symbol = NormalizeSymbol(symbol);
        var marketType = ParseMarket(market);
        var marketName = marketType.ToString();
        if (!_desiredSubscriptions.ContainsKey(symbol) && _desiredSubscriptions.Count >= Math.Max(1, _options.MaxSubscriptions))
            throw new InvalidOperationException($"subscription limit reached ({_options.MaxSubscriptions}).");
        _desiredSubscriptions[symbol] = marketName;

        if (!_connected || _api is null || string.IsNullOrWhiteSpace(_sessionAccount))
            throw new InvalidOperationException("Yuanta SPARK is not connected.");

        var today = DateOnly.FromDateTime(TaipeiNow().Date);
        if (_sessionTradeDate != today)
            throw new InvalidOperationException("Yuanta SPARK session is stale for the current Taipei date.");

        await SubmitSubscriptionAsync(marketName, symbol, cancellationToken);
        QueueBackfillIfNeeded(marketName, symbol);
    }


    public async Task<IReadOnlyList<DailyKlineCandle>> QueryDailyKlineAsync(string market, string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (end < start) return Array.Empty<DailyKlineCandle>();
        await _dailyKlineGate.WaitAsync(cancellationToken);
        try
        {
            if (!_connected || _api is null || string.IsNullOrWhiteSpace(_sessionAccount))
                throw new InvalidOperationException("Yuanta SPARK is not connected.");
            var ready = new TaskCompletionSource<IReadOnlyList<DailyKlineCandle>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingDailyKline = ready;
            bool accepted;
            lock (_sdkGate)
            {
                accepted = _api.GetKLine(
                    _sessionAccount,
                    (KLineType)11,
                    ParseMarket(market),
                    NormalizeSymbol(symbol),
                    start.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
                    end.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
            }
            if (!accepted) throw new InvalidOperationException("Yuanta GetKLine() was not accepted.");
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(10, _options.BackfillTimeoutSeconds)), cancellationToken);
        }
        finally
        {
            _pendingDailyKline = null;
            _dailyKlineGate.Release();
        }
    }

    public IReadOnlyCollection<SubscriptionStatus> GetSubscriptions() => _subscriptions.Values
        .Select(x => x.View(_bootId)).OrderBy(x => x.Market).ThenBy(x => x.Symbol).ToArray();

    public MarketDataSessionStatus GetSessionStatus()
    {
        var now = TaipeiNow();
        var today = DateOnly.FromDateTime(now.Date);
        var generation = Interlocked.Read(ref _sessionGeneration);
        var currentCount = _subscriptions.Values.Count(state =>
        {
            lock (state.Gate) return state.SessionGeneration == generation && state.TradeDate == today;
        });

        bool liveChannelHealthy = !IsLiveChannelStale(now);
        bool effectiveConnected = _connected && liveChannelHealthy;
        return new(Name, _bootId, generation, _sessionTradeDate, _connectedAt, today, effectiveConnected,
            effectiveConnected && _sessionTradeDate == today, IsReady, _desiredSubscriptions.Count, currentCount);
    }

    public NormalizedTick? GetLatest(string symbol)
    {
        symbol = NormalizeSymbol(symbol);
        if (!_latest.TryGetValue(symbol, out var tick)) return null;
        var today = DateOnly.FromDateTime(TaipeiNow().Date);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(tick.ExchangeAt, TaipeiTimeZone).Date) == today ? tick : null;
    }

    public IReadOnlyList<NormalizedTick> GetTicks(string symbol, DateOnly tradeDate)
    {
        symbol = NormalizeSymbol(symbol);
        string key = TickKey(symbol, tradeDate);
        lock (_tickGate)
        {
            EnsureTicksLoadedUnsafe(symbol, tradeDate);
            return _ticks.TryGetValue(key, out var rows)
                ? rows.OrderBy(x => x.ExchangeAt).ToArray()
                : Array.Empty<NormalizedTick>();
        }
    }

    public MarketDataReadinessStatus GetReadiness(IReadOnlyCollection<string>? symbols = null)
    {
        var now = TaipeiNow();
        var today = DateOnly.FromDateTime(now.Date);
        var generation = Interlocked.Read(ref _sessionGeneration);
        bool sessionCurrent = _connected && _sessionTradeDate == today;
        var freshness = TimeSpan.FromSeconds(Math.Max(1, _options.ReadyFreshnessSeconds));

        string[] requested = symbols is { Count: > 0 }
            ? symbols.Select(NormalizeSymbol).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray()
            : _desiredSubscriptions.Keys.OrderBy(x => x).ToArray();

        var missingSubmitted = new List<string>();
        var missingLive = new List<string>();
        var staleLive = new List<string>();
        int submitted = 0, observed = 0, fresh = 0;

        foreach (string symbol in requested)
        {
            if (!_subscriptions.TryGetValue(symbol, out var state))
            {
                missingSubmitted.Add(symbol);
                missingLive.Add(symbol);
                continue;
            }
            lock (state.Gate)
            {
                bool current = sessionCurrent && state.SessionGeneration == generation && state.TradeDate == today;
                bool isSubmitted = current && state.SubmittedAt.HasValue && state.State is "ACK_CONFIRMED" or "LIVE_OBSERVED";
                bool isObserved = current && state.State == "LIVE_OBSERVED" && state.LastReceivedAt.HasValue;
                bool isFresh = isObserved && now - state.LastReceivedAt!.Value <= freshness;
                if (isSubmitted) submitted++; else missingSubmitted.Add(symbol);
                if (isObserved) observed++; else missingLive.Add(symbol);
                if (isFresh) fresh++; else if (isObserved) staleLive.Add(symbol);
            }
        }

        bool preopenPrepared = sessionCurrent && requested.Length > 0 && submitted == requested.Length;
        bool channelHasFreshLive = sessionCurrent && _subscriptions.Values.Any(state =>
        {
            lock (state.Gate)
                return state.SessionGeneration == generation && state.TradeDate == today && state.State == "LIVE_OBSERVED"
                    && state.LastReceivedAt.HasValue && now - state.LastReceivedAt.Value <= freshness;
        });
        bool requestedReady = sessionCurrent && requested.Length > 0 && fresh == requested.Length;

        return new(Name, _bootId, generation, today, sessionCurrent, preopenPrepared, channelHasFreshLive,
            requestedReady, requested.Length, submitted, observed, fresh, missingSubmitted.ToArray(),
            missingLive.ToArray(), staleLive.ToArray(), now);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        ValidateConnectionOptions();
        var environment = _options.Environment.Trim().ToUpperInvariant() switch
        {
            "UAT" => enumEnvironmentMode.UAT,
            "PROD" => enumEnvironmentMode.PROD,
            _ => throw new InvalidOperationException("Yuanta environment must be UAT or PROD.")
        };

        _loginReady = NewLoginCompletion();
        _latest.Clear();
        Directory.CreateDirectory(_options.LogDirectory);
        var api = new YuantaSparkAPITrader(_options.LogDirectory);
        api.SetLogType(enumLogType.COMMON);
        api.OnResponse += OnResponse;
        _api = api;

        lock (_sdkGate) api.Open(environment);
        await Task.Delay(_options.LoginDelayMilliseconds, cancellationToken);

        bool accepted;
        lock (_sdkGate)
        {
            accepted = OperatingSystem.IsLinux()
                ? api.Login(_options.CertificatePath, _options.CertificatePassword, _options.Account, _options.Password)
                : api.Login(_options.Account, _options.Password);
        }
        if (!accepted) throw new InvalidOperationException("Yuanta Login() was not accepted.");

        _sessionAccount = await _loginReady.Task.WaitAsync(TimeSpan.FromSeconds(_options.LoginTimeoutSeconds), cancellationToken);
        Interlocked.Increment(ref _sessionGeneration);
        var now = TaipeiNow();
        _sessionTradeDate = DateOnly.FromDateTime(now.Date);
        _connectedAt = now;
        _lastLiveReceivedAt = null;
        _connected = true;
        _subscriptions.Clear();
    }

    private Task SubmitSubscriptionAsync(string market, string symbol, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        symbol = NormalizeSymbol(symbol);
        var marketType = ParseMarket(market);
        var marketName = marketType.ToString();

        if (!_connected || _api is null || string.IsNullOrWhiteSpace(_sessionAccount) || !_sessionTradeDate.HasValue)
            throw new InvalidOperationException("Yuanta SPARK is not connected.");

        var now = TaipeiNow();
        var generation = Interlocked.Read(ref _sessionGeneration);
        var tradeDate = _sessionTradeDate.Value;
        var state = _subscriptions.AddOrUpdate(symbol,
            _ => new SubscriptionState(marketName, symbol, now, tradeDate, generation),
            (_, existing) =>
            {
                lock (existing.Gate)
                {
                    return existing.SessionGeneration == generation && existing.TradeDate == tradeDate && existing.Market.Equals(marketName, StringComparison.OrdinalIgnoreCase)
                        ? existing
                        : new SubscriptionState(marketName, symbol, now, tradeDate, generation);
                }
            });

        lock (state.Gate)
        {
            if (state.State is "SUBMITTED" or "ACK_CONFIRMED" or "LIVE_OBSERVED") return Task.CompletedTask;
            state.State = "SUBMITTING";
            state.AckState = "PENDING";
            state.AckConfirmedAt = null;
            state.LastError = null;
            try
            {
                lock (_sdkGate)
                {
                    _api.SubscribeStockTick(_sessionAccount, new List<StockTick>
                    {
                        new() { MarketType = marketType, StockCode = symbol }
                    });
                }
                state.SubmittedAt = TaipeiNow();
                state.State = "SUBMITTED";
            }
            catch (Exception ex)
            {
                state.State = "FAILED";
                state.LastError = ex.ToString();
                throw;
            }
        }
        return Task.CompletedTask;
    }

    private void QueueBackfillIfNeeded(string market, string symbol)
    {
        if (!_subscriptions.TryGetValue(symbol, out var state)) return;
        var now = TaipeiNow();
        lock (state.Gate)
        {
            var marketOpen = new DateTimeOffset(state.TradeDate.ToDateTime(new TimeOnly(9, 0)), TaipeiTimeZone.GetUtcOffset(state.TradeDate.ToDateTime(new TimeOnly(9, 0))));
            // A subscription submitted before market open has no pre-subscription trading gap to backfill.
            if (state.SubmittedAt.HasValue && state.SubmittedAt.Value < marketOpen)
            {
                state.BackfillState = "NOT_REQUIRED";
                return;
            }
            if (now.TimeOfDay < new TimeSpan(9, 0, 0))
            {
                state.BackfillState = "NOT_REQUIRED";
                return;
            }
            if (state.BackfillState is "QUEUED" or "RUNNING" or "COMPLETED") return;
            state.BackfillState = "QUEUED";
            state.BackfillError = null;
        }

        _ = Task.Run(async () =>
        {
            try { await RunBackfillAsync(market, symbol, CancellationToken.None); }
            catch (Exception ex)
            {
                if (_subscriptions.TryGetValue(symbol, out var current))
                    lock (current.Gate) { current.BackfillState = "FAILED"; current.BackfillError = ex.ToString(); }
            }
        });
    }

    private async Task RunBackfillAsync(string market, string symbol, CancellationToken cancellationToken)
    {
        await _backfillGate.WaitAsync(cancellationToken);
        try
        {
            if (!_connected || _api is null || string.IsNullOrWhiteSpace(_sessionAccount) || !_sessionTradeDate.HasValue) return;
            if (!_subscriptions.TryGetValue(symbol, out var state)) return;

            long generation = Interlocked.Read(ref _sessionGeneration);
            DateOnly tradeDate = _sessionTradeDate.Value;
            lock (state.Gate)
            {
                if (state.SessionGeneration != generation || state.TradeDate != tradeDate) return;
                state.BackfillState = "RUNNING";
            }

            var now = TaipeiNow();
            string end = now.TimeOfDay >= new TimeSpan(13, 30, 0) ? "13:30:00" : now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var ready = new TaskCompletionSource<IReadOnlyList<NormalizedTick>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingBackfill(generation, tradeDate, ready);
            if (!_pendingBackfills.TryAdd(symbol, pending)) return;

            try
            {
                bool accepted;
                lock (_sdkGate)
                    accepted = _api.GetStkTickDetail(_sessionAccount, ParseMarket(market), symbol, (enumStkTickSelectType)0, "09:00:00", end, 0);
                if (!accepted) throw new InvalidOperationException($"GetStkTickDetail was not accepted for {symbol}.");

                var rows = await ready.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _options.BackfillTimeoutSeconds)), cancellationToken);
                if (!_connected || _sessionTradeDate != tradeDate || Interlocked.Read(ref _sessionGeneration) != generation) return;

                foreach (var tick in rows) _tickChannel.Writer.TryWrite(tick);
                lock (state.Gate)
                {
                    state.BackfillTickCount = rows.Count;
                    state.BackfillState = "COMPLETED";
                }
            }
            finally { _pendingBackfills.TryRemove(symbol, out _); }
        }
        finally { _backfillGate.Release(); }
    }

    private void VerifySubscriptionAcks()
    {
        if (!_connected || _api is null || string.IsNullOrWhiteSpace(_sessionAccount) || !_sessionTradeDate.HasValue) return;

        dynamic response;
        lock (_sdkGate) response = _api.GetQuoteListSync(_sessionAccount);
        if (response is null) return;
        var now = TaipeiNow();
        var generation = Interlocked.Read(ref _sessionGeneration);
        var tradeDate = _sessionTradeDate.Value;

        if (!response.Success)
            throw new InvalidOperationException($"GetQuoteListSync failed: {response.ErrorMessage}");

        IEnumerable<Quote> quoteRows = response.objValue?.QuoteList ?? new List<Quote>();
        var acked = quoteRows
            .Select(x => x.StockCode?.Trim().ToUpperInvariant() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var state in _subscriptions.Values)
        {
            lock (state.Gate)
            {
                if (state.SessionGeneration != generation || state.TradeDate != tradeDate || !state.SubmittedAt.HasValue) continue;
                if (state.State == "LIVE_OBSERVED") continue;
                if (acked.Contains(state.Symbol))
                {
                    state.AckState = "CONFIRMED";
                    state.AckConfirmedAt = now;
                    state.State = "ACK_CONFIRMED";
                    state.LastError = null;
                }
                else
                {
                    state.AckState = "MISSING";
                    state.State = "ACK_MISSING";
                    state.LastError = "Symbol not present in GetQuoteListSync result after subscription submit.";
                }
            }
        }
    }


    private bool IsLiveChannelStale(DateTimeOffset now)
    {
        if (!_connected || !_sessionTradeDate.HasValue || _sessionTradeDate.Value != DateOnly.FromDateTime(now.Date)) return false;

        var open = new TimeSpan(Math.Clamp(_options.MarketOpenHour, 0, 23), Math.Clamp(_options.MarketOpenMinute, 0, 59), 0);
        var close = new TimeSpan(Math.Clamp(_options.MarketCloseHour, 0, 23), Math.Clamp(_options.MarketCloseMinute, 0, 59), 0);
        if (now.TimeOfDay < open || now.TimeOfDay > close) return false;

        var threshold = TimeSpan.FromSeconds(Math.Max(30, _options.ChannelStaleSeconds));
        var baseline = _lastLiveReceivedAt ?? _connectedAt;
        if (!baseline.HasValue || now - baseline.Value < threshold) return false;

        return _subscriptions.Values.Any(state =>
        {
            lock (state.Gate)
                return state.SessionGeneration == Interlocked.Read(ref _sessionGeneration)
                    && state.TradeDate == _sessionTradeDate.Value
                    && state.SubmittedAt.HasValue
                    && state.State is "ACK_CONFIRMED" or "LIVE_OBSERVED" or "SUBMITTED";
        });
    }

    private async Task RebuildSessionAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var now = TaipeiNow();
            if (!IsLiveChannelStale(now)) return;
            DisconnectCore(markDeactivated: true);
            await ConnectCoreAsync(cancellationToken);
        }
        finally { _sessionGate.Release(); }

        await PrepareRequiredSubscriptionsAsync(cancellationToken);
    }

    private void RearmSubmittedWithoutLive(DateTimeOffset now)
    {
        if (now.TimeOfDay < new TimeSpan(9, 0, 0)) return;
        var grace = TimeSpan.FromSeconds(Math.Max(5, _options.NoLiveResubmitSeconds));
        foreach (var state in _subscriptions.Values)
        {
            lock (state.Gate)
            {
                if (!state.SubmittedAt.HasValue) continue;
                if (state.State == "ACK_MISSING" || (state.State == "SUBMITTED" && now - state.SubmittedAt.Value >= grace))
                {
                    state.State = "REARM_REQUIRED";
                    state.LastError = "SPARK subscription ACK was not confirmed; scheduling paced resubscribe.";
                }
            }
        }
    }

    private void DisconnectCore(bool markDeactivated)
    {
        var api = _api;
        if (api is null) { _connected = false; _sessionAccount = null; return; }
        try
        {
            if (!string.IsNullOrWhiteSpace(_sessionAccount))
            {
                foreach (var state in _subscriptions.Values.Where(x => x.SubmittedAt.HasValue))
                {
                    try
                    {
                        lock (_sdkGate) api.UnSubscribeStockTick(_sessionAccount, new List<StockTick>
                        {
                            new() { MarketType = ParseMarket(state.Market), StockCode = state.Symbol }
                        });
                    }
                    catch { }
                    if (markDeactivated) lock (state.Gate) state.State = "DEACTIVATED";
                }
            }
            try { lock (_sdkGate) api.LogOut(); } catch { }
            try { lock (_sdkGate) api.Close(); } catch { }
        }
        finally
        {
            api.OnResponse -= OnResponse;
            _connected = false;
            _sessionAccount = null;
            _api = null;
            _lastLiveReceivedAt = null;
            foreach (var pending in _pendingBackfills.Values)
                pending.Ready.TrySetException(new InvalidOperationException("SPARK session disconnected during backfill."));
            _pendingBackfills.Clear();
        }
    }

    private void OnResponse(int mark, uint index, string function, object handle, object value)
    {
        var receivedAt = TaipeiNow();
        var responseFunction = function;
        var responseValue = value;
        if (string.IsNullOrWhiteSpace(responseFunction) && value is string alternateFunction && handle is not null)
        {
            responseFunction = alternateFunction;
            responseValue = handle;
        }
        if (mark == 0) return;

        if (responseFunction == "Login" && responseValue is LoginResult login)
        {
            if (login.LoginStatus.MsgCode is "0001" or "00001" && login.LoginList.Count > 0)
                _loginReady.TrySetResult(login.LoginList[0].Account.Trim());
            else
                _loginReady.TrySetException(new InvalidOperationException($"Yuanta login failed: {login.LoginStatus.MsgCode} {login.LoginStatus.MsgContent}"));
            return;
        }


        if (responseFunction == "GetKLine" && responseValue is KLineResult kline)
        {
            var rows = kline.KLineList
                .Select(x => new DailyKlineCandle(
                    DateOnly.FromDateTime(x.TimeStamp),
                    Convert.ToDecimal(x.OpenPrice, CultureInfo.InvariantCulture),
                    Convert.ToDecimal(x.HighPrice, CultureInfo.InvariantCulture),
                    Convert.ToDecimal(x.LowPrice, CultureInfo.InvariantCulture),
                    Convert.ToDecimal(x.ClosePrice, CultureInfo.InvariantCulture),
                    Convert.ToInt64(x.DealVol, CultureInfo.InvariantCulture)))
                .OrderBy(x => x.Date)
                .ToArray();
            _pendingDailyKline?.TrySetResult(rows);
            return;
        }

        if (responseFunction == "GetStkTickDetail" && responseValue is StickDetailResult detail)
        {
            string symbol = detail.StockCode?.Trim().ToUpperInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(symbol) || !_pendingBackfills.TryGetValue(symbol, out var pending)) return;
            if (!_connected || _sessionTradeDate != pending.TradeDate || Interlocked.Read(ref _sessionGeneration) != pending.SessionGeneration) return;

            var rows = new List<NormalizedTick>();
            foreach (dynamic x in detail.StickDetailList ?? Enumerable.Empty<dynamic>())
            {
                DateTime raw = x.TimeStamp;
                DateTime local = pending.TradeDate.ToDateTime(TimeOnly.MinValue).Add(raw.TimeOfDay);
                var exchangeAt = new DateTimeOffset(local, TaipeiTimeZone.GetUtcOffset(local));
                decimal price = SafeDecimal(x.DealPrice);
                long quantity = SafeLong(x.DealVol);
                if (price <= 0 || quantity < 0) continue;
                rows.Add(new NormalizedTick(symbol, detail.MarketNo.ToString(), price, quantity,
                    SafeDecimal(x.BuyPrice), SafeDecimal(x.SellPrice), SafeLong(x.SeqNo), exchangeAt, receivedAt, "SPARK-backfill"));
            }
            pending.Ready.TrySetResult(rows);
            return;
        }

        if (responseFunction != "SubscribeStockTick" || responseValue is not StockTickResult live) return;
        string liveSymbol = live.StkCode.Trim().ToUpperInvariant();
        if (!_subscriptions.TryGetValue(liveSymbol, out var state)) return;

        lock (state.Gate)
        {
            long generation = Interlocked.Read(ref _sessionGeneration);
            DateOnly today = DateOnly.FromDateTime(receivedAt.Date);
            if (!_connected || !_sessionTradeDate.HasValue || state.SessionGeneration != generation || state.TradeDate != _sessionTradeDate.Value || state.TradeDate != today) return;

            DateTime exchangeLocal = state.TradeDate.ToDateTime(TimeOnly.MinValue)
                .AddHours(live.Time.bytHour).AddMinutes(live.Time.bytMin).AddSeconds(live.Time.bytSec).AddMilliseconds(live.Time.ushtMSec);
            var tick = new NormalizedTick(liveSymbol, live.MarketType.ToString(), Convert.ToDecimal(live.DealPrice), Convert.ToInt64(live.DealVol),
                Convert.ToDecimal(live.BuyPrice), Convert.ToDecimal(live.SellPrice), Convert.ToInt64(live.SerialNo),
                new DateTimeOffset(exchangeLocal, TaipeiTimeZone.GetUtcOffset(exchangeLocal)), receivedAt, "SPARK-live");
            if (tick.Price <= 0 || tick.Quantity < 0) return;

            _latest[liveSymbol] = tick;
            state.AckState = "CONFIRMED_BY_LIVE";
            state.AckConfirmedAt ??= receivedAt;
            state.State = "LIVE_OBSERVED";
            state.FirstReceivedAt ??= receivedAt;
            state.LastReceivedAt = receivedAt;
            _lastLiveReceivedAt = receivedAt;
            state.CallbackCount++;
            _tickChannel.Writer.TryWrite(tick);
        }
    }

    private async Task TickWriterLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var tick in _tickChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try { PersistTick(tick); }
            catch { /* persistence failure must not mutate live subscription readiness */ }
        }
    }

    private void PersistTick(NormalizedTick tick)
    {
        DateOnly tradeDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(tick.ExchangeAt, TaipeiTimeZone).Date);
        string key = TickKey(tick.Symbol, tradeDate);
        lock (_tickGate)
        {
            EnsureTicksLoadedUnsafe(tick.Symbol, tradeDate);
            if (!_seenSequences.TryGetValue(key, out var seen)) _seenSequences[key] = seen = new HashSet<long>();
            if (tick.Sequence > 0 && !seen.Add(tick.Sequence)) return;
            if (!_ticks.TryGetValue(key, out var rows)) _ticks[key] = rows = new List<NormalizedTick>();
            rows.Add(tick);

            string directory = Path.Combine(_options.DataDirectory, tradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{tick.Symbol}.ticks.jsonl");
            File.AppendAllText(path, JsonSerializer.Serialize(tick, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    private void EnsureTicksLoadedUnsafe(string symbol, DateOnly tradeDate)
    {
        string key = TickKey(symbol, tradeDate);
        if (_ticks.ContainsKey(key)) return;
        var rows = new List<NormalizedTick>();
        var seen = new HashSet<long>();
        string path = Path.Combine(_options.DataDirectory, tradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), $"{symbol}.ticks.jsonl");
        if (File.Exists(path))
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var tick = JsonSerializer.Deserialize<NormalizedTick>(line, JsonOptions);
                    if (tick is null) continue;
                    rows.Add(tick);
                    if (tick.Sequence > 0) seen.Add(tick.Sequence);
                }
                catch (JsonException) { }
            }
        }
        _ticks[key] = rows;
        _seenSequences[key] = seen;
    }

    private IReadOnlyList<RequiredSubscription> LoadRequiredSubscriptions()
    {
        string path = _options.RequiredSymbolsPath;
        if (!File.Exists(path)) return Array.Empty<RequiredSubscription>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
            {
                if (DateOnly.TryParse(dateEl.GetString(), out var configuredDate) && configuredDate != DateOnly.FromDateTime(TaipeiNow().Date))
                    return Array.Empty<RequiredSubscription>();
            }
            JsonElement symbols = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("symbols", out var s) ? s : default;
            if (symbols.ValueKind != JsonValueKind.Array) return Array.Empty<RequiredSubscription>();

            var result = new List<RequiredSubscription>();
            foreach (var row in symbols.EnumerateArray())
            {
                string symbol = row.TryGetProperty("symbol", out var se) ? se.GetString()?.Trim().ToUpperInvariant() ?? "" : "";
                string market = row.TryGetProperty("market", out var me) ? me.GetString()?.Trim().ToUpperInvariant() ?? "" : "";
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(market)) continue;
                try { result.Add(new RequiredSubscription(symbol, ParseMarket(market).ToString())); } catch (ArgumentException) { }
            }
            return result.DistinctBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { return Array.Empty<RequiredSubscription>(); }
    }

    private void ValidateConnectionOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.Account) || string.IsNullOrWhiteSpace(_options.Password))
            throw new InvalidOperationException("Yuanta account/password are required when provider=Yuanta.");
        if (OperatingSystem.IsLinux())
        {
            if (string.IsNullOrWhiteSpace(_options.CertificatePath)) throw new InvalidOperationException("Yuanta certificate path is required on Linux.");
            if (!File.Exists(_options.CertificatePath)) throw new FileNotFoundException("Yuanta certificate file was not found.", _options.CertificatePath);
        }
    }

    private static enumMarketType ParseMarket(string market) => market.Trim().ToUpperInvariant() switch
    {
        "TWSE" => enumMarketType.TWSE,
        "TPEX" or "TWOTC" or "OTC" => enumMarketType.TWOTC,
        "TWEMERGING" or "EMERGING" => enumMarketType.TWEMERGING,
        _ => throw new ArgumentException($"unsupported market: {market}", nameof(market))
    };

    private static decimal SafeDecimal(object? value) { try { return value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture); } catch { return 0m; } }
    private static long SafeLong(object? value) { try { return value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture); } catch { return 0L; } }
    private static TaskCompletionSource<string> NewLoginCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string NormalizeSymbol(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol is required", nameof(symbol));
        return symbol;
    }
    private static string TickKey(string symbol, DateOnly date) => $"{date:yyyy-MM-dd}|{symbol.ToUpperInvariant()}";
    private static DateTimeOffset TaipeiNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TaipeiTimeZone);

    public void Dispose()
    {
        try { DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        _writerCts.Cancel();
        _tickChannel.Writer.TryComplete();
        try { _writerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _writerCts.Dispose();
        _sessionGate.Dispose();
        _backfillGate.Dispose();
        _dailyKlineGate.Dispose();
    }

    private sealed record PendingBackfill(long SessionGeneration, DateOnly TradeDate, TaskCompletionSource<IReadOnlyList<NormalizedTick>> Ready);
    private sealed record RequiredSubscription(string Symbol, string Market);

    private sealed class SubscriptionState(string market, string symbol, DateTimeOffset now, DateOnly tradeDate, long sessionGeneration)
    {
        public object Gate { get; } = new();
        public string Symbol { get; } = symbol;
        public string Market { get; } = market;
        public string State { get; set; } = "QUEUED";
        public string AckState { get; set; } = "NOT_CHECKED";
        public string BackfillState { get; set; } = "NOT_REQUIRED";
        public long SessionGeneration { get; } = sessionGeneration;
        public DateOnly TradeDate { get; } = tradeDate;
        public DateTimeOffset RequestedAt { get; } = now;
        public DateTimeOffset? SubmittedAt { get; set; }
        public DateTimeOffset? AckConfirmedAt { get; set; }
        public DateTimeOffset? FirstReceivedAt { get; set; }
        public DateTimeOffset? LastReceivedAt { get; set; }
        public long CallbackCount { get; set; }
        public long BackfillTickCount { get; set; }
        public string? LastError { get; set; }
        public string? BackfillError { get; set; }

        public SubscriptionStatus View(string bootId)
        {
            lock (Gate)
                return new(Symbol, Market, State, AckState, BackfillState, bootId, SessionGeneration, TradeDate, RequestedAt,
                    SubmittedAt, AckConfirmedAt, FirstReceivedAt, LastReceivedAt, CallbackCount, BackfillTickCount, LastError, BackfillError);
        }
    }
}
