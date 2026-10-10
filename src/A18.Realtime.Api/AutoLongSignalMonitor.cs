using A18.Realtime.Core;
namespace A18.Realtime.Api;
internal sealed record AutoLongSignalObservation(string Symbol,string State,string Reason,DateTimeOffset? LastCompletedBarAt,DateTimeOffset? LastSignalAt,decimal? LastSignalPrice,DateTimeOffset UpdatedAt);
/// <summary>Signal-only observer, no PAPER or broker order functionality.</summary>
internal sealed class AutoLongSignalMonitor(IMarketDataProvider provider,AutoLongWatch watches)
{
    private readonly object _gate=new();
    private readonly Dictionary<string,AutoLongSignalObservation> _rows=new();
    private static readonly TimeZoneInfo Taipei=TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    public AutoLongSignalObservation[] List(){lock(_gate)return _rows.Values.OrderBy(x=>x.Symbol).ToArray();}
    public void Scan(DateTimeOffset now)
    {
        var local=TimeZoneInfo.ConvertTime(now,Taipei);
        foreach(var task in watches.List().Where(x=>x.EntryEnabled&&x.Status is "WATCH_ONLY" or "STAGING_WATCH_ONLY"))
        {
            var reason="NO_LIVE_FEED";string state="WAITING_DATA";DateTimeOffset? completed=null,signal=null;decimal? price=null;
            if(local.TimeOfDay<new TimeSpan(9,0,0)||local.TimeOfDay>=new TimeSpan(9,30,0))reason="OUTSIDE_ENTRY_WINDOW";
            else if(provider.Name.Equals("Stub",StringComparison.OrdinalIgnoreCase))reason="STUB_PROVIDER_NO_EXECUTION";
            else if(!provider.IsReady)reason="PROVIDER_NOT_READY";
            else
            {
                var ticks=provider.GetTicks(task.Symbol,DateOnly.FromDateTime(local.Date))
                    .Where(t=>t.Price>0&&t.ExchangeAt<=now&&t.ExchangeAt>=now.AddMinutes(-3))
                    .OrderBy(t=>t.ExchangeAt).ToArray();
                var buckets=ticks.GroupBy(t=>t.ExchangeAt.ToUnixTimeSeconds()/5*5).OrderBy(x=>x.Key)
                    .Where(x=>DateTimeOffset.FromUnixTimeSeconds(x.Key+5)<=now.AddMilliseconds(-250))
                    .Select(x=>(at:DateTimeOffset.FromUnixTimeSeconds(x.Key),close:x.Last().Price)).ToArray();
                if(buckets.Length>0)completed=buckets[^1].at.AddSeconds(5);
                if(completed is null||(now-completed.Value).TotalSeconds>10)reason="STALE_COMPLETED_BAR";
                else
                {
                    state="OBSERVING";reason="V2_60S_5SEC_CLOSE";
                    decimal? prev=null;var ups=new Queue<DateTimeOffset>();
                    foreach(var bar in buckets)
                    {
                        if(prev is not null)
                        {
                            if(bar.close>prev.Value)
                            {
                                ups.Enqueue(bar.at);
                                while(ups.Count>0&&(bar.at.AddSeconds(5)-ups.Peek()).TotalSeconds>60)ups.Dequeue();
                                if(ups.Count==3){signal=bar.at.AddSeconds(5);price=bar.close;ups.Clear();}
                            }
                            else if(bar.close<prev.Value)ups.Clear();
                        }
                        prev=bar.close;
                    }
                    if(signal is not null&&now-signal.Value<=TimeSpan.FromSeconds(12)){
                        state="SIGNAL_OBSERVED";reason="SHADOW_ONLY_NO_ORDER";
                    }
                }
            }
            lock(_gate)_rows[task.Symbol]=new(task.Symbol,state,reason,completed,signal,price,now);
        }
    }
}
internal sealed class AutoLongSignalHostedService(AutoLongSignalMonitor signals,IConfiguration configuration,ILogger<AutoLongSignalHostedService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!configuration.GetValue<bool>("A18:StagingSafeMode"))return;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        do{try{signals.Scan(DateTimeOffset.UtcNow);}catch(Exception e){logger.LogError(e,"AutoLong shadow signal scan failed");}}
        while(await timer.WaitForNextTickAsync(ct));
    }
}
