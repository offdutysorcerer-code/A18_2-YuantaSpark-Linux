using System.Collections.Concurrent;
using A18.Realtime.Core;
using A18.Realtime.Api;
using A18.YuantaSpark;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<YuantaSparkOptions>(builder.Configuration.GetSection("Yuanta"));
string symbolsPath = builder.Configuration["A18:SymbolsPath"]?.Trim() ?? "/data/reference/symbols.csv";
builder.Services.AddSingleton(new TaiwanSymbolDirectory(symbolsPath));

string providerName = builder.Configuration["A18:Provider"]?.Trim() ?? "Stub";
if (providerName.Equals("Yuanta", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IMarketDataProvider, YuantaSparkMarketDataProvider>();
}
else if (providerName.Equals("Stub", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IMarketDataProvider, StubMarketDataProvider>();
}
else
{
    throw new InvalidOperationException($"Unsupported A18 provider: {providerName}");
}

builder.Services.AddHostedService<MarketDataProviderHostedService>();
builder.Services.AddSingleton<HistoricalSecondBars>();
builder.Services.AddSingleton<SwingGroups>();
builder.Services.AddSingleton<SwingState>();

var app = builder.Build();

app.MapGet("/", () => Results.Content(DiagnosticUi.Html, "text/html; charset=utf-8"));
app.MapGet("/swing", () => Results.Content(SwingUi.Html, "text/html; charset=utf-8"));

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "live",
    service = "A18_2",
    at = DateTimeOffset.Now
}));

app.MapGet("/health/ready", (IMarketDataProvider provider) =>
    provider.IsReady
        ? Results.Ok(new { status = "ready", provider = provider.Name, at = DateTimeOffset.Now })
        : Results.Json(new { status = "not_ready", provider = provider.Name, at = DateTimeOffset.Now }, statusCode: 503));

app.MapGet("/api/symbols/names", (TaiwanSymbolDirectory symbols) => Results.Ok(symbols.AllNames()));

app.MapGet("/api/swing/state", (SwingState state) => Results.Ok(state.Read()));
app.MapPut("/api/swing/state", async (HttpRequest request, SwingState state) =>
{
    var incoming=await request.ReadFromJsonAsync<SwingBrowseState>() ?? new();
    return Results.Ok(state.Save(incoming));
});

app.MapGet("/api/swing/groups", (SwingGroups groups) => Results.Ok(groups.Read()));
app.MapPut("/api/swing/groups", async (HttpRequest request, SwingGroups groups) =>
{
    var incoming = await request.ReadFromJsonAsync<Dictionary<string,string[]>>() ?? new();
    return Results.Ok(groups.Save(incoming));
});

app.MapGet("/api/session", (IMarketDataProvider provider) => Results.Ok(provider.GetSessionStatus()));

app.MapGet("/api/subscriptions", (IMarketDataProvider provider) => Results.Ok(provider.GetSubscriptions()));

