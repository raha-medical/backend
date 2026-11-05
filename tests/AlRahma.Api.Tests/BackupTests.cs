using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Application.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>النسخ الاحتياطي: نقاط المدير العام، طلب التشغيل اليدوي، ومهمة مراقبة الفشل والتأخر.</summary>
[Collection(ApiCollection.Name)]
public class BackupTests(TestApp app)
{
    [SkippableFact]
    public async Task Admin_requests_manual_backup_once_and_others_are_forbidden()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager");
        Assert.Equal(403, (await Api.GetAsync(app.Client(managerToken), "/api/v1/admin/backups/status")).Status);
        Assert.Equal(403, (await Api.PostAsync(app.Client(managerToken), "/api/v1/admin/backups/run", new { type = "full" })).Status);

        var status = await Api.GetAsync(admin, "/api/v1/admin/backups/status");
        Assert.True(status.Status == 200, status.Raw);
        Assert.False(status.Body!["enabled"]!.GetValue<bool>());
        Assert.Equal("off", status.Body!["files"]!["mode"]!.GetValue<string>());
        Assert.Equal("Asia/Baghdad", status.Body!["schedule"]!["timezone"]!.GetValue<string>());

        Assert.Equal(400, (await Api.PostAsync(admin, "/api/v1/admin/backups/run", new { type = "weekly" })).Status);
        Assert.Equal("BACKUP_FILES_DISABLED", (await Api.PostAsync(admin, "/api/v1/admin/backups/run", new { type = "files" })).Code);
        var run = await Api.PostAsync(admin, "/api/v1/admin/backups/run", new { type = "full" });
        Assert.True(run.Status == 202, run.Raw);
        Assert.Equal("requested", run.Body!["status"]!.GetValue<string>());
        Assert.Equal("manual", run.Body!["trigger"]!.GetValue<string>());
        var again = await Api.PostAsync(admin, "/api/v1/admin/backups/run", new { });
        Assert.Equal(409, again.Status);
        Assert.Equal("BACKUP_IN_PROGRESS", again.Code);

        var list = await Api.GetAsync(admin, "/api/v1/admin/backups?status=requested");
        Assert.Contains(list.Body!["items"]!.AsArray(), r => r!["run_id"]!.GetValue<Guid>() == run.Body!["run_id"]!.GetValue<Guid>());
        Assert.NotNull((await Api.GetAsync(admin, "/api/v1/admin/backups/status")).Body!["active"]);

        // السكربت ينهي الطلب: يمكن طلب نسخة جديدة
        await app.ExecAsync($"UPDATE backup_runs SET status = 'succeeded', started_at = now() - interval '2 minutes', finished_at = now(), backup_label = 'x' WHERE run_id = '{run.Body!["run_id"]}'");
        var fresh = await Api.GetAsync(admin, "/api/v1/admin/backups/status");
        Assert.NotNull(fresh.Body!["last_success"]);
        Assert.Equal(202, (await Api.PostAsync(admin, "/api/v1/admin/backups/run", new { type = "incr" })).Status);
        await app.ExecAsync("UPDATE backup_runs SET status = 'succeeded', finished_at = now() WHERE status = 'requested'");
    }

    [SkippableFact]
    public async Task Monitor_alerts_admins_on_failure_once_and_on_stale_backups()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        await app.AdminTokenAsync();
        var failed = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO backup_runs (run_id, type, status, error, created_at) VALUES ('{failed}', 'incr', 'failed', 'disk full', now())");

        using var scope = app.Server.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var jobs = new ScheduledJobs(sp.GetRequiredService<IAppDbContext>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<SettingsReader>(), sp.GetRequiredService<QueueNotifier>(),
            Options.Create(new BackupOptions { Enabled = true, MaxHoursWithoutSuccess = 26 }),
            sp.GetRequiredService<QueueEngine>(), sp.GetRequiredService<AlRahma.Application.Patients.BanService>(), sp.GetRequiredService<IPushProvider>());
        // آخر نجاح قبل 30 ساعة: تنبيه تأخر + تنبيه الفشل
        await app.ExecAsync($"INSERT INTO backup_runs (run_id, type, status, finished_at, created_at) VALUES ('{Guid.NewGuid()}', 'full', 'succeeded', now(), now() - interval '31 hours')");
        await app.ExecAsync("UPDATE backup_runs SET finished_at = now() - interval '30 hours' WHERE status = 'succeeded'");
        Assert.Equal(2, await jobs.MonitorBackupsAsync(CancellationToken.None));
        Assert.Equal(0, await jobs.MonitorBackupsAsync(CancellationToken.None));
        Assert.True(await app.ScalarAsync<bool>($"SELECT alerted_at IS NOT NULL FROM backup_runs WHERE run_id = '{failed}'"));
        Assert.Contains("disk full", await app.ScalarAsync<string>(
            $"SELECT message FROM notifications WHERE type = 'backup.failed' AND link_id = '{failed}' LIMIT 1"));
        Assert.Equal(1, await app.ScalarAsync<long>("SELECT count(DISTINCT created_at) FROM notifications WHERE type = 'backup.failed' AND link_type = 'backup_stale'"));
    }
}
