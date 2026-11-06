using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Billing;

public record PrintLine(string Text, long Amount, string? DoctorName = null, string? TicketNumber = null);
public record PrintTender(string Method, long Amount)
{
    /// <summary>اسم طريقة الدفع بالعربي للطباعة.</summary>
    public string MethodAr => ArabicFormat.PaymentMethod(Method);
}
public record PrintPayloadDto(string ReceiptNumber, string Type, string TypeAr, string BranchName, DateTimeOffset IssuedAt, string CashierName,
    string? PatientName, string? FileNumber, IReadOnlyList<PrintLine> Lines, long DepositUsed, IReadOnlyList<PrintTender> Tenders, long Total,
    string QrCode, string Footer, bool IsCopy, int CopyNumber,
    string? ClinicName = null, string? DoctorName = null, string? TicketNumber = null, string? LogoUrl = null)
{
    /// <summary>حقول العرض بتوقيت بغداد؛ issued_at بتوقيت UTC قد يقع في يوم آخر قرب منتصف الليل.</summary>
    public string IssuedDate => ArabicFormat.IsoDate(IssuedAt);
    public string IssuedDateAr => ArabicFormat.Date(IssuedAt);
    public string IssuedTimeAr => ArabicFormat.Time(IssuedAt);
}
/// <summary>إيصالات الوردية لإعادة الطباعة من شاشة الاستقبال.</summary>
public record ShiftReceiptItem(Guid ReceiptId, string ReceiptNumber, string Type, string TypeAr, DateTimeOffset IssuedAt, long Total, string Status,
    string? PatientName, string? FileNumber, string? DoctorName, string? TicketNumber, int ReprintCount, bool CanReprint, string PrintPayloadUrl);
public record ReprintResponse(string PrintPayloadUrl, int CopyNumber);
public record VerifyReceiptDto(bool Valid, string ReceiptNumber, string Type, DateTimeOffset IssuedAt, long Total, string BranchName, string Status)
{
    public string IssuedDateAr => ArabicFormat.Date(IssuedAt);
    public string IssuedTimeAr => ArabicFormat.Time(IssuedAt);
}

/// <summary>بيانات طباعة الإيصال، وإعادة الطباعة (بدون فتح الدرج)، والتحقق العام من الـ QR.</summary>
public class ReceiptService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, FinanceCore finance, IClock clock)
{
    private static readonly IReadOnlyDictionary<string, string> TypeNames = new Dictionary<string, string>
    {
        [ReceiptTypes.Payment] = "إيصال قبض",
        [ReceiptTypes.Refund] = "إيصال استرجاع",
        [ReceiptTypes.Deposit] = "إيصال عربون",
        [ReceiptTypes.Online] = "إيصال دفع إلكتروني",
    };

