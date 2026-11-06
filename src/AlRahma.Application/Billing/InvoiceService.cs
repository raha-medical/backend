using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Billing;

public record InvoiceItemInput(Guid? ServiceId, Guid? DoctorId, short? Quantity);
public record DiscountInput(string? Type, long? Value, string? Reason);
public record CreateInvoiceRequest(Guid? InvoiceId, Guid? PatientId, List<InvoiceItemInput>? Items, DiscountInput? Discount);
public record VoidInvoiceRequest(string? Reason);

public record InvoiceItemDto(Guid InvoiceItemId, Guid ServiceId, string ServiceName, string ItemType, string Department, Guid? DoctorId,
    string? DoctorName, Guid? TicketId, short Quantity, long UnitPrice, long Price, long Discount, long NetPrice, decimal CommissionRate,
    long DoctorShare, long ComplexShare, long RefundedAmount, string Status, DateTimeOffset? PerformedAt);
public record InvoicePaymentDto(Guid PaymentId, Guid ReceiptId, string ReceiptNumber, long Amount, DateTimeOffset CreatedAt, string? CashierName);
public record InvoiceRefundDto(Guid RefundId, Guid InvoiceItemId, long Amount, string Method, string Status, string Reason, DateTimeOffset CreatedAt);
public record InvoiceDto(Guid InvoiceId, string InvoiceNumber, Guid BranchId, Guid PatientId, string PatientName, string FileNumber, string Status,
    long GrossAmount, long DiscountAmount, long NetAmount, long PaidAmount, long RefundedAmount, string? DiscountReason, string? VoidReason,
    DateTimeOffset CreatedAt, IReadOnlyList<InvoiceItemDto> Items, IReadOnlyList<InvoicePaymentDto> Payments, IReadOnlyList<InvoiceRefundDto> Refunds);
public record InvoiceListItem(Guid InvoiceId, string InvoiceNumber, Guid PatientId, string PatientName, string FileNumber, string Status,
    long NetAmount, long PaidAmount, int ItemsCount, DateTimeOffset CreatedAt);
public record InvoiceListQuery(Guid? PatientId, string? Status, DateOnly? From, DateOnly? To, string? Q, int Page = 1, int PageSize = 20);

