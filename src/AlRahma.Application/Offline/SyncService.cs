using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Billing;
using AlRahma.Application.Common;
using AlRahma.Application.Doctors;
using AlRahma.Application.Notifications;
using AlRahma.Application.Patients;
using AlRahma.Application.Queue;
using AlRahma.Application.Services;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Offline;

public record CreateBlockRequest(int? Size);
/// <summary>`device_id` و`device_code` ليعرف الوكيل جهازه ويتحقق من التوقيع: `{block_id}|{device_id}|{prefix}|{from}|{to}`.</summary>
public record ReceiptBlockDto(Guid BlockId, string Prefix, long From, long To, string Signature, Guid DeviceId, string DeviceCode);
public record ReturnBlockRequest(long? LastUsed);
public record ReturnBlockResponse(Guid BlockId, long? ReturnedFrom, long? ReturnedTo);
public record SyncOperationInput(Guid? OpId, string? Type, DateTimeOffset? OccurredAt, JsonElement Payload);
public record SyncBatchRequest(string? DeviceId, List<SyncOperationInput>? Operations);
/// <summary>`Detail` اختياري لتوضيح التحذير (مثل OFFLINE_LATE_SYNC و LATE_PAYMENT_CLOSED_SHIFT).</summary>
public record SyncWarning(string Code, Guid? PatientId, string? Detail = null);
public record SyncError(string Code, string Title, string? Detail, IDictionary<string, string[]>? Errors);
public record SyncResult(Guid OpId, string Status, Dictionary<string, object?>? ServerRefs, IReadOnlyList<SyncWarning>? Warnings, string? Detail, SyncError? Error);
public record SyncBatchResponse(IReadOnlyList<SyncResult> Results, DateTimeOffset ServerTime);
public record SnapshotDto(IReadOnlyList<ServiceDto> Services, IReadOnlyList<DoctorDto> Doctors, IReadOnlyList<SnapshotSession> Sessions,
    IReadOnlyList<QueueItemDto> Tickets, IReadOnlyList<LiveQueueDto> Queues, IReadOnlyList<PatientDto> Patients, ReceiptBlockDto? ReceiptBlock,
    DateTimeOffset ServerTime);
/// <summary>كائن الوجبة (نفس GET /doctors/{id}/sessions) مع doctor_id.</summary>
public record SnapshotSession(Guid DoctorId, Guid SessionId, DateOnly Date, string Weekday, string Start, string End, DateTimeOffset StartsAt,
    short SlotMinutes, short CapacityTotal, short OnlineQuota, string Status, string? CancelReason)
{
    public static SnapshotSession From(Guid doctorId, SessionDto s) =>
        new(doctorId, s.SessionId, s.Date, s.Weekday, s.Start, s.End, s.StartsAt, s.SlotMinutes, s.CapacityTotal, s.OnlineQuota, s.Status, s.CancelReason);
}

