using System.Text.Json;
namespace A18.Realtime.Api;
internal sealed record AutoLongPaperIntent(string Id,string Symbol,string SignalTime,long Quantity,decimal ReferencePrice,decimal ReservedBuyTwd,string Status,DateTimeOffset CreatedAt,decimal FilledBuyTwd=0m);
internal sealed record AutoLongPaperSummary(string Symbol,int EntryCount,decimal CumulativeBuyTwd,decimal ReservedBuyTwd,int PendingCount,int MaxEntries,decimal BuyBudgetTwd);
/// <summary>Atomic shadow order-intent ledger. No external broker/PAPER execution.</summary>
internal sealed class AutoLongPaperLedger(IConfiguration config,AutoLongWatch watches)
{
    private readonly object _gate=new();
    private readonly string _path=config["AutoLong:PaperLedgerPath"]??"/data/auto-long-paper-intents.json";
    private Dictionary<string,AutoLongPaperIntent> Read(){try{return File.Exists(_path)?JsonSerializer.Deserialize<Dictionary<string,AutoLongPaperIntent>>(File.ReadAllText(_path))??new():new();}catch{return new();}}
    private void Write(Dictionary<string,AutoLongPaperIntent> rows){Directory.CreateDirectory(Path.GetDirectoryName(_path)!);var tmp=_path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,_path,true);}
    public AutoLongPaperIntent[] List(){lock(_gate)return Read().Values.OrderByDescending(x=>x.CreatedAt).ToArray();}
    public AutoLongPaperSummary[] Summary(){lock(_gate){var rows=Read().Values.ToArray();return watches.List().Select(w=>{
        var mine=rows.Where(r=>r.Symbol==w.Symbol).ToArray();return new AutoLongPaperSummary(w.Symbol,mine.Count(x=>x.Status=="FILLED"),mine.Sum(x=>x.FilledBuyTwd),mine.Where(x=>x.Status=="RESERVED").Sum(x=>x.ReservedBuyTwd),mine.Count(x=>x.Status=="RESERVED"),w.MaxEntries,w.BuyBudgetTwd);
    }).ToArray();}}
    public (bool Accepted,string Reason,AutoLongPaperIntent? Intent) Reserve(string symbol,DateTimeOffset signalTime,long quantity,decimal price)
    {
        lock(_gate)
        {
            var watch=watches.List().FirstOrDefault(x=>x.Symbol==symbol);
            if(watch is null||!watch.EntryEnabled||(watch.Status is not ("WATCH_ONLY" or "STAGING_WATCH_ONLY")))return(false,"WATCH_DISABLED",null);
            if(signalTime.Offset!=TimeSpan.FromHours(8))signalTime=signalTime.ToOffset(TimeSpan.FromHours(8));
            if(signalTime.TimeOfDay<new TimeSpan(9,0,0)||signalTime.TimeOfDay>=new TimeSpan(9,30,0))return(false,"OUTSIDE_ENTRY_WINDOW",null);
            if(watch.ActivationMode=="NEXT_SESSION"&&watch.SessionDate!=signalTime.ToString("yyyy-MM-dd"))return(false,"WRONG_SESSION",null);
            if(quantity<=0||price<=0||quantity>1_000_000)return(false,"INVALID_SIZE_OR_PRICE",null);
            var amount=quantity*price;var rows=Read();var id=$"{symbol}:{signalTime:yyyyMMddHHmmss}";
            if(rows.TryGetValue(id,out var existing))return(false,"SIGNAL_ALREADY_PROCESSED",existing);
            var mine=rows.Values.Where(x=>x.Symbol==symbol).ToArray();
            if(mine.Count(x=>x.Status is "RESERVED" or "FILLED")>=watch.MaxEntries)return(false,"ENTRY_LIMIT",null);
            var used=mine.Sum(x=>x.FilledBuyTwd)+mine.Where(x=>x.Status=="RESERVED").Sum(x=>x.ReservedBuyTwd);
            if(used+amount>watch.BuyBudgetTwd)return(false,"BUY_BUDGET_LIMIT",null);
            var intent=new AutoLongPaperIntent(id,symbol,signalTime.ToString("O"),quantity,price,amount,"RESERVED",DateTimeOffset.UtcNow);
            rows[id]=intent;Write(rows);return(true,"SHADOW_RESERVED",intent);
        }
    }
    // Called only after confirmed PAPER fill reconciliation, not on signal observation.
    public bool ReconcileFill(string id,decimal actualFilledBuyTwd)
    {
        lock(_gate){var rows=Read();if(!rows.TryGetValue(id,out var row)||row.Status!="RESERVED"||actualFilledBuyTwd<=0||actualFilledBuyTwd>row.ReservedBuyTwd)return false;
        rows[id]=row with{Status="FILLED",FilledBuyTwd=actualFilledBuyTwd,ReservedBuyTwd=0};Write(rows);return true;}
    }
}
