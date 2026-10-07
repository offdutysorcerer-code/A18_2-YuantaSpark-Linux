using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace A18.Realtime.Api;

public sealed record SecondBar(DateTimeOffset StartTime, decimal Open, decimal High, decimal Low, decimal Close, long Volume, decimal Turnover, long TickCount);
public sealed record HistoricalSecondBarsResult(string Source, IReadOnlyList<SecondBar> Bars);

public sealed class HistoricalSecondBars(ILogger<HistoricalSecondBars> logger, IConfiguration config)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly string _root = config["History:SecondRoot"] ?? "/warehouse/market/Taiwan/KLines/Second";
    private readonly string _intradayRoot = config["History:IntradayRoot"] ?? "/data/intraday";
    private readonly string _python = config["History:Python"] ?? "/opt/shioaji/.venv/bin/python";
    private readonly string _fetcher = config["History:Fetcher"] ?? "/opt/shioaji/history_fetch.py";
    private readonly SemaphoreSlim _fetchLock = new(1,1);
    private readonly string _nonTradingDaysPath = config["History:NonTradingDaysPath"] ?? "/data/reference/non-trading-days.txt";

    public async Task<HistoricalSecondBarsResult> GetAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        symbol=symbol.Trim().ToUpperInvariant();
        var intradayPath=Path.Combine(_intradayRoot,date.ToString("yyyy-MM-dd"),$"{symbol}.ticks.jsonl");
        if (File.Exists(intradayPath))
        {
            var localTicks=ReadIntradayTicks(intradayPath);
            if (localTicks.Count>0) return new("A18_2_INTRADAY_TICKS",localTicks);
        }
        var path=Path.Combine(_root,symbol,$"{date:yyyy-MM-dd}.csv");
        if (File.Exists(path)) return new("A18_10_SECOND", Read(path));
        if (IsKnownNonTradingDay(date)) return new("KNOWN_NON_TRADING_DAY", Array.Empty<SecondBar>());
        await _fetchLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return new("A18_10_SECOND", Read(path));
            var psi=new ProcessStartInfo(_python) { RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false };
            psi.ArgumentList.Add(_fetcher); psi.ArgumentList.Add(symbol); psi.ArgumentList.Add(date.ToString("yyyy-MM-dd")); psi.ArgumentList.Add(path);
            using var p=Process.Start(psi) ?? throw new InvalidOperationException("Unable to start Shioaji history fetcher");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(Timeout);
            await p.WaitForExitAsync(timeout.Token); var stdout=await p.StandardOutput.ReadToEndAsync(); var stderr=await p.StandardError.ReadToEndAsync();
            // A single symbol returning NO_DATA does not prove the whole market was closed.
            // Keep the date-level non-trading calendar authoritative and do not poison it from symbol-level history misses.
            if (!File.Exists(path) && stdout.Contains("\"status\": \"NO_DATA\"", StringComparison.OrdinalIgnoreCase))
                return new("SINOPAC_SHIOAJI_NO_DATA", Array.Empty<SecondBar>());
            if (p.ExitCode!=0 || !File.Exists(path)) throw new InvalidOperationException($"Shioaji history fetch failed ({p.ExitCode}): {stderr.Trim()} {stdout.Trim()}");
            logger.LogInformation("Historical second bars fetched via Shioaji: {Symbol} {Date} {Output}",symbol,date,stdout.Trim());
            return new("SINOPAC_SHIOAJI_FETCHED", Read(path));
        }
        finally { _fetchLock.Release(); }
    }

    public bool IsKnownNonTradingDay(DateOnly date)
    {
        if (!File.Exists(_nonTradingDaysPath)) return false;
        var key = date.ToString("yyyy-MM-dd");
        return File.ReadLines(_nonTradingDaysPath).Any(x => x.Trim() == key);
    }

    private static IReadOnlyList<SecondBar> ReadIntradayTicks(string path)
    {
        var ticks=new List<(DateTimeOffset At, decimal Price, long Quantity)>();
        foreach(var line in File.ReadLines(path))
        {
            if(string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc=JsonDocument.Parse(line);
                var root=doc.RootElement;
                if(!root.TryGetProperty("exchangeAt",out var atNode) || !DateTimeOffset.TryParse(atNode.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.None,out var at)) continue;
                if(!root.TryGetProperty("price",out var priceNode) || !priceNode.TryGetDecimal(out var price) || price<=0) continue;
                var quantity=0L;
                if(root.TryGetProperty("quantity",out var quantityNode)) quantityNode.TryGetInt64(out quantity);
                ticks.Add((at,price,Math.Max(0,quantity)));
            }
            catch(JsonException) { }
        }
        return ticks.GroupBy(x=>x.At.ToUnixTimeSeconds()).OrderBy(g=>g.Key).Select(g=>
        {
            var rows=g.OrderBy(x=>x.At).ToArray();
            var start=DateTimeOffset.FromUnixTimeSeconds(g.Key).ToOffset(rows[0].At.Offset);
            return new SecondBar(start,rows[0].Price,rows.Max(x=>x.Price),rows.Min(x=>x.Price),rows[^1].Price,rows.Sum(x=>x.Quantity),rows.Sum(x=>x.Price*x.Quantity),rows.LongLength);
        }).ToArray();
    }

    private static IReadOnlyList<SecondBar> Read(string path)
    {
        var tz=TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"); var rows=new List<SecondBar>();
        foreach(var line in File.ReadLines(path).Skip(1))
        {
            var c=line.Trim().Split(','); if(c.Length<8) continue;
            if(!DateTime.TryParseExact(c[0],"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var local)) continue;
            var dto=new DateTimeOffset(DateTime.SpecifyKind(local,DateTimeKind.Unspecified),tz.GetUtcOffset(local));
            rows.Add(new(dto,decimal.Parse(c[1],CultureInfo.InvariantCulture),decimal.Parse(c[2],CultureInfo.InvariantCulture),decimal.Parse(c[3],CultureInfo.InvariantCulture),decimal.Parse(c[4],CultureInfo.InvariantCulture),long.Parse(c[5],CultureInfo.InvariantCulture),decimal.Parse(c[6],CultureInfo.InvariantCulture),long.Parse(c[7],CultureInfo.InvariantCulture)));
        }
        return rows;
    }
}
