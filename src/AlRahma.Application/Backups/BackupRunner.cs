using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.Backups;

/// <summary>مهمة نسخة واحدة: المجلد يُسمى من الوقت والقاعدة ويُنشأ داخل Backup:Path.</summary>
public record BackupJob(Guid RunId, string Trigger, DateTimeOffset StartedAt);

/// <summary>نتيجة المنفذ. Ok = false مع Error عربي مختصر، والمجلد الجزئي يحذفه المنفذ.</summary>
public record BackupResult(bool Ok, string? Error, string? Path, string? Label, long? DumpBytes, long? FilesBytes, long? TotalBytes);

/// <summary>نتيجة النسخ للقرص الخارجي.</summary>
public record OffsiteResult(string Status, string? Path, string? Error);

/// <summary>
/// ينفذ النسخة على الجهاز نفسه (Infrastructure/Backups/NativeBackupExecutor): pg_dump -Fc ثم pg_restore --list،
/// ونسخ مجلد بيانات التخزين ومفتاح توقيع الإيصالات، ثم النسخ للقرص الخارجي إن كان متصلاً.
/// </summary>
public interface IBackupExecutor
{
    Task<BackupResult> RunAsync(BackupJob job, CancellationToken ct);
    /// <summary>ينسخ مجلد نسخة ناجحة للقرص الخارجي. off = غير مضبوط، pending = القرص غير متصل.</summary>
    Task<OffsiteResult> CopyOffsiteAsync(string localPath, CancellationToken ct);
    /// <summary>يحذف مجلد نسخة (محلي أو خارجي) بسياسة الاحتفاظ. لا يحذف إلا مجلد نسخة فيه manifest.json.</summary>
    void DeleteBackupFolder(string path);
}

/// <summary>
/// المنفذ الأصلي (Backup:Mode=native) من العامل الدوري كل دقيقة:
/// 1) الطلب requested أو التنفيذ running العالق أكثر من Backup:TimeoutMinutes (10) يُعلَّم failed.
/// 2) النسخة اليومية في الوقت المحفوظ backup.daily_time (بغداد)، وإن كان الخادم مطفأً وقتها تؤخذ عند أول تشغيل بعده.
/// 3) أقدم طلب requested: running ثم succeeded أو failed مع الحجم والمسار.
/// 4) الاحتفاظ بآخر Backup:Keep (14) نسخة ناجحة، وإعادة محاولة النسخ للقرص الخارجي المعلّق.
/// </summary>
public class BackupRunner(IAppDbContext db, IClock clock, IAuditLogger audit, IBackupExecutor executor, IOptions<BackupOptions> options)
{
    public const string DailyTimeKey = "backup.daily_time";
    public const string SystemTrigger = "schedule";
    private BackupOptions Opt => options.Value;

