namespace A18.Realtime.Api;

internal sealed class TaiwanSymbolDirectory
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _markets = new(StringComparer.OrdinalIgnoreCase);

    public TaiwanSymbolDirectory(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var cols = ParseCsv(line);
            if (cols.Count < 2) continue;
            var symbol = cols[0].Trim();
            var name = cols[1].Trim();
            if (symbol.Length == 0 || name.Length == 0) continue;
            _names[symbol] = name;
            if (cols.Count > 3) _markets[symbol] = cols[3].Trim();
        }
    }

    public string? GetName(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        return _names.TryGetValue(symbol, out var name) ? name : null;
    }

    public IReadOnlyDictionary<string,string> AllNames() => _names;

    public string GetMarket(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (symbol == "IX0001") return "TWSE";
        if (!_markets.TryGetValue(symbol, out var market)) return "TWSE";
        return market.Equals("TPEx", StringComparison.OrdinalIgnoreCase) ? "TPEX" : market.ToUpperInvariant();
    }

    private static List<string> ParseCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }
}
