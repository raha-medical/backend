using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Application.PatientApp;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Clinical;

public record WorklistQuery(string? Status, DateOnly? Date, string? Q, string? Modality, int Page = 1, int PageSize = 20);
public record WorklistItemDto(Guid OrderItemId, Guid PatientId, string PatientName, string FileNumber, string? Gender, int? Age, Guid ServiceId,
    string ServiceName, string? OrderedBy, string? ReferringDoctorName, string Status, string PaymentStatus, string? SampleBarcode, bool HasCritical,
    DateTimeOffset OrderedAt, string? Note, string? Modality, DateTimeOffset? ScheduledAt, string? Room, DateTimeOffset? PerformedAt)
{
    /// <summary>كيف دخل قائمة العمل، ومتى (دورة التحويل، العقد 23).</summary>
    public string? ActivationMethod { get; init; }
    public DateTimeOffset? ActivatedAt { get; init; }
    public Guid? ReferralId { get; init; }
    /// <summary>حاضر لكن غير مدفوع: يظهر للفني بلا إجراء حتى يُدفع أو يُستثنى.</summary>
    public bool AwaitingPayment { get; init; }
}
public record CollectSampleRequest(string? SampleBarcode);
public record CollectSampleResponse(Guid OrderItemId, string Status, string SampleBarcode)
{
    /// <summary>لطباعة الملصق: بتوقيت بغداد.</summary>
    public string? CollectedDateAr { get; init; }
    public string? CollectedTimeAr { get; init; }
}
public record OrderStatusResponse(Guid OrderItemId, string Status);
public record LabParameterDto(string Code, string Name, string? Unit, decimal? RefLow, decimal? RefHigh, decimal? CriticalLow, decimal? CriticalHigh, short SortOrder);
public record LabParameterInput(string? Code, string? Name, string? Unit, decimal? RefLow, decimal? RefHigh, decimal? CriticalLow, decimal? CriticalHigh, short? SortOrder);
public record LabTemplateDto(Guid ServiceId, string ServiceName, string ResultType, IReadOnlyList<LabParameterDto> Parameters);
public record PutLabTemplateRequest(List<LabParameterInput>? Parameters);
public record LabValueInput(string? Code, decimal? Value);
public record AttachmentInput(Guid? FileId);
public record PutLabResultRequest(List<LabValueInput>? Values, string? TextResult, string? Comment, List<AttachmentInput>? Attachments, bool? Release, string? AmendReason, bool? CriticalFlag = null);
public record LabValueDto(string Code, string Name, string? Unit, decimal Value, decimal? RefLow, decimal? RefHigh, string Flag);
public record AttachmentDto(Guid FileId, string FileName, string ContentType, string Url, int ExpiresIn);
public record LabResultDto(Guid OrderItemId, string ServiceName, string Status, PatientSummaryDto Patient, string? OrderedBy, string? SampleBarcode,
    DateTimeOffset? SampleCollectedAt, IReadOnlyList<LabValueDto> Values, string? TextResult, string? Comment, IReadOnlyList<AttachmentDto> Attachments,
    bool HasCritical, DateTimeOffset? ReleasedAt, string? ReleasedBy, bool Amended);
public record ScheduleRequest(DateTimeOffset? ScheduledAt, string? Room);
public record ScheduleResponse(Guid OrderItemId, string Status, DateTimeOffset? ScheduledAt, string? Room);
public record PerformedResponse(Guid OrderItemId, string Status, DateTimeOffset? PerformedAt);
public record PutRadiologyReportRequest(string? Findings, string? Impression, List<AttachmentInput>? Attachments, bool? Release, string? AmendReason, bool? CriticalFlag = null);
public record RadiologyReportDto(Guid OrderItemId, string ServiceName, string? Modality, string Status, PatientSummaryDto Patient, string? OrderedBy,
    DateTimeOffset? ScheduledAt, string? Room, DateTimeOffset? PerformedAt, string? Findings, string? Impression, IReadOnlyList<AttachmentDto> Attachments,
    DateTimeOffset? ReleasedAt, string? ReportedBy, bool Amended);

