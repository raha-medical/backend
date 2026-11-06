using AlRahma.Application.Abstractions;
using AlRahma.Infrastructure.Persistence;
using AlRahma.Infrastructure.Security;
using AlRahma.Infrastructure.Services;
using AlRahma.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlRahma.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddDbContext<AppDbContext>(o => o
            .UseNpgsql(cs, n => n.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.Configure<BootstrapOptions>(config.GetSection(BootstrapOptions.Section));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<BootstrapService>();

        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();

        services.Configure<DrawerOptions>(config.GetSection(DrawerOptions.Section));
        services.PostConfigure<DrawerOptions>(o => o.CashDrawerEnabled = config.GetValue("Hardware:CashDrawerEnabled", false));
        services.AddSingleton<IDrawerTokenIssuer, DrawerTokenIssuer>();
        services.Configure<ReceiptOptions>(config.GetSection(ReceiptOptions.Section));
        services.Configure<OtpOptions>(config.GetSection(OtpOptions.Section));
        services.Configure<BackupOptions>(config.GetSection(BackupOptions.Section));
        // النسخ الأصلي (Backup:Mode=native): pg_dump + ملفات التخزين + مفتاح التوقيع، على الجهاز نفسه
        services.AddSingleton<Backups.NativeBackupExecutor>();
        services.AddSingleton<AlRahma.Application.Backups.IBackupExecutor>(sp => sp.GetRequiredService<Backups.NativeBackupExecutor>());
        services.AddSingleton<AlRahma.Application.Backups.IBackupEnvironment>(sp => sp.GetRequiredService<Backups.NativeBackupExecutor>());
        services.Configure<OnlinePaymentOptions>(config.GetSection(OnlinePaymentOptions.Section));
        // رمز QR تثبيت الحضور: مفتاح مستقل اختياري (CheckIn__SigningKey)، وإلا يُشتق من مفتاح JWT
        services.Configure<AlRahma.Application.Queue.CheckInOptions>(config.GetSection(AlRahma.Application.Queue.CheckInOptions.Section));
        services.PostConfigure<AlRahma.Application.Queue.CheckInOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.SigningKey)) o.SigningKey = "jwt:" + (config["Jwt:SigningKey"] ?? "");
        });
        // المساعد: gemini عند وجود المفتاح، وإلا المزود التجريبي الواضح (الإنتاج يرفض gemini بلا مفتاح في Program.cs)
        services.Configure<AlRahma.Application.Assistant.AssistantOptions>(config.GetSection(AlRahma.Application.Assistant.AssistantOptions.Section));
        // المفتاح: Assistant__ApiKey، وإلا متغير البيئة GEMINI_API_KEY (يمرره start-native.bat لنافذة الـ API وحدها)
        services.PostConfigure<AlRahma.Application.Assistant.AssistantOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.ApiKey))
                o.ApiKey = Environment.GetEnvironmentVariable(AlRahma.Application.Assistant.AssistantOptions.ApiKeyEnvironmentVariable)?.Trim() ?? "";
        });
        services.AddSingleton<AlRahma.Application.Assistant.IAssistantProvider>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlRahma.Application.Assistant.AssistantOptions>>();
            return options.Value.UseGemini
                ? new GeminiAssistantProvider(options, new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 5, 120)) })
                : new AlRahma.Application.Assistant.SandboxAssistantProvider();
        });
        // Push: Firebase فقط عند Push__Mode=live مع بيانات الاعتماد، وإلا تجريبي
        services.Configure<FirebasePushOptions>(config.GetSection(FirebasePushOptions.Section));
        services.AddSingleton<IPushProvider>(sp =>
        {
            var firebase = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FirebasePushOptions>>();
            return string.Equals(config["Push:Mode"], "live", StringComparison.OrdinalIgnoreCase) && firebase.Value.IsConfigured
                ? new FirebasePushProvider(firebase, new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                : new SandboxPushProvider();
        });
        services.Configure<MessagingOptions>(o =>
        {
            o.SmsMode = config["Sms:Mode"] ?? "sandbox";
            o.PushMode = config["Push:Mode"] ?? "sandbox";
        });
        return services;
    }
}
