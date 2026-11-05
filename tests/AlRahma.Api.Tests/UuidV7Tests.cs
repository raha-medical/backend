using AlRahma.Domain.Common;
using Xunit;

namespace AlRahma.Api.Tests;

public class UuidV7Tests
{
    [Fact]
    public void Generates_version_7_sorted_by_time()
    {
        var a = UuidV7.New(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var b = UuidV7.New(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_001));
        Assert.Equal('7', a.ToString()[14]);
        Assert.Contains(a.ToString()[19], "89ab");
        Assert.True(string.CompareOrdinal(a.ToString(), b.ToString()) < 0);
    }

    [Fact]
    public void Password_hash_roundtrip()
    {
        var h = new AlRahma.Infrastructure.Security.Argon2PasswordHasher();
        var hash = h.Hash("Some-Password-1");
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", hash);
        Assert.True(h.Verify("Some-Password-1", hash));
        Assert.False(h.Verify("Some-Password-2", hash));
        Assert.False(h.Verify("x", "not-a-hash"));
    }
}