app.MapGet("/api/readiness", (string? symbols, IMarketDataProvider provider) =>
{
    try
    {
        string[]? requested = string.IsNullOrWhiteSpace(symbols)
            ? null
            : symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Results.Ok(provider.GetReadiness(requested));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/subscriptions/{market}/{symbol}", async (
    string market,
    string symbol,
    IMarketDataProvider provider,
    CancellationToken ct) =>
{
    try
    {
        await provider.SubscribeAsync(market, symbol, ct);
        return Results.Accepted(
            $"/api/subscriptions/{market}/{symbol}",
            new
            {
                market = market.Trim().ToUpperInvariant(),
                symbol = symbol.Trim().ToUpperInvariant()
            });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

app.MapGet("/api/ticks/{symbol}/latest", (string symbol, IMarketDataProvider provider) =>
{
    try
    {
        var tick = provider.GetLatest(symbol);
        return tick is null ? Results.NotFound() : Results.Ok(tick);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/ticks/{symbol}", (string symbol, string? date, IMarketDataProvider provider, TaiwanSymbolDirectory symbols) =>
{
    try
    {
        var taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, taipei).Date);
        var requested = string.IsNullOrWhiteSpace(date) ? today : DateOnly.ParseExact(date, "yyyy-MM-dd");
        var ticks = provider.GetTicks(symbol, requested);
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        return Results.Ok(new
        {
            symbol = normalizedSymbol,
            name = symbols.GetName(normalizedSymbol),
            tradeDate = requested,
            hasData = ticks.Count > 0,
            dataState = ticks.Count > 0 ? "AVAILABLE" : "NO_DATA_YET",
            ticks
        });
    }
    catch (FormatException)
    {
        return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/calendar/non-trading/{date}", (string date, HistoricalSecondBars history) =>
{
    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d)) return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
    return Results.Ok(new { date, nonTrading = history.IsKnownNonTradingDay(d) });
});

app.MapGet("/api/bars/{symbol}", async (string symbol, string? date, int? interval, IMarketDataProvider provider, TaiwanSymbolDirectory symbols, HistoricalSecondBars history, CancellationToken ct) =>
{
    try
    {
        var taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, taipei).Date);
        var requested = string.IsNullOrWhiteSpace(date) ? today : DateOnly.ParseExact(date, "yyyy-MM-dd");
        int seconds = interval ?? 60;
        if (seconds is not (1 or 5 or 60 or 300))
            return Results.BadRequest(new { error = "interval must be one of 1, 5, 60, 300 seconds" });

        if (requested > today)
        {
            var futureSymbol = symbol.Trim().ToUpperInvariant();
            return Results.Ok(new { symbol = futureSymbol, name = symbols.GetName(futureSymbol), tradeDate = requested, intervalSeconds = seconds, hasData = false, dataState = "FUTURE_DATE", source = "NONE", bars = Array.Empty<object>() });
        }

        if (requested < today)
        {
            var historical = await history.GetAsync(symbol, requested, ct);
            var secondBars = historical.Bars;
            var historicalBars = secondBars
                .GroupBy(x => x.StartTime.ToUnixTimeSeconds() / seconds * seconds)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    startTime = DateTimeOffset.FromUnixTimeSeconds(g.Key).ToOffset(TimeSpan.FromHours(8)),
                    open = g.First().Open,
                    high = g.Max(x => x.High),
                    low = g.Min(x => x.Low),
                    close = g.Last().Close,
                    volume = g.Sum(x => x.Volume),
                    tickCount = g.Sum(x => x.TickCount)
                }).ToArray();
            var normalizedHistoricalSymbol = symbol.Trim().ToUpperInvariant();
            return Results.Ok(new { symbol = normalizedHistoricalSymbol, name = symbols.GetName(normalizedHistoricalSymbol), tradeDate = requested, intervalSeconds = seconds, hasData = historicalBars.Length > 0, dataState = historicalBars.Length > 0 ? "AVAILABLE" : "NO_DATA_YET", source = historical.Source, bars = historicalBars });
        }

        var normalizedTodaySymbol = symbol.Trim().ToUpperInvariant();
        var ticks = provider.GetTicks(normalizedTodaySymbol, requested).Where(x => x.Price > 0).OrderBy(x => x.ExchangeAt).ToArray();
        if (requested == today)
        {
            try { await provider.SubscribeAsync(symbols.GetMarket(normalizedTodaySymbol), normalizedTodaySymbol, ct); } catch (InvalidOperationException) { }
            if (ticks.Length == 0)
            {
                var historical = await history.GetAsync(normalizedTodaySymbol, requested, ct);
                if (historical.Bars.Count > 0)
                {
                    var backfillBars = historical.Bars.GroupBy(x => x.StartTime.ToUnixTimeSeconds() / seconds * seconds).OrderBy(g => g.Key).Select(g => new { startTime = DateTimeOffset.FromUnixTimeSeconds(g.Key).ToOffset(TimeSpan.FromHours(8)), open = g.First().Open, high = g.Max(x => x.High), low = g.Min(x => x.Low), close = g.Last().Close, volume = g.Sum(x => x.Volume), tickCount = g.Sum(x => x.TickCount) }).ToArray();
                    return Results.Ok(new { symbol = normalizedTodaySymbol, name = symbols.GetName(normalizedTodaySymbol), tradeDate = requested, intervalSeconds = seconds, hasData = true, dataState = "AVAILABLE", source = historical.Source, bars = backfillBars });
                }
            }
        }
        var bars = ticks
            .GroupBy(x =>
            {
                long bucket = x.ExchangeAt.ToUnixTimeSeconds() / seconds * seconds;
                return DateTimeOffset.FromUnixTimeSeconds(bucket).ToOffset(x.ExchangeAt.Offset);
            })
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var rows = g.OrderBy(x => x.ExchangeAt).ToArray();
                return new
                {
                    startTime = g.Key,
                    open = rows[0].Price,
                    high = rows.Max(x => x.Price),
                    low = rows.Min(x => x.Price),
                    close = rows[^1].Price,
                    volume = rows.Sum(x => x.Quantity),
                    tickCount = rows.Length
                };
            })
            .ToArray();

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        return Results.Ok(new
        {
            symbol = normalizedSymbol,
            name = symbols.GetName(normalizedSymbol),
            tradeDate = requested,
            intervalSeconds = seconds,
            hasData = bars.Length > 0,
            dataState = bars.Length > 0 ? "AVAILABLE" : "NO_DATA_YET",
            bars
        });
    }
    catch (FormatException)
    {
        return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();

sealed class MarketDataProviderHostedService(
    IMarketDataProvider provider,
    ILogger<MarketDataProviderHostedService> logger) : IHostedService
{
    private CancellationTokenSource? _maintenanceCts;
    private Task? _maintenanceTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await provider.ConnectAsync(cancellationToken);
            await provider.PrepareRequiredSubscriptionsAsync(cancellationToken);
            logger.LogInformation("Market data provider {Provider} connected and required subscriptions prepared", provider.Name);

            _maintenanceCts = new CancellationTokenSource();
            _maintenanceTask = Task.Run(() => MaintainLoopAsync(_maintenanceCts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Market data provider {Provider} failed to connect", provider.Name);
            if (!provider.Name.Equals("Stub", StringComparison.OrdinalIgnoreCase)) throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_maintenanceCts is not null)
        {
            _maintenanceCts.Cancel();
            if (_maintenanceTask is not null)
            {
                try { await _maintenanceTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            _maintenanceCts.Dispose();
        }

        try
        {
            await provider.DisconnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Market data provider {Provider} failed to disconnect cleanly", provider.Name);
        }
    }

    private async Task MaintainLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await provider.MaintainSessionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Market data provider {Provider} session maintenance failed", provider.Name);
            }
        }
    }
}