/// <summary>
/// العمل أوفلاين: كتل أرقام إيصالات محجوزة لكل جهاز، ودفعة المزامنة (Outbox) بعمليات idempotent عبر op_id،
/// ولقطة بيانات اليوم التي تخزنها الواجهة محلياً.
/// </summary>
public class SyncService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, NumberSequences numbers, SettingsReader settings,
    IDrawerTokenIssuer signer, PatientService patients, QueueService queue, PaymentService payments, ShiftService shifts,
    ServiceCatalog catalog, DoctorService doctors, NotificationService notifications, OperationClock opClock)
{
    /// <summary>#5: تذكرة أوفلاين دُمجت مع حجز المريض؛ عمليات الدفعة اللاحقة على المعرف المحلي تُوجّه لتذكرة الحجز.</summary>
    private readonly Dictionary<Guid, Guid> ticketAlias = [];
    private Guid Alias(Guid id) => ticketAlias.GetValueOrDefault(id, id);
    public const int MaxOperations = 500;
    public static readonly string[] Types =
    [
        "patient.create", "patient.update", "queue.check_in", "queue.call", "queue.start", "queue.complete", "queue.no_show",
        "queue.emergency", "queue.late_arrival", "payment.create", "shift.open", "shift.close", "agent_event",
    ];
    private static readonly string[] AgentEventTypes = ["receipt.printed", "drawer.opened", "drawer.opened_without_receipt", "printer.error", "paper.out"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    // ------------------------------------------------------------------ كتل الإيصالات

    public async Task<(ReceiptBlockDto Block, bool Created)> CreateBlockAsync(string deviceRef, CreateBlockRequest req, CancellationToken ct)
    {
        var defaultSize = await settings.GetLongAsync(SettingsReader.ReceiptBlockSize, current.BranchId, 200, ct);
        var size = req.Size ?? (int)defaultSize;
        new Validator().Check(size is >= 10 and <= 2000, "size", "من 10 إلى 2000").ThrowIfInvalid();
        var device = await LoadDeviceAsync(deviceRef, ct);
        if (device.Kind != "reception_pc") throw AppException.Validation(new Dictionary<string, string[]> { ["device_id"] = ["كتل الإيصالات لأجهزة الاستقبال فقط"] });

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync($"SELECT 1 FROM devices WHERE device_id = {device.DeviceId} FOR UPDATE", ct);
        var active = await db.ReceiptBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.DeviceId == device.DeviceId && b.ReturnedAt == null, ct);
        if (active is not null) return (ToDto(active, device), false);

        var previous = await db.ReceiptBlocks.AsNoTracking().Where(b => b.DeviceId == device.DeviceId).OrderByDescending(b => b.RangeTo).FirstOrDefaultAsync(ct);
        var prefix = previous?.Prefix
                     ?? $"{await numbers.BranchCodeAsync(device.BranchId, ct)}-D{await numbers.NextAsync(device.BranchId, "device_prefix", "all", ct):D2}-";
        var from = (previous?.RangeTo ?? 0) + 1;
        var block = new ReceiptBlock
        {
            BlockId = UuidV7.New(), DeviceId = device.DeviceId, BranchId = device.BranchId, Prefix = prefix, RangeFrom = from, RangeTo = from + size - 1,
            IssuedAt = clock.UtcNow,
        };
        db.ReceiptBlocks.Add(block);
        audit.Add("RECEIPT_BLOCK_ISSUED", "device", device.DeviceId, after: new { block.BlockId, block.Prefix, block.RangeFrom, block.RangeTo });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return (ToDto(block, device), true);
    }

    public async Task<ReturnBlockResponse> ReturnBlockAsync(string deviceRef, Guid blockId, ReturnBlockRequest req, CancellationToken ct)
    {
        var device = await LoadDeviceAsync(deviceRef, ct);
        var block = await db.ReceiptBlocks.FirstOrDefaultAsync(b => b.BlockId == blockId && b.DeviceId == device.DeviceId, ct)
                    ?? throw new AppException(404, "RECEIPT_BLOCK_NOT_FOUND", "الكتلة غير موجودة لهذا الجهاز");
        if (block.ReturnedAt is null)
        {
            new Validator().Check(req.LastUsed is null || (req.LastUsed >= block.RangeFrom && req.LastUsed <= block.RangeTo), "last_used",
                $"من {block.RangeFrom} إلى {block.RangeTo} أو null").ThrowIfInvalid();
            // لا يُعاد رقم استُخدم فعلاً في إيصال وصل للسيرفر
            var usedMax = (await db.Receipts.AsNoTracking().Where(r => r.BlockId == block.BlockId).Select(r => r.ReceiptNumber).ToListAsync(ct))
                .Select(n => long.TryParse(n[block.Prefix.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max();
            block.LastUsed = Math.Max(req.LastUsed ?? 0, usedMax) is var last && last >= block.RangeFrom ? last : null;
            block.ReturnedAt = clock.UtcNow;
            audit.Add("RECEIPT_BLOCK_RETURNED", "device", device.DeviceId, after: new { block.BlockId, block.LastUsed });
            await db.SaveChangesAsync(ct);
        }
        var returnedFrom = (block.LastUsed ?? block.RangeFrom - 1) + 1;
        return returnedFrom > block.RangeTo
            ? new ReturnBlockResponse(block.BlockId, null, null)
            : new ReturnBlockResponse(block.BlockId, returnedFrom, block.RangeTo);
    }

    private ReceiptBlockDto ToDto(ReceiptBlock b, Device device) =>
        new(b.BlockId, b.Prefix, b.RangeFrom, b.RangeTo, signer.Sign($"{b.BlockId}|{b.DeviceId}|{b.Prefix}|{b.RangeFrom}|{b.RangeTo}"), b.DeviceId, device.DeviceCode);

    private async Task<Device> LoadDeviceAsync(string deviceRef, CancellationToken ct)
    {
        var byId = Guid.TryParse(deviceRef, out var id);
        return await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => (byId ? d.DeviceId == id : d.DeviceCode == deviceRef)
                                                                        && d.BranchId == current.BranchId && d.IsActive, ct)
               ?? throw new AppException(404, "DEVICE_NOT_FOUND", "الجهاز غير مسجل في هذا الفرع");
    }

    // ------------------------------------------------------------------ دفعة المزامنة

    public async Task<SyncBatchResponse> BatchAsync(SyncBatchRequest req, CancellationToken ct)
    {
        var ops = req.Operations ?? [];
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(req.DeviceId), "device_id", "الجهاز مطلوب")
            .Check(ops.Count is > 0 and <= MaxOperations, "operations", $"من 1 إلى {MaxOperations} عملية")
            .Check(ops.All(o => o.OpId is not null), "operations", "op_id مطلوب لكل عملية")
            .ThrowIfInvalid();
        var device = await LoadDeviceAsync(req.DeviceId!.Trim(), ct);
        var results = new List<SyncResult>();
        foreach (var op in ops.OrderBy(o => o.OccurredAt ?? DateTimeOffset.MaxValue))
            results.Add(await ApplyAsync(device, op, ct));
        return new SyncBatchResponse(results, clock.UtcNow);
    }

    private async Task<SyncResult> ApplyAsync(Device device, SyncOperationInput op, CancellationToken ct)
    {
        var opId = op.OpId!.Value;
        var previous = await db.SyncOperations.AsNoTracking().FirstOrDefaultAsync(s => s.OpId == opId, ct);
        if (previous is not null)
        {
            var refs = previous.Result is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(previous.Result, Json);
            return new SyncResult(opId, "duplicate", refs, null, null, null);
        }
        SyncResult result;
        try
        {
            if (op.Type is null || !Types.Contains(op.Type))
                throw AppException.Validation(new Dictionary<string, string[]> { ["type"] = [string.Join(" | ", Types)] });
            if (op.OccurredAt is null || op.OccurredAt > clock.UtcNow.AddMinutes(5))
                throw AppException.Validation(new Dictionary<string, string[]> { ["occurred_at"] = ["وقت الحدوث مطلوب ولا يكون في المستقبل"] });
            // C2/M4: العملية تُنفذ بوقت حدوثها (اختيار الوجبة، أوقات الوصول والمعاينة)
            Dictionary<string, object?> refs; List<SyncWarning> warnings;
            using (opClock.Offline(op.OccurredAt.Value))
                (refs, warnings) = await ExecuteAsync(device, op.Type, op.OccurredAt.Value, op.Payload, ct);
            result = new SyncResult(opId, "applied", refs, warnings.Count > 0 ? warnings : null, null, null);
        }
        catch (AppException e)
        {
            db.DiscardChanges();
            var error = new SyncError(e.Code, e.Title, e.Detail, e.Errors);
            result = e.Status == 409
                ? new SyncResult(opId, "conflict", null, null, e.Detail ?? e.Title, error)
                : new SyncResult(opId, "rejected", null, null, null, error);
        }
        catch (JsonException)
        {
            db.DiscardChanges();
            result = new SyncResult(opId, "rejected", null, null, null, new SyncError("VALIDATION_ERROR", "صيغة payload غير صالحة", null, null));
        }
        db.SyncOperations.Add(new SyncOperation
        {
            OpId = opId, DeviceId = device.DeviceId, UserId = current.UserId, Type = op.Type ?? "unknown", OccurredAt = op.OccurredAt ?? clock.UtcNow,
            ReceivedAt = clock.UtcNow, Status = result.Status, Payload = op.Payload.ValueKind == JsonValueKind.Undefined ? "{}" : op.Payload.GetRawText(),
            Result = JsonSerializer.Serialize(result.Status == "applied" ? (object?)result.ServerRefs : result.Error, Json),
        });
        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<(Dictionary<string, object?> Refs, List<SyncWarning> Warnings)> ExecuteAsync(Device device, string type, DateTimeOffset occurredAt,
        JsonElement payload, CancellationToken ct)
    {
        T Body<T>() => payload.ValueKind == JsonValueKind.Object
            ? payload.Deserialize<T>(Json) ?? throw new JsonException()
            : throw new JsonException();
        Guid TicketId() => Alias(Body<TicketRef>().TicketId ?? throw AppException.Validation(new Dictionary<string, string[]> { ["ticket_id"] = ["مطلوب"] }));
        var warnings = new List<SyncWarning>();
        switch (type)
        {
            case "patient.create":
            {
                var body = Body<CreatePatientRequest>();
                var r = await patients.CreateAsync(body with { CreatedOfflineAt = body.CreatedOfflineAt ?? occurredAt }, ct);
                warnings.AddRange(r.PossibleDuplicates.Where(d => d.PatientId != r.PatientId).Select(d => new SyncWarning("POSSIBLE_DUPLICATE_PATIENT", d.PatientId)));
                return (new() { ["patient_id"] = r.PatientId, ["file_number"] = r.FileNumber }, warnings);
            }
            case "patient.update":
            {
                var id = Body<PatientRef>().PatientId ?? throw AppException.Validation(new Dictionary<string, string[]> { ["patient_id"] = ["مطلوب"] });
                var r = await patients.UpdateAsync(id, Body<UpdatePatientRequest>(), ct);
                return (new() { ["patient_id"] = r.PatientId }, warnings);
            }
            case "queue.check_in":
            {
                var body = Body<CheckInRequest>();
                var r = await queue.CheckInAsync(body with { CreatedOfflineAt = body.CreatedOfflineAt ?? occurredAt }, ct);
                // وصل بعد انتهاء وجبته: قُبل (المريض عوين فعلاً) مع علامة للمراجعة
                var session = await db.QueueTickets.AsNoTracking().Where(t => t.TicketId == r.TicketId)
                    .Join(db.DoctorSessions.AsNoTracking(), t => t.SessionId, s => s.SessionId, (t, s) => s).FirstAsync(ct);
                if (QueueEngine.SessionEndUtc(session) < clock.UtcNow)
                    warnings.Add(new SyncWarning("OFFLINE_LATE_SYNC", body.PatientId, "سُجّل الحضور أوفلاين وزُومن بعد انتهاء الوجبة"));
                if (body.TicketId is { } local && local != r.TicketId)
                {
                    ticketAlias[local] = r.TicketId;
                    warnings.Add(new SyncWarning("MERGED_WITH_BOOKING", body.PatientId, "للمريض حجز في هذه الوجبة: دُمج الحضور مع حجزه"));
                }
                return (new() { ["ticket_id"] = r.TicketId, ["ticket_number"] = r.TicketNumber }, warnings);
            }
            case "queue.call": return (Refs(await queue.CallAsync(TicketId(), ct)), warnings);
            case "queue.start": return (Refs(await queue.StartAsync(TicketId(), ct)), warnings);
            case "queue.complete": return (Refs(await queue.CompleteAsync(TicketId(), ct)), warnings);
            case "queue.late_arrival": return (Refs(await queue.LateArrivalAsync(TicketId(), ct)), warnings);
            case "queue.no_show":
            {
                var r = await queue.NoShowAsync(TicketId(), new NoShowRequest(Body<NoShowRequest>().Mode ?? "final"), ct);
                return (new() { ["ticket_id"] = r.TicketId, ["status"] = r.Status, ["patient_banned"] = r.PatientBanned }, warnings);
            }
            case "queue.emergency":
            {
                var r = await queue.EmergencyAsync(TicketId(), Body<EmergencyRequest>(), ct);
                return (new() { ["ticket_id"] = r.TicketId, ["position"] = r.Position }, warnings);
            }
            case "payment.create":
            {
                var body = Body<CollectPaymentRequest>();
                var ticket = Body<TicketRef>().TicketId is { } t ? Alias(t) : (Guid?)null;
                var shiftId = Body<ShiftRef>().ShiftId; // C3: الوردية التي كانت مفتوحة على الجهاز وقت القبض
                var r = await payments.CollectAsync(body with { CreatedOfflineAt = body.CreatedOfflineAt ?? occurredAt }, ct,
                    new OfflineContext(device.DeviceId, occurredAt, ticket, shiftId));
                if (await shifts.ApplyLatePaymentAsync(r.ShiftId, r.PaymentId, ct))
                    warnings.Add(new SyncWarning("LATE_PAYMENT_CLOSED_SHIFT", body.PatientId, "الوردية أُغلقت قبل المزامنة؛ أُعيدت مطابقتها ونُبّه المحاسب"));
                var mismatch = await db.Payments.Where(p => p.PaymentId == r.PaymentId).Select(p => p.PriceMismatch).FirstAsync(ct);
                if (mismatch) warnings.Add(new SyncWarning("PRICE_MISMATCH", body.PatientId));
                return (new() { ["payment_id"] = r.PaymentId, ["receipt_id"] = r.Receipt.ReceiptId, ["receipt_number"] = r.Receipt.ReceiptNumber }, warnings);
            }
            case "shift.open":
            {
                var body = Body<OpenShiftRequest>();
                var r = await shifts.OpenAsync(body with { DeviceId = body.DeviceId ?? device.DeviceId }, ct);
                return (new() { ["shift_id"] = r.ShiftId }, warnings);
            }
            case "shift.close":
            {
                var id = Body<ShiftRef>().ShiftId ?? throw AppException.Validation(new Dictionary<string, string[]> { ["shift_id"] = ["مطلوب"] });
                var r = await shifts.CloseAsync(id, Body<CloseShiftRequest>(), ct);
                return (new() { ["shift_id"] = r.ShiftId, ["status"] = r.Status }, warnings);
            }
            default: // agent_event
                return (await AgentEventAsync(device, Body<AgentEventInput>(), ct), warnings);
        }
    }

    private static Dictionary<string, object?> Refs(TicketActionResponse r) =>
        new() { ["ticket_id"] = r.TicketId, ["ticket_number"] = r.TicketNumber, ["status"] = r.Status };

    private async Task<Dictionary<string, object?>> AgentEventAsync(Device device, AgentEventInput e, CancellationToken ct)
    {
        new Validator()
            .Check(e.LocalEventId is not null, "local_event_id", "مطلوب")
            .Check(e.Type is not null && AgentEventTypes.Contains(e.Type), "type", string.Join(" | ", AgentEventTypes))
            .Check(e.OccurredAt is not null, "occurred_at", "مطلوب")
            .ThrowIfInvalid();
        var existing = await db.AgentEvents.AsNoTracking().FirstOrDefaultAsync(x => x.DeviceId == device.DeviceId && x.LocalEventId == e.LocalEventId, ct);
        if (existing is not null) return new() { ["event_id"] = existing.EventId };
        var row = new AgentEvent
        {
            EventId = UuidV7.New(), DeviceId = device.DeviceId, LocalEventId = e.LocalEventId!.Value, Type = e.Type!,
            ReceiptNumber = string.IsNullOrWhiteSpace(e.ReceiptNumber) ? null : e.ReceiptNumber.Trim(), OccurredAt = e.OccurredAt!.Value, SyncedAt = clock.UtcNow,
        };
        db.AgentEvents.Add(row);
        if (row.Type == "drawer.opened_without_receipt")
            audit.Add("DRAWER_OPENED_WITHOUT_RECEIPT", "device", device.DeviceId, after: new { row.LocalEventId, row.OccurredAt });
        await db.SaveChangesAsync(ct);
        if (row.Type == "drawer.opened_without_receipt")
            await notifications.NotifyUsersAsync(await notifications.FinanceUsersAsync(device.BranchId, ct), device.BranchId, "drawer.opened_without_receipt",
                "فتح الدرج بدون إيصال", $"{device.Name} في {row.OccurredAt.ToOffset(ClockExtensions.BaghdadOffset):yyyy-MM-dd HH:mm}", null, null, ct);
        return new() { ["event_id"] = row.EventId };
    }

    private record TicketRef(Guid? TicketId);
    private record PatientRef(Guid? PatientId);
    private record ShiftRef(Guid? ShiftId);
    private record AgentEventInput(long? LocalEventId, string? Type, string? ReceiptNumber, DateTimeOffset? OccurredAt);

    // ------------------------------------------------------------------ اللقطة

    public async Task<SnapshotDto> SnapshotAsync(DateTimeOffset? since, string? deviceRef, CancellationToken ct)
    {
        // الخصوصية: أسماء وهواتف مرضى اليوم تُنزّل للعمل أوفلاين على أجهزة الاستقبال فقط (تسجيل المرضى)، لا لكل موظف
        if (!current.Has(Permissions.PatientsWrite))
            throw AppException.Forbidden("لقطة الأوفلاين لموظفي الاستقبال فقط");
        var now = clock.UtcNow;
        var today = clock.BaghdadToday();
        var services = await catalog.ListAsync(new ServiceListQuery(null, null, null, false), ct);
        var doctorList = await doctors.ListAsync(null, null, null, ct);
        var sessions = new List<SnapshotSession>();
        foreach (var d in doctorList)
            sessions.AddRange((await doctors.GetSessionsAsync(d.DoctorId, today, today, ct)).Select(s => SnapshotSession.From(d.DoctorId, s)));
        var queues = await queue.LiveListAsync(null, today, ct);

        var sessionIds = await db.DoctorSessions.Where(s => s.BranchId == current.BranchId && s.SessionDate == today).Select(s => s.SessionId).ToListAsync(ct);
        var patientIds = await db.Bookings.Where(b => sessionIds.Contains(b.SessionId)).Select(b => b.PatientId)
            .Union(db.QueueTickets.Where(t => sessionIds.Contains(t.SessionId)).Select(t => t.PatientId)).ToListAsync(ct);
        var pq = db.Patients.AsNoTracking().Where(p => patientIds.Contains(p.PatientId));
        if (since is { } s) pq = pq.Where(p => p.UpdatedAt >= s || p.CreatedAt >= s);
        var list = await pq.ToListAsync(ct);
        var ids = list.Select(p => p.PatientId).ToList();
        var credits = await db.PatientCredits.Where(c => ids.Contains(c.PatientId)).GroupBy(c => c.PatientId)
            .Select(g => new { g.Key, Sum = g.Sum(c => c.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var patientDtos = list.Select(p => new PatientDto(p.PatientId, p.FileNumber, p.FullName, p.PhoneNumber, p.BirthDate, p.ApproxAge, p.Gender,
            p.HomeBranchId, p.IsBanned, p.NoShowCount, credits.GetValueOrDefault(p.PatientId), p.MergedIntoPatientId, p.CreatedAt, p.UpdatedAt)).ToList();

        ReceiptBlockDto? block = null;
        if (!string.IsNullOrWhiteSpace(deviceRef))
        {
            var device = await LoadDeviceAsync(deviceRef.Trim(), ct);
            var active = await db.ReceiptBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.DeviceId == device.DeviceId && b.ReturnedAt == null, ct);
            if (active is not null) block = ToDto(active, device);
        }
        return new SnapshotDto(services, doctorList, sessions, queues.SelectMany(q => q.Items).ToList(), queues, patientDtos, block, now);
    }
}
