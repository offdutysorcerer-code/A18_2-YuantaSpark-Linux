using System.Text.Json;

namespace A18.Realtime.Api;

internal sealed record SwingAnnotation(
    Guid Id,
    string Symbol,
    DateOnly TradeDate,
    DateTimeOffset BarTime,
    decimal Price,
    string Label,
    string Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record SwingAnnotationInput(
    string Symbol,
    DateOnly TradeDate,
    DateTimeOffset BarTime,
    decimal Price,
    string Label,
    string? Note);

internal sealed class SwingAnnotations(IConfiguration config)
{
    private static readonly HashSet<string> AllowedLabels =
    [
        "A進場好點",
        "A進場不要",
        "觀察但沒進"
    ];

    private readonly string _path = config["Swing:AnnotationsPath"] ?? "/data/reference/swing-annotations.json";
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public IReadOnlyList<SwingAnnotation> Read(string? symbol = null, DateOnly? tradeDate = null)
    {
        lock (_gate)
        {
            IEnumerable<SwingAnnotation> rows = ReadUnsafe();
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                var normalized = NormalizeSymbol(symbol);
                rows = rows.Where(x => x.Symbol.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            }

            if (tradeDate.HasValue) rows = rows.Where(x => x.TradeDate == tradeDate.Value);
            return rows.OrderBy(x => x.BarTime).ThenBy(x => x.CreatedAt).ToArray();
        }
    }

    public SwingAnnotation Create(SwingAnnotationInput input)
    {
        lock (_gate)
        {
            var rows = ReadUnsafe();
            var now = DateTimeOffset.UtcNow;
            var clean = Clean(input, Guid.NewGuid(), now, now);
            rows.Add(clean);
            WriteUnsafe(rows);
            return clean;
        }
    }

    public SwingAnnotation Update(Guid id, SwingAnnotationInput input)
    {
        lock (_gate)
        {
            var rows = ReadUnsafe();
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) throw new KeyNotFoundException("annotation not found");
            var existing = rows[index];
            var clean = Clean(input, id, existing.CreatedAt, DateTimeOffset.UtcNow);
            rows[index] = clean;
            WriteUnsafe(rows);
            return clean;
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var rows = ReadUnsafe();
            var removed = rows.RemoveAll(x => x.Id == id) > 0;
            if (removed) WriteUnsafe(rows);
            return removed;
        }
    }

    private SwingAnnotation Clean(SwingAnnotationInput input, Guid id, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        var symbol = NormalizeSymbol(input.Symbol);
        var label = (input.Label ?? "").Trim();
        if (!AllowedLabels.Contains(label)) throw new ArgumentException("label must be A進場好點, A進場不要, or 觀察但沒進");
        if (input.BarTime == default) throw new ArgumentException("barTime is required");
        if (input.BarTime.ToOffset(TimeSpan.FromHours(8)).Date != input.TradeDate.ToDateTime(TimeOnly.MinValue).Date)
            throw new ArgumentException("barTime must belong to tradeDate in Asia/Taipei");

        return new SwingAnnotation(
            id,
            symbol,
            input.TradeDate,
            input.BarTime,
            input.Price,
            label,
            (input.Note ?? "").Trim(),
            createdAt,
            updatedAt);
    }

    private static string NormalizeSymbol(string? symbol)
    {
        var normalized = (symbol ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0) throw new ArgumentException("symbol is required");
        return normalized;
    }

    private List<SwingAnnotation> ReadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SwingAnnotation>>(
                File.ReadAllText(_path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void WriteUnsafe(List<SwingAnnotation> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(rows, _json));
        File.Move(tempPath, _path, true);
    }
}
