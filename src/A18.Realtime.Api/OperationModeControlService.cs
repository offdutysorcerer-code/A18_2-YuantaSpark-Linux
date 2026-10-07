using System.Text.Json;

namespace A18.Realtime.Api;

public sealed record OperationModeControlRequest(string RequestId, string TargetMode, DateTimeOffset RequestedAt);
public sealed record OperationModeControlStatus(
    string State,
    string? RequestId,
    string? TargetMode,
    string? ActiveMode,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Message);

public sealed class OperationModeControlService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly string _requestPath;
    private readonly string _statusPath;
    private readonly object _gate = new();

    public OperationModeControlService(IConfiguration config)
    {
        _root = config["A18:OperationModeControlDirectory"]?.Trim() ?? "/data/operation-mode-control";
        _requestPath = Path.Combine(_root, "request.json");
        _statusPath = Path.Combine(_root, "status.json");
        Directory.CreateDirectory(_root);
    }

    public OperationModeControlStatus Read(A18OperationModeState active)
    {
        lock (_gate)
        {
            OperationModeControlStatus? status = null;
            try
            {
                if (File.Exists(_statusPath))
                    status = JsonSerializer.Deserialize<OperationModeControlStatus>(File.ReadAllText(_statusPath), JsonOptions);
            }
            catch { }
            return status is null
                ? new("Idle", null, null, active.Mode, null, null, null, "尚未進行模式切換。")
                : status with { ActiveMode = active.Mode };
        }
    }

    public OperationModeControlStatus Enqueue(string target, A18OperationModeState active)
    {
        target = target.Trim();
        string normalized = target.Equals("legacy", StringComparison.OrdinalIgnoreCase) ||
                            target.Equals(A18OperationModeState.Legacy, StringComparison.OrdinalIgnoreCase)
            ? A18OperationModeState.Legacy
            : target.Equals("fullmarket", StringComparison.OrdinalIgnoreCase) ||
              target.Equals(A18OperationModeState.FullMarketExperimental, StringComparison.OrdinalIgnoreCase)
                ? A18OperationModeState.FullMarketExperimental
                : throw new ArgumentException("target mode must be Legacy or FullMarketExperimental.", nameof(target));

        lock (_gate)
        {
            var existing = Read(active);
            if (existing.State is "Pending" or "Running")
                throw new InvalidOperationException($"operation-mode switch is already {existing.State.ToLowerInvariant()} (request {existing.RequestId}).");
            if (active.Mode.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return new("Completed", null, normalized, active.Mode, DateTimeOffset.Now, null, DateTimeOffset.Now, "目前已是指定模式，不需切換。");

            var now = DateTimeOffset.Now;
            var id = Guid.NewGuid().ToString("N");
            var request = new OperationModeControlRequest(id, normalized, now);
            var status = new OperationModeControlStatus("Pending", id, normalized, active.Mode, now, null, null, "已排入 Ubuntu host 切換佇列。");
            WriteAtomic(_statusPath, status);
            WriteAtomic(_requestPath, request);
            return status;
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tmp, path, true);
    }
}
