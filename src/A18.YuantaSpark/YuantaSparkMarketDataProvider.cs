using System.Collections.Concurrent;
using System.Text;
using A18.Realtime.Core;
using Microsoft.Extensions.Options;
using YuantaOneAPI;

namespace A18.YuantaSpark;

public sealed class YuantaSparkMarketDataProvider : IMarketDataProvider, IDisposable
{
    private static readonly TimeZoneInfo TaipeiTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private readonly YuantaSparkOptions _options;
    private readonly object _sdkGate = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _desiredSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NormalizedTick> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _bootId = Guid.NewGuid().ToString("N");

    private TaskCompletionSource<string> _loginReady = NewLoginCompletion();
    private YuantaSparkAPITrader? _api;
    private string? _sessionAccount;
    private long _sessionGeneration;
    private DateOnly? _sessionTradeDate;
    private DateTimeOffset? _connectedAt;
    private volatile bool _connected;

    public YuantaSparkMarketDataProvider(IOptions<YuantaSparkOptions> options)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _options = options.Value;
    }

    public string Name => "YuantaSpark";

    public bool IsReady
    {
        get
        {
            var now = TaipeiNow();
            var today = DateOnly.FromDateTime(now.Date);
            var generation = Interlocked.Read(ref _sessionGeneration);
            if (!_connected || _sessionTradeDate != today) return false;

            var freshness = TimeSpan.FromSeconds(Math.Max(1, _options.ReadyFreshnessSeconds));
            return _subscriptions.Values.Any(state =>
            {
                lock (state.Gate)
                {
                    return state.SessionGeneration == generation
                        && state.TradeDate == today
                        && state.State == "LIVE_OBSERVED"
                        && state.LastReceivedAt.HasValue
                        && now - state.LastReceivedAt.Value <= freshness;
                }
            });
        }
    }

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
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            DisconnectCore(markDeactivated: true);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task MaintainSessionAsync(CancellationToken cancellationToken)
    {
        if (!_connected) return;

        var now = TaipeiNow();
        var today = DateOnly.FromDateTime(now.Date);
        if (_sessionTradeDate == today) return;

        var rebuildAt = new TimeSpan(
            Math.Clamp(_options.SessionRebuildHour, 0, 23),
            Math.Clamp(_options.SessionRebuildMinute, 0, 59),
            0);

        // At midnight the old session is immediately stale because IsReady requires today's trade date.
        // Rebuild once the configured preparation time is reached (default 08:45 Asia/Taipei).
        if (now.TimeOfDay < rebuildAt) return;

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            now = TaipeiNow();
            today = DateOnly.FromDateTime(now.Date);
            if (_sessionTradeDate == today) return;

            DisconnectCore(markDeactivated: true);
            await ConnectCoreAsync(cancellationToken);

            foreach (var desired in _desiredSubscriptions.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await SubmitSubscriptionAsync(desired.Value, desired.Key, cancellationToken);
                }
                catch
                {
                    // The subscription state carries FAILED + LastError. Continue rearming other symbols.
                }
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task SubscribeAsync(string market, string symbol, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        symbol = NormalizeSymbol(symbol);
        var marketType = ParseMarket(market);
        var marketName = marketType.ToString();

        _desiredSubscriptions[symbol] = marketName;

        if (!_connected || _api is null || string.IsNullOrWhiteSpace(_sessionAccount))
            throw new InvalidOperationException("Yuanta SPARK is not connected.");

        var today = DateOnly.FromDateTime(TaipeiNow().Date);
        if (_sessionTradeDate != today)
            throw new InvalidOperationException("Yuanta SPARK session is stale for the current Taipei date; wait for the daily session rebuild.");

        await SubmitSubscriptionAsync(marketName, symbol, cancellationToken);
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
            lock (state.Gate)
                return state.SessionGeneration == generation && state.TradeDate == today;
        });

        return new(
            Name,
            _bootId,
            generation,
            _sessionTradeDate,
            _connectedAt,
            today,
            _connected,
            _connected && _sessionTradeDate == today,
            IsReady,
            _desiredSubscriptions.Count,
            currentCount);
    }

    public NormalizedTick? GetLatest(string symbol)
    {
        symbol = NormalizeSymbol(symbol);
        if (!_latest.TryGetValue(symbol, out var tick)) return null;

        var today = DateOnly.FromDateTime(TaipeiNow().Date);
        if (_sessionTradeDate != today) return null;
        var receivedDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(tick.ReceivedAt, TaipeiTimeZone).Date);
        return receivedDate == today ? tick : null;
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

        if (!accepted)
            throw new InvalidOperationException("Yuanta Login() was not accepted.");

        _sessionAccount = await _loginReady.Task.WaitAsync(
            TimeSpan.FromSeconds(_options.LoginTimeoutSeconds), cancellationToken);

        Interlocked.Increment(ref _sessionGeneration);
        var now = TaipeiNow();
        _sessionTradeDate = DateOnly.FromDateTime(now.Date);
        _connectedAt = now;
        _connected = true;

        // Current-state view is per session generation. Desired subscriptions live separately and survive rollover.
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
                    return existing.SessionGeneration == generation
                        && existing.TradeDate == tradeDate
                        && existing.Market.Equals(marketName, StringComparison.OrdinalIgnoreCase)
                        ? existing
                        : new SubscriptionState(marketName, symbol, now, tradeDate, generation);
                }
            });

        lock (state.Gate)
        {
            if (state.State is "SUBMITTED_WAITING_LIVE" or "LIVE_OBSERVED")
                return Task.CompletedTask;

            state.State = "SUBMITTING";
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
                state.State = "SUBMITTED_WAITING_LIVE";
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

    private void DisconnectCore(bool markDeactivated)
    {
        var api = _api;
        if (api is null)
        {
            _connected = false;
            _sessionAccount = null;
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_sessionAccount))
            {
                foreach (var state in _subscriptions.Values.Where(x => x.SubmittedAt.HasValue))
                {
                    try
                    {
                        lock (_sdkGate)
                            api.UnSubscribeStockTick(_sessionAccount, new List<StockTick>
                            {
                                new() { MarketType = ParseMarket(state.Market), StockCode = state.Symbol }
                            });
                    }
                    catch { }

                    if (markDeactivated)
                    {
                        lock (state.Gate)
                            state.State = "DEACTIVATED";
                    }
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
                _loginReady.TrySetException(new InvalidOperationException(
                    $"Yuanta login failed: {login.LoginStatus.MsgCode} {login.LoginStatus.MsgContent}"));
            return;
        }

        if (responseFunction != "SubscribeStockTick" || responseValue is not StockTickResult live)
            return;

        var symbol = live.StkCode.Trim().ToUpperInvariant();
        if (!_subscriptions.TryGetValue(symbol, out var state)) return;

        lock (state.Gate)
        {
            var generation = Interlocked.Read(ref _sessionGeneration);
            var today = DateOnly.FromDateTime(receivedAt.Date);

            // A callback from a previous login generation/date must never make the new session ready.
            if (!_connected
                || !_sessionTradeDate.HasValue
                || state.SessionGeneration != generation
                || state.TradeDate != _sessionTradeDate.Value
                || state.TradeDate != today)
                return;

            var exchangeLocal = state.TradeDate.ToDateTime(TimeOnly.MinValue)
                .AddHours(live.Time.bytHour)
                .AddMinutes(live.Time.bytMin)
                .AddSeconds(live.Time.bytSec)
                .AddMilliseconds(live.Time.ushtMSec);
            var exchangeAt = new DateTimeOffset(exchangeLocal, TaipeiTimeZone.GetUtcOffset(exchangeLocal));

            var tick = new NormalizedTick(
                symbol,
                live.MarketType.ToString(),
                Convert.ToDecimal(live.DealPrice),
                Convert.ToInt64(live.DealVol),
                Convert.ToDecimal(live.BuyPrice),
                Convert.ToDecimal(live.SellPrice),
                Convert.ToInt64(live.SerialNo),
                exchangeAt,
                receivedAt,
                "SPARK-live");

            if (tick.Price <= 0 || tick.Quantity < 0) return;

            _latest[symbol] = tick;
            state.State = "LIVE_OBSERVED";
            state.FirstReceivedAt ??= receivedAt;
            state.LastReceivedAt = receivedAt;
            state.CallbackCount++;
        }
    }

    private void ValidateConnectionOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.Account) || string.IsNullOrWhiteSpace(_options.Password))
            throw new InvalidOperationException("Yuanta account/password are required when provider=Yuanta.");

        if (OperatingSystem.IsLinux())
        {
            if (string.IsNullOrWhiteSpace(_options.CertificatePath))
                throw new InvalidOperationException("Yuanta certificate path is required on Linux.");
            if (!File.Exists(_options.CertificatePath))
                throw new FileNotFoundException("Yuanta certificate file was not found.", _options.CertificatePath);
        }
    }

    private static enumMarketType ParseMarket(string market) => market.Trim().ToUpperInvariant() switch
    {
        "TWSE" => enumMarketType.TWSE,
        "TPEX" or "TWOTC" or "OTC" => enumMarketType.TWOTC,
        "TWEMERGING" or "EMERGING" => enumMarketType.TWEMERGING,
        _ => throw new ArgumentException($"unsupported market: {market}", nameof(market))
    };

    private static TaskCompletionSource<string> NewLoginCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NormalizeSymbol(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("symbol is required", nameof(symbol));
        return symbol;
    }

    private static DateTimeOffset TaipeiNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TaipeiTimeZone);

    public void Dispose()
    {
        DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        _sessionGate.Dispose();
    }

    private sealed class SubscriptionState(
        string market,
        string symbol,
        DateTimeOffset now,
        DateOnly tradeDate,
        long sessionGeneration)
    {
        public object Gate { get; } = new();
        public string Symbol { get; } = symbol;
        public string Market { get; } = market;
        public string State { get; set; } = "QUEUED";
        public long SessionGeneration { get; } = sessionGeneration;
        public DateOnly TradeDate { get; } = tradeDate;
        public DateTimeOffset RequestedAt { get; } = now;
        public DateTimeOffset? SubmittedAt { get; set; }
        public DateTimeOffset? FirstReceivedAt { get; set; }
        public DateTimeOffset? LastReceivedAt { get; set; }
        public long CallbackCount { get; set; }
        public string? LastError { get; set; }

        public SubscriptionStatus View(string bootId)
        {
            lock (Gate)
                return new(
                    Symbol,
                    Market,
                    State,
                    bootId,
                    SessionGeneration,
                    TradeDate,
                    RequestedAt,
                    SubmittedAt,
                    FirstReceivedAt,
                    LastReceivedAt,
                    CallbackCount,
                    LastError);
        }
    }
}
