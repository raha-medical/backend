using AlRahma.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AlRahma.Api.Infrastructure;

/// <summary>من عليه تغيير كلمة المرور لا يستخدم أي endpoint عدا /auth/* حتى يغيرها.</summary>
public class PasswordChangeRequiredFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        if (http.User.FindFirst(AppClaims.MustChangePassword) is null) return;
        if (http.Request.Path.StartsWithSegments("/api/v1/auth")) return;

        context.Result = new ObjectResult(Problems.Create(http, 403, "PASSWORD_CHANGE_REQUIRED",
            "يجب تغيير كلمة المرور المؤقتة أولاً", "استخدم POST /auth/change-password ثم /auth/refresh"))
        { StatusCode = 403, ContentTypes = { "application/problem+json" } };
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}

/// <summary>يظهر هيدر Idempotency-Key في Swagger على العمليات التي تتطلبه.</summary>
public class IdempotencyHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var attr = context.MethodInfo.GetCustomAttributes(typeof(IdempotentAttribute), false).FirstOrDefault();
        if (attr is null) return;
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = IdempotentAttribute.Header,
            In = ParameterLocation.Header,
            Required = true,
            Schema = new OpenApiSchema { Type = "string", Format = "uuid" },
            Description = "UUID جديد لكل عملية؛ إعادة الإرسال بنفس القيمة آمنة",
        });
    }
}
