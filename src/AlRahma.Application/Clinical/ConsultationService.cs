using System.Security.Cryptography;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.PatientApp;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Clinical;

public record PrescriptionLineInput(string? DrugName, string? Dose, string? Frequency, short? DurationDays, string? Instructions);
public record CreateConsultationRequest(Guid? ConsultationId, Guid? TicketId, string? Diagnosis, string? Icd10Code, string? ClinicalNotes,
    string? PrivateNotes, List<PrescriptionLineInput>? Prescription, List<OrderLineInput>? LabOrders, List<OrderLineInput>? RadiologyOrders,
    short? FollowUpAfterDays, bool? FinishVisit = null, bool? CallNext = null);
/// <param name="FinishVisit">"حفظ وإنهاء": يحفظ ثم ينهي المعاينة في الطابور (نفس POST /queue/tickets/{id}/complete).</param>
/// <param name="CallNext">مع finish_visit: استدعاء المريض التالي. null = إعداد الفرع queue.auto_call_next.</param>
public record UpdateConsultationRequest(string? Diagnosis, string? Icd10Code, string? ClinicalNotes, string? PrivateNotes, short? FollowUpAfterDays,
    List<PrescriptionLineInput>? Prescription, bool? FinishVisit = null, bool? CallNext = null);
public record AddOrdersRequest(List<OrderLineInput>? LabOrders, List<OrderLineInput>? RadiologyOrders);
public record AddAddendumRequest(string? Text);

public record PatientSummaryDto(Guid PatientId, string FullName, string FileNumber, string? Gender, int? Age);
public record PrescriptionLineDto(short LineNo, string DrugName, string? Dose, string? Frequency, short? DurationDays, string? Instructions);
public record ConsultationPrescriptionDto(Guid PrescriptionId, string PrescriptionCode, string Status, DateTimeOffset ExpiresAt, IReadOnlyList<PrescriptionLineDto> Lines);
public record ConsultationOrderDto(Guid OrderItemId, string Department, Guid ServiceId, string ServiceName, string? Note, string Status,
    string PaymentStatus, Guid InvoiceItemId, long Price, bool HasCritical, bool ResultAvailable);
public record AddendumDto(Guid AddendumId, string Text, string CreatedBy, DateTimeOffset CreatedAt);
public record ConsultationDto(Guid ConsultationId, Guid TicketId, string TicketNumber, PatientSummaryDto Patient, Guid DoctorId, string DoctorName,
    string ClinicName, string? VisitKind, string? Diagnosis, string? Icd10Code, string? ClinicalNotes, string? PrivateNotes, short? FollowUpAfterDays,
    DateOnly? FollowUpDate, ConsultationPrescriptionDto? Prescription, IReadOnlyList<ConsultationOrderDto> Orders, IReadOnlyList<AddendumDto> Addenda,
    DateTimeOffset EditableUntil, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>عند finish_visit: نتيجة إنهاء المعاينة في الطابور، ومعها next_ticket إذا استُدعي التالي.</summary>
    public Queue.TicketActionResponse? Queue { get; init; }
}

public record HistoryPrescriptionDto(string PrescriptionCode, string Status, IReadOnlyList<PrescriptionLineDto> Lines);
public record HistoryOrderDto(Guid OrderItemId, string Department, string ServiceName, string Status, bool HasCritical, bool ResultAvailable);
public record HistoryItemDto(Guid ConsultationId, DateOnly Date, string BranchName, string DoctorName, string ClinicName, string? VisitKind,
    string? Diagnosis, string? Icd10Code, string? ClinicalNotes, string? PrivateNotes, HistoryPrescriptionDto? Prescription,
    IReadOnlyList<HistoryOrderDto> Orders, int AddendaCount);
public record PatientHistoryDto(Guid PatientId, IReadOnlyList<HistoryItemDto> Items, int Page, int PageSize, int Total);

public record VisitPrescriptionDto(string PrescriptionCode, string Status, DateTimeOffset ExpiresAt, IReadOnlyList<PrescriptionLineDto> Lines);
public record VisitOrderDto(Guid OrderItemId, string Department, string ServiceName, string Status, bool ResultAvailable, bool PaymentRequired);
public record VisitDto(Guid ConsultationId, DateOnly Date, string DoctorName, string ClinicName, string? Diagnosis, DateOnly? FollowUpDate,
    VisitPrescriptionDto? Prescription, IReadOnlyList<VisitOrderDto> Orders)
{
    public string? BranchName { get; init; }
}

