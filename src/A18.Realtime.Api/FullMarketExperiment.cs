using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using A18.Realtime.Core;

namespace A18.Realtime.Api;

public sealed class A18OperationModeState
{
    public const string Legacy = "Legacy";
    public const string FullMarketExperimental = "FullMarketExperimental";
    public string Mode { get; }
    public bool IsFullMarketExperimental => Mode.Equals(FullMarketExperimental, StringComparison.OrdinalIgnoreCase);

    public A18OperationModeState(string? configured)
    {
        Mode = configured?.Trim() switch
        {
            var x when string.Equals(x, FullMarketExperimental, StringComparison.OrdinalIgnoreCase) => FullMarketExperimental,
            _ => Legacy
        };
    }
}

public sealed record FullMarketIngressTick(
    string Provider,
    string Session,
    string Symbol,
    decimal Price,
    long Quantity,
    long TimestampUnixMs);

public sealed record FullMarketLatestState(
    string Provider,
    string Session,
    string Symbol,
    decimal Price,
    long Quantity,
    long TimestampUnixMs,
    long EventCount);

public sealed class FullMarketStateStore
{
    private readonly ConcurrentDictionary<string, FullMarketLatestState> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _providerEvents = new(StringComparer.OrdinalIgnoreCase);
    private long _events;

    public bool Publish(FullMarketIngressTick tick)
    {
        var symbol = tick.Symbol?.Trim().ToUpperInvariant() ?? "";
        if (symbol.Length == 0 || tick.Price <= 0 || tick.TimestampUnixMs <= 0) return false;
        var provider = tick.Provider?.Trim() ?? "";
        var session = tick.Session?.Trim() ?? "";
        _latest.AddOrUpdate(symbol,
            _ => new(provider, session, symbol, tick.Price, tick.Quantity, tick.TimestampUnixMs, 1),
            (_, old) => new(provider, session, symbol, tick.Price, tick.Quantity, tick.TimestampUnixMs, old.EventCount + 1));
        Interlocked.Increment(ref _events);
        _providerEvents.AddOrUpdate($"{provider}:{session}", 1, (_, value) => value + 1);
        return true;
    }

    public bool PublishYuanta(NormalizedTick tick)
        => Publish(new("yuanta", "main", tick.Symbol, tick.Price, tick.Quantity, tick.ExchangeAt.ToUnixTimeMilliseconds()));

    public object Status(A18OperationModeState mode)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = _latest.Values.ToArray();
        var fresh = rows.Count(x => now - x.TimestampUnixMs <= 10_000);
        return new
        {
            operationMode = mode.Mode,
            enabled = mode.IsFullMarketExperimental,
            latestSymbols = rows.Length,
            freshSymbols10s = fresh,
            events = Interlocked.Read(ref _events),
            providers = _providerEvents.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value)
        };
    }

    public IReadOnlyList<FullMarketLatestState> Snapshot(int take = 2000)
        => _latest.Values.OrderByDescending(x => x.TimestampUnixMs).Take(Math.Clamp(take, 1, 5000)).ToArray();
}

public sealed class FullMarketBootstrapHostedService(
    A18OperationModeState mode,
    IConfiguration config,
    ILogger<FullMarketBootstrapHostedService> logger) : IHostedService
{
    private readonly List<Process> _collectors = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!mode.IsFullMarketExperimental)
        {
            logger.LogInformation("A18 operation mode {Mode}; full-market collectors disabled", mode.Mode);
            return Task.CompletedTask;
        }

        var planPath = config["A18:FullMarketPlanPath"] ?? "/warehouse/market/Taiwan/realtime_subscription_plan.json";
        var requiredPath = config["A18:FullMarketYuantaRequiredSymbolsPath"] ?? "/data/fullmarket-required-symbols.json";
        MaterializeYuantaMain(planPath, requiredPath);

        if (!config.GetValue("A18:FullMarketStartShioajiCollectors", true))
        {
            logger.LogWarning("FULL MARKET EXPERIMENT enabled with Shioaji collectors disabled by configuration");
            return Task.CompletedTask;
        }

        var python = config["A18:FullMarketShioajiPython"] ?? "/opt/shioaji/.venv/bin/python";
        var script = config["A18:FullMarketShioajiCollector"] ?? "/opt/shioaji/fullmarket_stream.py";
        var endpoint = config["A18:FullMarketIngestEndpoint"] ?? "http://127.0.0.1:8080/api/fullmarket/ticks";
        for (var session = 1; session <= 7; session++)
        {
            var psi = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("--session"); psi.ArgumentList.Add(session.ToString());
            psi.ArgumentList.Add("--plan"); psi.ArgumentList.Add(planPath);
            psi.ArgumentList.Add("--endpoint"); psi.ArgumentList.Add(endpoint);
            var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start Shioaji collector session {session}");
            _collectors.Add(process);
        }

        logger.LogWarning("FULL MARKET EXPERIMENT enabled: Yuanta main=200 isolated required list; Shioaji collectors=7x200");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var process in _collectors)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            process.Dispose();
        }
        _collectors.Clear();
        return Task.CompletedTask;
    }

    private static void MaterializeYuantaMain(string planPath, string requiredPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(planPath));
        var rows = new JsonArray();
        foreach (var item in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            var provider = item.TryGetProperty("provider", out var p) ? p.GetString() : null;
            var session = item.TryGetProperty("providerSession", out var s) ? s.ToString() : null;
            if (!string.Equals(provider, "yuanta", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(session, "main", StringComparison.OrdinalIgnoreCase)) continue;
            var symbol = item.GetProperty("symbol").GetString() ?? "";
            var market = item.TryGetProperty("market", out var m) ? m.GetString() ?? "TWSE" : "TWSE";
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol,
                ["market"] = market,
                ["sources"] = new JsonArray { "full-market-experimental" }
            });
        }
        if (rows.Count != 200) throw new InvalidOperationException($"full-market Yuanta main expected 200 symbols, got {rows.Count}");
        Directory.CreateDirectory(Path.GetDirectoryName(requiredPath)!);
        var root = new JsonObject { ["symbols"] = rows };
        var tmp = requiredPath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
        File.Move(tmp, requiredPath, true);
    }
}

public sealed class FullMarketYuantaMirrorHostedService(
    A18OperationModeState mode,
    IConfiguration config,
    IMarketDataProvider provider,
    FullMarketStateStore store,
    ILogger<FullMarketYuantaMirrorHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!mode.IsFullMarketExperimental) return;
        var planPath = config["A18:FullMarketPlanPath"] ?? "/warehouse/market/Taiwan/realtime_subscription_plan.json";
        var symbols = LoadYuantaMain(planPath);
        var seen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        logger.LogInformation("Yuanta full-market mirror watching {Count} symbols", symbols.Length);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var symbol in symbols)
            {
                var tick = provider.GetLatest(symbol);
                if (tick is null) continue;
                if (seen.TryGetValue(symbol, out var sequence) && sequence == tick.Sequence) continue;
                seen[symbol] = tick.Sequence;
                store.PublishYuanta(tick);
            }
            await Task.Delay(100, stoppingToken);
        }
    }

    private static string[] LoadYuantaMain(string planPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(planPath));
        return doc.RootElement.GetProperty("symbols").EnumerateArray()
            .Where(item =>
                item.TryGetProperty("provider", out var p) && string.Equals(p.GetString(), "yuanta", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("providerSession", out var s) && string.Equals(s.ToString(), "main", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.GetProperty("symbol").GetString() ?? "")
            .Where(x => x.Length > 0)
            .ToArray();
    }
}
