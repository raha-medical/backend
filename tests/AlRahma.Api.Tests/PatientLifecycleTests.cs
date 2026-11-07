using System.Text.Json.Nodes;
using AlRahma.Application.PatientApp.Gateways;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class PatientLifecycleTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();
    private static void Ok(ApiResult r, int code = 200) => Assert.True(r.Status == code, r.Raw);

    [Theory]
    [InlineData("5555555555554444", true)]
    [InlineData("5213720951375414", true)]
    [InlineData("5555555555554445", false)]
    [InlineData("0000000000000000", false)]
    public void Mock_card_requires_Luhn(string card, bool expected) => Assert.Equal(expected, MockGatewayService.IsValidTestCard(card));

    [SkippableTheory]
    [InlineData(true, "qicard")]
    [InlineData(false, "qicard")]
    [InlineData(true, "zaincash")]
    public async Task Online_card_paid_called_ack_optional_started_completed(bool acknowledge, string provider)
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var phone = "0778" + Random.Shared.Next(1000000, 9999999);
        var p = await Api.PostAsync(s.Reception, "/api/v1/patients", new { full_name = "رحلة حجز أونلاين", phone_number = phone }, Guid.NewGuid());
        Ok(p, 201); var patient = Id(p.Body, "patient_id");
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        var login = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") });
        Ok(login); var me = app.Client(S(login.Body, "access_token"));
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var sessions = await Api.GetAsync(s.Doctor, $"/api/v1/doctors/{s.DoctorId}/sessions?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
        var booking = await Api.PostAsync(me, "/api/v1/bookings", new { patient_id = patient, session_id = Id(sessions.Body!["items"]![0], "session_id"), visit_kind = "auto" }, Guid.NewGuid());
        Ok(booking,201); var bid = Id(booking.Body,"booking_id");
        var payment = await Api.PostAsync(me, "/api/v1/online-payments", new { patient_id = patient, booking_id = bid, purpose = "booking_full", provider }, Guid.NewGuid());
        Ok(payment,201); var reference = S(payment.Body,"provider_reference");
        var credentials = await Api.PostAsync(me,$"/api/v1/mock-gateway/{reference}/credentials",provider == "qicard" ? (object)new { card_number="5555555555554444",expiry="12/40",cvv="987" } : new {wallet_number="07809999999",pin="4321"});
        Ok(credentials);
        Ok(await Api.PostAsync(me,$"/api/v1/mock-gateway/{reference}/otp",new { otp=MockPay.Otp(credentials) }));
        var verified=await Api.PostAsync(me,$"/api/v1/online-payments/{Id(payment.Body,"online_payment_id")}/verify");
        Ok(verified); Assert.Equal("succeeded",S(verified.Body,"status"));
        var check=await Api.PostAsync(s.Reception,"/api/v1/queue/check-in",new { booking_id=bid },Guid.NewGuid());
        Ok(check,201); var ticket=Id(check.Body,"ticket_id");
        Ok(await Api.PostAsync(s.Reception,$"/api/v1/queue/tickets/{ticket}/call"));
        if(acknowledge) Ok(await Api.PostAsync(me,$"/api/v1/me/queue-tickets/{ticket}/acknowledge-call"));
        Ok(await Api.PostAsync(s.Doctor,$"/api/v1/queue/tickets/{ticket}/start"));
        var done=await Api.PostAsync(s.Doctor,$"/api/v1/queue/tickets/{ticket}/complete",new {expect_return=false,call_next=false});
        Ok(done); Assert.Equal("completed",S(done.Body,"status"));
    }

    [SkippableFact]
    public async Task Walk_in_without_phone_collect_called_staff_present_started_completed()
    {
        Skip.If(TestApp.ServerUrl is null,"TEST_DATABASE_URL not set");
        var s=await new QueueTests(app).SetupAsync();
        var p=await Api.PostAsync(s.Reception,"/api/v1/patients",new {full_name="مراجع مباشر بدون هاتف",approx_age=30},Guid.NewGuid());
        Ok(p,201); Assert.Equal("",S(p.Body,"phone_number")); var patient=Id(p.Body,"patient_id");
        var check=await Api.PostAsync(s.Reception,"/api/v1/queue/check-in",new {patient_id=patient,doctor_id=s.DoctorId},Guid.NewGuid());
        Ok(check,201); var ticket=Id(check.Body,"ticket_id");
        Assert.Equal(402,(await Api.PostAsync(s.Reception,$"/api/v1/queue/tickets/{ticket}/call")).Status);
        Ok(await Api.PostAsync(s.Reception,"/api/v1/shifts/open",new {opening_float=0},Guid.NewGuid()),201);
        var basket=await Api.GetAsync(s.Reception,$"/api/v1/patients/{patient}/billable-items?department=clinic");
        var payment=await Api.PostAsync(s.Reception,"/api/v1/payments",new {patient_id=patient,invoice_item_ids=new[]{Id(basket.Body!["items"]![0],"invoice_item_id")},tenders=new[]{new {method="cash",amount=25000}}},Guid.NewGuid());
        Ok(payment,201);
        Ok(await Api.PostAsync(s.Reception,$"/api/v1/queue/tickets/{ticket}/call"));
        var live=await Api.GetAsync(s.Reception,$"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Assert.Null(live.Body!["items"]![0]!["call_ack_status"]);
        Ok(await Api.PostAsync(s.Reception,$"/api/v1/queue/tickets/{ticket}/present"));
        Ok(await Api.PostAsync(s.Reception,$"/api/v1/queue/tickets/{ticket}/present"));
        live=await Api.GetAsync(s.Reception,$"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Assert.Equal("acknowledged",S(live.Body!["items"]![0],"call_ack_status"));
        Ok(await Api.PostAsync(s.Doctor,$"/api/v1/queue/tickets/{ticket}/start"));
        var done=await Api.PostAsync(s.Doctor,$"/api/v1/queue/tickets/{ticket}/complete",new {expect_return=false,call_next=false});
        Ok(done); Assert.Equal("completed",S(done.Body,"status"));
    }

    [SkippableFact]
    public async Task In_room_ticket_in_previous_session_is_visible_and_can_be_closed()
    {
        Skip.If(TestApp.ServerUrl is null,"TEST_DATABASE_URL not set");
        var s=await new QueueTests(app).SetupAsync();
        var patient=await new QueueTests(app).PatientAsync(s.Reception);
        var check=await Api.PostAsync(s.Reception,"/api/v1/queue/check-in",new {patient_id=patient,doctor_id=s.DoctorId},Guid.NewGuid());
        Ok(check,201); var ticket=Id(check.Body,"ticket_id");
        // Simulate a genuinely abandoned previous-day visit, rather than silently marking it complete.
        var old=Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO doctor_sessions(session_id,doctor_id,branch_id,clinic_id,session_date,start_time,end_time,slot_minutes,capacity_total,online_quota) VALUES ('{old}','{s.DoctorId}','{app.Branch1}','{s.ClinicId}',CURRENT_DATE-1,'00:00','23:59',10,100,60); UPDATE queue_tickets SET session_id='{old}',status='in_room',started_at=now()-interval '1 day' WHERE ticket_id='{ticket}'");
        var live=await Api.GetAsync(s.Doctor,$"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Ok(live); Assert.Equal(old,Id(live.Body,"session_id")); Assert.Equal("in_room",S(live.Body!["items"]![0],"status"));
        var nextPatient=await new QueueTests(app).PatientAsync(s.Reception);
        var next=await Api.PostAsync(s.Reception,"/api/v1/queue/check-in",new {patient_id=nextPatient,doctor_id=s.DoctorId},Guid.NewGuid());
        Ok(next,201); var nextTicket=Id(next.Body,"ticket_id");
        Assert.NotEqual(old,await app.ScalarAsync<Guid>($"SELECT session_id FROM queue_tickets WHERE ticket_id='{nextTicket}'"));
        Ok(await Api.PostAsync(s.Reception,$"/api/v1/queue/tickets/{nextTicket}/emergency",new {reason="اختبار بدء متزامن مع معاينة معلقة"}));
        Assert.Equal("DOCTOR_BUSY",(await Api.PostAsync(s.Doctor,$"/api/v1/queue/tickets/{nextTicket}/start")).Code);
        Ok(await Api.PostAsync(s.Doctor,$"/api/v1/queue/tickets/{ticket}/complete",new {call_next=false,expect_return=false}));
        live=await Api.GetAsync(s.Doctor,$"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Assert.NotEqual(old,Id(live.Body,"session_id"));
        Ok(await Api.PostAsync(s.Doctor,$"/api/v1/queue/tickets/{nextTicket}/start"));
        Ok(await Api.PostAsync(s.Doctor,$"/api/v1/queue/tickets/{nextTicket}/complete",new {call_next=false,expect_return=false}));
    }
}
