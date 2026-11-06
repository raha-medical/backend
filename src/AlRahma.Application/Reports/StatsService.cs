using AlRahma.Application.Abstractions;
using AlRahma.Application.Billing;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Reports;

public record StatsDay(DateOnly Date, long Revenue, long Refunds, int Visits, int Bookings, int Cancelled, int NoShows, int NewPatients, long CollectedIqd, long CollectedUsd);
/// <summary>المقبوض بالعملتين منفصلتين: iqd = ما قُبض بالدينار، usd = ما قُبض بالدولار (المبلغ الأصلي)، usd_as_iqd = مقابله بالدينار.</summary>
public record StatsCurrency(long Iqd, long Usd, long UsdAsIqd);
public record StatsSlice(string Key, long Value);
public record StatsBranch(Guid BranchId, string Code, string Name, long Revenue, int Visits, int Patients, int Bookings, int Cancelled, int NoShows);
public record StatsDoctor(Guid DoctorId, string Name, string BranchName, int Visits);
public record StatsTotals(long Revenue, long Refunds, int Visits, int Patients, int NewPatients, int Bookings, int Cancelled, int NoShows,
    decimal? AttendanceRate, decimal? CancellationRate, long AvgPerDay);
public record StatsDto(DateOnly From, DateOnly To, Guid? BranchId, StatsTotals Totals, StatsTotals PreviousTotals, IReadOnlyList<StatsDay> Days,
    IReadOnlyList<StatsSlice> Departments, IReadOnlyList<StatsSlice> Methods, IReadOnlyList<StatsBranch> Branches, IReadOnlyList<StatsDoctor> TopDoctors,
    IReadOnlyList<StatsSlice> Weekdays, StatsCurrency Currency, StatsCurrency PreviousCurrency);

/// <summary>
/// لوحة الإحصائيات: سلسلة يومية للإيراد والزيارات والحجوزات والمراجعين الجدد، وتوزيع الإيراد حسب القسم وطريقة الدفع،
/// ومقارنة الفروع وأكثر الأطباء زيارات، مع مقارنة بالفترة السابقة بنفس الطول. التعريفات نفسها المستخدمة في تقرير الإيراد:
/// الإيراد = مخصصات الدفعات على بنود الفواتير ناقص الاسترجاعات المكتملة. الزيارة = تذكرة طابور حضر صاحبها.
/// المدير العام يرى كل الفروع، وغيره فروعه المسندة فقط (ReportScope).
/// </summary>
public class StatsService(IAppDbContext db, ReportScope scope)
{
    public const int DefaultDays = 30;

