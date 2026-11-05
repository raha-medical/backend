using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Reports;

public record EarningGroup(int Count, long Gross, long DoctorShare);
/// <summary>
/// البنود المحصّلة للطبيب في الفترة (حسب وقت الدفع)، بالنسبة المسجلة لحظة الفوترة.
/// refund_adjustments: ما خُصم من حصته بسبب استرجاع تمت الموافقة عليه في الفترة (سالب).
/// </summary>
public record DoctorEarningsDto(Guid DoctorId, string DoctorName, string? ClinicName, DateOnly From, DateOnly To, EarningGroup NewVisits,
    EarningGroup FollowUps, EarningGroup OtherServices, long RefundAdjustments, long TotalDoctorShare, long TotalComplexShare);
public record DoctorEarningsList(DateOnly From, DateOnly To, IReadOnlyList<DoctorEarningsDto> Items, long TotalDoctorShare, long TotalComplexShare);

public record NoShowDoctorRow(Guid DoctorId, string DoctorName, string? ClinicName, int Count);
/// <summary>
/// الغياب حسب الطبيب في الفترة. الحظر عام على المريض في كل الفروع، فأرقام الحظر وفكه تشمل كل الفروع.
/// </summary>
public record NoShowReport(DateOnly From, DateOnly To, int TotalNoShows, int DistinctPatients, IReadOnlyList<NoShowDoctorRow> ByDoctor,
    int PatientsBanned, int PatientsUnbanned, int UnbannedByDeposit, int UnbannedByWaiver, int CurrentlyBanned);

public class DoctorReportService(IAppDbContext db, ICurrentUser current, ReportScope scope)
{
    /// <summary>
    /// صلاحية reports.finance: أي طبيب في فروعه، وبدون doctor_id قائمة كل الأطباء.
    /// الطبيب بدون هذه الصلاحية يرى أرباحه هو فقط.
    /// </summary>
    public async Task<object> EarningsAsync(Guid? doctorId, DateOnly? from, DateOnly? to, Guid? branchId, CancellationToken ct)
    {
        var period = scope.Period(from, to);
        IReadOnlyList<Guid>? branches;
        if (current.Has(Permissions.ReportsFinance)) branches = await scope.BranchesAsync(branchId, ct);
        else if (current.Role == Roles.Doctor)
        {
            var own = await db.Doctors.AsNoTracking().Where(d => d.UserId == current.UserId).Select(d => (Guid?)d.DoctorId).FirstOrDefaultAsync(ct)
                      ?? throw AppException.Forbidden("لا يوجد ملف طبيب لهذا الحساب");
            if (doctorId is { } asked && asked != own) throw AppException.Forbidden("ترى أرباحك فقط");
            doctorId = own;
            branches = null; // كل فروع الطبيب
        }
        else throw AppException.Forbidden();

        var visibleBranches = branches is null || current.Role == Roles.Admin ? null : branches.ToList();
        if (doctorId is { } id && !await db.Doctors.AnyAsync(d => d.DoctorId == id
                && (visibleBranches == null || visibleBranches.Contains(d.Clinic!.BranchId)), ct))
            throw AppException.NotFound("الطبيب غير موجود");
        var list = await ComputeAsync(doctorId, branches, period, ct);
        if (doctorId is { } one)
            return list.FirstOrDefault() ?? await EmptyAsync(one, period, ct);
        return new DoctorEarningsList(period.From, period.To, list, list.Sum(x => x.TotalDoctorShare), list.Sum(x => x.TotalComplexShare));
    }

    public async Task<List<DoctorEarningsDto>> ComputeAsync(Guid? doctorId, IReadOnlyList<Guid>? branches, ReportPeriod period, CancellationToken ct)
    {
        DateTimeOffset s = period.StartUtc, e = period.EndUtc;
        var ids = branches?.ToList();
        var paid =
            from p in db.Payments.AsNoTracking()
            join a in db.PaymentAllocations on p.PaymentId equals a.PaymentId
            join i in db.InvoiceItems on a.InvoiceItemId equals i.InvoiceItemId
            where p.CreatedAt >= s && p.CreatedAt < e && i.DoctorId != null
            select new { p.BranchId, i.DoctorId, i.ItemType, a.Amount, i.NetPrice, i.DoctorShare, i.ComplexShare };
        if (doctorId is { } d) paid = paid.Where(x => x.DoctorId == d);
        if (ids is not null) paid = paid.Where(x => ids.Contains(x.BranchId));
        var groups = await paid.GroupBy(x => new { x.DoctorId, x.ItemType })
            .Select(g => new { g.Key.DoctorId, g.Key.ItemType, Count = g.Count(), Gross = g.Sum(x => x.Amount), Doctor = g.Sum(x => x.DoctorShare), Complex = g.Sum(x => x.ComplexShare) })
            .ToListAsync(ct);

        var refunds =
            from r in db.Refunds.AsNoTracking()
            join i in db.InvoiceItems on r.InvoiceItemId equals i.InvoiceItemId
            join inv in db.Invoices on i.InvoiceId equals inv.InvoiceId
            where r.Status == RefundStatuses.Completed && i.DoctorId != null
                  && (r.DecidedAt ?? r.CreatedAt) >= s && (r.DecidedAt ?? r.CreatedAt) < e
            select new { inv.BranchId, i.DoctorId, r.DoctorShareAdjustment, r.ComplexShareAdjustment };
        if (doctorId is { } d2) refunds = refunds.Where(x => x.DoctorId == d2);
        if (ids is not null) refunds = refunds.Where(x => ids.Contains(x.BranchId));
        var adjustments = await refunds.GroupBy(x => x.DoctorId)
            .Select(g => new { DoctorId = g.Key, Doctor = g.Sum(x => x.DoctorShareAdjustment), Complex = g.Sum(x => x.ComplexShareAdjustment) })
            .ToListAsync(ct);

        var doctorIds = groups.Select(g => g.DoctorId!.Value).Concat(adjustments.Select(a => a.DoctorId!.Value)).Distinct().ToList();
        var doctors = await db.Doctors.AsNoTracking().Where(x => doctorIds.Contains(x.DoctorId))
            .Select(x => new { x.DoctorId, x.FullName, Clinic = x.Clinic!.Name }).ToListAsync(ct);
        return doctors.Select(doc =>
        {
            var mine = groups.Where(g => g.DoctorId == doc.DoctorId).ToList();
            EarningGroup Of(Func<string, bool> match)
            {
                var rows = mine.Where(g => match(g.ItemType)).ToList();
                return new EarningGroup(rows.Sum(r => r.Count), rows.Sum(r => r.Gross), rows.Sum(r => r.Doctor));
            }
            var adj = adjustments.FirstOrDefault(a => a.DoctorId == doc.DoctorId);
            return new DoctorEarningsDto(doc.DoctorId, doc.FullName, doc.Clinic, period.From, period.To,
                Of(t => t == "new_visit"), Of(t => t == "follow_up"), Of(t => t is not ("new_visit" or "follow_up")),
                adj?.Doctor ?? 0, mine.Sum(g => g.Doctor) + (adj?.Doctor ?? 0), mine.Sum(g => g.Complex) + (adj?.Complex ?? 0));
        }).OrderByDescending(x => x.TotalDoctorShare).ToList();
    }

