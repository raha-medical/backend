using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.Backups;

public record BackupRunDto(Guid RunId, string Type, string Trigger, string Status, Guid? RequestedBy, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, double? DurationSeconds, string? BackupLabel, long? SizeBytes, long? StoredBytes, string? Repos,
    string? Error, DateTimeOffset CreatedAt);

/// <summary>جدول النسخ بتوقيت بغداد (للعرض فقط، التنفيذ في سكربت السيرفر).</summary>
public record BackupScheduleDto(string Timezone, string Full, string Incremental, string Wal, string Verify, string Retention);

public record BackupFilesStatusDto(string Mode, int PendingFiles, DateTimeOffset? OldestPendingAt, double? LagHours, bool Healthy);

/// <summary>
/// healthy: آخر نسخة ناجحة خلال max_hours_without_success ولا يوجد فشل لاحق لها، ونسخ الملفات (إن كان مفعلاً) غير متأخر.
/// </summary>
public record BackupStatusDto(bool Enabled, bool Healthy, bool Repo2Enabled, BackupRunDto? LastSuccess, BackupRunDto? LastFailure,
    BackupRunDto? LastVerify, BackupRunDto? Active, double? HoursSinceLastSuccess, int MaxHoursWithoutSuccess, BackupFilesStatusDto Files,
    BackupScheduleDto Schedule, IReadOnlyList<string> Notes);

/// <summary>ملخص قراءة لمدير الفرع: حالة نسخ السيرفر كله، بلا أحجام ولا نصوص أخطاء.</summary>
public record BackupSummaryDto(string Scope, bool Enabled, bool Healthy, DateTimeOffset? LastSuccessAt, double? HoursSinceLastSuccess, string NoteAr);

/// <summary>type: full | diff | incr (الافتراضي incr). verify = اختبار استرجاع، files = نسخ ملفات MinIO.</summary>
public record RunBackupRequest(string? Type);

public class BackupService(IAppDbContext db, ICurrentUser current, IClock clock, IAuditLogger audit, IOptions<BackupOptions> options)
{
    public static readonly string[] DatabaseTypes = ["full", "diff", "incr"];
    private static readonly string[] AllTypes = ["full", "diff", "incr", "verify", "files"];
    private BackupOptions Opt => options.Value;

    public async Task<BackupStatusDto> StatusAsync(CancellationToken ct)
    {
        EnsureAdmin();
        return await BuildStatusAsync(ct);
    }

    /// <summary>النسخ على مستوى السيرفر (pgBackRest) فلا توجد نسخة لكل فرع: ملخص قراءة فقط (backups.view).</summary>
    public async Task<BackupSummaryDto> SummaryAsync(CancellationToken ct)
    {
        var s = await BuildStatusAsync(ct);
        return new BackupSummaryDto("server", s.Enabled, s.Healthy, s.LastSuccess?.FinishedAt, s.HoursSinceLastSuccess,
            "النسخ الاحتياطي للنظام كله وليس لكل فرع على حدة، ويديره المدير العام.");
    }

