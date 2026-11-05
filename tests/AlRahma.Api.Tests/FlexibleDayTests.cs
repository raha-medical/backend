using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>المرحلة 2أ: يوم العيادة المرن (التمديد، إغلاق التسجيل، الإضافي فوق السعة، الطابور الواحد بالفجوات، الغياب التلقائي).</summary>
[Collection(ApiCollection.Name)]
public class FlexibleDayTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();
    private static string Hm(DateTime t) => t.ToString("HH:mm");
    private static DateTime BaghdadNow => DateTime.UtcNow.AddHours(3);

    private async Task<Guid> TodaySessionAsync(ClinicSetup s)
    {
        var today = DateOnly.FromDateTime(BaghdadNow);
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        return Id(sessions.Body!["items"]![0], "session_id");
    }

    private Task<ApiResult> WalkInAsync(QueueTests q, ClinicSetup s, Guid patient, bool? allowExtra = null) =>
        Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId, allow_extra = allowExtra }, Guid.NewGuid());

    [SkippableFact]
    public async Task After_planned_end_registration_stays_open_until_closed_and_extend_reopens_it()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var session = await TodaySessionAsync(s);
        if (BaghdadNow.Hour < 1 || BaghdadNow.Hour >= 23) return; // نافذة الوقت لا تكفي لتحريك الوجبة
        // الوقت المخطط انتهى قبل 10 دقائق
        await app.ExecAsync($"UPDATE doctor_sessions SET start_time = '{Hm(BaghdadNow.AddMinutes(-60))}', end_time = '{Hm(BaghdadNow.AddMinutes(-10))}' WHERE session_id = '{session}'");

        var a = await WalkInAsync(q, s, await q.PatientAsync(s.Reception, "بعد الوقت"));
        Assert.True(a.Status == 201, a.Raw); // ما زال الطبيب يعاين: يُقبل

        var close = await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/registration", new { closed = true });
        Assert.True(close.Status == 200, close.Raw);
        Assert.True(close.Body!["registration_closed"]!.GetValue<bool>());
        Assert.Equal("NO_ACTIVE_SESSION", (await WalkInAsync(q, s, await q.PatientAsync(s.Reception, "بعد الإغلاق"))).Code);

        var extend = await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/extend", new { end = Hm(BaghdadNow.AddMinutes(50)) });
        Assert.True(extend.Status == 200, extend.Raw);
        Assert.False(extend.Body!["registration_closed"]!.GetValue<bool>());
        Assert.Equal(201, (await WalkInAsync(q, s, await q.PatientAsync(s.Reception, "بعد التمديد"))).Status);

        var arrived = await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/doctor-arrived");
        Assert.NotNull(arrived.Body!["doctor_arrived_at"]);
        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        var sess = live.Body!["sessions"]?[0] ?? live.Body;
        Assert.True(sess!["extended"]!.GetValue<bool>(), live.Raw);
    }

    [SkippableFact]
    public async Task Over_capacity_walk_in_needs_confirmation_and_is_marked_extra()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var session = await TodaySessionAsync(s);
        await app.ExecAsync($"UPDATE doctor_sessions SET capacity_total = 2, online_quota = 0 WHERE session_id = '{session}'");
        Assert.Equal(201, (await WalkInAsync(q, s, await q.PatientAsync(s.Reception, "أول"))).Status);
        var second = await WalkInAsync(q, s, await q.PatientAsync(s.Reception, "ثاني"));
        Assert.Equal(201, second.Status);

        // الغائب لا يُحسب من السعة
        await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{Id(second.Body, "ticket_id")}/no-show", new { mode = "final" });
        Assert.Equal(201, (await WalkInAsync(q, s, await q.PatientAsync(s.Reception, "مكان الغائب"))).Status);

        var p = await q.PatientAsync(s.Reception, "إضافي");
        var full = await WalkInAsync(q, s, p);
        Assert.Equal("SESSION_FULL_EXTRA_POSSIBLE", full.Code);
        Assert.NotNull(full.Body!["expected_end"]);
        var extra = await WalkInAsync(q, s, p, allowExtra: true);
        Assert.True(extra.Status == 201, extra.Raw);
        Assert.True(extra.Body!["is_extra"]!.GetValue<bool>());

        // «ممنوع الإضافي»
        await app.ExecAsync($"UPDATE doctors SET allow_extra = false WHERE doctor_id = '{s.DoctorId}'");
        Assert.Equal("SESSION_FULL", (await WalkInAsync(q, s, await q.PatientAsync(s.Reception, "مرفوض"), allowExtra: true)).Code);
    }

    [SkippableFact]
    public async Task Walk_in_fills_the_gap_before_the_next_booked_slot()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        if (BaghdadNow.Hour >= 21) return; // يحتاج ساعتين قبل منتصف الليل
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var session = await TodaySessionAsync(s);
        // الوجبة بدأت الآن، ومواعيد الحجز بعد 60 دقيقة (التسلسل 7 بفترة 10 دقائق)
        await app.ExecAsync($"UPDATE doctor_sessions SET start_time = '{Hm(BaghdadNow.AddMinutes(-1))}', end_time = '23:59' WHERE session_id = '{session}'");
        var booker = await q.PatientAsync(s.Reception, "محجوز");
        var booking = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = booker, session_id = session }, Guid.NewGuid());
        Assert.True(booking.Status == 201, booking.Raw);
        await app.ExecAsync($"UPDATE bookings SET sequence_number = 7 WHERE booking_id = '{Id(booking.Body, "booking_id")}'");
        await app.ExecAsync($"UPDATE queue_tickets SET sequence_number = 7 WHERE booking_id = '{Id(booking.Body, "booking_id")}'");

        var walk = await WalkInAsync(q, s, await q.PatientAsync(s.Reception, "بدون حجز"));
        Assert.True(walk.Status == 201, walk.Raw);
        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        var items = (live.Body!["sessions"]?[0] ?? live.Body)!["items"]!.AsArray();
        // المراجع بدون حجز يأخذ الوقت الفارغ قبل الموعد، والمحجوز يبقى على موعده
        Assert.Equal(Id(walk.Body, "ticket_id"), Id(items[0], "ticket_id"));
    }

    [SkippableFact]
    public async Task Second_open_booking_with_the_same_doctor_is_refused()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var tomorrow = DateOnly.FromDateTime(BaghdadNow).AddDays(1);
        var after = tomorrow.AddDays(1);
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={tomorrow:yyyy-MM-dd}&to={after:yyyy-MM-dd}");
        var items = sessions.Body!["items"]!.AsArray();
        Assert.True(items.Count >= 2, sessions.Raw);
        var patient = await q.PatientAsync(s.Reception, "حجزان");
        var first = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = Id(items[0], "session_id") }, Guid.NewGuid());
        Assert.True(first.Status == 201, first.Raw);
        var second = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = Id(items[1], "session_id") }, Guid.NewGuid());
        Assert.Equal("DUPLICATE_DOCTOR_BOOKING", second.Code);
        Assert.Equal(Id(first.Body, "booking_id"), Id(second.Body, "booking_id"));
    }

    [SkippableFact]
    public async Task Doctor_settings_make_follow_up_free_and_remove_deposit()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var put = await Api.SendAsync(s.Admin, HttpMethod.Put, $"/api/v1/doctors/{s.DoctorId}/settings",
            new { allow_extra = true, late_grace_minutes = 15, follow_up_days = 30, follow_up_price = 0, online_deposit_amount = 0 });
        Assert.True(put.Status == 200, put.Raw);
        Assert.Equal(30, put.Body!["follow_up_days"]!.GetValue<int>());
        Assert.Equal(403, (await Api.SendAsync(s.Reception, HttpMethod.Put, $"/api/v1/doctors/{s.DoctorId}/settings", new { allow_extra = false })).Status);

        // زيارة أولى مكتملة ثم مراجعة: مجانية ومدفوعة تلقائياً
        var patient = await q.PatientAsync(s.Reception, "مراجعة مجانية");
        var first = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        await app.ExecAsync($"UPDATE queue_tickets SET status = 'completed', started_at = now() - interval '5 minutes', completed_at = now() WHERE ticket_id = '{Id(first.Body, "ticket_id")}'");
        var second = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        // نفس الوجبة لا تقبل التذكرة الثانية إن كانت الأولى فعالة؛ الأولى مكتملة فتقبل
        Assert.True(second.Status == 201, second.Raw);
        Assert.Equal(0, second.Body!["amount_due"]!.GetValue<long>());
        Assert.Equal("paid", S(second.Body, "payment_status"));
    }
}
