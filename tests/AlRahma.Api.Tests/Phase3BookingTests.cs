using System.Text.Json.Nodes;
using AlRahma.Application.Queue;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>
/// المرحلة 3: تثبيت الحضور الذاتي بـ QR الشاشة، جدول مرضى اليوم في الاستقبال، سياسة الإلغاء (رسوم/غياب/رصيد أو إرجاع)،
/// تغيير الموعد بحدوده، الأحداث المباشرة، ومنع الحجز المكرر والتزامن. كل اختبار في فرع جديد حتى لا تتسرب الإعدادات.
/// </summary>
[Collection(ApiCollection.Name)]
public class Phase3BookingTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();
    private static long L(JsonNode? n, string key) => n![key]!.GetValue<long>();
    private static bool B(JsonNode? n, string key) => n![key]!.GetValue<bool>();
    private static string Phone() => "0776" + Random.Shared.Next(1000000, 9999999);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private sealed record Ctx(ClinicSetup S, Guid Branch);

    private async Task<Ctx> NewBranchAsync(object? settings = null)
    {
        var branch = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO branches (branch_id, code, name, city) VALUES ('{branch}', 'P{branch.ToString()[..5].ToUpperInvariant()}', 'فرع {branch.ToString()[..4]}', 'بغداد')");
        var s = await new QueueTests(app).SetupAsync(branch);
        if (settings is not null)
        {
            var set = await Api.PatchAsync(s.Admin, $"/api/v1/branches/{branch}/settings", new { values = settings });
            Assert.True(set.Status == 200, set.Raw);
        }
        return new Ctx(s, branch);
    }

    private async Task<(Guid Patient, HttpClient Me)> AppPatientAsync(HttpClient reception, string name)
    {
        var phone = Phone();
        var r = await Api.PostAsync(reception, "/api/v1/patients", new { full_name = name, phone_number = phone, gender = "female", approx_age = 30 }, Guid.NewGuid());
        Assert.True(r.Status == 201, r.Raw);
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        var ok = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") });
        Assert.True(ok.Status == 200, ok.Raw);
        return (Id(r.Body, "patient_id"), app.Client(S(ok.Body, "access_token")));
    }

    private async Task<Guid> SessionAsync(ClinicSetup s, DateOnly day, Guid? doctor = null)
    {
        var r = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{doctor ?? s.DoctorId}/sessions?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
        Assert.True(r.Status == 200, r.Raw);
        return Id(r.Body!["items"]![0], "session_id");
    }

    private static async Task<Guid> BookAsync(HttpClient c, Guid patient, Guid session)
    {
        var r = await Api.PostAsync(c, "/api/v1/bookings", new { patient_id = patient, session_id = session, visit_kind = "auto" }, Guid.NewGuid());
        Assert.True(r.Status == 201, r.Raw);
        return Id(r.Body, "booking_id");
    }

    private async Task<(Guid Id, string Key)> DisplayAsync(ClinicSetup s, Guid clinic)
    {
        var d = await Api.PostAsync(s.Admin, "/api/v1/devices", new { kind = "display", name = "شاشة", clinic_id = clinic });
        Assert.True(d.Status == 201, d.Raw);
        return (Id(d.Body, "device_id"), S(d.Body, "display_key"));
    }

    private async Task<ApiResult> TokenAsync(Guid display, string key)
    {
        var c = app.Client();
        c.DefaultRequestHeaders.Add("X-Display-Key", key);
        return await Api.GetAsync(c, $"/api/v1/public/displays/{display}/checkin-token");
    }

    private HubConnection Hub(string? token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/live"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                if (token is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    // ------------------------------------------------------------------ تثبيت الحضور الذاتي

    [SkippableFact]
    public async Task Patient_self_checks_in_by_scanning_the_rotating_display_qr()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, branch) = await NewBranchAsync();
        var (display, key) = await DisplayAsync(s, s.ClinicId);
        var (patient, me) = await AppPatientAsync(s.Reception, "مريضة الرمز");
        var booking = await BookAsync(me, patient, await SessionAsync(s, Today));

        // الشاشة تجلب الرمز بمفتاحها فقط
        var wrong = app.Client();
        wrong.DefaultRequestHeaders.Add("X-Display-Key", "nope");
        Assert.Equal("INVALID_DISPLAY_KEY", (await Api.GetAsync(wrong, $"/api/v1/public/displays/{display}/checkin-token")).Code);
        var tok = await TokenAsync(display, key);
        Assert.True(tok.Status == 200, tok.Raw);
        Assert.True(B(tok.Body, "enabled"));
        var token = S(tok.Body, "token");
        Assert.StartsWith("alrahma://checkin?t=", S(tok.Body, "qr_payload"));
        Assert.Equal("clinic", S(tok.Body, "scope"));
        Assert.InRange(tok.Body!["refresh_after_seconds"]!.GetValue<int>(), 1, 45);

        var url = $"/api/v1/bookings/{booking}/self-check-in";
        Assert.Equal("CHECKIN_TOKEN_INVALID", (await Api.PostAsync(me, url, new { token = "garbage" })).Code);
        Assert.Equal("CHECKIN_TOKEN_INVALID", (await Api.PostAsync(me, url, new { token = token[..^2] + (token[^1] == 'A' ? "BB" : "AA") })).Code);
        Assert.Equal(400, (await Api.PostAsync(me, url, new { })).Status);
        // رمز قديم (صورة أُرسلت لشخص خارج المجمع) ينتهي خلال نافذتين
        using (var scope = app.Server.Services.CreateScope())
        {
            var old = scope.ServiceProvider.GetRequiredService<CheckInTokenService>().Issue(display, DateTimeOffset.UtcNow.AddMinutes(-5)).Token;
            Assert.Equal("CHECKIN_TOKEN_EXPIRED", (await Api.PostAsync(me, url, new { token = old })).Code);
        }
        // شاشة عيادة أخرى في نفس الفرع
        var otherClinic = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO clinics (clinic_id, branch_id, name, department) VALUES ('{otherClinic}', '{branch}', 'عيادة أخرى {otherClinic.ToString()[..4]}', 'clinic')");
        var (otherDisplay, otherKey) = await DisplayAsync(s, otherClinic);
        var wrongClinic = await Api.PostAsync(me, url, new { token = S((await TokenAsync(otherDisplay, otherKey)).Body, "token") });
        Assert.Equal("CHECKIN_WRONG_CLINIC", wrongClinic.Code);
        // حساب مريض آخر لا يثبت حجزاً ليس له
        var (_, stranger) = await AppPatientAsync(s.Reception, "غريب");
        Assert.Equal(404, (await Api.PostAsync(stranger, url, new { token })).Status);
        // الموظف لا يستخدم هذا المسار
        Assert.True((await Api.PostAsync(s.Reception, url, new { token })).Status is 401 or 403);

        // نص الـ QR كاملاً مقبول، والنتيجة نفس تثبيت الاستقبال
        var ok = await Api.PostAsync(me, url, new { token = S(tok.Body, "qr_payload") });
        Assert.True(ok.Status == 200, ok.Raw);
        Assert.Equal("arrived", S(ok.Body, "status"));
        Assert.False(B(ok.Body, "already_checked_in"));
        Assert.Equal(25000, L(ok.Body, "amount_due"));
        Assert.Equal("unpaid", S(ok.Body, "payment_status"));
        Assert.Contains("الاستقبال", S(ok.Body, "message_ar"));
        var ticket = Id(ok.Body, "ticket_id");
        var bk = await Api.GetAsync(me, $"/api/v1/bookings/{booking}");
        Assert.Equal("checked_in", S(bk.Body, "status"));
        Assert.Equal("self_qr", S(bk.Body, "check_in_method"));
        Assert.NotNull(bk.Body!["checked_in_at"]);
        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Assert.Equal("arrived", S(live.Body!["items"]!.AsArray().First(i => Id(i, "ticket_id") == ticket), "status"));

        // التكرار (من التطبيق أو الاستقبال) يعيد نفس النتيجة بفاتورة واحدة
        var again = await Api.PostAsync(me, url, new { token });
        Assert.True(B(again.Body, "already_checked_in"));
        Assert.Equal(ticket, Id(again.Body, "ticket_id"));
        var rec = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { booking_id = booking }, Guid.NewGuid());
        Assert.Equal(201, rec.Status);
        Assert.Equal(ticket, Id(rec.Body, "ticket_id"));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM invoice_items WHERE ticket_id = '{ticket}'"));
        Assert.True(await app.ScalarAsync<bool>($"SELECT created_by IS NULL FROM invoices WHERE invoice_id = '{Id(ok.Body, "invoice_id")}'"));
    }

    [SkippableFact]
    public async Task Self_check_in_without_booking_id_resolves_todays_booking_and_honours_branch_settings()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, branch) = await NewBranchAsync();
        var (display, key) = await DisplayAsync(s, s.ClinicId);
        var (patient, me) = await AppPatientAsync(s.Reception, "مريض بدون اختيار");
        var token = S((await TokenAsync(display, key)).Body, "token");
        Assert.Equal("NO_BOOKING_TODAY", (await Api.PostAsync(me, "/api/v1/me/self-check-in", new { token })).Code);

        // حجز لغير اليوم لا يُثبت
        var future = await BookAsync(me, patient, await SessionAsync(s, Today.AddDays(2)));
        Assert.Equal("BOOKING_NOT_TODAY", (await Api.PostAsync(me, $"/api/v1/bookings/{future}/self-check-in", new { token })).Code);
        await Api.PostAsync(me, $"/api/v1/bookings/{future}/cancel", new { reason = "تبديل" });

        var booking = await BookAsync(me, patient, await SessionAsync(s, Today));
        // الفرع أوقف الحضور الذاتي: الشاشة تخفي الرمز، والرمز السابق يُرفض
        await Api.PatchAsync(s.Admin, $"/api/v1/branches/{branch}/settings", new { values = new Dictionary<string, object> { ["checkin.self_enabled"] = 0 } });
        var off = await TokenAsync(display, key);
        Assert.False(B(off.Body, "enabled"));
        Assert.Null(off.Body!["token"]?.GetValue<string>());
        Assert.Equal("SELF_CHECKIN_DISABLED", (await Api.PostAsync(me, "/api/v1/me/self-check-in", new { token })).Code);
        await Api.PatchAsync(s.Admin, $"/api/v1/branches/{branch}/settings", new { values = new Dictionary<string, object?> { ["checkin.self_enabled"] = null } });

        // نطاق الفرع: شاشة أي عيادة في الفرع تكفي
        var otherClinic = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO clinics (clinic_id, branch_id, name, department) VALUES ('{otherClinic}', '{branch}', 'استعلامات {otherClinic.ToString()[..4]}', 'clinic')");
        var (lobby, lobbyKey) = await DisplayAsync(s, otherClinic);
        var lobbyToken = S((await TokenAsync(lobby, lobbyKey)).Body, "token");
        Assert.Equal("NO_BOOKING_TODAY", (await Api.PostAsync(me, "/api/v1/me/self-check-in", new { token = lobbyToken })).Code);
        await Api.PatchAsync(s.Admin, $"/api/v1/branches/{branch}/settings", new { values = new Dictionary<string, object> { ["checkin.qr_scope"] = "branch" } });
        var ok = await Api.PostAsync(me, "/api/v1/me/self-check-in", new { token = lobbyToken });
        Assert.True(ok.Status == 200, ok.Raw);
        Assert.Equal(booking, Id(ok.Body, "booking_id"));

        // شاشة فرع آخر
        var other = (await NewBranchAsync()).S;
        var (d2, k2) = await DisplayAsync(other, other.ClinicId);
        Assert.Equal("CHECKIN_WRONG_BRANCH",
            (await Api.PostAsync(me, $"/api/v1/bookings/{booking}/self-check-in", new { token = S((await TokenAsync(d2, k2)).Body, "token") })).Code);
    }

    [SkippableFact]
    public async Task Self_check_in_pushes_realtime_events_to_reception_display_and_patient()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, _) = await NewBranchAsync();
        var (display, key) = await DisplayAsync(s, s.ClinicId);
        var (patient, me) = await AppPatientAsync(s.Reception, "مريض مباشر");
        var booking = await BookAsync(me, patient, await SessionAsync(s, Today));
        var patientToken = me.DefaultRequestHeaders.Authorization!.Parameter!;

        var receptionChanged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var staff = Hub(s.ReceptionToken);
        staff.On<Dictionary<string, object?>>("booking.changed", p => { if (p["action"]?.ToString() == "self_checked_in") receptionChanged.TrySetResult(p["booking_id"]!.ToString()!); });
        await staff.StartAsync();
        var displayUpdated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var screen = Hub(null);
        screen.On<Dictionary<string, object?>>("display.updated", _ => displayUpdated.TrySetResult(true));
        await screen.StartAsync();
        Assert.True(await screen.InvokeAsync<bool>("JoinDisplay", display, key));
        var mine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var myQueue = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var phone = Hub(patientToken);
        phone.On<Dictionary<string, object?>>("booking.changed", p => mine.TrySetResult(p["status"]!.ToString()!));
        phone.On<Dictionary<string, object?>>("my_queue.updated", p => { if (p["status"]?.ToString() == "arrived") myQueue.TrySetResult(p["ticket_number"]!.ToString()!); });
        await phone.StartAsync();

        var ok = await Api.PostAsync(me, $"/api/v1/bookings/{booking}/self-check-in", new { token = S((await TokenAsync(display, key)).Body, "token") });
        Assert.True(ok.Status == 200, ok.Raw);
        Assert.Equal(booking.ToString(), await receptionChanged.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await displayUpdated.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("checked_in", await mine.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(S(ok.Body, "ticket_number"), await myQueue.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ------------------------------------------------------------------ جدول مرضى اليوم

    [SkippableFact]
    public async Task Reception_today_lists_bookings_and_walk_ins_with_unified_status_and_actions()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, branch) = await NewBranchAsync();
        var session = await SessionAsync(s, Today);
        var booked = await new QueueTests(app).PatientAsync(s.Reception, "محجوز لم يصل");
        var arrived = await new QueueTests(app).PatientAsync(s.Reception, "محجوز وصل");
        var cancelled = await new QueueTests(app).PatientAsync(s.Reception, "محجوز ألغى");
        var walkIn = await new QueueTests(app).PatientAsync(s.Reception, "بدون حجز");
        var b1 = await BookAsync(s.Reception, booked, session);
        var b2 = await BookAsync(s.Reception, arrived, session);
        var b3 = await BookAsync(s.Reception, cancelled, session);
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { booking_id = b2 }, Guid.NewGuid())).Status);
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/bookings/{b3}/cancel", new { reason = "سفر" })).Status);
        var wi = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = walkIn, doctor_id = s.DoctorId }, Guid.NewGuid());
        await app.ExecAsync($"UPDATE invoice_items SET status = 'paid' WHERE invoice_id = '{wi.Body!["invoice_id"]}'; UPDATE invoices SET status = 'paid', paid_amount = net_amount WHERE invoice_id = '{wi.Body!["invoice_id"]}';");

        var r = await Api.GetAsync(s.Reception, "/api/v1/reception/today");
        Assert.True(r.Status == 200, r.Raw);
        var items = r.Body!["items"]!.AsArray();
        Assert.Equal(4, items.Count);
        Assert.Equal(4, r.Body!["total"]!.GetValue<int>());
        JsonNode Row(Guid patient) => items.First(i => Id(i, "patient_id") == patient)!;
        Assert.Equal("booked", S(Row(booked), "status"));
        Assert.Equal("unpaid", S(Row(booked), "payment_status"));
        Assert.Contains("check_in", Row(booked)["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Contains("reschedule", Row(booked)["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(b1, Id(Row(booked), "booking_id"));
        Assert.Equal("arrived", S(Row(arrived), "status"));
        Assert.Equal("reception", S(Row(arrived), "check_in_method"));
        Assert.Equal(25000, L(Row(arrived), "amount_due"));
        Assert.Contains("collect_payment", Row(arrived)["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.DoesNotContain("check_in", Row(arrived)["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal("cancelled", S(Row(cancelled), "status"));
        Assert.Empty(Row(cancelled)["actions"]!.AsArray());
        Assert.Equal("walk_in", S(Row(walkIn), "source"));
        Assert.Equal("arrived", S(Row(walkIn), "status"));
        Assert.Equal("paid", S(Row(walkIn), "payment_status"));
        Assert.Null(Row(walkIn)["booking_id"]?.GetValue<Guid?>());
        Assert.False(string.IsNullOrEmpty(S(Row(walkIn), "file_number")));
        Assert.Equal(1, r.Body!["summary"]!["booked"]!.GetValue<int>());
        Assert.Equal(2, r.Body!["summary"]!["arrived"]!.GetValue<int>());
        Assert.Equal(1, r.Body!["summary"]!["cancelled"]!.GetValue<int>());

        // الفلاتر
        Assert.Single((await Api.GetAsync(s.Reception, "/api/v1/reception/today?status=booked")).Body!["items"]!.AsArray());
        Assert.Equal(3, (await Api.GetAsync(s.Reception, "/api/v1/reception/today?status=booked,arrived")).Body!["items"]!.AsArray().Count);
        Assert.Single((await Api.GetAsync(s.Reception, "/api/v1/reception/today?q=" + Uri.EscapeDataString("بدون حجز"))).Body!["items"]!.AsArray());
        Assert.Single((await Api.GetAsync(s.Reception, "/api/v1/reception/today?payment_status=paid")).Body!["items"]!.AsArray());
        Assert.Equal(4, (await Api.GetAsync(s.Reception, $"/api/v1/reception/today?doctor_id={s.DoctorId}&date={Today:yyyy-MM-dd}")).Body!["items"]!.AsArray().Count);
        Assert.Empty((await Api.GetAsync(s.Reception, $"/api/v1/reception/today?date={Today.AddDays(1):yyyy-MM-dd}")).Body!["items"]!.AsArray());
        Assert.Equal(400, (await Api.GetAsync(s.Reception, "/api/v1/reception/today?status=weird")).Status);

        // نطاق الموظف: استقبال عيادة أخرى في نفس الفرع لا يرى شيئاً، وفرع آخر كذلك، والمحاسب بلا queue.manage ممنوع
        var sideClinic = await OtherClinicAsync(branch);
        var (_, otherRec) = await app.CreateStaffAsync("receptionist", branch, clinics: [sideClinic]);
        Assert.Empty((await Api.GetAsync(app.Client(otherRec), "/api/v1/reception/today")).Body!["items"]!.AsArray());
        var (_, b1Rec) = await app.CreateStaffAsync("receptionist");
        Assert.DoesNotContain((await Api.GetAsync(app.Client(b1Rec), "/api/v1/reception/today")).Body!["items"]!.AsArray(), i => Id(i, "patient_id") == booked);
        var (_, accountant) = await app.CreateStaffAsync("accountant", branch);
        Assert.Equal(403, (await Api.GetAsync(app.Client(accountant), "/api/v1/reception/today")).Status);
        // مدير الفرع يرى الفرع كاملاً
        var (_, manager) = await app.CreateStaffAsync("branch_manager", branch);
        Assert.Equal(4, (await Api.GetAsync(app.Client(manager), "/api/v1/reception/today")).Body!["items"]!.AsArray().Count);
    }

    private async Task<Guid> OtherClinicAsync(Guid branch)
    {
        var id = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO clinics (clinic_id, branch_id, name, department) VALUES ('{id}', '{branch}', 'عيادة جانبية {id.ToString()[..4]}', 'clinic')");
        return id;
    }

    // ------------------------------------------------------------------ سياسة الإلغاء

    [SkippableFact]
    public async Task Late_cancellation_adds_fee_to_basket_and_free_cancellation_does_not()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, branch) = await NewBranchAsync(new Dictionary<string, object>
        {
            ["booking.late_cancel_fee"] = 5000, ["booking.late_cancel_counts_no_show"] = 0, ["booking.cancel_min_hours"] = 3,
        });
        var settings = (await Api.GetAsync(s.Admin, $"/api/v1/branches/{branch}/settings")).Body!["items"]!.AsArray();
        Assert.Equal(5000, L(settings.First(x => S(x, "key") == "booking.late_cancel_fee"), "value"));
        Assert.Equal("credit", S(settings.First(x => S(x, "key") == "booking.cancel_paid_to"), "value_text"));
        Assert.Equal(2, L(settings.First(x => S(x, "key") == "booking.max_reschedules"), "value"));

        var (patient, me) = await AppPatientAsync(s.Reception, "مريض إلغاء متأخر");
        var today = await BookAsync(me, patient, await SessionAsync(s, Today));
        var preview = await Api.GetAsync(me, $"/api/v1/bookings/{today}/cancel-preview");
        Assert.True(preview.Status == 200, preview.Raw);
        Assert.True(B(preview.Body, "allowed"));
        Assert.True(B(preview.Body, "is_late"));
        Assert.Equal(5000, L(preview.Body, "fee_amount"));
        Assert.False(B(preview.Body, "counts_as_no_show"));
        Assert.False(B(preview.Body, "can_waive"));
        Assert.Equal(3, L(preview.Body, "free_cancel_hours"));
        Assert.Contains("5,000", S(preview.Body, "summary_ar"));
        Assert.NotEmpty(preview.Body!["conditions_ar"]!.AsArray());
        // المريض لا يعفي نفسه
        Assert.Equal("POLICY_OVERRIDE_FORBIDDEN", (await Api.PostAsync(me, $"/api/v1/bookings/{today}/cancel", new { reason = "x", waive_fee = true, waive_reason = "y" })).Code);

        var cancel = await Api.PostAsync(me, $"/api/v1/bookings/{today}/cancel", new { reason = "ظرف طارئ" });
        Assert.True(cancel.Status == 200, cancel.Raw);
        Assert.True(B(cancel.Body, "late"));
        Assert.False(B(cancel.Body, "counted_as_no_show"));
        Assert.Equal(5000, L(cancel.Body, "fee_amount"));
        Assert.Equal("none", S(cancel.Body, "paid_amount_to"));
        var feeItem = Id(cancel.Body, "fee_invoice_item_id");
        Assert.Equal(0L, await app.ScalarAsync<long>($"SELECT count(*) FROM patient_no_shows WHERE patient_id = '{patient}'"));
        var basket = await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}/billable-items");
        Assert.True(basket.Status == 200, basket.Raw);
        var fee = basket.Body!["items"]!.AsArray().Single(i => Id(i, "invoice_item_id") == feeItem)!;
        Assert.Equal("رسوم إلغاء متأخر", S(fee, "service_name"));
        Assert.Equal("cancellation_fee", S(fee, "item_type"));
        Assert.Equal(5000, L(fee, "price"));
        Assert.Equal("unpaid", S(fee, "status"));
        Assert.Equal("patient", S((await Api.GetAsync(me, $"/api/v1/bookings/{today}")).Body, "cancelled_by"));
        // خدمة الرسوم الداخلية لا تظهر في الكتالوج
        Assert.DoesNotContain("\"fee\"", (await Api.GetAsync(s.Admin, "/api/v1/services?include_inactive=true")).Raw);
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM services WHERE branch_id = '{branch}' AND type = 'fee'"));

        // قبل الموعد بمدة كافية: مجاني
        var later = await BookAsync(me, patient, await SessionAsync(s, Today.AddDays(2)));
        var free = await Api.GetAsync(me, $"/api/v1/bookings/{later}/cancel-preview");
        Assert.False(B(free.Body, "is_late"));
        Assert.Equal(0, L(free.Body, "fee_amount"));
        var freeCancel = await Api.PostAsync(me, $"/api/v1/bookings/{later}/cancel", new { reason = "تغيير خطة" });
        Assert.False(B(freeCancel.Body, "late"));
        Assert.Equal(0, L(freeCancel.Body, "fee_amount"));
        Assert.Null(freeCancel.Body!["fee_invoice_item_id"]?.GetValue<Guid?>());
        // الإلغاء الثاني مرفوض
        Assert.Equal("BOOKING_NOT_CANCELLABLE", (await Api.PostAsync(me, $"/api/v1/bookings/{later}/cancel", new { reason = "x" })).Code);
        Assert.False(B((await Api.GetAsync(me, $"/api/v1/bookings/{later}/cancel-preview")).Body, "allowed"));
    }

    [SkippableFact]
    public async Task Staff_can_waive_late_cancellation_only_with_permission_and_reason()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, branch) = await NewBranchAsync(new Dictionary<string, object> { ["booking.late_cancel_fee"] = 7000 });
        var patient = await new QueueTests(app).PatientAsync(s.Reception, "إعفاء");
        var booking = await BookAsync(s.Reception, patient, await SessionAsync(s, Today));
        Assert.Equal("POLICY_OVERRIDE_FORBIDDEN",
            (await Api.PostAsync(s.Reception, $"/api/v1/bookings/{booking}/cancel", new { reason = "اتصل", waive_fee = true, waive_reason = "مرض" })).Code);
        // الصلاحية لا تُضاف تلقائياً لمدير الفرع: تُمنح صراحة
        var (_, plainToken) = await app.CreateStaffAsync("branch_manager", branch);
        Assert.False(B((await Api.GetAsync(app.Client(plainToken), $"/api/v1/bookings/{booking}/cancel-preview")).Body, "can_waive"));
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager", branch, extraPermissions: ["bookings.policy_override"]);
        var manager = app.Client(managerToken);
        Assert.True(B((await Api.GetAsync(manager, $"/api/v1/bookings/{booking}/cancel-preview")).Body, "can_waive"));
        Assert.Equal(400, (await Api.PostAsync(manager, $"/api/v1/bookings/{booking}/cancel", new { reason = "اتصل", waive_fee = true })).Status);
        var ok = await Api.PostAsync(manager, $"/api/v1/bookings/{booking}/cancel", new { reason = "اتصل المريض", waive_fee = true, waive_reason = "دخل المستشفى" });
        Assert.True(ok.Status == 200, ok.Raw);
        Assert.True(B(ok.Body, "fee_waived"));
        Assert.Equal(0, L(ok.Body, "fee_amount"));
        Assert.False(B(ok.Body, "counted_as_no_show"));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'BOOKING_POLICY_WAIVED' AND entity_id = '{booking}' AND reason = 'دخل المستشفى'"));
        Assert.Equal("staff", await app.ScalarAsync<string>($"SELECT cancelled_by FROM bookings WHERE booking_id = '{booking}'"));

        // بدون إعفاء: السلوك السابق (الإلغاء المتأخر يُحتسب غياباً افتراضياً) + الرسوم
        var other = await new QueueTests(app).PatientAsync(s.Reception, "بدون إعفاء");
        var b2 = await BookAsync(s.Reception, other, await SessionAsync(s, Today));
        var late = await Api.PostAsync(s.Reception, $"/api/v1/bookings/{b2}/cancel", new { reason = "لم يرد" });
        Assert.True(B(late.Body, "counted_as_no_show"));
        Assert.Equal(7000, L(late.Body, "fee_amount"));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM patient_no_shows WHERE patient_id = '{other}'"));

        // الصلاحية في الكتالوج، ولا تُزرع لأي دور نظام
        var catalog = await Api.GetAsync(s.Admin, "/api/v1/permissions/catalog");
        Assert.Contains("bookings.policy_override", catalog.Raw);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM role_permissions WHERE permission = 'bookings.policy_override'"));
    }

    [SkippableFact]
    public async Task Paid_booking_cancel_goes_to_credit_or_refund_by_branch_policy()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, branch) = await NewBranchAsync();
        var (patient, me) = await AppPatientAsync(s.Reception, "دافع أونلاين");

        async Task<Guid> PaidBookingAsync(DateOnly day)
        {
            var booking = await BookAsync(me, patient, await SessionAsync(s, day));
            var op = await Api.PostAsync(me, "/api/v1/online-payments", new { purpose = "booking_full", patient_id = patient, booking_id = booking, provider = "sandbox" }, Guid.NewGuid());
            Assert.True(op.Status == 201, op.Raw);
            var done = await Api.PostAsync(me, $"/api/v1/sandbox/pay/{Id(op.Body, "online_payment_id")}", new { result = "succeeded" });
            Assert.True(done.Status == 200, done.Raw);
            return booking;
        }

        var credit = await PaidBookingAsync(Today.AddDays(2));
        var pc = await Api.GetAsync(me, $"/api/v1/bookings/{credit}/cancel-preview");
        Assert.Equal("credit", S(pc.Body, "paid_amount_to"));
        Assert.Equal(25000, L(pc.Body, "credit_amount"));
        var c1 = await Api.PostAsync(me, $"/api/v1/bookings/{credit}/cancel", new { reason = "x" });
        Assert.Equal(25000, L(c1.Body, "credit_added"));
        Assert.Equal("credit", S(c1.Body, "paid_amount_to"));
        Assert.Equal("credited", S((await Api.GetAsync(me, $"/api/v1/bookings/{credit}")).Body, "payment_status"));

        await Api.PatchAsync(s.Admin, $"/api/v1/branches/{branch}/settings", new { values = new Dictionary<string, object> { ["booking.cancel_paid_to"] = "refund" } });
        var refund = await PaidBookingAsync(Today.AddDays(3));
        var pr = await Api.GetAsync(me, $"/api/v1/bookings/{refund}/cancel-preview");
        Assert.Equal("refund", S(pr.Body, "paid_amount_to"));
        Assert.Equal(25000, L(pr.Body, "refund_amount"));
        var c2 = await Api.PostAsync(me, $"/api/v1/bookings/{refund}/cancel", new { reason = "x" });
        Assert.True(c2.Status == 200, c2.Raw);
        Assert.Equal(0, L(c2.Body, "credit_added"));
        Assert.Equal(25000, L(c2.Body, "refund_amount"));
        Assert.Equal("refunded", S((await Api.GetAsync(me, $"/api/v1/bookings/{refund}")).Body, "payment_status"));
        Assert.Equal("refunded", await app.ScalarAsync<string>($"SELECT status FROM online_payments WHERE booking_id = '{refund}'"));
        Assert.Equal(25000L, await app.ScalarAsync<long>($"SELECT COALESCE(sum(amount), 0)::bigint FROM patient_credit_ledger WHERE patient_id = '{patient}' AND kind = 'booking_cancel_credit'"));
    }

    // ------------------------------------------------------------------ تغيير الموعد

    [SkippableFact]
    public async Task Patient_reschedules_within_policy_keeping_payment_and_booking_id()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, branch) = await NewBranchAsync(new Dictionary<string, object> { ["booking.max_reschedules"] = 1 });
        var (patient, me) = await AppPatientAsync(s.Reception, "مغيّر الموعد");
        var d2 = await SessionAsync(s, Today.AddDays(2));
        var d3 = await SessionAsync(s, Today.AddDays(3));
        var d4 = await SessionAsync(s, Today.AddDays(4));
        var booking = await BookAsync(me, patient, d2);
        var op = await Api.PostAsync(me, "/api/v1/online-payments", new { purpose = "booking_full", patient_id = patient, booking_id = booking, provider = "sandbox" }, Guid.NewGuid());
        await Api.PostAsync(me, $"/api/v1/sandbox/pay/{Id(op.Body, "online_payment_id")}", new { result = "succeeded" });

        var policyOnly = await Api.GetAsync(me, $"/api/v1/bookings/{booking}/reschedule-preview");
        Assert.True(policyOnly.Status == 200, policyOnly.Raw);
        Assert.True(B(policyOnly.Body, "allowed"));
        Assert.Equal(1, L(policyOnly.Body, "remaining_reschedules"));
        Assert.Null(policyOnly.Body!["target"]?.GetValue<object>());
        var preview = await Api.GetAsync(me, $"/api/v1/bookings/{booking}/reschedule-preview?session_id={d3}");
        Assert.True(B(preview.Body, "allowed"));
        Assert.Equal(0, L(preview.Body, "remaining_after"));
        Assert.Equal("kept", S(preview.Body, "payment_effect"));
        Assert.Equal(25000, L(preview.Body, "paid_amount"));
        Assert.Equal(Today.AddDays(3).ToString("yyyy-MM-dd"), S(preview.Body!["target"], "date"));
        Assert.Contains(preview.Body!["conditions_ar"]!.AsArray(), c => c!.GetValue<string>().Contains("نفس الطبيب"));

        var moved = await Api.PostAsync(me, $"/api/v1/bookings/{booking}/reschedule", new { session_id = d3 });
        Assert.True(moved.Status == 200, moved.Raw);
        Assert.Equal(booking, Id(moved.Body, "booking_id"));
        Assert.Equal(d3, Id(moved.Body, "session_id"));
        Assert.Equal(1, moved.Body!["reschedule_count"]!.GetValue<int>());
        Assert.Equal("paid", S(moved.Body, "payment_status"));
        Assert.Equal("booked", S(moved.Body, "status"));
        Assert.Equal(d3, await app.ScalarAsync<Guid>($"SELECT session_id FROM queue_tickets WHERE booking_id = '{booking}'"));
        Assert.Equal(d2, await app.ScalarAsync<Guid>($"SELECT original_session_id FROM bookings WHERE booking_id = '{booking}'"));
        Assert.Empty((await Api.GetAsync(s.Reception, $"/api/v1/bookings?session_id={d2}")).Body!["items"]!.AsArray());
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'BOOKING_RESCHEDULED' AND entity_id = '{booking}'"));

        // الحد: مرة واحدة في هذا الفرع
        var limit = await Api.PostAsync(me, $"/api/v1/bookings/{booking}/reschedule", new { session_id = d4 });
        Assert.Equal("RESCHEDULE_LIMIT_REACHED", limit.Code);
        Assert.False(B((await Api.GetAsync(me, $"/api/v1/bookings/{booking}/reschedule-preview?session_id={d4}")).Body, "allowed"));
        Assert.Equal("POLICY_OVERRIDE_FORBIDDEN", (await Api.PostAsync(s.Reception, $"/api/v1/bookings/{booking}/reschedule",
            new { session_id = d4, override_policy = true, override_reason = "طلب" })).Code);
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager", branch, extraPermissions: ["bookings.policy_override"]);
        var manager = app.Client(managerToken);
        Assert.True(B((await Api.GetAsync(manager, $"/api/v1/bookings/{booking}/reschedule-preview?session_id={d4}")).Body, "can_override"));
        var forced = await Api.PostAsync(manager, $"/api/v1/bookings/{booking}/reschedule", new { session_id = d4, override_policy = true, override_reason = "ظرف طبي" });
        Assert.True(forced.Status == 200, forced.Raw);
        Assert.Equal(2, forced.Body!["reschedule_count"]!.GetValue<int>());
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'BOOKING_POLICY_OVERRIDDEN' AND entity_id = '{booking}'"));

        // طبيب آخر، نفس الوجبة، وجبة غير موجودة
        var other = await new QueueTests(app).SetupAsync(branch);
        await Api.PatchAsync(s.Admin, $"/api/v1/branches/{branch}/settings", new { values = new Dictionary<string, object> { ["booking.max_reschedules"] = 5 } });
        Assert.Equal("RESCHEDULE_DIFFERENT_DOCTOR", (await Api.PostAsync(me, $"/api/v1/bookings/{booking}/reschedule",
            new { session_id = await SessionAsync(other, Today.AddDays(3)) })).Code);
        Assert.Equal("SAME_SESSION", (await Api.PostAsync(me, $"/api/v1/bookings/{booking}/reschedule", new { session_id = d4 })).Code);
        Assert.Equal(404, (await Api.PostAsync(me, $"/api/v1/bookings/{booking}/reschedule", new { session_id = Guid.NewGuid() })).Status);
        Assert.Equal(400, (await Api.PostAsync(me, $"/api/v1/bookings/{booking}/reschedule", new { })).Status);
    }

    [SkippableFact]
    public async Task Reschedule_respects_notice_duplicates_and_capacity()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, branch) = await NewBranchAsync();
        var today = await SessionAsync(s, Today);
        var d2 = await SessionAsync(s, Today.AddDays(2));
        var d3 = await SessionAsync(s, Today.AddDays(3));
        var patient = await new QueueTests(app).PatientAsync(s.Reception, "مهلة");

        // موعد اليوم (خلال ساعتين): فات وقت التغيير
        var now = await BookAsync(s.Reception, patient, today);
        Assert.Equal("RESCHEDULE_TOO_LATE", (await Api.PostAsync(s.Reception, $"/api/v1/bookings/{now}/reschedule", new { session_id = d2 })).Code);
        var pv = await Api.GetAsync(s.Reception, $"/api/v1/bookings/{now}/reschedule-preview?session_id={d2}");
        Assert.Equal("RESCHEDULE_TOO_LATE", S(pv.Body, "blocked_code"));
        Assert.False(B(pv.Body, "can_override"));
        await Api.PostAsync(s.Reception, $"/api/v1/bookings/{now}/cancel", new { reason = "x" });

        // المريض في طابور اليوم (حضور مباشر) لا يُنقل حجزه لنفس الوجبة
        var future = await BookAsync(s.Reception, patient, d2);
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid())).Status);
        Assert.Equal("DUPLICATE_BOOKING", (await Api.PostAsync(s.Reception, $"/api/v1/bookings/{future}/reschedule", new { session_id = today })).Code);

        // الحصة ممتلئة
        await app.ExecAsync($"UPDATE doctor_sessions SET online_quota = (SELECT count(*) FROM bookings WHERE session_id = '{d3}' AND status <> 'cancelled') WHERE session_id = '{d3}'");
        Assert.Equal("SESSION_FULL", (await Api.PostAsync(s.Reception, $"/api/v1/bookings/{future}/reschedule", new { session_id = d3 })).Code);
        Assert.Equal("SESSION_FULL", S((await Api.GetAsync(s.Reception, $"/api/v1/bookings/{future}/reschedule-preview?session_id={d3}")).Body, "blocked_code"));

        // عزل الفروع: موظف فرع آخر لا يرى الحجز
        var (_, b1Rec) = await app.CreateStaffAsync("receptionist");
        Assert.Equal(404, (await Api.GetAsync(app.Client(b1Rec), $"/api/v1/bookings/{future}/reschedule-preview")).Status);
        Assert.Equal(404, (await Api.PostAsync(app.Client(b1Rec), $"/api/v1/bookings/{future}/reschedule", new { session_id = d3 })).Status);
        Assert.Equal(404, (await Api.GetAsync(app.Client(b1Rec), $"/api/v1/bookings/{future}/cancel-preview")).Status);
        _ = branch;
    }

    // ------------------------------------------------------------------ التزامن

    [SkippableFact]
    public async Task Concurrent_reschedules_into_last_seat_admit_only_one()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, _) = await NewBranchAsync();
        var d2 = await SessionAsync(s, Today.AddDays(2));
        var d3 = await SessionAsync(s, Today.AddDays(3));
        var bookings = new List<Guid>();
        for (var i = 0; i < 4; i++) bookings.Add(await BookAsync(s.Reception, await new QueueTests(app).PatientAsync(s.Reception, $"متزامن {i}"), d2));
        // مقعد واحد متبقٍ في الوجبة الهدف
        await app.ExecAsync($"UPDATE doctor_sessions SET online_quota = 1 WHERE session_id = '{d3}'");

        var results = await Task.WhenAll(bookings.Select(b => Api.PostAsync(s.Reception, $"/api/v1/bookings/{b}/reschedule", new { session_id = d3 })));
        Assert.Equal(1, results.Count(r => r.Status == 200));
        Assert.All(results.Where(r => r.Status != 200), r => Assert.Equal("SESSION_FULL", r.Code));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM bookings WHERE session_id = '{d3}' AND status <> 'cancelled'"));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM queue_tickets WHERE session_id = '{d3}'"));
        Assert.Equal(3L, await app.ScalarAsync<long>($"SELECT count(*) FROM bookings WHERE session_id = '{d2}' AND status <> 'cancelled'"));
    }

    [SkippableFact]
    public async Task Concurrent_check_ins_and_reschedule_of_the_same_booking_stay_consistent()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, branch) = await NewBranchAsync();
        var (display, key) = await DisplayAsync(s, s.ClinicId);
        var today = await SessionAsync(s, Today);
        var d2 = await SessionAsync(s, Today.AddDays(2));

        // مسح التطبيق والاستقبال معاً: تذكرة واحدة وفاتورة واحدة
        var (patient, me) = await AppPatientAsync(s.Reception, "مسح مزدوج");
        var booking = await BookAsync(me, patient, today);
        var token = S((await TokenAsync(display, key)).Body, "token");
        var both = await Task.WhenAll(
            Api.PostAsync(me, $"/api/v1/bookings/{booking}/self-check-in", new { token }),
            Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { booking_id = booking }, Guid.NewGuid()),
            Api.PostAsync(me, $"/api/v1/bookings/{booking}/self-check-in", new { token }));
        Assert.All(both, r => Assert.True(r.Status is 200 or 201, r.Raw));
        Assert.Single(both.Select(r => Id(r.Body, "ticket_id")).Distinct());
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM invoice_items i JOIN queue_tickets t ON t.ticket_id = i.ticket_id WHERE t.booking_id = '{booking}'"));

        // تغيير موعد (بتجاوز المهلة من المدير) وتثبيت حضور متزامنان: أحدهما فقط ينجح، والحالة متسقة
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager", branch, extraPermissions: ["bookings.policy_override"]);
        var manager = app.Client(managerToken);
        for (var round = 0; round < 3; round++)
        {
            var p = await new QueueTests(app).PatientAsync(s.Reception, $"سباق {round}");
            var b = await BookAsync(s.Reception, p, today);
            var race = await Task.WhenAll(
                Api.PostAsync(manager, $"/api/v1/bookings/{b}/reschedule", new { session_id = d2, override_policy = true, override_reason = "سباق" }),
                Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { booking_id = b }, Guid.NewGuid()));
            Assert.All(race, r => Assert.True(r.Status < 500, r.Raw));
            Assert.Equal(1, race.Count(r => r.Status is 200 or 201));
            var session = await app.ScalarAsync<Guid>($"SELECT session_id FROM bookings WHERE booking_id = '{b}'");
            var status = await app.ScalarAsync<string>($"SELECT status FROM bookings WHERE booking_id = '{b}'");
            var invoices = await app.ScalarAsync<long>($"SELECT count(*) FROM invoice_items i JOIN queue_tickets t ON t.ticket_id = i.ticket_id WHERE t.booking_id = '{b}'");
            if (status == "checked_in") { Assert.Equal(today, session); Assert.Equal(1L, invoices); }
            else { Assert.Equal("booked", status); Assert.Equal(d2, session); Assert.Equal(0L, invoices); }
            Assert.Equal(session, await app.ScalarAsync<Guid>($"SELECT session_id FROM queue_tickets WHERE booking_id = '{b}'"));
        }
    }
}
