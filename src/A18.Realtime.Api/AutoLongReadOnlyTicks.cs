using System.Text.Json;
using A18.Realtime.Core;
namespace A18.Realtime.Api;
/// <summary>Staging-only, read-only tick snapshot from the production quote API. Never calls Runtime21.</summary>
internal sealed class AutoLongReadOnlyTicks(IConfiguration config, IHttpClientFactory clients)
{
    private static readonly TimeZoneInfo Taipei=TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    private readonly Dictionary<string,(DateTimeOffset Fetched,NormalizedTick[] Ticks)> _cache=new();
    private readonly object _gate=new();
    public bool Configured=>config.GetValue<bool>("A18:StagingSafeMode") && Uri.TryCreate(config["AutoLong:ReadOnlyFeedUrl"],UriKind.Absolute,out var u) && u.Scheme=="http" && u.Host=="host.docker.internal" && u.Port==5220;
    public IReadOnlyList<NormalizedTick> GetTicks(string symbol,DateOnly date,DateTimeOffset now)
    {
        if(!Configured||symbol.Length!=4||!symbol.All(char.IsAsciiDigit)||date!=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now,Taipei).Date))return [];
        lock(_gate)if(_cache.TryGetValue(symbol,out var c)&&now-c.Fetched<TimeSpan.FromSeconds(1.5))return c.Ticks;
        try
        {
            using var client=clients.CreateClient();client.Timeout=TimeSpan.FromSeconds(3);
            var root=config["AutoLong:ReadOnlyFeedUrl"]!.TrimEnd('/');
            using var response=client.GetAsync(root+"/api/ticks/"+symbol+"?date="+date.ToString("yyyy-MM-dd")).GetAwaiter().GetResult();
            if(!response.IsSuccessStatusCode)return [];
            using var document=JsonDocument.Parse(response.Content.ReadAsStream());
            var json=document.RootElement;
            if(!json.TryGetProperty("ticks",out var rows)||rows.ValueKind!=JsonValueKind.Array)return [];
            // Only same-session exchange ticks, never future or stale by more than 4 min.
            var ticks=rows.Deserialize<NormalizedTick[]>(new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??[];
            ticks=ticks.Where(t=>t.Symbol==symbol&&t.Price>0&&t.ExchangeAt<=now&&now-t.ExchangeAt<=TimeSpan.FromMinutes(4)&&DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t.ExchangeAt,Taipei).Date)==date).OrderBy(t=>t.ExchangeAt).ThenBy(t=>t.Sequence).ToArray();
            lock(_gate)_cache[symbol]=(now,ticks);
            return ticks;
        }
        catch{return [];}
    }
    public bool HasFreshTicks(string symbol,DateTimeOffset now)=>GetTicks(symbol,DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now,Taipei).Date),now).Any(t=>now-t.ExchangeAt<TimeSpan.FromSeconds(10));
}