/// <summary>
/// الفواتير: الكشفية تُنشأ تلقائياً عند تثبيت الحضور، وطلبات الطبيب مع الاستشارة، والفاتورة اليدوية هنا
/// (مراجع مباشر للمختبر أو الأشعة أو إجراء). السعر ونسبة الطبيب لقطة من السيرفر، ولا تُرسل من الواجهة.
/// </summary>
public class InvoiceService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, NumberSequences numbers,
    Clinical.MedicalOrderSync orders, CurrencyService currency)
{
    public async Task<InvoiceDto> CreateAsync(CreateInvoiceRequest req, CancellationToken ct)
    {
        var lines = req.Items ?? [];
        var d = req.Discount;
        new Validator()
            .Check(req.PatientId is not null, "patient_id", "المريض مطلوب")
            .Check(lines.Count is > 0 and <= 50, "items", "من 1 إلى 50 بنداً")
            .Check(lines.All(l => l.ServiceId is not null), "items", "service_id مطلوب لكل بند")
            .Check(lines.All(l => l.Quantity is null or > 0 and <= 100), "items", "الكمية من 1 إلى 100")
            .Check(d is null || d.Type is "amount" or "percent", "discount.type", "amount | percent")
            .Check(d is null || d.Value is > 0, "discount.value", "قيمة الخصم أكبر من صفر")
            .Check(d is null || d.Type != "percent" || d.Value <= 100, "discount.value", "النسبة 100 كحد أقصى")
            .Check(d is null || !string.IsNullOrWhiteSpace(d.Reason), "discount.reason", "سبب الخصم مطلوب")
            .ThrowIfInvalid();
        if (d is not null && !current.Has(Permissions.DiscountsGrant))
            throw AppException.Forbidden("الخصم يتطلب صلاحية discounts.grant");

        var id = req.InvoiceId ?? UuidV7.New();
        if (await db.Invoices.AnyAsync(i => i.InvoiceId == id, ct))
            throw new AppException(409, "INVOICE_EXISTS", "يوجد فاتورة بنفس المعرف");
        var patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.PatientId == req.PatientId, ct)
                      ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
        if (patient.MergedIntoPatientId is not null)
            throw new AppException(409, "PATIENT_MERGED", "هذا الملف مدموج في ملف آخر");

        var serviceIds = lines.Select(l => l.ServiceId!.Value).Distinct().ToList();
        var services = await db.Services.AsNoTracking()
            .Where(s => serviceIds.Contains(s.ServiceId) && s.BranchId == current.BranchId && s.IsActive).ToDictionaryAsync(s => s.ServiceId, ct);
        var doctorIds = lines.Where(l => l.DoctorId != null).Select(l => l.DoctorId!.Value).Distinct().ToList();
        var doctors = await db.Doctors.AsNoTracking().Include(x => x.Clinic)
            .Where(x => doctorIds.Contains(x.DoctorId) && x.IsActive && x.Clinic!.BranchId == current.BranchId).ToDictionaryAsync(x => x.DoctorId, ct);
        var v = new Validator();
        for (var i = 0; i < lines.Count; i++)
        {
            var s = services.GetValueOrDefault(lines[i].ServiceId!.Value);
            v.Check(s is not null, $"items[{i}].service_id", "الخدمة غير موجودة أو معطلة في هذا الفرع");
            v.Check(s is null || s.Type != "consultation", $"items[{i}].service_id", "الكشفية تُنشأ تلقائياً عند تثبيت الحضور في الطابور");
            v.Check(lines[i].DoctorId is null || doctors.ContainsKey(lines[i].DoctorId!.Value), $"items[{i}].doctor_id", "الطبيب غير موجود في هذا الفرع");
        }
        v.ThrowIfInvalid();

        var today = clock.BaghdadToday();
        var rates = await db.DoctorCommissionRates.AsNoTracking()
            .Where(r => doctorIds.Contains(r.DoctorId) && r.EffectiveFrom <= today).ToListAsync(ct);
        var now = clock.UtcNow;
        var invoice = new Invoice
        {
            InvoiceId = id,
            InvoiceNumber = $"{await numbers.BranchCodeAsync(current.BranchId, ct)}-INV-{await numbers.NextAsync(current.BranchId, "invoice", "all", ct):D6}",
            BranchId = current.BranchId,
            PatientId = patient.PatientId,
            CreatedBy = current.UserId,
            CreatedAt = now,
        };
        foreach (var l in lines)
        {
            var s = services[l.ServiceId!.Value];
            var qty = l.Quantity ?? 1;
            // الإجراء الذي ينفذه طبيب تُحسب له نسبة "جديد" الحالية؛ التحاليل والأشعة للمجمع ما لم تُعلَّم بنسبة
            var rate = s.IsCommissionable && l.DoctorId is { } did
                ? rates.Where(r => r.DoctorId == did).OrderByDescending(r => r.EffectiveFrom).Select(r => r.NewVisitRate).FirstOrDefault()
                : 0m;
            var price = await currency.PriceAsync(s, current.BranchId, ct); // 7.2: خدمة USD بسعر اليوم
            invoice.Items.Add(new InvoiceItem
            {
                InvoiceItemId = UuidV7.New(), InvoiceId = id, ServiceId = s.ServiceId, DoctorId = l.DoctorId, ItemType = s.Type,
                ServiceName = s.Name, Quantity = qty, UnitPrice = price.UnitPriceIqd, CommissionRate = rate, CreatedAt = now,
                Currency = price.Currency, OriginalUnitPrice = price.OriginalUnitPrice, ExchangeRate = price.Rate,
            });
        }
        var gross = invoice.Items.Sum(i => i.UnitPrice * i.Quantity);
        var discount = d is null ? 0 : d.Type == "amount" ? d.Value!.Value : (long)Math.Round(gross * d.Value!.Value / 100m, MidpointRounding.AwayFromZero);
        if (discount > gross)
            throw AppException.Validation(new Dictionary<string, string[]> { ["discount.value"] = ["الخصم أكبر من مبلغ الفاتورة"] });
        ApplyDiscount(invoice.Items, discount);
        invoice.GrossAmount = gross;
        invoice.DiscountAmount = discount;
        invoice.NetAmount = gross - discount;
        if (discount > 0) { invoice.DiscountReason = d!.Reason!.Trim(); invoice.DiscountBy = current.UserId; }
        if (invoice.NetAmount == 0)
        {
            foreach (var i in invoice.Items) i.Status = InvoiceItemStatuses.Paid; // مجانية بالكامل (خصم 100%)
            invoice.Status = InvoiceStatuses.Paid;
        }
        db.Invoices.Add(invoice);
        audit.Add("INVOICE_CREATED", "invoice", id, after: new { invoice.InvoiceNumber, invoice.NetAmount, invoice.DiscountAmount },
            reason: invoice.DiscountReason);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>يوزع الخصم على البنود بنسبة أسعارها (الباقي على آخر بند)، ويعيد حساب حصة الطبيب والمجمع.</summary>
    private static void ApplyDiscount(List<InvoiceItem> items, long discount)
    {
        var gross = items.Sum(i => i.UnitPrice * i.Quantity);
        long given = 0;
        for (var k = 0; k < items.Count; k++)
        {
            var it = items[k];
            var line = it.UnitPrice * it.Quantity;
            it.Discount = gross == 0 ? 0 : k == items.Count - 1 ? discount - given : discount * line / gross;
            given += it.Discount;
            it.NetPrice = line - it.Discount;
            it.DoctorShare = (long)Math.Round(it.NetPrice * it.CommissionRate / 100m, MidpointRounding.AwayFromZero);
            it.ComplexShare = it.NetPrice - it.DoctorShare;
        }
    }

    public async Task<InvoiceDto> GetAsync(Guid invoiceId, CancellationToken ct)
    {
        var inv = await db.Invoices.AsNoTracking().Include(i => i.Items).FirstOrDefaultAsync(i => i.InvoiceId == invoiceId, ct)
                  ?? throw new AppException(404, "INVOICE_NOT_FOUND", "الفاتورة غير موجودة");
        if (current.Role != Roles.Admin && inv.BranchId != current.BranchId) throw new AppException(404, "INVOICE_NOT_FOUND", "الفاتورة غير موجودة");
        var patient = await db.Patients.Where(p => p.PatientId == inv.PatientId).Select(p => new { p.FullName, p.FileNumber }).FirstAsync(ct);
        var itemIds = inv.Items.Select(i => i.InvoiceItemId).ToList();
        var doctorIds = inv.Items.Where(i => i.DoctorId != null).Select(i => i.DoctorId!.Value).Distinct().ToList();
        var doctorNames = await db.Doctors.Where(x => doctorIds.Contains(x.DoctorId)).ToDictionaryAsync(x => x.DoctorId, x => x.FullName, ct);
        var payments = await (
            from a in db.PaymentAllocations
            join p in db.Payments on a.PaymentId equals p.PaymentId
            join r in db.Receipts on p.ReceiptId equals r.ReceiptId
            join u in db.Users on p.CreatedBy equals u.UserId into uj
            from u in uj.DefaultIfEmpty()
            where itemIds.Contains(a.InvoiceItemId)
            group a by new { p.PaymentId, p.ReceiptId, r.ReceiptNumber, p.CreatedAt, Cashier = u == null ? null : u.FullName } into g
            select new InvoicePaymentDto(g.Key.PaymentId, g.Key.ReceiptId, g.Key.ReceiptNumber, g.Sum(x => x.Amount), g.Key.CreatedAt, g.Key.Cashier))
            .ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking().Where(r => itemIds.Contains(r.InvoiceItemId)).OrderBy(r => r.CreatedAt)
            .Select(r => new InvoiceRefundDto(r.RefundId, r.InvoiceItemId, r.Amount, r.Method, r.Status, r.Reason, r.CreatedAt)).ToListAsync(ct);
        var items = inv.Items.OrderBy(i => i.CreatedAt).Select(i => new InvoiceItemDto(i.InvoiceItemId, i.ServiceId, i.ServiceName, i.ItemType,
            FinanceCore.DepartmentOf(i.ItemType), i.DoctorId, i.DoctorId is { } did ? doctorNames.GetValueOrDefault(did) : null, i.TicketId,
            i.Quantity, i.UnitPrice, i.UnitPrice * i.Quantity, i.Discount, i.NetPrice, i.CommissionRate, i.DoctorShare, i.ComplexShare,
            i.RefundedAmount, i.Status, i.PerformedAt)).ToList();
        return new InvoiceDto(inv.InvoiceId, inv.InvoiceNumber, inv.BranchId, inv.PatientId, patient.FullName, patient.FileNumber, inv.Status,
            inv.GrossAmount, inv.DiscountAmount, inv.NetAmount, inv.PaidAmount, inv.Items.Sum(i => i.RefundedAmount), inv.DiscountReason,
            inv.VoidReason, inv.CreatedAt, items, payments.OrderBy(p => p.CreatedAt).ToList(), refunds);
    }

    public async Task<PagedResult<InvoiceListItem>> ListAsync(InvoiceListQuery q, CancellationToken ct)
    {
        var page = new PageQuery(q.Page, q.PageSize);
        var query = db.Invoices.AsNoTracking().Where(i => i.BranchId == current.BranchId);
        if (q.PatientId is { } pid) query = query.Where(i => i.PatientId == pid);
        if (!string.IsNullOrWhiteSpace(q.Status)) query = query.Where(i => i.Status == q.Status);
        if (q.From is { } from) { var f = ClockExtensions.ToUtc(from, TimeOnly.MinValue); query = query.Where(i => i.CreatedAt >= f); }
        if (q.To is { } to) { var t = ClockExtensions.ToUtc(to.AddDays(1), TimeOnly.MinValue); query = query.Where(i => i.CreatedAt < t); }
        var joined = query.Join(db.Patients, i => i.PatientId, p => p.PatientId, (i, p) => new { i, p.FullName, p.FileNumber });
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            joined = joined.Where(x => x.i.InvoiceNumber == term.ToUpper() || x.FileNumber == term.ToUpper() || EF.Functions.ILike(x.FullName, $"%{term}%"));
        }
        var total = await joined.CountAsync(ct);
        var rows = await joined.OrderByDescending(x => x.i.CreatedAt).Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize)
            .Select(x => new InvoiceListItem(x.i.InvoiceId, x.i.InvoiceNumber, x.i.PatientId, x.FullName, x.FileNumber, x.i.Status,
                x.i.NetAmount, x.i.PaidAmount, x.i.Items.Count, x.i.CreatedAt)).ToListAsync(ct);
        return new PagedResult<InvoiceListItem>(rows, page.SafePage, page.SafePageSize, total);
    }

    /// <summary>إلغاء فاتورة غير مدفوعة بالكامل (طلب خاطئ). المدفوعة تُعالج بالاسترجاع.</summary>
    public async Task<InvoiceDto> VoidAsync(Guid invoiceId, VoidInvoiceRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Reason), "reason", "سبب الإلغاء مطلوب").ThrowIfInvalid();
        var inv = await db.Invoices.Include(i => i.Items).FirstOrDefaultAsync(i => i.InvoiceId == invoiceId && i.BranchId == current.BranchId, ct)
                  ?? throw new AppException(404, "INVOICE_NOT_FOUND", "الفاتورة غير موجودة");
        if (inv.Status == InvoiceStatuses.Void) return await GetAsync(invoiceId, ct);
        var itemIds = inv.Items.Select(i => i.InvoiceItemId).ToList();
        if (inv.Items.Any(i => i.Status is not (InvoiceItemStatuses.Unpaid or InvoiceItemStatuses.Void))
            || await db.PaymentAllocations.AnyAsync(a => itemIds.Contains(a.InvoiceItemId), ct))
            throw new AppException(409, "INVOICE_HAS_PAYMENTS", "لا يمكن إلغاء فاتورة عليها دفعات", "استخدم الاسترجاع للبنود المدفوعة");
        foreach (var i in inv.Items) i.Status = InvoiceItemStatuses.Void;
        inv.Status = InvoiceStatuses.Void;
        inv.VoidReason = req.Reason!.Trim();
        inv.VoidedBy = current.UserId;
        inv.VoidedAt = clock.UtcNow;
        audit.Add("INVOICE_VOIDED", "invoice", inv.InvoiceId, after: new { inv.InvoiceNumber, inv.NetAmount }, reason: inv.VoidReason);
        await db.SaveChangesAsync(ct);
        await orders.SyncAsync(itemIds.ToArray(), ct); // الطلبات الطبية للبنود الملغاة تُلغى
        return await GetAsync(invoiceId, ct);
    }
}