    private async Task<DoctorEarningsDto> EmptyAsync(Guid doctorId, ReportPeriod period, CancellationToken ct)
    {
        var doc = await db.Doctors.AsNoTracking().Where(x => x.DoctorId == doctorId).Select(x => new { x.FullName, Clinic = x.Clinic!.Name }).FirstAsync(ct);
        var zero = new EarningGroup(0, 0, 0);
        return new DoctorEarningsDto(doctorId, doc.FullName, doc.Clinic, period.From, period.To, zero, zero, zero, 0, 0, 0);
    }

    public async Task<NoShowReport> NoShowsAsync(DateOnly? from, DateOnly? to, Guid? doctorId, Guid? branchId, CancellationToken ct)
    {
        var period = scope.Period(from, to);
        var branches = (await scope.BranchesAsync(branchId, ct)).ToList();
        DateTimeOffset s = period.StartUtc, e = period.EndUtc;

        // الغياب يُسجل على التذكرة أو الحجز، ومنهما الوجبة (الطبيب والفرع)
        var rows = await (
            from ns in db.PatientNoShows.AsNoTracking()
            join t in db.QueueTickets on ns.TicketId equals t.TicketId into tj
            from t in tj.DefaultIfEmpty()
            join b in db.Bookings on ns.BookingId equals b.BookingId into bj
            from b in bj.DefaultIfEmpty()
            where ns.OccurredAt >= s && ns.OccurredAt < e
            select new { ns.PatientId, SessionId = t != null ? (Guid?)t.SessionId : b != null ? (Guid?)b.SessionId : null }).ToListAsync(ct);
        var sessionIds = rows.Where(r => r.SessionId != null).Select(r => r.SessionId!.Value).Distinct().ToList();
        var sessions = await db.DoctorSessions.AsNoTracking().Where(x => sessionIds.Contains(x.SessionId))
            .Select(x => new { x.SessionId, x.DoctorId, x.BranchId }).ToDictionaryAsync(x => x.SessionId, ct);
        var scoped = rows
            .Select(r => new { r.PatientId, Session = r.SessionId is { } sid ? sessions.GetValueOrDefault(sid) : null })
            .Where(r => r.Session is not null && branches.Contains(r.Session.BranchId) && (doctorId is null || r.Session.DoctorId == doctorId))
            .ToList();
        var docIds = scoped.Select(r => r.Session!.DoctorId).Distinct().ToList();
        var doctors = await db.Doctors.AsNoTracking().Where(x => docIds.Contains(x.DoctorId))
            .Select(x => new { x.DoctorId, x.FullName, Clinic = x.Clinic!.Name }).ToDictionaryAsync(x => x.DoctorId, ct);
        var byDoctor = scoped.GroupBy(r => r.Session!.DoctorId)
            .Select(g => new NoShowDoctorRow(g.Key, doctors.GetValueOrDefault(g.Key)?.FullName ?? "", doctors.GetValueOrDefault(g.Key)?.Clinic, g.Count()))
            .OrderByDescending(x => x.Count).ToList();

        var events = await db.PatientBanEvents.AsNoTracking().Where(x => x.CreatedAt >= s && x.CreatedAt < e)
            .GroupBy(x => new { x.Action, x.Method }).Select(g => new { g.Key.Action, g.Key.Method, Count = g.Count() }).ToListAsync(ct);
        var unbans = events.Where(x => x.Action == "unban").ToList();
        var waived = unbans.Where(x => x.Method == "waived").Sum(x => x.Count);
        return new NoShowReport(period.From, period.To, scoped.Count, scoped.Select(r => r.PatientId).Distinct().Count(), byDoctor,
            events.Where(x => x.Action == "ban").Sum(x => x.Count), unbans.Sum(x => x.Count), unbans.Sum(x => x.Count) - waived, waived,
            await db.Patients.CountAsync(p => p.IsBanned && p.MergedIntoPatientId == null, ct));
    }
}
