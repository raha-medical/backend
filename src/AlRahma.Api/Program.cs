using System.Text.Json;
using AlRahma.Api.Infrastructure;
using AlRahma.Api.Realtime;
using AlRahma.Application.Common;
using AlRahma.Application;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Auth;
using AlRahma.Domain.Common;
using AlRahma.Infrastructure;
using AlRahma.Infrastructure.Persistence;
using AlRahma.Infrastructure.Security;
using AlRahma.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// C1: حماية الإنتاج من إعدادات الاختبار
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
{
    if (config.GetValue<bool>("Otp:ExposeCodeInResponse"))
        throw new InvalidOperationException("Otp:ExposeCodeInResponse مسموح في Development/Testing فقط.");
    // مزود SMS مؤجل لما بعد التسليم: الوضع التجريبي آمن (رمز عشوائي لا يُعاد في الاستجابة) لكنه لا يوصل الرمز للمريض
    if (builder.Environment.IsProduction() && !string.Equals(config["Otp:Mode"], "sms", StringComparison.OrdinalIgnoreCase))
        Console.Error.WriteLine("تحذير: Otp:Mode تجريبي في الإنتاج — رموز دخول المرضى تظهر فقط في «الرسائل التجريبية» لدى الإدارة.");
    if (builder.Environment.IsProduction())
    {
        // البوابة التجريبية تسمح لأي شخص بتعليم دفعة ناجحة: تُعطل في الإنتاج حتى تُربط بوابة حقيقية (يُدفع في الاستقبال)
        config["Payments:SandboxAllowed"] = "false";
        var jwtKey = config["Jwt:SigningKey"] ?? "";
        if (jwtKey.Length < 32 || jwtKey.Contains("change-me", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("الإنتاج يتطلب Jwt__SigningKey سرياً بطول 32 حرفاً على الأقل (لا القيمة التجريبية).");
        // المساعد: لا تشغيل صامت بمزود تجريبي عند طلب gemini بلا مفتاح
        if (string.Equals(config["Assistant:Provider"], "gemini", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(config["Assistant:ApiKey"]))
            throw new InvalidOperationException("Assistant__Provider = gemini يتطلب Assistant__ApiKey في الإنتاج.");
        if (!string.Equals(config["Assistant:Provider"], "gemini", StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine("تحذير: المساعد الذكي بالمزود التجريبي (قواعد بسيطة) في الإنتاج.");
        if ((config["ConnectionStrings:Default"] ?? "").Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("كلمة مرور قاعدة البيانات ما زالت CHANGE_ME.");
        if (string.Equals(config["Bootstrap:AdminPassword"], "Admin-Change-Me-2026", StringComparison.Ordinal))
            throw new InvalidOperationException("غيّر Bootstrap__AdminPassword قبل التشغيل في الإنتاج.");
    }
}

builder.Services.AddApplication();
builder.Services.AddInfrastructure(config);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
// الصلاحيات والدور من قاعدة البيانات في كل طلب (الترحيل 009)، لا من التوكن فقط
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, PermissionClaimsTransformation>();
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddSingleton<IRealtime, SignalRRealtime>();
if (config.GetValue("Jobs:Enabled", true))
{
    builder.Services.AddHostedService<ScheduledJobsWorker>();
    // منفذ النسخ الاحتياطي كل دقيقة في عامل مستقل حتى لا تؤخر نسخة طويلة التذكيرات والمهام الأخرى
    builder.Services.AddHostedService<BackupWorker>();
}

builder.Services
    .AddControllers(o => o.Filters.Add<PasswordChangeRequiredFilter>())
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
    })
    .ConfigureApiBehaviorOptions(o =>
    {
        // أخطاء قراءة JSON أو أنواع الحقول بنفس صيغة الأخطاء الموحدة
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var errors = ctx.ModelState.Where(e => e.Value?.Errors.Count > 0)
                .ToDictionary(
                    e => JsonNamingPolicy.SnakeCaseLower.ConvertName(e.Key.TrimStart('$', '.')),
                    e => e.Value!.Errors.Select(x => string.IsNullOrEmpty(x.ErrorMessage) ? "قيمة غير صالحة" : x.ErrorMessage).ToArray());
            var p = Problems.Create(ctx.HttpContext, 400, "VALIDATION_ERROR", "خطأ في البيانات المرسلة", errors: errors);
            return new ObjectResult(p) { StatusCode = 400, ContentTypes = { "application/problem+json" } };
        };
    });

// المصادقة JWT
var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
// مخططان: Bearer للموظفين، و Patient لتطبيق المريض (جمهور مختلف). توكن المريض لا يمر على endpoints الموظفين.
void ConfigureBearer(JwtBearerOptions o, string audience)
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = audience,
        IssuerSigningKey = JwtTokenService.SigningKey(jwt),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        RoleClaimType = AppClaims.Role,
        NameClaimType = "name",
    };
    var staff = audience == jwt.Audience;
    o.Events = new JwtBearerEvents
    {
        // موظف عُطّل أو أُزيل من فرع التوكن: التوكن يتوقف فوراً (401) دون انتظار انتهائه (عزل الفروع، 0.11 في العقد)
        OnTokenValidated = async ctx =>
        {
            if (!staff || ctx.Principal is null) return;
            if (!Guid.TryParse(ctx.Principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value, out var userId)) return;
            Guid.TryParse(ctx.Principal.FindFirst(AppClaims.BranchId)?.Value, out var branchId);
            var sp = ctx.HttpContext.RequestServices;
            var access = await sp.GetRequiredService<AlRahma.Application.Security.AccessCache>()
                .UserAsync(sp.GetRequiredService<IAppDbContext>(), userId, ctx.HttpContext.RequestAborted);
            if (access is null || !access.IsActive) ctx.Fail("user disabled");
            else if (access.Role != Roles.Admin && !access.BranchIds.Contains(branchId)) ctx.Fail("branch not assigned");
        },
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            if (ctx.Response.HasStarted) return; // endpoint بمخططين: يكتب الأول فقط
            var expired = ctx.AuthenticateFailure is SecurityTokenExpiredException;
            await Problems.WriteAsync(ctx.HttpContext, 401,
                expired ? "TOKEN_EXPIRED" : "UNAUTHORIZED",
                expired ? "انتهى التوكن، استخدم refresh" : "يلزم تسجيل الدخول");
        },
        OnForbidden = ctx => ctx.Response.HasStarted
            ? Task.CompletedTask
            : Problems.WriteAsync(ctx.HttpContext, 403, "FORBIDDEN", "لا تملك صلاحية لهذه العملية"),
        // SignalR في المتصفح لا يرسل ترويسة Authorization، فيُمرر التوكن في الرابط
        OnMessageReceived = ctx =>
        {
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                ctx.Token = token;
            return Task.CompletedTask;
        },
    };
}
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o => ConfigureBearer(o, jwt.Audience))
    .AddJwtBearer(AuthSchemes.Patient, o => ConfigureBearer(o, jwt.PatientAudience));