/// <summary>
/// المعاينة والسجل الطبي: التشخيص والوصفة والطلبات (تنشئ بنوداً غير مدفوعة في سلة المريض).
/// التعديل لصاحب المعاينة خلال نافذة (consultation.edit_window_hours)، وبعدها ملحق فقط. private_notes للأطباء فقط.
/// </summary>
public class ConsultationService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, SettingsReader settings,
    OrderBilling billing, MedicalOrderSync sync, PatientPortalService portal, Queue.QueueService queue, ReferralTracker referrals)
{
    public const int MaxPrescriptionLines = 30;
    public const int MaxOrders = 30;

    // ------------------------------------------------------------------ الطبيب

    private async Task<Doctor> CurrentDoctorAsync(CancellationToken ct) =>
        await db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.UserId == current.UserId, ct)
        ?? throw AppException.Forbidden("الحساب غير مربوط بملف طبيب");

    public async Task<ConsultationDto> CreateAsync(CreateConsultationRequest req, CancellationToken ct)
    {
        new Validator().Check(req.TicketId is not null, "ticket_id", "التذكرة مطلوبة")
            .Check(req.CallNext is null || req.FinishVisit == true, "call_next", "يُستخدم مع finish_visit فقط").ThrowIfInvalid();
        ValidateText(req.Diagnosis, req.Icd10Code, req.ClinicalNotes, req.PrivateNotes, req.FollowUpAfterDays, req.Prescription);
        var doctor = await CurrentDoctorAsync(ct);
        var ticket = await db.QueueTickets.AsNoTracking().FirstOrDefaultAsync(t => t.TicketId == req.TicketId, ct)
                     ?? throw new AppException(404, "TICKET_NOT_FOUND", "التذكرة غير موجودة");
        var existing = await db.Consultations.AsNoTracking().Where(c => c.TicketId == ticket.TicketId).Select(c => (Guid?)c.ConsultationId).FirstOrDefaultAsync(ct);
        if (existing is { } eid)
            throw new AppException(409, "CONSULTATION_EXISTS", "للتذكرة معاينة مسبقاً", "عدّلها بـ PATCH")
                { Extensions = new Dictionary<string, object?> { ["consultation_id"] = eid } };
        if (ticket.DoctorId != doctor.DoctorId || ticket.Status is not (TicketStatuses.InRoom or TicketStatuses.Completed))
            throw new AppException(409, "INVALID_TICKET_STATE", "المريض ليس داخل غرفتك", "ابدأ المعاينة من الطابور أولاً")
                { Extensions = new Dictionary<string, object?> { ["current_status"] = ticket.Status } };
        if (req.CallNext == true) await queue.EnsureCallAllowedAsync(ticket.BranchId, ct);
        var lines = await ResolveOrdersAsync(ticket.BranchId, req.LabOrders, req.RadiologyOrders, ct);

        var id = req.ConsultationId ?? UuidV7.New();
        if (await db.Consultations.AnyAsync(c => c.ConsultationId == id, ct))
            throw new AppException(409, "CONSULTATION_EXISTS", "يوجد معاينة بنفس المعرف")
                { Extensions = new Dictionary<string, object?> { ["consultation_id"] = id } };
        var now = clock.UtcNow;
        var c = new Consultation
        {
            ConsultationId = id, TicketId = ticket.TicketId, PatientId = ticket.PatientId, DoctorId = doctor.DoctorId,
            Diagnosis = Clean(req.Diagnosis), Icd10Code = Clean(req.Icd10Code)?.ToUpperInvariant(), ClinicalNotes = Clean(req.ClinicalNotes),
            PrivateNotes = Clean(req.PrivateNotes), FollowUpAfterDays = req.FollowUpAfterDays, CreatedAt = now, UpdatedAt = now,
        };
        db.Consultations.Add(c);
        var newRx = req.Prescription is { Count: > 0 } rx ? await AddPrescriptionAsync(c, rx, ticket.BranchId, ct) : null;
        var orders = await billing.CreateAsync(ticket.BranchId, c.PatientId, doctor.DoctorId, c.ConsultationId, null, lines, ct);
        audit.Add("CONSULTATION_CREATED", "consultation", id, after: new
        {
            ticket_id = ticket.TicketId, c.Diagnosis, c.Icd10Code, prescription_lines = req.Prescription?.Count ?? 0, orders = orders.Count,
        });
        await db.SaveChangesAsync(ct);
        await sync.SyncAsync(orders.Where(o => o.Status == OrderStatuses.Paid).Select(o => o.InvoiceItemId).ToArray(), ct);
        await referrals.NotifyCreatedAsync(c.PatientId, orders.Select(o => o.OrderItemId).ToArray(), newRx?.PrescriptionId, ct);
        return await FinishAsync(await GetAsync(id, ct), req.FinishVisit, req.CallNext, ct);
    }

    /// <summary>"حفظ وإنهاء": ينهي المعاينة في الطابور إذا كان المريض ما زال في الغرفة، ويستدعي التالي حسب call_next أو إعداد الفرع.</summary>
    private async Task<ConsultationDto> FinishAsync(ConsultationDto dto, bool? finish, bool? callNext, CancellationToken ct)
    {
        if (finish != true) return dto;
        var status = await db.QueueTickets.Where(t => t.TicketId == dto.TicketId).Select(t => t.Status).FirstAsync(ct);
        if (status != TicketStatuses.InRoom) return dto;
        return dto with { Queue = await queue.CompleteAsync(dto.TicketId, new Queue.CompleteTicketRequest(callNext), ct) };
    }

    public async Task<ConsultationDto> GetAsync(Guid consultationId, CancellationToken ct)
    {
        var c = await db.Consultations.AsNoTracking().FirstOrDefaultAsync(x => x.ConsultationId == consultationId, ct)
                ?? throw new AppException(404, "CONSULTATION_NOT_FOUND", "المعاينة غير موجودة");
        return (await ToDtosAsync([c], ct))[0];
    }

    public async Task<PagedResult<ConsultationDto>> ListAsync(Guid? ticketId, Guid? patientId, int page, int pageSize, CancellationToken ct)
    {
        if (ticketId is null && patientId is null)
            throw AppException.Validation(new Dictionary<string, string[]> { ["ticket_id"] = ["أرسل ticket_id أو patient_id"] });
        var p = new PageQuery(page, pageSize);
        var q = db.Consultations.AsNoTracking();
        if (ticketId is { } t) q = q.Where(c => c.TicketId == t);
        if (patientId is { } pid) q = q.Where(c => c.PatientId == pid);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(c => c.CreatedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        return new PagedResult<ConsultationDto>(await ToDtosAsync(rows, ct), p.SafePage, p.SafePageSize, total);
    }

    public async Task<ConsultationDto> UpdateAsync(Guid consultationId, UpdateConsultationRequest req, CancellationToken ct)
    {
        ValidateText(req.Diagnosis, req.Icd10Code, req.ClinicalNotes, req.PrivateNotes, req.FollowUpAfterDays, req.Prescription);
        new Validator().Check(req.CallNext is null || req.FinishVisit == true, "call_next", "يُستخدم مع finish_visit فقط").ThrowIfInvalid();
        var (c, branchId) = await LoadOwnAsync(consultationId, requireWindow: true, ct);
        if (req.CallNext == true) await queue.EnsureCallAllowedAsync(branchId, ct);
        var before = new { c.Diagnosis, c.Icd10Code, c.ClinicalNotes, c.FollowUpAfterDays };
        if (req.Diagnosis is not null) c.Diagnosis = Clean(req.Diagnosis);
        if (req.Icd10Code is not null) c.Icd10Code = Clean(req.Icd10Code)?.ToUpperInvariant();
        if (req.ClinicalNotes is not null) c.ClinicalNotes = Clean(req.ClinicalNotes);
        if (req.PrivateNotes is not null) c.PrivateNotes = Clean(req.PrivateNotes);
        if (req.FollowUpAfterDays is not null) c.FollowUpAfterDays = req.FollowUpAfterDays == 0 ? null : req.FollowUpAfterDays;
        if (req.Prescription is not null)
        {
            var rx = await db.Prescriptions.Include(p => p.Lines).FirstOrDefaultAsync(p => p.ConsultationId == c.ConsultationId, ct);
            if (rx is not null && rx.Status == PrescriptionStatuses.Dispensed)
                throw new AppException(409, "PRESCRIPTION_ALREADY_DISPENSED", "صُرفت الوصفة ولا يمكن تعديلها", "أضف ملحقاً");
            if (rx is not null)
            {
                db.PrescriptionLines.RemoveRange(rx.Lines);
                rx.Lines.Clear();
                if (req.Prescription.Count == 0) db.Prescriptions.Remove(rx);
                else rx.Lines.AddRange(Lines(rx.PrescriptionId, req.Prescription));
            }
            else if (req.Prescription.Count > 0) await AddPrescriptionAsync(c, req.Prescription, branchId, ct);
        }
        c.UpdatedAt = clock.UtcNow;
        audit.Add("CONSULTATION_UPDATED", "consultation", c.ConsultationId, before: before,
            after: new { c.Diagnosis, c.Icd10Code, c.ClinicalNotes, c.FollowUpAfterDays, prescription_lines = req.Prescription?.Count });
        await db.SaveChangesAsync(ct);
        return await FinishAsync(await GetAsync(c.ConsultationId, ct), req.FinishVisit, req.CallNext, ct);
    }

    public async Task<ConsultationDto> AddOrdersAsync(Guid consultationId, AddOrdersRequest req, CancellationToken ct)
    {
        var (c, branchId) = await LoadOwnAsync(consultationId, requireWindow: true, ct);
        var lines = await ResolveOrdersAsync(branchId, req.LabOrders, req.RadiologyOrders, ct);
        if (lines.Count == 0) throw AppException.Validation(new Dictionary<string, string[]> { ["lab_orders"] = ["أضف طلباً واحداً على الأقل"] });
        var orders = await billing.CreateAsync(branchId, c.PatientId, c.DoctorId, c.ConsultationId, null, lines, ct);
        c.UpdatedAt = clock.UtcNow;
        audit.Add("ORDERS_ADDED", "consultation", c.ConsultationId, after: new { orders = orders.Select(o => new { o.OrderItemId, o.ServiceId }) });
        await db.SaveChangesAsync(ct);
        await sync.SyncAsync(orders.Where(o => o.Status == OrderStatuses.Paid).Select(o => o.InvoiceItemId).ToArray(), ct);
        await referrals.NotifyCreatedAsync(c.PatientId, orders.Select(o => o.OrderItemId).ToArray(), null, ct);
        return await GetAsync(c.ConsultationId, ct);
    }

    public async Task<ConsultationDto> AddAddendumAsync(Guid consultationId, AddAddendumRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Text) && req.Text.Length <= 4000, "text", "نص الملحق مطلوب (4000 حرف كحد أقصى)").ThrowIfInvalid();
        var (c, _) = await LoadOwnAsync(consultationId, requireWindow: false, ct);
        db.ConsultationAddenda.Add(new ConsultationAddendum
        {
            AddendumId = UuidV7.New(), ConsultationId = c.ConsultationId, Text = req.Text!.Trim(), CreatedBy = current.UserId, CreatedAt = clock.UtcNow,
        });
        audit.Add("CONSULTATION_ADDENDUM", "consultation", c.ConsultationId, after: new { text = req.Text!.Trim() });
        await db.SaveChangesAsync(ct);
        return await GetAsync(c.ConsultationId, ct);
    }

    // ------------------------------------------------------------------ الطلب المباشر (الاستقبال) والإلغاء

    public async Task<DirectOrderResponse> CreateDirectOrdersAsync(DirectOrderRequest req, CancellationToken ct)
    {
        var items = req.Items ?? [];
        new Validator()
            .Check(req.PatientId is not null, "patient_id", "المريض مطلوب")
            .Check(items.Count is > 0 and <= MaxOrders, "items", $"من 1 إلى {MaxOrders} طلباً")
            .Check(req.ReferringDoctorName is null || req.ReferringDoctorName.Length <= 150, "referring_doctor_name", "150 حرفاً كحد أقصى")
            .ThrowIfInvalid();
        var patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.PatientId == req.PatientId, ct)
                      ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
        if (patient.MergedIntoPatientId is not null) throw new AppException(409, "PATIENT_MERGED", "هذا الملف مدموج في ملف آخر");
        var lines = await billing.ResolveAsync(current.BranchId, items.Select((x, i) => ($"items[{i}]", x, (string?)null)).ToList(), ct);
        var orders = await billing.CreateAsync(current.BranchId, patient.PatientId, null, null, Clean(req.ReferringDoctorName), lines, ct);
        audit.Add("DIRECT_ORDERS_CREATED", "patient", patient.PatientId, after: new
        {
            orders = orders.Select(o => new { o.OrderItemId, o.ServiceId }), referring_doctor = req.ReferringDoctorName,
        });
        await db.SaveChangesAsync(ct);
        await sync.SyncAsync(orders.Where(o => o.Status == OrderStatuses.Paid).Select(o => o.InvoiceItemId).ToArray(), ct);
        await referrals.NotifyCreatedAsync(patient.PatientId, orders.Select(o => o.OrderItemId).ToArray(), null, ct);
        var prices = lines.ToDictionary(l => l.Service.ServiceId, l => l.Service);
        return new DirectOrderResponse(orders.Select(o => new DirectOrderItemDto(o.OrderItemId, o.Department, prices[o.ServiceId].Name, o.InvoiceItemId,
            prices[o.ServiceId].Price, o.Status)).ToList());
    }

    public async Task<CancelOrderResponse> CancelOrderAsync(Guid orderItemId, CancelOrderRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Reason), "reason", "سبب الإلغاء مطلوب").ThrowIfInvalid();
        var order = await db.MedicalOrders.FirstOrDefaultAsync(o => o.OrderItemId == orderItemId, ct)
                    ?? throw new AppException(404, "ORDER_NOT_FOUND", "الطلب غير موجود");
        var isReception = current.Has(Permissions.PatientsWrite) || current.Has(Permissions.PaymentsCollect);
        var doctorId = await db.Doctors.Where(d => d.UserId == current.UserId).Select(d => (Guid?)d.DoctorId).FirstOrDefaultAsync(ct);
        var isOrderingDoctor = doctorId is not null && order.OrderedByDoctorId == doctorId;
        if (!isOrderingDoctor && !(isReception && order.BranchId == current.BranchId))
            throw new AppException(404, "ORDER_NOT_FOUND", "الطلب غير موجود");
        if (order.Status == OrderStatuses.Cancelled) return new CancelOrderResponse(order.OrderItemId, order.Status);
        if (order.Status != OrderStatuses.Ordered)
            throw new AppException(409, "INVALID_ORDER_STATE", "الطلب مدفوع أو قيد التنفيذ", "البند المدفوع يُسترجع من الفاتورة (القسم 8)")
                { Extensions = new Dictionary<string, object?> { ["current_status"] = order.Status } };
        var invoice = await db.Invoices.Include(i => i.Items).FirstAsync(i => i.Items.Any(x => x.InvoiceItemId == order.InvoiceItemId), ct);
        var item = invoice.Items.First(i => i.InvoiceItemId == order.InvoiceItemId);
        if (item.Status != InvoiceItemStatuses.Unpaid)
            throw new AppException(409, "INVALID_ORDER_STATE", "البند مدفوع أو مستثنى", "البند المدفوع يُسترجع من الفاتورة (القسم 8)");
        item.Status = InvoiceItemStatuses.Void;
        Billing.FinanceCore.RefreshInvoice(invoice);
        if (invoice.Status == InvoiceStatuses.Void)
        {
            invoice.VoidReason = req.Reason!.Trim();
            invoice.VoidedBy = current.StaffUserId;
            invoice.VoidedAt = clock.UtcNow;
        }
        order.Status = OrderStatuses.Cancelled;
        order.CancelReason = req.Reason!.Trim();
        audit.Add("ORDER_CANCELLED", "medical_order", order.OrderItemId, after: new { order.Status }, reason: order.CancelReason);
        await db.SaveChangesAsync(ct);
        await referrals.CancelByOrdersAsync([order.OrderItemId], ct);
        return new CancelOrderResponse(order.OrderItemId, order.Status);
    }

    // ------------------------------------------------------------------ السجل

    public async Task<PatientHistoryDto> HistoryAsync(Guid patientId, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.PatientId == patientId, ct)) throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
        var p = new PageQuery(page, pageSize);
        var q = db.Consultations.AsNoTracking().Where(c => c.PatientId == patientId);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(c => c.CreatedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        var dtos = await ToDtosAsync(rows, ct);
        var ctx = await ContextAsync(rows, ct);
        var items = dtos.Select(d => new HistoryItemDto(d.ConsultationId, Day(d.CreatedAt), ctx.BranchNames.GetValueOrDefault(d.TicketId, ""), d.DoctorName,
            d.ClinicName, d.VisitKind, d.Diagnosis, d.Icd10Code, d.ClinicalNotes, d.PrivateNotes,
            d.Prescription is null ? null : new HistoryPrescriptionDto(d.Prescription.PrescriptionCode, d.Prescription.Status, d.Prescription.Lines),
            d.Orders.Select(o => new HistoryOrderDto(o.OrderItemId, o.Department, o.ServiceName, o.Status, o.HasCritical, o.ResultAvailable)).ToList(),
            d.Addenda.Count)).ToList();
        return new PatientHistoryDto(patientId, items, p.SafePage, p.SafePageSize, total);
    }

    /// <summary>زيارات المريض في التطبيق: بلا الملاحظات السريرية والخاصة ورمز ICD.</summary>
    public async Task<PagedResult<VisitDto>> MyVisitsAsync(Guid patientId, int page, int pageSize, CancellationToken ct)
    {
        await portal.EnsureLinkedAsync(patientId, ct);
        var p = new PageQuery(page, pageSize);
        var q = db.Consultations.AsNoTracking().Where(c => c.PatientId == patientId);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(c => c.CreatedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        var dtos = await ToDtosAsync(rows, ct);
        var ticketIds = rows.Select(c => c.TicketId).ToList();
        var branchByConsultation = await (
            from c in db.Consultations.AsNoTracking()
            join t in db.QueueTickets.AsNoTracking() on c.TicketId equals t.TicketId
            join b in db.Branches.AsNoTracking() on t.BranchId equals b.BranchId
            where ticketIds.Contains(c.TicketId)
            select new { c.ConsultationId, b.Name }).ToDictionaryAsync(x => x.ConsultationId, x => x.Name, ct);
        var showDeferred = new Dictionary<Guid, bool>();
        foreach (var d in dtos)
        {
            var branch = await db.Consultations.AsNoTracking().Where(c => c.ConsultationId == d.ConsultationId)
                .Join(db.QueueTickets.AsNoTracking(), c => c.TicketId, t => t.TicketId, (c, t) => t.BranchId).FirstAsync(ct);
            showDeferred[d.ConsultationId] = await settings.GetLongAsync(SettingsReader.ShowDeferredResults, branch, 0, ct) == 1;
        }
        return new PagedResult<VisitDto>(dtos.Select(d => new VisitDto(d.ConsultationId, Day(d.CreatedAt), d.DoctorName, d.ClinicName, d.Diagnosis,
            d.FollowUpDate,
            d.Prescription is null ? null : new VisitPrescriptionDto(d.Prescription.PrescriptionCode, d.Prescription.Status, d.Prescription.ExpiresAt, d.Prescription.Lines),
            d.Orders.Where(o => o.Status != OrderStatuses.Cancelled).Select(o => new VisitOrderDto(o.OrderItemId, o.Department, o.ServiceName, o.Status,
                o.ResultAvailable && (o.PaymentStatus is "paid" or "refunded" || (o.PaymentStatus == "deferred" && showDeferred.GetValueOrDefault(d.ConsultationId))),
                o.PaymentStatus == "unpaid" || (o.PaymentStatus == "deferred" && o.ResultAvailable && !showDeferred.GetValueOrDefault(d.ConsultationId)))).ToList())
        { BranchName = branchByConsultation.GetValueOrDefault(d.ConsultationId) }).ToList(), p.SafePage, p.SafePageSize, total);
    }

    // ------------------------------------------------------------------ أدوات

    private async Task<(Consultation C, Guid BranchId)> LoadOwnAsync(Guid consultationId, bool requireWindow, CancellationToken ct)
    {
        var c = await db.Consultations.FirstOrDefaultAsync(x => x.ConsultationId == consultationId, ct)
                ?? throw new AppException(404, "CONSULTATION_NOT_FOUND", "المعاينة غير موجودة");
        var doctor = await CurrentDoctorAsync(ct);
        if (c.DoctorId != doctor.DoctorId) throw AppException.Forbidden("ليست معاينتك");
        var branchId = await db.QueueTickets.Where(t => t.TicketId == c.TicketId).Select(t => t.BranchId).FirstAsync(ct);
        if (requireWindow && clock.UtcNow > await EditableUntilAsync(c, branchId, ct))
            throw new AppException(409, "EDIT_WINDOW_CLOSED", "انتهت فترة التعديل", "أضف ملحقاً للمعاينة");
        return (c, branchId);
    }

    private async Task<DateTimeOffset> EditableUntilAsync(Consultation c, Guid branchId, CancellationToken ct) =>
        c.CreatedAt.AddHours(await settings.GetLongAsync(SettingsReader.ConsultationEditHours, branchId, 24, ct));

    private async Task<List<OrderBilling.Line>> ResolveOrdersAsync(Guid branchId, List<OrderLineInput>? lab, List<OrderLineInput>? rad, CancellationToken ct)
    {
        var inputs = (lab ?? []).Select((x, i) => ($"lab_orders[{i}]", x, (string?)ServiceTypes.Lab))
            .Concat((rad ?? []).Select((x, i) => ($"radiology_orders[{i}]", x, (string?)ServiceTypes.Radiology))).ToList();
        if (inputs.Count > MaxOrders) throw AppException.Validation(new Dictionary<string, string[]> { ["lab_orders"] = [$"{MaxOrders} طلباً كحد أقصى"] });
        return await billing.ResolveAsync(branchId, inputs, ct);
    }

    private async Task<Prescription> AddPrescriptionAsync(Consultation c, List<PrescriptionLineInput> lines, Guid branchId, CancellationToken ct)
    {
        var days = await settings.GetLongAsync(SettingsReader.PrescriptionValidDays, branchId, 30, ct);
        string code;
        do code = "RX-" + RandomCode(6);
        while (await db.Prescriptions.AnyAsync(p => p.PrescriptionCode == code, ct));
        var rx = new Prescription
        {
            PrescriptionId = UuidV7.New(), ConsultationId = c.ConsultationId, PatientId = c.PatientId, DoctorId = c.DoctorId,
            PrescriptionCode = code, IssuedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddDays(days),
        };
        rx.Lines.AddRange(Lines(rx.PrescriptionId, lines));
        db.Prescriptions.Add(rx);
        referrals.TrackPrescription(rx, branchId);
        return rx;
    }

    private static IEnumerable<PrescriptionLine> Lines(Guid prescriptionId, List<PrescriptionLineInput> lines) =>
        lines.Select((l, i) => new PrescriptionLine
        {
            PrescriptionId = prescriptionId, LineNo = (short)(i + 1), DrugName = l.DrugName!.Trim(), Dose = Clean(l.Dose), Frequency = Clean(l.Frequency),
            DurationDays = l.DurationDays, Instructions = Clean(l.Instructions),
        });

    private static string RandomCode(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    private static void ValidateText(string? diagnosis, string? icd, string? notes, string? priv, short? followUp, List<PrescriptionLineInput>? rx)
    {
        var v = new Validator()
            .Check(diagnosis is null || diagnosis.Length <= 1000, "diagnosis", "1000 حرف كحد أقصى")
            .Check(icd is null || icd.Trim().Length <= 10, "icd10_code", "10 أحرف كحد أقصى")
            .Check(notes is null || notes.Length <= 8000, "clinical_notes", "8000 حرف كحد أقصى")
            .Check(priv is null || priv.Length <= 8000, "private_notes", "8000 حرف كحد أقصى")
            .Check(followUp is null or >= 0 and <= 365, "follow_up_after_days", "من 1 إلى 365 يوماً")
            .Check(rx is null || rx.Count <= MaxPrescriptionLines, "prescription", $"{MaxPrescriptionLines} سطراً كحد أقصى");
        for (var i = 0; i < (rx?.Count ?? 0); i++)
        {
            var l = rx![i];
            v.Check(!string.IsNullOrWhiteSpace(l.DrugName) && l.DrugName.Trim().Length <= 200, $"prescription[{i}].drug_name", "اسم الدواء مطلوب (200 حرف كحد أقصى)");
            v.Check(l.DurationDays is null or > 0 and <= 365, $"prescription[{i}].duration_days", "من 1 إلى 365");
            v.Check((l.Dose?.Length ?? 0) <= 100 && (l.Frequency?.Length ?? 0) <= 100, $"prescription[{i}]", "الجرعة والتكرار 100 حرف كحد أقصى");
        }
        v.ThrowIfInvalid();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.ToOffset(ClockExtensions.BaghdadOffset).DateTime);

    public static string PaymentStatusOf(string itemStatus) => itemStatus switch
    {
        InvoiceItemStatuses.Paid or InvoiceItemStatuses.PartiallyRefunded => "paid",
        InvoiceItemStatuses.Deferred => "deferred",
        InvoiceItemStatuses.Refunded => "refunded",
        InvoiceItemStatuses.Void => "void",
        _ => "unpaid",
    };

    private record Ctx(Dictionary<Guid, QueueTicket> Tickets, Dictionary<Guid, string> BranchNames);

    private async Task<Ctx> ContextAsync(List<Consultation> rows, CancellationToken ct)
    {
        var ticketIds = rows.Select(c => c.TicketId).ToList();
        var tickets = await db.QueueTickets.AsNoTracking().Where(t => ticketIds.Contains(t.TicketId)).ToDictionaryAsync(t => t.TicketId, ct);
        var branchIds = tickets.Values.Select(t => t.BranchId).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.BranchId)).ToDictionaryAsync(b => b.BranchId, b => b.Name, ct);
        return new Ctx(tickets, tickets.ToDictionary(t => t.Key, t => branches.GetValueOrDefault(t.Value.BranchId, "")));
    }

    private async Task<List<ConsultationDto>> ToDtosAsync(List<Consultation> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(c => c.ConsultationId).ToList();
        var ctx = await ContextAsync(rows, ct);
        var patientIds = rows.Select(c => c.PatientId).Distinct().ToList();
        var patients = await db.Patients.AsNoTracking().Where(p => patientIds.Contains(p.PatientId)).ToDictionaryAsync(p => p.PatientId, ct);
        var doctorIds = rows.Select(c => c.DoctorId).Distinct().ToList();
        var doctors = await db.Doctors.AsNoTracking().Include(d => d.Clinic).Where(d => doctorIds.Contains(d.DoctorId)).ToDictionaryAsync(d => d.DoctorId, ct);
        var rxs = await db.Prescriptions.AsNoTracking().Include(p => p.Lines).Where(p => ids.Contains(p.ConsultationId)).ToDictionaryAsync(p => p.ConsultationId, ct);
        var orders = await (
            from o in db.MedicalOrders.AsNoTracking()
            join i in db.InvoiceItems.AsNoTracking() on o.InvoiceItemId equals i.InvoiceItemId
            where o.ConsultationId != null && ids.Contains(o.ConsultationId.Value)
            orderby o.CreatedAt
            select new { o, i.ServiceName, i.NetPrice, ItemStatus = i.Status }).ToListAsync(ct);
        var addenda = await (
            from a in db.ConsultationAddenda.AsNoTracking()
            join u in db.Users.AsNoTracking() on a.CreatedBy equals u.UserId
            where ids.Contains(a.ConsultationId)
            orderby a.CreatedAt
            select new { a, u.FullName }).ToListAsync(ct);
        var ticketIds = rows.Select(c => c.TicketId).ToList();
        var visitKinds = await db.InvoiceItems.AsNoTracking()
            .Where(i => i.TicketId != null && ticketIds.Contains(i.TicketId.Value) && (i.ItemType == VisitKinds.NewVisit || i.ItemType == VisitKinds.FollowUp))
            .Select(i => new { TicketId = i.TicketId!.Value, i.ItemType }).ToListAsync(ct);
        // نافذة التعديل بإعداد فرع المعاينة (أو الإعداد العام)
        var hours = new Dictionary<Guid, long>();
        foreach (var b in ctx.Tickets.Values.Select(t => t.BranchId).Distinct())
            hours[b] = await settings.GetLongAsync(SettingsReader.ConsultationEditHours, b, 24, ct);
        var defaultHours = await settings.GetLongAsync(SettingsReader.ConsultationEditHours, null, 24, ct);
        var showPrivate = current.Role == Roles.Doctor;
        var today = clock.BaghdadToday();

        return rows.Select(c =>
        {
            var p = patients[c.PatientId];
            var d = doctors[c.DoctorId];
            ctx.Tickets.TryGetValue(c.TicketId, out var t);
            rxs.TryGetValue(c.ConsultationId, out var rx);
            return new ConsultationDto(c.ConsultationId, c.TicketId, t?.TicketNumber ?? "",
                new PatientSummaryDto(p.PatientId, p.FullName, p.FileNumber, p.Gender, PatientPortalService.AgeOf(p, today)),
                d.DoctorId, d.FullName, d.Clinic?.Name ?? "", visitKinds.FirstOrDefault(v => v.TicketId == c.TicketId)?.ItemType,
                c.Diagnosis, c.Icd10Code, c.ClinicalNotes, showPrivate ? c.PrivateNotes : null, c.FollowUpAfterDays,
                c.FollowUpAfterDays is { } f and > 0 ? Day(c.CreatedAt).AddDays(f) : null,
                rx is null ? null : new ConsultationPrescriptionDto(rx.PrescriptionId, rx.PrescriptionCode, EffectiveStatus(rx, clock.UtcNow), rx.ExpiresAt,
                    rx.Lines.OrderBy(l => l.LineNo).Select(l => new PrescriptionLineDto(l.LineNo, l.DrugName, l.Dose, l.Frequency, l.DurationDays, l.Instructions)).ToList()),
                orders.Where(x => x.o.ConsultationId == c.ConsultationId).Select(x => new ConsultationOrderDto(x.o.OrderItemId, x.o.Department, x.o.ServiceId,
                    x.ServiceName, x.o.Note, x.o.Status, PaymentStatusOf(x.ItemStatus), x.o.InvoiceItemId, x.NetPrice, x.o.HasCritical,
                    x.o.Status == OrderStatuses.Released)).ToList(),
                addenda.Where(x => x.a.ConsultationId == c.ConsultationId).Select(x => new AddendumDto(x.a.AddendumId, x.a.Text, x.FullName, x.a.CreatedAt)).ToList(),
                c.CreatedAt.AddHours(t is null ? defaultHours : hours.GetValueOrDefault(t.BranchId, defaultHours)), c.CreatedAt, c.UpdatedAt);
        }).ToList();
    }

    /// <summary>الوصفة غير المصروفة بعد انتهاء صلاحيتها تظهر expired.</summary>
    public static string EffectiveStatus(Prescription rx, DateTimeOffset now) =>
        rx.Status == PrescriptionStatuses.NotDispensed && rx.ExpiresAt <= now ? PrescriptionStatuses.Expired : rx.Status;
}
