using AlRahma.Application.Abstractions;
using AlRahma.Application.Backups;
using AlRahma.Application.Common;
using AlRahma.Infrastructure.Backups;
using AlRahma.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>
/// النسخ الأصلي على Windows (Backup:Mode=native): انتقالات الحالة، مهلة 10 دقائق، الجدولة اليومية واللحاق،
/// صلاحيات وقت النسخ، والاحتفاظ بآخر 14 نسخة. المنفذ الحقيقي (pg_dump) يُختبر إن وُجد PostgreSQL محلياً.
/// </summary>
[Collection(ApiCollection.Name)]
public class NativeBackupTests(TestApp app)
{
    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FakeExecutor : IBackupExecutor
    {
        public Func<BackupJob, Task<BackupResult>> Run { get; set; } =
            j => Task.FromResult(new BackupResult(true, null, $@"X:\b\{j.RunId:N}", $"label_{j.RunId:N}", 1000, 500, 1600));
        public OffsiteResult Offsite { get; set; } = new("off", null, null);
        public List<string> Deleted { get; } = [];
        public int Runs { get; private set; }
        public Task<BackupResult> RunAsync(BackupJob job, CancellationToken ct) { Runs++; return Run(job); }
        public Task<OffsiteResult> CopyOffsiteAsync(string localPath, CancellationToken ct) => Task.FromResult(Offsite);
        public void DeleteBackupFolder(string path) => Deleted.Add(path);
    }

    private static BackupOptions Native(string? offsite = null) =>
        new() { Enabled = true, Mode = "native", Path = @"X:\b", Keep = 14, TimeoutMinutes = 10, OffsitePath = offsite };

    private BackupRunner Runner(IServiceScope scope, IBackupExecutor exec, IClock clock, BackupOptions opt)
    {
        var sp = scope.ServiceProvider;
        return new BackupRunner(sp.GetRequiredService<IAppDbContext>(), clock, sp.GetRequiredService<IAuditLogger>(), exec, Options.Create(opt));
    }

    private async Task ResetAsync()
    {
        await app.AdminTokenAsync();
        await app.ExecAsync("DELETE FROM backup_runs; DELETE FROM system_settings WHERE key = 'backup.daily_time'");
    }

    [SkippableFact]
    public async Task Executor_moves_requested_to_running_then_succeeded_or_failed()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        await ResetAsync();
        var id = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO backup_runs (run_id, type, trigger, status, created_at) VALUES ('{id}', 'full', 'manual', 'requested', now())");
        var exec = new FakeExecutor { Offsite = new OffsiteResult("pending", null, "بانتظار القرص الخارجي") };
        string? statusDuringRun = null;
        var ok = exec.Run;
        exec.Run = async j => { statusDuringRun = await app.ScalarAsync<string>($"SELECT status FROM backup_runs WHERE run_id = '{j.RunId}'"); return await ok(j); };

        using (var scope = app.Server.Services.CreateScope())
            Assert.Equal(1, await Runner(scope, exec, new FakeClock(DateTimeOffset.UtcNow), Native()).TickAsync(CancellationToken.None));
        Assert.Equal("running", statusDuringRun);
        Assert.Equal("succeeded", await app.ScalarAsync<string>($"SELECT status FROM backup_runs WHERE run_id = '{id}'"));
        Assert.Equal($@"X:\b\{id:N}", await app.ScalarAsync<string>($"SELECT path FROM backup_runs WHERE run_id = '{id}'"));
        Assert.Equal(1600L, await app.ScalarAsync<long>($"SELECT stored_bytes FROM backup_runs WHERE run_id = '{id}'"));
        Assert.Equal(500L, await app.ScalarAsync<long>($"SELECT files_bytes FROM backup_runs WHERE run_id = '{id}'"));
        Assert.Equal("pending", await app.ScalarAsync<string>($"SELECT offsite_status FROM backup_runs WHERE run_id = '{id}'"));
        Assert.True(await app.ScalarAsync<bool>($"SELECT started_at IS NOT NULL AND finished_at IS NOT NULL FROM backup_runs WHERE run_id = '{id}'"));
        // التنفيذ التلقائي يُدقق باسم النظام (بلا مستخدم)
        Assert.True(await app.ScalarAsync<bool>($"SELECT user_id IS NULL FROM audit_logs WHERE action = 'BACKUP_SUCCEEDED' AND entity_id = '{id}'"));