    /// <summary>دورة واحدة. تعيد عدد النسخ المنفذة (للاختبارات).</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        if (!Opt.Enabled) return 0;
        await FailStuckAsync(ct);
        if (!Opt.IsNative) return 0;
        await EnsureScheduledAsync(ct);
        var done = 0;
        if (await ExecuteNextAsync(ct)) done++;
        await RetryOffsiteAsync(ct);
        return done;
    }

    /// <summary>الطلب الذي لم يبدأ أو التنفيذ الذي لم ينته خلال المهلة: failed، فيُنبَّه المدير ويمكن طلب نسخة جديدة.</summary>
    public async Task<int> FailStuckAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var limit = now.AddMinutes(-Math.Max(1, Opt.TimeoutMinutes));
        var stuck = await db.BackupRuns
            .Where(r => (r.Status == BackupStatuses.Requested && r.CreatedAt < limit)
                     || (r.Status == BackupStatuses.Running && (r.StartedAt ?? r.CreatedAt) < limit))
            .ToListAsync(ct);
        foreach (var r in stuck)
        {
            r.Error = r.Status == BackupStatuses.Requested
                ? $"لم يبدأ تنفيذ الطلب خلال {Opt.TimeoutMinutes} دقائق (منفذ النسخ غير مشغّل)"
                : $"توقف التنفيذ أكثر من {Opt.TimeoutMinutes} دقائق (أُغلق الخادم أثناء النسخ؟)";
            r.Status = BackupStatuses.Failed;
            r.FinishedAt = now;
            audit.Add("BACKUP_FAILED", "backup_run", r.RunId, after: new { r.Type, r.Error });
        }
        if (stuck.Count > 0) await db.SaveChangesAsync(ct);
        return stuck.Count;
    }

    // ------------------------------------------------------------------ الجدولة اليومية
    public Task<(TimeOnly Time, DateTimeOffset? UpdatedAt)> DailyTimeAsync(CancellationToken ct) => BackupService.DailyTimeAsync(db, Opt, ct);

    public static bool TryParseTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out t);

    /// <summary>آخر موعد يومي مضى (بتوقيت بغداد) قبل now أو عنده.</summary>
    public static DateTimeOffset LastOccurrence(DateTimeOffset now, TimeOnly time)
    {
        var local = now.ToOffset(ClockExtensions.BaghdadOffset);
        var today = new DateTimeOffset(local.Date + time.ToTimeSpan(), ClockExtensions.BaghdadOffset);
        return (today <= local ? today : today.AddDays(-1)).ToUniversalTime(); // Npgsql يقبل timestamptz بإزاحة 0 فقط
    }

    public static DateTimeOffset NextOccurrence(DateTimeOffset now, TimeOnly time) => LastOccurrence(now, time).AddDays(1);

    /// <summary>
    /// يُنشئ طلب النسخة اليومية إن مضى موعدها ولم تُطلب أي نسخة منذ ذلك الموعد (يشمل الخادم المطفأ وقت الموعد).
    /// تغيير الوقت إلى وقت مضى اليوم لا يُطلق نسخة فورية: تبدأ الجدولة من الموعد التالي.
    /// </summary>
    public async Task<BackupRun?> EnsureScheduledAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var (time, updatedAt) = await DailyTimeAsync(ct);
        var due = LastOccurrence(now, time);
        if (updatedAt is { } u && u > due) return null;
        if (await db.BackupRuns.AsNoTracking().AnyAsync(r => r.CreatedAt >= due, ct)) return null;
        return await BackupService.CreateRunAsync(db, audit, clock, "full", SystemTrigger, null, throwIfActive: false, ct);
    }

    // ------------------------------------------------------------------ التنفيذ
    public async Task<bool> ExecuteNextAsync(CancellationToken ct)
    {
        var next = await db.BackupRuns.AsNoTracking().Where(r => r.Status == BackupStatuses.Requested)
            .OrderBy(r => r.CreatedAt).Select(r => new { r.RunId, r.Trigger }).FirstOrDefaultAsync(ct);
        if (next is null) return false;
        var started = clock.UtcNow;
        // حجز الطلب ذرياً: لا ينفذه منفذان معاً
        var claimed = await db.ExecuteAsync(
            $"UPDATE backup_runs SET status = 'running', started_at = {started} WHERE run_id = {next.RunId} AND status = 'requested'", ct);
        if (claimed == 0) return false;

        BackupResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, Opt.TimeoutMinutes)));
            try
            {
                result = await executor.RunAsync(new BackupJob(next.RunId, next.Trigger, started), timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = new BackupResult(false, $"انتهت المهلة ({Opt.TimeoutMinutes} دقائق) قبل اكتمال النسخة", null, null, null, null, null);
            }
            catch (Exception e)
            {
                result = new BackupResult(false, e.Message, null, null, null, null, null);
            }
        }

        var run = await db.BackupRuns.FirstAsync(r => r.RunId == next.RunId, ct);
        run.StartedAt = started; // قد يكون الكيان متتبعاً من قبل الحجز (طلب الجدولة في نفس الدورة)
        run.FinishedAt = clock.UtcNow;
        if (result.Ok)
        {
            run.Status = BackupStatuses.Succeeded;
            run.Path = result.Path;
            run.BackupLabel = result.Label;
            run.SizeBytes = result.DumpBytes;
            run.FilesBytes = result.FilesBytes;
            run.StoredBytes = result.TotalBytes;
            run.Repos = "local";
            run.Error = null;
            var off = result.Path is null ? new OffsiteResult("off", null, null) : await SafeOffsiteAsync(result.Path, ct);
            ApplyOffsite(run, off);
            audit.Add("BACKUP_SUCCEEDED", "backup_run", run.RunId, after: new { run.Type, run.Path, run.StoredBytes, run.OffsiteStatus });
        }
        else
        {
            run.Status = BackupStatuses.Failed;
            run.Error = result.Error ?? "فشل النسخ الاحتياطي";
            audit.Add("BACKUP_FAILED", "backup_run", run.RunId, after: new { run.Type, run.Error });
        }
        await db.SaveChangesAsync(ct);
        if (result.Ok) await ApplyRetentionAsync(ct);
        return true;
    }

    private async Task<OffsiteResult> SafeOffsiteAsync(string path, CancellationToken ct)
    {
        try { return await executor.CopyOffsiteAsync(path, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return new OffsiteResult("failed", null, e.Message); }
    }

    private void ApplyOffsite(BackupRun run, OffsiteResult off)
    {
        run.OffsiteStatus = off.Status;
        if (off.Status == "copied")
        {
            run.OffsitePath = off.Path;
            run.OffsiteAt = clock.UtcNow;
            run.Repos = "local,offsite";
        }
    }

    /// <summary>القرص الخارجي وُصل لاحقاً: تُنسخ أحدث نسخة معلّقة (واحدة في كل دورة).</summary>
    public async Task<bool> RetryOffsiteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Opt.OffsitePath)) return false;
        var run = await db.BackupRuns.Where(r => r.Status == BackupStatuses.Succeeded && r.OffsiteStatus == "pending"
                && r.DeletedAt == null && r.Path != null)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (run is null) return false;
        var off = await SafeOffsiteAsync(run.Path!, ct);
        if (off.Status == "pending") return false;
        ApplyOffsite(run, off);
        await db.SaveChangesAsync(ct);
        return off.Status == "copied";
    }

    /// <summary>يبقي آخر Keep نسخة ناجحة ويحذف مجلدات الأقدم (محلياً وعلى القرص الخارجي)، ويبقى سجلها.</summary>
    public async Task<int> ApplyRetentionAsync(CancellationToken ct)
    {
        var keep = Math.Max(1, Opt.Keep);
        var old = await db.BackupRuns.Where(r => r.Status == BackupStatuses.Succeeded && r.DeletedAt == null && r.Path != null)
            .OrderByDescending(r => r.FinishedAt).Skip(keep).ToListAsync(ct);
        foreach (var r in old)
        {
            executor.DeleteBackupFolder(r.Path!);
            if (r.OffsitePath is { Length: > 0 } o) executor.DeleteBackupFolder(o);
            r.DeletedAt = clock.UtcNow;
        }
        if (old.Count > 0)
        {
            audit.Add("BACKUP_PRUNED", "backup_run", null, after: new { deleted = old.Select(r => r.BackupLabel).ToList(), keep });
            await db.SaveChangesAsync(ct);
        }
        return old.Count;
    }
}
