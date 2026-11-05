using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Application.PatientApp;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Clinical;

public record ReferralDto(Guid ReferralId, string Service, string ServiceAr, string Status, string StatusAr, Guid PatientId, string PatientName, string FileNumber,
    string Title, string? DoctorName, Guid? OrderItemId, Guid? PrescriptionId, string? PrescriptionCode, string PaymentStatus, long AmountDue,
    string? ActivationMethod, string? ActivationMethodAr, DateTimeOffset? ActivatedAt, DateTimeOffset? CompletedAt, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);
public record ReferralSummaryDto(int Referred, int Activated, int CompletedToday);
public record ScanReferralRequest(string? Service, string? Code);
public record ScanReferralResponse(int Activated, IReadOnlyList<ReferralDto> Items);

/// <summary>
/// ربط دورة التحويل بباقي النظام، بلا فحص صلاحيات (يُستدعى من الطبيب والدفع والأقسام):
/// الإنشاء عند الطلب، التفعيل، الإكمال، والإغلاق عند الإلغاء.
/// </summary>
public class ReferralTracker(IAppDbContext db, IClock clock, ICurrentUser current, SettingsReader settings, IRealtime realtime, NotificationService notifications)
{
    public static string ServiceOf(string department, string? modality) =>
        department == OrderDepartments.Lab ? ReferralServices.Lab : modality == "ultrasound" ? ReferralServices.Ultrasound : ReferralServices.Radiology;

    /// <summary>مجموعة التحديث المباشر: السونار ضمن قسم الأشعة.</summary>
    public static string GroupOf(string service) => service switch
    {
        ReferralServices.Lab => OrderDepartments.Lab,
        ReferralServices.Pharmacy => ReferralServices.Pharmacy,
        _ => OrderDepartments.Radiology,
    };

    public static string[] WritePermissionOf(string service) => service switch
    {
        ReferralServices.Lab => [Permissions.LabWrite],
        ReferralServices.Pharmacy => [Permissions.PharmacyDispense],
        _ => [Permissions.RadiologyWrite],
    };

    public async Task<DateTimeOffset> ExpiryAsync(Guid branchId, CancellationToken ct) =>
        clock.UtcNow.AddDays(await settings.GetLongAsync(SettingsReader.ReferralExpiryDays, branchId, 7, ct));

    /// <summary>لا يحفظ: SaveChanges على المستدعي مع الطلب نفسه.</summary>
    public void TrackOrder(MedicalOrder order, string? modality, DateTimeOffset expiresAt) =>
        db.Referrals.Add(new Referral
        {
            ReferralId = UuidV7.New(), BranchId = order.BranchId, PatientId = order.PatientId, Service = ServiceOf(order.Department, modality),
            OrderItemId = order.OrderItemId, ExpiresAt = expiresAt, CreatedAt = clock.UtcNow,
        });

    public void TrackPrescription(Prescription rx, Guid branchId) =>
        db.Referrals.Add(new Referral
        {
            ReferralId = UuidV7.New(), BranchId = branchId, PatientId = rx.PatientId, Service = ReferralServices.Pharmacy, PrescriptionId = rx.PrescriptionId,
            ExpiresAt = rx.ExpiresAt, CreatedAt = clock.UtcNow,
        });

    /// <summary>إشعار هادئ للخدمات بعد التحويل: صف واحد لكل خدمة، بلا صوت، والحدث المباشر يحدّث التبويب فقط.</summary>
    public async Task NotifyCreatedAsync(Guid patientId, Guid[] orderIds, Guid? prescriptionId, CancellationToken ct)
    {
        var rows = await db.Referrals.AsNoTracking()
            .Where(r => (r.OrderItemId != null && orderIds.Contains(r.OrderItemId.Value)) || (prescriptionId != null && r.PrescriptionId == prescriptionId)).ToListAsync(ct);
        if (rows.Count == 0) return;
        var patient = await db.Patients.AsNoTracking().Where(p => p.PatientId == patientId).Select(p => p.FullName).FirstAsync(ct);
        foreach (var g in rows.GroupBy(r => new { r.BranchId, r.Service }))
        {
            var users = await ServiceUsersAsync(g.Key.BranchId, g.Key.Service, ct);
            await notifications.NotifyUsersAsync(users, g.Key.BranchId, NotificationTypes.ReferralCreated, "تحويل وارد",
                $"{patient}: {g.Count()} {ServiceArOf(g.Key.Service)}", "referral", g.First().ReferralId, ct, NotificationPriorities.Low);
            await realtime.SendAsync(RealtimeGroups.BranchDepartment(g.Key.BranchId, GroupOf(g.Key.Service)), "referral.updated", new { service = g.Key.Service, status = ReferralStatuses.Referred });
        }
    }