sealed class StubMarketDataProvider : IMarketDataProvider
{
    private static readonly TimeZoneInfo TaipeiTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    private readonly ConcurrentDictionary<string, SubscriptionStatus> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _bootId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _connectedAt = TaipeiNow();
    private const long Generation = 1;

    public string Name => "Stub";
    public bool IsReady => false;
    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task MaintainSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task PrepareRequiredSubscriptionsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SubscribeAsync(string market, string symbol, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        symbol = symbol.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol is required", nameof(symbol));

        market = market.Trim().ToUpperInvariant();
        var now = TaipeiNow();
        _subscriptions[symbol] = new SubscriptionStatus(
            symbol,
            market,
            "ACK_CONFIRMED",
            "CONFIRMED",
            "NOT_REQUIRED",
            _bootId,
            Generation,
            DateOnly.FromDateTime(now.Date),
            now,
            now,
            now,
            null,
            null,
            0,
            0,
            "Stub provider does not connect to Yuanta SPARK.",
            null);
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<SubscriptionStatus> GetSubscriptions() => _subscriptions.Values.ToArray();

    public MarketDataSessionStatus GetSessionStatus()
    {
        var now = TaipeiNow();
        var today = DateOnly.FromDateTime(now.Date);
        return new(
            Name,
            _bootId,
            Generation,
            today,
            _connectedAt,
            today,
            true,
            true,
            false,
            _subscriptions.Count,
            _subscriptions.Count);
    }

    public NormalizedTick? GetLatest(string symbol) => null;
    public IReadOnlyList<NormalizedTick> GetTicks(string symbol, DateOnly tradeDate) => Array.Empty<NormalizedTick>();
    public MarketDataReadinessStatus GetReadiness(IReadOnlyCollection<string>? symbols = null)
    {
        var now = TaipeiNow();
        var today = DateOnly.FromDateTime(now.Date);
        string[] requested = symbols is { Count: > 0 }
            ? symbols.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct().ToArray()
            : _subscriptions.Keys.OrderBy(x => x).ToArray();
        int submitted = requested.Count(x => _subscriptions.ContainsKey(x));
        return new(Name, _bootId, Generation, today, true, requested.Length > 0 && submitted == requested.Length,
            false, false, requested.Length, submitted, 0, 0,
            requested.Where(x => !_subscriptions.ContainsKey(x)).ToArray(), requested, Array.Empty<string>(), now);
    }

    private static DateTimeOffset TaipeiNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TaipeiTimeZone);
}

