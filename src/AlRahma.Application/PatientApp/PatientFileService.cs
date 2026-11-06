using AlRahma.Application.Abstractions;
using AlRahma.Application.Billing;
using AlRahma.Application.Bookings;
using AlRahma.Application.Clinical;
using AlRahma.Application.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.PatientApp;

public record MyInvoiceItemDto(Guid InvoiceItemId, string ItemType, string ItemTypeAr, string ServiceName, long NetPrice, long RefundedAmount, string Status, string StatusAr, bool Payable);
public record MyInvoiceDto(Guid InvoiceId, string InvoiceNumber, string BranchName, string Status, string StatusAr, long NetAmount, long PaidAmount, long DueAmount,
    DateTimeOffset CreatedAt, IReadOnlyList<MyInvoiceItemDto> Items);
/// <summary>رصيد المحفظة: المبلغ المحفوظ للمراجع (إلغاء حجز مدفوع، استرجاع كرصيد، تأمين)، ويُخصم تلقائياً من الدفع في المجمع.</summary>
public record PatientCreditEntryDto(long Amount, string Kind, string KindAr, string? BranchName, DateTimeOffset CreatedAt);
public record PatientCreditDto(Guid PatientId, long Balance, string Currency, IReadOnlyList<PatientCreditEntryDto> Recent);
public record PatientFilePersonDto(Guid PatientId, string FullName, string FileNumber, string Relation, string? Gender, DateOnly? BirthDate, int? Age, bool IsBanned);
public record PatientFileBranchDto(Guid BranchId, string Name);
/// <summary>
/// طلب مختبر أو أشعة في التطبيق. القائمة لا تتوقف على الدفع: result_ready = النتيجة معتمدة،
/// result_available = جاهزة ومسموح عرضها (مدفوعة أو مستثناة).
/// </summary>
public record MyOrderDto(Guid OrderItemId, string Department, string ServiceName, string Status, string StatusAr, string BranchName, string? DoctorName,
    DateTimeOffset OrderedAt, bool ResultReady, string PaymentStatus, bool PaymentRequired, long AmountDue, Guid InvoiceItemId, bool ResultAvailable)
{
    public Guid InvoiceId { get; init; }
    /// <summary>دورة التحويل: يبقى الطلب في الملف مهما كانت حالتها.</summary>
    public Guid? ReferralId { get; init; }
    public string? ReferralStatus { get; init; }
    public string? ReferralStatusAr { get; init; }
}
public record PatientFileDto(PatientFilePersonDto Patient, IReadOnlyList<PatientFileBranchDto> Branches, IReadOnlyList<BookingDto> UpcomingAppointments,
    IReadOnlyList<BookingDto> PastAppointments, IReadOnlyList<VisitDto> Visits, IReadOnlyList<PrescriptionDto> Prescriptions,
    IReadOnlyList<MyOrderDto> Orders, DateTimeOffset GeneratedAt);

