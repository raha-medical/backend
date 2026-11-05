using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Clinical;

public record OrderLineInput(Guid? ServiceId, string? Note);
public record DirectOrderRequest(Guid? PatientId, List<OrderLineInput>? Items, string? ReferringDoctorName);
public record DirectOrderItemDto(Guid OrderItemId, string Department, string ServiceName, Guid InvoiceItemId, long Price, string Status);
public record DirectOrderResponse(IReadOnlyList<DirectOrderItemDto> Items);
public record CancelOrderRequest(string? Reason);
public record CancelOrderResponse(Guid OrderItemId, string Status);

/// <summary>
/// يربط حالة الطلب الطبي بحالة بنده في السلة: الدفع أو الاستثناء ينقله من ordered إلى paid/deferred (فيظهر للقسم)،
/// والاسترجاع الكامل أو الإلغاء قبل بدء العمل يجعله cancelled. يُستدعى بعد كل عملية مالية على البنود.
/// </summary>
public class MedicalOrderSync(IAppDbContext db, IRealtime realtime, ReferralTracker referrals)
{
    private record Changed(Guid OrderItemId, Guid BranchId, string Department, string Status, string ServiceName, string PatientName);

    /// <param name="inPerson">تحصيل أو استثناء من موظف في الفرع: المراجع حاضر فيُفعَّل تحويله. الدفع من التطبيق لا يثبت الحضور.</param>
    public async Task SyncAsync(Guid[] invoiceItemIds, CancellationToken ct, bool inPerson = false)
    {
        if (invoiceItemIds.Length == 0) return;
        var changed = await db.QueryAsync<Changed>($"""
            UPDATE medical_orders m
               SET status = CASE WHEN i.status IN ('void', 'refunded') THEN 'cancelled'
                                 WHEN i.status = 'deferred' THEN 'deferred' ELSE 'paid' END,
                   cancel_reason = CASE WHEN i.status IN ('void', 'refunded') THEN COALESCE(m.cancel_reason, 'ألغي البند في الفاتورة') ELSE m.cancel_reason END
              FROM invoice_items i, patients p
             WHERE i.invoice_item_id = m.invoice_item_id AND p.patient_id = m.patient_id
               AND m.invoice_item_id = ANY({invoiceItemIds})
               AND (   (m.status = 'ordered' AND i.status IN ('paid', 'partially_refunded', 'deferred'))
                    OR (m.status = 'deferred' AND i.status IN ('paid', 'partially_refunded'))
                    OR (m.status IN ('ordered', 'paid', 'deferred', 'scheduled') AND i.status IN ('void', 'refunded')))
            RETURNING m.order_item_id, m.branch_id, m.department, m.status, i.service_name, p.full_name AS patient_name
            """, ct);
        foreach (var c in changed.Where(c => c.Status is OrderStatuses.Paid or OrderStatuses.Deferred))
            await realtime.SendAsync(RealtimeGroups.BranchDepartment(c.BranchId, c.Department), "order.created", new
            {
                order_item_id = c.OrderItemId, department = c.Department, service_name = c.ServiceName, patient_name = c.PatientName, status = c.Status,
            });
        await referrals.CancelByOrdersAsync(changed.Where(c => c.Status == OrderStatuses.Cancelled).Select(c => c.OrderItemId).ToArray(), ct);
        if (inPerson) await referrals.ActivateByInvoiceItemsAsync(invoiceItemIds, ct);
    }
}

/// <summary>إنشاء الطلبات الطبية مع بنودها في سلة المريض (فاتورة جديدة بسعر الخدمة من السيرفر).</summary>
public class OrderBilling(IAppDbContext db, ICurrentUser current, IClock clock, NumberSequences numbers, ReferralTracker referrals)
{
    public record Line(Service Service, string? Note);

