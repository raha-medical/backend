using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Queue;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.PatientApp;

public record MyQueueTicketDto(Guid TicketId, Guid PatientId, string PatientName, Guid? BookingId, string TicketNumber, string Source,
    string Status, short? QueuePosition, int? PatientsAhead, DateTimeOffset? EstimatedTime, string DoctorName, string ClinicName, short? Floor,
    DateTimeOffset? CalledAt, DateTimeOffset? CallAcknowledgedAt, DateTimeOffset? OnTheWayAt);

public record AcknowledgeCallResponse(Guid TicketId, string Status, DateTimeOffset CallAcknowledgedAt);

/// <summary>
/// تذاكر اليوم لمرضى الحساب (بالحجز أو من الاستقبال مباشرة)، وزر "أنا قادم" الذي يوقف تنبيه الاستدعاء المستمر.
/// </summary>
public class MyQueueService(IAppDbContext db, IClock clock, PatientPortalService portal, QueueNotifier notifier)
{
    public async Task<List<MyQueueTicketDto>> ActiveAsync(CancellationToken ct)
    {
        var today = clock.BaghdadToday();
        var linked = portal.LinkedPatientIds();
        var rows = await (
            from t in db.QueueTickets.AsNoTracking()
            join s in db.DoctorSessions.AsNoTracking() on t.SessionId equals s.SessionId
            join d in db.Doctors.AsNoTracking() on t.DoctorId equals d.DoctorId
            join p in db.Patients.AsNoTracking() on t.PatientId equals p.PatientId
            where linked.Contains(t.PatientId) && s.SessionDate == today && TicketStatuses.Active.Contains(t.Status)
            orderby t.EstimatedTime
            select new { Ticket = t, PatientName = p.FullName, DoctorName = d.FullName, ClinicName = d.Clinic!.Name, d.Clinic.Floor })
            .ToListAsync(ct);
        return rows.Select(r => new MyQueueTicketDto(r.Ticket.TicketId, r.Ticket.PatientId, r.PatientName, r.Ticket.BookingId, r.Ticket.TicketNumber,
            r.Ticket.Source, r.Ticket.Status, r.Ticket.QueuePosition,
            r.Ticket.Status is TicketStatuses.Waiting or TicketStatuses.Arrived && r.Ticket.QueuePosition is { } pos ? pos - 1 : null,
            r.Ticket.EstimatedTime, r.DoctorName, r.ClinicName, r.Floor, r.Ticket.CalledAt, r.Ticket.CallAcknowledgedAt, r.Ticket.OnTheWayAt)).ToList();
    }

    public async Task<AcknowledgeCallResponse> AcknowledgeCallAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await db.QueueTickets.FirstOrDefaultAsync(t => t.TicketId == ticketId, ct)
                     ?? throw new AppException(404, "TICKET_NOT_FOUND", "التذكرة غير موجودة");
        await portal.EnsureLinkedAsync(ticket.PatientId, ct);
        if (ticket.CallAcknowledgedAt is { } done)
            return new AcknowledgeCallResponse(ticket.TicketId, ticket.Status, done);
        if (ticket.Status != TicketStatuses.Called)
            throw new AppException(409, "TICKET_NOT_CALLED", "لم يتم استدعاؤك بعد أو انتهى الاستدعاء");
        var now = clock.UtcNow;
        // PostgreSQL يحفظ الوقت بدقة الميكروثانية، فنقص الأجزاء الأصغر ليطابق الرد ما يُحفظ
        ticket.CallAcknowledgedAt = now.AddTicks(-(now.Ticks % 10));
        await db.SaveChangesAsync(ct);
        await notifier.SessionChangedAsync(ticket.SessionId, ct);
        return new AcknowledgeCallResponse(ticket.TicketId, ticket.Status, ticket.CallAcknowledgedAt.Value);
    }
}