    public async Task<PrintPayloadDto> PrintPayloadAsync(Guid receiptId, int? copy, CancellationToken ct)
    {
        var r = await LoadAsync(receiptId, ct);
        var branch = await db.Branches.Where(b => b.BranchId == r.BranchId).Select(b => b.Name).FirstAsync(ct);
        var cashier = r.IssuedBy is null ? "" : await db.Users.Where(u => u.UserId == r.IssuedBy).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "";
        var patient = r.PatientId is null ? null
            : await db.Patients.Where(p => p.PatientId == r.PatientId).Select(p => new { p.FullName, p.FileNumber }).FirstOrDefaultAsync(ct);

        var lines = new List<PrintLine>();
        var tenders = new List<PrintTender>();
        long depositUsed = 0;
        if (r.Type == ReceiptTypes.Refund)
        {
            var refund = await db.Refunds.FirstAsync(x => x.ReceiptId == r.ReceiptId, ct);
            var it = await (from i in db.InvoiceItems.AsNoTracking()
                            where i.InvoiceItemId == refund.InvoiceItemId
                            join d in db.Doctors on i.DoctorId equals d.DoctorId into dj
                            from d in dj.DefaultIfEmpty()
                            join t in db.QueueTickets on i.TicketId equals t.TicketId into tj
                            from t in tj.DefaultIfEmpty()
                            select new { i.ServiceName, Doctor = d == null ? null : d.FullName, Ticket = t == null ? null : t.TicketNumber }).FirstAsync(ct);
            lines.Add(new PrintLine($"استرجاع: {it.ServiceName}", refund.Amount, it.Doctor, it.Ticket));
            tenders.Add(new PrintTender(refund.Method, refund.Amount));
        }
        else
        {
            var payment = await db.Payments.AsNoTracking().Include(p => p.Tenders).Include(p => p.Allocations)
                .FirstAsync(p => p.ReceiptId == r.ReceiptId, ct);
            depositUsed = payment.DepositUsed;
            var itemIds = payment.Allocations.Select(a => a.InvoiceItemId).ToList();
            var items = await (from i in db.InvoiceItems.AsNoTracking()
                               where itemIds.Contains(i.InvoiceItemId)
                               join d in db.Doctors on i.DoctorId equals d.DoctorId into dj
                               from d in dj.DefaultIfEmpty()
                               join t in db.QueueTickets on i.TicketId equals t.TicketId into tj
                               from t in tj.DefaultIfEmpty()
                               select new { i.InvoiceItemId, i.ServiceName, Doctor = d == null ? null : d.FullName, Ticket = t == null ? null : t.TicketNumber })
                .ToDictionaryAsync(i => i.InvoiceItemId, ct);
            lines.AddRange(payment.Allocations.Select(a => new PrintLine(items[a.InvoiceItemId].ServiceName, a.Amount, items[a.InvoiceItemId].Doctor, items[a.InvoiceItemId].Ticket)));
            if (r.Type == ReceiptTypes.Deposit) lines.Add(new PrintLine("عربون (رصيد للمريض)", payment.TotalPaid));
            tenders.AddRange(payment.Tenders.Select(t => new PrintTender(t.Method, t.Amount)));
        }
        var copyNumber = copy is > 1 ? copy.Value : 1;
        // نقطة التحصيل (عيادة الطبيب) من الوردية، والطبيب ورقم الدور إن كانت كل البنود لطبيب واحد
        var clinic = r.ShiftId is null ? null
            : await (from sh in db.Shifts where sh.ShiftId == r.ShiftId join c in db.Clinics on sh.ClinicId equals c.ClinicId select c.Name).FirstOrDefaultAsync(ct);
        var doctors = lines.Select(l => l.DoctorName).Where(n => n != null).Distinct().ToList();
        var tickets = lines.Select(l => l.TicketNumber).Where(n => n != null).Distinct().ToList();
        // درج عام (وردية بلا عيادة) والبنود لطبيب واحد: عيادة ذلك الطبيب
        if (clinic is null && doctors.Count == 1)
            clinic = await (from d in db.Doctors where d.FullName == doctors[0]
                            join c in db.Clinics on d.ClinicId equals c.ClinicId
                            where c.BranchId == r.BranchId select c.Name).FirstOrDefaultAsync(ct);
        return new PrintPayloadDto(r.ReceiptNumber, r.Type, TypeNames[r.Type], $"{finance.Options.ComplexName} - {branch}", r.IssuedAt, cashier,
            patient?.FullName, patient?.FileNumber, lines, depositUsed, tenders, r.Total, finance.QrUrl(r.VerifyCode),
            copyNumber > 1 ? $"نسخة مكررة ({copyNumber}) - {finance.Options.Footer}" : finance.Options.Footer, copyNumber > 1, copyNumber,
            clinic, doctors.Count == 1 ? doctors[0] : null, tickets.Count == 1 ? tickets[0] : null,
            string.IsNullOrWhiteSpace(finance.Options.LogoUrl) ? null : finance.Options.LogoUrl);
    }