/// <summary>أدوات مشتركة للمختبر والأشعة: تحميل الطلب، قائمة العمل، المرفقات، وصلاحية عرض النتيجة.</summary>
public class DepartmentCore(IAppDbContext db, ICurrentUser current, IClock clock, IObjectStorage storage, PatientPortalService portal,
    NotificationService notifications, ReferralTracker referrals, SettingsReader settings)
{
    public static readonly string[] OpenLab = [OrderStatuses.Paid, OrderStatuses.Deferred, OrderStatuses.SampleCollected, OrderStatuses.InProgress, OrderStatuses.Resulted];
    public static readonly string[] OpenRadiology = [OrderStatuses.Paid, OrderStatuses.Deferred, OrderStatuses.Scheduled, OrderStatuses.Performed, OrderStatuses.Reported];

    /// <summary>طلب القسم في الفرع الحالي (للفني). غير المدفوع: 402 INVOICE_UNPAID.</summary>
    public async Task<(MedicalOrder Order, InvoiceItem Item)> LoadForWorkAsync(Guid orderItemId, string department, CancellationToken ct)
    {
        var order = await db.MedicalOrders.FirstOrDefaultAsync(o => o.OrderItemId == orderItemId && o.Department == department && o.BranchId == current.BranchId, ct)
                    ?? throw new AppException(404, "ORDER_NOT_FOUND", "الطلب غير موجود");
        var item = await db.InvoiceItems.FirstAsync(i => i.InvoiceItemId == order.InvoiceItemId, ct);
        if (order.Status == OrderStatuses.Ordered)
            throw new AppException(402, "INVOICE_UNPAID", "الطلب غير مدفوع", "يُدفع البند أو يُستثنى أولاً (القسم 8 أو 14)");
        if (order.Status == OrderStatuses.Cancelled)
            throw InvalidState(order, "الطلب ملغى");
        await referrals.ActivateForWorkAsync(order.OrderItemId, ct); // بدء العمل يثبت الحضور
        return (order, item);
    }

    public static AppException InvalidState(MedicalOrder o, string detail) =>
        new(409, "INVALID_ORDER_STATE", "حالة الطلب لا تسمح بهذه العملية", detail)
            { Extensions = new Dictionary<string, object?> { ["current_status"] = o.Status } };

    /// <summary>
    /// شرط الدفع لعرض النتيجة للمريض: مدفوع، أو مؤجل إذا فعّل الفرع results.show_deferred_to_patient.
    /// مكان واحد للتطبيق وقائمة الطلبات والمساعد.
    /// </summary>
    public static bool PatientMayReadResult(string itemStatus, bool showDeferred) =>
        itemStatus is InvoiceItemStatuses.Paid or InvoiceItemStatuses.PartiallyRefunded || (showDeferred && itemStatus == InvoiceItemStatuses.Deferred);

    /// <summary>
    /// قراءة النتيجة: الطبيب (أي فرع)، فني القسم (فرعه)، أو المريض (مربوط بحسابه، نتيجة معتمدة، والبند مدفوع أو مستثنى).
    /// </summary>
    public async Task<(MedicalOrder Order, InvoiceItem Item)> LoadForReadAsync(Guid orderItemId, string department, string workPermission, CancellationToken ct)
    {
        var order = await db.MedicalOrders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderItemId == orderItemId && o.Department == department, ct)
                    ?? throw new AppException(404, "ORDER_NOT_FOUND", "الطلب غير موجود");
        var item = await db.InvoiceItems.AsNoTracking().FirstAsync(i => i.InvoiceItemId == order.InvoiceItemId, ct);
        if (current.IsPatient)
        {
            await portal.EnsureLinkedAsync(order.PatientId, ct);
            if (order.Status != OrderStatuses.Released) throw new AppException(404, "RESULT_NOT_READY", "النتيجة غير جاهزة بعد");
            if (!PatientMayReadResult(item.Status, await settings.GetLongAsync(SettingsReader.ShowDeferredResults, order.BranchId, 0, ct) == 1))
                throw new AppException(402, "INVOICE_UNPAID", "البند غير مدفوع", "ادفع البند لعرض النتيجة (القسم 6)")
                    { Extensions = new Dictionary<string, object?> { ["invoice_item_id"] = item.InvoiceItemId, ["amount"] = item.NetPrice } };
            return (order, item);
        }
        var isDoctor = current.Has(Permissions.ClinicalWrite);
        // من يعمل في القسم أو من يملك رمز العرض (مدير الفرع)، لطلبات فرعه الحالي فقط
        var viewPermission = department == OrderDepartments.Lab ? Permissions.LabView : Permissions.RadiologyView;
        var isTech = (current.Has(workPermission) || current.Has(viewPermission)) && order.BranchId == current.BranchId;
        if (!isDoctor && !isTech) throw new AppException(404, "ORDER_NOT_FOUND", "الطلب غير موجود");
        return (order, item);
    }

    public async Task<PagedResult<WorklistItemDto>> WorklistAsync(string department, WorklistQuery q, CancellationToken ct)
    {
        var open = department == OrderDepartments.Lab ? OpenLab : OpenRadiology;
        var allowed = open.Append(OrderStatuses.Released).Append(OrderStatuses.Ordered).ToArray();
        new Validator()
            .Check(q.Status is null || allowed.Contains(q.Status), "status", string.Join(" | ", allowed))
            .Check(q.Modality is null || Modalities.All.Contains(q.Modality), "modality", string.Join(" | ", Modalities.All))
            .ThrowIfInvalid();
        var page = new PageQuery(q.Page, q.PageSize);
        // بدون date: كل الطلبات المفتوحة مهما كان تاريخها (لا يضيع طلب الأمس)، والمعتمدة لليوم فقط
        var dated = q.Date is not null || q.Status == OrderStatuses.Released;
        var day = q.Date ?? clock.BaghdadToday();
        DateTimeOffset dayStart = ClockExtensions.ToUtc(day, TimeOnly.MinValue), dayEnd = ClockExtensions.ToUtc(day.AddDays(1), TimeOnly.MinValue);
        var query =
            from o in db.MedicalOrders.AsNoTracking()
            join i in db.InvoiceItems.AsNoTracking() on o.InvoiceItemId equals i.InvoiceItemId
            join p in db.Patients.AsNoTracking() on o.PatientId equals p.PatientId
            join s in db.Services.AsNoTracking() on o.ServiceId equals s.ServiceId
            join d in db.Doctors.AsNoTracking() on o.OrderedByDoctorId equals d.DoctorId into dj
            from d in dj.DefaultIfEmpty()
            join r in db.Referrals.AsNoTracking() on o.OrderItemId equals r.OrderItemId into rj
            from r in rj.DefaultIfEmpty()
            where o.BranchId == current.BranchId && o.Department == department
                  // قائمة العمل للمفعّلين فقط، والمنتظر في تبويب التحويلات الواردة (GET /referrals)
                  && (r == null || r.Status == ReferralStatuses.Activated || r.Status == ReferralStatuses.Completed)
                  && (!dated || (q.Status == OrderStatuses.Released ? o.ReleasedAt >= dayStart && o.ReleasedAt < dayEnd
                                 // مواعيد الأشعة: اليوم بموعد التنفيذ لا بوقت الطلب
                                 : q.Status == OrderStatuses.Scheduled ? o.ScheduledAt >= dayStart && o.ScheduledAt < dayEnd
                                 : o.CreatedAt >= dayStart && o.CreatedAt < dayEnd))
            select new { o, i.ServiceName, ItemStatus = i.Status, p, s.Modality, Doctor = d == null ? null : d.FullName, Referral = r };
        // حاضر غير مدفوع يظهر بلا إجراء (العقد 23.3): الفني يعرف أنه وصل ويوجهه للدفع
        query = q.Status is { } st ? query.Where(x => x.o.Status == st) : query.Where(x => open.Contains(x.o.Status) || x.o.Status == OrderStatuses.Ordered);
        if (q.Modality is { } m) query = query.Where(x => x.Modality == m);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            query = query.Where(x => EF.Functions.ILike(x.p.FullName, $"%{term}%") || x.p.FileNumber == term.ToUpper() || x.o.SampleBarcode == term.ToUpper());
        }
        var total = await query.CountAsync(ct);
        var ordered = q.Status == OrderStatuses.Scheduled ? query.OrderBy(x => x.o.ScheduledAt) : query.OrderBy(x => x.o.CreatedAt);
        var rows = await ordered.Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        var today = clock.BaghdadToday();
        return new PagedResult<WorklistItemDto>(rows.Select(x => new WorklistItemDto(x.o.OrderItemId, x.p.PatientId, x.p.FullName, x.p.FileNumber,
            x.p.Gender, PatientPortalService.AgeOf(x.p, today), x.o.ServiceId, x.ServiceName, x.Doctor, x.o.ReferringDoctorName, x.o.Status,
            ConsultationService.PaymentStatusOf(x.ItemStatus), x.o.SampleBarcode, x.o.HasCritical, x.o.CreatedAt, x.o.Note,
            x.Modality, x.o.ScheduledAt, x.o.Room, x.o.PerformedAt)
        {
            ActivationMethod = x.Referral?.ActivationMethod, ActivatedAt = x.Referral?.ActivatedAt, ReferralId = x.Referral?.ReferralId,
            AwaitingPayment = x.o.Status == OrderStatuses.Ordered,
        }).ToList(), page.SafePage, page.SafePageSize, total);
    }

    /// <summary>المرفقات يجب أن تكون ملفات مرفوعة ومكتملة لنفس الطلب.</summary>
    public async Task ValidateAttachmentsAsync(Guid orderItemId, List<AttachmentInput>? attachments, CancellationToken ct)
    {
        var ids = (attachments ?? []).Where(a => a.FileId is not null).Select(a => a.FileId!.Value).Distinct().ToList();
        if (ids.Count == 0) return;
        var ok = await db.Files.Where(f => ids.Contains(f.FileId) && f.OrderItemId == orderItemId && f.Status == FileStatuses.Stored).CountAsync(ct);
        if (ok != ids.Count)
            throw AppException.Validation(new Dictionary<string, string[]> { ["attachments"] = ["ملف غير موجود أو لم يكتمل رفعه أو لا يخص هذا الطلب"] });
    }

    public async Task<List<AttachmentDto>> AttachmentsAsync(Guid orderItemId, CancellationToken ct)
    {
        var files = await db.Files.AsNoTracking().Where(f => f.OrderItemId == orderItemId && f.Status == FileStatuses.Stored)
            .OrderBy(f => f.CreatedAt).ToListAsync(ct);
        var expiry = Files.FileService.DownloadExpiry;
        return files.Select(f => new AttachmentDto(f.FileId, f.FileName, f.ContentType, storage.PresignDownload(f.ObjectKey, expiry), (int)expiry.TotalSeconds)).ToList();
    }

    public async Task<PatientSummaryDto> PatientAsync(Guid patientId, CancellationToken ct)
    {
        var p = await db.Patients.AsNoTracking().FirstAsync(x => x.PatientId == patientId, ct);
        return new PatientSummaryDto(p.PatientId, p.FullName, p.FileNumber, p.Gender, PatientPortalService.AgeOf(p, clock.BaghdadToday()));
    }

    public async Task<string?> DoctorNameAsync(Guid? doctorId, CancellationToken ct) =>
        doctorId is null ? null : await db.Doctors.Where(d => d.DoctorId == doctorId).Select(d => d.FullName).FirstOrDefaultAsync(ct);

    public async Task<string?> UserNameAsync(Guid? userId, CancellationToken ct) =>
        userId is null ? null : await db.Users.Where(u => u.UserId == userId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

    /// <summary>
    /// تنبيه القيمة الحرجة للطبيب المعالج فوراً عند اكتشافها، حتى قبل الاعتماد وبغض النظر عن دفع المريض، بأولوية عالية.
    /// طلب بلا طبيب في النظام (ورقة خارجية): يصل لمدير الفرع. amended: تعديل نتيجة معتمدة يعيد التنبيه.
    /// </summary>
    public async Task NotifyCriticalAsync(MedicalOrder order, string serviceName, string criticalText, bool amended, CancellationToken ct)
    {
        if (order.CriticalNotifiedAt is not null && !amended) return;
        var patientName = await db.Patients.Where(p => p.PatientId == order.PatientId).Select(p => p.FullName).FirstAsync(ct);
        var doctorUser = order.OrderedByDoctorId is null ? null
            : await db.Doctors.Where(d => d.DoctorId == order.OrderedByDoctorId).Select(d => d.UserId).FirstOrDefaultAsync(ct);
        List<Guid> to = doctorUser is { } du ? [du]
            : await db.Users.AsNoTracking().Where(u => u.IsActive && u.Role == Roles.BranchManager && db.UserBranches.Any(b => b.UserId == u.UserId && b.BranchId == order.BranchId))
                .Select(u => u.UserId).ToListAsync(ct);
        var linkType = order.Department == OrderDepartments.Lab ? "lab_order" : "radiology_order";
        await notifications.NotifyUsersAsync(to, order.BranchId, NotificationTypes.LabCriticalValue, "نتيجة حرجة",
            $"{serviceName}: {criticalText} للمريض {patientName}", linkType, order.OrderItemId, ct, NotificationPriorities.High);
        order.CriticalNotifiedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// إشعار الطبيب الطالب والمريض عند الاعتماد. المريض يُخطر بالاكتمال دائماً، ولا تصله القيم،
    /// ويُذكر له المبلغ إن كانت النتيجة مقفلة بشرط الدفع.
    /// </summary>
    public async Task NotifyReleasedAsync(MedicalOrder order, string serviceName, bool amended, CancellationToken ct)
    {
        await referrals.CompleteOrderAsync(order.OrderItemId, ct);
        var lab = order.Department == OrderDepartments.Lab;
        var type = lab ? NotificationTypes.LabResultReady : NotificationTypes.RadiologyReportReady;
        var linkType = lab ? "lab_order" : "radiology_order";
        var patientName = await db.Patients.Where(p => p.PatientId == order.PatientId).Select(p => p.FullName).FirstAsync(ct);
        var doctorUser = order.OrderedByDoctorId is null ? null
            : await db.Doctors.Where(d => d.DoctorId == order.OrderedByDoctorId).Select(d => d.UserId).FirstOrDefaultAsync(ct);
        // صياغة عربية صحيحة: «نتيجة جاهزة/معدّلة» و«تقرير جاهز/معدّل»
        var ready = lab ? "نتيجة جاهزة" : "تقرير جاهز";
        var changed = lab ? "نتيجة معدّلة" : "تقرير معدّل";
        if (doctorUser is { } du)
        {
            await notifications.NotifyUsersAsync([du], order.BranchId, type, amended ? changed : ready,
                $"{serviceName} للمريض {patientName}", linkType, order.OrderItemId, ct);
        }
        if (amended) return;
        var item = await db.InvoiceItems.AsNoTracking().FirstAsync(i => i.InvoiceItemId == order.InvoiceItemId, ct);
        var open = PatientMayReadResult(item.Status, await settings.GetLongAsync(SettingsReader.ShowDeferredResults, order.BranchId, 0, ct) == 1);
        var message = open ? $"{serviceName} - {patientName}"
            : $"{serviceName} - {patientName}. اكتملت النتيجة، وتظهر بعد دفع {item.NetPrice:N0} د.ع من التطبيق أو في استقبال المجمع.";
        await notifications.NotifyPatientAsync(order.PatientId, order.BranchId, type, ready, message, linkType, order.OrderItemId, ct);
    }
}

/// <summary>المختبر: سحب العينة (باركود)، قيد العمل، إدخال النتيجة بالقالب، الاعتماد والتعديل بسبب.</summary>
public class LabService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, NumberSequences numbers, DepartmentCore core)
{
    public Task<PagedResult<WorklistItemDto>> WorklistAsync(WorklistQuery q, CancellationToken ct) =>
        core.WorklistAsync(OrderDepartments.Lab, q with { Modality = null }, ct);

