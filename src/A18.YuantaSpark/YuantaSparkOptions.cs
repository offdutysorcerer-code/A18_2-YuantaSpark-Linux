namespace A18.YuantaSpark;

public sealed class YuantaSparkOptions
{
    public string Environment { get; set; } = "PROD";
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
    public int LoginDelayMilliseconds { get; set; } = 10000;
    public int LoginTimeoutSeconds { get; set; } = 30;
    public string LogDirectory { get; set; } = "/logs/yuanta";
    public int ReadyFreshnessSeconds { get; set; } = 300;
    public int SessionRebuildHour { get; set; } = 8;
    public int SessionRebuildMinute { get; set; } = 45;
}
