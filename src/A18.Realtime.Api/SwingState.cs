using System.Text.Json;
namespace A18.Realtime.Api;
internal sealed record SwingBrowseState(string Symbol="2330", string Date="", string Interval="5", bool Overlay=false, bool MultiDay=false, int LookbackDays=1, string Group="", bool ShowSectionNumbers=true, bool AutoStrategyA=false, bool AutoStrategyB=false, decimal AutoAStopPct=1m, decimal AutoATrailPct=0.5m, decimal AutoBStopPct=0.7m, decimal AutoBTrailPct=1m);
internal sealed class SwingState(IConfiguration config)
{
    private readonly string _path=config["Swing:StatePath"]??"/data/reference/swing-state.json"; private readonly object _gate=new();
    public SwingBrowseState Read(){lock(_gate){if(!File.Exists(_path))return new();try{return JsonSerializer.Deserialize<SwingBrowseState>(File.ReadAllText(_path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new();}catch{return new();}}}
    public SwingBrowseState Save(SwingBrowseState s){lock(_gate){var clean=s with{Symbol=(s.Symbol??"2330").Trim().ToUpperInvariant(),Interval=new[]{"1","5","60","300"}.Contains(s.Interval)?s.Interval:"5",LookbackDays=Math.Clamp(s.LookbackDays,1,20),Group=(s.Group??"").Trim(),AutoAStopPct=Math.Clamp(s.AutoAStopPct,0m,50m),AutoATrailPct=Math.Clamp(s.AutoATrailPct,0m,50m),AutoBStopPct=Math.Clamp(s.AutoBStopPct,0m,50m),AutoBTrailPct=Math.Clamp(s.AutoBTrailPct,0m,50m)};Directory.CreateDirectory(Path.GetDirectoryName(_path)!);var tmp=_path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(clean,new JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,_path,true);return clean;}}
}
