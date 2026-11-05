using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>المرحلة 1ب: الأوفلاين بوقت حدوثه (C2، M4) والتحصيل الأوفلاين في ورديته الأصلية حتى لو أُغلقت (C3).</summary>
[Collection(ApiCollection.Name)]
public class OfflineTimeTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();

    [SkippableFact]
    public async Task Offline_cash_synced_after_shift_close_lands_in_its_own_shift()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var deviceCode = $"dev_off_{Guid.NewGuid():N}"[..20];
        await app.ScalarAsync<Guid>($"""
            INSERT INTO devices (device_code, branch_id, clinic_id, kind, name) VALUES ('{deviceCode}', '{app.Branch1}', '{s.ClinicId}', 'reception_pc', 'استقبال أوفلاين')
            RETURNING device_id
            """);
        var block = await Api.PostAsync(s.Reception, $"/api/v1/devices/{deviceCode}/receipt-blocks", new { size = 20 });
        var prefix = S(block.Body, "prefix");
        var shift = await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0, device_code = deviceCode }, Guid.NewGuid());
        Assert.True(shift.Status == 201, shift.Raw);
        var shiftId = Id(shift.Body, "shift_id");
        var cashier = await app.ScalarAsync<Guid>($"SELECT user_id FROM shifts WHERE shift_id = '{shiftId}'");

        // الحضور أوفلاين قبل 30 دقيقة: يُسجل وصوله بوقت حدوثه لا بوقت المزامنة
        var patient = await q.PatientAsync(s.Reception);
        var ticket = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/sync/batch", new
        {
            device_id = deviceCode,
            operations = new object[] { new { op_id = Guid.NewGuid(), type = "queue.check_in", occurred_at = at, payload = new { ticket_id = ticket, patient_id = patient, doctor_id = s.DoctorId } } },
        });
        Assert.Equal("applied", S(checkIn.Body!["results"]![0], "status"));
        var arrived = await app.ScalarAsync<DateTime>($"SELECT arrived_at FROM queue_tickets WHERE ticket_id = '{ticket}'");
        Assert.True(Math.Abs((arrived.ToUniversalTime() - at.UtcDateTime).TotalSeconds) < 2, $"arrived_at {arrived:o} != occurred_at {at:o}");

        // الموظف أغلق ورديته قبل أن يعود الاتصال (والنقد في الدرج): عدّ 25,000
        var closed = await Api.PostAsync(s.Reception, $"/api/v1/shifts/{shiftId}/close", new { declared = new { cash = 25000, pos_slips_count = 0, pos_total = 0 } }, Guid.NewGuid());
        Assert.Equal(200, closed.Status);
        Assert.Equal("OVERAGE", await app.ScalarAsync<string>($"SELECT audit_status FROM shifts WHERE shift_id = '{shiftId}'"));

        // المزامنة بعد الإغلاق: الدفعة تُقبل في ورديتها الأصلية، وتعاد المطابقة فتصبح متطابقة
        var pay = await Api.PostAsync(s.Reception, "/api/v1/sync/batch", new
        {
            device_id = deviceCode,
            operations = new object[]
            {
                new
                {
                    op_id = Guid.NewGuid(), type = "payment.create", occurred_at = at.AddMinutes(1),
                    payload = new { patient_id = patient, ticket_id = ticket, shift_id = shiftId, tenders = new[] { new { method = "cash", amount = 25000 } }, offline_receipt_number = prefix + "1" },
                },
            },
        });
        var r = pay.Body!["results"]![0]!;
        Assert.True(S(r, "status") == "applied", r.ToJsonString());
        Assert.Contains(r["warnings"]!.AsArray(), w => S(w, "code") == "LATE_PAYMENT_CLOSED_SHIFT");
        Assert.Equal(shiftId, await app.ScalarAsync<Guid>($"SELECT shift_id FROM payments WHERE patient_id = '{patient}'"));
        Assert.Equal(cashier, await app.ScalarAsync<Guid>($"SELECT created_by FROM payments WHERE patient_id = '{patient}'"));
        Assert.Equal(25000L, await app.ScalarAsync<long>($"SELECT expected_cash FROM shifts WHERE shift_id = '{shiftId}'"));
        Assert.Equal("MATCHED", await app.ScalarAsync<string>($"SELECT audit_status FROM shifts WHERE shift_id = '{shiftId}'"));
        Assert.True(await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'SHIFT_LATE_PAYMENT'") > 0);
    }

    [SkippableFact]
    public async Task Offline_walk_in_of_a_booked_patient_merges_with_the_booking()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var patient = await q.PatientAsync(s.Reception, "محجوز أوفلاين");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        var booking = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = Id(sessions.Body!["items"]![0], "session_id") }, Guid.NewGuid());
        Assert.True(booking.Status == 201, booking.Raw);

        var deviceCode = $"dev_mrg_{Guid.NewGuid():N}"[..20];
        await app.ScalarAsync<Guid>($"INSERT INTO devices (device_code, branch_id, clinic_id, kind, name) VALUES ('{deviceCode}', '{app.Branch1}', '{s.ClinicId}', 'reception_pc', 'دمج') RETURNING device_id");
        var local = Guid.NewGuid();
        var sync = await Api.PostAsync(s.Reception, "/api/v1/sync/batch", new
        {
            device_id = deviceCode,
            operations = new object[]
            {
                new { op_id = Guid.NewGuid(), type = "queue.check_in", occurred_at = DateTimeOffset.UtcNow.AddMinutes(-2), payload = new { ticket_id = local, patient_id = patient, doctor_id = s.DoctorId } },
                new { op_id = Guid.NewGuid(), type = "queue.call", occurred_at = DateTimeOffset.UtcNow.AddMinutes(-1), payload = new { ticket_id = local } },
            },
        });
        var r = sync.Body!["results"]![0]!;
        Assert.True(S(r, "status") == "applied", r.ToJsonString());
        Assert.Contains(r["warnings"]!.AsArray(), w => S(w, "code") == "MERGED_WITH_BOOKING");
        Assert.Equal("checked_in", await app.ScalarAsync<string>($"SELECT status FROM bookings WHERE booking_id = '{Id(booking.Body, "booking_id")}'"));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM queue_tickets WHERE patient_id = '{patient}'"));
        // النداء على المعرف المحلي وُجّه لتذكرة الحجز (قد يُرفض لسبب آخر كعدم الدفع، لكن ليس لأن التذكرة غير موجودة)
        var second = sync.Body!["results"]![1]!;
        Assert.NotEqual("TICKET_NOT_FOUND", second["error"]?["code"]?.GetValue<string>());
    }

    [SkippableFact]
    public async Task Online_payment_success_after_expiry_is_not_lost()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var phone = "0774" + Random.Shared.Next(1000000, 9999999);
        var p = await Api.PostAsync(s.Reception, "/api/v1/patients", new { full_name = "مريض دفع متأخر", phone_number = phone, gender = "male", approx_age = 40 }, Guid.NewGuid());
        var patient = Id(p.Body, "patient_id");
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        var login = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") });
        var me = app.Client(S(login.Body, "access_token"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        var booking = await Api.PostAsync(me, "/api/v1/bookings", new { patient_id = patient, session_id = Id(sessions.Body!["items"]![0], "session_id"), visit_kind = "auto" }, Guid.NewGuid());
        Assert.True(booking.Status == 201, booking.Raw);
        var bookingId = Id(booking.Body, "booking_id");
        var op = await Api.PostAsync(me, "/api/v1/online-payments", new { purpose = "booking_full", patient_id = patient, booking_id = bookingId, provider = "sandbox" }, Guid.NewGuid());
        var opId = Id(op.Body, "online_payment_id");

        // انتهت المهلة وعُلّمت منتهية، ثم وصل تأكيد البوابة بالنجاح
        await app.ScalarAsync<int>($"UPDATE online_payments SET expires_at = now() - interval '1 minute' WHERE online_payment_id = '{opId}' RETURNING 1");
        Assert.Equal("expired", S((await Api.GetAsync(me, $"/api/v1/online-payments/{opId}")).Body, "status"));
        var done = await Api.PostAsync(me, $"/api/v1/sandbox/pay/{opId}", new { result = "succeeded" });
        Assert.True(done.Status == 200, done.Raw);
        Assert.Equal("succeeded", S(done.Body, "status"));
        Assert.Equal("paid", S((await Api.GetAsync(me, $"/api/v1/bookings/{bookingId}")).Body, "payment_status"));
    }
}
