using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Branch> Branches { get; }
    DbSet<Clinic> Clinics { get; }
    DbSet<ClinicClosure> ClinicClosures { get; }
    DbSet<BranchClosure> BranchClosures { get; }
    DbSet<LookupValue> LookupValues { get; }
    DbSet<SystemSetting> SystemSettings { get; }
    DbSet<User> Users { get; }
    DbSet<UserBranch> UserBranches { get; }
    DbSet<UserClinic> UserClinics { get; }
    DbSet<UserPermission> UserPermissions { get; }
    DbSet<Role> Roles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<BranchWorkingHour> BranchWorkingHours { get; }
    DbSet<Doctor> Doctors { get; }
    DbSet<Device> Devices { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Service> Services { get; }
    DbSet<DoctorCommissionRate> DoctorCommissionRates { get; }
    DbSet<DoctorScheduleTemplate> DoctorScheduleTemplates { get; }
    DbSet<DoctorSession> DoctorSessions { get; }
    DbSet<StoredFile> Files { get; }
    DbSet<Patient> Patients { get; }
    DbSet<PatientNoShow> PatientNoShows { get; }
    DbSet<PatientBanEvent> PatientBanEvents { get; }
    DbSet<PatientCreditEntry> PatientCredits { get; }
    DbSet<Booking> Bookings { get; }
    DbSet<QueueTicket> QueueTickets { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceItem> InvoiceItems { get; }
    DbSet<Shift> Shifts { get; }
    DbSet<Receipt> Receipts { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentTender> PaymentTenders { get; }
    DbSet<PaymentAllocation> PaymentAllocations { get; }
    DbSet<Refund> Refunds { get; }
    DbSet<Deferral> Deferrals { get; }
    DbSet<DeferralItem> DeferralItems { get; }
    DbSet<Expense> Expenses { get; }
    DbSet<PatientAccount> PatientAccounts { get; }
    DbSet<PatientAccountLink> PatientAccountLinks { get; }
    DbSet<OtpCode> OtpCodes { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<PushDevice> PushDevices { get; }
    DbSet<OutboundMessage> OutboundMessages { get; }
    DbSet<BackupRun> BackupRuns { get; }
    DbSet<Consultation> Consultations { get; }
    DbSet<ConsultationAddendum> ConsultationAddenda { get; }
    DbSet<Prescription> Prescriptions { get; }
    DbSet<PrescriptionLine> PrescriptionLines { get; }
    DbSet<MedicalOrder> MedicalOrders { get; }
    DbSet<Referral> Referrals { get; }
    DbSet<StaffPayPlan> StaffPayPlans { get; }
    DbSet<Payout> Payouts { get; }
    DbSet<LabTestParameter> LabTestParameters { get; }
    DbSet<LabResult> LabResults { get; }
    DbSet<LabResultValue> LabResultValues { get; }
    DbSet<RadiologyReport> RadiologyReports { get; }
    DbSet<OnlinePayment> OnlinePayments { get; }
    DbSet<MockGatewayTransaction> MockGatewayTransactions { get; }
    DbSet<StaffLiability> StaffLiabilities { get; }
    DbSet<FaqItem> FaqItems { get; }
    DbSet<SupportRequest> SupportRequests { get; }
    DbSet<PharmacyDispenseLine> PharmacyDispenseLines { get; }
    DbSet<ExchangeRate> ExchangeRates { get; }
    DbSet<ReceiptBlock> ReceiptBlocks { get; }
    DbSet<SyncOperation> SyncOperations { get; }
    DbSet<AgentEvent> AgentEvents { get; }
    DbSet<FastForwardOffer> FastForwardOffers { get; }
    DbSet<FastForwardRecipient> FastForwardRecipients { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>يتجاهل التغييرات غير المحفوظة (بعد فشل عملية في دفعة المزامنة حتى لا تُحفظ مع التالية).</summary>
    void DiscardChanges();

    /// <summary>تنفيذ استعلام SQL يعيد قيمة منطقية واحدة (للتحقق من جداول لم تُربط بعد).</summary>
    Task<bool> ExistsAsync(FormattableString sql, CancellationToken ct = default);

    /// <summary>استعلام SQL يعيد عدداً صحيحاً واحداً (عمود باسم Value).</summary>
    Task<int> CountAsync(FormattableString sql, CancellationToken ct = default);

    /// <summary>تنفيذ أمر SQL مباشرة (خارج الـ change tracker) وإرجاع عدد الصفوف المتأثرة.</summary>
    Task<int> ExecuteAsync(FormattableString sql, CancellationToken ct = default);

    /// <summary>استعلام SQL يعيد قيمة واحدة من النوع T (عمود باسم Value).</summary>
    Task<T> ScalarAsync<T>(FormattableString sql, CancellationToken ct = default);

    /// <summary>استعلام SQL يعيد قائمة من النوع T (لجمل UPDATE ... RETURNING).</summary>
    Task<List<T>> QueryAsync<T>(FormattableString sql, CancellationToken ct = default);

    /// <summary>معاملة صريحة (للأقفال مثل SELECT ... FOR UPDATE). إذا كانت هناك معاملة مفتوحة يعيد null.</summary>
    Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public record IssuedTokens(string AccessToken, int AccessExpiresIn, string RefreshToken, int RefreshExpiresIn);

public interface ITokenService
{
    /// <summary>توكن وصول قصير العمر. sessionId = معرف الـ refresh token الذي صدر معه (لتسجيل الخروج).</summary>
    (string Token, int ExpiresIn) CreateAccessToken(User user, Guid branchId, IReadOnlyCollection<string> permissions, Guid sessionId);
    string CreateRefreshTokenValue();
    string HashRefreshToken(string value);
    TimeSpan RefreshLifetime { get; }

    /// <summary>توكن تطبيق المريض (جمهور منفصل لا يصل إلى endpoints الموظفين).</summary>
    (string Token, int ExpiresIn) CreatePatientAccessToken(Guid patientAccountId, string phoneNumber, Guid sessionId);
    TimeSpan PatientRefreshLifetime { get; }
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    string Role { get; }
    Guid BranchId { get; }
    Guid? SessionId { get; }
    IReadOnlySet<string> Permissions { get; }
    bool Has(string permission) => Permissions.Contains(permission);
    string? IpAddress { get; }

    /// <summary>توكن تطبيق المريض: UserId هنا هو patient_account_id، ولا يوجد فرع.</summary>
    bool IsPatient => Role == Domain.Common.Roles.Patient;
    Guid? PatientAccountId => IsAuthenticated && IsPatient ? UserId : null;
    /// <summary>معرف الموظف لأعمدة created_by وما شابه، أو null للمريض أو الطلب العام.</summary>
    Guid? StaffUserId => IsAuthenticated && !IsPatient && UserId != Guid.Empty ? UserId : null;
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public static class ClockExtensions
{
    /// <summary>توقيت بغداد ثابت UTC+3 (لا يوجد توقيت صيفي في العراق).</summary>
    public static readonly TimeSpan BaghdadOffset = TimeSpan.FromHours(3);

    public static DateTimeOffset BaghdadNow(this IClock clock) => clock.UtcNow.ToOffset(BaghdadOffset);

    public static DateOnly BaghdadToday(this IClock clock) => DateOnly.FromDateTime(clock.BaghdadNow().DateTime);

    /// <summary>تحويل تاريخ + وقت محلي (بغداد) إلى لحظة UTC.</summary>
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time) =>
        new DateTimeOffset(date.ToDateTime(time), BaghdadOffset).ToUniversalTime();
}

public interface IAuditLogger
{
    /// <summary>يضيف سجل تدقيق إلى نفس الـ SaveChanges الخاص بالعملية.</summary>
    void Add(string action, string entityType, Guid? entityId, object? before = null, object? after = null, string? reason = null);
}

/// <summary>تخزين الكائنات (MinIO محلياً في كل فرع). الروابط مؤقتة وموقعة، والملف يُرفع من المتصفح مباشرة.</summary>
public interface IObjectStorage
{
    string Bucket { get; }
    string PresignUpload(string objectKey, string contentType, TimeSpan expiresIn);
    string PresignDownload(string objectKey, TimeSpan expiresIn);
    /// <summary>حجم الكائن بالبايت، أو null إذا لم يُرفع.</summary>
    Task<long?> GetSizeAsync(string objectKey, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);
}

/// <summary>
/// توكن فتح الدرج: JWT قصير العمر (60 ثانية) بتوقيع ES256. الوكيل المحلي يتحقق منه بالمفتاح العام
/// (GET /api/v1/agent/drawer-public-key) ولا يفتح الدرج بدونه.
/// </summary>
public interface IDrawerTokenIssuer
{
    /// <summary>قرار المالك (7.1-أ): درج النقود موقوف، والطابعة فقط. Hardware__CashDrawerEnabled=true يعيده. موقوف = لا توكن ولا open_drawer.</summary>
    bool Enabled { get; }
    string Issue(string receiptNumber, Guid shiftId, Guid? deviceId);
    /// <summary>توقيع ES256 (DER بصيغة base64) لبيانات يتحقق منها الوكيل بنفس المفتاح العام، مثل كتلة الإيصالات.</summary>
    string Sign(string payload);
    string PublicKeyPem { get; }
    /// <summary>أول 16 حرفاً (hex) من SHA-256 للمفتاح العام.</summary>
    string PublicKeyId { get; }
}

/// <summary>إعدادات الإيصال المطبوع.</summary>
public class ReceiptOptions
{
    public const string Section = "Receipts";
    /// <summary>بادئة رابط الـ QR على الإيصال، يُلحق بها رمز التحقق: صفحة التحقق العامة في الواجهة. في الإنتاج رابط كامل مثل https://alrahma.iq/verify/</summary>
    public string VerifyBaseUrl { get; set; } = "/verify/";
    /// <summary>اسم المجمع كما يُطبع على الإيصال.</summary>
    public string ComplexName { get; set; } = "مجمع الرحمة الطبي";
    public string Footer { get; set; } = "لا يعتد بأي إيصال يدوي";
    /// <summary>7.1-ج: شعار المجمع على الوصل (SVG أو PNG). نسبي = من أصل الواجهة. فارغ = بلا شعار.</summary>
    public string LogoUrl { get; set; } = "/rahma-mark.svg";
}

/// <summary>رمز الدخول لتطبيق المريض: عشوائي دائماً. sandbox: الرسالة تذهب لصندوق الرسائل التجريبي. sms: تُرسل فعلاً.</summary>
public class OtpOptions
{
    public const string Section = "Otp";
    public string Mode { get; set; } = "sandbox";
    /// <summary>للاختبار الآلي/التطوير فقط: يعيد الرمز العشوائي في الاستجابة (sandbox_code). مرفوض خارج Development/Testing.</summary>
    public bool ExposeCodeInResponse { get; set; }
    public bool IsSandbox => !string.Equals(Mode, "sms", StringComparison.OrdinalIgnoreCase);
}

/// <summary>مزود Push. التجريبي لا يرسل شيئاً. Firebase يعمل عند Push__Mode=live مع بيانات اعتماد الخدمة.</summary>
public interface IPushProvider
{
    string Name { get; }
    bool IsSandbox { get; }
    Task<PushSendResult> SendAsync(string token, string text, string? dataJson, CancellationToken ct);
}
public record PushSendResult(bool Ok, string? Error = null);

public class SandboxPushProvider : IPushProvider
{
    public string Name => "sandbox";
    public bool IsSandbox => true;
    public Task<PushSendResult> SendAsync(string token, string text, string? dataJson, CancellationToken ct) => Task.FromResult(new PushSendResult(true));
}

/// <summary>Push__Firebase__ProjectId و Push__Firebase__CredentialsFile (ملف حساب الخدمة، سر على السيرفر فقط).</summary>
public class FirebasePushOptions
{
    public const string Section = "Push:Firebase";
    public string ProjectId { get; set; } = "";
    public string CredentialsFile { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(CredentialsFile);
}

/// <summary>الرسائل الصادرة. sandbox: تُسجل في outbound_messages فقط (GET /sandbox/messages).</summary>
public class MessagingOptions
{
    public string SmsMode { get; set; } = "sandbox";
    public string PushMode { get; set; } = "sandbox";
}

/// <summary>الدفع الإلكتروني. sandbox: بوابة تجريبية على /sandbox/pay.</summary>
public class OnlinePaymentOptions
{
    public const string Section = "Payments";
    public string Mode { get; set; } = "sandbox";
    /// <summary>العنوان العام للـ API (لبناء redirect_url)، مثل https://api.example.iq. فارغ = رابط نسبي.</summary>
    public string PublicBaseUrl { get; set; } = "";
    public int ExpiryMinutes { get; set; } = 15;
    /// <summary>إعداد كل بوابة (3.5-ج): Payments__Providers__zaincash__Mode = mock | live | off. غير المضبوط = mock.</summary>
    public Dictionary<string, AlRahma.Application.PatientApp.Gateways.PaymentProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>C1: الإنتاج يضعها false (Program.cs) فتتعطل البوابة التجريبية وصفحتها، لأنها تسمح بتعليم أي دفعة ناجحة مجاناً.</summary>
    public bool SandboxAllowed { get; set; } = true;
    public bool IsSandbox => SandboxAllowed && string.Equals(Mode, "sandbox", StringComparison.OrdinalIgnoreCase);
    /// <summary>هل يُعرض الدفع الإلكتروني للمريض أصلاً (لا بوابة حقيقية بعد = البوابة التجريبية فقط).</summary>
    public bool OnlineAvailable => IsSandbox;
}

/// <summary>
/// النسخ الاحتياطي المجاني (pgBackRest على سيرفر قاعدة البيانات). الـ API لا ينفذ أوامر على السيرفر أبداً:
/// يقرأ سجل backup_runs ويضيف طلبات التشغيل اليدوي، وسكربت السيرفر ينفذها.
/// </summary>
public class BackupOptions
{
    public const string Section = "Backup";
    /// <summary>true بعد تشغيل حاوية النسخ (deploy/backup). مطفأ = لا تنبيهات تأخر.</summary>
    public bool Enabled { get; set; }
    /// <summary>الموقع الثاني (قرص آخر، سيرفر فرع، أو S3 متوافق) مفعّل في pgbackrest.conf.</summary>
    public bool Repo2Enabled { get; set; }
    /// <summary>ساعات بلا نسخة ناجحة قبل تنبيه المدير.</summary>
    public int MaxHoursWithoutSuccess { get; set; } = 26;
    public BackupFilesOptions Files { get; set; } = new();
}

public class BackupFilesOptions
{
    /// <summary>off (افتراضي) | mirror: نسخ ملفات MinIO للموقع الثاني وتعبئة files.s3_synced_at.</summary>
    public string Mode { get; set; } = "off";
    /// <summary>ساعات تأخر نسخ الملفات قبل تنبيه المدير.</summary>
    public int MaxLagHours { get; set; } = 24;
    public bool IsOn => !string.Equals(Mode, "off", StringComparison.OrdinalIgnoreCase);
}
