using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Bookings;
using AlRahma.Application.Branches;
using AlRahma.Application.Clinical;
using AlRahma.Application.Common;
using AlRahma.Application.PatientApp;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Assistant;

/// <summary>
/// أدوات المساعد: قائمة ثابتة، كل منها تقرأ بيانات الحساب الحالي فقط من التوكن. الوسائط من النموذج غير موثوقة:
/// patient_id و order_item_id يُفحصان هنا مقابل الحساب، فلا يصل نموذج مخترق أو حقن تعليمات إلى بيانات شخص آخر.
/// النتائج تمر بنفس شرط الدفع في الـ API. الردود بلا رقم هاتف ولا رقم ملف ولا ملاحظات سريرية.
/// </summary>
public class AssistantTools(IAppDbContext db, PatientPortalService portal, BookingService bookings, PharmacyService pharmacy,
    PatientFileService files, LabService lab, RadiologyService radiology, BranchPortalService branches)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly object PatientArg = new
    {
        type = "object",
        properties = new { patient_id = new { type = "string", description = "معرف أحد أفراد الحساب من list_family_members. اختياري." } },
    };

    public static readonly IReadOnlyList<AssistantToolDefinition> Definitions =
    [
        new("list_family_members", "أفراد هذا الحساب.", new { type = "object", properties = new { } }),
        new("get_my_appointments", "مواعيد أفراد الحساب.", new
        {
            type = "object",
            properties = new
            {
                patient_id = new { type = "string", description = "اختياري" },
                scope = new { type = "string", @enum = new[] { "upcoming", "past" } },
            },
        }),
        new("get_my_prescriptions", "وصفات أفراد الحساب.", PatientArg),
        new("get_my_lab_imaging_status", "حالة التحاليل والأشعة والدفع، بدون قيم النتائج.", PatientArg),
        new("get_order_result", "نتيجة طلب واحد. تعيد القيم فقط إذا سمح شرط الدفع.", new
        {
            type = "object",
            properties = new { order_item_id = new { type = "string" } },
            required = new[] { "order_item_id" },
        }),
        new("get_branch_info", "معلومات الفروع: العنوان والهاتف والدوام.", new
        {
            type = "object",
            properties = new { name = new { type = "string", description = "جزء من اسم الفرع أو المدينة. اختياري." } },
        }),
    ];

    /// <summary>ok = false: أداة غير معروفة أو طلب خارج الحساب. النتيجة مغلفة في untrusted_data، فهي بيانات لا تعليمات.</summary>
    public async Task<(bool Ok, string Json)> ExecuteAsync(string name, JsonElement? args, Guid? defaultPatient, CancellationToken ct)
    {
        object result;
        var ok = true;
        try
        {
            result = name switch
            {
                "list_family_members" => await FamilyAsync(ct),
                "get_my_appointments" => await AppointmentsAsync(await TargetsAsync(args, defaultPatient, ct), Str(args, "scope"), ct),
                "get_my_prescriptions" => await PrescriptionsAsync(await TargetsAsync(args, defaultPatient, ct), ct),
                "get_my_lab_imaging_status" => await OrdersAsync(await TargetsAsync(args, defaultPatient, ct), ct),
                "get_order_result" => await ResultAsync(Str(args, "order_item_id"), ct),
                "get_branch_info" => await BranchesAsync(Str(args, "name"), ct),
                _ => throw new NotFound(),
            };
        }
        catch (NotFound)
        {
            ok = false;
            result = new { error = "not_found" };
        }
        return (ok, JsonSerializer.Serialize(new { untrusted_data = result }, Json));
    }

    private sealed class NotFound : Exception;

    private static string? Str(JsonElement? args, string key) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>أفراد الحساب المطلوبون: المذكور (إن كان من الحساب)، أو الفرد المختار في التطبيق، أو كل الأفراد.</summary>
    private async Task<List<Guid>> TargetsAsync(JsonElement? args, Guid? defaultPatient, CancellationToken ct)
    {
        var linked = await portal.LinkedPatientIds().ToListAsync(ct);
        var requested = Str(args, "patient_id");
        if (!string.IsNullOrWhiteSpace(requested))
            return Guid.TryParse(requested, out var id) && linked.Contains(id) ? [id] : throw new NotFound();
        return defaultPatient is { } d ? [d] : linked;
    }

    private async Task<object> FamilyAsync(CancellationToken ct)
    {
        var members = await portal.MyPatientsAsync(portal.AccountId, ct);
        return new { items = members.Select(m => new { patient_id = m.PatientId, name = m.FullName, relation = m.Relation }).ToList() };
    }

    private async Task<object> AppointmentsAsync(List<Guid> patients, string? scope, CancellationToken ct)
    {
        var items = new List<object>();
        foreach (var p in patients)
            foreach (var b in await bookings.MyAsync(p, scope is "past" ? "past" : "upcoming", ct))
                items.Add(new
                {
                    patient_name = b.PatientName, doctor_name = b.DoctorName, date_ar = ArabicFormat.Date(b.Date),
                    time_ar = b.EstimatedTime is { } t ? ArabicFormat.Time(t) : null, ticket_number = b.TicketNumber, status = b.Status,
                    queue_position = b.QueuePosition, payment_status = b.PaymentStatus,
                });
        return new { items };
    }

    private async Task<object> PrescriptionsAsync(List<Guid> patients, CancellationToken ct)
    {
        var items = new List<object>();
        foreach (var p in patients)
            foreach (var r in await pharmacy.MyPrescriptionsAsync(p, ct))
                items.Add(new
                {
                    patient_name = r.PatientName, prescription_code = r.PrescriptionCode, doctor_name = r.DoctorName, branch_name = r.BranchName,
                    date_ar = ArabicFormat.Date(r.IssuedAt), status = r.Status,
                    lines = r.Items.Select(l => new { drug_name = l.DrugName, dose = l.Dose, frequency = l.Frequency, duration_days = l.DurationDays, instructions = l.Instructions }).ToList(),
                });
        return new { items };
    }

    private async Task<object> OrdersAsync(List<Guid> patients, CancellationToken ct)
    {
        var items = new List<object>();
        foreach (var p in patients)
            foreach (var o in await files.OrdersAsync(p, null, ct))
                items.Add(new
                {
                    order_item_id = o.OrderItemId, department = o.Department, service_name = o.ServiceName, status_ar = o.StatusAr, branch_name = o.BranchName,
                    result_ready = o.ResultReady, result_available = o.ResultAvailable, payment_required = o.PaymentRequired, amount_due = o.AmountDue,
                });
        return new { items };
    }

    /// <summary>القيم تأتي من نفس خدمة الـ API، فشرط الربط بالحساب وشرط الدفع هما نفسهما.</summary>
    private async Task<object> ResultAsync(string? orderItemId, CancellationToken ct)
    {
        if (!Guid.TryParse(orderItemId, out var id)) throw new NotFound();
        var linked = portal.LinkedPatientIds();
        var order = await db.MedicalOrders.AsNoTracking().Where(o => o.OrderItemId == id && linked.Contains(o.PatientId))
            .Join(db.InvoiceItems.AsNoTracking(), o => o.InvoiceItemId, i => i.InvoiceItemId, (o, i) => new { o.Department, i.ServiceName })
            .FirstOrDefaultAsync(ct) ?? throw new NotFound();
        try
        {
            if (order.Department == OrderDepartments.Lab)
            {
                var r = await lab.ResultAsync(id, ct);
                return new
                {
                    ready = true, locked = false, service_name = r.ServiceName, released_date_ar = r.ReleasedAt is { } at ? ArabicFormat.Date(at) : null,
                    values = r.Values.Select(v => new { name = v.Name, value = v.Value, unit = v.Unit, ref_low = v.RefLow, ref_high = v.RefHigh }).ToList(),
                    text_result = r.TextResult,
                };
            }
            var report = await radiology.ReportAsync(id, ct);
            return new { ready = true, locked = false, service_name = report.ServiceName, findings = report.Findings, impression = report.Impression };
        }
        catch (AppException e) when (e.Code == "INVOICE_UNPAID")
        {
            return new { ready = true, locked = true, service_name = order.ServiceName, amount_due = e.Extensions?["amount"] };
        }
        catch (AppException e) when (e.Code == "RESULT_NOT_READY")
        {
            return new { ready = false, locked = false, service_name = order.ServiceName };
        }
    }

    private async Task<object> BranchesAsync(string? name, CancellationToken ct)
    {
        var list = await branches.PublicListAsync(null, ct);
        var term = AssistantSafety.Normalize(name);
        var matched = term.Length == 0 ? list : list.Where(b => AssistantSafety.Normalize($"{b.Name} {b.City} {b.Address}").Contains(term)).ToList();
        return new
        {
            items = matched.Select(b => new
            {
                name = b.Name, city = b.City, address = b.Address, phone = b.Phone, working_hours = b.WorkingHours, status_ar = b.StatusAr, open_now = b.OpenNow,
            }).ToList(),
        };
    }
}