builder.Services.AddAuthorization(o =>
{
    foreach (var p in Permissions.All)
        o.AddPolicy(p, pb => pb.RequireAuthenticatedUser().RequireClaim(AppClaims.Permission, p));
    // عرض الأدوار: شاشة الأدوار، ونموذج المستخدم (قائمة الأدوار المتاحة)
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.RolesRead, pb => pb.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Permission, Permissions.RolesView, Permissions.UsersView));
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.LabRead, pb => pb.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Permission, Permissions.LabWrite, Permissions.LabView));
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.RadiologyRead, pb => pb.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Permission, Permissions.RadiologyWrite, Permissions.RadiologyView));
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.PharmacyRead, pb => pb.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Permission, Permissions.PharmacyDispense, Permissions.PharmacyView));
    // قراءة الفواتير: نقاط التحصيل والمحاسبة
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.BillingRead, pb => pb.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Permission, Permissions.PaymentsCollect, Permissions.ReportsFinance, Permissions.RefundsApprove));
    // طلب استرجاع: موظف التحصيل أو المعتمد
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.RefundsRequest, pb => pb.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Permission, Permissions.PaymentsCollect, Permissions.RefundsApprove));
    // عرض استثناء: من يمنحه أو من يتابع الذمم
    // تطبيق المريض فقط
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.Patient, pb => pb.AddAuthenticationSchemes(AuthSchemes.Patient)
        .RequireAuthenticatedUser().RequireClaim(AppClaims.Role, Roles.Patient));
    // أي موظف أو مريض (الإشعارات، الأجهزة، النتائج...): الصلاحية الدقيقة تُفحص في الخدمة
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.StaffOrPatient, pb => pb
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, AuthSchemes.Patient).RequireAuthenticatedUser());
    // قراءة نتائج المختبر والأشعة: الأطباء والفنيون (والمريض عبر StaffOrPatient)
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.ClinicalRead, pb => pb.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Permission, Permissions.ClinicalWrite, Permissions.LabWrite, Permissions.RadiologyWrite));
    // الطلب المباشر من الاستقبال
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.OrdersCreate, pb => pb.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Permission, Permissions.PatientsWrite, Permissions.PaymentsCollect));
    o.AddPolicy(AlRahma.Api.Infrastructure.Policies.DeferralsRead, pb => pb.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Permission, Permissions.DeferralsGrant, Permissions.ReportsFinance));
});

var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(origins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()
    .WithExposedHeaders("Idempotent-Replayed")));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Al-Rahma Medical Complex API",
        Version = "v1",
        Description = "عقود الـ API الكاملة في docs/api_contracts_v5.md",
    });
    var scheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    o.AddSecurityDefinition("Bearer", scheme);
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = [] });
    o.OperationFilter<IdempotencyHeaderOperationFilter>();
    var xml = Path.Combine(AppContext.BaseDirectory, "AlRahma.Api.xml");
    if (File.Exists(xml)) o.IncludeXmlComments(xml);
});

var app = builder.Build();

// تهيئة: hash وهمي ثابت لمقاومة كشف وجود الحسابات بالتوقيت، وإنشاء المدير الأول إن لزم
AuthService.DummyHash.Value = app.Services.GetRequiredService<IPasswordHasher>().Hash(Guid.NewGuid().ToString());
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync();

app.UseMiddleware<ErrorHandlingMiddleware>();
app.Use((ctx, next) =>
{
    ctx.Request.EnableBuffering(); // يسمح لـ Idempotency بقراءة جسم الطلب بعد ربطه
    return next();
});

if (app.Environment.IsDevelopment() || config.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<LiveHub>("/hubs/live");

app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "db_unreachable" }, statusCode: 503))
    .AllowAnonymous()
    .ExcludeFromDescription();

app.Run();

public partial class Program;
