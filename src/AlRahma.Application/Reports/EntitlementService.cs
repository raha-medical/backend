using AlRahma.Application.Abstractions;
using AlRahma.Application.Branches;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Reports;

public record PayPlanRequest(Guid? UserId, string? Model, decimal? Percent, long? FixedMonthly, long? PerCaseAmount, DateOnly? EffectiveFrom);
public record PayPlanDto(Guid PlanId, Guid UserId, string UserName, string Model, string ModelAr, decimal? Percent, long? FixedMonthly, long? PerCaseAmount, DateOnly EffectiveFrom);
public record PayStaffDto(Guid UserId, string FullName, string Role);
public record EntitlementRow(Guid UserId, string UserName, string Role, string Model, string ModelAr, int Cases, long Basis, long Earned, long Paid, long Balance);
public record EntitlementStatement(Guid BranchId, DateOnly From, DateOnly To, IReadOnlyList<EntitlementRow> Items, long TotalEarned, long TotalPaid, long TotalBalance);
public record PayoutRequest(Guid? UserId, DateOnly? PeriodFrom, DateOnly? PeriodTo, long? Amount, string? Method, string? Note);
public record PayoutDto(Guid PayoutId, Guid UserId, string UserName, DateOnly PeriodFrom, DateOnly PeriodTo, long Amount, string Method, string? Note, string CreatedByName, DateTimeOffset CreatedAt);
public record StatementRow(string Period, long Gross, long Refunds, long Net, long CashIn, long Expenses, long Payouts, long NetAfterCosts, int PaymentsCount);
public record FinancialStatement(DateOnly From, DateOnly To, string Group, IReadOnlyList<Guid> BranchIds, IReadOnlyList<StatementRow> Rows, StatementRow Totals);
public record PaymentRow(Guid PaymentId, string BranchName, string? ReceiptNumber, string PatientName, string FileNumber, long Amount, string Methods, string? CashierName, DateTimeOffset CreatedAt);
public record BranchOverviewRow(Guid BranchId, string Code, string Name, decimal? Latitude, decimal? Longitude, string LiveStatus, string LiveStatusAr, string? HoursToday,
    int PatientsToday, long RevenueToday, int DoctorsPresent, long RevenueMonth, long EntitlementsMonth, int StaffCount, int DoctorsCount, int ClinicsCount, int PatientsMonth,
    decimal? AttendanceRate, decimal? CancellationRate)
{
    /// <summary>المراجعون في الطابور الآن (حضروا ولم يكتمل دورهم).</summary>
    public int QueueNow { get; init; }
    /// <summary>المقبوض اليوم بالدولار (المبلغ الأصلي)، ومقابله بالدينار ضمن revenue_today.</summary>
    public long RevenueTodayUsd { get; init; }
    public long RevenueTodayUsdIqd { get; init; }
    public int OnlineBookingsToday { get; init; }
    public int RefundsToday { get; init; }
    public long RefundsTodayAmount { get; init; }
    public int OpenShifts { get; init; }
    public int ShiftsPendingReview { get; init; }
}
public record BranchesOverview(DateOnly Date, IReadOnlyList<BranchOverviewRow> Items, int Open, int Closed, int OpenAfterHours, int PatientsToday, long RevenueToday, int DoctorsPresent)
{
    public int QueueNow { get; init; }
    public long RevenueTodayUsd { get; init; }
    public long RevenueTodayUsdIqd { get; init; }
    public int OnlineBookingsToday { get; init; }
    public int RefundsToday { get; init; }
    public long RefundsTodayAmount { get; init; }
    public int OpenShifts { get; init; }
    public int ShiftsPendingReview { get; init; }
}

