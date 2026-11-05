using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Queue;

public record QueueItemDto(Guid TicketId, string TicketNumber, Guid PatientId, string PatientName, Guid? BookingId, string Source,
    string Priority, string Status, string PaymentStatus, Guid? InvoiceId, short? QueuePosition, DateTimeOffset? EstimatedTime,
    DateTimeOffset? ArrivedAt, DateTimeOffset? CalledAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
{
    /// <summary>المريض ضغط "أنا قادم" على تنبيه الاستدعاء.</summary>
    public DateTimeOffset? CallAcknowledgedAt { get; init; }
    /// <summary>عدد تنبيهات الاستدعاء المرسلة للمريض.</summary>
    public int CallAlertsSent { get; init; }
    /// <summary>أكد أنه في الطريق (دعوة الحضور المبكر).</summary>
    public DateTimeOffset? OnTheWayAt { get; init; }
    public int? OnTheWayEtaMinutes { get; init; }
    /// <summary>مراجع «إضافي» فوق السعة.</summary>
    public bool IsExtra { get; init; }
}

public record LiveQueueDto(Guid SessionId, Guid DoctorId, string DoctorName, Guid ClinicId, string ClinicName, DateOnly Date,
    string Start, string End, string SessionStatus, double? AvgConsultMinutesToday, int DelayMinutes, IReadOnlyList<QueueItemDto> Items)
{
    /// <summary>من يستدعي التالي في هذا الفرع، وهل الاستدعاء التلقائي مفعل.</summary>
    public CallPolicyDto? CallPolicy { get; init; }
    /// <summary>«الطبيب وصل» (null = لم يُسجل بعد).</summary>
    public DateTimeOffset? DoctorArrivedAt { get; init; }
    /// <summary>نهاية الجدول الأصلية؛ `End` هي النهاية الفعلية بعد «تمديد اليوم».</summary>
    public string? ScheduledEnd { get; init; }
    public bool Extended { get; init; }
    public bool RegistrationClosed { get; init; }
    /// <summary>false = الطبيب لا يقبل «إضافي» فوق السعة.</summary>
    public bool AllowExtra { get; init; } = true;
    public int CapacityTotal { get; init; }
}

public record DisplayBoardDto(string DoctorName, string? NowServing, IReadOnlyList<string> Next);
public record DisplayQueueDto(string ClinicName, string? DoctorName, string? NowServing, IReadOnlyList<string> Next,
    IReadOnlyList<DisplayBoardDto> Boards);

/// <summary>قراءة الطابور للعرض (الاستقبال، الطبيب، الشاشة العامة، والأحداث المباشرة).</summary>
public class QueueReader(IAppDbContext db, IClock clock, QueueEngine engine, SettingsReader settings)
{
    private static int StatusOrder(QueueTicket t, int position) => t.Status switch
    {
        TicketStatuses.Skipped => 10_000,
        TicketStatuses.Completed => 20_000,
        TicketStatuses.NoShow => 30_000,
        _ => position,
    };

    public async Task<LiveQueueDto> LiveAsync(DoctorSession session, CancellationToken ct)
    {
        var doctor = await db.Doctors.AsNoTracking().Include(d => d.Clinic).FirstAsync(d => d.DoctorId == session.DoctorId, ct);
        var tickets = await db.QueueTickets.AsNoTracking().Include(t => t.Patient)
            .Where(t => t.SessionId == session.SessionId && t.Status != TicketStatuses.Cancelled).ToListAsync(ct);
        var ticketIds = tickets.Select(t => t.TicketId).ToList();
        var items = await db.InvoiceItems.AsNoTracking()
            .Where(i => i.TicketId != null && ticketIds.Contains(i.TicketId.Value) && i.Status != InvoiceItemStatuses.Void)
            .ToListAsync(ct);
        var byTicket = items.GroupBy(i => i.TicketId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First());

        var ordered = QueueEngine.Ordered(tickets);
        var rank = ordered.Select((t, i) => (t.TicketId, i)).ToDictionary(x => x.TicketId, x => x.i);
        var sorted = tickets
            .OrderBy(t => StatusOrder(t, rank.GetValueOrDefault(t.TicketId, 5_000)))
            .ThenBy(t => t.CompletedAt ?? t.CreatedAt)
            .ThenBy(t => t.SequenceNumber)
            .ToList();

        var avg = await engine.AverageConsultMinutesAsync(session.DoctorId, ct);
        // التأخير يقاس على المحجوزين فقط (لهم موعد)؛ المراجع بدون حجز رقم تسلسله لا يعني موعداً
        var firstPending = ordered.FirstOrDefault(t => t.Status is TicketStatuses.Arrived or TicketStatuses.Waiting
                                                      && t.BookingId != null && t.Priority != TicketPriorities.LateReinserted);
        var delay = 0;
        if (firstPending?.EstimatedTime is { } eta)
        {
            var scheduled = QueueEngine.ScheduledTime(session, firstPending.SequenceNumber);
            // المحجوز الذي وصل بعد موعده: التأخير يُقاس من وصوله لا من موعده (تأخره ليس تأخر العيادة)
            var baseline = firstPending.ArrivedAt is { } arrived && arrived > scheduled ? arrived : scheduled;
            delay = Math.Max(0, (int)Math.Round((eta - baseline).TotalMinutes));
        }

        return new LiveQueueDto(session.SessionId, doctor.DoctorId, doctor.FullName, doctor.ClinicId, doctor.Clinic!.Name,
            session.SessionDate, session.StartTime.ToString("HH:mm"), session.EffectiveEndTime.ToString("HH:mm"), session.Status, avg, delay,
            sorted.Select(t =>
            {
                byTicket.TryGetValue(t.TicketId, out var item);
                return new QueueItemDto(t.TicketId, t.TicketNumber, t.PatientId, t.Patient!.FullName, t.BookingId, t.Source, t.Priority,
                    t.Status, QueueEngine.PaymentStatusOf(item), item?.InvoiceId, t.QueuePosition, t.EstimatedTime,
                    t.ArrivedAt, t.CalledAt, t.StartedAt, t.CompletedAt)
                {
                    CallAcknowledgedAt = t.CallAcknowledgedAt, CallAlertsSent = t.CallAlertsSent,
                    OnTheWayAt = t.OnTheWayAt, OnTheWayEtaMinutes = t.OnTheWayEtaMinutes, IsExtra = t.IsExtra,
                };
            }).ToList())
        {
            CallPolicy = await settings.CallPolicyAsync(doctor.Clinic.BranchId, ct),
            DoctorArrivedAt = session.DoctorArrivedAt, ScheduledEnd = session.EndTime.ToString("HH:mm"), Extended = session.ExtendedEndTime is not null,
            RegistrationClosed = session.RegistrationClosedAt is not null, AllowExtra = doctor.AllowExtra, CapacityTotal = session.CapacityTotal,
        };
    }

    /// <summary>الوجبة "الحالية" للطبيب في يوم: أول وجبة غير ملغاة لم ينتهِ وقتها، وإلا آخر وجبة.</summary>
    public async Task<DoctorSession?> CurrentSessionAsync(Guid doctorId, DateOnly date, CancellationToken ct)
    {
        var sessions = await db.DoctorSessions.AsNoTracking()
            .Where(s => s.DoctorId == doctorId && s.SessionDate == date && s.Status != SessionStatuses.Cancelled)
            .OrderBy(s => s.StartTime).ToListAsync(ct);
        var now = clock.UtcNow;
        return sessions.FirstOrDefault(s => QueueEngine.SessionEndUtc(s) > now) ?? sessions.LastOrDefault();
    }

    /// <summary>شاشة الانتظار العامة لعيادة: أرقام التذاكر فقط بدون أسماء.</summary>
    public async Task<DisplayQueueDto> DisplayAsync(Guid clinicId, CancellationToken ct)
    {
        var clinic = await db.Clinics.AsNoTracking().FirstAsync(c => c.ClinicId == clinicId, ct);
        var today = clock.BaghdadToday();
        var doctors = await db.Doctors.AsNoTracking().Where(d => d.ClinicId == clinicId && d.IsActive)
            .OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var boards = new List<DisplayBoardDto>();
        foreach (var d in doctors)
        {
            var session = await CurrentSessionAsync(d.DoctorId, today, ct);
            if (session is null) continue;
            var tickets = await db.QueueTickets.AsNoTracking().Where(t => t.SessionId == session.SessionId).ToListAsync(ct);
            var ordered = QueueEngine.Ordered(tickets);
            var serving = ordered.Where(t => t.Status is TicketStatuses.InRoom or TicketStatuses.Called)
                .OrderByDescending(t => t.CalledAt).FirstOrDefault();
            var next = ordered.Where(t => t.Status == TicketStatuses.Arrived).Take(3).Select(t => t.TicketNumber).ToList();
            boards.Add(new DisplayBoardDto(d.FullName, serving?.TicketNumber, next));
        }
        var first = boards.FirstOrDefault();
        return new DisplayQueueDto(clinic.Name, first?.DoctorName, first?.NowServing, first?.Next ?? [], boards);
    }
}

/// <summary>قيم action في حدث booking.changed (المرحلة 3).</summary>
public static class BookingChangeActions
{
    public const string Created = "created";
    public const string CheckedIn = "checked_in";
    public const string SelfCheckedIn = "self_checked_in";
    public const string Cancelled = "cancelled";
    public const string Rescheduled = "rescheduled";
    public const string Skipped = "skipped";
    public const string NoShow = "no_show";
}

/// <summary>يرسل تحديثات الطابور المباشرة بعد كل تغيير (القسم 7 - Real-time events).</summary>
public class QueueNotifier(IAppDbContext db, QueueReader reader, IRealtime realtime, QueueEngine engine,
    Notifications.NotificationService notifications, SettingsReader settings, IClock clock)
{
    public async Task SessionChangedAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.DoctorSessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
        if (session is null) return;
        var live = await reader.LiveAsync(session, ct);
        var payload = new { session_id = session.SessionId, doctor_id = session.DoctorId, items = live.Items };
        await realtime.SendAsync(RealtimeGroups.BranchQueue(session.BranchId), "queue.updated", payload);
        await realtime.SendAsync(RealtimeGroups.Doctor(session.DoctorId), "queue.updated", payload);

        // تطبيق المريض: الموقع والوقت التقديري لحجوزات الوجبة النشطة لكل حساب مربوط بالمريض
        var bookings = live.Items.Where(i => i.BookingId != null && i.Status is TicketStatuses.Waiting or TicketStatuses.Arrived or TicketStatuses.Called)
            .Select(i => new { BookingId = i.BookingId!.Value, i.PatientId, i.EstimatedTime, i.QueuePosition }).ToList();
        if (bookings.Count > 0)
        {
            var patientIds = bookings.Select(b => b.PatientId).Distinct().ToList();
            var links = await db.PatientAccountLinks.AsNoTracking().Where(l => patientIds.Contains(l.PatientId)).ToListAsync(ct);
            foreach (var b in bookings)
                foreach (var l in links.Where(l => l.PatientId == b.PatientId))
                    await realtime.SendAsync(RealtimeGroups.PatientAccount(l.PatientAccountId), "eta.changed",
                        new { booking_id = b.BookingId, estimated_time = b.EstimatedTime, queue_position = b.QueuePosition });
        }
        // المرحلة 3: «طابوري» في تطبيق المريض لكل تذاكر الوجبة (يشمل المسجل بدون حجز)، وشاشات انتظار العيادة
        var ticketPatients = live.Items.Select(i => i.PatientId).Distinct().ToList();
        if (ticketPatients.Count > 0)
        {
            var links = await db.PatientAccountLinks.AsNoTracking().Where(l => ticketPatients.Contains(l.PatientId)).ToListAsync(ct);
            foreach (var i in live.Items)
                foreach (var l in links.Where(l => l.PatientId == i.PatientId))
                    await realtime.SendAsync(RealtimeGroups.PatientAccount(l.PatientAccountId), "my_queue.updated", new
                    {
                        ticket_id = i.TicketId, booking_id = i.BookingId, patient_id = i.PatientId, session_id = session.SessionId,
                        doctor_id = session.DoctorId, ticket_number = i.TicketNumber, status = i.Status, queue_position = i.QueuePosition,
                        estimated_time = i.EstimatedTime,
                    });
        }
        await DisplaysChangedAsync(live.ClinicId, ct);
        await TurnSoonAsync(session, live.Items, ct);
        var changes = engine.PendingEtaChanges.ToList();
        engine.PendingEtaChanges.Clear();
        foreach (var c in changes.GroupBy(c => c.BookingId).Select(g => g.Last()))
            await notifications.NotifyPatientAsync(c.PatientId, c.BranchId, Notifications.NotificationTypes.BookingEtaChanged, "تغير موعدك التقريبي",
                $"الوقت التقديري الجديد {c.EstimatedTime.ToOffset(ClockExtensions.BaghdadOffset):HH:mm}", "booking", c.BookingId, ct);
    }

    /// <summary>display.updated: نفس محتوى GET /public/displays/{id}/queue لكل شاشة فعالة في العيادة (أرقام فقط، بدون أسماء).</summary>
    public async Task DisplaysChangedAsync(Guid clinicId, CancellationToken ct)
    {
        var displays = await db.Devices.AsNoTracking()
            .Where(d => d.Kind == "display" && d.IsActive && d.ClinicId == clinicId).Select(d => d.DeviceId).ToListAsync(ct);
        if (displays.Count == 0) return;
        var board = await reader.DisplayAsync(clinicId, ct);
        foreach (var id in displays) await realtime.SendAsync(RealtimeGroups.Display(id), "display.updated", board);
    }

    /// <summary>
    /// booking.changed: تغير حالة حجز (تثبيت حضور، إلغاء، تغيير موعد، تخطي، غياب) لجدول الاستقبال (branch:{id}:queue)
    /// ولتطبيق المريض (patient_account:{id}). previousSessionId في تغيير الموعد فقط.
    /// </summary>
    public async Task BookingChangedAsync(Guid bookingId, string action, Guid? previousSessionId, CancellationToken ct)
    {
        var b = await db.Bookings.AsNoTracking().Include(x => x.Session).FirstOrDefaultAsync(x => x.BookingId == bookingId, ct);
        if (b?.Session is null) return;
        var t = await db.QueueTickets.AsNoTracking().FirstOrDefaultAsync(x => x.BookingId == bookingId, ct);
        var payload = new
        {
            action, booking_id = b.BookingId, patient_id = b.PatientId, session_id = b.SessionId, previous_session_id = previousSessionId,
            doctor_id = b.Session.DoctorId, clinic_id = b.Session.ClinicId, date = b.Session.SessionDate, status = b.Status, payment_status = b.PaymentStatus,
            check_in_method = b.CheckInMethod, reschedule_count = b.RescheduleCount, ticket_id = t?.TicketId, ticket_number = t?.TicketNumber,
            ticket_status = t?.Status, queue_position = t?.QueuePosition, estimated_time = t?.EstimatedTime ?? b.EstimatedTime, occurred_at = clock.UtcNow,
        };
        await realtime.SendAsync(RealtimeGroups.BranchQueue(b.Session.BranchId), "booking.changed", payload);
        var accounts = await db.PatientAccountLinks.AsNoTracking().Where(l => l.PatientId == b.PatientId).Select(l => l.PatientAccountId).Distinct().ToListAsync(ct);
        foreach (var a in accounts) await realtime.SendAsync(RealtimeGroups.PatientAccount(a), "booking.changed", payload);
    }

    /// <summary>
    /// "اقترب دورك": مرة واحدة لكل تذكرة عندما يبقى أمامه queue.turn_soon_ahead مرضى أو أقل (المنتظر في البيت أو في الصالة).
    /// </summary>
    private async Task TurnSoonAsync(DoctorSession session, IReadOnlyList<QueueItemDto> items, CancellationToken ct)
    {
        var ahead = await settings.GetLongAsync(SettingsReader.TurnSoonAhead, session.BranchId, 3, ct);
        var candidates = items.Where(i => i.Status is TicketStatuses.Waiting or TicketStatuses.Arrived && i.QueuePosition is { } p && p - 1 <= ahead)
            .ToList();
        if (candidates.Count == 0) return;
        var doctorName = await db.Doctors.Where(d => d.DoctorId == session.DoctorId).Select(d => d.FullName).FirstAsync(ct);
        var now = clock.UtcNow;
        foreach (var c in candidates)
        {
            // الحجز أولاً حتى لا يتكرر الإشعار من طلبين متزامنين
            if (await db.ExecuteAsync($"UPDATE queue_tickets SET turn_soon_notified_at = {now} WHERE ticket_id = {c.TicketId} AND turn_soon_notified_at IS NULL", ct) == 0)
                continue;
            var before = c.QueuePosition!.Value - 1;
            await notifications.NotifyPatientAsync(c.PatientId, session.BranchId, Notifications.NotificationTypes.QueueTurnSoon, "اقترب دورك",
                before == 0
                    ? $"أنت التالي عند {doctorName} (رقمك {c.TicketNumber}). كن قريباً من الغرفة."
                    : $"أمامك {before} {(before == 1 ? "مريض" : "مرضى")} عند {doctorName} (رقمك {c.TicketNumber}). كن قريباً من العيادة.",
                c.BookingId is null ? null : "booking", c.BookingId, ct);
        }
    }

    /// <summary>إشعار المريض برصيد أُضيف لحسابه (إلغاء حجز مدفوع).</summary>
    public Task CreditAddedAsync(Guid patientId, Guid branchId, long amount, string reason, Guid? bookingId, CancellationToken ct) =>
        notifications.NotifyPatientAsync(patientId, branchId, Notifications.NotificationTypes.CreditAdded, "أُضيف رصيد لحسابك",
            $"{amount:N0} د.ع {reason}، يُستخدم في زيارتك القادمة", bookingId is null ? null : "booking", bookingId, ct);

    /// <summary>إشعار المريض بالحظر التلقائي بعد تجاوز حد الغياب.</summary>
    public Task PatientBannedAsync(Guid patientId, Guid branchId, CancellationToken ct) =>
        notifications.NotifyPatientAsync(patientId, branchId, Notifications.NotificationTypes.PatientBanned, "تم إيقاف الحجز",
            "تجاوزت حد الغياب المسموح. يُفك الإيقاف بدفع العربون من التطبيق أو الاستقبال.", null, null, ct);

    /// <summary>
    /// الاستدعاء: شاشة الانتظار، وتنبيه المريض بصوت مستمر حتى يضغط "أنا قادم" (Push عالي الأولوية + حدث مباشر للتطبيق المفتوح).
    /// إذا لم يكن له تطبيق تصله SMS.
    /// </summary>
    public async Task TicketCalledAsync(QueueTicket ticket, CancellationToken ct)
    {
        var doctor = await db.Doctors.AsNoTracking().Include(d => d.Clinic).FirstAsync(d => d.DoctorId == ticket.DoctorId, ct);
        var displays = await db.Devices.AsNoTracking()
            .Where(d => d.Kind == "display" && d.IsActive && d.ClinicId == doctor.ClinicId).Select(d => d.DeviceId).ToListAsync(ct);
        var payload = new { ticket_number = ticket.TicketNumber, clinic_name = doctor.Clinic!.Name, doctor_name = doctor.FullName, room = doctor.Clinic.Floor };
        foreach (var id in displays)
            await realtime.SendAsync(RealtimeGroups.Display(id), "ticket.called", payload);
        await CallAlertAsync(ticket, doctor, repeat: false, ct);
    }

    /// <summary>تكرار تنبيه الاستدعاء (المهمة الدورية كل دقيقة) لمن لم يضغط "أنا قادم".</summary>
    public async Task CallAlertRepeatAsync(QueueTicket ticket, CancellationToken ct)
    {
        var doctor = await db.Doctors.AsNoTracking().Include(d => d.Clinic).FirstAsync(d => d.DoctorId == ticket.DoctorId, ct);
        await CallAlertAsync(ticket, doctor, repeat: true, ct);
    }

    private async Task CallAlertAsync(QueueTicket ticket, Doctor doctor, bool repeat, CancellationToken ct)
    {
        var place = $"{doctor.FullName} - {doctor.Clinic!.Name}" + (doctor.Clinic.Floor is { } floor ? $"، الطابق {floor}" : "");
        var title = repeat ? "تذكير: حان دورك الآن" : "تفضل للدخول";
        var message = $"حان دورك (رقم {ticket.TicketNumber}). تفضل إلى {place}";
        await notifications.NotifyPatientAsync(ticket.PatientId, ticket.BranchId,
            repeat ? Notifications.NotificationTypes.QueueCallRepeat : Notifications.NotificationTypes.QueueCalled, title, message,
            "queue_ticket", ticket.TicketId, new Notifications.PushOptions("queue_alarm", "high", RequireAck: true), ct);
        var accounts = await db.PatientAccountLinks.AsNoTracking().Where(l => l.PatientId == ticket.PatientId)
            .Select(l => l.PatientAccountId).Distinct().ToListAsync(ct);
        foreach (var a in accounts)
            await realtime.SendAsync(RealtimeGroups.PatientAccount(a), "queue.call_alert", new
            {
                ticket_id = ticket.TicketId, ticket_number = ticket.TicketNumber, booking_id = ticket.BookingId, doctor_name = doctor.FullName,
                clinic_name = doctor.Clinic.Name, floor = doctor.Clinic.Floor, title, message, sound = "queue_alarm", require_ack = true, repeat,
            });
    }
}