    /// <summary>يفعّل التحويلات المحددة ويُخطر الخدمة (عند الحضور). يعيد ما تغير فعلاً.</summary>
    public async Task<List<Referral>> ActivateAsync(List<Referral> rows, string method, CancellationToken ct, Guid? moveToBranch = null)
    {
        var now = clock.UtcNow;
        var changed = rows.Where(r => r.Status is ReferralStatuses.Referred or ReferralStatuses.Expired or ReferralStatuses.External).ToList();
        foreach (var r in changed)
        {
            r.Status = ReferralStatuses.Activated;
            r.ActivationMethod = method;
            r.ActivatedAt = now;
            r.ActivatedBy = current.IsPatient ? null : current.StaffUserId;
            r.ClosedAt = null;
            if (moveToBranch is { } b) r.BranchId = b;
        }
        if (changed.Count == 0) return changed;
        await db.SaveChangesAsync(ct);
        var patients = changed.Select(r => r.PatientId).Distinct().ToList();
        var names = await db.Patients.AsNoTracking().Where(p => patients.Contains(p.PatientId)).ToDictionaryAsync(p => p.PatientId, p => p.FullName, ct);
        foreach (var g in changed.GroupBy(r => new { r.BranchId, r.Service, r.PatientId }))
        {
            var users = (await ServiceUsersAsync(g.Key.BranchId, g.Key.Service, ct)).Where(u => u != (current.IsPatient ? Guid.Empty : current.UserId));
            await notifications.NotifyUsersAsync(users, g.Key.BranchId, NotificationTypes.ReferralActivated, "مراجع حاضر للخدمة",
                $"{names.GetValueOrDefault(g.Key.PatientId)}: {g.Count()} {ServiceArOf(g.Key.Service)}", "referral", g.First().ReferralId, ct);
            await realtime.SendAsync(RealtimeGroups.BranchDepartment(g.Key.BranchId, GroupOf(g.Key.Service)), "referral.updated", new { service = g.Key.Service, status = ReferralStatuses.Activated });
        }
        return changed;
    }

    /// <summary>الدفع الحضوري أو الاستثناء من موظف في الفرع يعني أن المراجع حاضر: يدخل قائمة العمل (العقد 23.3).</summary>
    public async Task ActivateByInvoiceItemsAsync(Guid[] invoiceItemIds, CancellationToken ct)
    {
        if (invoiceItemIds.Length == 0) return;
        var rows = await (from r in db.Referrals
                          join m in db.MedicalOrders on r.OrderItemId equals m.OrderItemId // بلا AsNoTracking: يجب أن تبقى صفوف التحويل متتبعة
                          where invoiceItemIds.Contains(m.InvoiceItemId) && m.Status != OrderStatuses.Cancelled
                          select r).ToListAsync(ct);
        await ActivateAsync(rows, ReferralMethods.Cashier, ct);
    }

    /// <summary>بدء العمل على طلب يثبت الحضور.</summary>
    public async Task ActivateForWorkAsync(Guid orderItemId, CancellationToken ct)
    {
        var row = await db.Referrals.FirstOrDefaultAsync(r => r.OrderItemId == orderItemId && r.Status != ReferralStatuses.Activated && r.Status != ReferralStatuses.Completed, ct);
        if (row is not null) await ActivateAsync([row], ReferralMethods.Work, ct);
    }

    public Task CompleteOrderAsync(Guid orderItemId, CancellationToken ct) => CompleteAsync(r => r.OrderItemId == orderItemId, ct);
    public Task CompletePrescriptionAsync(Guid prescriptionId, Guid branchId, CancellationToken ct) => CompleteAsync(r => r.PrescriptionId == prescriptionId, ct, branchId);