    public async Task<CollectSampleResponse> CollectSampleAsync(Guid orderItemId, CollectSampleRequest req, CancellationToken ct)
    {
        new Validator().Check(req.SampleBarcode is null || req.SampleBarcode.Trim().Length is > 0 and <= 50, "sample_barcode", "50 حرفاً كحد أقصى")
            .ThrowIfInvalid();
        var (order, item) = await core.LoadForWorkAsync(orderItemId, OrderDepartments.Lab, ct);
        if (order.Status == OrderStatuses.SampleCollected && (req.SampleBarcode is null || req.SampleBarcode.Trim().ToUpperInvariant() == order.SampleBarcode))
            return new CollectSampleResponse(order.OrderItemId, order.Status, order.SampleBarcode!)
            {
                CollectedDateAr = order.SampleCollectedAt is null ? null : ArabicFormat.Date(order.SampleCollectedAt.Value),
                CollectedTimeAr = order.SampleCollectedAt is null ? null : ArabicFormat.Time(order.SampleCollectedAt.Value),
            };
        if (order.Status is not (OrderStatuses.Paid or OrderStatuses.Deferred))
            throw DepartmentCore.InvalidState(order, "سُحبت العينة مسبقاً");
        string barcode;
        if (!string.IsNullOrWhiteSpace(req.SampleBarcode))
        {
            barcode = req.SampleBarcode.Trim().ToUpperInvariant();
            if (await db.MedicalOrders.AnyAsync(o => o.SampleBarcode == barcode && o.OrderItemId != order.OrderItemId, ct))
                throw new AppException(409, "DUPLICATE_BARCODE", "الباركود مستخدم لعينة أخرى");
        }
        else
        {
            var today = clock.BaghdadToday();
            do barcode = $"S-{today:yyMMdd}-{await numbers.NextAsync(current.BranchId, "sample", today.ToString("yyMMdd"), ct):D4}";
            while (await db.MedicalOrders.AnyAsync(o => o.SampleBarcode == barcode, ct));
        }
        var now = clock.UtcNow;
        order.Status = OrderStatuses.SampleCollected;
        order.SampleBarcode = barcode;
        order.SampleCollectedAt = now;
        order.CollectedBy = current.UserId;
        item.PerformedAt ??= now;
        audit.Add("SAMPLE_COLLECTED", "medical_order", order.OrderItemId, after: new { barcode });
        await db.SaveChangesAsync(ct);
        return new CollectSampleResponse(order.OrderItemId, order.Status, barcode)
        {
            CollectedDateAr = order.SampleCollectedAt is null ? null : ArabicFormat.Date(order.SampleCollectedAt.Value),
            CollectedTimeAr = order.SampleCollectedAt is null ? null : ArabicFormat.Time(order.SampleCollectedAt.Value),
        };
    }

