using System.Collections.Concurrent;
using A18.Realtime.Core;
using A18.YuantaSpark;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<YuantaSparkOptions>(builder.Configuration.GetSection("Yuanta"));

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

var app = builder.Build();

app.MapGet("/", () => Results.Content(DiagnosticUi.Html, "text/html; charset=utf-8"));

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

app.MapGet("/api/session", (IMarketDataProvider provider) => Results.Ok(provider.GetSessionStatus()));

app.MapGet("/api/subscriptions", (IMarketDataProvider provider) => Results.Ok(provider.GetSubscriptions()));

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
            logger.LogInformation("Market data provider {Provider} connected", provider.Name);

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
            "SUBMITTED_WAITING_LIVE",
            _bootId,
            Generation,
            DateOnly.FromDateTime(now.Date),
            now,
            now,
            null,
            null,
            0,
            "Stub provider does not connect to Yuanta SPARK.");
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
    <button onclick="subscribeSymbol()">訂閱</button>
    <button onclick="loadLatest()">查最新 Tick</button>
  </div>
  <div class="small">SUBMITTED_WAITING_LIVE 只代表訂閱呼叫已送出；收到真正 callback 才會變成 LIVE_OBSERVED。</div>
  <h2>Latest Tick</h2>
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
refreshAll();
setInterval(refreshAll,5000);
</script>
</body>
</html>
""";
}
