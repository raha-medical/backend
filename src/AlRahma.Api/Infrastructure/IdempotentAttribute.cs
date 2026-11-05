using System.Security.Cryptography;
using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Api.Infrastructure;

/// <summary>
/// يطبق قاعدة Idempotency-Key (القسم 0.5): نفس المفتاح + نفس الطلب خلال 72 ساعة يعيد الرد الأصلي دون تنفيذ ثانٍ.
/// نفس المفتاح مع طلب مختلف = 409 IDEMPOTENCY_KEY_REUSED. الطلب الفاشل لا يُحفظ، فيمكن إعادته بنفس المفتاح.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class IdempotentAttribute(bool required = true) : Attribute, IAsyncActionFilter
{
    public const string Header = "Idempotency-Key";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var raw = http.Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(raw))
        {
            if (required)
            {
                context.Result = ProblemResult(http, 400, "IDEMPOTENCY_KEY_REQUIRED", "الهيدر Idempotency-Key مطلوب لهذه العملية");
                return;
            }
            await next();
            return;
        }
        if (!Guid.TryParse(raw, out var key))
        {
            context.Result = ProblemResult(http, 400, "VALIDATION_ERROR", "Idempotency-Key يجب أن يكون UUID");
            return;
        }

        var services = http.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();
        var current = services.GetRequiredService<ICurrentUser>();
        var clock = services.GetRequiredService<IClock>();

        var who = current.IsAuthenticated ? current.UserId.ToString() : "anon";
        var route = context.ActionDescriptor.AttributeRouteInfo?.Template ?? http.Request.Path.ToString();
        // scope بحد 100 حرف: بلا البادئة وقيود المعاملات، ومع hash إن بقي أطول
        route = System.Text.RegularExpressions.Regex.Replace(route.Replace("api/v1/", ""), @":\w+}", "}");
        var scope = $"{who}:{http.Request.Method} {route}";
        if (scope.Length > 100)
            scope = $"{who}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope)))[..40]}";
        var hash = await HashRequestAsync(http);
        var now = clock.UtcNow;

        var existing = await db.IdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.IdempotencyKey == key && r.Scope == scope);

        if (existing is not null && existing.ExpiresAt > now)
        {
            if (existing.RequestHash != hash)
            {
                context.Result = ProblemResult(http, 409, "IDEMPOTENCY_KEY_REUSED", "نفس المفتاح استُخدم مع طلب مختلف");
                return;
            }
            if (existing.ResponseStatus is { } status)
            {
                http.Response.Headers["Idempotent-Replayed"] = "true";
                context.Result = new ContentResult
                {
                    StatusCode = status,
                    Content = existing.ResponseBody,
                    ContentType = "application/json; charset=utf-8",
                };
                return;
            }
            if (now - existing.CreatedAt < StaleAfter)
            {
                context.Result = ProblemResult(http, 409, "REQUEST_IN_PROGRESS", "الطلب نفسه قيد التنفيذ، أعد المحاولة بعد لحظات");
                return;
            }
        }

        // حجز المفتاح (أو استلام حجز قديم معلق/منتهي)
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO idempotency_keys (idempotency_key, scope, request_hash, created_at, expires_at)
            VALUES ({key}, {scope}, {hash}, {now}, {now.AddHours(72)})
            ON CONFLICT (idempotency_key, scope) DO UPDATE
               SET request_hash = EXCLUDED.request_hash, created_at = EXCLUDED.created_at,
                   expires_at = EXCLUDED.expires_at, response_status = NULL, response_body = NULL
             WHERE idempotency_keys.expires_at <= {now}
                OR (idempotency_keys.response_status IS NULL AND idempotency_keys.created_at < {now - StaleAfter})");
        if (claimed == 0)
        {
            context.Result = ProblemResult(http, 409, "REQUEST_IN_PROGRESS", "الطلب نفسه قيد التنفيذ، أعد المحاولة بعد لحظات");
            return;
        }

        var executed = await next();

        var succeeded = executed.Exception is null || executed.ExceptionHandled;
        var (statusCode, body) = succeeded ? Describe(executed.Result) : (0, null);
        if (succeeded && statusCode is >= 200 and < 300)
        {
            await db.IdempotencyRecords
                .Where(r => r.IdempotencyKey == key && r.Scope == scope)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.ResponseStatus, (short)statusCode)
                    .SetProperty(r => r.ResponseBody, body));
        }
        else
        {
            await db.IdempotencyRecords
                .Where(r => r.IdempotencyKey == key && r.Scope == scope)
                .ExecuteDeleteAsync();
        }
    }

    private static (int Status, string? Body) Describe(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, JsonSerializer.Serialize(o.Value, Problems.Json)),
        StatusCodeResult s => (s.StatusCode, "null"),
        _ => (200, "null"),
    };

    private static async Task<string> HashRequestAsync(HttpContext http)
    {
        http.Request.EnableBuffering();
        http.Request.Body.Position = 0;
        var hash = await SHA256.HashDataAsync(http.Request.Body);
        http.Request.Body.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ObjectResult ProblemResult(HttpContext http, int status, string code, string title) =>
        new(Problems.Create(http, status, code, title)) { StatusCode = status, ContentTypes = { "application/problem+json" } };
}