    private async Task CompleteAsync(System.Linq.Expressions.Expression<Func<Referral, bool>> filter, CancellationToken ct, Guid? branchId = null)
    {
        var row = await db.Referrals.Where(filter).FirstOrDefaultAsync(ct);
        if (row is null || row.Status == ReferralStatuses.Completed) return;
        var now = clock.UtcNow;
        row.Status = ReferralStatuses.Completed;
        row.CompletedAt = now;
        row.ClosedAt = now;
        if (row.ActivatedAt is null) { row.ActivatedAt = now; row.ActivationMethod = ReferralMethods.Work; row.ActivatedBy = current.StaffUserId; }
        if (branchId is { } b) row.BranchId = b; // الوصفة تُصرف من أي فرع
        await db.SaveChangesAsync(ct);
        await realtime.SendAsync(RealtimeGroups.BranchDepartment(row.BranchId, GroupOf(row.Service)), "referral.updated", new { service = row.Service, status = row.Status });
    }

    /// <summary>الطلب أُلغي أو استُرجع: التحويل يُغلق كملغى.</summary>
    public async Task CancelByOrdersAsync(Guid[] orderItemIds, CancellationToken ct)
    {
        if (orderItemIds.Length == 0) return;
        var rows = await db.Referrals.Where(r => r.OrderItemId != null && orderItemIds.Contains(r.OrderItemId.Value)
                                                 && r.Status != ReferralStatuses.Completed && r.Status != ReferralStatuses.Cancelled).ToListAsync(ct);
        foreach (var r in rows) { r.Status = ReferralStatuses.Cancelled; r.ClosedAt = clock.UtcNow; }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>موظفو الخدمة في الفرع (بدون مدراء القراءة فقط).</summary>
    public Task<List<Guid>> ServiceUsersAsync(Guid branchId, string service, CancellationToken ct)
    {
        var perms = WritePermissionOf(service);
        return db.Users.AsNoTracking()
            .Where(u => u.IsActive && db.UserBranches.Any(b => b.UserId == u.UserId && b.BranchId == branchId)
                        && (db.RolePermissions.Any(rp => rp.RoleCode == u.Role && perms.Contains(rp.Permission))
                            || db.UserPermissions.Any(p => p.UserId == u.UserId && perms.Contains(p.Permission))))
            .Select(u => u.UserId).ToListAsync(ct);
    }

    private static string ServiceArOf(string service) => service switch
    {
        ReferralServices.Lab => "تحليل",
        ReferralServices.Radiology => "أشعة",
        ReferralServices.Ultrasound => "سونار",
        _ => "وصفة",
    };
}

/// <summary>
/// دورة التحويل (المرحلة 5): قوائم الخدمات، التفعيل بالمسح أو البحث أو تأكيد المريض، و«سأنفذه خارجياً».
/// مستقلة عن حالة الدفع: العقد 23.3. عزل الفروع: الفرع الحالي في التوكن دائماً.
/// </summary>
public class ReferralService(IAppDbContext db, ICurrentUser current, IClock clock, IAuditLogger audit, PatientPortalService portal, ReferralTracker tracker)
{
    private static readonly string[] StatusList = [ReferralStatuses.Referred, ReferralStatuses.Activated, ReferralStatuses.Completed, ReferralStatuses.Expired, ReferralStatuses.External, ReferralStatuses.Cancelled];

    private void EnsureCanRead(string? service)
    {
        new Validator().Check(service is ReferralServices.Lab or ReferralServices.Radiology or ReferralServices.Ultrasound or ReferralServices.Pharmacy, "service", "lab | radiology | ultrasound | pharmacy").ThrowIfInvalid();
        var ok = service switch
        {
            ReferralServices.Lab => current.Has(Permissions.LabWrite) || current.Has(Permissions.LabView),
            ReferralServices.Pharmacy => current.Has(Permissions.PharmacyDispense) || current.Has(Permissions.PharmacyView),
            _ => current.Has(Permissions.RadiologyWrite) || current.Has(Permissions.RadiologyView),
        };
        if (!ok) throw AppException.Forbidden();
    }

    private void EnsureCanWrite(string service)
    {
        if (!ReferralTracker.WritePermissionOf(service).Any(current.Has)) throw AppException.Forbidden();
    }

    private IQueryable<Referral> Scoped(string service) => db.Referrals.Where(r => r.BranchId == current.BranchId && r.Service == service);