static class DiagnosticUi
{
    public const string Html = """
<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>A18_2 Realtime Diagnostics</title>
<style>
body{font-family:system-ui,-apple-system,sans-serif;margin:24px;max-width:1000px;background:#111;color:#eee}
.card{border:1px solid #444;border-radius:12px;padding:16px;margin-bottom:16px;background:#1a1a1a}
.row{display:flex;gap:10px;flex-wrap:wrap;align-items:end}
label{display:flex;flex-direction:column;gap:6px;font-size:13px;color:#bbb}
input,select,button{font:inherit;padding:9px 10px;border-radius:8px;border:1px solid #555;background:#222;color:#eee}
button{cursor:pointer} button:hover{background:#333}
.badge{display:inline-block;padding:4px 8px;border-radius:999px;background:#333;margin-right:8px}
pre{white-space:pre-wrap;word-break:break-word;background:#0c0c0c;padding:12px;border-radius:8px;min-height:40px}
.small{color:#aaa;font-size:13px} h1{font-size:22px;margin-top:0} h2{font-size:16px}
</style>
</head>
<body>
<h1>A18_2 · Yuanta SPARK Realtime Diagnostics</h1>
<p><a href="/swing" style="color:#54a8ff">開啟 K線波段轉折觀察台 →</a></p>
<div class="card">
  <div class="row">
    <span class="badge" id="liveBadge">LIVE ?</span>
    <span class="badge" id="readyBadge">READY ?</span>
    <button onclick="refreshAll()">重新整理</button>
  </div>
  <h2>Session</h2>
  <pre id="session">loading...</pre>
</div>
<div class="card">
  <h2>訂閱股票</h2>
  <div class="row">
    <label>市場
      <select id="market">
        <option value="TWSE">TWSE 上市</option>
        <option value="TPEX">TPEX 上櫃</option>
        <option value="TWEMERGING">TWEMERGING 興櫃</option>
      </select>
    </label>
    <label>股票代號
      <input id="symbol" value="2330" inputmode="numeric" autocomplete="off">
    </label>
    <label>資料日期
      <input id="tradeDate" type="date">
    </label>
    <button onclick="subscribeSymbol()">訂閱</button>
    <button onclick="loadLatest()">查今日最新 Tick</button>
    <button onclick="loadDay()">查指定日期</button>
  </div>
  <div class="small">指定哪一天就只查哪一天；沒有資料回 NO_DATA_YET，不會 fallback 到前一交易日。SUBMITTED_WAITING_LIVE 只代表訂閱呼叫已送出；收到真正 callback 才會變成 LIVE_OBSERVED。</div>
  <h2>Tick Data</h2>
  <pre id="latest">尚未查詢</pre>
</div>
<div class="card">
  <h2>Subscriptions</h2>
  <pre id="subscriptions">loading...</pre>
</div>
<script>
const pretty=v=>JSON.stringify(v,null,2);
async function getJson(url,options){
  const r=await fetch(url,options);
  let body=null; try{body=await r.json()}catch{}
  return {ok:r.ok,status:r.status,body};
}
async function refreshAll(){
  const [live,ready,session,subs]=await Promise.all([
    getJson('/health/live'),getJson('/health/ready'),getJson('/api/session'),getJson('/api/subscriptions')
  ]);
  document.getElementById('liveBadge').textContent=`LIVE ${live.status}`;
  document.getElementById('readyBadge').textContent=`READY ${ready.status}`;
  document.getElementById('session').textContent=pretty(session.body ?? {status:session.status});
  document.getElementById('subscriptions').textContent=pretty(subs.body ?? {status:subs.status});
}
async function subscribeSymbol(){
  const market=document.getElementById('market').value;
  const symbol=document.getElementById('symbol').value.trim();
  if(!symbol)return;
  const r=await getJson(`/api/subscriptions/${encodeURIComponent(market)}/${encodeURIComponent(symbol)}`,{method:'POST'});
  document.getElementById('latest').textContent=pretty({subscribeStatus:r.status,result:r.body});
  await refreshAll();
}
async function loadLatest(){
  const symbol=document.getElementById('symbol').value.trim();
  if(!symbol)return;
  const r=await getJson(`/api/ticks/${encodeURIComponent(symbol)}/latest`);
  document.getElementById('latest').textContent=r.status===404?'尚未收到本日 live callback (404)':pretty(r.body ?? {status:r.status});
}
async function loadDay(){
  const symbol=document.getElementById('symbol').value.trim();
  const date=document.getElementById('tradeDate').value;
  if(!symbol||!date)return;
  const r=await getJson(`/api/ticks/${encodeURIComponent(symbol)}?date=${encodeURIComponent(date)}`);
  document.getElementById('latest').textContent=pretty(r.body ?? {status:r.status});
}
const now=new Date();
document.getElementById('tradeDate').value=`${now.getFullYear()}-${String(now.getMonth()+1).padStart(2,'0')}-${String(now.getDate()).padStart(2,'0')}`;
refreshAll();
setInterval(refreshAll,5000);
</script>
</body>
</html>
""";
}