    public async Task<OrderStatusResponse> StartAsync(Guid orderItemId, CancellationToken ct)
    {
        var (order, _) = await core.LoadForWorkAsync(orderItemId, OrderDepartments.Lab, ct);
        if (order.Status == OrderStatuses.InProgress) return new OrderStatusResponse(order.OrderItemId, order.Status);
        if (order.Status != OrderStatuses.SampleCollected) throw DepartmentCore.InvalidState(order, "اسحب العينة أولاً");
        order.Status = OrderStatuses.InProgress;
        await db.SaveChangesAsync(ct);
        return new OrderStatusResponse(order.OrderItemId, order.Status);
    }

    // ------------------------------------------------------------------ القالب

    public async Task<LabTemplateDto> TemplateAsync(Guid serviceId, CancellationToken ct)
    {
        var s = await db.Services.AsNoTracking().FirstOrDefaultAsync(x => x.ServiceId == serviceId && x.Type == ServiceTypes.Lab, ct)
                ?? throw AppException.NotFound("التحليل غير موجود");
        // عزل الفروع: كتالوج كل فرع خاص به (المدير العام كل الفروع)
        if (current.Role != Roles.Admin && s.BranchId != current.BranchId) throw AppException.NotFound("التحليل غير موجود في هذا الفرع");
        var ps = await db.LabTestParameters.AsNoTracking().Where(p => p.ServiceId == serviceId).OrderBy(p => p.SortOrder).ThenBy(p => p.Code).ToListAsync(ct);
        return new LabTemplateDto(s.ServiceId, s.Name, ps.Count > 0 ? "numeric" : "text",
            ps.Select(p => new LabParameterDto(p.Code, p.Name, p.Unit, p.RefLow, p.RefHigh, p.CriticalLow, p.CriticalHigh, p.SortOrder)).ToList());
    }

