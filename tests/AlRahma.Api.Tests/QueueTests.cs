using System.Text.Json.Nodes;
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
}
