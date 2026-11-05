using System.Security.Cryptography;
using System.Text;
using AlRahma.Application.Abstractions;
using Konscious.Security.Cryptography;

namespace AlRahma.Infrastructure.Security;

/// <summary>
/// Argon2id بصيغة PHC: $argon2id$v=19$m=19456,t=2,p=1$&lt;salt&gt;$&lt;hash&gt; (الحد الأدنى الموصى به من OWASP).
/// المعاملات محفوظة داخل الـ hash، فيمكن رفعها لاحقاً دون كسر كلمات المرور القديمة.
/// </summary>
public class Argon2PasswordHasher : IPasswordHasher
{
    private const int MemoryKb = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, MemoryKb, Iterations, Parallelism, HashSize);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            // ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
            if (parts.Length != 6 || parts[1] != "argon2id") return false;
            var prm = parts[3].Split(',').Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1]));
            var salt = FromB64(parts[4]);
            var expected = FromB64(parts[5]);
            var actual = Compute(password, salt, prm["m"], prm["t"], prm["p"], expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKb, int iterations, int parallelism, int size)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(size);
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string s) =>
        Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
}
