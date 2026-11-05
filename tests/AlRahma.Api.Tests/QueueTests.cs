using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>عيادة جديدة بطبيب دوامه طوال اليوم وكشفيتين، ليكون الاختبار مستقلاً عن الوقت والبيانات الأخرى.</summary>
public sealed record ClinicSetup(Guid ClinicId, Guid DoctorId, string DoctorToken, string ReceptionToken, HttpClient Admin, HttpClient Reception, HttpClient Doctor);

[Collection(ApiCollection.Name)]
public class QueueTests(TestApp app)
{
    internal async Task<ClinicSetup> SetupAsync(Guid? branch = null, string[]? receptionPermissions = null)
    {
        var branchId = branch ?? app.Branch1;
        var admin = app.Client(await app.AdminTokenAsync());
        if (branchId != app.Branch1)
        {
            var login = await Api.PostAsync(app.Client(), "/api/v1/auth/login",
                new { username = TestApp.AdminUsername, password = TestApp.AdminPassword, branch_id = branchId });
            Assert.Equal(200, login.Status);
            admin = app.Client(login.Body!["access_token"]!.GetValue<string>());
        }
        var clinic = Guid.NewGuid();
        await app.ExecAsync($"""
            INSERT INTO clinics (clinic_id, branch_id, name, department) VALUES ('{clinic}', '{branchId}', 'عيادة {clinic.ToString()[..4]}', 'clinic');
            INSERT INTO services (branch_id, clinic_id, name, type, visit_kind, price, is_commissionable) VALUES
              ('{branchId}', '{clinic}', 'كشفية جديد', 'consultation', 'new_visit', 25000, true),
              ('{branchId}', '{clinic}', 'كشفية مراجعة', 'consultation', 'follow_up', 15000, true);
            """);
        var (doctorUser, doctorToken) = await app.CreateStaffAsync("doctor", branchId, doctorProfile: new { specialty = "باطنية", clinic_id = clinic });
        var doctorId = await app.ScalarAsync<Guid>($"SELECT doctor_id FROM doctors WHERE user_id = '{doctorUser}'");
        var put = await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/schedule", new
        {
            weekly = new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }
                .Select(d => new { weekday = d, start = "00:00", end = "23:59", slot_minutes = 10, online_quota_percent = 60 }).ToArray(),
        });
        Assert.Equal(200, put.Status);
        await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/commissions", new { new_visit_rate = 70, follow_up_rate = 60, effective_from = "2026-01-01" });
        var (_, rec) = await app.CreateStaffAsync("receptionist", branchId, clinics: [clinic], extraPermissions: receptionPermissions);
        return new ClinicSetup(clinic, doctorId, doctorToken, rec, admin, app.Client(rec), app.Client(doctorToken));
    }

    internal async Task<Guid> PatientAsync(HttpClient c, string name = "مريض اختبار") =>
        (await Api.PostAsync(c, "/api/v1/patients", new { full_name = name, phone_number = "0771" + Random.Shared.Next(1000000, 9999999) }, Guid.NewGuid()))
        .Body!["patient_id"]!.GetValue<Guid>();

    private Task MarkPaidAsync(JsonNode checkIn) => app.ExecAsync($"""
        UPDATE invoice_items SET status = 'paid' WHERE invoice_id = '{checkIn["invoice_id"]}';
        UPDATE invoices SET status = 'paid', paid_amount = net_amount WHERE invoice_id = '{checkIn["invoice_id"]}';
        """);

    private async Task<Guid> TodaySessionAsync(ClinicSetup s)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        return sessions.Body!["items"]![0]!["session_id"]!.GetValue<Guid>();
    }

    [SkippableFact]
    public async Task Walk_in_pays_before_entering_and_doctor_completes_visit()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var patient = await PatientAsync(s.Reception);

        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.Equal(201, checkIn.Status);
        Assert.Equal("arrived", checkIn.Body!["status"]!.GetValue<string>());
        Assert.Equal("A-001", checkIn.Body!["ticket_number"]!.GetValue<string>());
        Assert.Equal("new_visit", checkIn.Body!["visit_kind"]!.GetValue<string>());
        Assert.Equal(25000, checkIn.Body!["amount_due"]!.GetValue<long>());
        Assert.Equal("unpaid", checkIn.Body!["payment_status"]!.GetValue<string>());
        var ticket = checkIn.Body!["ticket_id"]!.GetValue<Guid>();

        // لقطة السعر ونسبة الطبيب في بند الفاتورة
        Assert.Equal(17500L, await app.ScalarAsync<long>($"SELECT doctor_share FROM invoice_items WHERE ticket_id = '{ticket}'"));
        Assert.Matches(@"^B1-INV-\d{6}$", await app.ScalarAsync<string>($"SELECT invoice_number FROM invoices WHERE invoice_id = '{checkIn.Body!["invoice_id"]}'"));

        var unpaid = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call");
        Assert.Equal(402, unpaid.Status);
        Assert.Equal("INVOICE_UNPAID", unpaid.Code);

        await MarkPaidAsync(checkIn.Body!);
        var call = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call");
        Assert.Equal(200, call.Status);
        Assert.Equal("called", call.Body!["status"]!.GetValue<string>());

        // البدء والإنهاء للطبيب فقط
        Assert.Equal(403, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/start")).Status);
        var start = await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/start");
        Assert.Equal("in_room", start.Body!["status"]!.GetValue<string>());
        var done = await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/complete");
        Assert.Equal("completed", done.Body!["status"]!.GetValue<string>());

        // زيارة ثانية خلال فترة المراجعة = مراجعة بسعر المراجعة
        var again = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.Equal("follow_up", again.Body!["visit_kind"]!.GetValue<string>());
        Assert.Equal(15000, again.Body!["amount_due"]!.GetValue<long>());

        var dup = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.Equal("ALREADY_IN_QUEUE", dup.Code);

        // طبيب آخر لا يدير هذا الطابور، وموظف استقبال عيادة أخرى كذلك
        var (_, otherDoctor) = await app.CreateStaffAsync("doctor", doctorProfile: new { specialty = "أطفال", clinic_id = app.ClinicPediatrics });
        Assert.Equal(403, (await Api.PostAsync(app.Client(otherDoctor), $"/api/v1/queue/tickets/{again.Body!["ticket_id"]}/call")).Status);
        var (_, otherRec) = await app.CreateStaffAsync("receptionist");
        Assert.Equal(403, (await Api.PostAsync(app.Client(otherRec), "/api/v1/queue/check-in",
            new { patient_id = await PatientAsync(s.Admin), doctor_id = s.DoctorId }, Guid.NewGuid())).Status);

        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Assert.Equal(200, live.Status);
        var items = live.Body!["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal("arrived", items[0]!["status"]!.GetValue<string>());
        Assert.Equal("completed", items[1]!["status"]!.GetValue<string>());
        Assert.Equal("paid", items[1]!["payment_status"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task Phone_booking_then_check_in_and_availability()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var session = await TodaySessionAsync(s);
        var patient = await PatientAsync(s.Reception);

        var avail = await Api.GetAsync(app.Client(), $"/api/v1/public/doctors/{s.DoctorId}/availability");
        Assert.Equal(200, avail.Status);
        var today = avail.Body!["days"]![0]!;
        var remaining = today["online_remaining"]!.GetValue<int>();
        Assert.Equal("open", today["status"]!.GetValue<string>());

        var booking = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = session, visit_kind = "auto" }, Guid.NewGuid());
        Assert.Equal(201, booking.Status);
        Assert.Equal("booked", booking.Body!["status"]!.GetValue<string>());
        Assert.Equal(25000, booking.Body!["price"]!.GetValue<long>());
        Assert.Equal("phone", booking.Body!["source"]!.GetValue<string>());
        Assert.NotNull(booking.Body!["ticket_number"]);
        var bookingId = booking.Body!["booking_id"]!.GetValue<Guid>();

        var after = await Api.GetAsync(app.Client(), $"/api/v1/public/doctors/{s.DoctorId}/availability");
        Assert.Equal(remaining - 1, after.Body!["days"]![0]!["online_remaining"]!.GetValue<int>());

        var dup = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = session }, Guid.NewGuid());
        Assert.Equal("DUPLICATE_BOOKING", dup.Code);

        // قبل الوصول: يظهر في الطابور بانتظار الوصول ولا يمكن استدعاؤه
        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        var item = live.Body!["items"]!.AsArray().Single(i => i!["booking_id"]?.GetValue<Guid>() == bookingId)!;
        Assert.Equal("waiting", item["status"]!.GetValue<string>());
        Assert.Equal("not_billed", item["payment_status"]!.GetValue<string>());

        // تثبيت الحضور بمسح QR
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { booking_qr = $"booking:{bookingId}" }, Guid.NewGuid());
        Assert.Equal(201, checkIn.Status);
        Assert.Equal("arrived", checkIn.Body!["status"]!.GetValue<string>());
        Assert.Equal(25000, checkIn.Body!["amount_due"]!.GetValue<long>());
        var b = await Api.GetAsync(s.Reception, $"/api/v1/bookings/{bookingId}");
        Assert.Equal("checked_in", b.Body!["status"]!.GetValue<string>());

        // الحجز المدفوع مسبقاً لا يُطالب بشيء عند الوصول
        var p2 = await PatientAsync(s.Reception);
        var paidBooking = (await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = p2, session_id = session }, Guid.NewGuid()))
            .Body!["booking_id"]!.GetValue<Guid>();
        await app.ExecAsync($"UPDATE bookings SET payment_status = 'paid' WHERE booking_id = '{paidBooking}'");
        var paidIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { booking_id = paidBooking }, Guid.NewGuid());
        Assert.Equal(0, paidIn.Body!["amount_due"]!.GetValue<long>());
        Assert.Equal("paid", paidIn.Body!["payment_status"]!.GetValue<string>());

        var doctors = await Api.GetAsync(app.Client(), $"/api/v1/public/branches/{app.Branch1}/doctors");
        var doc = doctors.Body!["items"]!.AsArray().Single(d => d!["doctor_id"]!.GetValue<Guid>() == s.DoctorId)!;
        Assert.Equal(25000, doc["new_visit_price"]!.GetValue<long>());
        Assert.Equal(15000, doc["follow_up_price"]!.GetValue<long>());
    }

    [SkippableFact]
    public async Task Cancel_booking_credit_and_late_cancel_counts_as_no_show()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).AddDays(2);
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={tomorrow:yyyy-MM-dd}&to={tomorrow:yyyy-MM-dd}");
        var future = sessions.Body!["items"]![0]!["session_id"]!.GetValue<Guid>();
        var patient = await PatientAsync(s.Reception);

        var bk = (await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = future }, Guid.NewGuid()))
            .Body!["booking_id"]!.GetValue<Guid>();
        await app.ExecAsync($"UPDATE bookings SET payment_status = 'paid' WHERE booking_id = '{bk}'");
        var cancel = await Api.PostAsync(s.Reception, $"/api/v1/bookings/{bk}/cancel", new { reason = "ظرف طارئ" });
        Assert.Equal(200, cancel.Status);
        Assert.False(cancel.Body!["counted_as_no_show"]!.GetValue<bool>());
        Assert.Equal(25000, cancel.Body!["credit_added"]!.GetValue<long>());
        Assert.Equal(25000, (await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}")).Body!["credit_balance"]!.GetValue<long>());
        Assert.Equal("BOOKING_NOT_CANCELLABLE", (await Api.PostAsync(s.Reception, $"/api/v1/bookings/{bk}/cancel", new { reason = "x" })).Code);

        // إلغاء قبل الموعد بأقل من ساعتين يُحتسب غياباً
        var today = await TodaySessionAsync(s);
        var late = (await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = today }, Guid.NewGuid()))
            .Body!["booking_id"]!.GetValue<Guid>();
        var lateCancel = await Api.PostAsync(s.Reception, $"/api/v1/bookings/{late}/cancel", new { reason = "تأخر" });
        Assert.True(lateCancel.Body!["counted_as_no_show"]!.GetValue<bool>());
        Assert.Equal(1, (await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}/ban-status")).Body!["no_show_count"]!.GetValue<int>());
    }

    [SkippableFact]
    public async Task Three_no_shows_ban_the_patient_until_manager_unbans()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var session = await TodaySessionAsync(s);
        var patient = await PatientAsync(s.Reception);

        for (var i = 0; i < 3; i++)
        {
            var ci = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
            Assert.Equal(201, ci.Status);
            var ns = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ci.Body!["ticket_id"]}/no-show", new { mode = "final" });
            Assert.Equal("no_show", ns.Body!["status"]!.GetValue<string>());
            Assert.Equal(i == 2, ns.Body!["patient_banned"]!.GetValue<bool>());
            // فاتورة الغائب غير المدفوعة تُلغى
            Assert.Equal("void", await app.ScalarAsync<string>($"SELECT status FROM invoices WHERE invoice_id = '{ci.Body!["invoice_id"]}'"));
        }

        var status = await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}/ban-status");
        Assert.True(status.Body!["is_banned"]!.GetValue<bool>());
        Assert.Equal("no_show_limit", status.Body!["reason"]!.GetValue<string>());
        Assert.Equal(10000, status.Body!["unban_deposit_amount"]!.GetValue<long>());

        var blocked = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = session }, Guid.NewGuid());
        Assert.Equal(403, blocked.Status);
        Assert.Equal("PATIENT_BANNED", blocked.Code);
        Assert.Equal(10000, blocked.Body!["unban_deposit_amount"]!.GetValue<long>());

        // العربون النقدي يحتاج وردية مفتوحة (انظر BillingTests)؛ الإعفاء للمدير فقط
        var cash = await Api.PostAsync(s.Admin, $"/api/v1/patients/{patient}/unban", new { method = "cash", amount = 10000 }, Guid.NewGuid());
        Assert.Equal("NO_OPEN_SHIFT", cash.Code);
        var (_, accountant) = await app.CreateStaffAsync("accountant");
        Assert.Equal(403, (await Api.PostAsync(app.Client(accountant), $"/api/v1/patients/{patient}/unban",
            new { method = "waived", amount = 0, note = "حالة إنسانية" }, Guid.NewGuid())).Status);
        var waived = await Api.PostAsync(s.Admin, $"/api/v1/patients/{patient}/unban", new { method = "waived", amount = 0, note = "حالة إنسانية" }, Guid.NewGuid());
        Assert.Equal(200, waived.Status);
        Assert.False(waived.Body!["is_banned"]!.GetValue<bool>());

        // الغيابات السابقة لا تُحتسب بعد الفك
        Assert.Equal(0, (await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}/ban-status")).Body!["no_show_count"]!.GetValue<int>());
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = session }, Guid.NewGuid())).Status);
    }

    [SkippableFact]
    public async Task Emergency_skip_and_late_arrival_reorder_the_queue()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var tickets = new List<JsonNode>();
        for (var i = 0; i < 4; i++)
        {
            var ci = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in",
                new { patient_id = await PatientAsync(s.Reception, $"مريض {i}"), doctor_id = s.DoctorId }, Guid.NewGuid());
            tickets.Add(ci.Body!);
            if (i < 3) await MarkPaidAsync(ci.Body!);
        }
        Guid T(int i) => tickets[i]["ticket_id"]!.GetValue<Guid>();

        // الرابع غير مدفوع لكنه طوارئ: يتقدم للأول ويمكن استدعاؤه
        var em = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{T(3)}/emergency", new { reason = "ضيق تنفس حاد" });
        Assert.Equal(200, em.Status);
        Assert.Equal(1, em.Body!["position"]!.GetValue<int>());
        Assert.Equal(3, em.Body!["affected_tickets"]!.GetValue<int>());
        Assert.Equal(400, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{T(2)}/emergency", new { })).Status);

        var next = await Api.PostAsync(s.Reception, $"/api/v1/queue/call-next?doctor_id={s.DoctorId}");
        Assert.Equal(T(3), next.Body!["ticket_id"]!.GetValue<Guid>());

        // تخطي الأول ثم وصوله متأخراً: يعود بأولوية late_reinserted
        var skip = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{T(0)}/no-show", new { mode = "skip" });
        Assert.Equal("skipped", skip.Body!["status"]!.GetValue<string>());
        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        var order = live.Body!["items"]!.AsArray().Select(i => i!["ticket_id"]!.GetValue<Guid>()).ToList();
        Assert.True(order.SequenceEqual([T(3), T(1), T(2), T(0)]), live.Raw);

        var back = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{T(0)}/late-arrival");
        Assert.Equal(200, back.Status);
        Assert.Equal("arrived", back.Body!["status"]!.GetValue<string>());
        Assert.Equal("late_reinserted", back.Body!["priority"]!.GetValue<string>());
        // يأتي خلف الحاضرين المنتظرين
        live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        order = live.Body!["items"]!.AsArray().Select(i => i!["ticket_id"]!.GetValue<Guid>()).ToList();
        Assert.Equal([T(3), T(1), T(2), T(0)], order);
        Assert.Equal(409, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{T(0)}/late-arrival")).Status);

        // الطبيب لا يبدأ مريضاً وآخر في الغرفة
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{T(3)}/start")).Status);
        Assert.Equal("DOCTOR_BUSY", (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{T(1)}/start")).Code);
    }

    [SkippableFact]
    public async Task Display_screen_shows_numbers_and_receives_realtime_events()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var device = await Api.PostAsync(s.Admin, "/api/v1/devices", new { kind = "display", name = "شاشة الانتظار", clinic_id = s.ClinicId });
        Assert.Equal(201, device.Status);
        var displayId = device.Body!["device_id"]!.GetValue<Guid>();
        var key = device.Body!["display_key"]!.GetValue<string>();
        Assert.Equal(403, (await Api.PostAsync(s.Reception, "/api/v1/devices", new { kind = "display", name = "x", clinic_id = s.ClinicId })).Status);

        // الاستقبال متصل بالقناة المباشرة، والشاشة كذلك بمفتاحها
        var staffEvents = new List<string>();
        await using var staff = Hub(s.ReceptionToken);
        staff.On<JsonElementWrapper>("queue.updated", _ => { lock (staffEvents) staffEvents.Add("queue.updated"); });
        await staff.StartAsync();
        var called = new TaskCompletionSource<string>();
        await using var screen = Hub(null);
        screen.On<Dictionary<string, object?>>("ticket.called", p => called.TrySetResult(p["ticket_number"]?.ToString() ?? ""));
        await screen.StartAsync();
        Assert.False(await screen.InvokeAsync<bool>("JoinDisplay", displayId, "wrong-key"));
        Assert.True(await screen.InvokeAsync<bool>("JoinDisplay", displayId, key));

        var ci = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = await PatientAsync(s.Reception, "سرّي جداً"), doctor_id = s.DoctorId }, Guid.NewGuid());
        await MarkPaidAsync(ci.Body!);
        await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ci.Body!["ticket_id"]}/call");

        Assert.Equal(ci.Body!["ticket_number"]!.GetValue<string>(), await called.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        for (var i = 0; i < 50 && staffEvents.Count < 2; i++) await Task.Delay(100);
        Assert.True(staffEvents.Count >= 2);

        var wrong = app.Client();
        wrong.DefaultRequestHeaders.Add("X-Display-Key", "nope");
        Assert.Equal(401, (await Api.GetAsync(wrong, $"/api/v1/public/displays/{displayId}/queue")).Status);
        var c = app.Client();
        c.DefaultRequestHeaders.Add("X-Display-Key", key);
        var board = await Api.GetAsync(c, $"/api/v1/public/displays/{displayId}/queue");
        Assert.Equal(200, board.Status);
        Assert.Equal(ci.Body!["ticket_number"]!.GetValue<string>(), board.Body!["now_serving"]!.GetValue<string>());
        Assert.DoesNotContain("سرّي", board.Raw);
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

    private sealed class JsonElementWrapper;

    // ------------------------------------------------------------------ العائد بالنتائج (التدقيق 1.2، 1.3-د، 2.3-ج)

    private async Task<(Guid Lab, Guid Ultrasound)> DiagnosticServicesAsync()
    {
        var radClinic = Guid.NewGuid();
        var lab = Guid.NewGuid();
        var us = Guid.NewGuid();
        await app.ExecAsync($"""
            INSERT INTO clinics (clinic_id, branch_id, name, department) VALUES ('{radClinic}', '{app.Branch1}', 'الأشعة {radClinic.ToString()[..4]}', 'radiology');
            INSERT INTO services (service_id, branch_id, clinic_id, name, type, price) VALUES ('{lab}', '{app.Branch1}', '{app.LabBranch1}', 'CBC {lab.ToString()[..4]}', 'lab', 30000);
            INSERT INTO services (service_id, branch_id, clinic_id, name, type, modality, price) VALUES ('{us}', '{app.Branch1}', '{radClinic}', 'سونار {us.ToString()[..4]}', 'radiology', 'ultrasound', 40000);
            """);
        return (lab, us);
    }

    /// <summary>مراجع مباشر مدفوع: يدخل للطبيب ويُنهي المعاينة بطلبات (بلا طلبات = إنهاء عادي).</summary>
    private async Task<JsonNode> VisitWithOrdersAsync(ClinicSetup s, Guid patient, Guid[] lab, Guid[] radiology)
    {
        var ci = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.True(ci.Status == 201, ci.Raw);
        await MarkPaidAsync(ci.Body!);
        var ticket = ci.Body!["ticket_id"]!.GetValue<Guid>();
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call")).Status);
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/start")).Status);
        var consult = await Api.PostAsync(s.Doctor, "/api/v1/consultations", new
        {
            ticket_id = ticket, diagnosis = "فحص",
            lab_orders = lab.Select(id => new { service_id = id }).ToArray(),
            radiology_orders = radiology.Select(id => new { service_id = id }).ToArray(),
            finish_visit = true, call_next = false,
        }, Guid.NewGuid());
        Assert.True(consult.Status == 201, consult.Raw);
        return ci.Body!;
    }

    private async Task<JsonNode> LiveItemAsync(ClinicSetup s, Guid ticket) =>
        (await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}")).Body!["items"]!.AsArray()
        .First(i => i!["ticket_id"]!.GetValue<Guid>() == ticket)!;

    [SkippableFact]
    public async Task Finish_with_lab_referral_awaits_results_then_returns_after_emergency_without_new_invoice()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var (lab, _) = await DiagnosticServicesAsync();
        var returning = await PatientAsync(s.Reception, "عائد بالنتائج");
        var first = await VisitWithOrdersAsync(s, returning, [lab], []);
        var ticket = first["ticket_id"]!.GetValue<Guid>();

        // إنهاء المعاينة مع تحويل مختبر: بانتظار قسم، خارج الترتيب، والكشفية نُفذت مرة واحدة
        var awaiting = await LiveItemAsync(s, ticket);
        Assert.Equal("awaiting_results", awaiting["status"]!.GetValue<string>());
        Assert.Equal("awaiting_results", awaiting["display_state"]!.GetValue<string>());
        Assert.Equal("بانتظار المختبر", awaiting["display_state_ar"]!.GetValue<string>());
        Assert.Null(awaiting["queue_position"]);
        Assert.False(await app.ScalarAsync<bool>($"SELECT performed_at IS NULL FROM invoice_items WHERE ticket_id = '{ticket}'"));

        // مباشران مدفوعان ثم طوارئ غير مدفوع
        var others = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var ci = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = await PatientAsync(s.Reception, $"مباشر {i}"), doctor_id = s.DoctorId }, Guid.NewGuid());
            await MarkPaidAsync(ci.Body!);
            others.Add(ci.Body!["ticket_id"]!.GetValue<Guid>());
        }
        var em = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = await PatientAsync(s.Reception, "طوارئ"), doctor_id = s.DoctorId }, Guid.NewGuid());
        var emergency = em.Body!["ticket_id"]!.GetValue<Guid>();
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{emergency}/emergency", new { reason = "نزيف" })).Status);

        // عاد بالنتائج: نفس الرقم، لا فاتورة جديدة، لا مبلغ مطلوب
        var back = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/returned", new { });
        Assert.True(back.Status == 200, back.Raw);
        Assert.Equal(first["ticket_number"]!.GetValue<string>(), back.Body!["ticket_number"]!.GetValue<string>());
        Assert.Equal("arrived", back.Body!["status"]!.GetValue<string>());
        Assert.Equal(0, back.Body!["amount_due"]!.GetValue<long>());
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM invoice_items WHERE ticket_id = '{ticket}'"));
        Assert.Equal("returning", await app.ScalarAsync<string>($"SELECT priority FROM queue_tickets WHERE ticket_id = '{ticket}'"));

        // الترتيب: الطوارئ ثم العائد ثم المباشرون
        var order = (await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}")).Body!["items"]!.AsArray()
            .Where(i => i!["queue_position"] is not null).OrderBy(i => i!["queue_position"]!.GetValue<int>())
            .Select(i => i!["ticket_id"]!.GetValue<Guid>()).ToList();
        Assert.Equal([emergency, ticket, others[0], others[1]], order);
        Assert.Equal("عائد بالنتائج", (await LiveItemAsync(s, ticket))["display_state_ar"]!.GetValue<string>());

        // الاستدعاء: الطوارئ أولاً ثم العائد بلا فحص دفع
        Assert.Equal(emergency, (await Api.PostAsync(s.Reception, $"/api/v1/queue/call-next?doctor_id={s.DoctorId}")).Body!["ticket_id"]!.GetValue<Guid>());
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{emergency}/start")).Status);
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{emergency}/complete", new { call_next = false })).Status);
        Assert.Equal(ticket, (await Api.PostAsync(s.Reception, $"/api/v1/queue/call-next?doctor_id={s.DoctorId}")).Body!["ticket_id"]!.GetValue<Guid>());
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/start")).Status);

        // المعاينة الثانية على نفس الاستشارة تُنهي نهائياً
        var consultation = await app.ScalarAsync<Guid>($"SELECT consultation_id FROM consultations WHERE ticket_id = '{ticket}'");
        var finish = await Api.PatchAsync(s.Doctor, $"/api/v1/consultations/{consultation}", new { clinical_notes = "النتيجة طبيعية", finish_visit = true, call_next = false });
        Assert.True(finish.Status == 200, finish.Raw);
        Assert.Equal("completed", finish.Body!["queue"]!["status"]!.GetValue<string>());
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM queue_tickets WHERE patient_id = '{returning}'"));
    }

    [SkippableFact]
    public async Task Check_in_of_awaiting_patient_requires_return_or_new_choice_and_is_audited()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var (lab, _) = await DiagnosticServicesAsync();

        var p1 = await PatientAsync(s.Reception, "يختار العودة");
        var t1 = (await VisitWithOrdersAsync(s, p1, [lab], []))["ticket_id"]!.GetValue<Guid>();
        var ask = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = p1, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.Equal(409, ask.Status);
        Assert.Equal("RETURN_OR_NEW_REQUIRED", ask.Code);
        Assert.Equal(t1, ask.Body!["ticket_id"]!.GetValue<Guid>());
        Assert.Equal("return", ask.Body!["default"]!.GetValue<string>());
        Assert.True(ask.Body!["same_ticket"]!.GetValue<bool>());
        Assert.Equal(400, (await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = p1, doctor_id = s.DoctorId, visit_mode = "x" }, Guid.NewGuid())).Status);

        var ret = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = p1, doctor_id = s.DoctorId, visit_mode = "return" }, Guid.NewGuid());
        Assert.True(ret.Status == 201, ret.Raw);
        Assert.Equal(t1, ret.Body!["ticket_id"]!.GetValue<Guid>());
        Assert.Equal(0, ret.Body!["amount_due"]!.GetValue<long>());
        var log = await app.ScalarAsync<string>($"SELECT after::text FROM audit_logs WHERE action = 'TICKET_RETURNED_WITH_RESULTS' AND entity_id = '{t1}'");
        Assert.Contains("\"staff_user_id\"", log);
        Assert.Contains("\"staff_name\"", log);
        Assert.Contains("return_same_ticket", log);

        // زيارة جديدة بدل العودة: دور وفاتورة جديدان، والقرار مسجل
        var p2 = await PatientAsync(s.Reception, "يختار زيارة جديدة");
        var t2 = (await VisitWithOrdersAsync(s, p2, [lab], []))["ticket_id"]!.GetValue<Guid>();
        var fresh = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = p2, doctor_id = s.DoctorId, visit_mode = "new" }, Guid.NewGuid());
        Assert.True(fresh.Status == 201, fresh.Raw);
        Assert.NotEqual(t2, fresh.Body!["ticket_id"]!.GetValue<Guid>());
        Assert.True(fresh.Body!["amount_due"]!.GetValue<long>() > 0);
        var newLog = await app.ScalarAsync<string>($"SELECT after::text FROM audit_logs WHERE action = 'VISIT_NEW_INSTEAD_OF_RETURN' AND entity_id = '{fresh.Body!["ticket_id"]}'");
        Assert.Contains(t2.ToString(), newLog);
    }

    [SkippableFact]
    public async Task Session_close_turns_awaiting_into_free_return_and_next_day_return_is_free_and_linked()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var (lab, _) = await DiagnosticServicesAsync();
        await app.ExecAsync($"UPDATE clinics SET free_return_days = 3 WHERE clinic_id = '{s.ClinicId}'");
        var patient = await PatientAsync(s.Reception, "يعود غداً");
        var first = await VisitWithOrdersAsync(s, patient, [lab], []);
        var original = first["ticket_id"]!.GetValue<Guid>();

        // انتهت الوجبة (أمس) وهو بانتظار المختبر: مهمة الإغلاق تنهي الدور وتعطيه عودة مجانية بمدة العيادة
        await app.ExecAsync($"UPDATE doctor_sessions SET session_date = session_date - 1, end_time = '01:00', extended_end_time = NULL, status = 'open' WHERE session_id = (SELECT session_id FROM queue_tickets WHERE ticket_id = '{original}')");
        using (var scope = app.Server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AlRahma.Application.Notifications.ScheduledJobs>().AutoNoShowAsync(default);
        Assert.Equal("completed", await app.ScalarAsync<string>($"SELECT status FROM queue_tickets WHERE ticket_id = '{original}'"));
        var days = await app.ScalarAsync<double>($"SELECT (extract(epoch FROM free_return_until - now()) / 86400)::float8 FROM queue_tickets WHERE ticket_id = '{original}'");
        Assert.InRange(days, 2.9, 3.1);

        var ask = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.Equal("RETURN_OR_NEW_REQUIRED", ask.Code);
        Assert.False(ask.Body!["same_ticket"]!.GetValue<bool>());

        var ret = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId, visit_mode = "return" }, Guid.NewGuid());
        Assert.True(ret.Status == 201, ret.Raw);
        var again = ret.Body!["ticket_id"]!.GetValue<Guid>();
        Assert.NotEqual(original, again);
        Assert.Equal(0, ret.Body!["amount_due"]!.GetValue<long>());
        Assert.Equal("paid", ret.Body!["payment_status"]!.GetValue<string>());
        Assert.Equal(original, await app.ScalarAsync<Guid>($"SELECT original_ticket_id FROM queue_tickets WHERE ticket_id = '{again}'"));
        Assert.Equal("returning", await app.ScalarAsync<string>($"SELECT priority FROM queue_tickets WHERE ticket_id = '{again}'"));
        Assert.Contains(original.ToString(), await app.ScalarAsync<string>($"SELECT after::text FROM audit_logs WHERE action = 'TICKET_RETURN_NEW_SESSION' AND entity_id = '{again}'"));
        // العودة المجانية تُستخدم مرة واحدة
        Assert.Equal("ALREADY_IN_QUEUE", (await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid())).Code);
    }

    [SkippableFact]
    public async Task Multiple_referrals_show_each_department_until_ready_and_external_results_close_the_rest()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await SetupAsync();
        var (lab, us) = await DiagnosticServicesAsync();
        var patient = await PatientAsync(s.Reception, "مختبر وسونار");
        var ticket = (await VisitWithOrdersAsync(s, patient, [lab], [us]))["ticket_id"]!.GetValue<Guid>();
        Assert.Equal("بانتظار المختبر والسونار", (await LiveItemAsync(s, ticket))["display_state_ar"]!.GetValue<string>());

        string Referral(string service) => $"SELECT r.referral_id FROM referrals r JOIN medical_orders m ON m.order_item_id = r.order_item_id JOIN consultations c ON c.consultation_id = m.consultation_id WHERE c.ticket_id = '{ticket}' AND r.service = '{service}'";
        await app.ExecAsync($"UPDATE referrals SET status = 'completed', completed_at = now() WHERE referral_id = ({Referral("lab")})");
        var item = await LiveItemAsync(s, ticket);
        Assert.Equal("بانتظار السونار", item["display_state_ar"]!.GetValue<string>());
        Assert.Equal(["ultrasound"], item["awaiting_services"]!.AsArray().Select(x => x!.GetValue<string>()).ToList());

        // نتيجة السونار من خارج المجمع: يعود ويُعلَّم المتبقي خارجياً
        var back = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/returned", new { external = true });
        Assert.True(back.Status == 200, back.Raw);
        Assert.Equal("external", await app.ScalarAsync<string>($"SELECT status FROM referrals WHERE referral_id = ({Referral("ultrasound")})"));

        // تحويلان آخران يكتملان → «جاهز للعودة»
        var p2 = await PatientAsync(s.Reception, "جاهز للعودة");
        var t2 = (await VisitWithOrdersAsync(s, p2, [lab], [us]))["ticket_id"]!.GetValue<Guid>();
        await app.ExecAsync($"""
            UPDATE referrals SET status = 'completed', completed_at = now() WHERE referral_id IN (
              SELECT r.referral_id FROM referrals r JOIN medical_orders m ON m.order_item_id = r.order_item_id
              JOIN consultations c ON c.consultation_id = m.consultation_id WHERE c.ticket_id = '{t2}');
            """);
        var ready = await LiveItemAsync(s, t2);
        Assert.Equal("ready_to_return", ready["display_state"]!.GetValue<string>());
        Assert.Equal("جاهز للعودة", ready["display_state_ar"]!.GetValue<string>());

        // expect_return=false: إنهاء نهائي رغم التحويل
        var p3 = await PatientAsync(s.Reception, "لا يعود");
        var ci = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = p3, doctor_id = s.DoctorId }, Guid.NewGuid());
        await MarkPaidAsync(ci.Body!);
        var t3 = ci.Body!["ticket_id"]!.GetValue<Guid>();
        await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t3}/call");
        await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t3}/start");
        var c3 = await Api.PostAsync(s.Doctor, "/api/v1/consultations", new
        {
            ticket_id = t3, diagnosis = "x", lab_orders = new[] { new { service_id = lab } }, finish_visit = true, call_next = false, expect_return = false,
        }, Guid.NewGuid());
        Assert.Equal("completed", c3.Body!["queue"]!["status"]!.GetValue<string>());
    }
}
