using AlRahma.Application.Abstractions;
using AlRahma.Application.Branches;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Application.PatientApp;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Support;

public record FaqItemDto(Guid FaqId, string Category, string CategoryAr, string QuestionAr, string AnswerAr, int Sort, bool IsActive);
public record FaqItemRequest(string? Category, string? QuestionAr, string? AnswerAr, int? Sort, bool? IsActive);
public record CreateSupportRequest(Guid? SupportRequestId, string? Type, string? Description, Guid? PatientId, Guid? BookingId);
public record SupportReplyRequest(string? Reply);
public record SupportRequestDto(Guid SupportRequestId, string Type, string TypeAr, string Description, string Status, string StatusAr,
    Guid? PatientId, string? PatientName, Guid? BookingId, string? BookingLabel, string? Reply, string? RepliedByName, DateTimeOffset? RepliedAt,
    DateTimeOffset CreatedAt)
{
    /// <summary>للموظف فقط: رقم هاتف حساب المراجع.</summary>
    public string? PhoneNumber { get; init; }
}
public record SupportListQuery(string? Status, string? Type, int Page = 1, int PageSize = 20);
/// <summary>«تواصل مع المجمع»: الهاتف والواتساب والعنوان والموقع والدوام لكل فرع فعال (قسم الدعم).</summary>
public record BranchContactDto(Guid BranchId, string Name, string? City, string? Address, string? Phone, string? Whatsapp, string? WhatsappUrl,
    string? CallUrl, decimal? Latitude, decimal? Longitude, BranchDirectionsDto? Directions, string? WorkingHours,
    IReadOnlyList<WorkingDayDto>? WorkingHoursSchedule, bool? OpenNow);

