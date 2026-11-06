using System.Text.Json.Nodes;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlRahma.Api.Tests;

/// <summary>
/// الجلسة 4: رحلة المختبر (6.1)، المراجع الخارجي (6.3)، صورة النتيجة الخارجية (1.2-ج3)، الدعم، خيارات إلغاء الطبيب (5.1-ب)،
/// النقد لمن غادر قبل الدخول (5.2-ب)، نمط التحصيل (3.1-د)، فوترة الصيدلية وسجل الصرف (6.2)، والدولار (7.2).
/// </summary>
[Collection(ApiCollection.Name)]
public class Session4Tests(TestApp app)
{
    private static Guid Id(JsonNode? n, string k) => n![k]!.GetValue<Guid>();
    private static string S(JsonNode? n, string k) => n![k]!.GetValue<string>();
    private static long L(JsonNode? n, string k) => n![k]!.GetValue<long>();
    private static bool B(JsonNode? n, string k) => n![k]!.GetValue<bool>();
    private static string Phone() => "0779" + Random.Shared.Next(1000000, 9999999);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private async Task<(ClinicSetup S, Guid Branch)> NewBranchAsync()
    {
        var branch = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO branches (branch_id, code, name, city) VALUES ('{branch}', 'S{branch.ToString()[..5].ToUpperInvariant()}', 'فرع {branch.ToString()[..4]}', 'بغداد')");
        return (await new QueueTests(app).SetupAsync(branch), branch);
    }

    private async Task<(Guid Patient, HttpClient Me)> AppPatientAsync(HttpClient reception, string name)
    {
        var phone = Phone();
        var r = await Api.PostAsync(reception, "/api/v1/patients", new { full_name = name, phone_number = phone, gender = "male", approx_age = 40 }, Guid.NewGuid());
        Assert.True(r.Status == 201, r.Raw);
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        var ok = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") });
        Assert.True(ok.Status == 200, ok.Raw);
        return (Id(r.Body, "patient_id"), app.Client(S(ok.Body, "access_token")));
    }

