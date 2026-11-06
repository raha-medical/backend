using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Backups;
using AlRahma.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AlRahma.Infrastructure.Backups;

/// <summary>
/// النسخ الأصلي على Windows بدون Docker (Backup:Mode=native). كل نسخة مجلد واحد داخل Backup:Path:
///   db.dump        pg_dump -Fc لقاعدة البيانات، ويُتحقق منه بـ pg_restore --list
///   files\         نسخة من مجلد بيانات تخزين الملفات (SeaweedFS -dir) إن ضُبط Backup:FilesPath
///   keys\          مفتاح توقيع الإيصالات (Backup:KeyFile أو Drawer:KeyFile)
///   manifest.json  وقت النسخة والأحجام وبصمة SHA-256 للـ dump
/// كلمة مرور القاعدة تُمرر لـ pg_dump بمتغير البيئة PGPASSWORD للعملية الفرعية فقط (من ConnectionStrings:Default)،
/// ولا تُكتب في سطر الأوامر ولا في أي ملف.
/// </summary>
public sealed class NativeBackupExecutor(IOptions<BackupOptions> options, IOptions<DrawerOptions> drawer, IConfiguration config,
    ILogger<NativeBackupExecutor> log) : IBackupExecutor, IBackupEnvironment
{
    public const string Manifest = "manifest.json";
    private BackupOptions Opt => options.Value;

    public bool OffsiteAvailable => IsReachable(Opt.OffsitePath);
    public bool FilesIncluded => !string.IsNullOrWhiteSpace(Opt.FilesPath) && Directory.Exists(Opt.FilesPath);
    public bool KeyIncluded => KeyFile is { } k && File.Exists(k);

    private string? KeyFile
    {
        get
        {
            var k = !string.IsNullOrWhiteSpace(Opt.KeyFile) ? Opt.KeyFile : drawer.Value.KeyFile;
            return string.IsNullOrWhiteSpace(k) ? null : Path.GetFullPath(k);
        }
    }

    public async Task<BackupResult> RunAsync(BackupJob job, CancellationToken ct)
    {
        var csText = config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default غير مضبوط");
        var cs = new NpgsqlConnectionStringBuilder(csText);
        var stamp = job.StartedAt.ToOffset(TimeSpan.FromHours(3)).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var label = $"{Safe(cs.Database ?? "db")}_{stamp}_{(job.Trigger == "manual" ? "manual" : "daily")}";
        var root = Path.GetFullPath(Opt.Path);
        var folder = Path.Combine(root, label);
        try
        {
            Directory.CreateDirectory(folder);
            var dump = Path.Combine(folder, "db.dump");

            // 1) قاعدة البيانات
            var pgDump = FindTool("pg_dump");
            var env = new Dictionary<string, string?> { ["PGPASSWORD"] = cs.Password, ["PGCLIENTENCODING"] = "UTF8" };
            var dumpRes = await RunToolAsync(pgDump, ["-Fc", "-Z", "6", "--no-password", "-h", cs.Host ?? "localhost", "-p", cs.Port.ToString(),
                "-U", cs.Username ?? "postgres", "-d", cs.Database ?? "", "-f", dump], env, ct);
            if (dumpRes.Code != 0) return Fail(folder, $"فشل pg_dump: {Short(dumpRes.Err)}");
            var dumpBytes = new FileInfo(dump).Length;

            // 2) التحقق: الأرشيف مقروء ويحتوي بيانات الجداول
            var listRes = await RunToolAsync(FindTool("pg_restore"), ["--list", dump], null, ct);
            if (listRes.Code != 0) return Fail(folder, $"فشل التحقق pg_restore --list: {Short(listRes.Err)}");
            var tables = listRes.Out.Split('\n').Count(l => l.Contains(" TABLE DATA "));
            if (tables == 0) return Fail(folder, "التحقق: النسخة لا تحتوي بيانات جداول");

            // 3) الملفات المرفوعة (مجلد بيانات التخزين)
            long filesBytes = 0;
            var fileCount = 0;
            var skipped = new List<string>();
            if (FilesIncluded)
                (filesBytes, fileCount) = CopyTree(Opt.FilesPath!, Path.Combine(folder, "files"), skipped, ct);

            // 4) مفتاح توقيع الإيصالات
            string? keyName = null;
            if (KeyFile is { } key)
            {
                if (!File.Exists(key)) return Fail(folder, $"مفتاح توقيع الإيصالات غير موجود: {key}");
                Directory.CreateDirectory(Path.Combine(folder, "keys"));
                keyName = Path.GetFileName(key);
                CopyFileShared(key, Path.Combine(folder, "keys", keyName));
            }

            var manifest = new
            {
                run_id = job.RunId, label, trigger = job.Trigger, created_at = job.StartedAt, database = cs.Database, host = cs.Host, port = cs.Port,
                dump = new { file = "db.dump", bytes = dumpBytes, sha256 = await Sha256Async(dump, ct), table_data_entries = tables },
                files = new { included = FilesIncluded, source = Opt.FilesPath, count = fileCount, bytes = filesBytes, skipped },
                key = new { included = keyName is not null, file = keyName is null ? null : $"keys/{keyName}" },
                restore = "docs/backup_restore.md (Windows)",
            };
            await File.WriteAllTextAsync(Path.Combine(folder, Manifest),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct);
            var total = DirSize(folder);
            log.LogInformation("Backup {Label} done: dump {Dump} bytes, {Files} files ({FilesBytes} bytes)", label, dumpBytes, fileCount, filesBytes);
            return new BackupResult(true, null, folder, label, dumpBytes, filesBytes, total);
        }
        catch (OperationCanceledException)
        {
            TryDelete(folder);
            throw;
        }
        catch (Exception e)
        {
            log.LogError(e, "Backup {Label} failed", label);
            return Fail(folder, e.Message);
        }
    }

    public Task<OffsiteResult> CopyOffsiteAsync(string localPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Opt.OffsitePath)) return Task.FromResult(new OffsiteResult("off", null, null));
        if (!IsReachable(Opt.OffsitePath)) return Task.FromResult(new OffsiteResult("pending", null, "بانتظار القرص الخارجي"));
        if (!Directory.Exists(localPath)) return Task.FromResult(new OffsiteResult("failed", null, "مجلد النسخة المحلي غير موجود"));
        var target = Path.Combine(Path.GetFullPath(Opt.OffsitePath), Path.GetFileName(localPath.TrimEnd('\\', '/')));
        var tmp = target + ".partial";
        try
        {
            TryDelete(tmp);
            CopyTree(localPath, tmp, [], ct);
            TryDelete(target);
            Directory.Move(tmp, target);
            return Task.FromResult(new OffsiteResult("copied", target, null));
        }
        catch (IOException e) when (!IsReachable(Opt.OffsitePath))
        {
            log.LogWarning(e, "Offsite disk disconnected during copy");
            return Task.FromResult(new OffsiteResult("pending", null, "بانتظار القرص الخارجي"));
        }
    }

    public void DeleteBackupFolder(string path)
    {
        // حماية: لا يُحذف إلا مجلد نسخة (فيه manifest.json) داخل Backup:Path أو Backup:OffsitePath
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || !File.Exists(Path.Combine(path, Manifest))) return;
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(full);
        bool Under(string? root) => !string.IsNullOrWhiteSpace(root)
            && string.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), parent, StringComparison.OrdinalIgnoreCase);
        if (!Under(Opt.Path) && !Under(Opt.OffsitePath)) return;
        Directory.Delete(full, recursive: true);
    }

    // ------------------------------------------------------------------ أدوات
    /// <summary>Backup:PgBin، ثم أحدث PostgreSQL 17..13 في Program Files (مثل pg-env.bat)، ثم PATH.</summary>
    internal string FindTool(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        if (!string.IsNullOrWhiteSpace(Opt.PgBin))
        {
            var p = Path.Combine(Opt.PgBin, exe);
            if (File.Exists(p)) return p;
        }
        if (OperatingSystem.IsWindows())
            foreach (var v in new[] { 17, 16, 15, 14, 13 })
            {
                var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", v.ToString(), "bin", exe);
                if (File.Exists(p)) return p;
            }
        return exe;
    }

    private static async Task<(int Code, string Out, string Err)> RunToolAsync(string file, IEnumerable<string> args,
        IDictionary<string, string?>? env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception) { return (-1, "", $"لم يُعثر على {Path.GetFileName(file)} (اضبط Backup__PgBin)"); }
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* انتهت */ }
            throw;
        }
        return (p.ExitCode, await outTask, await errTask);
    }

    /// <summary>ينسخ شجرة مجلد. الملف المقفل حصرياً (مثل LOCK لقاعدة بيانات التخزين) يُتخطى ويُسجل في manifest.</summary>
    private static (long Bytes, int Count) CopyTree(string source, string target, List<string> skipped, CancellationToken ct)
    {
        long bytes = 0;
        var count = 0;
        var src = Path.GetFullPath(source);
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(src, file);
            try
            {
                bytes += CopyFileShared(file, Path.Combine(target, rel));
                count++;
            }
            catch (IOException) when (Path.GetFileName(file).Equals("LOCK", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(rel);
            }
        }
        return (bytes, count);
    }

    /// <summary>نسخ ملف مفتوح للكتابة من عملية أخرى (خادم التخزين يعمل أثناء النسخ).</summary>
    private static long CopyFileShared(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        return output.Length;
    }

    private static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        await using var s = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
    }

    private static long DirSize(string dir) => new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static bool IsReachable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full)) return true;
            // المجلد غير موجود بعد لكن القرص متصل: يُنشأ
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            Directory.CreateDirectory(full);
            return true;
        }
        catch { return false; }
    }

    private static BackupResult Fail(string folder, string error)
    {
        TryDelete(folder);
        return new BackupResult(false, error, null, null, null, null, null);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* يبقى للحذف اليدوي */ }
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

    private static string Short(string s)
    {
        s = s.Trim();
        return s.Length <= 400 ? s : s[..400] + "…";
    }
}
