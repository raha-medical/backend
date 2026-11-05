using AlRahma.Application.Notifications;

namespace AlRahma.Api.Infrastructure;

/// <summary>يشغّل المهام الدورية كل دقيقة. يُعطّل بـ Jobs:Enabled=false (الاختبارات، أو نسخ إضافية من السيرفر).</summary>
public class ScheduledJobsWorker(IServiceScopeFactory scopes, ILogger<ScheduledJobsWorker> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<ScheduledJobs>();
                await jobs.ExpireOnlinePaymentsAsync(stoppingToken);
                await jobs.ExpireReferralsAsync(stoppingToken);
                await jobs.DispatchPushAsync(stoppingToken);
                await jobs.SendBookingRemindersAsync(stoppingToken);
                await jobs.RepeatCallAlertsAsync(stoppingToken);
                await jobs.AutoNoShowAsync(stoppingToken);
                await jobs.SendDayBeforeAndSoonRemindersAsync(stoppingToken);
                await jobs.MonitorBackupsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogError(e, "Scheduled jobs failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