/// <summary>الملف الكامل لفرد في حساب التطبيق من كل الفروع (4.2)، وقائمة طلباته الدائمة (4.3). الملاحظات السريرية والخاصة لا تظهر.</summary>
public class PatientFileService(IAppDbContext db, IClock clock, PatientPortalService portal, BookingService bookings,
    ConsultationService consultations, PharmacyService pharmacy, SettingsReader settings)
{
    private static string ItemTypeAr(string type, string? modality) => type switch
    {
        "new_visit" or "follow_up" or "consultation" => "كشف",
        "lab" => "تحليل",
        "radiology" => modality == "ultrasound" ? "سونار" : "أشعة",
        InvoiceItemTypes.CancellationFee => "رسوم إلغاء",
        _ => "إجراء",
    };
    private static string InvoiceStatusAr(string s) => s switch
    {
        InvoiceStatuses.Paid => "مدفوعة", InvoiceStatuses.PartiallyPaid => "مدفوعة جزئياً", InvoiceStatuses.Deferred => "مؤجلة", _ => "غير مدفوعة",
    };
    private static string ItemStatusAr(string s) => s switch
    {
        InvoiceItemStatuses.Paid => "مدفوع", InvoiceItemStatuses.Deferred => "مؤجل", InvoiceItemStatuses.PartiallyRefunded => "مسترجع جزئياً", InvoiceItemStatuses.Refunded => "مسترجع", _ => "غير مدفوع",
    };

    private static string CreditKindAr(string kind) => kind switch
    {
        "booking_cancel_credit" => "إلغاء حجز مدفوع",
        "refund_credit" => "استرجاع كرصيد",
        "applied_to_payment" => "استُخدم في دفع",
        "refunded_out" => "أُرجع لوسيلة الدفع",
        "booking_rebook" => "نُقل لحجز جديد",
        "booking_deposit" => "عربون حجز",
        "unban_deposit" => "تأمين فك الحظر",
        _ => "حركة رصيد",
    };

    /// <summary>رصيد فرد في الحساب وآخر 20 حركة (فحص الربط بالحساب أولاً).</summary>
    public async Task<PatientCreditDto> CreditAsync(Guid patientId, CancellationToken ct)
    {
        await portal.EnsureLinkedAsync(patientId, ct);
        var balance = await db.PatientCredits.AsNoTracking().Where(c => c.PatientId == patientId).SumAsync(c => (long?)c.Amount, ct) ?? 0;
        var recent = await (from c in db.PatientCredits.AsNoTracking()
                            join b in db.Branches.AsNoTracking() on c.BranchId equals b.BranchId into bj
                            from b in bj.DefaultIfEmpty()
                            where c.PatientId == patientId
                            orderby c.CreatedAt descending
                            select new { c.Amount, c.Kind, BranchName = b == null ? null : b.Name, c.CreatedAt }).Take(20).ToListAsync(ct);
        return new PatientCreditDto(patientId, balance, "IQD",
            recent.Select(r => new PatientCreditEntryDto(r.Amount, r.Kind, CreditKindAr(r.Kind), r.BranchName, r.CreatedAt)).ToList());
    }

    /// <summary>فواتير فرد في الحساب من كل الفروع (6). المستحق يستثني المشطوب والطلب المغلق كخارجي أو منتهٍ، كسلة التحصيل.</summary>
    public async Task<IReadOnlyList<MyInvoiceDto>> InvoicesAsync(Guid patientId, CancellationToken ct)
    {
        await portal.EnsureLinkedAsync(patientId, ct);
        var invoices = await (from i in db.Invoices.AsNoTracking()
                              join b in db.Branches.AsNoTracking() on i.BranchId equals b.BranchId
                              where i.PatientId == patientId && i.Status != InvoiceStatuses.Void
                              orderby i.CreatedAt descending
                              select new { i, Branch = b.Name }).ToListAsync(ct);
        var ids = invoices.Select(x => x.i.InvoiceId).ToList();
        var items = await (from it in db.InvoiceItems.AsNoTracking()
                           join s in db.Services.AsNoTracking() on it.ServiceId equals s.ServiceId
                           where ids.Contains(it.InvoiceId) && it.Status != InvoiceItemStatuses.Void
                           orderby it.CreatedAt
                           select new { it, s.Modality }).ToListAsync(ct);
        var itemIds = items.Select(x => x.it.InvoiceItemId).ToList();
        var writtenOff = await DeferralService.WrittenOffItemIds(db).Where(x => itemIds.Contains(x)).ToListAsync(ct);
        var closed = await (from m in db.MedicalOrders.AsNoTracking()
                            join r in db.Referrals.AsNoTracking() on m.OrderItemId equals r.OrderItemId
                            where itemIds.Contains(m.InvoiceItemId) && (r.Status == ReferralStatuses.External || r.Status == ReferralStatuses.Expired)
                            select m.InvoiceItemId).ToListAsync(ct);
        var notPayable = writtenOff.Concat(closed).ToHashSet();
        var byInvoice = items.ToLookup(x => x.it.InvoiceId);
        return invoices.Select(x =>
        {
            var lines = byInvoice[x.i.InvoiceId].Select(l => new MyInvoiceItemDto(l.it.InvoiceItemId, l.it.ItemType, ItemTypeAr(l.it.ItemType, l.Modality), l.it.ServiceName, l.it.NetPrice, l.it.RefundedAmount,
                l.it.Status, ItemStatusAr(l.it.Status), l.it.Status is InvoiceItemStatuses.Unpaid or InvoiceItemStatuses.Deferred && !notPayable.Contains(l.it.InvoiceItemId))).ToList();
            return new MyInvoiceDto(x.i.InvoiceId, x.i.InvoiceNumber, x.Branch, x.i.Status, InvoiceStatusAr(x.i.Status), x.i.NetAmount, x.i.PaidAmount, lines.Where(l => l.Payable).Sum(l => l.NetPrice), x.i.CreatedAt, lines);
        }).ToList();
    }

    public async Task<IReadOnlyList<MyOrderDto>> OrdersAsync(Guid patientId, string? department, CancellationToken ct)
    {
        await portal.EnsureLinkedAsync(patientId, ct);
        new Validator().Check(department is null or OrderDepartments.Lab or OrderDepartments.Radiology, "department", "lab | radiology").ThrowIfInvalid();
        var rows = await (
            from o in db.MedicalOrders.AsNoTracking()
            join i in db.InvoiceItems.AsNoTracking() on o.InvoiceItemId equals i.InvoiceItemId
            join b in db.Branches.AsNoTracking() on o.BranchId equals b.BranchId
            join d in db.Doctors.AsNoTracking() on o.OrderedByDoctorId equals d.DoctorId into dj
            from d in dj.DefaultIfEmpty()
            join r in db.Referrals.AsNoTracking() on o.OrderItemId equals r.OrderItemId into rj
            from r in rj.DefaultIfEmpty()
            where o.PatientId == patientId && o.Status != OrderStatuses.Cancelled && (department == null || o.Department == department)
            orderby o.CreatedAt descending
            select new { o, i.ServiceName, ItemStatus = i.Status, i.InvoiceId, i.NetPrice, Branch = b.Name, Doctor = d == null ? null : d.FullName, Referral = r }).ToListAsync(ct);
        var deferredOpen = new Dictionary<Guid, bool>();
        foreach (var branch in rows.Select(x => x.o.BranchId).Distinct())
            deferredOpen[branch] = await settings.GetLongAsync(SettingsReader.ShowDeferredResults, branch, 0, ct) == 1;
        return rows.Select(x =>
        {
            var ready = x.o.Status == OrderStatuses.Released;
            var mayRead = DepartmentCore.PatientMayReadResult(x.ItemStatus, deferredOpen[x.o.BranchId]);
            // المستحق: غير المدفوع، والمؤجل الذي اكتملت نتيجته ومقفل بشرط الدفع
            var due = x.ItemStatus == InvoiceItemStatuses.Unpaid || (ready && !mayRead && x.ItemStatus == InvoiceItemStatuses.Deferred);
            return new MyOrderDto(x.o.OrderItemId, x.o.Department, x.ServiceName, x.o.Status, StatusAr(x.o.Status), x.Branch, x.Doctor ?? x.o.ReferringDoctorName,
                x.o.CreatedAt, ready, ConsultationService.PaymentStatusOf(x.ItemStatus), due, due ? x.NetPrice : 0, x.o.InvoiceItemId, ready && mayRead)
            {
                InvoiceId = x.InvoiceId, ReferralId = x.Referral?.ReferralId, ReferralStatus = x.Referral?.Status,
                ReferralStatusAr = x.Referral is null ? null : ReferralService.StatusAr(x.Referral.Status),
            };
        }).ToList();
    }

    public async Task<PatientFileDto> FileAsync(Guid patientId, CancellationToken ct)
    {
        await portal.EnsureLinkedAsync(patientId, ct);
        var account = portal.AccountId;
        var person = await (
            from p in db.Patients.AsNoTracking()
            join l in db.PatientAccountLinks.AsNoTracking() on p.PatientId equals l.PatientId
            where p.PatientId == patientId && l.PatientAccountId == account
            select new { p, l.Relation }).FirstAsync(ct);
        var upcoming = await bookings.MyAsync(patientId, "upcoming", ct);
        var past = await bookings.MyAsync(patientId, "past", ct);
        var visits = await consultations.MyVisitsAsync(patientId, 1, 100, ct);
        var prescriptions = await pharmacy.MyPrescriptionsAsync(patientId, ct);
        var orders = await OrdersAsync(patientId, null, ct);

        var ticketBranches = db.QueueTickets.AsNoTracking().Where(t => t.PatientId == patientId).Select(t => t.BranchId);
        var orderBranches = db.MedicalOrders.AsNoTracking().Where(o => o.PatientId == patientId).Select(o => o.BranchId);
        var branches = await db.Branches.AsNoTracking().Where(b => ticketBranches.Contains(b.BranchId) || orderBranches.Contains(b.BranchId))
            .OrderBy(b => b.Name).Select(b => new PatientFileBranchDto(b.BranchId, b.Name)).ToListAsync(ct);

        return new PatientFileDto(
            new PatientFilePersonDto(person.p.PatientId, person.p.FullName, person.p.FileNumber, person.Relation, person.p.Gender, person.p.BirthDate,
                PatientPortalService.AgeOf(person.p, clock.BaghdadToday()), person.p.IsBanned),
            branches, upcoming, past, visits.Items, prescriptions, orders, clock.UtcNow);
    }

    public static string StatusAr(string status) => status switch
    {
        OrderStatuses.Ordered => "مطلوب",
        OrderStatuses.Paid => "مدفوع وبانتظار التنفيذ",
        OrderStatuses.Deferred => "بانتظار التنفيذ",
        OrderStatuses.SampleCollected => "سُحبت العينة",
        OrderStatuses.InProgress => "قيد العمل",
        OrderStatuses.Resulted or OrderStatuses.Reported => "بانتظار الاعتماد",
        OrderStatuses.Scheduled => "له موعد",
        OrderStatuses.Performed => "نُفذ وبانتظار التقرير",
        OrderStatuses.Released => "النتيجة جاهزة",
        OrderStatuses.Cancelled => "ملغى",
        _ => status,
    };
}