    /// <summary>كشفية مدفوعة ثم معاينة بطلبات (تحليلان) ووصفة بسطرين.</summary>
    private async Task<(Guid Ticket, JsonNode Consult)> ConsultAsync(ClinicSetup s, Guid patient, object[] labOrders, object[] prescription)
    {
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.True(checkIn.Status == 201, checkIn.Raw);
        var ticket = Id(checkIn.Body, "ticket_id");
        await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid());
        var item = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{ticket}'");
        var pay = await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = L(checkIn.Body, "amount_due") } } }, Guid.NewGuid());
        Assert.True(pay.Status == 201, pay.Raw);
        await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call");
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/start")).Status);
        var consult = await Api.PostAsync(s.Doctor, "/api/v1/consultations", new
        {
            ticket_id = ticket, diagnosis = "فحص", lab_orders = labOrders, prescription,
        }, Guid.NewGuid());
        Assert.True(consult.Status == 201, consult.Raw);
        return (ticket, consult.Body!);
    }

    private async Task<Guid> LabServiceAsync(Guid branch, string name, long price, string currency = "IQD")
    {
        var id = Guid.NewGuid();
        var clinic = await app.ScalarAsync<Guid?>($"SELECT clinic_id FROM clinics WHERE branch_id = '{branch}' AND department = 'lab' LIMIT 1");
        if (clinic is null)
        {
            clinic = Guid.NewGuid();
            await app.ExecAsync($"INSERT INTO clinics (clinic_id, branch_id, name, department) VALUES ('{clinic}', '{branch}', 'مختبر {clinic.ToString()![..4]}', 'lab')");
        }
        await app.ExecAsync($"INSERT INTO services (service_id, branch_id, clinic_id, name, type, price, currency) VALUES ('{id}', '{branch}', '{clinic}', '{name}', 'lab', {price}, '{currency}')");
        return id;
    }

    // ------------------------------------------------------------------ البند 2: رحلة المختبر (6.1)

    [SkippableFact]
    public async Task Lab_sees_only_its_referrals_confirms_arrival_patient_pays_part_and_work_starts_after_payment()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (s, branch) = await NewBranchAsync();
        var (patient, _) = await AppPatientAsync(s.Reception, "مراجع رحلة المختبر");
        Guid cbc = await LabServiceAsync(branch, "CBC", 30000), sugar = await LabServiceAsync(branch, "سكر صائم", 10000);
        var (_, consult) = await ConsultAsync(s, patient, [new { service_id = cbc }, new { service_id = sugar }], []);
        var orders = consult["orders"]!.AsArray();
        var cbcOrder = orders.First(o => Id(o, "service_id") == cbc)!;
        var sugarOrder = orders.First(o => Id(o, "service_id") == sugar)!;
        var (_, techToken) = await app.CreateStaffAsync("lab_tech", branch);
        var tech = app.Client(techToken);

        // يرى المحوَّلين إليه فقط: فني مختبر فرع آخر لا يرى التحويل، والاستقبال بلا صلاحية المختبر لا يرى القائمة
        var referred = await Api.GetAsync(tech, "/api/v1/referrals?service=lab&status=referred");
        Assert.Contains(referred.Body!["items"]!.AsArray(), x => Id(x, "patient_id") == patient);
        var (_, otherTech) = await app.CreateStaffAsync("lab_tech", app.Branch1);
        Assert.DoesNotContain((await Api.GetAsync(app.Client(otherTech), "/api/v1/referrals?service=lab&status=referred")).Body!["items"]!.AsArray(), x => Id(x, "patient_id") == patient);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/lab/worklist")).Status);
        // قبل تأكيد الوصول: لا شيء في قائمة العمل
        Assert.DoesNotContain((await Api.GetAsync(tech, "/api/v1/lab/worklist")).Body!["items"]!.AsArray(), i => Id(i, "order_item_id") == Id(cbcOrder, "order_item_id"));

        // يؤكد الوصول (مسح رمز المراجع) فيظهر الطلبان بانتظار الدفع، ولا عمل قبل الدفع
        var scan = await Api.PostAsync(tech, "/api/v1/referrals/scan", new { service = "lab", code = $"patient:{patient}" });
        Assert.True(scan.Status == 200, scan.Raw);
        var list = (await Api.GetAsync(tech, "/api/v1/lab/worklist")).Body!["items"]!.AsArray();
        Assert.True(B(list.First(i => Id(i, "order_item_id") == Id(cbcOrder, "order_item_id")), "awaiting_payment"));
        Assert.Equal("INVOICE_UNPAID", (await Api.PostAsync(tech, $"/api/v1/lab/orders/{Id(cbcOrder, "order_item_id")}/collect-sample")).Code);

        // المراجع يختار جزءاً: يدفع CBC فقط
        var pay = await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { Id(cbcOrder, "invoice_item_id") }, tenders = new[] { new { method = "cash", amount = 30000 } } }, Guid.NewGuid());
        Assert.True(pay.Status == 201, pay.Raw);
        Assert.Equal(200, (await Api.PostAsync(tech, $"/api/v1/lab/orders/{Id(cbcOrder, "order_item_id")}/collect-sample")).Status);
        Assert.Equal("INVOICE_UNPAID", (await Api.PostAsync(tech, $"/api/v1/lab/orders/{Id(sugarOrder, "order_item_id")}/collect-sample")).Code);
        // غير المدفوع يبقى في السلة فقط
        var cart = (await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}/billable-items")).Body!["items"]!.AsArray();
        Assert.Single(cart, i => Id(i, "invoice_item_id") == Id(sugarOrder, "invoice_item_id"));
    }

    // ------------------------------------------------------------------ البند 3: المراجع الخارجي (6.3)

    [SkippableFact]
    public async Task External_lab_patient_gets_a_short_file_chooses_tests_and_pays()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (s, branch) = await NewBranchAsync();
        Guid cbc = await LabServiceAsync(branch, "CBC خارجي", 30000), tsh = await LabServiceAsync(branch, "TSH", 20000);
        var phone = Phone();
        var res = await Api.PostAsync(s.Reception, "/api/v1/medical-orders", new
        {
            new_patient = new { full_name = "مراجع خارجي للمختبر", phone_number = phone },
            items = new[] { new { service_id = cbc }, new { service_id = tsh } }, referring_doctor_name = "د. خارجي",
        }, Guid.NewGuid());
        Assert.True(res.Status == 201, res.Raw);
        Assert.True(B(res.Body, "patient_created"));
        Assert.Equal(50000, L(res.Body, "total_due"));
        var patient = Id(res.Body, "patient_id");
        // الملف مختصر: الاسم والهاتف فقط
        Assert.Equal(0, await app.ScalarAsync<long>($"SELECT count(*) FROM patients WHERE patient_id = '{patient}' AND (birth_date IS NOT NULL OR gender IS NOT NULL)"));
        // طلب ثانٍ بنفس الاسم والهاتف يستخدم الملف نفسه
        var again = await Api.PostAsync(s.Reception, "/api/v1/medical-orders", new
        {
            new_patient = new { full_name = "مراجع خارجي للمختبر", phone_number = phone }, items = new[] { new { service_id = tsh } },
        }, Guid.NewGuid());
        Assert.False(B(again.Body, "patient_created"));
        Assert.Equal(patient, Id(again.Body, "patient_id"));

        var items = res.Body!["items"]!.AsArray().Select(i => Id(i, "invoice_item_id")).ToArray();
        await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid());
        var pay = await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = items, tenders = new[] { new { method = "cash", amount = 50000 } } }, Guid.NewGuid());
        Assert.True(pay.Status == 201, pay.Raw);
        var (_, techToken) = await app.CreateStaffAsync("lab_tech", branch);
        var work = (await Api.GetAsync(app.Client(techToken), "/api/v1/lab/worklist")).Body!["items"]!.AsArray();
        Assert.Equal(2, work.Count(i => Id(i, "patient_id") == patient));
        // بلا مراجع ولا ملف مختصر: خطأ تحقق
        Assert.Equal(400, (await Api.PostAsync(s.Reception, "/api/v1/medical-orders", new { items = new[] { new { service_id = tsh } } }, Guid.NewGuid())).Status);
    }

    // ------------------------------------------------------------------ البند 4: صورة النتيجة الخارجية (1.2-ج3)

    [SkippableFact]
    public async Task Reception_uploads_external_result_image_on_the_ticket_and_doctor_sees_it()
    {
        Skip.If(TestApp.ServerUrl is null);
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var patient = await q.PatientAsync(s.Reception);
        var ticket = Id((await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid())).Body, "ticket_id");

        var up = await Api.PostAsync(s.Reception, "/api/v1/files/upload-url", new
        {
            file_name = "نتيجة-خارجية.jpg", content_type = "image/jpeg", size_bytes = 4000, purpose = "external_result", ticket_id = ticket,
        });
        Assert.True(up.Status == 201, up.Raw);
        app.Storage.Upload(S(up.Body, "upload_url"), 4000);
        var done = await Api.PostAsync(s.Reception, $"/api/v1/files/{Id(up.Body, "file_id")}/complete");
        Assert.Equal("stored", S(done.Body, "status"));

        var seen = await Api.GetAsync(s.Doctor, $"/api/v1/files?ticket_id={ticket}");
        Assert.True(seen.Status == 200, seen.Raw);
        var file = seen.Body!["items"]!.AsArray().Single()!;
        Assert.Equal("external_result", S(file, "purpose"));
        Assert.Contains("X-Amz-Signature=down", S(file, "download_url"));
        Assert.Single((await Api.GetAsync(s.Doctor, $"/api/v1/files?patient_id={patient}")).Body!["items"]!.AsArray());

        // فني المختبر لا يرفع نتيجة خارجية، والملف غير صورة أو PDF مرفوض
        var (_, tech) = await app.CreateStaffAsync("lab_tech");
        Assert.Equal(403, (await Api.PostAsync(app.Client(tech), "/api/v1/files/upload-url", new { file_name = "x.jpg", content_type = "image/jpeg", size_bytes = 10, purpose = "external_result", ticket_id = ticket })).Status);
        Assert.Equal(400, (await Api.PostAsync(s.Reception, "/api/v1/files/upload-url", new { file_name = "x.dcm", content_type = "application/dicom", size_bytes = 10, purpose = "external_result", ticket_id = ticket })).Status);
    }

    // ------------------------------------------------------------------ البند 5: الدعم

    [SkippableFact]
    public async Task Support_faq_contacts_and_requests_with_staff_reply_and_patient_notification()
    {
        Skip.If(TestApp.ServerUrl is null);
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        // الأسئلة الشائعة عامة، وفيها سياسات الاسترجاع (5.2-هـ)
        var faq = await Api.GetAsync(app.Client(), "/api/v1/public/faq");
        Assert.Equal(200, faq.Status);
        Assert.Contains(faq.Body!["items"]!.AsArray(), f => S(f, "answer_ar").Contains("يُضاف ما دفعته رصيداً في محفظتك"));
        Assert.Contains(faq.Body!["items"]!.AsArray(), f => S(f, "answer_ar").Contains("ولا يُسترجع نقداً"));

        var (_, managerToken) = await app.CreateStaffAsync("branch_manager");
        var manager = app.Client(managerToken);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/support-requests")).Status);
        var created = await Api.PostAsync(manager, "/api/v1/faq-items", new { category = "app", question_ar = $"كيف أغيّر رقمي؟ {Guid.NewGuid():N}", answer_ar = "من الاستقبال", sort = 5 });
        Assert.True(created.Status == 201, created.Raw);
        var faqId = Id(created.Body, "faq_id");
        Assert.Equal(200, (await Api.PatchAsync(manager, $"/api/v1/faq-items/{faqId}", new { is_active = false })).Status);
        Assert.DoesNotContain((await Api.GetAsync(app.Client(), "/api/v1/public/faq")).Body!["items"]!.AsArray(), f => Id(f, "faq_id") == faqId);
        Assert.Contains((await Api.GetAsync(manager, "/api/v1/faq-items")).Body!["items"]!.AsArray(), f => Id(f, "faq_id") == faqId);
        Assert.Equal(204, (await Api.SendAsync(manager, HttpMethod.Delete, $"/api/v1/faq-items/{faqId}")).Status);

        // التواصل: واتساب الفرع
        var admin = app.Client(await app.AdminTokenAsync());
        Assert.Equal(200, (await Api.PatchAsync(admin, $"/api/v1/branches/{app.Branch1}", new { whatsapp = "07701234567" })).Status);
        var contact = (await Api.GetAsync(app.Client(), "/api/v1/public/contacts")).Body!["items"]!.AsArray().First(b => Id(b, "branch_id") == app.Branch1)!;
        Assert.Equal("https://wa.me/9647701234567", S(contact, "whatsapp_url"));

        // طلب دعم من المراجع مرتبط بحجز، ورد الموظف، وإشعار للمراجع
        var (patient, me) = await AppPatientAsync(s.Reception, "مراجع الدعم");
        var session = Id((await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).Body!["items"]![0], "session_id");
        var booking = Id((await Api.PostAsync(me, "/api/v1/bookings", new { patient_id = patient, session_id = session, visit_kind = "auto" }, Guid.NewGuid())).Body, "booking_id");
        Assert.Equal(400, (await Api.PostAsync(me, "/api/v1/me/support-requests", new { type = "x", description = "قصير" })).Status);
        var req = await Api.PostAsync(me, "/api/v1/me/support-requests", new { type = "payment", description = "دفعت ولم يظهر الدفع في الحجز", booking_id = booking });
        Assert.True(req.Status == 201, req.Raw);
        Assert.Equal("open", S(req.Body, "status"));
        Assert.Equal(patient, Id(req.Body, "patient_id"));
        var id = Id(req.Body, "support_request_id");

        var open = await Api.GetAsync(manager, "/api/v1/support-requests?status=open");
        var row = open.Body!["items"]!.AsArray().First(x => Id(x, "support_request_id") == id)!;
        Assert.Equal("مراجع الدعم", S(row, "patient_name"));
        Assert.False(string.IsNullOrEmpty(S(row, "phone_number")));
        var reply = await Api.PostAsync(manager, $"/api/v1/support-requests/{id}/reply", new { reply = "تم التحقق، الدفع مسجل الآن" });
        Assert.True(reply.Status == 200, reply.Raw);
        Assert.Equal("replied", S(reply.Body, "status"));

        var mine = (await Api.GetAsync(me, "/api/v1/me/support-requests")).Body!["items"]!.AsArray().Single()!;
        Assert.Equal("تم التحقق، الدفع مسجل الآن", S(mine, "reply"));
        Assert.Equal("فريق الدعم", S(mine, "replied_by_name"));
        Assert.Contains((await Api.GetAsync(me, "/api/v1/me/notifications")).Body!["items"]!.AsArray(), n => S(n, "type") == "support.replied");
    }

    // ------------------------------------------------------------------ البند 6: خيارات إلغاء الطبيب (5.1-ب)

    private async Task<(ClinicSetup S, Guid Patient, HttpClient Me, Guid Booking, Guid Op)> PaidBookingAsync(DateOnly day)
    {
        var s = await new QueueTests(app).SetupAsync();
        var (patient, me) = await AppPatientAsync(s.Reception, "مراجع إلغاء الطبيب");
        var session = Id((await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).Body!["items"]![0], "session_id");
        var b = await Api.PostAsync(me, "/api/v1/bookings", new { patient_id = patient, session_id = session, visit_kind = "auto" }, Guid.NewGuid());
        Assert.True(b.Status == 201, b.Raw);
        var booking = Id(b.Body, "booking_id");
        var op = await Api.PostAsync(me, "/api/v1/online-payments", new { purpose = "booking_full", patient_id = patient, booking_id = booking, provider = "zaincash" }, Guid.NewGuid());
        Assert.True(op.Status == 201, op.Raw);
        Assert.Equal("succeeded", S((await MockPay.SucceedAsync(me, Id(op.Body, "online_payment_id"))).Body, "status"));
        var cancel = await Api.PostAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions/{day:yyyy-MM-dd}/cancel", new { reason = "سفر الطبيب" });
        Assert.True(cancel.Status == 200, cancel.Raw);
        return (s, patient, me, booking, Id(op.Body, "online_payment_id"));
    }

    private Task<long> CreditAsync(Guid patient) => app.ScalarAsync<long>($"SELECT COALESCE(SUM(amount), 0)::bigint FROM patient_credit_ledger WHERE patient_id = '{patient}'");

    [SkippableFact]
    public async Task Doctor_cancel_shows_three_options_and_keeping_credit_works()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (_, patient, me, booking, _) = await PaidBookingAsync(Today);
        var mine = (await Api.GetAsync(me, "/api/v1/bookings/my?scope=past")).Body!["items"]!.AsArray().First(x => Id(x, "booking_id") == booking)!;
        Assert.True(B(mine, "cancel_options_pending"));

        var opts = await Api.GetAsync(me, $"/api/v1/me/bookings/{booking}/cancellation-options");
        Assert.True(opts.Status == 200, opts.Raw);
        Assert.True(B(opts.Body, "doctor_cancelled"));
        Assert.Equal(25000, L(opts.Body, "amount"));
        var options = opts.Body!["options"]!.AsArray();
        Assert.Equal(["rebook", "credit", "refund"], options.Select(o => S(o, "code")).ToArray());
        Assert.All(options, o => Assert.True(B(o, "available")));
        Assert.NotNull(options[0]!["session_id"]);

        var choice = await Api.PostAsync(me, $"/api/v1/me/bookings/{booking}/cancellation-choice", new { choice = "credit" });
        Assert.True(choice.Status == 200, choice.Raw);
        Assert.Equal("credit", S(choice.Body, "resolution"));
        Assert.Equal(25000, L(choice.Body, "credit_balance"));
        Assert.Equal(25000, await CreditAsync(patient));
        Assert.Equal("CHOICE_ALREADY_MADE", (await Api.PostAsync(me, $"/api/v1/me/bookings/{booking}/cancellation-choice", new { choice = "refund" })).Code);
        Assert.False(B((await Api.GetAsync(me, $"/api/v1/me/bookings/{booking}/cancellation-options")).Body!["options"]![1], "available"));
    }

    [SkippableFact]
    public async Task Doctor_cancel_rebook_uses_the_credit_and_refund_goes_back_to_the_gateway()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (s, patient, me, booking, _) = await PaidBookingAsync(Today);
        var rebook = await Api.PostAsync(me, $"/api/v1/me/bookings/{booking}/cancellation-choice", new { choice = "rebook" });
        Assert.True(rebook.Status == 200, rebook.Raw);
        Assert.Equal("rebooked", S(rebook.Body, "resolution"));
        var nb = rebook.Body!["new_booking"]!;
        Assert.Equal("deposit_paid", S(nb, "payment_status"));
        Assert.True(DateOnly.Parse(S(nb, "date")) > Today);
        // الرصيد انتقل عربوناً للحجز الجديد: يُطرح من المطلوب عند الوصول
        Assert.Equal(25000, await CreditAsync(patient));
        Assert.Equal(25000, await app.ScalarAsync<long>($"SELECT COALESCE(SUM(amount), 0)::bigint FROM patient_credit_ledger WHERE booking_id = '{Id(nb, "booking_id")}' AND kind = 'booking_deposit'"));

        var (_, patient2, me2, booking2, op2) = await PaidBookingAsync(Today);
        var refund = await Api.PostAsync(me2, $"/api/v1/me/bookings/{booking2}/cancellation-choice", new { choice = "refund" });
        Assert.True(refund.Status == 200, refund.Raw);
        Assert.Equal("refunded", S(refund.Body, "resolution"));
        Assert.Equal(0, await CreditAsync(patient2));
        Assert.Equal("refunded", await app.ScalarAsync<string>($"SELECT status FROM online_payments WHERE online_payment_id = '{op2}'"));
        Assert.Equal("refunded", await app.ScalarAsync<string>($"SELECT payment_status FROM bookings WHERE booking_id = '{booking2}'"));
        // حجز ألغاه المراجع بنفسه: لا خيارات
        Assert.False(B((await Api.GetAsync(me, $"/api/v1/me/bookings/{Id(nb, "booking_id")}/cancellation-options")).Body, "doctor_cancelled"));
    }

    // ------------------------------------------------------------------ البند 6: دفع نقداً وغادر قبل الدخول

    [SkippableFact]
    public async Task Cash_paid_patient_who_left_before_entry_gets_wallet_credit_and_drawer_is_unchanged()
    {
        Skip.If(TestApp.ServerUrl is null);
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var patient = await q.PatientAsync(s.Reception);
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        var ticket = Id((await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid())).Body, "ticket_id");
        var item = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{ticket}'");
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid())).Status);
        var before = (await Api.GetAsync(s.Reception, "/api/v1/shifts/current")).Body!["summary"]!;

        // نهاية اليوم: المهمة الدورية
        var sessionId = await app.ScalarAsync<Guid>($"SELECT session_id FROM queue_tickets WHERE ticket_id = '{ticket}'");
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var session = await db.DoctorSessions.FirstAsync(x => x.SessionId == sessionId);
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ScheduledJobs>().CreditLeftBeforeEntryAsync(session, DateTimeOffset.UtcNow, default));
        }
        Assert.Equal("no_show", await app.ScalarAsync<string>($"SELECT status FROM queue_tickets WHERE ticket_id = '{ticket}'"));
        Assert.Equal("refunded", await app.ScalarAsync<string>($"SELECT status FROM invoice_items WHERE invoice_item_id = '{item}'"));
        Assert.Equal(25000, await CreditAsync(patient));
        Assert.Equal("left_before_entry", await app.ScalarAsync<string>($"SELECT kind FROM patient_credit_ledger WHERE patient_id = '{patient}'"));
        Assert.Equal("patient_credit", await app.ScalarAsync<string>($"SELECT method FROM refunds WHERE invoice_item_id = '{item}'"));
        // الحساب اليومي والمطابقة: النقد ما زال في الدرج ولا استرجاع نقدي على الحساب
        var after = (await Api.GetAsync(s.Reception, "/api/v1/shifts/current")).Body!["summary"]!;
        Assert.Equal(L(before, "cash_in_hand"), L(after, "cash_in_hand"));
        Assert.Equal(0, L(after, "cash_refunds"));
        Assert.Equal(0, (int)L(after, "refunds_count"));
        // لا يتكرر
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var session = await db.DoctorSessions.FirstAsync(x => x.SessionId == sessionId);
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ScheduledJobs>().CreditLeftBeforeEntryAsync(session, DateTimeOffset.UtcNow, default));
        }
    }

    [SkippableFact]
    public async Task Collection_mode_is_a_branch_setting()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (s, branch) = await NewBranchAsync();
        var set = await Api.PatchAsync(s.Admin, $"/api/v1/branches/{branch}/settings", new { values = new Dictionary<string, object> { ["billing.collection_mode"] = "distributed" } });
        Assert.True(set.Status == 200, set.Raw);
        var row = set.Body!["items"]!.AsArray().First(x => S(x, "key") == "billing.collection_mode")!;
        Assert.Equal("distributed", S(row, "value_text"));
        Assert.Equal("\"distributed\"", await app.ScalarAsync<string>($"SELECT value::text FROM system_settings WHERE branch_id = '{branch}' AND key = 'billing.collection_mode'"));
    }

    // ------------------------------------------------------------------ البند 1: الصيدلية

    private async Task<Guid> DrugAsync(Guid branch, string name, long price, bool requiresPrescription = false)
    {
        var id = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO services (service_id, branch_id, name, type, price, requires_prescription) VALUES ('{id}', '{branch}', '{name}', 'pharmacy', {price}, {requiresPrescription})");
        return id;
    }

    [SkippableFact]
    public async Task Pharmacy_partial_dispense_bills_collects_then_delivers_and_appears_in_daily_log_as_partial()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (s, branch) = await NewBranchAsync();
        var (patient, _) = await AppPatientAsync(s.Reception, "مراجع صيدلية");
        var omeprazole = await DrugAsync(branch, "Omeprazole 20mg", 4000);
        var (_, consult) = await ConsultAsync(s, patient, [], [
            new { drug_name = "Omeprazole 20mg", dose = "1 cap", frequency = "مرتين", duration_days = 14 },
            new { drug_name = "دواء غير متوفر", dose = "1", frequency = "يومياً", duration_days = 5 },
        ]);
        var code = S(consult["prescription"], "prescription_code");
        var (pharmacistId, pharmToken) = await app.CreateStaffAsync("pharmacist", branch);
        var pharm = app.Client(pharmToken);
        Assert.True((await Api.PostAsync(pharm, "/api/v1/referrals/scan", new { service = "pharmacy", code })).Status == 200);
        var rxId = Id((await Api.GetAsync(pharm, $"/api/v1/pharmacy/prescriptions/{code}")).Body, "prescription_id");

        // التسليم بعد الدفع: لا صرف قبل التسعير
        var early = await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rxId}/dispense", new { lines = new object[] { new { line_no = 1, dispensed = true }, new { line_no = 2, dispensed = false, not_dispensed_reason = "غير متوفر" } } }, Guid.NewGuid());
        Assert.Equal("PRESCRIPTION_NOT_BILLED", early.Code);
        // سطر بلا دواء من القائمة ولا سعر لا يُسعَّر
        var noPrice = await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rxId}/bill", new { lines = new object[] { new { line_no = 1, available = true, quantity = 2 }, new { line_no = 2, available = false } } }, Guid.NewGuid());
        Assert.Equal(400, noPrice.Status);

        var bill = await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rxId}/bill", new
        {
            lines = new object[] { new { line_no = 1, available = true, wanted = true, quantity = 2, service_id = omeprazole }, new { line_no = 2, available = false, reason = "نفد" } },
        }, Guid.NewGuid());
        Assert.True(bill.Status == 200, bill.Raw);
        Assert.Equal(8000, L(bill.Body, "total_due"));
        var itemId = Id(bill.Body!["lines"]!.AsArray().First(l => S(l, "status") == "billed"), "invoice_item_id");
        Assert.Equal("PRESCRIPTION_ALREADY_BILLED", (await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rxId}/bill", new { lines = new object[] { new { line_no = 1, available = false }, new { line_no = 2, available = false } } }, Guid.NewGuid())).Code);

        var dispense = new { lines = new object[] { new { line_no = 1, dispensed = true }, new { line_no = 2, dispensed = false, not_dispensed_reason = "غير متوفر" } } };
        Assert.Equal("INVOICE_UNPAID", (await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rxId}/dispense", dispense, Guid.NewGuid())).Code);
        // التحصيل على الحساب اليومي لموظف الصيدلية
        var shift = await Api.PostAsync(pharm, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid());
        Assert.True(shift.Status == 201, shift.Raw);
        var pay = await Api.PostAsync(pharm, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { itemId }, tenders = new[] { new { method = "cash", amount = 8000 } } }, Guid.NewGuid());
        Assert.True(pay.Status == 201, pay.Raw);
        var done = await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rxId}/dispense", dispense, Guid.NewGuid());
        Assert.True(done.Status == 200, done.Raw);
        Assert.Equal("paid", S(done.Body!["items"]![0], "payment_status"));

        var log = await Api.GetAsync(pharm, $"/api/v1/pharmacy/dispense-log?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(log.Status == 200, log.Raw);
        var rows = log.Body!["items"]!.AsArray().Where(i => Id(i, "patient_id") == patient).ToList();
        Assert.Equal(2, rows.Count);
        var given = rows.First(r => S(r, "status") == "dispensed")!;
        Assert.Equal(2, (int)L(given, "quantity"));
        Assert.Equal(8000, L(given, "amount"));
        Assert.Equal("جزئي", S(given, "prescription_status_ar"));
        Assert.False(B(given, "is_external"));
        Assert.Contains(rows, r => S(r, "status") == "not_available");
        Assert.Equal(1, (int)L(log.Body!["summary"], "partial_count"));
        Assert.Equal(8000, L(log.Body!["summary"], "total_amount"));
        // فلاتر: الدواء والموظف، والتصدير CSV
        Assert.Single((await Api.GetAsync(pharm, $"/api/v1/pharmacy/dispense-log?drug=omepra&user_id={pharmacistId}")).Body!["items"]!.AsArray());
        var csv = await pharm.GetAsync("/api/v1/pharmacy/dispense-log?format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Omeprazole 20mg", await csv.Content.ReadAsStringAsync());
        Assert.Single((await Api.GetAsync(pharm, $"/api/v1/pharmacy/dispensed?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&drug=omeprazole")).Body!["items"]!.AsArray(),
            x => S(x, "prescription_code") == code);
    }

    [SkippableFact]
    public async Task External_pharmacy_sale_requires_prescription_image_or_note_for_restricted_drugs()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (s, branch) = await NewBranchAsync();
        var antibiotic = await DrugAsync(branch, "Amoxicillin 500", 6000, requiresPrescription: true);
        var paracetamol = await DrugAsync(branch, "Paracetamol", 1000);
        var (_, pharmToken) = await app.CreateStaffAsync("pharmacist", branch);
        var pharm = app.Client(pharmToken);
        var person = new { full_name = "مراجع خارجي للصيدلية", phone_number = Phone() };

        var refused = await Api.PostAsync(pharm, "/api/v1/pharmacy/external-sales", new { new_patient = person, items = new[] { new { service_id = antibiotic, quantity = 1 } } }, Guid.NewGuid());
        Assert.Equal(422, refused.Status);
        Assert.Equal("PRESCRIPTION_REQUIRED", refused.Code);
        // دواء لا يحتاج وصفة يُصرف بلا شيء
        Assert.Equal(201, (await Api.PostAsync(pharm, "/api/v1/pharmacy/external-sales", new { new_patient = person, items = new[] { new { service_id = paracetamol, quantity = 2 } } }, Guid.NewGuid())).Status);

        // مع صورة الوصفة
        var up = await Api.PostAsync(pharm, "/api/v1/files/upload-url", new { file_name = "rx.jpg", content_type = "image/jpeg", size_bytes = 2000, purpose = "prescription" });
        Assert.True(up.Status == 201, up.Raw);
        app.Storage.Upload(S(up.Body, "upload_url"), 2000);
        Assert.Equal(200, (await Api.PostAsync(pharm, $"/api/v1/files/{Id(up.Body, "file_id")}/complete")).Status);
        var withImage = await Api.PostAsync(pharm, "/api/v1/pharmacy/external-sales", new
        {
            new_patient = person, items = new[] { new { service_id = antibiotic, quantity = 1 } }, prescription_file_id = Id(up.Body, "file_id"),
        }, Guid.NewGuid());
        Assert.True(withImage.Status == 201, withImage.Raw);

        // أو بملاحظة إلزامية، ثم التسليم بعد الدفع
        var withNote = await Api.PostAsync(pharm, "/api/v1/pharmacy/external-sales", new
        {
            new_patient = person, items = new[] { new { service_id = antibiotic, quantity = 2 } }, note = "وصفة ورقية من د. أحمد، اطلعتُ عليها",
            referring_doctor_name = "د. أحمد",
        }, Guid.NewGuid());
        Assert.True(withNote.Status == 201, withNote.Raw);
        Assert.Equal(12000, L(withNote.Body, "total_due"));
        var invoice = Id(withNote.Body, "invoice_id");
        Assert.Equal("INVOICE_UNPAID", (await Api.PostAsync(pharm, $"/api/v1/pharmacy/external-sales/{invoice}/deliver")).Code);
        await Api.PostAsync(pharm, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid());
        var item = Id(withNote.Body!["lines"]![0], "invoice_item_id");
        Assert.Equal(201, (await Api.PostAsync(pharm, "/api/v1/payments", new { patient_id = Id(withNote.Body, "patient_id"), invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 12000 } } }, Guid.NewGuid())).Status);
        var delivered = await Api.PostAsync(pharm, $"/api/v1/pharmacy/external-sales/{invoice}/deliver");
        Assert.True(delivered.Status == 200, delivered.Raw);
        Assert.Equal("dispensed", S(delivered.Body!["lines"]![0], "status"));
        var log = (await Api.GetAsync(pharm, "/api/v1/pharmacy/dispense-log?external=true&status=dispensed")).Body!["items"]!.AsArray();
        Assert.Contains(log, r => S(r, "doctor_name") == "مراجع خارجي (د. أحمد)" && B(r, "is_external"));
    }

    // ------------------------------------------------------------------ البند 7: الدولار

    [SkippableFact]
    public async Task Usd_collection_needs_todays_rate_shows_equivalent_on_receipt_and_daily_account_separates_currencies()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (s, branch) = await NewBranchAsync();
        var patient = await new QueueTests(app).PatientAsync(s.Reception);
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        var ticket = Id((await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid())).Body, "ticket_id");
        var consultItem = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{ticket}'");
        var usdBody = new { patient_id = patient, invoice_item_ids = new[] { consultItem }, tenders = new[] { new { method = "cash_usd", amount = 20 } } };

        // قبل سعر اليوم: مرفوض، وخدمة بالدولار لا تُطلب
        var noRate = await Api.PostAsync(s.Reception, "/api/v1/payments", usdBody, Guid.NewGuid());
        Assert.Equal(409, noRate.Status);
        Assert.Equal("EXCHANGE_RATE_REQUIRED", noRate.Code);
        var ct = await LabServiceAsync(branch, "CT متقدم", 20, "USD");
        Assert.Equal("EXCHANGE_RATE_REQUIRED", (await Api.PostAsync(s.Reception, "/api/v1/medical-orders", new { patient_id = patient, items = new[] { new { service_id = ct } } }, Guid.NewGuid())).Code);

        // محاسب الفرع يدخل سعر اليوم (الاستقبال لا يستطيع)
        Assert.Equal(403, (await Api.SendAsync(s.Reception, HttpMethod.Put, "/api/v1/exchange-rates", new { usd_to_iqd = 1310 })).Status);
        var (_, accToken) = await app.CreateStaffAsync("accountant", branch);
        var acc = app.Client(accToken);
        var rate = await Api.SendAsync(acc, HttpMethod.Put, "/api/v1/exchange-rates", new { usd_to_iqd = 1310 });
        Assert.True(rate.Status == 200, rate.Raw);
        Assert.Equal(1310, L((await Api.GetAsync(s.Reception, "/api/v1/exchange-rates")).Body!["today"], "usd_to_iqd"));

        // 20$ = 26,200 لكشفية 25,000: الفكة 1,200 بالدينار
        var paid = await Api.PostAsync(s.Reception, "/api/v1/payments", usdBody, Guid.NewGuid());
        Assert.True(paid.Status == 201, paid.Raw);
        Assert.Equal(25000, L(paid.Body, "total_paid"));
        Assert.Equal(1200, L(paid.Body, "change_iqd"));
        var print = await Api.GetAsync(s.Reception, $"/api/v1/receipts/{Id(paid.Body!["receipt"], "receipt_id")}/print-payload");
        var tender = print.Body!["tenders"]![0]!;
        Assert.Equal("USD", S(tender, "currency"));
        Assert.Equal(20, L(tender, "original_amount"));
        Assert.Contains("26,200", S(tender, "display_ar"));
        Assert.Equal(1200, L(print.Body, "change_iqd"));
        // الدفع بالدولار أكثر من الحاجة (الفكة ≥ جزء الدولار) مرفوض
        var order = await Api.PostAsync(s.Reception, "/api/v1/medical-orders", new { patient_id = patient, items = new[] { new { service_id = ct } } }, Guid.NewGuid());
        Assert.True(order.Status == 201, order.Raw);
        var ctItem = order.Body!["items"]![0]!;
        Assert.Equal(26200, L(ctItem, "price"));
        Assert.Equal("USD", await app.ScalarAsync<string>($"SELECT currency FROM invoice_items WHERE invoice_item_id = '{Id(ctItem, "invoice_item_id")}'"));
        Assert.Equal("AMOUNT_MISMATCH", (await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { Id(ctItem, "invoice_item_id") }, tenders = new object[] { new { method = "cash", amount = 26200 }, new { method = "cash_usd", amount = 1 } } }, Guid.NewGuid())).Code);
        // مختلط: 10$ + 13,100 د.ع
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { Id(ctItem, "invoice_item_id") }, tenders = new object[] { new { method = "cash", amount = 13100 }, new { method = "cash_usd", amount = 10 } } }, Guid.NewGuid())).Status);

        // الحساب اليومي: الدينار (13,100 − فكة 1,200) والدولار (30) منفصلان
        var cur = await Api.GetAsync(s.Reception, "/api/v1/shifts/current");
        var sum = cur.Body!["summary"]!;
        Assert.Equal(11900, L(sum, "cash_received"));
        Assert.Equal(30, L(sum, "usd_received"));
        Assert.Equal(30, L(sum, "usd_in_hand"));
        Assert.Equal(1200, L(sum, "change_given_iqd"));
        var shiftId = Id(cur.Body, "shift_id");
        await DailyAccount.PrintAllAsync(s.Reception);
        Assert.Equal(400, (await Api.PostAsync(s.Reception, $"/api/v1/shifts/{shiftId}/close", new { declared = new { cash = 11900 } }, Guid.NewGuid())).Status);
        var closed = await Api.PostAsync(s.Reception, $"/api/v1/shifts/{shiftId}/close", new { declared = new { cash = 11900, usd = 29 } }, Guid.NewGuid());
        Assert.True(closed.Status == 200, closed.Raw);
        var rec = await Api.GetAsync(acc, $"/api/v1/shifts/{shiftId}/reconciliation");
        Assert.Equal("SHORTAGE", S(rec.Body, "audit_status")); // نقص دولار واحد والدينار مطابق
        var usdLine = rec.Body!["lines"]!.AsArray().First(l => S(l, "method") == "cash_usd")!;
        Assert.Equal(30, L(usdLine, "expected"));
        Assert.Equal(-1, L(usdLine, "variance"));
        Assert.Equal(0, L(rec.Body!["lines"]!.AsArray().First(l => S(l, "method") == "cash"), "variance"));
    }
}

/// <summary>الصيدلية بالأسعار (الجلسة 4): التسليم بعد التسعير والدفع. للاختبارات القديمة التي تختبر الصرف فقط: تسعير مجاني (سعر 0) فيُعدّ مدفوعاً.</summary>
public static class PharmacyTest
{
    public static async Task BillFreeAsync(HttpClient pharmacist, Guid prescriptionId, int lines = 1)
    {
        var r = await Api.PostAsync(pharmacist, $"/api/v1/pharmacy/prescriptions/{prescriptionId}/bill",
            new { lines = Enumerable.Range(1, lines).Select(n => new { line_no = (short)n, available = true, quantity = 1, unit_price = 0 }).ToArray() }, Guid.NewGuid());
        if (r.Status != 200) throw new InvalidOperationException("bill: " + r.Raw);
    }
}
