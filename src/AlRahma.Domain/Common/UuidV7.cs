using System.Security.Cryptography;

namespace AlRahma.Domain.Common;

/// <summary>توليد UUID v7 (مرتب زمنياً) - .NET 8 لا يحتوي Guid.CreateVersion7.</summary>
public static class UuidV7
{
    public static Guid New() => New(DateTimeOffset.UtcNow);

    public static Guid New(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        long ms = timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true);
    }
}
