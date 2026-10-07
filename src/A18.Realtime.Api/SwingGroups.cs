using System.Text.Json;
namespace A18.Realtime.Api;
internal sealed class SwingGroups(IConfiguration config)
{
    private readonly string _path = config["Swing:GroupsPath"] ?? "/data/reference/swing-groups.json";
    private readonly object _gate = new();
    public Dictionary<string,string[]> Read(){lock(_gate){if(!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);try{return JsonSerializer.Deserialize<Dictionary<string,string[]>>(File.ReadAllText(_path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new(StringComparer.OrdinalIgnoreCase);}catch{return new(StringComparer.OrdinalIgnoreCase);}}}
    public Dictionary<string,string[]> Save(Dictionary<string,string[]> groups){lock(_gate){var clean=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);foreach(var (name,members) in groups){var n=(name??"").Trim();if(n.Length==0)continue;clean[n]=(members??[]).Select(x=>x.Trim().ToUpperInvariant()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}Directory.CreateDirectory(Path.GetDirectoryName(_path)!);var tmp=_path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(clean,new JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,_path,true);return clean;}}
}