    public async Task<PagedResult<ReferralDto>> ListAsync(string? service, string? status, DateOnly? date, string? search, int page, int pageSize, CancellationToken ct)
    {
        EnsureCanRead(service);
        new Validator().Check(status is null || StatusList.Contains(status), "status", string.Join(" | ", StatusList)).ThrowIfInvalid();
        var p = new PageQuery(page, pageSize);
        var q = Scoped(service!).AsNoTracking();
        if (status is not null) q = q.Where(r => r.Status == status);
        if (date is { } d)
        {
            DateTimeOffset s = ClockExtensions.ToUtc(d, TimeOnly.MinValue), e = ClockExtensions.ToUtc(d.AddDays(1), TimeOnly.MinValue);
            q = q.Where(r => (r.CompletedAt ?? r.ActivatedAt ?? r.CreatedAt) >= s && (r.CompletedAt ?? r.ActivatedAt ?? r.CreatedAt) < e);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var phone = PhoneNumbers.Normalize(term);
            q = q.Where(r => db.Patients.Any(x => x.PatientId == r.PatientId
                && (EF.Functions.ILike(x.FullName, $"%{term}%") || x.FileNumber == term.ToUpper() || (phone != null && x.PhoneNumber == phone))));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.CompletedAt ?? r.ActivatedAt ?? r.CreatedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        return new PagedResult<ReferralDto>(await ToDtosAsync(rows, ct), p.SafePage, p.SafePageSize, total);
    }

    public async Task<ReferralSummaryDto> SummaryAsync(string? service, CancellationToken ct)
    {
        EnsureCanRead(service);
        var day = clock.BaghdadToday();
        DateTimeOffset s = ClockExtensions.ToUtc(day, TimeOnly.MinValue), e = ClockExtensions.ToUtc(day.AddDays(1), TimeOnly.MinValue);
        var q = Scoped(service!).AsNoTracking();
        return new ReferralSummaryDto(await q.CountAsync(r => r.Status == ReferralStatuses.Referred, ct), await q.CountAsync(r => r.Status == ReferralStatuses.Activated, ct),
            await q.CountAsync(r => r.Status == ReferralStatuses.Completed && r.CompletedAt >= s && r.CompletedAt < e, ct));
    }

    /// <summary>تأكيد الموظف بعد البحث عن الاسم. المنتهي والخارجي يعودان للعمل إذا حضر المراجع لاحقاً.</summary>
    public async Task<ReferralDto> ActivateAsync(Guid referralId, CancellationToken ct)
    {
        var row = await db.Referrals.FirstOrDefaultAsync(r => r.ReferralId == referralId && r.BranchId == current.BranchId, ct)
                  ?? throw new AppException(404, "REFERRAL_NOT_FOUND", "التحويل غير موجود في هذا الفرع");
        EnsureCanWrite(row.Service);
        if (row.Status is ReferralStatuses.Completed or ReferralStatuses.Cancelled)
            throw new AppException(409, "REFERRAL_CLOSED", "التحويل مكتمل أو ملغى");
        var changed = await tracker.ActivateAsync([row], ReferralMethods.StaffSearch, ct);
        if (changed.Count > 0) audit.Add("REFERRAL_ACTIVATED", "referral", row.ReferralId, after: new { row.Service, method = ReferralMethods.StaffSearch });
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([row], ct))[0];
    }

