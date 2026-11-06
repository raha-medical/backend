using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Bookings;
using AlRahma.Application.Branches;
using AlRahma.Application.Clinical;
using AlRahma.Application.Common;
using AlRahma.Application.PatientApp;
using AlRahma.Application.PatientApp.Gateways;
using AlRahma.Application.Support;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Assistant;

/// <summary>
/// أدوات المساعد: قائمة ثابتة. أدوات الحساب تقرأ بيانات الحساب الحالي فقط من التوكن، والوسائط من النموذج غير موثوقة:
/// patient_id و session_id يُفحصان هنا مقابل الحساب والحالة، فلا يصل نموذج مخترق أو حقن تعليمات إلى بيانات شخص آخر.
/// تقليل البيانات: الاسم الأول فقط، بلا رقم هاتف ولا رقم ملف ولا ملاحظات سريرية، ولا قيم نتائج ولا نص تقرير أشعة
/// ولا جرعات أو تكرار أو تعليمات استعمال. النتائج والوصفات تُفتح بزر في التطبيق (Actions).
/// propose_booking لا يحجز: يعيد اقتراحاً (Proposal) يؤكده المراجع بنفسه عبر POST /api/v1/bookings.
/// الخدمة Scoped: الاقتراح والأزرار لهذا الطلب فقط.
/// </summary>
public class AssistantTools(IAppDbContext db, IClock clock, PatientPortalService portal, BookingService bookings, PharmacyService pharmacy,
    PatientFileService files, BranchPortalService branches, SupportService support, PaymentGatewayRegistry gateways)
{
    public const int MaxDoctors = 10;
    public const int MaxAvailabilityDays = 14;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly string[] WeekdaysAr = ["الأحد", "الاثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت"];

    public AssistantProposalDto? Proposal { get; private set; }
    private readonly List<AssistantActionDto> actions = [];
    public IReadOnlyList<AssistantActionDto> Actions => actions;

    private static readonly object PatientArg = new
    {
        type = "object",
        properties = new { patient_id = new { type = "string", description = "معرف أحد أفراد الحساب من list_family_members. اختياري." } },
    };

    public static readonly IReadOnlyList<AssistantToolDefinition> Definitions =
    [
        new("list_family_members", "أفراد هذا الحساب (الاسم الأول وصلة القرابة).", new { type = "object", properties = new { } }),
        new("get_my_appointments", "مواعيد أفراد الحساب ودورهم في الطابور.", new
        {
            type = "object",
            properties = new
            {
                patient_id = new { type = "string", description = "اختياري" },
                scope = new { type = "string", @enum = new[] { "upcoming", "past" } },
            },
        }),
        new("get_my_prescriptions", "وصفات أفراد الحساب: أسماء الأدوية وحالة الصرف فقط، بلا جرعات.", PatientArg),
        new("get_my_lab_imaging_status", "حالة التحاليل والأشعة: جاهزة أم لا، وهل تنفتح بعد الدفع. بلا قيم النتائج.", PatientArg),
        new("get_my_invoices", "فواتير أفراد الحساب: المدفوع والمتبقي.", PatientArg),
        new("get_my_credit", "رصيد محفظة فرد في الحساب (يُستخدم في الزيارة القادمة).", PatientArg),
        new("get_branch_info", "معلومات الفروع: العنوان والهاتف والواتساب والدوام والموقع على الخريطة وهل الفرع مفتوح الآن.", new
        {
            type = "object",
            properties = new { name = new { type = "string", description = "جزء من اسم الفرع أو المدينة. اختياري." } },
        }),
        new("list_specialties", "التخصصات المتوفرة فعلاً في الفروع مع عدد الأطباء.", new
        {
            type = "object",
            properties = new { branch = new { type = "string", description = "جزء من اسم الفرع. اختياري." } },
        }),
        new("find_doctors", "البحث عن أطباء حسب التخصص أو الاسم أو الفرع، مع الكشفية (جديد ومراجعة).", new
        {
            type = "object",
            properties = new
            {
                specialty = new { type = "string", description = "اسم التخصص كما في list_specialties أو رمزه. اختياري." },
                name = new { type = "string", description = "جزء من اسم الطبيب. اختياري." },
                branch = new { type = "string", description = "جزء من اسم الفرع. اختياري." },
            },
        }),
        new("get_doctor_availability", "أيام دوام طبيب والمقاعد المتبقية للحجز والوقت المتوقع، وأقرب موعد متاح.", new
        {
            type = "object",
            properties = new
            {
                doctor_id = new { type = "string", description = "من find_doctors" },
                from = new { type = "string", description = "yyyy-MM-dd. اختياري، الافتراضي اليوم." },
                days = new { type = "integer", description = $"عدد الأيام (1 إلى {MaxAvailabilityDays})، الافتراضي 7." },
            },
            required = new[] { "doctor_id" },
        }),
        new("propose_booking", "يجهز اقتراح حجز لوجبة متاحة ليؤكده المراجع بزر في التطبيق. لا يحجز. نوع الزيارة (جديد أو مراجعة) يحدده النظام تلقائياً.", new
        {
            type = "object",
            properties = new
            {
                session_id = new { type = "string", description = "من get_doctor_availability" },
                patient_id = new { type = "string", description = "فرد الحساب المطلوب الحجز له. اختياري إذا كان واحداً أو مختاراً." },
            },
            required = new[] { "session_id" },
        }),
        new("search_faq", "الأسئلة الشائعة للمجمع: الحجز والإلغاء والاسترجاع والمحفظة والدفع والنتائج.", new
        {
            type = "object",
            properties = new { query = new { type = "string", description = "كلمات السؤال. اختياري." } },
        }),
        new("get_payment_methods", "طرق الدفع المتاحة: في المجمع، والدفع الإلكتروني المفعّل.", new { type = "object", properties = new { } }),
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
                "get_my_invoices" => await InvoicesAsync(await TargetsAsync(args, defaultPatient, ct), ct),
                "get_my_credit" => await CreditAsync(await TargetsAsync(args, defaultPatient, ct), ct),
                "get_branch_info" => await BranchesAsync(Str(args, "name"), ct),
                "list_specialties" => await SpecialtiesAsync(Str(args, "branch"), ct),
                "find_doctors" => await DoctorsAsync(Str(args, "specialty"), Str(args, "name"), Str(args, "branch"), ct),
                "get_doctor_availability" => await AvailabilityAsync(Str(args, "doctor_id"), Str(args, "from"), Int(args, "days"), ct),
                "propose_booking" => await ProposeAsync(Str(args, "session_id"), Str(args, "patient_id"), defaultPatient, ct),
                "search_faq" => await FaqAsync(Str(args, "query"), ct),
                "get_payment_methods" => PaymentMethods(),
                _ => throw new NotFound(),
            };
        }
        catch (NotFound)
        {
            ok = false;
            result = new { error = "not_found" };
        }
        catch (AppException e) when (e.Status is 400 or 404 or 409)
        {
            // خطأ عمل معروف (طبيب غير موجود، فترة غير صالحة): الرمز فقط، بلا تفاصيل داخلية
            ok = false;
            result = new { error = e.Code.ToLowerInvariant() };
        }
        return (ok, JsonSerializer.Serialize(new { untrusted_data = result }, Json));
    }

    private sealed class NotFound : Exception;

    private static string? Str(JsonElement? args, string key) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement? args, string key) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private void AddAction(string type, string label, Guid? patient)
    {
        if (!actions.Any(a => a.Type == type && a.PatientId == patient)) actions.Add(new AssistantActionDto(type, label, patient));
    }

    /// <summary>أفراد الحساب المطلوبون: المذكور (إن كان من الحساب)، أو الفرد المختار في التطبيق، أو كل الأفراد.</summary>
    private async Task<List<Guid>> TargetsAsync(JsonElement? args, Guid? defaultPatient, CancellationToken ct)
    {
        var linked = await portal.LinkedPatientIds().ToListAsync(ct);
        var requested = Str(args, "patient_id");
        if (!string.IsNullOrWhiteSpace(requested))
            return Guid.TryParse(requested, out var id) && linked.Contains(id) ? [id] : throw new NotFound();
        return defaultPatient is { } d ? [d] : linked;
    }

    private async Task<Dictionary<Guid, string>> FirstNamesAsync(CancellationToken ct) =>
        (await portal.MyPatientsAsync(portal.AccountId, ct)).ToDictionary(m => m.PatientId, m => AssistantSafety.FirstName(m.FullName));

    private async Task<object> FamilyAsync(CancellationToken ct)
    {
        var members = await portal.MyPatientsAsync(portal.AccountId, ct);
        return new { items = members.Select(m => new { patient_id = m.PatientId, first_name = AssistantSafety.FirstName(m.FullName), relation = m.Relation }).ToList() };
    }

    private async Task<object> AppointmentsAsync(List<Guid> patients, string? scope, CancellationToken ct)
    {
        var items = new List<object>();
        foreach (var p in patients)
            foreach (var b in await bookings.MyAsync(p, scope is "past" ? "past" : "upcoming", ct))
                items.Add(new
                {
                    patient_first_name = AssistantSafety.FirstName(b.PatientName), doctor_name = b.DoctorName, branch_name = b.BranchName,
                    date_ar = ArabicFormat.Date(b.Date), time_ar = b.EstimatedTime is { } t ? ArabicFormat.Time(t) : null, ticket_number = b.TicketNumber,
                    status = b.Status, queue_position = b.QueuePosition, payment_status = b.PaymentStatus,
                });
        return new { items };
    }

    /// <summary>أسماء الأدوية وحالة الوصفة فقط: الجرعة والتكرار والمدة والتعليمات تبقى في التطبيق (زر open_prescriptions).</summary>
    private async Task<object> PrescriptionsAsync(List<Guid> patients, CancellationToken ct)
    {
        var items = new List<object>();
        foreach (var p in patients)
        {
            var list = await pharmacy.MyPrescriptionsAsync(p, ct);
            if (list.Count > 0) AddAction("open_prescriptions", "افتح وصفاتي", patients.Count == 1 ? p : null);
            foreach (var r in list)
                items.Add(new
                {
                    patient_first_name = AssistantSafety.FirstName(r.PatientName), prescription_code = r.PrescriptionCode, doctor_name = r.DoctorName,
                    branch_name = r.BranchName, date_ar = ArabicFormat.Date(r.IssuedAt), status = r.Status,
                    drug_names = r.Items.Select(l => AssistantSafety.DrugNameOnly(l.DrugName)).Distinct().ToList(),
                });
        }
        return new { items, details_in_app = true };
    }

    /// <summary>جاهزة أو لا، ومبلغ الفتح إن وُجد. القيم ونص التقرير لا تُقرأ هنا أصلاً، والزر يفتح الملف في التطبيق.</summary>
    private async Task<object> OrdersAsync(List<Guid> patients, CancellationToken ct)
    {
        var names = await FirstNamesAsync(ct);
        var items = new List<object>();
        foreach (var p in patients)
        {
            var list = await files.OrdersAsync(p, null, ct);
            if (list.Any(o => o.ResultReady)) AddAction("open_results", "افتح نتائجي", p);
            foreach (var o in list)
                items.Add(new
                {
                    patient_first_name = names.GetValueOrDefault(p, ""), department = o.Department, service_name = o.ServiceName, status_ar = o.StatusAr,
                    branch_name = o.BranchName, date_ar = ArabicFormat.Date(o.OrderedAt), result_ready = o.ResultReady,
                    can_open_now = o.ResultAvailable, opens_after_paying = o.ResultReady && !o.ResultAvailable && o.PaymentRequired ? o.AmountDue : (long?)null,
                });
        }
        return new { items };
    }

    private async Task<object> InvoicesAsync(List<Guid> patients, CancellationToken ct)
    {
        var names = await FirstNamesAsync(ct);
        var items = new List<object>();
        foreach (var p in patients)
        {
            var list = (await files.InvoicesAsync(p, ct)).OrderByDescending(i => i.CreatedAt).Take(10).ToList();
            if (list.Any(i => i.DueAmount > 0)) AddAction("open_invoices", "افتح فواتيري", p);
            foreach (var i in list)
                items.Add(new
                {
                    patient_first_name = names.GetValueOrDefault(p, ""), invoice_number = i.InvoiceNumber, branch_name = i.BranchName, status_ar = i.StatusAr,
                    total = i.NetAmount, paid = i.PaidAmount, due = i.DueAmount, date_ar = ArabicFormat.Date(i.CreatedAt),
                    items = i.Items.Select(x => new { service_name = x.ServiceName, type_ar = x.ItemTypeAr, status_ar = x.StatusAr, amount = x.NetPrice }).ToList(),
                });
        }
        return new { items, currency = "د.ع" };
    }

    private async Task<object> CreditAsync(List<Guid> patients, CancellationToken ct)
    {
        var names = await FirstNamesAsync(ct);
        var items = new List<object>();
        foreach (var p in patients)
        {
            var credit = await files.CreditAsync(p, ct);
            items.Add(new { patient_first_name = names.GetValueOrDefault(p, ""), balance = credit.Balance });
        }
        return new { items, currency = "د.ع", note = "الرصيد يُخصم تلقائياً من الدفع في المجمع" };
    }

    private async Task<IReadOnlyList<PublicBranchDto>> MatchBranchesAsync(string? name, CancellationToken ct)
    {
        var list = await branches.PublicListAsync(null, ct);
        var term = AssistantSafety.Normalize(name);
        return term.Length == 0 ? list : list.Where(b => AssistantSafety.Normalize($"{b.Name} {b.City} {b.Address}").Contains(term)).ToList();
    }

    private async Task<object> BranchesAsync(string? name, CancellationToken ct) => new
    {
        items = (await MatchBranchesAsync(name, ct)).Select(b => new
        {
            name = b.Name, city = b.City, address = b.Address, phone = b.Phone, whatsapp_url = b.WhatsappUrl, working_hours = b.WorkingHours,
            status_ar = b.StatusAr, closed_reason_ar = b.StatusReasonAr, open_now = b.OpenNow, map_url = b.Directions?.GoogleMapsUrl,
        }).ToList(),
    };

    private async Task<object> SpecialtiesAsync(string? branch, CancellationToken ct)
    {
        var map = new Dictionary<string, (string Code, int Doctors, List<string> Branches)>();
        foreach (var b in await MatchBranchesAsync(branch, ct))
            foreach (var s in (await branches.PublicHomeAsync(b.BranchId, ct)).Specialties)
            {
                var e = map.GetValueOrDefault(s.NameAr, (s.Code, 0, []));
                e.Branches.Add(b.Name);
                map[s.NameAr] = (e.Code, e.Doctors + s.DoctorsCount, e.Branches);
            }
        return new { items = map.OrderBy(x => x.Key).Select(x => new { name = x.Key, code = x.Value.Code, doctors_count = x.Value.Doctors, branches = x.Value.Branches }).ToList() };
    }

    private async Task<object> DoctorsAsync(string? specialty, string? name, string? branch, CancellationToken ct)
    {
        var items = new List<object>();
        foreach (var b in await MatchBranchesAsync(branch, ct))
        {
            if (items.Count >= MaxDoctors) break;
            foreach (var d in await bookings.PublicDoctorsAsync(b.BranchId, specialty, name, ct))
            {
                if (items.Count >= MaxDoctors) break;
                items.Add(new
                {
                    doctor_id = d.DoctorId, name = d.FullName, specialty = d.Specialty, title = d.JobTitle, clinic = d.ClinicName, branch_name = b.Name,
                    branch_status_ar = b.StatusAr, new_visit_price = d.NewVisitPrice, follow_up_price = d.FollowUpPrice,
                });
            }
        }
        return new { items, currency = "د.ع" };
    }

    private async Task<object> AvailabilityAsync(string? doctorId, string? from, int? days, CancellationToken ct)
    {
        if (!Guid.TryParse(doctorId, out var id)) throw new NotFound();
        var start = DateOnly.TryParse(from, out var f) ? f : clock.BaghdadToday();
        var count = Math.Clamp(days ?? 7, 1, MaxAvailabilityDays);
        var a = await bookings.AvailabilityAsync(id, start, start.AddDays(count - 1), ct);
        var list = a.Days.Select(d => new
        {
            session_id = d.SessionId, date = d.Date.ToString("yyyy-MM-dd"), date_ar = ArabicFormat.Date(d.Date), weekday_ar = WeekdaysAr[(int)d.Date.DayOfWeek],
            start = d.Start, end = d.End, status = d.Status, online_remaining = d.OnlineRemaining,
            expected_time_ar = d.NextEstimatedTime is { } t ? ArabicFormat.Time(t) : null, cancel_reason = d.CancelReason,
        }).ToList();
        var nearest = list.FirstOrDefault(d => d.status == SessionStatuses.Open && d.online_remaining > 0);
        return new { days = list, nearest_available = nearest };
    }

    /// <summary>
    /// اقتراح فقط: يتحقق أن الفرد من الحساب وأن الوجبة مفتوحة وفيها مقاعد، ويحجز booking_id جديداً للتطبيق (يُستخدم لإحصاء التأكيدات).
    /// لا يُنشئ حجزاً ولا يغير أي شيء في الوجبة.
    /// </summary>
    private async Task<object> ProposeAsync(string? sessionId, string? patientId, Guid? defaultPatient, CancellationToken ct)
    {
        if (!Guid.TryParse(sessionId, out var sid)) throw new NotFound();
        var members = await portal.MyPatientsAsync(portal.AccountId, ct);
        Guid patient;
        if (!string.IsNullOrWhiteSpace(patientId))
            patient = Guid.TryParse(patientId, out var p) && members.Any(m => m.PatientId == p) ? p : throw new NotFound();
        else if (defaultPatient is { } d) patient = d;
        else if (members.Count == 1) patient = members[0].PatientId;
        else return new { error = "patient_required", family = members.Select(m => new { patient_id = m.PatientId, first_name = AssistantSafety.FirstName(m.FullName) }).ToList() };

        var s = await (from x in db.DoctorSessions.AsNoTracking()
                       join doc in db.Doctors.AsNoTracking() on x.DoctorId equals doc.DoctorId
                       join c in db.Clinics.AsNoTracking() on x.ClinicId equals c.ClinicId
                       join b in db.Branches.AsNoTracking() on x.BranchId equals b.BranchId
                       where x.SessionId == sid && doc.IsActive && c.IsActive && b.IsActive
                       select new { x.DoctorId, x.SessionDate, x.BranchId, DoctorName = doc.FullName, doc.Specialty, ClinicName = c.Name, BranchName = b.Name })
                .FirstOrDefaultAsync(ct) ?? throw new NotFound();
        var day = (await bookings.AvailabilityAsync(s.DoctorId, s.SessionDate, s.SessionDate, ct)).Days.FirstOrDefault(x => x.SessionId == sid);
        if (day is null || day.Status != SessionStatuses.Open || day.OnlineRemaining <= 0)
            return new { error = "session_not_available", status = day?.Status ?? SessionStatuses.Closed };
        var doctor = (await bookings.PublicDoctorsAsync(s.BranchId, null, null, ct)).FirstOrDefault(x => x.DoctorId == s.DoctorId);
        // المراجع لا يحدد نوع الزيارة (POST /bookings يرفضه بـ 403): النظام يحسبه من آخر زيارة عند نفس الطبيب
        const string kind = "auto";
        var first = AssistantSafety.FirstName(members.First(m => m.PatientId == patient).FullName);

        var bookingId = UuidV7.New();
        var account = portal.AccountId;
        var now = clock.UtcNow;
        await db.ExecuteAsync($"INSERT INTO assistant_proposals (booking_id, patient_account_id, created_at) VALUES ({bookingId}, {account}, {now})", ct);
        Proposal = new AssistantProposalDto(bookingId, patient, first, sid, s.DoctorId, s.DoctorName, s.Specialty, s.ClinicName, s.BranchName,
            s.SessionDate, ArabicFormat.Date(s.SessionDate), day.Start, day.End, day.NextEstimatedTime is { } t ? ArabicFormat.Time(t) : null,
            doctor?.NewVisitPrice, doctor?.FollowUpPrice, kind);
        return new
        {
            proposal_ready = true, booked = false, patient_first_name = first, doctor_name = s.DoctorName, branch_name = s.BranchName,
            date_ar = Proposal.DateAr, weekday_ar = WeekdaysAr[(int)s.SessionDate.DayOfWeek], expected_time_ar = Proposal.EstimatedTimeAr,
            new_visit_price = doctor?.NewVisitPrice, follow_up_price = doctor?.FollowUpPrice,
            instruction = "الحجز لم يتم بعد. المراجع يضغط زر «تأكيد الحجز» تحت الرد.",
        };
    }

    private async Task<object> FaqAsync(string? query, CancellationToken ct)
    {
        var all = await support.FaqAsync(false, ct);
        var terms = AssistantSafety.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.StartsWith("ال") && t.Length > 4 ? t[2..] : t).Where(t => t.Length >= 3).Distinct().ToList();
        var scored = all.Select(f => (f, score: terms.Count(t => AssistantSafety.Normalize($"{f.CategoryAr} {f.QuestionAr} {f.AnswerAr}").Contains(t))))
            .Where(x => terms.Count == 0 || x.score > 0).OrderByDescending(x => x.score).Take(terms.Count == 0 ? 20 : 5).ToList();
        if (scored.Count == 0) AddAction("open_support", "اطلب مساعدة من المجمع", null);
        return new
        {
            items = scored.Select(x => new { category = x.f.CategoryAr, question = x.f.QuestionAr, answer = x.f.AnswerAr }).ToList(),
            found = scored.Count > 0,
        };
    }

    private object PaymentMethods() => new
    {
        at_clinic = new[] { "نقداً بالدينار أو الدولار", "بطاقة (جهاز POS) في نقطة التحصيل" },
        online = gateways.Available().Select(p => p.NameAr).ToList(),
        wallet_note = "رصيد المحفظة يُخصم تلقائياً عند الدفع في المجمع",
    };
}
