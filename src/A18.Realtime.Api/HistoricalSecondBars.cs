using System.Diagnostics;
using System.Globalization;

namespace A18.Realtime.Api;

public sealed record SecondBar(DateTimeOffset StartTime, decimal Open, decimal High, decimal Low, decimal Close, long Volume, decimal Turnover, long TickCount);
public sealed record HistoricalSecondBarsResult(string Source, IReadOnlyList<SecondBar> Bars);

public sealed class HistoricalSecondBars(ILogger<HistoricalSecondBars> logger, IConfiguration config)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly string _root = config["History:SecondRoot"] ?? "/warehouse/market/Taiwan/KLines/Second";
    private readonly string _python = config["History:Python"] ?? "/opt/shioaji/.venv/bin/python";
    private readonly string _fetcher = config["History:Fetcher"] ?? "/opt/shioaji/history_fetch.py";
    private readonly SemaphoreSlim _fetchLock = new(1,1);

    public async Task<HistoricalSecondBarsResult> GetAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        symbol=symbol.Trim().ToUpperInvariant();
        var path=Path.Combine(_root,symbol,$"{date:yyyy-MM-dd}.csv");
        if (File.Exists(path)) return new("A18_10_SECOND", Read(path));
        await _fetchLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return new("A18_10_SECOND", Read(path));
            var psi=new ProcessStartInfo(_python) { RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false };
            psi.ArgumentList.Add(_fetcher); psi.ArgumentList.Add(symbol); psi.ArgumentList.Add(date.ToString("yyyy-MM-dd")); psi.ArgumentList.Add(path);
            using var p=Process.Start(psi) ?? throw new InvalidOperationException("Unable to start Shioaji history fetcher");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(Timeout);
            await p.WaitForExitAsync(timeout.Token); var stdout=await p.StandardOutput.ReadToEndAsync(); var stderr=await p.StandardError.ReadToEndAsync();
            if (!File.Exists(path) && stdout.Contains("\"status\": \"NO_DATA\"", StringComparison.OrdinalIgnoreCase)) return new("SINOPAC_SHIOAJI_NO_DATA", Array.Empty<SecondBar>());
            if (p.ExitCode!=0 || !File.Exists(path)) throw new InvalidOperationException($"Shioaji history fetch failed ({p.ExitCode}): {stderr.Trim()} {stdout.Trim()}");
            logger.LogInformation("Historical second bars fetched via Shioaji: {Symbol} {Date} {Output}",symbol,date,stdout.Trim());
            return new("SINOPAC_SHIOAJI_FETCHED", Read(path));
        }
        finally { _fetchLock.Release(); }
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
