using AlRahma.Application.Abstractions;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using AlRahma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlRahma.Infrastructure.Services;

public class BootstrapOptions
{
    public const string Section = "Bootstrap";
    public string? BranchCode { get; set; }
    public string? BranchName { get; set; }
    public string? AdminUsername { get; set; }
    public string? AdminPassword { get; set; }
    public string AdminFullName { get; set; } = "مدير النظام";
}

/// <summary>
/// عند أول تشغيل على قاعدة فارغة: ينشئ الفرع الأول وحساب المدير العام من الإعدادات
/// (Bootstrap__AdminUsername, Bootstrap__AdminPassword ...). لا يفعل شيئاً إذا وُجد أي مستخدم.
/// </summary>
public class BootstrapService(AppDbContext db, IPasswordHasher hasher, IOptions<BootstrapOptions> options, ILogger<BootstrapService> log)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.AdminUsername) || string.IsNullOrWhiteSpace(o.AdminPassword))
            return;
        if (await db.Users.AnyAsync(ct))
            return;

        var now = DateTimeOffset.UtcNow;
        var branch = await db.Branches.OrderBy(b => b.CreatedAt).FirstOrDefaultAsync(ct);
        if (branch is null)
        {
            branch = new Branch
            {
                BranchId = UuidV7.New(),
                Code = string.IsNullOrWhiteSpace(o.BranchCode) ? "B1" : o.BranchCode.Trim(),
                Name = string.IsNullOrWhiteSpace(o.BranchName) ? "الفرع الرئيسي" : o.BranchName.Trim(),
                CreatedAt = now,
            };
            db.Branches.Add(branch);
        }

        var admin = new User
        {
            UserId = UuidV7.New(),
            Username = o.AdminUsername.Trim().ToLowerInvariant(),
            PasswordHash = hasher.Hash(o.AdminPassword),
            FullName = o.AdminFullName,
            Role = Roles.Admin,
            MustChangePassword = true,
            CreatedAt = now,
            UpdatedAt = now,
            Branches = [new UserBranch { BranchId = branch.BranchId }],
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Bootstrap: created admin '{User}' on branch {Code}. Change the password after first login.", admin.Username, branch.Code);
    }
}
