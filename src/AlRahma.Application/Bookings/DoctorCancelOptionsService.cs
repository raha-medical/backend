using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.PatientApp;
using AlRahma.Application.PatientApp.Gateways;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Bookings;

public record CancelOptionDto(string Code, string LabelAr, bool Available, string? Note)
{
    /// <summary>rebook فقط: أقرب وجبة متاحة للطبيب نفسه.</summary>
    public Guid? SessionId { get; init; }
    public DateOnly? Date { get; init; }
    public string? Start { get; init; }
    public DateTimeOffset? EstimatedTime { get; init; }
}
/// <summary>resolution: null = لم يختر بعد (المبلغ رصيد في المحفظة)، وإلا credit | rebooked | refunded.</summary>
public record CancelOptionsDto(Guid BookingId, bool DoctorCancelled, long Amount, string? Resolution, string? ResolutionAr, Guid? RebookedBookingId,
    string DoctorName, IReadOnlyList<CancelOptionDto> Options);
public record CancelChoiceRequest(string? Choice, Guid? SessionId);
public record CancelChoiceResponse(Guid BookingId, string Resolution, string ResolutionAr, long Amount, long CreditBalance, BookingDto? NewBooking);

/// <summary>
/// 5.1-ب: حين يلغي الطبيب جدوله (أو يُغلق الفرع أو العيادة) يصبح المدفوع رصيداً تلقائياً كما كان، ثم يُعرض على المراجع:
/// (1) إعادة الحجز على أقرب موعد للطبيب نفسه يُدفع من الرصيد، (2) إبقاء المبلغ رصيداً في المحفظة، (3) الاسترجاع إلى وسيلة الدفع
/// الإلكتروني عبر البوابة (RefundAsync). اختيار واحد لكل حجز.
/// </summary>
public class DoctorCancelOptionsService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, PatientPortalService portal,
    BookingService bookings, PaymentGatewayRegistry gateways, Notifications.NotificationService notifications)
{
    public const string Rebook = "rebook";
    public const string Credit = "credit";
    public const string Refund = "refund";
    public static readonly string[] Choices = [Rebook, Credit, Refund];
    private const int SearchDays = 30;

    public static string ResolutionAr(string? r) => r switch
    {
        "credit" => "أبقيتَ المبلغ رصيداً في محفظتك",
        "rebooked" => "أُعيد حجزك على أقرب موعد",
        "refunded" => "استُرجع المبلغ إلى وسيلة الدفع",
        _ => "المبلغ رصيد في محفظتك، اختر ما تريد",
    };

    private record Context(Booking Booking, DoctorSession Session, string DoctorName, long Amount, OnlinePayment? Online);

    private async Task<Context> LoadAsync(Guid bookingId, bool tracking, CancellationToken ct)
    {
        var q = tracking ? db.Bookings : db.Bookings.AsNoTracking();
        var booking = await q.Include(b => b.Session).Include(b => b.Patient)
                          .Where(b => b.BookingId == bookingId && portal.LinkedPatientIds().Contains(b.PatientId)).FirstOrDefaultAsync(ct)
                      ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
        var doctor = await db.Doctors.AsNoTracking().Where(d => d.DoctorId == booking.Session!.DoctorId).Select(d => d.FullName).FirstAsync(ct);
        var amount = await db.PatientCredits.AsNoTracking()
            .Where(c => c.BookingId == bookingId && c.Kind == "booking_cancel_credit").SumAsync(c => (long?)c.Amount, ct) ?? 0;
        var online = await (tracking ? db.OnlinePayments : db.OnlinePayments.AsNoTracking())
            .Where(o => o.BookingId == bookingId && o.Purpose == OnlinePaymentPurposes.BookingFull && o.Status == OnlinePaymentStatuses.Succeeded)
            .FirstOrDefaultAsync(ct);
        return new Context(booking, booking.Session!, doctor, amount, online);
    }

    /// <summary>الحجز ألغاه الطبيب (أو إغلاق) والوجبة ملغاة، ودُفع فتحول رصيداً.</summary>
    private static bool DoctorCancelled(Context c) =>
        c.Booking.Status == BookingStatuses.Cancelled && c.Booking.CancelledBy != "patient" && c.Session.Status == SessionStatuses.Cancelled;

    public async Task<CancelOptionsDto> OptionsAsync(Guid bookingId, CancellationToken ct)
    {
        var c = await LoadAsync(bookingId, false, ct);
        var eligible = DoctorCancelled(c) && c.Amount > 0;
        var open = eligible && c.Booking.CancelResolution is null;
        var options = new List<CancelOptionDto>();
        if (eligible)
        {
            var next = open ? await NearestAsync(c, ct) : null;
            options.Add(new CancelOptionDto(Rebook, "إعادة الحجز على أقرب موعد للطبيب نفسه", open && next is not null,
                next is null ? "لا يوجد موعد متاح خلال 30 يوماً" : "يُدفع من رصيدك، ولا تدفع شيئاً إضافياً إلا إن زاد السعر")
            {
                SessionId = next?.SessionId, Date = next?.Date, Start = next?.Start, EstimatedTime = next?.NextEstimatedTime,
            });
            options.Add(new CancelOptionDto(Credit, "إبقاء المبلغ رصيداً في المحفظة", open, "تستخدمه لأي حجز أو زيارة لاحقة"));
            var refundable = c.Online?.ProviderReference is not null;
            options.Add(new CancelOptionDto(Refund, "الاسترجاع إلى وسيلة الدفع", open && refundable,
                refundable ? "يعود المبلغ إلى زين كاش أو كي كارد الذي دفعت به" : "متاح فقط لما دُفع إلكترونياً من التطبيق"));
        }
        return new CancelOptionsDto(c.Booking.BookingId, DoctorCancelled(c), c.Amount, c.Booking.CancelResolution,
            eligible ? ResolutionAr(c.Booking.CancelResolution) : null, c.Booking.RebookedBookingId, c.DoctorName, options);
    }

    private async Task<AvailabilityDay?> NearestAsync(Context c, CancellationToken ct)
    {
        var today = clock.BaghdadToday();
        try
        {
            var a = await bookings.AvailabilityAsync(c.Session.DoctorId, today, today.AddDays(SearchDays - 1), ct);
            return a.Days.Where(d => d.Status == SessionStatuses.Open && d.OnlineRemaining > 0 && d.SessionId != c.Session.SessionId)
                .OrderBy(d => d.Date).ThenBy(d => d.Start).FirstOrDefault();
        }
        catch (AppException)
        {
            return null; // الطبيب موقوف
        }
    }

    public async Task<CancelChoiceResponse> ChooseAsync(Guid bookingId, CancelChoiceRequest req, CancellationToken ct)
    {
        new Validator().Check(req.Choice is not null && Choices.Contains(req.Choice), "choice", string.Join(" | ", Choices)).ThrowIfInvalid();
        var c = await LoadAsync(bookingId, true, ct);
        if (!DoctorCancelled(c) || c.Amount <= 0)
            throw new AppException(409, "NO_CANCEL_OPTIONS", "هذا الحجز لم يُلغَ من الطبيب أو لم يُدفع");
        if (c.Booking.CancelResolution is not null)
            throw new AppException(409, "CHOICE_ALREADY_MADE", "اخترت مسبقاً لهذا الحجز")
                { Extensions = new Dictionary<string, object?> { ["resolution"] = c.Booking.CancelResolution } };
        var patientId = c.Booking.PatientId;
        var branchId = c.Session.BranchId;
        var now = clock.UtcNow;
        var balance = await db.PatientCredits.Where(x => x.PatientId == patientId).SumAsync(x => (long?)x.Amount, ct) ?? 0;
        if (req.Choice != Credit && balance < c.Amount)
            throw new AppException(409, "CREDIT_ALREADY_USED", "رصيد هذا الحجز استُخدم", "يمكنك إبقاؤه كما هو")
                { Extensions = new Dictionary<string, object?> { ["credit_balance"] = balance, ["amount"] = c.Amount } };

        BookingDto? created = null;
        switch (req.Choice)
        {
            case Credit:
                c.Booking.CancelResolution = "credit";
                break;
            case Refund:
                if (c.Online?.ProviderReference is null)
                    throw new AppException(422, "REFUND_METHOD_NOT_AVAILABLE", "الاسترجاع متاح فقط لما دُفع إلكترونياً من التطبيق");
                await gateways.RefundAsync(c.Online, c.Amount, ct);
                c.Online.Status = OnlinePaymentStatuses.Refunded;
                c.Booking.PaymentStatus = PaymentStatuses.Refunded;
                c.Booking.CancelResolution = "refunded";
                db.PatientCredits.Add(new PatientCreditEntry
                {
                    EntryId = UuidV7.New(), PatientId = patientId, BranchId = branchId, Amount = -c.Amount, Kind = "refunded_out",
                    BookingId = c.Booking.BookingId, OnlinePaymentId = c.Online.OnlinePaymentId, CreatedAt = now,
                });
                break;
            case Rebook:
                var target = req.SessionId ?? (await NearestAsync(c, ct))?.SessionId
                             ?? throw new AppException(409, "NO_AVAILABLE_SESSION", "لا يوجد موعد متاح للطبيب خلال 30 يوماً");
                var targetDoctor = await db.DoctorSessions.AsNoTracking().Where(s => s.SessionId == target).Select(s => (Guid?)s.DoctorId).FirstOrDefaultAsync(ct);
                if (targetDoctor != c.Session.DoctorId)
                    throw AppException.Validation(new Dictionary<string, string[]> { ["session_id"] = ["الموعد لطبيب آخر"] });
                await db.SaveChangesAsync(ct); // لا تغييرات معلقة قبل إنشاء الحجز (له معاملته)
                created = await bookings.CreateAsync(new CreateBookingRequest(null, patientId, target, "auto"), ct);
                var nb = await db.Bookings.FirstAsync(b => b.BookingId == created.BookingId, ct);
                // الرصيد ينتقل إلى الحجز الجديد عربوناً: يُطرح من المطلوب عند الوصول ويُستخدم تلقائياً عند التحصيل
                var moved = Math.Min(c.Amount, nb.Price);
                if (moved > 0)
                {
                    db.PatientCredits.Add(new PatientCreditEntry
                    {
                        EntryId = UuidV7.New(), PatientId = patientId, BranchId = branchId, Amount = -moved, Kind = "booking_rebook",
                        BookingId = c.Booking.BookingId, CreatedAt = now,
                    });
                    db.PatientCredits.Add(new PatientCreditEntry
                    {
                        EntryId = UuidV7.New(), PatientId = patientId, BranchId = nb.Session?.BranchId ?? branchId, Amount = moved, Kind = "booking_deposit",
                        BookingId = nb.BookingId, CreatedAt = now,
                    });
                    nb.PaymentStatus = PaymentStatuses.DepositPaid;
                }
                c.Booking.CancelResolution = "rebooked";
                c.Booking.RebookedBookingId = nb.BookingId;
                break;
        }
        audit.Add("BOOKING_CANCEL_CHOICE", "booking", c.Booking.BookingId, after: new
        {
            choice = req.Choice, amount = c.Amount, new_booking_id = created?.BookingId, account = current.PatientAccountId,
        });
        await db.SaveChangesAsync(ct);
        if (req.Choice == Refund)
            await notifications.NotifyPatientAsync(patientId, branchId, Notifications.NotificationTypes.PaymentRefunded, "تم إرجاع مبلغ لك",
                $"{c.Amount:N0} د.ع من حجز ألغاه الطبيب إلى وسيلة الدفع الإلكتروني", "booking", c.Booking.BookingId, ct);
        if (created is not null) created = await bookings.GetAsync(created.BookingId, ct);
        var after = await db.PatientCredits.Where(x => x.PatientId == patientId).SumAsync(x => (long?)x.Amount, ct) ?? 0;
        return new CancelChoiceResponse(c.Booking.BookingId, c.Booking.CancelResolution!, ResolutionAr(c.Booking.CancelResolution), c.Amount, after, created);
    }
}