    public async Task<LabTemplateDto> PutTemplateAsync(Guid serviceId, PutLabTemplateRequest req, CancellationToken ct)
    {
        var s = await db.Services.AsNoTracking().FirstOrDefaultAsync(x => x.ServiceId == serviceId && x.Type == ServiceTypes.Lab, ct)
                ?? throw AppException.NotFound("التحليل غير موجود");
        if (current.Role != Roles.Admin && s.BranchId != current.BranchId) throw AppException.NotFound("التحليل غير موجود في هذا الفرع");
        var input = req.Parameters ?? [];
        var v = new Validator().Check(input.Count <= 60, "parameters", "60 بارامتراً كحد أقصى");
        for (var i = 0; i < input.Count; i++)
        {
            var p = input[i];
            v.Check(!string.IsNullOrWhiteSpace(p.Code) && p.Code.Trim().Length <= 30, $"parameters[{i}].code", "الرمز مطلوب (30 حرفاً كحد أقصى)");
            v.Check(!string.IsNullOrWhiteSpace(p.Name) && p.Name.Trim().Length <= 100, $"parameters[{i}].name", "الاسم مطلوب (100 حرف كحد أقصى)");
            v.Check(p.Unit is null || p.Unit.Length <= 30, $"parameters[{i}].unit", "30 حرفاً كحد أقصى");
            v.Check(Ordered(p.CriticalLow, p.RefLow, p.RefHigh, p.CriticalHigh), $"parameters[{i}]", "يجب أن تكون critical_low <= ref_low <= ref_high <= critical_high");
        }
        var codes = input.Where(p => p.Code is not null).Select(p => p.Code!.Trim().ToUpperInvariant()).ToList();
        v.Check(codes.Distinct().Count() == codes.Count, "parameters", "الرمز مكرر في القالب");
        v.ThrowIfInvalid();

        var existing = await db.LabTestParameters.Where(p => p.ServiceId == serviceId).ToListAsync(ct);
        var removed = existing.Where(e => !codes.Contains(e.Code.ToUpperInvariant())).ToList();
        var removedIds = removed.Select(r => r.ParameterId).ToList();
        if (removedIds.Count > 0 && await db.LabResultValues.AnyAsync(x => removedIds.Contains(x.ParameterId), ct))
            throw new AppException(409, "TEMPLATE_IN_USE", "لا يُحذف بارامتر له نتائج مسجلة",
                "أبقِ البارامتر في القالب (يمكن تغيير اسمه وحدوده)")
                { Extensions = new Dictionary<string, object?> { ["codes"] = removed.Select(r => r.Code).ToList() } };
        db.LabTestParameters.RemoveRange(removed);
        for (var i = 0; i < input.Count; i++)
        {
            var p = input[i];
            var code = p.Code!.Trim().ToUpperInvariant();
            var row = existing.FirstOrDefault(e => e.Code.ToUpperInvariant() == code);
            if (row is null) { row = new LabTestParameter { ParameterId = UuidV7.New(), ServiceId = serviceId }; db.LabTestParameters.Add(row); }
            row.Code = code;
            row.Name = p.Name!.Trim();
            row.Unit = string.IsNullOrWhiteSpace(p.Unit) ? null : p.Unit.Trim();
            row.RefLow = p.RefLow; row.RefHigh = p.RefHigh; row.CriticalLow = p.CriticalLow; row.CriticalHigh = p.CriticalHigh;
            row.SortOrder = p.SortOrder ?? (short)(i + 1);
        }
        audit.Add("LAB_TEMPLATE_UPDATED", "service", serviceId, after: new { codes });
        await db.SaveChangesAsync(ct);
        return await TemplateAsync(serviceId, ct);
    }