    public async Task<StatsDto> GetAsync(DateOnly? from, DateOnly? to, Guid? branchId, CancellationToken ct)
    {
        var end = to ?? scope.Today;
        var period = scope.Period(from ?? end.AddDays(-(DefaultDays - 1)), end);
        var ids = (await scope.BranchesAsync(branchId, ct)).ToList();
        var len = period.To.DayNumber - period.From.DayNumber + 1;
        var prev = new ReportPeriod(period.From.AddDays(-len), period.From.AddDays(-1));

        var cur = await CollectAsync(ids, period, ct);
        var before = await CollectAsync(ids, prev, ct);

        var days = Enumerable.Range(0, len).Select(i => period.From.AddDays(i)).Select(d => new StatsDay(d,
            cur.Gross.Where(x => x.Day == d).Sum(x => x.Amount) - cur.Refunds.Where(x => x.Day == d).Sum(x => x.Amount),
            cur.Refunds.Where(x => x.Day == d).Sum(x => x.Amount),
            cur.Tickets.Count(t => t.Day == d && t.Arrived),
            cur.Bookings.Count(b => b.Day == d && b.Status != BookingStatuses.Cancelled),
            cur.Bookings.Count(b => b.Day == d && b.Status == BookingStatuses.Cancelled),
            cur.Bookings.Count(b => b.Day == d && b.Status == BookingStatuses.NoShow),
            cur.NewPatients.Count(p => p == d),
            cur.Tenders.Where(t => t.Day == d && t.Currency != "USD").Sum(t => t.Amount),
            cur.Tenders.Where(t => t.Day == d && t.Currency == "USD").Sum(t => t.Original))).ToList();

        var departments = RevenueService.Departments.Select(dep => new StatsSlice(dep,
            cur.Gross.Where(x => x.Department == dep).Sum(x => x.Amount) - cur.Refunds.Where(x => x.Department == dep).Sum(x => x.Amount))).ToList();
        // طرق الدفع بالدينار (cash_usd = مقابل الدولار بالدينار؛ المبلغ بالدولار في currency.usd)
        var methods = RevenueService.Methods.Append(TenderMethods.CashUsd).Select(m => new StatsSlice(m, cur.Tenders.Where(t => t.Method == m).Sum(t => t.Amount))).ToList();

        var branches = await db.Branches.AsNoTracking().Where(b => ids.Contains(b.BranchId)).OrderBy(b => b.Code)
            .Select(b => new { b.BranchId, b.Code, b.Name }).ToListAsync(ct);
        var branchRows = branches.Select(b => new StatsBranch(b.BranchId, b.Code, b.Name,
                cur.Gross.Where(x => x.BranchId == b.BranchId).Sum(x => x.Amount) - cur.Refunds.Where(x => x.BranchId == b.BranchId).Sum(x => x.Amount),
                cur.Tickets.Count(t => t.BranchId == b.BranchId && t.Arrived),
                cur.Tickets.Where(t => t.BranchId == b.BranchId && t.Arrived).Select(t => t.PatientId).Distinct().Count(),
                cur.Bookings.Count(x => x.BranchId == b.BranchId && x.Status != BookingStatuses.Cancelled),
                cur.Bookings.Count(x => x.BranchId == b.BranchId && x.Status == BookingStatuses.Cancelled),
                cur.Bookings.Count(x => x.BranchId == b.BranchId && x.Status == BookingStatuses.NoShow)))
            .OrderByDescending(r => r.Revenue).ToList();

        var top = cur.Tickets.Where(t => t.Arrived).GroupBy(t => t.DoctorId).Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).Take(8).ToList();
        var topIds = top.Select(x => x.Key).ToList();
        var docs = await db.Doctors.AsNoTracking().Where(d => topIds.Contains(d.DoctorId))
            .Select(d => new { d.DoctorId, d.FullName, Branch = d.Clinic!.Branch!.Name }).ToListAsync(ct);
        var topDoctors = top.Select(x => { var d = docs.FirstOrDefault(y => y.DoctorId == x.Key); return new StatsDoctor(x.Key, d?.FullName ?? "—", d?.Branch ?? "", x.Count); }).ToList();

        // أيام الأسبوع بترتيب العراق: السبت أولاً
        DayOfWeek[] order = [DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        var weekdays = order.Select(w => new StatsSlice(w.ToString().ToLowerInvariant(), cur.Tickets.Count(t => t.Arrived && t.Day.DayOfWeek == w))).ToList();

        return new StatsDto(period.From, period.To, branchId, Totals(cur, len), Totals(before, len), days, departments, methods, branchRows, topDoctors, weekdays,
            Currency(cur), Currency(before));
    }

    private static StatsCurrency Currency(Data d)
    {
        var usd = d.Tenders.Where(t => t.Currency == "USD").ToList();
        return new StatsCurrency(d.Tenders.Where(t => t.Currency != "USD").Sum(t => t.Amount), usd.Sum(t => t.Original), usd.Sum(t => t.Amount));
    }

    private static StatsTotals Totals(Data d, int len)
    {
        long gross = d.Gross.Sum(x => x.Amount), refunds = d.Refunds.Sum(x => x.Amount);
        var counted = d.Bookings.Count(b => b.Status != BookingStatuses.Cancelled);
        var cancelled = d.Bookings.Count(b => b.Status == BookingStatuses.Cancelled);
        var attended = d.Bookings.Count(b => b.Status is BookingStatuses.CheckedIn or BookingStatuses.Completed);
        var arrived = d.Tickets.Where(t => t.Arrived).ToList();
        return new StatsTotals(gross - refunds, refunds, arrived.Count, arrived.Select(t => t.PatientId).Distinct().Count(), d.NewPatients.Count,
            counted, cancelled, d.Bookings.Count(b => b.Status == BookingStatuses.NoShow),
            counted == 0 ? null : Math.Round(attended * 100m / counted, 1), d.Bookings.Count == 0 ? null : Math.Round(cancelled * 100m / d.Bookings.Count, 1),
            len == 0 ? 0 : (gross - refunds) / len);
    }

