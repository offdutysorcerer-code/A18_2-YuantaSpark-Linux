using System.Text.Json;
namespace A18.Realtime.Api;
internal sealed class SwingGroups(IConfiguration config)
{
    private readonly string _path = config["Swing:GroupsPath"] ?? "/data/reference/swing-groups.json";
    private readonly object _gate = new();
    public const string MoversGroupName="最新交易日漲幅排行（A18_1）";
    private string[] LatestMovers()
    {
        var source= config["Swing:DailyMoversUrl"] ?? "http://host.docker.internal:5212/daily-movers/data.json";
        try
        {
            if(!Uri.TryCreate(source,UriKind.Absolute,out var uri)||uri.Scheme!="http"||uri.Host!="host.docker.internal"||uri.Port!=5212)return [];
            using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(3)};
            using var response=http.GetAsync(uri).GetAwaiter().GetResult();
            if(!response.IsSuccessStatusCode)return [];
            using var doc=JsonDocument.Parse(response.Content.ReadAsStream());
            var rows=doc.RootElement.GetProperty("rows");
            return rows.EnumerateArray().Select(x=>x.GetProperty("symbol").GetString()??"")
                .Where(x=>x.Length is >= 4 and <= 8 && x.All(char.IsLetterOrDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(300).ToArray();
        }
        catch{return [];}
    }
    private Dictionary<string,string[]> AddMovers(Dictionary<string,string[]> groups)
    {
        var dynamic=LatestMovers();
        if(dynamic.Length>0)groups[MoversGroupName]=dynamic;
        return groups;
    }

    public Dictionary<string,string[]> Read(){lock(_gate){if(!File.Exists(_path)) return AddMovers(new(StringComparer.OrdinalIgnoreCase));try{return AddMovers(JsonSerializer.Deserialize<Dictionary<string,string[]>>(File.ReadAllText(_path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new(StringComparer.OrdinalIgnoreCase));}catch{return AddMovers(new(StringComparer.OrdinalIgnoreCase));}}}
    public Dictionary<string,string[]> Save(Dictionary<string,string[]> groups){lock(_gate){var clean=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);foreach(var (name,members) in groups){var n=(name??"").Trim();if(n.Length==0||n==MoversGroupName)continue;clean[n]=(members??[]).Select(x=>x.Trim().ToUpperInvariant()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}Directory.CreateDirectory(Path.GetDirectoryName(_path)!);var tmp=_path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(clean,new JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,_path,true);return AddMovers(clean);}}
}