    private static bool Ordered(params decimal?[] values)
    {
        var present = values.Where(x => x is not null).Select(x => x!.Value).ToList();
        for (var i = 1; i < present.Count; i++) if (present[i] < present[i - 1]) return false;
        return true;
    }

    public static string FlagOf(LabTestParameter p, decimal value) =>
        p.CriticalLow is { } cl && value <= cl ? ResultFlags.CriticalLow
        : p.CriticalHigh is { } ch && value >= ch ? ResultFlags.CriticalHigh
        : p.RefLow is { } rl && value < rl ? ResultFlags.Low
        : p.RefHigh is { } rh && value > rh ? ResultFlags.High
        : ResultFlags.Normal;

    // ------------------------------------------------------------------ النتيجة

    public async Task<LabResultDto> PutResultAsync(Guid orderItemId, PutLabResultRequest req, CancellationToken ct)
    {
        // قفل الطلب: فنيان أو حفظ واعتماد بنفس اللحظة يُنفذان بالتتابع، فلا تضيع كتابة ولا يصل الطبيب إشعاران متعارضان
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync($"SELECT 1 FROM medical_orders WHERE order_item_id = {orderItemId} FOR UPDATE", ct);
        var (order, item) = await core.LoadForWorkAsync(orderItemId, OrderDepartments.Lab, ct);
        if (order.Status is OrderStatuses.Paid or OrderStatuses.Deferred) throw DepartmentCore.InvalidState(order, "اسحب العينة أولاً");
        var wasReleased = order.Status == OrderStatuses.Released;
        var release = req.Release == true;
        new Validator()
            .Check(!wasReleased || !string.IsNullOrWhiteSpace(req.AmendReason), "amend_reason", "تعديل نتيجة معتمدة يتطلب سبباً")
            .Check(req.TextResult is null || req.TextResult.Length <= 8000, "text_result", "8000 حرف كحد أقصى")
            .Check(req.Comment is null || req.Comment.Length <= 2000, "comment", "2000 حرف كحد أقصى")
            .ThrowIfInvalid();
        var parameters = await db.LabTestParameters.AsNoTracking().Where(p => p.ServiceId == order.ServiceId).ToListAsync(ct);
        var byCode = parameters.ToDictionary(p => p.Code.ToUpperInvariant());
        var values = req.Values ?? [];
        var v = new Validator();
        for (var i = 0; i < values.Count; i++)
        {
            v.Check(values[i].Code is not null && byCode.ContainsKey(values[i].Code!.Trim().ToUpperInvariant()), $"values[{i}].code", "الرمز غير موجود في قالب التحليل");
            v.Check(values[i].Value is not null, $"values[{i}].value", "القيمة مطلوبة");
        }
        var given = values.Where(x => x.Code is not null).Select(x => x.Code!.Trim().ToUpperInvariant()).ToList();
        v.Check(given.Distinct().Count() == given.Count, "values", "رمز مكرر");
        if (release && parameters.Count > 0)
        {
            var missing = byCode.Keys.Except(given).ToList();
            v.Check(missing.Count == 0, "values", $"قيم ناقصة: {string.Join("، ", missing)}");
        }
        if (release && parameters.Count == 0) v.Check(!string.IsNullOrWhiteSpace(req.TextResult), "text_result", "النتيجة الوصفية مطلوبة للاعتماد");
        v.ThrowIfInvalid();
        await core.ValidateAttachmentsAsync(order.OrderItemId, req.Attachments, ct);

        var now = clock.UtcNow;
        var result = await db.LabResults.Include(r => r.Values).FirstOrDefaultAsync(r => r.OrderItemId == order.OrderItemId, ct);
        var before = result is null ? null : new { result.TextResult, values = result.Values.Select(x => new { x.ParameterId, x.Value }) };
        if (result is null)
        {
            result = new LabResult { OrderItemId = order.OrderItemId };
            db.LabResults.Add(result);
        }
        else
        {
            db.LabResultValues.RemoveRange(result.Values);
            result.Values.Clear();
        }
        result.TextResult = string.IsNullOrWhiteSpace(req.TextResult) ? null : req.TextResult.Trim();
        result.Comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
        result.EnteredBy = current.UserId;
        result.UpdatedAt = now;
        var critical = new List<string>();
        foreach (var x in values)
        {
            var p = byCode[x.Code!.Trim().ToUpperInvariant()];
            var flag = FlagOf(p, x.Value!.Value);
            if (flag is ResultFlags.CriticalLow or ResultFlags.CriticalHigh) critical.Add($"{p.Code} = {x.Value!.Value.Normalize()}");
            result.Values.Add(new LabResultValue { OrderItemId = order.OrderItemId, ParameterId = p.ParameterId, Value = x.Value.Value, Flag = flag });
        }
        if (req.CriticalFlag is { } flagged) order.CriticalFlagged = flagged;
        order.HasCritical = critical.Count > 0 || order.CriticalFlagged;
        if (release || wasReleased)
        {
            order.Status = OrderStatuses.Released;
            order.ReleasedAt = now;
            order.ReleasedBy = current.UserId;
            if (wasReleased) order.AmendedAt = now;
        }
        else order.Status = OrderStatuses.Resulted;
        audit.Add(wasReleased ? "LAB_RESULT_AMENDED" : release ? "LAB_RESULT_RELEASED" : "LAB_RESULT_SAVED", "medical_order", order.OrderItemId,
            before: before, after: new { result.TextResult, values = values.Select(x => new { x.Code, x.Value }), order.HasCritical },
            reason: wasReleased ? req.AmendReason!.Trim() : null);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        // الحرج فوري حتى قبل الاعتماد، والدفع لا يؤثر عليه
        if (order.HasCritical)
            await core.NotifyCriticalAsync(order, item.ServiceName, critical.Count > 0 ? string.Join("، ", critical) : "علامة حرجة من الفني", wasReleased, ct);
        if (order.Status == OrderStatuses.Released)
            await core.NotifyReleasedAsync(order, item.ServiceName, wasReleased, ct);
        return await ResultAsync(order.OrderItemId, ct);
    }