    /// <summary>يتحقق من الخدمات (فرع الطلب، فعالة، ومن نوع القسم) ويعيد أخطاء الحقول بأسماء الطلب.</summary>
    public async Task<List<Line>> ResolveAsync(Guid branchId, IReadOnlyList<(string Field, OrderLineInput Input, string? Department)> inputs, CancellationToken ct)
    {
        var ids = inputs.Where(i => i.Input.ServiceId is not null).Select(i => i.Input.ServiceId!.Value).Distinct().ToList();
        var services = await db.Services.AsNoTracking().Where(s => ids.Contains(s.ServiceId) && s.BranchId == branchId && s.IsActive)
            .ToDictionaryAsync(s => s.ServiceId, ct);
        var v = new Validator();
        var lines = new List<Line>();
        foreach (var (field, input, department) in inputs)
        {
            var s = input.ServiceId is { } sid ? services.GetValueOrDefault(sid) : null;
            var ok = s is not null && (department is null ? s.Type is ServiceTypes.Lab or ServiceTypes.Radiology : s.Type == department);
            v.Check(ok, $"{field}.service_id", department switch
            {
                ServiceTypes.Lab => "خدمة تحليل فعالة في هذا الفرع مطلوبة",
                ServiceTypes.Radiology => "خدمة أشعة فعالة في هذا الفرع مطلوبة",
                _ => "خدمة تحليل أو أشعة فعالة في هذا الفرع مطلوبة",
            });
            v.Check(input.Note is null || input.Note.Length <= 500, $"{field}.note", "500 حرف كحد أقصى");
            if (ok) lines.Add(new Line(s!, string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim()));
        }
        v.ThrowIfInvalid();
        return lines;
    }

    /// <summary>فاتورة واحدة لكل دفعة طلبات، وطلب طبي لكل بند. لا يحفظ (SaveChanges على المستدعي).</summary>
    public async Task<List<MedicalOrder>> CreateAsync(Guid branchId, Guid patientId, Guid? doctorId, Guid? consultationId, string? referringDoctorName,
        IReadOnlyList<Line> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return [];
        var now = clock.UtcNow;
        var referralExpiry = await referrals.ExpiryAsync(branchId, ct);
        var rate = 0m;
        if (doctorId is { } did && lines.Any(l => l.Service.IsCommissionable))
            rate = await db.DoctorCommissionRates.Where(r => r.DoctorId == did && r.EffectiveFrom <= clock.BaghdadToday())
                .OrderByDescending(r => r.EffectiveFrom).Select(r => r.NewVisitRate).FirstOrDefaultAsync(ct);
        var invoice = new Invoice
        {
            InvoiceId = UuidV7.New(),
            InvoiceNumber = $"{await numbers.BranchCodeAsync(branchId, ct)}-INV-{await numbers.NextAsync(branchId, "invoice", "all", ct):D6}",
            BranchId = branchId,
            PatientId = patientId,
            CreatedBy = current.StaffUserId,
            CreatedAt = now,
        };
        var orders = new List<MedicalOrder>();
        foreach (var l in lines)
        {
            var r = l.Service.IsCommissionable && doctorId is not null ? rate : 0m;
            var share = (long)Math.Round(l.Service.Price * r / 100m, MidpointRounding.AwayFromZero);
            var free = l.Service.Price == 0;
            var item = new InvoiceItem
            {
                InvoiceItemId = UuidV7.New(), InvoiceId = invoice.InvoiceId, ServiceId = l.Service.ServiceId, DoctorId = doctorId,
                ItemType = l.Service.Type, ServiceName = l.Service.Name, Quantity = 1, UnitPrice = l.Service.Price, NetPrice = l.Service.Price,
                CommissionRate = r, DoctorShare = share, ComplexShare = l.Service.Price - share,
                Status = free ? InvoiceItemStatuses.Paid : InvoiceItemStatuses.Unpaid, CreatedAt = now,
            };
            invoice.Items.Add(item);
            var order = new MedicalOrder
            {
                OrderItemId = UuidV7.New(), BranchId = branchId, ConsultationId = consultationId, PatientId = patientId,
                InvoiceItemId = item.InvoiceItemId, ServiceId = l.Service.ServiceId, Department = l.Service.Type,
                OrderedByDoctorId = doctorId, ReferringDoctorName = referringDoctorName, Note = l.Note,
                Status = free ? OrderStatuses.Paid : OrderStatuses.Ordered, CreatedAt = now,
            };
            orders.Add(order);
            referrals.TrackOrder(order, l.Service.Modality, referralExpiry);
        }
        invoice.GrossAmount = invoice.Items.Sum(i => i.NetPrice);
        invoice.NetAmount = invoice.GrossAmount;
        Billing.FinanceCore.RefreshInvoice(invoice);
        db.Invoices.Add(invoice);
        db.MedicalOrders.AddRange(orders);
        return orders;
    }
}