/// <summary>
/// قسم الدعم (التدقيق: دعم-أ إلى دعم-د): الأسئلة الشائعة (قراءة عامة، إدارة بصلاحية support.manage)، وبيانات التواصل مع الفروع،
/// وطلبات الدعم والشكاوى من تطبيق المراجع مربوطة بالمراجع والحجز، ورد الموظف مع إشعار للمراجع.
/// </summary>
public class SupportService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, PatientPortalService portal,
    NotificationService notifications, BranchService branches)
{
    public static readonly IReadOnlyDictionary<string, string> FaqCategories = new Dictionary<string, string>
    {
        ["booking"] = "الحجز", ["cancel"] = "الإلغاء", ["payment"] = "الدفع", ["refund"] = "الاسترجاع", ["hours"] = "أوقات الدوام",
        ["app"] = "التطبيق", ["other"] = "أخرى",
    };
    public static readonly IReadOnlyDictionary<string, string> RequestTypes = new Dictionary<string, string>
    {
        ["booking"] = "حجز", ["payment"] = "دفع", ["refund"] = "استرجاع", ["app"] = "التطبيق", ["other"] = "أخرى",
    };
    private static string StatusAr(string s) => s == "open" ? "بانتظار الرد" : "تم الرد";
    public const string NotificationType = "support.replied";

    // ------------------------------------------------------------------ الأسئلة الشائعة

    /// <summary>للمراجع وللعموم: الفعالة فقط. لمن يملك support.manage مع all=true: الكل.</summary>
    public async Task<List<FaqItemDto>> FaqAsync(bool all, CancellationToken ct)
    {
        var manage = all && current.IsAuthenticated && !current.IsPatient && current.Has(Permissions.SupportManage);
        var rows = await db.FaqItems.AsNoTracking().Where(f => manage || f.IsActive).OrderBy(f => f.Sort).ThenBy(f => f.CreatedAt).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<FaqItemDto> CreateFaqAsync(FaqItemRequest req, CancellationToken ct)
    {
        Validate(req, create: true);
        var now = clock.UtcNow;
        var row = new FaqItem
        {
            FaqId = UuidV7.New(), Category = req.Category!, QuestionAr = req.QuestionAr!.Trim(), AnswerAr = req.AnswerAr!.Trim(),
            Sort = req.Sort ?? 0, IsActive = req.IsActive ?? true, CreatedAt = now, UpdatedAt = now,
        };
        if (await db.FaqItems.AnyAsync(f => f.QuestionAr == row.QuestionAr, ct))
            throw new AppException(409, "FAQ_EXISTS", "يوجد سؤال بنفس النص");
        db.FaqItems.Add(row);
        audit.Add("FAQ_CREATED", "faq_item", row.FaqId, after: ToDto(row));
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    public async Task<FaqItemDto> UpdateFaqAsync(Guid faqId, FaqItemRequest req, CancellationToken ct)
    {
        Validate(req, create: false);
        var row = await db.FaqItems.FirstOrDefaultAsync(f => f.FaqId == faqId, ct) ?? throw AppException.NotFound("السؤال غير موجود");
        var before = ToDto(row);
        if (req.Category is not null) row.Category = req.Category;
        if (req.QuestionAr is not null) row.QuestionAr = req.QuestionAr.Trim();
        if (req.AnswerAr is not null) row.AnswerAr = req.AnswerAr.Trim();
        if (req.Sort is { } sort) row.Sort = sort;
        if (req.IsActive is { } active) row.IsActive = active;
        row.UpdatedAt = clock.UtcNow;
        audit.Add("FAQ_UPDATED", "faq_item", row.FaqId, before, ToDto(row));
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    public async Task DeleteFaqAsync(Guid faqId, CancellationToken ct)
    {
        var row = await db.FaqItems.FirstOrDefaultAsync(f => f.FaqId == faqId, ct) ?? throw AppException.NotFound("السؤال غير موجود");
        db.FaqItems.Remove(row);
        audit.Add("FAQ_DELETED", "faq_item", row.FaqId, before: ToDto(row));
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(FaqItemRequest r, bool create) => new Validator()
        .Check(!create || r.Category is not null, "category", "التصنيف مطلوب")
        .Check(r.Category is null || FaqCategories.ContainsKey(r.Category), "category", string.Join(" | ", FaqCategories.Keys))
        .Check(!create || !string.IsNullOrWhiteSpace(r.QuestionAr), "question_ar", "السؤال مطلوب")
        .Check(r.QuestionAr is null || r.QuestionAr.Trim().Length is > 0 and <= 300, "question_ar", "300 حرف كحد أقصى")
        .Check(!create || !string.IsNullOrWhiteSpace(r.AnswerAr), "answer_ar", "الجواب مطلوب")
        .Check(r.AnswerAr is null || r.AnswerAr.Trim().Length is > 0 and <= 4000, "answer_ar", "4000 حرف كحد أقصى")
        .ThrowIfInvalid();

    private static FaqItemDto ToDto(FaqItem f) =>
        new(f.FaqId, f.Category, FaqCategories.GetValueOrDefault(f.Category, f.Category), f.QuestionAr, f.AnswerAr, f.Sort, f.IsActive);

    // ------------------------------------------------------------------ التواصل مع المجمع

    public async Task<List<BranchContactDto>> ContactsAsync(CancellationToken ct)
    {
        var list = await db.Branches.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Code).ToListAsync(ct);
        var withStatus = await branches.WithStatusAsync(list, ct);
        return list.Zip(withStatus).Select(x => new BranchContactDto(x.First.BranchId, x.First.Name, x.First.City, x.First.Address, x.First.Phone,
            x.First.Whatsapp, WhatsappUrl(x.First.Whatsapp), x.First.Phone is null ? null : $"tel:{x.First.Phone.Replace(" ", "")}",
            x.First.Latitude, x.First.Longitude, BranchDirectionsDto.For(x.First.Latitude, x.First.Longitude), x.Second.WorkingHoursSummary ?? x.First.WorkingHours,
            x.Second.WorkingHoursSchedule, x.Second.WorkingHoursSchedule is null ? null : x.Second.LiveStatus == "open")).ToList();
    }

    /// <summary>رابط واتساب: الرقم العراقي 07XXXXXXXXX يصبح 9647XXXXXXXXX.</summary>
    public static string? WhatsappUrl(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        var digits = new string(number.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith('0')) digits = "964" + digits[1..];
        return digits.Length < 8 ? null : $"https://wa.me/{digits}";
    }

    // ------------------------------------------------------------------ طلبات الدعم (المراجع)

    public async Task<SupportRequestDto> CreateRequestAsync(CreateSupportRequest req, CancellationToken ct)
    {
        var account = portal.AccountId;
        new Validator()
            .Check(req.Type is not null && RequestTypes.ContainsKey(req.Type), "type", string.Join(" | ", RequestTypes.Keys))
            .Check(!string.IsNullOrWhiteSpace(req.Description) && req.Description.Trim().Length is >= 5 and <= 2000, "description", "اكتب وصف المشكلة (5 إلى 2000 حرف)")
            .ThrowIfInvalid();
        var id = req.SupportRequestId ?? UuidV7.New();
        if (await db.SupportRequests.AsNoTracking().FirstOrDefaultAsync(r => r.SupportRequestId == id, ct) is { } existing)
        {
            if (existing.PatientAccountId == account) return (await ToDtosAsync([existing], staff: false, ct))[0];
            throw new AppException(409, "SUPPORT_REQUEST_EXISTS", "يوجد طلب بنفس المعرف");
        }
        Guid? patientId = req.PatientId, branchId = null;
        if (patientId is { } pid) await portal.EnsureLinkedAsync(pid, ct);
        if (req.BookingId is { } bid)
        {
            var booking = await db.Bookings.AsNoTracking().Include(b => b.Session)
                              .Where(b => b.BookingId == bid && portal.LinkedPatientIds().Contains(b.PatientId)).FirstOrDefaultAsync(ct)
                          ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
            if (patientId is not null && patientId != booking.PatientId)
                throw AppException.Validation(new Dictionary<string, string[]> { ["booking_id"] = ["الحجز لمراجع آخر في الحساب"] });
            patientId = booking.PatientId;
            branchId = booking.Session!.BranchId;
        }
        patientId ??= await db.PatientAccountLinks.AsNoTracking().Where(l => l.PatientAccountId == account && l.Relation == PatientRelations.Self)
            .Select(l => (Guid?)l.PatientId).FirstOrDefaultAsync(ct);
        if (branchId is null && patientId is { } p)
            branchId = await db.Patients.AsNoTracking().Where(x => x.PatientId == p).Select(x => x.HomeBranchId).FirstOrDefaultAsync(ct);

        var row = new SupportRequest
        {
            SupportRequestId = id, PatientAccountId = account, PatientId = patientId, BookingId = req.BookingId, BranchId = branchId,
            Type = req.Type!, Description = req.Description!.Trim(), Status = "open", CreatedAt = clock.UtcNow,
        };
        db.SupportRequests.Add(row);
        audit.Add("SUPPORT_REQUEST_CREATED", "support_request", row.SupportRequestId, after: new { row.Type, row.PatientId, row.BookingId });
        await db.SaveChangesAsync(ct);
        // تنبيه موظفي الدعم في الفرع
        if (branchId is { } b)
        {
            var staff = await StaffForBranchAsync(b, ct);
            if (staff.Count > 0)
                await notifications.NotifyUsersAsync(staff, b, "support.request_created", "طلب دعم جديد",
                    $"{RequestTypes[row.Type]}: {Short(row.Description)}", "support_request", row.SupportRequestId, ct);
        }
        return (await ToDtosAsync([row], staff: false, ct))[0];
    }

    public async Task<List<SupportRequestDto>> MyRequestsAsync(CancellationToken ct)
    {
        var account = portal.AccountId;
        var rows = await db.SupportRequests.AsNoTracking().Where(r => r.PatientAccountId == account).OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        return await ToDtosAsync(rows, staff: false, ct);
    }

    // ------------------------------------------------------------------ لوحة الموظف

    /// <summary>طلبات فرع الموظف الحالي (والطلبات بلا فرع). المدير العام: كل الفروع.</summary>
    public async Task<PagedResult<SupportRequestDto>> ListAsync(SupportListQuery q, CancellationToken ct)
    {
        new Validator()
            .Check(q.Status is null or "open" or "replied", "status", "open | replied")
            .Check(q.Type is null || RequestTypes.ContainsKey(q.Type), "type", string.Join(" | ", RequestTypes.Keys))
            .ThrowIfInvalid();
        var p = new PageQuery(q.Page, q.PageSize);
        var rows = Scoped();
        if (q.Status is not null) rows = rows.Where(r => r.Status == q.Status);
        if (q.Type is not null) rows = rows.Where(r => r.Type == q.Type);
        var total = await rows.CountAsync(ct);
        var page = await rows.OrderBy(r => r.Status == "open" ? 0 : 1).ThenByDescending(r => r.CreatedAt)
            .Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        return new PagedResult<SupportRequestDto>(await ToDtosAsync(page, staff: true, ct), p.SafePage, p.SafePageSize, total);
    }

    public async Task<SupportRequestDto> ReplyAsync(Guid id, SupportReplyRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Reply) && req.Reply.Trim().Length <= 2000, "reply", "اكتب الرد (2000 حرف كحد أقصى)").ThrowIfInvalid();
        var row = await Scoped(tracking: true).FirstOrDefaultAsync(r => r.SupportRequestId == id, ct)
                  ?? throw new AppException(404, "SUPPORT_REQUEST_NOT_FOUND", "الطلب غير موجود");
        var before = new { row.Status, row.Reply };
        row.Reply = req.Reply!.Trim();
        row.Status = "replied";
        row.RepliedBy = current.UserId;
        row.RepliedAt = clock.UtcNow;
        audit.Add("SUPPORT_REQUEST_REPLIED", "support_request", row.SupportRequestId, before, new { row.Status, row.Reply });
        await db.SaveChangesAsync(ct);
        await notifications.NotifyAccountAsync(row.PatientAccountId, row.BranchId, NotificationType, "تم الرد على طلبك",
            Short(row.Reply, 140), "support_request", row.SupportRequestId, ct);
        return (await ToDtosAsync([row], staff: true, ct))[0];
    }

    private IQueryable<SupportRequest> Scoped(bool tracking = false)
    {
        var q = tracking ? db.SupportRequests : db.SupportRequests.AsNoTracking();
        if (current.Role == Roles.Admin) return q;
        var branch = current.BranchId;
        return q.Where(r => r.BranchId == null || r.BranchId == branch);
    }

    private async Task<List<Guid>> StaffForBranchAsync(Guid branchId, CancellationToken ct)
    {
        var roles = await db.RolePermissions.AsNoTracking().Where(r => r.Permission == Permissions.SupportManage).Select(r => r.RoleCode).ToListAsync(ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Role != Roles.Admin && u.Branches.Any(b => b.BranchId == branchId)
                        && (roles.Contains(u.Role) || db.UserPermissions.Any(p => p.UserId == u.UserId && p.Permission == Permissions.SupportManage)))
            .Select(u => u.UserId).ToListAsync(ct);
    }

    private static string Short(string s, int max = 80) => s.Length <= max ? s : s[..max] + "…";

    private async Task<List<SupportRequestDto>> ToDtosAsync(List<SupportRequest> rows, bool staff, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var patientIds = rows.Where(r => r.PatientId != null).Select(r => r.PatientId!.Value).Distinct().ToList();
        var patients = await db.Patients.AsNoTracking().Where(p => patientIds.Contains(p.PatientId)).ToDictionaryAsync(p => p.PatientId, p => p.FullName, ct);
        var bookingIds = rows.Where(r => r.BookingId != null).Select(r => r.BookingId!.Value).Distinct().ToList();
        var bookings = await db.Bookings.AsNoTracking().Where(b => bookingIds.Contains(b.BookingId))
            .Join(db.DoctorSessions, b => b.SessionId, s => s.SessionId, (b, s) => new { b.BookingId, s.SessionDate, s.DoctorId, b.SequenceNumber })
            .Join(db.Doctors, x => x.DoctorId, d => d.DoctorId, (x, d) => new { x.BookingId, x.SessionDate, x.SequenceNumber, d.FullName })
            .ToDictionaryAsync(x => x.BookingId, x => $"{x.FullName} · {x.SessionDate:yyyy-MM-dd} · رقم {x.SequenceNumber}", ct);
        var userIds = rows.Where(r => r.RepliedBy != null).Select(r => r.RepliedBy!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
        var accountIds = rows.Select(r => r.PatientAccountId).Distinct().ToList();
        var phones = staff
            ? await db.PatientAccounts.AsNoTracking().Where(a => accountIds.Contains(a.PatientAccountId)).ToDictionaryAsync(a => a.PatientAccountId, a => a.PhoneNumber, ct)
            : [];
        return rows.Select(r => new SupportRequestDto(r.SupportRequestId, r.Type, RequestTypes.GetValueOrDefault(r.Type, r.Type), r.Description, r.Status,
            StatusAr(r.Status), r.PatientId, r.PatientId is { } p ? patients.GetValueOrDefault(p) : null, r.BookingId,
            r.BookingId is { } b ? bookings.GetValueOrDefault(b) : null, r.Reply,
            // المراجع يرى أن الرد من «فريق الدعم» لا اسم الموظف
            r.RepliedBy is { } u ? (staff ? users.GetValueOrDefault(u) : "فريق الدعم") : null, r.RepliedAt, r.CreatedAt)
        {
            PhoneNumber = staff ? phones.GetValueOrDefault(r.PatientAccountId) : null,
        }).ToList();
    }
}
