using AlRahma.Application.Abstractions;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Reports;

public record DashboardMoney(long Gross, long Refunds, long RevenueNet, long CashIn, long Expenses, long NetAfterExpenses, int PaymentsCount);
public record DashboardActivity(int Bookings, int CheckedIn, int Completed, int NoShows);
public record DashboardAlerts(int OpenShifts, int ShiftsPendingReview, int ShiftsWithVariance, long VarianceTotal, int PendingRefunds,
    long PendingRefundsAmount, long DeferralsOutstanding, int OverdueDeferrals);
public record DashboardBranchRow(Guid BranchId, string BranchName, string Code, bool IsActive, bool ClosedToday, DashboardMoney Today,
    DashboardMoney MonthToDate, DashboardActivity Activity, DashboardAlerts Alerts);
public record DashboardTotals(DashboardMoney Today, DashboardMoney MonthToDate, DashboardActivity Activity, DashboardAlerts Alerts);
public record DashboardDto(DateOnly Date, DateOnly MonthFrom, IReadOnlyList<DashboardBranchRow> Branches, DashboardTotals Totals);

/// <summary>
/// لوحة المدير العام: لكل فرع إيراد اليوم ومن بداية الشهر والمصاريف والصافي، ونشاط المرضى اليوم، والتنبيهات التي تحتاج تدخلاً
/// (ورديات مفتوحة أو بانتظار المراجعة أو بفرق، استرجاع بانتظار الموافقة، ديون آجلة ومتأخرة). مدير الفرع يرى فروعه فقط.
/// </summary>
public class DashboardService(IAppDbContext db, ReportScope scope, RevenueService revenue)
{
    public async Task<DashboardDto> GetAsync(DateOnly? date, Guid? branchId, CancellationToken ct)
    {
        var day = date ?? scope.Today;
        var month = new DateOnly(day.Year, day.Month, 1);
        var ids = (await scope.BranchesAsync(branchId, ct)).ToList();
        var branches = await db.Branches.AsNoTracking().Where(b => ids.Contains(b.BranchId)).OrderBy(b => b.Code).ToListAsync(ct);
        var today = await revenue.NumbersAsync(ids, new ReportPeriod(day, day), ct);
        var mtd = await revenue.NumbersAsync(ids, new ReportPeriod(month, day), ct);

        var closed = await db.BranchClosures.AsNoTracking()
            .Where(c => ids.Contains(c.BranchId) && c.EndedAt == null && c.StartsOn <= day && (c.EndsOn == null || c.EndsOn >= day))
            .Select(c => c.BranchId).Distinct().ToListAsync(ct);

        var bookings = await (
            from b in db.Bookings.AsNoTracking()
            join s in db.DoctorSessions on b.SessionId equals s.SessionId
            where ids.Contains(s.BranchId) && s.SessionDate == day && b.Status != BookingStatuses.Cancelled
            group b by s.BranchId into g
            select new { Branch = g.Key, Count = g.Count(), NoShows = g.Count(b => b.Status == BookingStatuses.NoShow) }).ToListAsync(ct);
        var tickets = await (
            from t in db.QueueTickets.AsNoTracking()
            join s in db.DoctorSessions on t.SessionId equals s.SessionId
            where ids.Contains(t.BranchId) && s.SessionDate == day && t.Status != TicketStatuses.Cancelled
            group t by t.BranchId into g
            select new
            {
                Branch = g.Key,
                CheckedIn = g.Count(t => t.ArrivedAt != null),
                Completed = g.Count(t => t.Status == TicketStatuses.Completed),
                NoShows = g.Count(t => t.Status == TicketStatuses.NoShow && t.BookingId == null),
            }).ToListAsync(ct);

        var shifts = await db.Shifts.AsNoTracking()
            .Where(s => ids.Contains(s.BranchId) && (s.Status == ShiftStatuses.Open || s.Status == ShiftStatuses.ClosedPendingReview))
            .GroupBy(s => s.BranchId)
            .Select(g => new
            {
                Branch = g.Key,
                Open = g.Count(s => s.Status == ShiftStatuses.Open),
                Pending = g.Count(s => s.Status == ShiftStatuses.ClosedPendingReview),
                WithVariance = g.Count(s => s.Status == ShiftStatuses.ClosedPendingReview && s.AuditStatus != "MATCHED"),
                Variance = g.Where(s => s.Status == ShiftStatuses.ClosedPendingReview).Sum(s => (s.VarianceCash ?? 0) + (s.VariancePos ?? 0)),
            }).ToListAsync(ct);
        var refunds = await (
            from r in db.Refunds.AsNoTracking()
            join i in db.InvoiceItems on r.InvoiceItemId equals i.InvoiceItemId
            join inv in db.Invoices on i.InvoiceId equals inv.InvoiceId
            where r.Status == RefundStatuses.PendingApproval && ids.Contains(inv.BranchId)
            group r.Amount by inv.BranchId into g
            select new { Branch = g.Key, Count = g.Count(), Sum = g.Sum() }).ToListAsync(ct);
        var deferrals = await (
            from d in db.Deferrals.AsNoTracking()
            join di in db.DeferralItems on d.DeferralId equals di.DeferralId
            join i in db.InvoiceItems on di.InvoiceItemId equals i.InvoiceItemId
            where d.Status == DeferralStatuses.Active && ids.Contains(d.BranchId) && i.Status == InvoiceItemStatuses.Deferred
            group i.NetPrice by d.BranchId into g
            select new { Branch = g.Key, Sum = g.Sum() }).ToListAsync(ct);
        var overdue = await db.Deferrals.AsNoTracking()
            .Where(d => ids.Contains(d.BranchId) && d.Status == DeferralStatuses.Active && d.DueDate != null && d.DueDate < day)
            .GroupBy(d => d.BranchId).Select(g => new { Branch = g.Key, Count = g.Count() }).ToListAsync(ct);

        var rows = branches.Select(b =>
        {
            var bk = bookings.FirstOrDefault(x => x.Branch == b.BranchId);
            var tk = tickets.FirstOrDefault(x => x.Branch == b.BranchId);
            var sh = shifts.FirstOrDefault(x => x.Branch == b.BranchId);
            var rf = refunds.FirstOrDefault(x => x.Branch == b.BranchId);
            return new DashboardBranchRow(b.BranchId, b.Name, b.Code, b.IsActive, closed.Contains(b.BranchId), Money(b.BranchId, today), Money(b.BranchId, mtd),
                new DashboardActivity(bk?.Count ?? 0, tk?.CheckedIn ?? 0, tk?.Completed ?? 0, (bk?.NoShows ?? 0) + (tk?.NoShows ?? 0)),
                new DashboardAlerts(sh?.Open ?? 0, sh?.Pending ?? 0, sh?.WithVariance ?? 0, sh?.Variance ?? 0, rf?.Count ?? 0, rf?.Sum ?? 0,
                    deferrals.FirstOrDefault(x => x.Branch == b.BranchId)?.Sum ?? 0, overdue.FirstOrDefault(x => x.Branch == b.BranchId)?.Count ?? 0));
        }).ToList();

        var totals = new DashboardTotals(Sum(rows.Select(r => r.Today)), Sum(rows.Select(r => r.MonthToDate)),
            new DashboardActivity(rows.Sum(r => r.Activity.Bookings), rows.Sum(r => r.Activity.CheckedIn), rows.Sum(r => r.Activity.Completed),
                rows.Sum(r => r.Activity.NoShows)),
            new DashboardAlerts(rows.Sum(r => r.Alerts.OpenShifts), rows.Sum(r => r.Alerts.ShiftsPendingReview), rows.Sum(r => r.Alerts.ShiftsWithVariance),
                rows.Sum(r => r.Alerts.VarianceTotal), rows.Sum(r => r.Alerts.PendingRefunds), rows.Sum(r => r.Alerts.PendingRefundsAmount),
                rows.Sum(r => r.Alerts.DeferralsOutstanding), rows.Sum(r => r.Alerts.OverdueDeferrals)));
        return new DashboardDto(day, month, rows, totals);
    }

    private static DashboardMoney Money(Guid b, RevenueNumbers n)
    {
        var r = RevenueService.Row(b, "", n);
        return new DashboardMoney(r.Gross, r.Refunds, r.TotalNet, r.CashIn, r.Expenses, r.NetAfterExpenses, r.PaymentsCount);
    }

    private static DashboardMoney Sum(IEnumerable<DashboardMoney> items)
    {
        var list = items.ToList();
        return new DashboardMoney(list.Sum(x => x.Gross), list.Sum(x => x.Refunds), list.Sum(x => x.RevenueNet), list.Sum(x => x.CashIn),
            list.Sum(x => x.Expenses), list.Sum(x => x.NetAfterExpenses), list.Sum(x => x.PaymentsCount));
    }
}
