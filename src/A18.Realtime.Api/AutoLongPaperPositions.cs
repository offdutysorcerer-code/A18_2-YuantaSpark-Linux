using System.Text.Json;
namespace A18.Realtime.Api;
internal sealed record AutoLongPaperPosition(string IntentId,string Symbol,long Quantity,decimal EntryPrice,decimal HighWater,decimal LastPrice,string Status,decimal? ExitPrice,DateTimeOffset EnteredAt,DateTimeOffset? ExitedAt,string ExitReason="",decimal StopPercent=1m,decimal TrailPercent=1.5m);
/// <summary>Isolated staging PAPER simulator. Never calls Runtime21 or the broker.</summary>
internal sealed class AutoLongPaperPositions(IConfiguration config,AutoLongPaperLedger ledger)
{
    private readonly object _gate=new();
    private readonly string _path=config["AutoLong:PaperPositionPath"]??"/data/auto-long-paper-positions.json";
    private Dictionary<string,AutoLongPaperPosition> Read(){try{return File.Exists(_path)?JsonSerializer.Deserialize<Dictionary<string,AutoLongPaperPosition>>(File.ReadAllText(_path))??new():new();}catch{return new();}}
    private void Write(Dictionary<string,AutoLongPaperPosition> rows){Directory.CreateDirectory(Path.GetDirectoryName(_path)!);var tmp=_path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,_path,true);}
    public AutoLongPaperPosition[] List(){lock(_gate)return Read().Values.OrderByDescending(x=>x.EnteredAt).ToArray();}
    public (bool Accepted,string Reason,AutoLongPaperPosition? Position) Open(string intentId,decimal price,DateTimeOffset filledAt)
    {
        lock(_gate)
        {
            var rows=Read();if(rows.TryGetValue(intentId,out var existing))return(false,"ALREADY_OPENED",existing);
            var intent=ledger.List().FirstOrDefault(x=>x.Id==intentId);
            if(intent is null||intent.Status!="RESERVED"||price<=0)return(false,"INVALID_OR_UNRESERVED_INTENT",null);
            var amount=intent.Quantity*price;
            if(amount>intent.ReservedBuyTwd)return(false,"FILL_EXCEEDS_RESERVED_BUDGET",null);
            if(!ledger.ReconcileFill(intentId,amount))return(false,"FILL_RECONCILIATION_FAILED",null);
            var row=new AutoLongPaperPosition(intentId,intent.Symbol,intent.Quantity,price,price,price,"OPEN",null,filledAt,null);
            rows[intentId]=row;Write(rows);return(true,"PAPER_OPENED",row);
        }
    }
    public (bool Accepted,string Reason,AutoLongPaperPosition? Position) Observe(string intentId,decimal open,decimal high,decimal low,decimal close,DateTimeOffset at)
    {
        lock(_gate)
        {
            var rows=Read();if(!rows.TryGetValue(intentId,out var row))return(false,"POSITION_NOT_FOUND",null);
            if(row.Status!="OPEN")return(false,"POSITION_ALREADY_CLOSED",row);
            if(at<=row.EnteredAt||open<=0||low<=0||high<low||close<=0||open>high||open<low||close>high||close<low)return(false,"INVALID_OR_PRE_ENTRY_BAR",row);
            // Prior high-water first: do not use an intrabar future high to stop on an earlier low.
            var stop=row.EntryPrice*(1-row.StopPercent/100m);
            var trail=row.HighWater*(1-row.TrailPercent/100m);
            var threshold=Math.Max(stop,trail);
            decimal? exit=null;string reason="";
            if(open<=threshold){exit=open;reason="OPEN_BELOW_STOP";}
            else if(low<=threshold){exit=threshold;reason="PRIOR_WATERMARK_STOP";}
            if(exit is not null)
                row=row with{Status="CLOSED",LastPrice=exit.Value,ExitPrice=exit.Value,ExitedAt=at,ExitReason=reason};
            else row=row with{HighWater=Math.Max(row.HighWater,high),LastPrice=close};
            rows[intentId]=row;Write(rows);return(true,reason.Length>0?reason:"HOLDING",row);
        }
    }
}
