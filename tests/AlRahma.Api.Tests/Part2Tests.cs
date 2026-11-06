using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>الجزء الثاني: المعاينة والمختبر والأشعة والصيدلية، وتطبيق المريض والدفع الإلكتروني، والعمل أوفلاين.</summary>
[Collection(ApiCollection.Name)]
public class Part2Tests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();
    private static string Phone() => "0772" + Random.Shared.Next(1000000, 9999999);

    private async Task<(Guid Patient, string Phone)> PatientAsync(HttpClient reception, string name = "مريض الجزء الثاني")
    {
        var phone = Phone();
        var r = await Api.PostAsync(reception, "/api/v1/patients", new { full_name = name, phone_number = phone, gender = "female", approx_age = 30 }, Guid.NewGuid());
        Assert.Equal(201, r.Status);
        return (Id(r.Body, "patient_id"), phone);
    }

    private async Task<(HttpClient Client, JsonNode Body)> PatientLoginAsync(string phone)
    {
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        Assert.Equal(202, otp.Status);
        var code = S(otp.Body, "sandbox_code");
        Assert.Matches("^[0-9]{6}$", code);
        var ok = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code });
        Assert.Equal(200, ok.Status);
        return (app.Client(S(ok.Body, "access_token")), ok.Body!);
    }

    private async Task<Guid> PayAsync(HttpClient reception, Guid patient, Guid item, long amount)
    {
        var paid = await Api.PostAsync(reception, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount } } }, Guid.NewGuid());
        Assert.True(paid.Status == 201, paid.Raw);
        return Id(paid.Body, "payment_id");
    }

    [SkippableFact]
    public async Task Doctor_orders_lab_and_radiology_then_results_reach_patient_app_and_pharmacy_dispenses()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var radClinic = Guid.NewGuid();
        var lab = Guid.NewGuid();
        var rad = Guid.NewGuid();
        await app.ExecAsync($"""
            INSERT INTO clinics (clinic_id, branch_id, name, department) VALUES ('{radClinic}', '{app.Branch1}', 'الأشعة {radClinic.ToString()[..4]}', 'radiology');
            INSERT INTO services (service_id, branch_id, clinic_id, name, type, price) VALUES ('{lab}', '{app.Branch1}', '{app.LabBranch1}', 'CBC {lab.ToString()[..4]}', 'lab', 30000);
            INSERT INTO services (service_id, branch_id, clinic_id, name, type, modality, price) VALUES ('{rad}', '{app.Branch1}', '{radClinic}', 'سونار بطن', 'radiology', 'ultrasound', 40000);
            """);
        var (patient, phone) = await PatientAsync(s.Reception);
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        var ticket = Id(checkIn.Body, "ticket_id");
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        await PayAsync(s.Reception, patient, await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{ticket}'"), 25000);
        await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call");

        var early = await Api.PostAsync(s.Doctor, "/api/v1/consultations", new { ticket_id = ticket, diagnosis = "x" }, Guid.NewGuid());
        Assert.Equal("INVALID_TICKET_STATE", early.Code);
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/start")).Status);

        // المعاينة: تشخيص ووصفة وطلب تحليل وأشعة (بنود غير مدفوعة في السلة)
        var body = new
        {
            ticket_id = ticket, diagnosis = "التهاب معدة", icd10_code = "K29.7", clinical_notes = "ألم شرسوفي", private_notes = "ملاحظة خاصة",
            prescription = new[] { new { drug_name = "Omeprazole 20mg", dose = "1 cap", frequency = "مرتين يومياً", duration_days = 14 } },
            lab_orders = new[] { new { service_id = lab, note = "صائم" } },
            radiology_orders = new[] { new { service_id = rad } },
            follow_up_after_days = 7,
        };
        var consult = await Api.PostAsync(s.Doctor, "/api/v1/consultations", body, Guid.NewGuid());
        Assert.True(consult.Status == 201, consult.Raw);
        var consultationId = Id(consult.Body, "consultation_id");
        Assert.StartsWith("RX-", S(consult.Body!["prescription"], "prescription_code"));
        var orders = consult.Body!["orders"]!.AsArray();
        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.Equal("ordered", S(o, "status")));
        var labOrder = orders.First(o => S(o, "department") == "lab")!;
        var radOrder = orders.First(o => S(o, "department") == "radiology")!;
        Assert.Equal("CONSULTATION_EXISTS", (await Api.PostAsync(s.Doctor, "/api/v1/consultations", body, Guid.NewGuid())).Code);

        // المختبر: لا عمل قبل الدفع
        var (_, labToken) = await app.CreateStaffAsync("lab_tech");
        var tech = app.Client(labToken);
        var labOrderId = Id(labOrder, "order_item_id");
        Assert.Equal(402, (await Api.PostAsync(tech, $"/api/v1/lab/orders/{labOrderId}/collect-sample")).Status);
        await PayAsync(s.Reception, patient, Id(labOrder, "invoice_item_id"), 30000);
        var worklist = await Api.GetAsync(tech, "/api/v1/lab/worklist");
        Assert.Contains(worklist.Body!["items"]!.AsArray(), x => Id(x, "order_item_id") == labOrderId);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/lab/worklist")).Status);

        var template = await Api.SendAsync(s.Admin, HttpMethod.Put, $"/api/v1/lab/tests/{lab}/template", new
        {
            parameters = new object[]
            {
                new { code = "WBC", name = "White Blood Cells", unit = "10^9/L", ref_low = 4.0, ref_high = 11.0, critical_low = 2.0, critical_high = 30.0 },
                new { code = "HGB", name = "Hemoglobin", unit = "g/dL", ref_low = 12.0, ref_high = 16.0 },
            },
        });
        Assert.True(template.Status == 200, template.Raw);
        Assert.Equal("numeric", S((await Api.GetAsync(tech, $"/api/v1/lab/tests/{lab}/template")).Body, "result_type"));

        var sample = await Api.PostAsync(tech, $"/api/v1/lab/orders/{labOrderId}/collect-sample");
        Assert.Equal("sample_collected", S(sample.Body, "status"));
        Assert.Matches(@"^S-\d{6}-\d{4}$", S(sample.Body, "sample_barcode"));
        var missing = await Api.SendAsync(tech, HttpMethod.Put, $"/api/v1/lab/orders/{labOrderId}/result",
            new { values = new[] { new { code = "WBC", value = 1.5 } }, release = true });
        Assert.Equal(400, missing.Status);
        var result = await Api.SendAsync(tech, HttpMethod.Put, $"/api/v1/lab/orders/{labOrderId}/result", new
        {
            values = new[] { new { code = "WBC", value = 1.5 }, new { code = "HGB", value = 13.2 } }, comment = "قيمة حرجة", release = true,
        });
        Assert.True(result.Status == 200, result.Raw);
        Assert.Equal("released", S(result.Body, "status"));
        Assert.True(result.Body!["has_critical"]!.GetValue<bool>());
        Assert.Equal("critical_low", S(result.Body!["values"]!.AsArray().First(v => S(v, "code") == "WBC"), "flag"));
        var amendNoReason = await Api.SendAsync(tech, HttpMethod.Put, $"/api/v1/lab/orders/{labOrderId}/result",
            new { values = new[] { new { code = "WBC", value = 2.5 }, new { code = "HGB", value = 13.2 } }, release = true });
        Assert.Equal(400, amendNoReason.Status);

        // الطبيب: إشعار القيمة الحرجة وقراءة النتيجة
        var bell = await Api.GetAsync(s.Doctor, "/api/v1/me/notifications");
        Assert.Contains(bell.Body!["items"]!.AsArray(), n => S(n, "type") == "lab.critical_value");
        Assert.Equal(200, (await Api.GetAsync(s.Doctor, $"/api/v1/lab/orders/{labOrderId}/result")).Status);

        // تطبيق المريض: الملف مربوط تلقائياً بنفس الهاتف
        var (me, login) = await PatientLoginAsync(phone);
        Assert.Contains(login["patients"]!.AsArray(), p => Id(p, "patient_id") == patient && S(p, "relation") == "self");
        Assert.Equal(200, (await Api.GetAsync(me, $"/api/v1/lab/orders/{labOrderId}/result")).Status);
        var radOrderId = Id(radOrder, "order_item_id");
        Assert.Equal("RESULT_NOT_READY", (await Api.GetAsync(me, $"/api/v1/radiology/orders/{radOrderId}/report")).Code);

        // قسم الأشعة: «مواعيد اليوم» تُصفّى بيوم الموعد لا بيوم الطلب
        await app.ExecAsync($"UPDATE medical_orders SET status = 'paid' WHERE order_item_id = '{radOrderId}' AND status = 'ordered'");
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).AddDays(2);
        var sch = await Api.PostAsync(s.Admin, $"/api/v1/radiology/orders/{radOrderId}/schedule", new { scheduled_at = new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(3)), room = "سونار 1" });
        Assert.True(sch.Status == 200, sch.Raw);
        var onDay = await Api.GetAsync(s.Admin, $"/api/v1/radiology/worklist?status=scheduled&date={day:yyyy-MM-dd}");
        Assert.Contains(onDay.Body!["items"]!.AsArray(), x => Id(x, "order_item_id") == radOrderId);
        var today = await Api.GetAsync(s.Admin, $"/api/v1/radiology/worklist?status=scheduled&date={DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)):yyyy-MM-dd}");
        Assert.DoesNotContain(today.Body!["items"]!.AsArray(), x => Id(x, "order_item_id") == radOrderId);
        // إرجاع الطلب لحالته (غير مدفوع) ليكمل الاختبار مسار الإلغاء أدناه
        await app.ExecAsync($"UPDATE medical_orders SET status = 'ordered', scheduled_at = NULL, room = NULL WHERE order_item_id = '{radOrderId}'");
        Assert.Equal(401, (await Api.GetAsync(me, $"/api/v1/patients/{patient}")).Status); // توكن المريض لا يصل لـ endpoints الموظفين
        Assert.Equal(401, (await Api.GetAsync(me, "/api/v1/lab/worklist")).Status);
        var visits = await Api.GetAsync(me, $"/api/v1/me/patients/{patient}/visits");
        Assert.True(visits.Status == 200, visits.Raw);
        var visit = visits.Body!["items"]![0]!;
        Assert.Equal("التهاب معدة", S(visit, "diagnosis"));
        Assert.Null(visit["clinical_notes"]);
        Assert.True(visit["orders"]!.AsArray().First(o => S(o, "department") == "radiology")!["payment_required"]!.GetValue<bool>());
        var myRx = await Api.GetAsync(me, $"/api/v1/me/patients/{patient}/prescriptions");
        var code = S(myRx.Body!["items"]![0], "prescription_code");
        Assert.Equal(404, (await Api.GetAsync(me, $"/api/v1/me/patients/{Guid.NewGuid()}/visits")).Status);

        // الأشعة: إلغاء الطلب غير المدفوع من الاستقبال
        var cancel = await Api.PostAsync(s.Reception, $"/api/v1/medical-orders/{radOrderId}/cancel", new { reason = "المريض لا يريد" });
        Assert.Equal("cancelled", S(cancel.Body, "status"));
        Assert.Equal("void", await app.ScalarAsync<string>($"SELECT status FROM invoice_items WHERE invoice_item_id = '{Id(radOrder, "invoice_item_id")}'"));
        Assert.Equal("INVALID_ORDER_STATE", (await Api.PostAsync(s.Reception, $"/api/v1/medical-orders/{labOrderId}/cancel", new { reason = "x" })).Code);

        // الصيدلية: صرف الوصفة مرة واحدة
        var (_, pharmToken) = await app.CreateStaffAsync("pharmacist");
        var pharmacist = app.Client(pharmToken);
        var rx = await Api.GetAsync(pharmacist, $"/api/v1/pharmacy/prescriptions/{code}");
        Assert.True(rx.Status == 200, rx.Raw);
        var rxId = Id(rx.Body, "prescription_id");
        var dispense = await Api.PostAsync(pharmacist, $"/api/v1/pharmacy/prescriptions/{rxId}/dispense",
            new { lines = new[] { new { line_no = 1, dispensed = true } } }, Guid.NewGuid());
        Assert.True(dispense.Status == 200, dispense.Raw);
        Assert.Equal("dispensed", S(dispense.Body, "status"));
        Assert.Equal("PRESCRIPTION_ALREADY_DISPENSED", (await Api.PostAsync(pharmacist, $"/api/v1/pharmacy/prescriptions/{rxId}/dispense",
            new { lines = new[] { new { line_no = 1, dispensed = true } } }, Guid.NewGuid())).Code);
        Assert.Single((await Api.GetAsync(pharmacist, "/api/v1/pharmacy/dispensed")).Body!["items"]!.AsArray());

        // السجل الطبي: الملاحظات الخاصة للأطباء فقط، والملحق بعد المعاينة
        Assert.Equal(201, (await Api.PostAsync(s.Doctor, $"/api/v1/consultations/{consultationId}/addenda", new { text = "تحسن بعد العلاج" })).Status);
        var history = await Api.GetAsync(s.Doctor, $"/api/v1/patients/{patient}/history");
        Assert.Equal("ملاحظة خاصة", S(history.Body!["items"]![0], "private_notes"));
        Assert.Equal(1, history.Body!["items"]![0]!["addenda_count"]!.GetValue<int>());
        Assert.Null((await Api.GetAsync(s.Admin, $"/api/v1/patients/{patient}/history")).Body!["items"]![0]!["private_notes"]);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}/history")).Status);
        var edited = await Api.PatchAsync(s.Doctor, $"/api/v1/consultations/{consultationId}", new { diagnosis = "قرحة معدة" });
        Assert.Equal("قرحة معدة", S(edited.Body, "diagnosis"));
    }

    [SkippableFact]
    public async Task Patient_books_from_app_pays_online_in_sandbox_and_gets_notifications()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var (patient, phone) = await PatientAsync(s.Reception, "أم علي");
        var (me, _) = await PatientLoginAsync(phone);

        var wrong = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = "123456" });
        Assert.Equal("OTP_EXPIRED", wrong.Code); // الرمز استُخدم في الدخول السابق

        var child = await Api.PostAsync(me, "/api/v1/me/patients", new { full_name = "علي الصغير", gender = "male", birth_date = "2020-01-15", relation = "child" }, Guid.NewGuid());
        Assert.True(child.Status == 201, child.Raw);
        var childId = Id(child.Body, "patient_id");
        Assert.Equal(2, (await Api.GetAsync(me, "/api/v1/me/patients")).Body!["items"]!.AsArray().Count);
        var again = await Api.PostAsync(me, "/api/v1/me/patients", new { full_name = "علي الصغير", gender = "male", birth_date = "2020-01-15", relation = "child" }, Guid.NewGuid());
        Assert.Equal(200, again.Status);
        Assert.Equal(childId, Id(again.Body, "patient_id"));

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        var session = Id(sessions.Body!["items"]![0], "session_id");
        var booking = await Api.PostAsync(me, "/api/v1/bookings", new { patient_id = childId, session_id = session, visit_kind = "auto" }, Guid.NewGuid());
        Assert.True(booking.Status == 201, booking.Raw);
        Assert.Equal("online", S(booking.Body, "source"));
        Assert.Contains("pay_full_online", booking.Body!["payment_options"]!.AsArray().Select(x => x!.GetValue<string>()));
        var bookingId = Id(booking.Body, "booking_id");
        Assert.Equal(404, (await Api.PostAsync(me, "/api/v1/bookings", new { patient_id = s.DoctorId, session_id = session }, Guid.NewGuid())).Status);
        var (_, pharm) = await app.CreateStaffAsync("pharmacist");
        Assert.Equal(403, (await Api.PostAsync(app.Client(pharm), "/api/v1/bookings", new { patient_id = patient, session_id = session }, Guid.NewGuid())).Status);
        Assert.Contains((await Api.GetAsync(me, "/api/v1/bookings/my")).Body!["items"]!.AsArray(), b => Id(b, "booking_id") == bookingId);

        // الدفع الإلكتروني عبر البوابة التجريبية (زين كاش)
        Assert.Equal("PROVIDER_NOT_AVAILABLE", (await Api.PostAsync(me, "/api/v1/online-payments",
            new { purpose = "booking_full", patient_id = childId, booking_id = bookingId, provider = "fib" }, Guid.NewGuid())).Code);
        var op = await Api.PostAsync(me, "/api/v1/online-payments",
            new { purpose = "booking_full", patient_id = childId, booking_id = bookingId, provider = "zaincash" }, Guid.NewGuid());
        Assert.True(op.Status == 201, op.Raw);
        Assert.Equal("pending", S(op.Body, "status"));
        Assert.Equal(25000, op.Body!["amount"]!.GetValue<long>());
        var opId = Id(op.Body, "online_payment_id");
        // أمان: نقطة الإكمال القديمة أُزيلت، والاستعلام وصفحة البوابة بلا تسجيل دخول مرفوضان في كل البيئات
        Assert.Contains((await Api.PostAsync(app.Client(), $"/api/v1/sandbox/pay/{opId}", new { result = "succeeded" })).Status, new[] { 401, 404 });
        Assert.Equal(401, (await Api.PostAsync(app.Client(), $"/api/v1/online-payments/{opId}/verify")).Status);
        Assert.Equal(401, (await Api.GetAsync(app.Client(), $"/api/v1/mock-gateway/{S(op.Body, "provider_reference")}")).Status);
        Assert.Equal("pending", S((await Api.GetAsync(me, $"/api/v1/online-payments/{opId}")).Body, "status"));
        var done = await MockPay.SucceedAsync(me, opId);
        Assert.True(done.Status == 200, done.Raw);
        Assert.Equal("succeeded", S(done.Body, "status"));
        Assert.StartsWith("B1-O-", S(done.Body!["receipt"], "receipt_number"));
        Assert.Equal("paid", S((await Api.GetAsync(me, $"/api/v1/bookings/{bookingId}")).Body, "payment_status"));
        Assert.Equal("ALREADY_PAID", (await Api.PostAsync(me, "/api/v1/online-payments",
            new { purpose = "booking_full", patient_id = childId, booking_id = bookingId, provider = "zaincash" }, Guid.NewGuid())).Code);

        // تثبيت الحضور: الكشفية مدفوعة مسبقاً
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { booking_id = bookingId }, Guid.NewGuid());
        Assert.True(checkIn.Status == 201, checkIn.Raw);
        Assert.Equal("paid", S(checkIn.Body, "payment_status"));

        // الإشعارات والأجهزة
        Assert.Equal(201, (await Api.PostAsync(me, "/api/v1/me/devices", new { push_token = "fcm_" + Guid.NewGuid(), platform = "android", app = "patient" })).Status);
        Assert.Equal(400, (await Api.PostAsync(me, "/api/v1/me/devices", new { push_token = "fcm_x", platform = "android", app = "staff" })).Status);
        var bell = await Api.GetAsync(me, "/api/v1/me/notifications");
        Assert.True(bell.Status == 200, bell.Raw);
        Assert.Equal(204, (await Api.PostAsync(me, "/api/v1/me/notifications/read-all")).Status);
        Assert.Equal(0, (await Api.GetAsync(me, "/api/v1/me/notifications")).Body!["unread_count"]!.GetValue<int>());
        Assert.Equal(200, (await Api.GetAsync(s.Admin, "/api/v1/sandbox/messages")).Status);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/sandbox/messages")).Status);

        // حد طلبات الرمز: 3 كل 10 دقائق
        var fresh = Phone();
        var codes = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var req = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = fresh });
            Assert.Equal(202, req.Status);
            codes.Add(S(req.Body, "sandbox_code"));
        }
        Assert.True(codes.Distinct().Count() > 1, "الرمز يجب أن يكون عشوائياً لكل طلب");
        var lastCode = codes[^1];
        // في الوضع التجريبي يُسجَّل الرمز في الرسائل التجريبية
        var outbox = await Api.GetAsync(s.Admin, $"/api/v1/sandbox/messages?phone_number={fresh}");
        Assert.Contains(lastCode, outbox.Raw);
        // الرمز القديم يُلغى عند طلب رمز جديد
        if (codes[0] != lastCode)
            Assert.Equal("INVALID_OTP", (await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = fresh, code = codes[0] })).Code);
        var limited = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = fresh });
        Assert.Equal(429, limited.Status);
        Assert.Equal("RATE_LIMITED", limited.Code);
        var bad = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = fresh, code = lastCode == "999999" ? "999998" : "999999" });
        Assert.Equal("INVALID_OTP", bad.Code);
        Assert.True(bad.Body!["attempts_left"]!.GetValue<int>() >= 3);

        // refresh ثم logout
        var login = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = fresh, code = lastCode });
        var refreshed = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/refresh", new { refresh_token = S(login.Body, "refresh_token") });
        Assert.Equal(200, refreshed.Status);
        Assert.Equal(401, (await Api.PostAsync(app.Client(), "/api/v1/patient-auth/refresh", new { refresh_token = S(login.Body, "refresh_token") })).Status);
        Assert.Equal(401, (await Api.PostAsync(app.Client(), "/api/v1/patient-auth/refresh", new { refresh_token = S(refreshed.Body, "refresh_token") })).Status);
        Assert.Empty(login.Body!["patients"]!.AsArray());
    }

    [SkippableFact]
    public async Task Reception_works_offline_with_receipt_block_and_syncs_batch()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var deviceCode = $"dev_rec_{Guid.NewGuid():N}"[..20];
        var deviceId = await app.ScalarAsync<Guid>($"""
            INSERT INTO devices (device_code, branch_id, clinic_id, kind, name) VALUES ('{deviceCode}', '{app.Branch1}', '{s.ClinicId}', 'reception_pc', 'حاسبة الاستقبال')
            RETURNING device_id
            """);

        var block = await Api.PostAsync(s.Reception, $"/api/v1/devices/{deviceCode}/receipt-blocks", new { size = 50 });
        Assert.True(block.Status == 201, block.Raw);
        var prefix = S(block.Body, "prefix");
        Assert.Matches(@"^B1-D\d{2}-$", prefix);
        Assert.Equal(1, block.Body!["from"]!.GetValue<long>());
        Assert.Equal(50, block.Body!["to"]!.GetValue<long>());
        Assert.False(string.IsNullOrEmpty(S(block.Body, "signature")));
        Assert.Equal(deviceId, Id(block.Body, "device_id"));
        Assert.Equal(deviceCode, S(block.Body, "device_code"));
        // الوكيل يتحقق من توقيع الكتلة بالمفتاح العام
        var pub = await Api.GetAsync(app.Client(), "/api/v1/agent/drawer-public-key");
        Assert.Matches("^[0-9a-f]{16}$", S(pub.Body, "key_id"));
        using (var ec = System.Security.Cryptography.ECDsa.Create())
        {
            ec.ImportFromPem(S(pub.Body, "public_key_pem"));
            var signed = $"{Id(block.Body, "block_id")}|{deviceId}|{prefix}|1|50";
            Assert.True(ec.VerifyData(System.Text.Encoding.UTF8.GetBytes(signed), Convert.FromBase64String(S(block.Body, "signature")),
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
        }
        var same = await Api.PostAsync(s.Reception, $"/api/v1/devices/{deviceId}/receipt-blocks", new { });
        Assert.Equal(200, same.Status);
        Assert.Equal(Id(block.Body, "block_id"), Id(same.Body, "block_id"));

        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0, device_code = deviceCode }, Guid.NewGuid())).Status);
        var patient = Guid.NewGuid();
        var ticket = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-20);
        var ops = new object[]
        {
            new { op_id = Guid.NewGuid(), type = "patient.create", occurred_at = at, payload = new { patient_id = patient, full_name = "مريض أوفلاين", phone_number = Phone() } },
            new { op_id = Guid.NewGuid(), type = "queue.check_in", occurred_at = at.AddMinutes(1), payload = new { ticket_id = ticket, patient_id = patient, doctor_id = s.DoctorId } },
            new
            {
                op_id = Guid.NewGuid(), type = "payment.create", occurred_at = at.AddMinutes(2),
                payload = new { patient_id = patient, ticket_id = ticket, tenders = new[] { new { method = "cash", amount = 25000 } }, offline_receipt_number = prefix + "7" },
            },
            new { op_id = Guid.NewGuid(), type = "agent_event", occurred_at = at.AddMinutes(3), payload = new { local_event_id = 991, type = "drawer.opened_without_receipt", occurred_at = at.AddMinutes(3) } },
        };
        var batch = await Api.PostAsync(s.Reception, "/api/v1/sync/batch", new { device_id = deviceCode, operations = ops });
        Assert.True(batch.Status == 200, batch.Raw);
        var results = batch.Body!["results"]!.AsArray();
        Assert.All(results, r => Assert.True(S(r, "status") == "applied", r!.ToJsonString()));
        Assert.Equal(patient, Id(results[0]!["server_refs"], "patient_id"));
        Assert.Equal(prefix + "7", S(results[2]!["server_refs"], "receipt_number"));
        Assert.True(await app.ScalarAsync<bool>($"SELECT issued_offline FROM receipts WHERE receipt_number = '{prefix}7'"));
        Assert.Equal("paid", await app.ScalarAsync<string>($"SELECT status FROM invoice_items WHERE ticket_id = '{ticket}'"));

        // إعادة الإرسال: duplicate بلا تكرار
        var replay = await Api.PostAsync(s.Reception, "/api/v1/sync/batch", new { device_id = deviceCode, operations = ops });
        Assert.All(replay.Body!["results"]!.AsArray(), r => Assert.Equal("duplicate", S(r, "status")));
        Assert.Equal(1L, await app.ScalarAsync<long>($"SELECT count(*) FROM patients WHERE patient_id = '{patient}'"));

        // رقم خارج الكتلة، أو مستخدم، أو نوع غير معروف
        var bad = await Api.PostAsync(s.Reception, "/api/v1/sync/batch", new
        {
            device_id = deviceCode,
            operations = new object[]
            {
                new { op_id = Guid.NewGuid(), type = "payment.create", occurred_at = at, payload = new { patient_id = patient, invoice_item_ids = Array.Empty<Guid>(), tenders = new[] { new { method = "cash", amount = 1 } }, offline_receipt_number = prefix + "999" } },
                new { op_id = Guid.NewGuid(), type = "queue.teleport", occurred_at = at, payload = new { } },
                new { op_id = Guid.NewGuid(), type = "queue.call", occurred_at = at, payload = new { ticket_id = Guid.NewGuid() } },
            },
        });
        var badResults = bad.Body!["results"]!.AsArray();
        Assert.Equal("rejected", S(badResults[1], "status"));
        Assert.Equal("VALIDATION_ERROR", S(badResults[1]!["error"], "code"));
        Assert.Equal("rejected", S(badResults[2], "status"));
        Assert.Equal(404, (await Api.PostAsync(s.Reception, "/api/v1/sync/batch", new { device_id = "nope", operations = ops })).Status);

        Assert.Equal(403, (await Api.GetAsync(s.Doctor, "/api/v1/sync/snapshot")).Status); // الخصوصية: الاستقبال فقط
        var snapshot = await Api.GetAsync(s.Reception, $"/api/v1/sync/snapshot?device_id={deviceCode}");
        Assert.True(snapshot.Status == 200, snapshot.Raw);
        Assert.NotEmpty(snapshot.Body!["services"]!.AsArray());
        Assert.Contains(snapshot.Body!["patients"]!.AsArray(), p => Id(p, "patient_id") == patient);
        Assert.Contains(snapshot.Body!["tickets"]!.AsArray(), t => Id(t, "ticket_id") == ticket);
        Assert.Equal(Id(block.Body, "block_id"), Id(snapshot.Body!["receipt_block"], "block_id"));

        var returned = await Api.PostAsync(s.Reception, $"/api/v1/devices/{deviceCode}/receipt-blocks/{Id(block.Body, "block_id")}/return", new { last_used = 3 });
        Assert.True(returned.Status == 200, returned.Raw);
        Assert.Equal(8, returned.Body!["returned_from"]!.GetValue<long>()); // 7 استُخدم فعلاً
        Assert.Equal(50, returned.Body!["returned_to"]!.GetValue<long>());
        var next = await Api.PostAsync(s.Reception, $"/api/v1/devices/{deviceCode}/receipt-blocks", new { size = 50 });
        Assert.Equal(51, next.Body!["from"]!.GetValue<long>());
        Assert.Equal(prefix, S(next.Body, "prefix"));
    }
}
