using AlRahma.Application.Backups;

namespace AlRahma.Api.Infrastructure;

/// <summary>
/// منفذ النسخ الاحتياطي كل دقيقة (BackupRunner): يعلّم العالق فاشلاً، ويطلب النسخة اليومية، وينفذ الطلبات في الوضع native.
/// يعمل مع Jobs:Enabled مثل ScheduledJobsWorker، ولا يفعل شيئاً ما لم يكن Backup:Enabled=true.
/// </summary>
public class BackupWorker(IServiceScopeFactory scopes, ILogger<BackupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScheduledJobsWorker.Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BackupRunner>().TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogError(e, "Backup worker failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