    public async Task<LabResultDto> ResultAsync(Guid orderItemId, CancellationToken ct)
    {
        var (order, item) = await core.LoadForReadAsync(orderItemId, OrderDepartments.Lab, Permissions.LabWrite, ct);
        var result = await db.LabResults.AsNoTracking().Include(r => r.Values).FirstOrDefaultAsync(r => r.OrderItemId == orderItemId, ct);
        var parameters = await db.LabTestParameters.AsNoTracking().Where(p => p.ServiceId == order.ServiceId).ToDictionaryAsync(p => p.ParameterId, ct);
        var values = (result?.Values ?? []).Where(x => parameters.ContainsKey(x.ParameterId)).Select(x => (x, p: parameters[x.ParameterId]))
            .OrderBy(t => t.p.SortOrder).ThenBy(t => t.p.Code)
            .Select(t => new LabValueDto(t.p.Code, t.p.Name, t.p.Unit, t.x.Value.Normalize(), t.p.RefLow, t.p.RefHigh, t.x.Flag)).ToList();
        return new LabResultDto(order.OrderItemId, item.ServiceName, order.Status, await core.PatientAsync(order.PatientId, ct),
            await core.DoctorNameAsync(order.OrderedByDoctorId, ct), order.SampleBarcode, order.SampleCollectedAt, values, result?.TextResult,
            result?.Comment, await core.AttachmentsAsync(order.OrderItemId, ct), order.HasCritical, order.ReleasedAt,
            await core.UserNameAsync(order.ReleasedBy, ct), order.AmendedAt is not null);
    }
}

/// <summary>الأشعة والسونار: الجدولة (اختيارية)، التنفيذ، التقرير والاعتماد والتعديل بسبب.</summary>
public class RadiologyService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, DepartmentCore core)
{
    public Task<PagedResult<WorklistItemDto>> WorklistAsync(WorklistQuery q, CancellationToken ct) =>
        core.WorklistAsync(OrderDepartments.Radiology, q, ct);