    private async Task<BackupStatusDto> BuildStatusAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var runs = db.BackupRuns.AsNoTracking();
        var lastSuccess = await runs.Where(r => DatabaseTypes.Contains(r.Type) && r.Status == BackupStatuses.Succeeded)
            .OrderByDescending(r => r.FinishedAt).FirstOrDefaultAsync(ct);
        var lastFailure = await runs.Where(r => r.Status == BackupStatuses.Failed).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        var lastVerify = await runs.Where(r => r.Type == "verify" && (r.Status == BackupStatuses.Succeeded || r.Status == BackupStatuses.Failed))
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        var active = await runs.Where(r => r.Status == BackupStatuses.Requested || r.Status == BackupStatuses.Running)
            .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        double? hours = lastSuccess?.FinishedAt is { } f ? Math.Round((now - f).TotalHours, 1) : null;
        var files = await FilesStatusAsync(now, ct);
        var dbHealthy = hours is not null && hours <= Opt.MaxHoursWithoutSuccess
                        && (lastFailure is null || lastFailure.CreatedAt < lastSuccess!.CreatedAt || !DatabaseTypes.Contains(lastFailure.Type));
        var notes = new List<string>
        {
            "النسخ الاحتياطي مجاني بالكامل (pgBackRest) ويُحفظ على قرص السيرفر.",
            Opt.Repo2Enabled
                ? "الموقع الثاني مفعّل: كل نسخة تُرسل أيضاً إلى الموقع الثاني."
                : "اختياري: يمكن تفعيل موقع ثانٍ (قرص آخر أو سيرفر فرع آخر مجاناً، أو تخزين سحابي S3 باشتراك) عند طلب مدير المجمع.",
            "التنزيل من الواجهة غير متاح، والاسترجاع يتم من السيرفر فقط حسب دليل docs/backup_restore.md.",
        };
        return new BackupStatusDto(Opt.Enabled, Opt.Enabled ? dbHealthy && files.Healthy : false, Opt.Repo2Enabled,
            ToDto(lastSuccess), ToDto(lastFailure), ToDto(lastVerify), ToDto(active), hours, Opt.MaxHoursWithoutSuccess, files,
            new BackupScheduleDto("Asia/Baghdad", "كل جمعة الساعة 03:00", "باقي الأيام الساعة 03:00", "كل 5 دقائق كحد أقصى",
                "اختبار استرجاع تلقائي كل جمعة الساعة 05:00", "4 نسخ أسبوعية + 12 شهرية، وسجل WAL لمدة 14 يوماً"),
            notes);
    }

    public async Task<PagedResult<BackupRunDto>> ListAsync(string? type, string? status, PageQuery p, CancellationToken ct)
    {
        EnsureAdmin();
        var q = db.BackupRuns.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(r => r.Type == type);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.CreatedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        return new PagedResult<BackupRunDto>(rows.Select(r => ToDto(r)!).ToList(), p.SafePage, p.SafePageSize, total);
    }

    /// <summary>طلب نسخة يدوية: يُسجل بحالة requested، وسكربت السيرفر ينفذه خلال دقيقة. 409 إذا يوجد تشغيل جارٍ أو مطلوب.</summary>
    public async Task<BackupRunDto> RequestRunAsync(RunBackupRequest req, CancellationToken ct)
    {
        EnsureAdmin();
        var type = string.IsNullOrWhiteSpace(req.Type) ? "incr" : req.Type.Trim();
        new Validator().Check(AllTypes.Contains(type), "type", "full | diff | incr | verify | files").ThrowIfInvalid();
        if (type == "files" && !Opt.Files.IsOn)
            throw new AppException(409, "BACKUP_FILES_DISABLED", "نسخ الملفات للموقع الثاني غير مفعّل", "فعّله بالإعداد Backup:Files:Mode على السيرفر");

        await using var tx = await db.BeginTransactionAsync(ct);
        // قفل حتى لا يُضاف طلبان معاً
        await db.ExecuteAsync($"SELECT pg_advisory_xact_lock(hashtext('backup_runs.request'))", ct);
        var active = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Status == BackupStatuses.Requested || r.Status == BackupStatuses.Running)
            .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (active is not null)
            throw new AppException(409, "BACKUP_IN_PROGRESS", "يوجد نسخ احتياطي جارٍ أو مطلوب", "انتظر انتهاءه ثم أعد المحاولة")
                { Extensions = new Dictionary<string, object?> { ["run_id"] = active.RunId, ["current_status"] = active.Status } };
        var run = new BackupRun
        {
            RunId = UuidV7.New(), Type = type, Trigger = "manual", Status = BackupStatuses.Requested,
            RequestedBy = current.StaffUserId, CreatedAt = clock.UtcNow,
        };
        db.BackupRuns.Add(run);
        audit.Add("BACKUP_REQUESTED", "backup_run", run.RunId, after: new { run.Type });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return ToDto(run)!;
    }

    private async Task<BackupFilesStatusDto> FilesStatusAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!Opt.Files.IsOn) return new BackupFilesStatusDto("off", 0, null, null, true);
        var pending = db.Files.AsNoTracking().Where(f => f.Status == "stored" && f.S3SyncedAt == null);
        var count = await pending.CountAsync(ct);
        var oldest = count == 0 ? null : await pending.MinAsync(f => (DateTimeOffset?)f.CreatedAt, ct);
        double? lag = oldest is { } o ? Math.Round((now - o).TotalHours, 1) : null;
        return new BackupFilesStatusDto(Opt.Files.Mode, count, oldest, lag, lag is null || lag <= Opt.Files.MaxLagHours);
    }

    private void EnsureAdmin()
    {
        if (current.Role != Roles.Admin) throw AppException.Forbidden("النسخ الاحتياطي للمدير العام فقط");
    }

    internal static BackupRunDto? ToDto(BackupRun? r) => r is null ? null : new BackupRunDto(r.RunId, r.Type, r.Trigger, r.Status, r.RequestedBy,
        r.StartedAt, r.FinishedAt, r.StartedAt is { } s && r.FinishedAt is { } f ? Math.Round((f - s).TotalSeconds, 1) : null,
        r.BackupLabel, r.SizeBytes, r.StoredBytes, r.Repos, r.Error, r.CreatedAt);
}