    /// <summary>
    /// مسح رمز المراجع أو رقم الطلب أو الوصفة. يقبل: patient:معرف، order:معرف، RX-كود، رقم الملف، أو باركود العينة.
    /// رمز المريض يفعّل كل تحويلاته المنتظرة لهذه الخدمة في هذا الفرع. الوصفة تُفعَّل في أي فرع وتنتقل إليه.
    /// </summary>
    public async Task<ScanReferralResponse> ScanAsync(ScanReferralRequest req, CancellationToken ct)
    {
        EnsureCanRead(req.Service);
        var service = req.Service!;
        EnsureCanWrite(service);
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Code) && req.Code.Length <= 100, "code", "الرمز مطلوب").ThrowIfInvalid();
        var code = req.Code!.Trim();
        var pending = new[] { ReferralStatuses.Referred, ReferralStatuses.Expired, ReferralStatuses.External };
        List<Referral> rows = [];
        Guid? move = null;
        var raw = code.Contains(':') ? code[(code.IndexOf(':') + 1)..] : code;
        if (service == ReferralServices.Pharmacy && !code.StartsWith("patient:", StringComparison.OrdinalIgnoreCase))
        {
            var rxCode = code.ToUpperInvariant();
            if (!rxCode.StartsWith("RX-")) rxCode = "RX-" + rxCode;
            var rxId = await db.Prescriptions.AsNoTracking().Where(p => p.PrescriptionCode == rxCode).Select(p => (Guid?)p.PrescriptionId).FirstOrDefaultAsync(ct);
            if (rxId is not null)
            {
                rows = await db.Referrals.Where(r => r.PrescriptionId == rxId && pending.Contains(r.Status)).ToListAsync(ct);
                move = current.BranchId;
            }
        }
        if (rows.Count == 0 && code.StartsWith("order:", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(raw, out var orderId))
            rows = await Scoped(service).Where(r => r.OrderItemId == orderId && pending.Contains(r.Status)).ToListAsync(ct);
        if (rows.Count == 0)
        {
            var upper = code.ToUpperInvariant();
            Guid? patientId = Guid.TryParse(raw, out var pid) ? pid : await db.Patients.AsNoTracking().Where(p => p.FileNumber == upper).Select(p => (Guid?)p.PatientId).FirstOrDefaultAsync(ct);
            if (patientId is not null)
            {
                rows = await Scoped(service).Where(r => r.PatientId == patientId && r.Status == ReferralStatuses.Referred).ToListAsync(ct);
                if (rows.Count == 0) rows = await Scoped(service).Where(r => r.PatientId == patientId && pending.Contains(r.Status)).ToListAsync(ct);
            }
            else if (service == ReferralServices.Lab)
            {
                var byBarcode = await db.MedicalOrders.AsNoTracking().Where(o => o.SampleBarcode == upper && o.BranchId == current.BranchId).Select(o => (Guid?)o.OrderItemId).FirstOrDefaultAsync(ct);
                if (byBarcode is { } oid) rows = await Scoped(service).Where(r => r.OrderItemId == oid).ToListAsync(ct);
            }
        }
        if (rows.Count == 0) throw new AppException(404, "REFERRAL_NOT_FOUND", "لا يوجد تحويل منتظر لهذا الرمز في هذا الفرع");
        var changed = await tracker.ActivateAsync(rows, ReferralMethods.Scan, ct, move);
        foreach (var r in changed) audit.Add("REFERRAL_ACTIVATED", "referral", r.ReferralId, after: new { r.Service, method = ReferralMethods.Scan });
        await db.SaveChangesAsync(ct);
        return new ScanReferralResponse(changed.Count, await ToDtosAsync(rows, ct));
    }

    // ------------------------------------------------------------------ تطبيق المريض

    private async Task<Referral> MineAsync(Guid referralId, CancellationToken ct)
    {
        var linked = portal.LinkedPatientIds();
        return await db.Referrals.FirstOrDefaultAsync(r => r.ReferralId == referralId && linked.Contains(r.PatientId), ct)
               ?? throw new AppException(404, "REFERRAL_NOT_FOUND", "التحويل غير موجود");
    }