    public async Task<ScheduleResponse> ScheduleAsync(Guid orderItemId, ScheduleRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.ScheduledAt is not null, "scheduled_at", "موعد التنفيذ مطلوب")
            .Check(req.ScheduledAt is null || req.ScheduledAt > clock.UtcNow.AddHours(-1), "scheduled_at", "الموعد في الماضي")
            .Check(req.Room is null || req.Room.Trim().Length <= 30, "room", "30 حرفاً كحد أقصى")
            .ThrowIfInvalid();
        var (order, _) = await core.LoadForWorkAsync(orderItemId, OrderDepartments.Radiology, ct);
        if (order.Status is not (OrderStatuses.Paid or OrderStatuses.Deferred or OrderStatuses.Scheduled))
            throw DepartmentCore.InvalidState(order, "نُفذت الأشعة مسبقاً");
        order.Status = OrderStatuses.Scheduled;
        order.ScheduledAt = req.ScheduledAt!.Value.ToUniversalTime();
        order.Room = string.IsNullOrWhiteSpace(req.Room) ? null : req.Room.Trim();
        audit.Add("RADIOLOGY_SCHEDULED", "medical_order", order.OrderItemId, after: new { order.ScheduledAt, order.Room });
        await db.SaveChangesAsync(ct);
        return new ScheduleResponse(order.OrderItemId, order.Status, order.ScheduledAt, order.Room);
    }

    public async Task<PerformedResponse> PerformedAsync(Guid orderItemId, CancellationToken ct)
    {
        var (order, item) = await core.LoadForWorkAsync(orderItemId, OrderDepartments.Radiology, ct);
        if (order.Status == OrderStatuses.Performed) return new PerformedResponse(order.OrderItemId, order.Status, order.PerformedAt);
        if (order.Status is not (OrderStatuses.Paid or OrderStatuses.Deferred or OrderStatuses.Scheduled))
            throw DepartmentCore.InvalidState(order, "نُفذت الأشعة مسبقاً");
        var now = clock.UtcNow;
        order.Status = OrderStatuses.Performed;
        order.PerformedAt = now;
        item.PerformedAt ??= now;
        audit.Add("RADIOLOGY_PERFORMED", "medical_order", order.OrderItemId);
        await db.SaveChangesAsync(ct);
        return new PerformedResponse(order.OrderItemId, order.Status, order.PerformedAt);
    }

    public async Task<RadiologyReportDto> PutReportAsync(Guid orderItemId, PutRadiologyReportRequest req, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync($"SELECT 1 FROM medical_orders WHERE order_item_id = {orderItemId} FOR UPDATE", ct);
        var (order, item) = await core.LoadForWorkAsync(orderItemId, OrderDepartments.Radiology, ct);
        if (order.Status is not (OrderStatuses.Performed or OrderStatuses.Reported or OrderStatuses.Released))
            throw DepartmentCore.InvalidState(order, "سجّل تنفيذ الأشعة أولاً");
        var wasReleased = order.Status == OrderStatuses.Released;
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(req.Findings) && req.Findings.Length <= 16000, "findings", "النتائج مطلوبة (16000 حرف كحد أقصى)")
            .Check(req.Impression is null || req.Impression.Length <= 4000, "impression", "4000 حرف كحد أقصى")
            .Check(!wasReleased || !string.IsNullOrWhiteSpace(req.AmendReason), "amend_reason", "تعديل تقرير معتمد يتطلب سبباً")
            .ThrowIfInvalid();
        await core.ValidateAttachmentsAsync(order.OrderItemId, req.Attachments, ct);
        var now = clock.UtcNow;
        var report = await db.RadiologyReports.FirstOrDefaultAsync(r => r.OrderItemId == order.OrderItemId, ct);
        var before = report is null ? null : new { report.Findings, report.Impression };
        if (report is null) { report = new RadiologyReport { OrderItemId = order.OrderItemId }; db.RadiologyReports.Add(report); }
        report.Findings = req.Findings!.Trim();
        report.Impression = string.IsNullOrWhiteSpace(req.Impression) ? null : req.Impression.Trim();
        report.ReportedBy = current.UserId;
        report.UpdatedAt = now;
        if (req.Release == true || wasReleased)
        {
            order.Status = OrderStatuses.Released;
            order.ReleasedAt = now;
            order.ReleasedBy = current.UserId;
            if (wasReleased) order.AmendedAt = now;
        }
        else order.Status = OrderStatuses.Reported;
        if (req.CriticalFlag is { } flagged) { order.CriticalFlagged = flagged; order.HasCritical = flagged; }
        audit.Add(wasReleased ? "RADIOLOGY_REPORT_AMENDED" : req.Release == true ? "RADIOLOGY_REPORT_RELEASED" : "RADIOLOGY_REPORT_SAVED",
            "medical_order", order.OrderItemId, before: before, after: new { report.Findings, report.Impression },
            reason: wasReleased ? req.AmendReason!.Trim() : null);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        if (order.HasCritical) await core.NotifyCriticalAsync(order, item.ServiceName, "علامة حرجة من أخصائي الأشعة", wasReleased, ct);
        if (order.Status == OrderStatuses.Released) await core.NotifyReleasedAsync(order, item.ServiceName, wasReleased, ct);
        return await ReportAsync(order.OrderItemId, ct);
    }

    public async Task<RadiologyReportDto> ReportAsync(Guid orderItemId, CancellationToken ct)
    {
        var (order, item) = await core.LoadForReadAsync(orderItemId, OrderDepartments.Radiology, Permissions.RadiologyWrite, ct);
        var report = await db.RadiologyReports.AsNoTracking().FirstOrDefaultAsync(r => r.OrderItemId == orderItemId, ct);
        var modality = await db.Services.Where(s => s.ServiceId == order.ServiceId).Select(s => s.Modality).FirstOrDefaultAsync(ct);
        return new RadiologyReportDto(order.OrderItemId, item.ServiceName, modality, order.Status, await core.PatientAsync(order.PatientId, ct),
            await core.DoctorNameAsync(order.OrderedByDoctorId, ct), order.ScheduledAt, order.Room, order.PerformedAt, report?.Findings, report?.Impression,
            await core.AttachmentsAsync(order.OrderItemId, ct), order.ReleasedAt, await core.UserNameAsync(report?.ReportedBy, ct), order.AmendedAt is not null);
    }
}

internal static class DecimalExtensions
{
    /// <summary>يحذف الأصفار الزائدة (7.5000 → 7.5) لعرض القيم كما أُدخلت.</summary>
    public static decimal Normalize(this decimal value) => value / 1.0000000000000000000000000000m;
}
