using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Queue;

public record ReceptionTodayQuery(DateOnly? Date, Guid? DoctorId, Guid? ClinicId, string? Status, string? PaymentStatus, string? Q, int? Page, int? PageSize);

/// <summary>
/// صف في جدول «مرضى اليوم» للاستقبال: حجز أو مراجع بدون حجز (كل صف تذكرة طابور).
/// status الموحد: booked | arrived | in_room | completed | cancelled | no_show. payment_status: paid | unpaid | partial | deferred | refunded.
/// actions: الأزرار المسموحة الآن (check_in، late_arrival، cancel، reschedule، no_show، collect_payment).
/// </summary>
public record ReceptionRowDto(Guid TicketId, Guid? BookingId, string TicketNumber, Guid PatientId, string PatientName, string PhoneNumber,
    string FileNumber, DateOnly Date, DateTimeOffset? ScheduledTime, DateTimeOffset? EstimatedTime, DateTimeOffset? ArrivedAt,
    Guid DoctorId, string DoctorName, Guid ClinicId, string ClinicName, Guid SessionId, string Source, string Status, string TicketStatus,
    string? BookingStatus, string PaymentStatus, Guid? InvoiceId, long AmountDue, short? QueuePosition, string Priority, bool IsExtra,
    string? CheckInMethod, int RescheduleCount, IReadOnlyList<string> Actions);

public record ReceptionSummaryDto(int Total, int Booked, int Arrived, int InRoom, int Completed, int Cancelled, int NoShow);
public record ReceptionTodayDto(DateOnly Date, IReadOnlyList<ReceptionRowDto> Items, int Page, int PageSize, int Total, ReceptionSummaryDto Summary);

/// <summary>جدول مرضى اليوم في الاستقبال (المرحلة 3): الحجوزات والحضور المباشر لعيادات الموظف في فرعه الحالي.</summary>
public class ReceptionService(IAppDbContext db, ICurrentUser current, IClock clock)
{
    public static readonly string[] Statuses = ["booked", "arrived", "in_room", "completed", "cancelled", "no_show"];
    public static readonly string[] PaymentStatusValues = ["paid", "unpaid", "partial", "deferred", "refunded"];

