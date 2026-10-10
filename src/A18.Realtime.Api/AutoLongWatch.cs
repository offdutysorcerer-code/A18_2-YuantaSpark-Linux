using System.Text.Json;
using System.Text.RegularExpressions;
namespace A18.Realtime.Api;
internal sealed record AutoLongWatchRow(string Symbol, bool EntryEnabled, int MaxEntries, decimal BuyBudgetTwd, int FilledEntries, decimal CumulativeBuyTwd, string Status, DateTimeOffset UpdatedAt);
internal sealed record AutoLongWatchRequest(string Symbol, bool EntryEnabled, int MaxEntries, decimal BuyBudgetTwd);
// Entry authorization registry. NO order submission in this class; running a live order
// requires the A18_21 entry dispatcher, reconciliation and broker acknowledgements.
internal sealed class AutoLongWatch(IConfiguration config)
{
    private readonly object _gate=new();
    private readonly string _path=config["AutoLong:StatePath"]??"/data/auto-long-v1.json";
    private Dictionary<string,AutoLongWatchRow> Read()
    {
        try {return File.Exists(_path)?JsonSerializer.Deserialize<Dictionary<string,AutoLongWatchRow>>(File.ReadAllText(_path))??new():new();}
        catch{return new();}
    }
    private void Write(Dictionary<string,AutoLongWatchRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string tmp=_path+".tmp";
        File.WriteAllText(tmp,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(tmp,_path,true);
    }
    public AutoLongWatchRow[] List(){lock(_gate)return Read().Values.OrderBy(x=>x.Symbol).ToArray();}
    public AutoLongWatchRow Set(AutoLongWatchRequest request)
    {
        var symbol=(request.Symbol??"").Trim().ToUpperInvariant();
        if(!Regex.IsMatch(symbol,@"^[0-9A-Z]{4,8}$"))throw new ArgumentException("股票代號格式不正確");
        if(request.MaxEntries<1||request.MaxEntries>1000)throw new ArgumentException("進場次數限制須在 1～1000 之間");
        if(request.BuyBudgetTwd<100m||request.BuyBudgetTwd>1_000_000_000m)throw new ArgumentException("累計買進金額上限須在 100～10 億元之間");
        lock(_gate)
        {
            var rows=Read();rows.TryGetValue(symbol,out var old);
            // Never erase already consumed entry-count or buy-only budget on toggle.
            var enabled=request.EntryEnabled&&!(old?.FilledEntries>=request.MaxEntries||old?.CumulativeBuyTwd>=request.BuyBudgetTwd);
            var row=new AutoLongWatchRow(symbol,enabled,request.MaxEntries,request.BuyBudgetTwd,old?.FilledEntries??0,old?.CumulativeBuyTwd??0m,enabled?"STAGING_WATCH_ONLY":"ENTRY_PAUSED",DateTimeOffset.UtcNow);
            rows[symbol]=row;Write(rows);return row;
        }
    }
}