        // القرص الخارجي وُصل: تُنسخ النسخة المعلّقة في الدورة التالية
        exec.Offsite = new OffsiteResult("copied", @"E:\off\x", null);
        using (var scope = app.Server.Services.CreateScope())
            await Runner(scope, exec, new FakeClock(DateTimeOffset.UtcNow), Native(@"E:\off")).TickAsync(CancellationToken.None);
        Assert.Equal("copied", await app.ScalarAsync<string>($"SELECT offsite_status FROM backup_runs WHERE run_id = '{id}'"));
        Assert.Equal("local,offsite", await app.ScalarAsync<string>($"SELECT repos FROM backup_runs WHERE run_id = '{id}'"));

        var bad = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO backup_runs (run_id, type, trigger, status, created_at) VALUES ('{bad}', 'full', 'manual', 'requested', now())");
        exec.Run = _ => Task.FromResult(new BackupResult(false, "فشل pg_dump: disk full", null, null, null, null, null));
        using (var scope = app.Server.Services.CreateScope())
            await Runner(scope, exec, new FakeClock(DateTimeOffset.UtcNow), Native()).TickAsync(CancellationToken.None);
        Assert.Equal("failed", await app.ScalarAsync<string>($"SELECT status FROM backup_runs WHERE run_id = '{bad}'"));
        Assert.Contains("disk full", await app.ScalarAsync<string>($"SELECT error FROM backup_runs WHERE run_id = '{bad}'"));
    }

    [SkippableFact]
    public async Task Stuck_request_or_run_over_ten_minutes_becomes_failed()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        await ResetAsync();
        var oldReq = Guid.NewGuid();
        var oldRun = Guid.NewGuid();
        var freshReq = Guid.NewGuid();
        await app.ExecAsync($"""
            INSERT INTO backup_runs (run_id, type, trigger, status, created_at) VALUES ('{oldReq}', 'full', 'manual', 'requested', now() - interval '11 minutes');
            INSERT INTO backup_runs (run_id, type, trigger, status, started_at, created_at) VALUES ('{oldRun}', 'full', 'schedule', 'running', now() - interval '11 minutes', now() - interval '12 minutes');
            INSERT INTO backup_runs (run_id, type, trigger, status, created_at) VALUES ('{freshReq}', 'full', 'manual', 'requested', now() - interval '5 minutes');
            """);
        using var scope = app.Server.Services.CreateScope();
        // يعمل في كل الأوضاع (حتى script) ما دام النسخ مفعّلاً
        var runner = Runner(scope, new FakeExecutor(), new FakeClock(DateTimeOffset.UtcNow), new BackupOptions { Enabled = true, TimeoutMinutes = 10 });
        await runner.TickAsync(CancellationToken.None);
        Assert.Equal("failed", await app.ScalarAsync<string>($"SELECT status FROM backup_runs WHERE run_id = '{oldReq}'"));
        Assert.Equal("failed", await app.ScalarAsync<string>($"SELECT status FROM backup_runs WHERE run_id = '{oldRun}'"));
        Assert.Equal("requested", await app.ScalarAsync<string>($"SELECT status FROM backup_runs WHERE run_id = '{freshReq}'"));
        Assert.Contains("10", await app.ScalarAsync<string>($"SELECT error FROM backup_runs WHERE run_id = '{oldReq}'"));

        // المنفذ الذي يتجاوز المهلة يُلغى ويُعلَّم فاشلاً
        await app.ExecAsync("DELETE FROM backup_runs");
        var slow = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO backup_runs (run_id, type, trigger, status, created_at) VALUES ('{slow}', 'full', 'manual', 'requested', now())");
        var exec = new FakeExecutor { Run = async _ => { await Task.Delay(Timeout.Infinite, new CancellationTokenSource(TimeSpan.FromMilliseconds(1)).Token); return null!; } };
        await Runner(scope, exec, new FakeClock(DateTimeOffset.UtcNow), Native()).TickAsync(CancellationToken.None);
        Assert.Equal("failed", await app.ScalarAsync<string>($"SELECT status FROM backup_runs WHERE run_id = '{slow}'"));
    }

    [SkippableFact]
    public async Task Daily_schedule_runs_once_at_saved_time_and_catches_up_after_downtime()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        await ResetAsync();
        // 03:00 بغداد = 00:00 UTC
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T00:00:00Z"), BackupRunner.LastOccurrence(DateTimeOffset.Parse("2026-11-06T09:00:00Z"), new TimeOnly(3, 0)));
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T00:00:00Z"), BackupRunner.LastOccurrence(DateTimeOffset.Parse("2026-11-05T23:59:00Z"), new TimeOnly(3, 0)));
        await app.ExecAsync("INSERT INTO system_settings (branch_id, key, value, updated_at) VALUES (NULL, 'backup.daily_time', '\"03:00\"', '2026-01-01T00:00:00Z')");

        var clock = new FakeClock(DateTimeOffset.Parse("2026-11-06T07:00:00Z")); // 10:00 بغداد: الجهاز كان مطفأً في 03:00
        var exec = new FakeExecutor();
        using var scope = app.Server.Services.CreateScope();
        var runner = Runner(scope, exec, clock, Native());
        Assert.NotNull(await runner.EnsureScheduledAsync(CancellationToken.None));       // لحاق
        Assert.Null(await runner.EnsureScheduledAsync(CancellationToken.None));          // مرة واحدة فقط
        await runner.ExecuteNextAsync(CancellationToken.None);
        Assert.Equal("schedule", await app.ScalarAsync<string>("SELECT trigger FROM backup_runs LIMIT 1"));
        Assert.True(await app.ScalarAsync<bool>("SELECT user_id IS NULL FROM audit_logs WHERE action = 'BACKUP_REQUESTED' ORDER BY occurred_at DESC LIMIT 1"));

        clock.UtcNow = DateTimeOffset.Parse("2026-11-06T23:59:00Z");                       // 02:59 بغداد في اليوم التالي
        Assert.Null(await runner.EnsureScheduledAsync(CancellationToken.None));
        clock.UtcNow = DateTimeOffset.Parse("2026-11-07T00:00:30Z");                       // 03:00 بغداد
        Assert.NotNull(await runner.EnsureScheduledAsync(CancellationToken.None));
        // لا طلب جديد أثناء وجود طلب قائم
        clock.UtcNow = DateTimeOffset.Parse("2026-11-08T00:00:30Z");
        Assert.Null(await runner.EnsureScheduledAsync(CancellationToken.None));

        // تغيير الوقت إلى وقت مضى اليوم لا يطلق نسخة فورية، والوقت الجديد يُطبق من الموعد التالي بلا إعادة تشغيل
        await app.ExecAsync("DELETE FROM backup_runs");
        clock.UtcNow = DateTimeOffset.Parse("2026-11-10T07:00:00Z");                       // 10:00 بغداد
        await app.ExecAsync("UPDATE system_settings SET value = '\"09:00\"', updated_at = '2026-11-10T07:00:00Z' WHERE key = 'backup.daily_time'");
        Assert.Null(await runner.EnsureScheduledAsync(CancellationToken.None));
        clock.UtcNow = DateTimeOffset.Parse("2026-11-11T06:00:10Z");                       // 09:00 بغداد غداً
        Assert.NotNull(await runner.EnsureScheduledAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task Only_admin_sets_daily_time_and_runs_backup_branch_manager_sees_status()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        await ResetAsync();
        using var factory = app.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Backup:Enabled", "true");
            b.UseSetting("Backup:Mode", "native");
            b.UseSetting("Backup:Path", Path.Combine(Path.GetTempPath(), "alrahma-backup-test"));
        });
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", await app.AdminTokenAsync());
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager");
        var manager = factory.CreateClient();
        manager.DefaultRequestHeaders.Authorization = new("Bearer", managerToken);

        var status = await Api.GetAsync(admin, "/api/v1/admin/backups/status");
        Assert.True(status.Status == 200, status.Raw);
        Assert.Equal("03:00", status.Body!["native"]!["daily_time"]!.GetValue<string>());
        Assert.DoesNotContain(status.Body!["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("pgBackRest"));

        Assert.Equal(403, (await Api.SendAsync(manager, HttpMethod.Put, "/api/v1/admin/backups/settings", new { daily_time = "04:00" })).Status);
        Assert.Equal(403, (await Api.PostAsync(manager, "/api/v1/admin/backups/run", new { })).Status);
        Assert.Equal(200, (await Api.GetAsync(manager, "/api/v1/branch/backup-status")).Status);
        Assert.Equal(400, (await Api.SendAsync(admin, HttpMethod.Put, "/api/v1/admin/backups/settings", new { daily_time = "25:99" })).Status);

        var saved = await Api.SendAsync(admin, HttpMethod.Put, "/api/v1/admin/backups/settings", new { daily_time = "04:30" });
        Assert.True(saved.Status == 200, saved.Raw);
        Assert.Equal("04:30", (await Api.GetAsync(admin, "/api/v1/admin/backups/status")).Body!["native"]!["daily_time"]!.GetValue<string>());
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE action = 'BACKUP_SETTINGS_UPDATED' AND user_id IS NOT NULL"));

        // التشغيل اليدوي: نسخة كاملة دائماً، باسم المدير، ولا طلب ثانٍ أثناء الأول
        var run = await Api.PostAsync(admin, "/api/v1/admin/backups/run", new { type = "incr" });
        Assert.True(run.Status == 202, run.Raw);
        Assert.Equal("full", run.Body!["type"]!.GetValue<string>());
        Assert.Equal("BACKUP_IN_PROGRESS", (await Api.PostAsync(admin, "/api/v1/admin/backups/run", new { })).Code);
        Assert.True(await app.ScalarAsync<bool>($"SELECT user_id IS NOT NULL FROM audit_logs WHERE action = 'BACKUP_REQUESTED' AND entity_id = '{run.Body!["run_id"]}'"));
        await app.ExecAsync("UPDATE backup_runs SET status = 'failed' WHERE status = 'requested'");
    }

    [SkippableFact]
    public async Task Retention_keeps_last_fourteen_successful_backups()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        await ResetAsync();
        for (var i = 1; i <= 16; i++)
            await app.ExecAsync($"""
                INSERT INTO backup_runs (run_id, type, trigger, status, path, offsite_path, finished_at, created_at)
                VALUES ('{Guid.NewGuid()}', 'full', 'schedule', 'succeeded', 'X:\b\n{i:00}', {(i == 1 ? "'E:\\off\\n01'" : "NULL")},
                        now() - interval '{20 - i} days', now() - interval '{20 - i} days')
                """);
        await app.ExecAsync($"INSERT INTO backup_runs (run_id, type, status, created_at) VALUES ('{Guid.NewGuid()}', 'full', 'failed', now() - interval '30 days')");
        var exec = new FakeExecutor();
        using var scope = app.Server.Services.CreateScope();
        Assert.Equal(2, await Runner(scope, exec, new FakeClock(DateTimeOffset.UtcNow), Native()).ApplyRetentionAsync(CancellationToken.None));
        Assert.Equal([@"X:\b\n02", @"X:\b\n01", @"E:\off\n01"], exec.Deleted);
        Assert.Equal(14L, await app.ScalarAsync<long>("SELECT count(*) FROM backup_runs WHERE status = 'succeeded' AND deleted_at IS NULL"));
        Assert.Equal(17L, await app.ScalarAsync<long>("SELECT count(*) FROM backup_runs")); // السجل يبقى للتاريخ
        Assert.Equal(0, await Runner(scope, exec, new FakeClock(DateTimeOffset.UtcNow), Native()).ApplyRetentionAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task Real_executor_writes_dump_files_and_key_and_verifies_with_pg_restore()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var pgBin = new[] { 17, 16, 15, 14, 13 }.Select(v => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", v.ToString(), "bin"))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump")))
            ?? Environment.GetEnvironmentVariable("PGBIN");
        Skip.If(pgBin is null, "pg_dump not found");
        await app.AdminTokenAsync();
        var root = Path.Combine(Path.GetTempPath(), $"alrahma-native-{Guid.NewGuid():N}");
        var files = Path.Combine(root, "s3data");
        Directory.CreateDirectory(Path.Combine(files, "vol"));
        await File.WriteAllTextAsync(Path.Combine(files, "vol", "1.dat"), "file-bytes");
        var key = Path.Combine(root, "drawer-es256.pem");
        await File.WriteAllTextAsync(key, "-----BEGIN TEST KEY-----");
        try
        {
            BackupOptions Opt(string? offsite = null) => new()
            {
                Enabled = true, Mode = "native", Path = Path.Combine(root, "out"), PgBin = pgBin, FilesPath = files, KeyFile = key, OffsitePath = offsite,
            };
            var opt = Opt();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = app.ConnectionString }).Build();
            var exec = new NativeBackupExecutor(Options.Create(opt), Options.Create(new DrawerOptions()), config, NullLogger<NativeBackupExecutor>.Instance);
            var res = await exec.RunAsync(new BackupJob(Guid.NewGuid(), "manual", DateTimeOffset.UtcNow), CancellationToken.None);
            Assert.True(res.Ok, res.Error);
            Assert.True(File.Exists(Path.Combine(res.Path!, "db.dump")));
            Assert.Equal("file-bytes", await File.ReadAllTextAsync(Path.Combine(res.Path!, "files", "vol", "1.dat")));
            Assert.True(File.Exists(Path.Combine(res.Path!, "keys", "drawer-es256.pem")));
            Assert.Contains("table_data_entries", await File.ReadAllTextAsync(Path.Combine(res.Path!, NativeBackupExecutor.Manifest)));
            Assert.True(res.DumpBytes > 0 && res.TotalBytes > res.DumpBytes);

            // القرص الخارجي غير متصل: pending بلا فشل
            var off = new NativeBackupExecutor(Options.Create(Opt(OperatingSystem.IsWindows() ? @"Q:\nope\AlRahma" : "/nonexistent-root/x")),
                Options.Create(new DrawerOptions()), config, NullLogger<NativeBackupExecutor>.Instance);
            Assert.Equal("pending", (await off.CopyOffsiteAsync(res.Path!, CancellationToken.None)).Status);
            var reachable = new NativeBackupExecutor(Options.Create(Opt(Path.Combine(root, "offsite"))),
                Options.Create(new DrawerOptions()), config, NullLogger<NativeBackupExecutor>.Instance);
            var copied = await reachable.CopyOffsiteAsync(res.Path!, CancellationToken.None);
            Assert.Equal("copied", copied.Status);
            Assert.True(File.Exists(Path.Combine(copied.Path!, "db.dump")));
            reachable.DeleteBackupFolder(copied.Path!);
            Assert.False(Directory.Exists(copied.Path!));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* مؤقت */ }
        }
    }
}
