using A18.Realtime.Core;
namespace A18.Realtime.Api;
/// <summary>
/// Opt-in staging PAPER execution loop. Stub/no feed => inert. No runtime21/broker calls.
/// Uses signal-confirmed fresh ticks for entry and independently manages open exits.
/// </summary>
internal sealed class AutoLongPaperRunner(IMarketDataProvider provider,AutoLongSignalMonitor signals,AutoLongPaperLedger ledger,AutoLongPaperPositions positions,AutoLongWatch watches,IConfiguration config,ILogger<AutoLongPaperRunner> log)
{
    private static readonly TimeZoneInfo Taipei=TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    public void Step(DateTimeOffset now)
    {
        if(!config.GetValue<bool>("A18:StagingSafeMode")||!config.GetValue<bool>("AutoLong:EnableIsolatedPaperRunner"))return;
        if(provider.Name.Equals("Stub",StringComparison.OrdinalIgnoreCase)||!provider.IsReady)return;
        var local=TimeZoneInfo.ConvertTime(now,Taipei);
        var date=DateOnly.FromDateTime(local.Date);
        var open=positions.List().Where(p=>p.Status=="OPEN").ToArray();
        // Exits remain active regardless of a watch being turned off or 09:30 cutoff.
        foreach(var position in open)
        {
            var after=position.LastObservedAt??position.EnteredAt;
            var ticks=provider.GetTicks(position.Symbol,date)
                .Where(t=>t.Price>0&&t.ExchangeAt<=now&&t.ExchangeAt>after)
                .OrderBy(t=>t.ExchangeAt).ThenBy(t=>t.Sequence).ToArray();
            foreach(var tick in ticks)
            {
                var updated=positions.Observe(position.IntentId,tick.Price,tick.Price,tick.Price,tick.Price,tick.ExchangeAt);
                if(updated.Position?.Status=="CLOSED")break;
            }
        }
        if(local.TimeOfDay<new TimeSpan(9,0,0)||local.TimeOfDay>=new TimeSpan(9,30,0))return;
        var observed=signals.List();
        var blocked=positions.UnreconciledSymbols().ToHashSet(StringComparer.Ordinal);
        foreach(var watch in watches.List().Where(w=>w.EntryEnabled&&w.Status is "WATCH_ONLY" or "STAGING_WATCH_ONLY"))
        {
            if(blocked.Contains(watch.Symbol))
            {
                log.LogError("AutoLong PAPER blocked: FILLED ledger without position for {Symbol}",watch.Symbol);
                continue;
            }
            if(positions.List().Any(p=>p.Symbol==watch.Symbol&&p.Status=="OPEN"))continue;
            var signal=observed.FirstOrDefault(o=>o.Symbol==watch.Symbol&&o.State=="SIGNAL_OBSERVED");
            if(signal?.LastSignalAt is not { } signalAt||signal.LastSignalPrice is not >0)continue;
            // Require a new tick strictly AFTER the completed five-second signal.
            if(now-signalAt>TimeSpan.FromSeconds(8)||signalAt>now)continue;
            var tick=provider.GetTicks(watch.Symbol,date)
                .Where(t=>t.Price>0&&t.ExchangeAt>signalAt&&t.ExchangeAt<=now&&now-t.ExchangeAt<TimeSpan.FromSeconds(4))
                .OrderBy(t=>t.ExchangeAt).FirstOrDefault();
            if(tick is null)continue;
            var remaining=watch.BuyBudgetTwd-ledger.List().Where(x=>x.Symbol==watch.Symbol).Sum(x=>x.FilledBuyTwd+x.ReservedBuyTwd);
            // Whole-lot V1; do not buy an odd lot without explicit trade-kind configuration.
            var lots=decimal.Floor(remaining/(tick.Price*1000m*1.02m));
            if(lots<1)continue;
            var quantity=(long)Math.Min(lots,1)*1000;
            var reservePrice=tick.Price*1.02m;
            var reservation=ledger.Reserve(watch.Symbol,signalAt,quantity,reservePrice);
            if(!reservation.Accepted||reservation.Intent is null)continue;
            var opened=positions.Open(reservation.Intent.Id,tick.Price,tick.ExchangeAt);
            if(!opened.Accepted)
            {
                ledger.CancelReservation(reservation.Intent.Id);
                log.LogWarning("Isolated PAPER open rejected for {Symbol}: {Reason}",watch.Symbol,opened.Reason);
            }
        }
    }
}
