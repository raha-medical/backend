using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.Backups;

public record BackupRunDto(Guid RunId, string Type, string Trigger, string Status, Guid? RequestedBy, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, double? DurationSeconds, string? BackupLabel, long? SizeBytes, long? StoredBytes, string? Repos,
    string? Error, DateTimeOffset CreatedAt, string? Path = null, long? FilesBytes = null, string? OffsiteStatus = null,
    string? OffsitePath = null, DateTimeOffset? OffsiteAt = null, DateTimeOffset? DeletedAt = null);

/// <summary>جدول النسخ بتوقيت بغداد (للعرض). في الوضع native: نسخة كاملة يومية في daily_time.</summary>
public record BackupScheduleDto(string Timezone, string Full, string Incremental, string Wal, string Verify, string Retention);

public record BackupFilesStatusDto(string Mode, int PendingFiles, DateTimeOffset? OldestPendingAt, double? LagHours, bool Healthy);

/// <summary>
/// healthy: آخر نسخة ناجحة خلال max_hours_without_success ولا يوجد فشل لاحق لها، ونسخ الملفات (إن كان مفعلاً) غير متأخر.
/// native: موجود فقط في الوضع native (Windows بدون Docker).
/// </summary>
public record BackupStatusDto(bool Enabled, bool Healthy, bool Repo2Enabled, BackupRunDto? LastSuccess, BackupRunDto? LastFailure,
    BackupRunDto? LastVerify, BackupRunDto? Active, double? HoursSinceLastSuccess, int MaxHoursWithoutSuccess, BackupFilesStatusDto Files,
    BackupScheduleDto Schedule, IReadOnlyList<string> Notes, BackupNativeDto? Native = null);

/// <summary>
/// الوضع native: وقت النسخ اليومي (بغداد) وموعد النسخة التالية، مجلد النسخ، والقرص الخارجي
/// (offsite_configured = مضبوط، offsite_available = متصل الآن). keep = عدد النسخ المحتفظ بها.
/// </summary>
public record BackupNativeDto(string DailyTime, DateTimeOffset NextScheduledAt, string Path, bool OffsiteConfigured, bool OffsiteAvailable,
    int Keep, int TimeoutMinutes, bool FilesIncluded, bool KeyIncluded);

/// <summary>daily_time: HH:mm بتوقيت بغداد (مثل 03:00). يُطبق فوراً بلا إعادة تشغيل.</summary>
public record UpdateBackupSettingsRequest(string? DailyTime);

/// <summary>ملخص قراءة لمدير الفرع: حالة نسخ السيرفر كله، بلا أحجام ولا نصوص أخطاء.</summary>
public record BackupSummaryDto(string Scope, bool Enabled, bool Healthy, DateTimeOffset? LastSuccessAt, double? HoursSinceLastSuccess, string NoteAr);

/// <summary>type: full | diff | incr (الافتراضي incr). verify = اختبار استرجاع، files = نسخ ملفات MinIO. في الوضع native كل نسخة full.</summary>
public record RunBackupRequest(string? Type);

/// <summary>ما يعرفه الوضع native عن الجهاز (القرص الخارجي متصل؟ هل الملفات والمفتاح ضمن النسخة؟). ينفذه Infrastructure.</summary>
public interface IBackupEnvironment
{
    bool OffsiteAvailable { get; }
    bool FilesIncluded { get; }
    bool KeyIncluded { get; }
}