/// <summary>
/// مستحقات الكوادر وصرفها، والكشوفات اليومية والشهرية، ولوحة المدير العام. عزل الفروع عبر ReportScope.
/// مستحق الطبيب بالنسبة هو حصته المسجلة في الفاتورة لحظة الفوترة (الأرباح القائمة)، وخطة الأجر تستبدله بالثابت أو لكل حالة.
/// </summary>
public class EntitlementService(IAppDbContext db, ICurrentUser current, IClock clock, IAuditLogger audit, ReportScope scope, RevenueService revenue,
    DoctorReportService doctors, BranchService branches)
{
    public static readonly string[] Models = ["percentage", "fixed", "per_case"];
    private static string ModelAr(string m) => m switch { "percentage" => "نسبة", "fixed" => "أجر ثابت", "per_case" => "لكل حالة", _ => m };

    private async Task<Guid> OneBranchAsync(Guid? branchId, CancellationToken ct)
    {
        var id = branchId ?? current.BranchId;
        await scope.BranchesAsync(id, ct); // فرع خارج النطاق: 403
        return id;
    }

    private Task<bool> BelongsAsync(Guid userId, Guid branchId, CancellationToken ct) =>
        db.UserBranches.AsNoTracking().AnyAsync(ub => ub.UserId == userId && ub.BranchId == branchId, ct);

    public async Task<IReadOnlyList<PayPlanDto>> PlansAsync(Guid? branchId, CancellationToken ct)
    {
        var id = await OneBranchAsync(branchId, ct);
        var rows = await (from p in db.StaffPayPlans.AsNoTracking()
                          join u in db.Users.AsNoTracking() on p.UserId equals u.UserId
                          where p.BranchId == id
                          orderby u.FullName, p.EffectiveFrom descending
                          select new { p, u.FullName }).ToListAsync(ct);
        return rows.Select(x => ToDto(x.p, x.FullName)).ToList();
    }

    /// <summary>موظفو الفرع النشطون، لاختيار من يُحدد نظام أجره حتى قبل أن يظهر في الكشف.</summary>
    public async Task<IReadOnlyList<PayStaffDto>> PlanStaffAsync(Guid? branchId, CancellationToken ct)
    {
        var id = await OneBranchAsync(branchId, ct);
        return await (from ub in db.UserBranches.AsNoTracking()
                      join u in db.Users.AsNoTracking() on ub.UserId equals u.UserId
                      where ub.BranchId == id && u.IsActive && u.Role != Roles.Admin
                      orderby u.FullName
                      select new PayStaffDto(u.UserId, u.FullName, u.Role)).ToListAsync(ct);
    }

    private static PayPlanDto ToDto(StaffPayPlan p, string name) =>
        new(p.PlanId, p.UserId, name, p.Model, ModelAr(p.Model), p.Percent, p.FixedMonthly, p.PerCaseAmount, p.EffectiveFrom);

    public async Task<PayPlanDto> SavePlanAsync(Guid? branchId, PayPlanRequest req, CancellationToken ct)
    {
        var id = await OneBranchAsync(branchId, ct);
        new Validator()
            .Check(req.UserId is not null, "user_id", "الموظف مطلوب")
            .Check(req.Model is not null && Models.Contains(req.Model), "model", string.Join(" | ", Models))
            .Check(req.Model != "percentage" || req.Percent is >= 0 and <= 100, "percent", "من 0 إلى 100")
            .Check(req.Model != "fixed" || req.FixedMonthly is >= 0, "fixed_monthly", "المبلغ مطلوب")
            .Check(req.Model != "per_case" || req.PerCaseAmount is >= 0, "per_case_amount", "المبلغ مطلوب")
            .ThrowIfInvalid();
        if (!await BelongsAsync(req.UserId!.Value, id, ct)) throw new AppException(404, "USER_NOT_FOUND", "الموظف ليس في هذا الفرع");
        var from = req.EffectiveFrom ?? clock.BaghdadToday();
        var row = await db.StaffPayPlans.FirstOrDefaultAsync(p => p.BranchId == id && p.UserId == req.UserId && p.EffectiveFrom == from, ct);
        var before = row is null ? null : new { row.Model, row.Percent, row.FixedMonthly, row.PerCaseAmount };
        if (row is null)
        {
            row = new StaffPayPlan { PlanId = UuidV7.New(), BranchId = id, UserId = req.UserId.Value, EffectiveFrom = from, CreatedBy = current.UserId, CreatedAt = clock.UtcNow };
            db.StaffPayPlans.Add(row);
        }
        row.Model = req.Model!;
        row.Percent = req.Model == "percentage" ? req.Percent : null;
        row.FixedMonthly = req.Model == "fixed" ? req.FixedMonthly : null;
        row.PerCaseAmount = req.Model == "per_case" ? req.PerCaseAmount : null;
        audit.Add("PAY_PLAN_CHANGED", "pay_plan", row.PlanId, before: before, after: new { row.UserId, row.Model, row.Percent, row.FixedMonthly, row.PerCaseAmount, row.EffectiveFrom });
        await db.SaveChangesAsync(ct);
        var name = await db.Users.AsNoTracking().Where(u => u.UserId == row.UserId).Select(u => u.FullName).FirstAsync(ct);
        return ToDto(row, name);
    }

    public async Task<EntitlementStatement> StatementAsync(Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var id = await OneBranchAsync(branchId, ct);
        var period = scope.Period(from, to);
        DateTimeOffset s = period.StartUtc, e = period.EndUtc;
        var people = await (from ub in db.UserBranches.AsNoTracking()
                            join u in db.Users.AsNoTracking() on ub.UserId equals u.UserId
                            where ub.BranchId == id && u.IsActive && u.Role != Roles.Admin
                            select new { u.UserId, u.FullName, u.Role }).ToListAsync(ct);
        var plans = await db.StaffPayPlans.AsNoTracking().Where(p => p.BranchId == id && p.EffectiveFrom <= period.To).ToListAsync(ct);
        var doctorRows = await db.Doctors.AsNoTracking().Where(d => d.UserId != null).Select(d => new { d.DoctorId, UserId = d.UserId!.Value }).ToListAsync(ct);
        var shares = (await doctors.ComputeAsync(null, [id], period, ct)).ToDictionary(x => x.DoctorId);
        var paid = await db.Payouts.AsNoTracking().Where(p => p.BranchId == id && p.PeriodFrom >= period.From && p.PeriodTo <= period.To)
            .GroupBy(p => p.UserId).Select(g => new { g.Key, Sum = g.Sum(p => p.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);

        var visits = await db.QueueTickets.AsNoTracking()
            .Where(t => t.BranchId == id && t.Status == TicketStatuses.Completed && t.ArrivedAt >= s && t.ArrivedAt < e)
            .Join(db.DoctorSessions.AsNoTracking(), t => t.SessionId, x => x.SessionId, (t, x) => x.DoctorId)
            .GroupBy(d => d).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var released = await (from o in db.MedicalOrders.AsNoTracking()
                              join i in db.InvoiceItems.AsNoTracking() on o.InvoiceItemId equals i.InvoiceItemId
                              where o.BranchId == id && o.ReleasedAt >= s && o.ReleasedAt < e && o.ReleasedBy != null
                              group i.NetPrice by o.ReleasedBy into g
                              select new { g.Key, Count = g.Count(), Sum = g.Sum() }).ToDictionaryAsync(x => x.Key!.Value, ct);
        var dispensed = await db.Prescriptions.AsNoTracking()
            .Where(p => p.DispensedBranchId == id && p.DispensedAt >= s && p.DispensedAt < e && p.DispensedBy != null)
            .GroupBy(p => p.DispensedBy).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.Count, ct);

        var rows = new List<EntitlementRow>();
        foreach (var u in people)
        {
            var plan = plans.Where(p => p.UserId == u.UserId).MaxBy(p => p.EffectiveFrom);
            var doctorId = doctorRows.FirstOrDefault(d => d.UserId == u.UserId)?.DoctorId;
            var share = doctorId is { } d ? shares.GetValueOrDefault(d) : null;
            var cases = doctorId is { } dd ? visits.GetValueOrDefault(dd)
                : (released.TryGetValue(u.UserId, out var r) ? r.Count : 0) + dispensed.GetValueOrDefault(u.UserId);
            var basis = share is not null ? share.NewVisits.Gross + share.FollowUps.Gross + share.OtherServices.Gross : released.TryGetValue(u.UserId, out var rr) ? rr.Sum : 0;
            var model = plan?.Model ?? (doctorId is null ? "none" : "percentage");
            long earned = model switch
            {
                "fixed" => FixedFor(plan!.FixedMonthly ?? 0, period),
                "per_case" => (plan!.PerCaseAmount ?? 0) * cases,
                // نسبة الطبيب محسوبة في الفاتورة لحظة الفوترة، وللكوادر من قيمة ما نفذوه
                "percentage" => doctorId is not null ? share?.TotalDoctorShare ?? 0 : (long)Math.Round(basis * (plan?.Percent ?? 0) / 100m, MidpointRounding.AwayFromZero),
                _ => 0,
            };
            var out_ = paid.GetValueOrDefault(u.UserId);
            if (model == "none" && out_ == 0) continue;
            rows.Add(new EntitlementRow(u.UserId, u.FullName, u.Role, model, model == "none" ? "غير محدد" : ModelAr(model), cases, basis, earned, out_, earned - out_));
        }
        var ordered = rows.OrderByDescending(r => r.Earned).ToList();
        return new EntitlementStatement(id, period.From, period.To, ordered, ordered.Sum(r => r.Earned), ordered.Sum(r => r.Paid), ordered.Sum(r => r.Balance));
    }

    /// <summary>الأجر الثابت الشهري موزعاً على أيام الفترة: كل شهر كامل بقيمته، والجزء بنسبة أيامه من شهره.</summary>
    public static long FixedFor(long monthly, ReportPeriod p)
    {
        decimal total = 0;
        for (var d = p.From; d <= p.To; d = d.AddDays(1)) total += monthly / (decimal)DateTime.DaysInMonth(d.Year, d.Month);
        return (long)Math.Round(total, MidpointRounding.AwayFromZero);
    }

    public async Task<PayoutDto> RecordPayoutAsync(Guid? branchId, PayoutRequest req, CancellationToken ct)
    {
        var id = await OneBranchAsync(branchId, ct);
        new Validator()
            .Check(req.UserId is not null, "user_id", "الموظف مطلوب")
            .Check(req.PeriodFrom is not null && req.PeriodTo is not null && req.PeriodTo >= req.PeriodFrom, "period_to", "الفترة مطلوبة")
            .Check(req.Amount is > 0, "amount", "المبلغ يجب أن يكون أكبر من صفر")
            .Check(req.Method is "cash" or "transfer", "method", "cash | transfer")
            .Check(req.Note is null || req.Note.Length <= 500, "note", "500 حرف كحد أقصى")
            .ThrowIfInvalid();
        if (!await BelongsAsync(req.UserId!.Value, id, ct)) throw new AppException(404, "USER_NOT_FOUND", "الموظف ليس في هذا الفرع");
        var row = new Payout
        {
            PayoutId = UuidV7.New(), BranchId = id, UserId = req.UserId.Value, PeriodFrom = req.PeriodFrom!.Value, PeriodTo = req.PeriodTo!.Value, Amount = req.Amount!.Value,
            Method = req.Method!, Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(), CreatedBy = current.UserId, CreatedAt = clock.UtcNow,
        };
        db.Payouts.Add(row);
        audit.Add("PAYOUT_RECORDED", "payout", row.PayoutId, after: new { row.BranchId, row.UserId, row.PeriodFrom, row.PeriodTo, row.Amount, row.Method });
        await db.SaveChangesAsync(ct);
        return (await PayoutRowsAsync(db.Payouts.AsNoTracking().Where(p => p.PayoutId == row.PayoutId), ct))[0];
    }

    public async Task<IReadOnlyList<PayoutDto>> PayoutsAsync(Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var id = await OneBranchAsync(branchId, ct);
        var period = scope.Period(from, to);
        return await PayoutRowsAsync(db.Payouts.AsNoTracking().Where(p => p.BranchId == id && p.CreatedAt >= period.StartUtc && p.CreatedAt < period.EndUtc), ct);
    }

    private async Task<List<PayoutDto>> PayoutRowsAsync(IQueryable<Payout> q, CancellationToken ct)
    {
        var rows = await (from p in q
                          join u in db.Users.AsNoTracking() on p.UserId equals u.UserId
                          join c in db.Users.AsNoTracking() on p.CreatedBy equals c.UserId
                          orderby p.CreatedAt descending
                          select new { p, Name = u.FullName, By = c.FullName }).ToListAsync(ct);
        return rows.Select(x => new PayoutDto(x.p.PayoutId, x.p.UserId, x.Name, x.p.PeriodFrom, x.p.PeriodTo, x.p.Amount, x.p.Method, x.p.Note, x.By, x.p.CreatedAt)).ToList();
    }

    /// <summary>كشف يومي أو شهري. الفترة اليومية حتى 62 يوماً.</summary>
    public async Task<FinancialStatement> FinancialStatementAsync(Guid? branchId, DateOnly? from, DateOnly? to, string? group, CancellationToken ct)
    {
        var period = scope.Period(from, to);
        var g = group ?? "day";
        new Validator().Check(g is "day" or "month", "group", "day | month")
            .Check(g != "day" || period.To.DayNumber - period.From.DayNumber <= 62, "to", "الكشف اليومي 62 يوماً كحد أقصى").ThrowIfInvalid();
        var ids = await scope.BranchesAsync(branchId, ct);
        var buckets = new List<(string Label, ReportPeriod P)>();
        for (var d = period.From; d <= period.To;)
        {
            var end = g == "day" ? d : new DateOnly(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
            if (end > period.To) end = period.To;
            buckets.Add((g == "day" ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM"), new ReportPeriod(d, end)));
            d = end.AddDays(1);
        }
        var rows = new List<StatementRow>();
        var list = ids.ToList();
        foreach (var (label, p) in buckets)
        {
            var n = await revenue.NumbersAsync(ids, p, ct);
            long Sum(Func<Guid, long> f) => ids.Sum(f);
            var payouts = await db.Payouts.AsNoTracking().Where(x => list.Contains(x.BranchId) && x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc).SumAsync(x => (long?)x.Amount, ct) ?? 0;
            var net = Sum(n.NetOf);
            var expenses = Sum(b => n.Expenses.GetValueOrDefault(b));
            rows.Add(new StatementRow(label, Sum(n.GrossOf), Sum(n.RefundsOf), net, Sum(n.CashInOf), expenses, payouts, net - expenses - payouts, ids.Sum(b => n.PaymentsCount.GetValueOrDefault(b))));
        }
        var t = new StatementRow("total", rows.Sum(r => r.Gross), rows.Sum(r => r.Refunds), rows.Sum(r => r.Net), rows.Sum(r => r.CashIn), rows.Sum(r => r.Expenses), rows.Sum(r => r.Payouts),
            rows.Sum(r => r.NetAfterCosts), rows.Sum(r => r.PaymentsCount));
        return new FinancialStatement(period.From, period.To, g, ids, rows, t);
    }

    public async Task<PagedResult<PaymentRow>> PaymentsAsync(Guid? branchId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct)
    {
        var period = scope.Period(from, to);
        var ids = (await scope.BranchesAsync(branchId, ct)).ToList();
        var p = new PageQuery(page, pageSize);
        var q = from x in db.Payments.AsNoTracking()
                join b in db.Branches.AsNoTracking() on x.BranchId equals b.BranchId
                join pt in db.Patients.AsNoTracking() on x.PatientId equals pt.PatientId
                join r in db.Receipts.AsNoTracking() on x.ReceiptId equals r.ReceiptId into rj
                from r in rj.DefaultIfEmpty()
                join u in db.Users.AsNoTracking() on x.CreatedBy equals u.UserId into uj
                from u in uj.DefaultIfEmpty()
                where ids.Contains(x.BranchId) && x.CreatedAt >= period.StartUtc && x.CreatedAt < period.EndUtc
                select new { x, Branch = b.Name, pt.FullName, pt.FileNumber, Receipt = r == null ? null : r.ReceiptNumber, Cashier = u == null ? null : u.FullName };
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(o => o.x.CreatedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        var paymentIds = rows.Select(o => o.x.PaymentId).ToList();
        var tenders = (await db.PaymentTenders.AsNoTracking().Where(t => paymentIds.Contains(t.PaymentId)).ToListAsync(ct)).ToLookup(t => t.PaymentId);
        return new PagedResult<PaymentRow>(rows.Select(o => new PaymentRow(o.x.PaymentId, o.Branch, o.Receipt, o.FullName, o.FileNumber, o.x.TotalPaid,
            string.Join("، ", tenders[o.x.PaymentId].Select(t => ArabicFormat.PaymentMethod(t.Method))), o.Cashier, o.x.CreatedAt)).ToList(), p.SafePage, p.SafePageSize, total);
    }

    /// <summary>لوحة المدير العام: لكل فرع حالته الحية ومؤشرات اليوم والشهر.</summary>
    public async Task<BranchesOverview> BranchesOverviewAsync(CancellationToken ct)
    {
        var day = scope.Today;
        var month = new ReportPeriod(new DateOnly(day.Year, day.Month, 1), day);
        var rows = await db.Branches.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Code).ToListAsync(ct);
        var live = await branches.WithStatusAsync(rows, ct, details: true);
        var ids = rows.Select(b => b.BranchId).ToList();
        var today = await revenue.NumbersAsync(ids, new ReportPeriod(day, day), ct);
        var mtd = await revenue.NumbersAsync(ids, month, ct);
        var ticketsToday = await (from t in db.QueueTickets.AsNoTracking()
                                  join s in db.DoctorSessions.AsNoTracking() on t.SessionId equals s.SessionId
                                  where ids.Contains(t.BranchId) && s.SessionDate == day && t.ArrivedAt != null && t.Status != TicketStatuses.Cancelled
                                  select new { t.BranchId, t.PatientId, s.DoctorId }).ToListAsync(ct);
        var bookings = await (from b in db.Bookings.AsNoTracking()
                              join s in db.DoctorSessions.AsNoTracking() on b.SessionId equals s.SessionId
                              where ids.Contains(s.BranchId) && s.SessionDate >= month.From && s.SessionDate <= day
                              select new { s.BranchId, b.Status, b.PatientId }).ToListAsync(ct);
        var shares = new Dictionary<Guid, long>();
        foreach (var id in ids) shares[id] = (await doctors.ComputeAsync(null, [id], month, ct)).Sum(x => x.TotalDoctorShare);
        var staff = await db.UserBranches.AsNoTracking().Where(u => ids.Contains(u.BranchId)).Join(db.Users.AsNoTracking().Where(u => u.IsActive), ub => ub.UserId, u => u.UserId, (ub, u) => ub.BranchId)
            .GroupBy(b => b).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var clinics = await db.Clinics.AsNoTracking().Where(c => ids.Contains(c.BranchId) && c.IsActive).GroupBy(c => c.BranchId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var queueNow = await (from t in db.QueueTickets.AsNoTracking()
                              join s in db.DoctorSessions.AsNoTracking() on t.SessionId equals s.SessionId
                              where ids.Contains(t.BranchId) && s.SessionDate == day && t.ArrivedAt != null
                                    && (t.Status == TicketStatuses.Waiting || t.Status == TicketStatuses.Arrived || t.Status == TicketStatuses.Called || t.Status == TicketStatuses.InRoom)
                              group t by t.BranchId into g
                              select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        DateTimeOffset dayStart = new ReportPeriod(day, day).StartUtc, dayEnd = new ReportPeriod(day, day).EndUtc;
        var usd = await (from p in db.Payments.AsNoTracking()
                         join t in db.PaymentTenders.AsNoTracking() on p.PaymentId equals t.PaymentId
                         where ids.Contains(p.BranchId) && p.CreatedAt >= dayStart && p.CreatedAt < dayEnd && t.Currency == "USD"
                         group t by p.BranchId into g
                         select new { g.Key, Usd = g.Sum(t => t.OriginalAmount ?? 0), Iqd = g.Sum(t => t.Amount) }).ToDictionaryAsync(x => x.Key, ct);
        var online = await (from b in db.Bookings.AsNoTracking()
                            join s in db.DoctorSessions.AsNoTracking() on b.SessionId equals s.SessionId
                            where ids.Contains(s.BranchId) && b.Source == AlRahma.Application.Bookings.BookingService.SourceOnline && b.CreatedAt >= dayStart && b.CreatedAt < dayEnd
                            group b by s.BranchId into g
                            select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var refundsToday = await (from r in db.Refunds.AsNoTracking()
                                  join i in db.InvoiceItems.AsNoTracking() on r.InvoiceItemId equals i.InvoiceItemId
                                  join inv in db.Invoices.AsNoTracking() on i.InvoiceId equals inv.InvoiceId
                                  where r.Status == RefundStatuses.Completed && ids.Contains(inv.BranchId)
                                        && (r.DecidedAt ?? r.CreatedAt) >= dayStart && (r.DecidedAt ?? r.CreatedAt) < dayEnd
                                  group r.Amount by inv.BranchId into g
                                  select new { g.Key, Count = g.Count(), Sum = g.Sum() }).ToDictionaryAsync(x => x.Key, ct);
        var shifts = await db.Shifts.AsNoTracking()
            .Where(s => ids.Contains(s.BranchId) && (s.Status == ShiftStatuses.Open || s.Status == ShiftStatuses.ClosedPendingReview))
            .GroupBy(s => s.BranchId)
            .Select(g => new { g.Key, Open = g.Count(s => s.Status == ShiftStatuses.Open), Pending = g.Count(s => s.Status == ShiftStatuses.ClosedPendingReview) })
            .ToDictionaryAsync(x => x.Key, ct);
        var doctorCount = await db.Doctors.AsNoTracking().Where(d => d.IsActive && ids.Contains(d.Clinic!.BranchId)).GroupBy(d => d.Clinic!.BranchId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var items = live.Select(b =>
        {
            var tk = ticketsToday.Where(t => t.BranchId == b.BranchId).ToList();
            var bk = bookings.Where(x => x.BranchId == b.BranchId).ToList();
            var cancelled = bk.Count(x => x.Status == BookingStatuses.Cancelled);
            var counted = bk.Count - cancelled;
            var attended = bk.Count(x => x.Status is BookingStatuses.CheckedIn or BookingStatuses.Completed);
            var src = rows.First(x => x.BranchId == b.BranchId);
            var hours = b.TodayHours is { Count: > 0 } h ? string.Join("، ", h.Select(x => $"{x.Open}-{x.Close}")) : null;
            return new BranchOverviewRow(b.BranchId, b.Code, b.Name, src.Latitude, src.Longitude, b.LiveStatus, b.LiveStatusAr, hours,
                tk.Select(t => t.PatientId).Distinct().Count(), today.NetOf(b.BranchId), tk.Select(t => t.DoctorId).Distinct().Count(),
                mtd.NetOf(b.BranchId), shares[b.BranchId], staff.GetValueOrDefault(b.BranchId), doctorCount.GetValueOrDefault(b.BranchId), clinics.GetValueOrDefault(b.BranchId),
                bk.Select(x => x.PatientId).Distinct().Count(), counted == 0 ? null : Math.Round(attended * 100m / counted, 1), bk.Count == 0 ? null : Math.Round(cancelled * 100m / bk.Count, 1))
            {
                QueueNow = queueNow.GetValueOrDefault(b.BranchId),
                RevenueTodayUsd = usd.TryGetValue(b.BranchId, out var u) ? u.Usd : 0,
                RevenueTodayUsdIqd = u?.Iqd ?? 0,
                OnlineBookingsToday = online.GetValueOrDefault(b.BranchId),
                RefundsToday = refundsToday.TryGetValue(b.BranchId, out var rf) ? rf.Count : 0,
                RefundsTodayAmount = rf?.Sum ?? 0,
                OpenShifts = shifts.TryGetValue(b.BranchId, out var sh) ? sh.Open : 0,
                ShiftsPendingReview = sh?.Pending ?? 0,
            };
        }).ToList();
        return new BranchesOverview(day, items, items.Count(i => i.LiveStatus == "open"), items.Count(i => i.LiveStatus is not ("open" or "open_after_hours")), items.Count(i => i.LiveStatus == "open_after_hours"),
            items.Sum(i => i.PatientsToday), items.Sum(i => i.RevenueToday), items.Sum(i => i.DoctorsPresent))
        {
            QueueNow = items.Sum(i => i.QueueNow), RevenueTodayUsd = items.Sum(i => i.RevenueTodayUsd), RevenueTodayUsdIqd = items.Sum(i => i.RevenueTodayUsdIqd),
            OnlineBookingsToday = items.Sum(i => i.OnlineBookingsToday), RefundsToday = items.Sum(i => i.RefundsToday), RefundsTodayAmount = items.Sum(i => i.RefundsTodayAmount),
            OpenShifts = items.Sum(i => i.OpenShifts), ShiftsPendingReview = items.Sum(i => i.ShiftsPendingReview),
        };
    }
}