    private sealed record Money(Guid BranchId, DateOnly Day, string Department, long Amount);
    private sealed record Tender(DateOnly Day, string Method, string Currency, long Amount, long Original);
    private sealed record Ticket(Guid BranchId, DateOnly Day, Guid DoctorId, Guid PatientId, bool Arrived);
    private sealed record Booking(Guid BranchId, DateOnly Day, string Status);
    private sealed record Data(List<Money> Gross, List<Money> Refunds, List<Tender> Tenders, List<Ticket> Tickets, List<Booking> Bookings, List<DateOnly> NewPatients);

    private static DateOnly Local(DateTimeOffset t) => DateOnly.FromDateTime(t.ToOffset(ClockExtensions.BaghdadOffset).DateTime);

    private async Task<Data> CollectAsync(List<Guid> ids, ReportPeriod period, CancellationToken ct)
    {
        DateTimeOffset s = period.StartUtc, e = period.EndUtc;
        var payments = db.Payments.AsNoTracking().Where(p => ids.Contains(p.BranchId) && p.CreatedAt >= s && p.CreatedAt < e);

        var gross = (await (from p in payments
                            join a in db.PaymentAllocations on p.PaymentId equals a.PaymentId
                            join i in db.InvoiceItems on a.InvoiceItemId equals i.InvoiceItemId
                            select new { p.BranchId, p.CreatedAt, i.ItemType, a.Amount }).ToListAsync(ct))
            .Select(x => new Money(x.BranchId, Local(x.CreatedAt), FinanceCore.DepartmentOf(x.ItemType), x.Amount)).ToList();

        var tenders = (await (from p in payments
                              join t in db.PaymentTenders on p.PaymentId equals t.PaymentId
                              select new { p.CreatedAt, t.Method, t.Currency, t.Amount, t.OriginalAmount }).ToListAsync(ct))
            .Select(x => new Tender(Local(x.CreatedAt), x.Method, x.Currency, x.Amount, x.OriginalAmount ?? 0)).ToList();

        var refunds = (await (from r in db.Refunds.AsNoTracking()
                              join i in db.InvoiceItems on r.InvoiceItemId equals i.InvoiceItemId
                              join inv in db.Invoices on i.InvoiceId equals inv.InvoiceId
                              where r.Status == RefundStatuses.Completed && ids.Contains(inv.BranchId)
                                    && (r.DecidedAt ?? r.CreatedAt) >= s && (r.DecidedAt ?? r.CreatedAt) < e
                              select new { inv.BranchId, At = r.DecidedAt ?? r.CreatedAt, i.ItemType, r.Amount }).ToListAsync(ct))
            .Select(x => new Money(x.BranchId, Local(x.At), FinanceCore.DepartmentOf(x.ItemType), x.Amount)).ToList();

        var tickets = (await (from t in db.QueueTickets.AsNoTracking()
                              join ss in db.DoctorSessions.AsNoTracking() on t.SessionId equals ss.SessionId
                              where ids.Contains(t.BranchId) && ss.SessionDate >= period.From && ss.SessionDate <= period.To && t.Status != TicketStatuses.Cancelled
                              select new { t.BranchId, ss.SessionDate, t.DoctorId, t.PatientId, Arrived = t.ArrivedAt != null }).ToListAsync(ct))
            .Select(x => new Ticket(x.BranchId, x.SessionDate, x.DoctorId, x.PatientId, x.Arrived)).ToList();

        var bookings = (await (from b in db.Bookings.AsNoTracking()
                               join ss in db.DoctorSessions.AsNoTracking() on b.SessionId equals ss.SessionId
                               where ids.Contains(ss.BranchId) && ss.SessionDate >= period.From && ss.SessionDate <= period.To
                               select new { ss.BranchId, ss.SessionDate, b.Status }).ToListAsync(ct))
            .Select(x => new Booking(x.BranchId, x.SessionDate, x.Status)).ToList();

        var newPatients = (await db.Patients.AsNoTracking()
                .Where(p => p.MergedIntoPatientId == null && p.CreatedAt >= s && p.CreatedAt < e && (p.HomeBranchId == null || ids.Contains(p.HomeBranchId.Value)))
                .Select(p => p.CreatedAt).ToListAsync(ct))
            .Select(Local).ToList();

        return new Data(gross, refunds, tenders, tickets, bookings, newPatients);
    }
}