    /// <summary>المريض يؤكد أنه سينفذ الخدمة داخل المجمع.</summary>
    public async Task<ReferralDto> ConfirmAsync(Guid referralId, CancellationToken ct)
    {
        var row = await MineAsync(referralId, ct);
        if (row.Status is ReferralStatuses.Completed or ReferralStatuses.Cancelled)
            throw new AppException(409, "REFERRAL_CLOSED", "التحويل مكتمل أو ملغى");
        var changed = await tracker.ActivateAsync([row], ReferralMethods.PatientApp, ct);
        if (changed.Count > 0) audit.Add("REFERRAL_ACTIVATED", "referral", row.ReferralId, after: new { row.Service, method = ReferralMethods.PatientApp });
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([row], ct))[0];
    }

    /// <summary>«سأنفذه خارجياً»: يخرج من انتظار الخدمة ويبقى في ملفه. المدفوع لا يُعلَّم خارجياً قبل استرجاعه.</summary>
    public async Task<ReferralDto> MarkExternalAsync(Guid referralId, CancellationToken ct)
    {
        var row = await MineAsync(referralId, ct);
        if (row.Status != ReferralStatuses.Referred && row.Status != ReferralStatuses.Expired)
            throw new AppException(409, "REFERRAL_NOT_PENDING", "لا يمكن تغيير هذا التحويل", "بدأ تنفيذه أو أُغلق");
        if (row.OrderItemId is { } oid && await PaidAsync(oid, ct))
            throw new AppException(409, "REFERRAL_ALREADY_PAID", "هذا الطلب مدفوع", "راجع الاستقبال لاسترجاع المبلغ قبل تنفيذه خارجياً");
        row.Status = ReferralStatuses.External;
        row.ClosedAt = clock.UtcNow;
        audit.Add("REFERRAL_EXTERNAL", "referral", row.ReferralId, after: new { row.Service });
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([row], ct))[0];
    }

    private Task<bool> PaidAsync(Guid orderItemId, CancellationToken ct) =>
        (from o in db.MedicalOrders.AsNoTracking()
         join i in db.InvoiceItems.AsNoTracking() on o.InvoiceItemId equals i.InvoiceItemId
         where o.OrderItemId == orderItemId && i.Status != InvoiceItemStatuses.Unpaid && i.Status != InvoiceItemStatuses.Void
         select i.InvoiceItemId).AnyAsync(ct);

    // ------------------------------------------------------------------ العرض

    public static string StatusAr(string status) => status switch
    {
        ReferralStatuses.Referred => "محوَّل",
        ReferralStatuses.Activated => "حاضر",
        ReferralStatuses.Completed => "مكتمل",
        ReferralStatuses.Expired => "منتهي الصلاحية",
        ReferralStatuses.External => "خارجي",
        ReferralStatuses.Cancelled => "ملغى",
        _ => status,
    };

    private static string ServiceAr(string service) => service switch
    {
        ReferralServices.Lab => "المختبر", ReferralServices.Radiology => "الأشعة", ReferralServices.Ultrasound => "السونار", _ => "الصيدلية",
    };

    private static string? MethodAr(string? method) => method switch
    {
        ReferralMethods.Scan => "مسح الرمز", ReferralMethods.StaffSearch => "تأكيد الموظف", ReferralMethods.PatientApp => "تأكيد المريض",
        ReferralMethods.Cashier => "الدفع في الاستقبال", ReferralMethods.Work => "بدء العمل", ReferralMethods.Migration => "سابق للنظام", _ => null,
    };

    private async Task<List<ReferralDto>> ToDtosAsync(List<Referral> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var patientIds = rows.Select(r => r.PatientId).Distinct().ToList();
        var patients = await db.Patients.AsNoTracking().Where(p => patientIds.Contains(p.PatientId)).ToDictionaryAsync(p => p.PatientId, ct);
        var orderIds = rows.Where(r => r.OrderItemId != null).Select(r => r.OrderItemId!.Value).ToList();
        var orders = await (
            from o in db.MedicalOrders.AsNoTracking()
            join i in db.InvoiceItems.AsNoTracking() on o.InvoiceItemId equals i.InvoiceItemId
            join d in db.Doctors.AsNoTracking() on o.OrderedByDoctorId equals d.DoctorId into dj
            from d in dj.DefaultIfEmpty()
            where orderIds.Contains(o.OrderItemId)
            select new { o.OrderItemId, i.ServiceName, ItemStatus = i.Status, i.NetPrice, Doctor = d == null ? o.ReferringDoctorName : d.FullName })
            .ToDictionaryAsync(x => x.OrderItemId, ct);
        var rxIds = rows.Where(r => r.PrescriptionId != null).Select(r => r.PrescriptionId!.Value).ToList();
        var rxs = await (
            from p in db.Prescriptions.AsNoTracking()
            join d in db.Doctors.AsNoTracking() on p.DoctorId equals d.DoctorId
            where rxIds.Contains(p.PrescriptionId)
            select new { p.PrescriptionId, p.PrescriptionCode, Doctor = d.FullName, Lines = p.Lines.Count }).ToDictionaryAsync(x => x.PrescriptionId, ct);
        return rows.Select(r =>
        {
            var p = patients[r.PatientId];
            var o = r.OrderItemId is { } oid ? orders.GetValueOrDefault(oid) : null;
            var rx = r.PrescriptionId is { } pid ? rxs.GetValueOrDefault(pid) : null;
            var unpaid = o?.ItemStatus is InvoiceItemStatuses.Unpaid or InvoiceItemStatuses.Deferred;
            return new ReferralDto(r.ReferralId, r.Service, ServiceAr(r.Service), r.Status, StatusAr(r.Status), p.PatientId, p.FullName, p.FileNumber,
                o?.ServiceName ?? (rx is null ? "" : $"وصفة ({rx.Lines} أدوية)"), o?.Doctor ?? rx?.Doctor, r.OrderItemId, r.PrescriptionId, rx?.PrescriptionCode,
                o is null ? "not_applicable" : ConsultationService.PaymentStatusOf(o.ItemStatus), unpaid ? o!.NetPrice : 0,
                r.ActivationMethod, MethodAr(r.ActivationMethod), r.ActivatedAt, r.CompletedAt, r.ExpiresAt, r.CreatedAt);
        }).ToList();
    }
}