    /// <summary>إيصالات وردية (الافتراضي ورديتي المفتوحة)، الأحدث أولاً، لإعادة طباعة نسخة.</summary>
    public async Task<IReadOnlyList<ShiftReceiptItem>> ShiftReceiptsAsync(Guid? shiftId, CancellationToken ct)
    {
        var id = shiftId ?? await db.Shifts.Where(s => s.UserId == current.UserId && s.Status == ShiftStatuses.Open)
            .Select(s => (Guid?)s.ShiftId).FirstOrDefaultAsync(ct)
            ?? throw new AppException(404, "NO_OPEN_SHIFT", "لا يوجد حساب يومي مفتوح");
        var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.ShiftId == id, ct)
                    ?? throw new AppException(404, "SHIFT_NOT_FOUND", "الحساب اليومي غير موجود");
        var reviewer = current.Has(Permissions.ShiftsReview) || current.Has(Permissions.RefundsApprove);
        // عزل الفروع: المراجع يرى ورديات فرعه الحالي فقط (المدير العام كل الفروع)
        if (shift.UserId != current.UserId && current.Role != Roles.Admin && shift.BranchId != current.BranchId)
            throw new AppException(404, "SHIFT_NOT_FOUND", "الحساب اليومي غير موجود");
        if (shift.UserId != current.UserId && !reviewer) throw new AppException(403, "FORBIDDEN", "ليس حسابك اليومي");
        var rows = await (from r in db.Receipts.AsNoTracking()
                          where r.ShiftId == id
                          join p in db.Patients on r.PatientId equals p.PatientId into pj
                          from p in pj.DefaultIfEmpty()
                          orderby r.IssuedAt descending
                          select new { r, Name = p == null ? null : p.FullName, File = p == null ? null : p.FileNumber })
            .Take(200).ToListAsync(ct);
        var receiptIds = rows.Select(x => x.r.ReceiptId).ToList();
        var meta = await (from pay in db.Payments.AsNoTracking()
                          where receiptIds.Contains(pay.ReceiptId)
                          from a in pay.Allocations
                          join i in db.InvoiceItems on a.InvoiceItemId equals i.InvoiceItemId
                          join d in db.Doctors on i.DoctorId equals d.DoctorId into dj
                          from d in dj.DefaultIfEmpty()
                          join t in db.QueueTickets on i.TicketId equals t.TicketId into tj
                          from t in tj.DefaultIfEmpty()
                          select new { pay.ReceiptId, Doctor = d == null ? null : d.FullName, Ticket = t == null ? null : t.TicketNumber })
            .ToListAsync(ct);
        string? One(IEnumerable<string?> xs) { var l = xs.Where(x => x != null).Distinct().ToList(); return l.Count == 1 ? l[0] : null; }
        return rows.Select(x =>
        {
            var m = meta.Where(z => z.ReceiptId == x.r.ReceiptId).ToList();
            return new ShiftReceiptItem(x.r.ReceiptId, x.r.ReceiptNumber, x.r.Type, TypeNames.GetValueOrDefault(x.r.Type, x.r.Type), x.r.IssuedAt, x.r.Total, x.r.Status,
                x.Name, x.File, One(m.Select(z => z.Doctor)), One(m.Select(z => z.Ticket)), x.r.ReprintCount, reviewer || x.r.ReprintCount < 1,
                $"/receipts/{x.r.ReceiptId}/print-payload");
        }).ToList();
    }

    /// <summary>
    /// الحساب اليومي: الوكيل أو المتصفح طبع الوصل فعلاً. يُسجَّل أول مرة فقط، ويمسح سبب «غير مطبوع» إن وُجد.
    /// غير المطبوع يحتاج سبباً عند إغلاق الحساب، ويظهر لمحاسب الفرع.
    /// </summary>
    public async Task MarkPrintedAsync(Guid receiptId, CancellationToken ct)
    {
        var r = await LoadAsync(receiptId, ct);
        var owner = r.IssuedBy == current.UserId
                    || (r.ShiftId is { } sid && await db.Shifts.AnyAsync(s => s.ShiftId == sid && s.UserId == current.UserId, ct));
        if (!owner && !current.Has(Permissions.ShiftsReview)) throw AppException.Forbidden("يسجّل الطباعة صاحب الوصل فقط");
        if (r.PrintedAt is not null) return;
        r.PrintedAt = clock.UtcNow;
        r.UnprintedReason = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>نسخة مكررة: لا تفتح الدرج، وتُسجل في التدقيق. الاستقبال مرة واحدة، والمحاسب بلا حد.</summary>
    public async Task<ReprintResponse> ReprintAsync(Guid receiptId, CancellationToken ct)
    {
        var r = await LoadAsync(receiptId, ct);
        var unlimited = current.Has(Permissions.ShiftsReview) || current.Has(Permissions.RefundsApprove);
        if (!unlimited && r.ReprintCount >= 1)
            throw new AppException(403, "REPRINT_LIMIT", "أُعيدت طباعة الإيصال مسبقاً", "إعادة الطباعة مرة ثانية للمحاسب");
        r.ReprintCount++;
        audit.Add("RECEIPT_REPRINTED", "receipt", r.ReceiptId, after: new { r.ReceiptNumber, copy = r.ReprintCount + 1 });
        await db.SaveChangesAsync(ct);
        var copy = r.ReprintCount + 1;
        return new ReprintResponse($"/receipts/{r.ReceiptId}/print-payload?copy={copy}", copy);
    }

    public async Task<VerifyReceiptDto> VerifyAsync(string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var r = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(x => x.VerifyCode == normalized, ct)
                ?? throw new AppException(404, "RECEIPT_NOT_FOUND", "الإيصال غير موجود", "قد يكون الإيصال مزوراً");
        var branch = await db.Branches.Where(b => b.BranchId == r.BranchId).Select(b => b.Name).FirstAsync(ct);
        return new VerifyReceiptDto(true, r.ReceiptNumber, r.Type, r.IssuedAt, r.Total, branch, r.Status);
    }

    private async Task<Receipt> LoadAsync(Guid receiptId, CancellationToken ct)
    {
        var r = await db.Receipts.FirstOrDefaultAsync(x => x.ReceiptId == receiptId, ct)
                ?? throw new AppException(404, "RECEIPT_NOT_FOUND", "الإيصال غير موجود");
        if (current.Role != Roles.Admin && r.BranchId != current.BranchId) throw new AppException(404, "RECEIPT_NOT_FOUND", "الإيصال غير موجود");
        return r;
    }
}