    public async Task<ReceptionTodayDto> TodayAsync(ReceptionTodayQuery q, CancellationToken ct)
    {
        new Validator()
            .Check(q.Status is null || q.Status.Split(',').All(s => Statuses.Contains(s.Trim())), "status", string.Join(" | ", Statuses))
            .Check(q.PaymentStatus is null || PaymentStatusValues.Contains(q.PaymentStatus), "payment_status", string.Join(" | ", PaymentStatusValues))
            .Check(q.Page is null or >= 1, "page", "1 فأكثر")
            .Check(q.PageSize is null or (>= 1 and <= 100), "page_size", "من 1 إلى 100")
            .ThrowIfInvalid();
        var day = q.Date ?? clock.BaghdadToday();
        var today = clock.BaghdadToday();

        // الفرع الحالي فقط، والموظف بنطاقه: الطبيب طابوره، وموظف الاستقبال عياداته
        var doctors = db.Doctors.AsNoTracking().Where(d => d.Clinic!.BranchId == current.BranchId);
        if (current.Role == Roles.Doctor) doctors = doctors.Where(d => d.UserId == current.UserId);
        else if (current.Role == Roles.Receptionist)
        {
            var mine = db.UserClinics.Where(c => c.UserId == current.UserId).Select(c => c.ClinicId);
            doctors = doctors.Where(d => mine.Contains(d.ClinicId));
        }
        if (q.DoctorId is { } did) doctors = doctors.Where(d => d.DoctorId == did);
        if (q.ClinicId is { } cid) doctors = doctors.Where(d => d.ClinicId == cid);
        var doctorList = await doctors.Include(d => d.Clinic).ToListAsync(ct);
        var doctorIds = doctorList.Select(d => d.DoctorId).ToList();

        var sessions = await db.DoctorSessions.AsNoTracking()
            .Where(s => s.BranchId == current.BranchId && s.SessionDate == day && doctorIds.Contains(s.DoctorId)).ToListAsync(ct);
        var sessionIds = sessions.Select(s => s.SessionId).ToList();
        var tickets = await db.QueueTickets.AsNoTracking().Include(t => t.Patient)
            .Where(t => sessionIds.Contains(t.SessionId)).ToListAsync(ct);
        var ticketIds = tickets.Select(t => t.TicketId).ToList();
        var bookingIds = tickets.Where(t => t.BookingId != null).Select(t => t.BookingId!.Value).ToList();
        var bookings = await db.Bookings.AsNoTracking().Where(b => bookingIds.Contains(b.BookingId)).ToDictionaryAsync(b => b.BookingId, ct);
        var items = (await db.InvoiceItems.AsNoTracking()
                .Where(i => i.TicketId != null && ticketIds.Contains(i.TicketId.Value) && i.Status != InvoiceItemStatuses.Void).ToListAsync(ct))
            .GroupBy(i => i.TicketId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First());
        var sessionById = sessions.ToDictionary(s => s.SessionId);
        var doctorById = doctorList.ToDictionary(d => d.DoctorId);
        var canCollect = current.Has(Permissions.PaymentsCollect);

        var rows = tickets.Select(t =>
        {
            var s = sessionById[t.SessionId];
            var d = doctorById[t.DoctorId];
            Booking? b = t.BookingId is { } bid ? bookings.GetValueOrDefault(bid) : null;
            items.TryGetValue(t.TicketId, out var item);
            var status = UnifiedStatus(t);
            var pay = PaymentStatus(item, b);
            var due = item is not null && pay is "unpaid" ? item.NetPrice : 0;
            var actions = new List<string>();
            if (b?.Status == BookingStatuses.Booked)
            {
                if (day == today && t.Status == TicketStatuses.Waiting) actions.Add("check_in");
                actions.Add("cancel");
                actions.Add("reschedule");
            }
            if (t.Status == TicketStatuses.Skipped) actions.Add("late_arrival");
            if (t.Status is TicketStatuses.Waiting or TicketStatuses.Arrived or TicketStatuses.Called or TicketStatuses.Skipped && day == today)
                actions.Add("no_show");
            if (canCollect && item is not null && pay is "unpaid") actions.Add("collect_payment");
            return new ReceptionRowDto(t.TicketId, t.BookingId, t.TicketNumber, t.PatientId, t.Patient!.FullName, t.Patient.PhoneNumber,
                t.Patient.FileNumber, s.SessionDate, t.BookingId is null ? null : QueueEngine.ScheduledTime(s, b?.SequenceNumber ?? t.SequenceNumber),
                t.EstimatedTime, t.ArrivedAt, d.DoctorId, d.FullName, d.ClinicId, d.Clinic!.Name, s.SessionId, t.Source, status, t.Status,
                b?.Status, pay, item?.InvoiceId, due, t.QueuePosition, t.Priority, t.IsExtra, b?.CheckInMethod, b?.RescheduleCount ?? 0, actions);
        }).ToList();

        var summary = new ReceptionSummaryDto(rows.Count, rows.Count(r => r.Status == "booked"), rows.Count(r => r.Status == "arrived"),
            rows.Count(r => r.Status == "in_room"), rows.Count(r => r.Status == "completed"), rows.Count(r => r.Status == "cancelled"),
            rows.Count(r => r.Status == "no_show"));

        IEnumerable<ReceptionRowDto> filtered = rows;
        if (q.Status is { } st)
        {
            var wanted = st.Split(',').Select(x => x.Trim()).ToHashSet();
            filtered = filtered.Where(r => wanted.Contains(r.Status));
        }
        if (q.PaymentStatus is { } ps) filtered = filtered.Where(r => r.PaymentStatus == ps);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            var phone = PhoneNumbers.Normalize(term);
            filtered = filtered.Where(r => r.PatientName.Contains(term, StringComparison.OrdinalIgnoreCase)
                                           || r.FileNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                                           || r.TicketNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                                           || r.PhoneNumber.Contains(term) || (phone is not null && r.PhoneNumber == phone));
        }
        // الترتيب: موعد الحجز (أو وقت الحضور للمراجع بدون حجز)، ثم رقم التذكرة
        var ordered = filtered.OrderBy(r => r.ScheduledTime ?? r.ArrivedAt ?? r.EstimatedTime ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.TicketNumber).ToList();
        var page = q.Page ?? 1;
        var size = q.PageSize ?? 50;
        return new ReceptionTodayDto(day, ordered.Skip((page - 1) * size).Take(size).ToList(), page, size, ordered.Count, summary);
    }

    public static string UnifiedStatus(QueueTicket t) => t.Status switch
    {
        TicketStatuses.Waiting => "booked",
        TicketStatuses.Arrived or TicketStatuses.Called => "arrived",
        TicketStatuses.InRoom => "in_room",
        TicketStatuses.Completed => "completed",
        TicketStatuses.NoShow => "no_show",
        TicketStatuses.Cancelled => "cancelled",
        // المتخطى: واصل إن كان ثبّت حضوره، وإلا ما زال محجوزاً لم يصل (متأخر)
        TicketStatuses.Skipped => t.ArrivedAt is null ? "booked" : "arrived",
        _ => t.Status,
    };

    /// <summary>قبل الوصول (لا فاتورة): من حالة دفع الحجز. بعده: من بند الكشفية.</summary>
    public static string PaymentStatus(InvoiceItem? item, Booking? b)
    {
        if (item is not null)
            return item.Status switch
            {
                InvoiceItemStatuses.Paid or InvoiceItemStatuses.PartiallyRefunded => "paid",
                InvoiceItemStatuses.Deferred => "deferred",
                InvoiceItemStatuses.Refunded => "refunded",
                _ => b?.PaymentStatus == PaymentStatuses.DepositPaid ? "partial" : "unpaid",
            };
        return b?.PaymentStatus switch
        {
            PaymentStatuses.Paid => "paid",
            PaymentStatuses.DepositPaid => "partial",
            PaymentStatuses.Refunded or PaymentStatuses.Credited => "refunded",
            _ => "unpaid",
        };
    }
}
