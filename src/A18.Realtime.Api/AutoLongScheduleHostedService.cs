namespace A18.Realtime.Api;
/// <summary>Staging-only schedule-status maintenance. NEVER places orders.</summary>
internal sealed class AutoLongScheduleHostedService(IServiceProvider provider,IConfiguration configuration,ILogger<AutoLongScheduleHostedService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if(!configuration.GetValue<bool>("A18:StagingSafeMode"))return;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try {provider.GetRequiredService<AutoLongWatch>().RefreshStatuses();}
            catch(Exception error){logger.LogError(error,"AutoLong schedule status refresh failed. No orders are sent.");}
        }while(await timer.WaitForNextTickAsync(cancellationToken));
    }
}
