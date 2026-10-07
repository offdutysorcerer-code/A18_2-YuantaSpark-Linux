using System.Globalization;
using A18.YuantaSpark;
using A18.Realtime.Core;

namespace A18.Realtime.Api;

internal sealed record DailyEnsureResult(
    string Symbol,
    DateOnly? Before,
    DateOnly? After,
    DateOnly Through,
    int YuantaAdded,
    int ShioajiAdded,
    string Status,
    string? YuantaError = null);

internal sealed class DailyKlineEnsureService(
    IMarketDataProvider provider,
    TaiwanSymbolDirectory symbols,
    HistoricalSecondBars history,
    IConfiguration config,
    ILogger<DailyKlineEnsureService> logger)
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    private readonly string _dailyRoot = config["History:DailyRoot"] ?? "/warehouse/market/Taiwan/KLines/Daily";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DailyEnsureResult> EnsureAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (symbol.Length == 0) throw new ArgumentException("symbol is required", nameof(symbol));

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_dailyRoot);
            string path = Path.Combine(_dailyRoot, symbol + ".csv");
            var rows = ReadDaily(path);
            DateOnly? before = rows.Count == 0 ? null : rows.Keys.Max();
            DateOnly through = CompletedThrough();
            while (through.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || history.IsKnownNonTradingDay(through))
                through = through.AddDays(-1);
            DateOnly start = before?.AddDays(1) ?? through.AddMonths(-11);
            if (start > through)
                return new(symbol, before, before, through, 0, 0, "UP_TO_DATE");

            int yuantaAdded = 0, shioajiAdded = 0;
            string? yuantaError = null;
            var yuantaDates = new HashSet<DateOnly>();

            if (provider is YuantaSparkMarketDataProvider yuanta)
            {
                try
                {
                    var fetched = await yuanta.QueryDailyKlineAsync(symbols.GetMarket(symbol), symbol, start, through, ct);
                    foreach (var c in fetched)
                    {
                        if (c.Date < start || c.Date > through) continue;
                        rows[c.Date] = new(c.Date, c.Open, c.High, c.Low, c.Close, c.Volume);
                        yuantaDates.Add(c.Date);
                        yuantaAdded++;
                    }
                }
                catch (Exception ex)
                {
                    yuantaError = ex.Message;
                    logger.LogWarning(ex, "Yuanta daily K backfill failed for {Symbol} {Start}~{Through}; falling back to Shioaji.", symbol, start, through);
                }
            }
            else
            {
                yuantaError = $"provider {provider.Name} is not YuantaSpark";
            }

            for (var d = start; d <= through; d = d.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                if (rows.ContainsKey(d)) continue;

                try
                {
                    var second = await history.GetAsync(symbol, d, ct);
                    if (second.Bars.Count == 0) continue;
                    var ordered = second.Bars.OrderBy(x => x.StartTime).ToArray();
                    rows[d] = new DailyRow(
                        d,
                        ordered[0].Open,
                        ordered.Max(x => x.High),
                        ordered.Min(x => x.Low),
                        ordered[^1].Close,
                        ordered.Sum(x => x.Volume));
                    shioajiAdded++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Shioaji daily fallback failed for {Symbol} {Date}.", symbol, d);
                }
            }

            if (yuantaAdded > 0 || shioajiAdded > 0) WriteDaily(path, rows.Values);
            DateOnly? after = rows.Count == 0 ? null : rows.Keys.Max();
            string status = yuantaAdded + shioajiAdded > 0 ? "UPDATED" : "NO_NEW_DATA";
            return new(symbol, before, after, through, yuantaAdded, shioajiAdded, status, yuantaError);
        }
        finally { _gate.Release(); }
    }

    private static DateOnly CompletedThrough()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Taipei);
        var today = DateOnly.FromDateTime(now.Date);
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return today;
        return now.TimeOfDay >= new TimeSpan(13, 35, 0) ? today : today.AddDays(-1);
    }

    private static SortedDictionary<DateOnly, DailyRow> ReadDaily(string path)
    {
        var result = new SortedDictionary<DateOnly, DailyRow>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var p = line.Split(',');
            if (p.Length < 6 || !DateOnly.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (!decimal.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o)) continue;
            if (!decimal.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h)) continue;
            if (!decimal.TryParse(p[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l)) continue;
            if (!decimal.TryParse(p[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var c)) continue;
            if (!long.TryParse(p[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) continue;
            result[d] = new(d, o, h, l, c, v);
        }
        return result;
    }

    private static void WriteDaily(string path, IEnumerable<DailyRow> rows)
    {
        string tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, false, new System.Text.UTF8Encoding(false)))
        {
            w.WriteLine("date,open,high,low,close,volume");
            foreach (var r in rows.OrderBy(x => x.Date))
                w.WriteLine($"{r.Date:yyyy-MM-dd},{F(r.Open)},{F(r.High)},{F(r.Low)},{F(r.Close)},{r.Volume}");
        }
        File.Move(tmp, path, true);
    }

    private static string F(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    private sealed record DailyRow(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
}
