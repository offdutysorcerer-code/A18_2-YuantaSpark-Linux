using System.Text.Json;
using System.Text.RegularExpressions;
namespace A18.Realtime.Api;
internal sealed record AutoLongWatchRow(string Symbol, bool EntryEnabled, int MaxEntries, decimal BuyBudgetTwd, int FilledEntries, decimal CumulativeBuyTwd, string Status, DateTimeOffset UpdatedAt, string ActivationMode="IMMEDIATE", string? SessionDate=null, string? ActivateAt=null);
internal sealed record AutoLongWatchRequest(string Symbol, bool EntryEnabled, int MaxEntries, decimal BuyBudgetTwd, string ActivationMode="IMMEDIATE");
// Entry authorization registry. NO order submission in this class; running a live order
// requires the A18_21 entry dispatcher, reconciliation and broker acknowledgements.
internal sealed class AutoLongWatch(IConfiguration config)
{
    private readonly object _gate=new();
    public static string ResolveStatus(AutoLongWatchRow row,DateTime now)
    {
        if(!row.EntryEnabled)return "ENTRY_PAUSED";
        if(row.ActivationMode!="NEXT_SESSION")return "STAGING_WATCH_ONLY";
        if(!DateOnly.TryParseExact(row.SessionDate,"yyyy-MM-dd",out var date))return "INVALID_SCHEDULE";
        var today=DateOnly.FromDateTime(now);
        if(today<date)return "SCHEDULED";
        if(today>date||now.TimeOfDay>=new TimeSpan(9,30,0))return "EXPIRED";
        if(now.TimeOfDay<new TimeSpan(9,0,0))return "SCHEDULED";
        return "WATCH_ONLY";
    }

    private static readonly TimeZoneInfo Taipei=TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    private static DateTime NowTaipei()=>TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,Taipei).DateTime;
    private readonly string _holidays=config["AutoLong:NonTradingDaysPath"]??"/data/reference/non-trading-days.txt";
    private bool IsOpen(DateTime date,HashSet<string> excluded)=>date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)&&!excluded.Contains(date.ToString("yyyy-MM-dd"));
    private HashSet<string> Excluded()=>File.Exists(_holidays)?File.ReadAllLines(_holidays).Select(x=>x.Trim()).Where(x=>x.Length==10).ToHashSet():throw new InvalidOperationException("交易日曆不存在，拒絕建立預約");
    public object Preview(DateTime? reference=null)
    {
        var now=reference??NowTaipei();var excluded=Excluded();var day=now.Date;
        if(now.TimeOfDay>=new TimeSpan(9,0,0)||!IsOpen(day,excluded))day=day.AddDays(1);
        for(var i=0;i<35;i++,day=day.AddDays(1))if(IsOpen(day,excluded))return new{sessionDate=day.ToString("yyyy-MM-dd"),activateAt=$"{day:yyyy-MM-dd}T09:00:00+08:00",timeZone="Asia/Taipei",calendar="configured-exclusions",executionEnabled=false};
        throw new InvalidOperationException("未找到下一個交易日");
    }
    public AutoLongWatchRow[] RefreshStatuses()
    {
        lock(_gate){var rows=Read();bool dirty=false;var now=NowTaipei();foreach(var key in rows.Keys.ToArray())
        {var row=rows[key];if(row.ActivationMode!="NEXT_SESSION"||!row.EntryEnabled||string.IsNullOrWhiteSpace(row.SessionDate))continue;
         var status=ResolveStatus(row,now);
         if(status!=row.Status){rows[key]=row with{Status=status,EntryEnabled=status!="EXPIRED"};dirty=true;}
        }if(dirty)Write(rows);return rows.Values.OrderBy(x=>x.Symbol).ToArray();}
    }

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
    public AutoLongWatchRow[] List()=>RefreshStatuses();
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
            var scheduled=request.ActivationMode=="NEXT_SESSION";
            if(request.ActivationMode is not ("NEXT_SESSION" or "IMMEDIATE"))throw new ArgumentException("不支援的啟動模式");
            // Staging only: booking persists without executing orders.
            // Date is resolved only by the server; clients cannot supply a backdated day.
            var preview=scheduled&&enabled?Preview():null;
            var row=new AutoLongWatchRow(symbol,enabled,request.MaxEntries,request.BuyBudgetTwd,old?.FilledEntries??0,old?.CumulativeBuyTwd??0m,enabled?(scheduled?"SCHEDULED":"STAGING_WATCH_ONLY"):"ENTRY_PAUSED",DateTimeOffset.UtcNow,scheduled?"NEXT_SESSION":"IMMEDIATE",scheduled&&enabled?(string?)preview?.GetType().GetProperty("sessionDate")?.GetValue(preview):old?.SessionDate,scheduled&&enabled?(string?)preview?.GetType().GetProperty("activateAt")?.GetValue(preview):old?.ActivateAt);
            rows[symbol]=row;Write(rows);return row;
        }
    }
}