public class BackupService(IAppDbContext db, ICurrentUser current, IClock clock, IAuditLogger audit, IOptions<BackupOptions> options,
    IBackupEnvironment environment)
{
    public static readonly string[] DatabaseTypes = ["full", "diff", "incr"];
    private static readonly string[] AllTypes = ["full", "diff", "incr", "verify", "files"];
    private BackupOptions Opt => options.Value;

    public async Task<BackupStatusDto> StatusAsync(CancellationToken ct)
    {
        EnsureAdmin();
        return await BuildStatusAsync(ct);
    }

    /// <summary>النسخ على مستوى السيرفر (pgBackRest أو native) فلا توجد نسخة لكل فرع: ملخص قراءة فقط (backups.view).</summary>
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

        if (Opt.IsNative)
        {
            var time = await DailyTimeAsync(db, Opt, ct);
            var daily = time.Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var offsite = !string.IsNullOrWhiteSpace(Opt.OffsitePath);
            var native = new BackupNativeDto(daily, BackupRunner.NextOccurrence(now, time.Time), Opt.Path, offsite,
                offsite && environment.OffsiteAvailable, Opt.Keep, Opt.TimeoutMinutes, environment.FilesIncluded, environment.KeyIncluded);
            var nativeNotes = new List<string>
            {
                $"النسخ الاحتياطي مجاني ويعمل داخل النظام نفسه: نسخة كاملة يومياً (قاعدة البيانات والملفات المرفوعة ومفتاح توقيع الإيصالات) في {Opt.Path}.",
                offsite
                    ? native.OffsiteAvailable
                        ? "القرص الخارجي متصل: كل نسخة تُنسخ إليه أيضاً."
                        : "القرص الخارجي غير متصل الآن: النسخ تبقى بانتظاره وتُنسخ إليه تلقائياً عند توصيله."
                    : "اختياري: يمكن ضبط قرص خارجي (Backup__OffsitePath) لنسخة ثانية خارج الجهاز.",
                "التنزيل من الواجهة غير متاح، والاسترجاع يتم على الجهاز فقط حسب دليل docs/backup_restore.md.",
            };
            return new BackupStatusDto(Opt.Enabled, Opt.Enabled && dbHealthy, offsite,
                ToDto(lastSuccess), ToDto(lastFailure), ToDto(lastVerify), ToDto(active), hours, Opt.MaxHoursWithoutSuccess, files,
                new BackupScheduleDto("Asia/Baghdad", $"يومياً الساعة {daily}", "لا يوجد (كل نسخة كاملة)", "لا يوجد",
                    "pg_restore --list بعد كل نسخة", $"آخر {Opt.Keep} نسخة"),
                nativeNotes, native);
        }

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

    /// <summary>وقت النسخ اليومي المحفوظ (backup.daily_time، بغداد) ووقت آخر تعديل له. غير المحفوظ = Backup:DefaultDailyTime.</summary>
    internal static async Task<(TimeOnly Time, DateTimeOffset? UpdatedAt)> DailyTimeAsync(IAppDbContext db, BackupOptions opt, CancellationToken ct)
    {
        var row = await db.SystemSettings.AsNoTracking().Where(s => s.BranchId == null && s.Key == BackupRunner.DailyTimeKey)
            .Select(s => new { s.Value, s.UpdatedAt }).FirstOrDefaultAsync(ct);
        var text = row?.Value.Trim('"', ' ') ?? opt.DefaultDailyTime;
        if (!BackupRunner.TryParseTime(text, out var t) && !BackupRunner.TryParseTime(opt.DefaultDailyTime, out t)) t = new TimeOnly(3, 0);
        return (t, row?.UpdatedAt);
    }

    /// <summary>وقت النسخ اليومي (المدير العام فقط). يُحفظ في system_settings ويقرؤه المنفذ في الدورة التالية بلا إعادة تشغيل.</summary>
    public async Task<BackupStatusDto> UpdateSettingsAsync(UpdateBackupSettingsRequest req, CancellationToken ct)
    {
        EnsureAdmin();
        new Validator().Check(BackupRunner.TryParseTime(req.DailyTime, out var time), "daily_time", "الوقت بصيغة HH:mm مثل 03:00").ThrowIfInvalid();
        var text = time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var value = System.Text.Json.JsonSerializer.Serialize(text);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.BranchId == null && s.Key == BackupRunner.DailyTimeKey, ct);
        var before = row?.Value.Trim('"');
        if (row is null)
        {
            row = new SystemSetting { SettingId = UuidV7.New(), BranchId = null, Key = BackupRunner.DailyTimeKey, Value = value, UpdatedAt = clock.UtcNow };
            db.SystemSettings.Add(row);
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = clock.UtcNow;
        }
        audit.Add("BACKUP_SETTINGS_UPDATED", "system_setting", row.SettingId, before: new { daily_time = before }, after: new { daily_time = text });
        await db.SaveChangesAsync(ct);
        return await BuildStatusAsync(ct);
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

    /// <summary>طلب نسخة يدوية: يُسجل بحالة requested، والمنفذ (native أو سكربت السيرفر) ينفذه خلال دقيقة. 409 إذا يوجد تشغيل جارٍ أو مطلوب.</summary>
    public async Task<BackupRunDto> RequestRunAsync(RunBackupRequest req, CancellationToken ct)
    {
        EnsureAdmin();
        var type = string.IsNullOrWhiteSpace(req.Type) ? "incr" : req.Type.Trim();
        new Validator().Check(AllTypes.Contains(type), "type", "full | diff | incr | verify | files").ThrowIfInvalid();
        if (Opt.IsNative)
        {
            // pg_dump ينتج نسخة كاملة دائماً، والتحقق (pg_restore --list) ونسخ الملفات جزء من كل نسخة
            if (type is "files" or "verify")
                throw new AppException(409, "BACKUP_FILES_DISABLED", "التحقق والملفات ضمن كل نسخة", "اطلب نسخة عادية");
            type = "full";
        }
        else if (type == "files" && !Opt.Files.IsOn)
            throw new AppException(409, "BACKUP_FILES_DISABLED", "نسخ الملفات للموقع الثاني غير مفعّل", "فعّله بالإعداد Backup:Files:Mode على السيرفر");
        var run = await CreateRunAsync(db, audit, clock, type, "manual", current.StaffUserId, throwIfActive: true, ct);
        return ToDto(run)!;
    }

    /// <summary>
    /// يسجل طلب نسخة requested تحت قفل، فلا يُضاف طلبان معاً ولا يُطلب أثناء نسخة جارية (409 BACKUP_IN_PROGRESS، أو null للجدولة).
    /// التدقيق باسم من طلب (المدير)، والطلب المجدول بلا مستخدم = "النظام".
    /// </summary>
    internal static async Task<BackupRun?> CreateRunAsync(IAppDbContext db, IAuditLogger audit, IClock clock, string type, string trigger,
        Guid? requestedBy, bool throwIfActive, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        // قفل حتى لا يُضاف طلبان معاً
        await db.ExecuteAsync($"SELECT pg_advisory_xact_lock(hashtext('backup_runs.request'))", ct);
        var active = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Status == BackupStatuses.Requested || r.Status == BackupStatuses.Running)
            .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (active is not null)
        {
            if (!throwIfActive) return null;
            throw new AppException(409, "BACKUP_IN_PROGRESS", "يوجد نسخ احتياطي جارٍ أو مطلوب", "انتظر انتهاءه ثم أعد المحاولة")
                { Extensions = new Dictionary<string, object?> { ["run_id"] = active.RunId, ["current_status"] = active.Status } };
        }
        var run = new BackupRun
        {
            RunId = UuidV7.New(), Type = type, Trigger = trigger, Status = BackupStatuses.Requested,
            RequestedBy = requestedBy, CreatedAt = clock.UtcNow,
        };
        db.BackupRuns.Add(run);
        audit.Add("BACKUP_REQUESTED", "backup_run", run.RunId, after: new { run.Type, run.Trigger });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return run;
    }

    private async Task<BackupFilesStatusDto> FilesStatusAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (Opt.IsNative || !Opt.Files.IsOn) return new BackupFilesStatusDto("off", 0, null, null, true);
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
        r.BackupLabel, r.SizeBytes, r.StoredBytes, r.Repos, r.Error, r.CreatedAt, r.Path, r.FilesBytes, r.OffsiteStatus, r.OffsitePath,
        r.OffsiteAt, r.DeletedAt);
}
