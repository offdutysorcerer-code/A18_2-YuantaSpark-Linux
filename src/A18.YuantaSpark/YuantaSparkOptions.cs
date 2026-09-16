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
    public string DataDirectory { get; set; } = "/data/intraday";
    public string RequiredSymbolsPath { get; set; } = "/data/required-symbols.json";
    public int BackfillTimeoutSeconds { get; set; } = 30;
    public int MaxSubscriptions { get; set; } = 300;
    public int SubscriptionSubmitDelayMilliseconds { get; set; } = 120;
    public int NoLiveResubmitSeconds { get; set; } = 20;
    public int AckVerifyDelayMilliseconds { get; set; } = 400;
    public int ReadyFreshnessSeconds { get; set; } = 300;
    public int ChannelStaleSeconds { get; set; } = 90;
    public int MarketOpenHour { get; set; } = 9;
    public int MarketOpenMinute { get; set; } = 0;
    public int MarketCloseHour { get; set; } = 13;
    public int MarketCloseMinute { get; set; } = 30;
    public int SessionRebuildHour { get; set; } = 8;
    public int SessionRebuildMinute { get; set; } = 45;
}
