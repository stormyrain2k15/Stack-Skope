using System.Security.Cryptography;

namespace StackScope.Core.Transactions;

/// <summary>
/// ULID generator. 128-bit sortable ID: 48-bit ms timestamp + 80-bit
/// randomness, Crockford base-32 encoded. Used as the primary key of a
/// <see cref="InferenceTransaction"/> because it sorts by creation time,
/// which is exactly what the capture library UI wants.
/// </summary>
public static class Ulid
{
    private static readonly char[] CrockfordBase32 =
        "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

    public static string NewUlid()
    {
        Span<byte> buf = stackalloc byte[16];
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        buf[0] = (byte)((ts >> 40) & 0xFF);
        buf[1] = (byte)((ts >> 32) & 0xFF);
        buf[2] = (byte)((ts >> 24) & 0xFF);
        buf[3] = (byte)((ts >> 16) & 0xFF);
        buf[4] = (byte)((ts >> 8) & 0xFF);
        buf[5] = (byte)(ts & 0xFF);

        RandomNumberGenerator.Fill(buf[6..]);

        return Encode(buf);
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        // 128 bits → 26 Crockford base-32 chars.
        Span<char> chars = stackalloc char[26];

        // Timestamp part (10 chars from first 6 bytes / 48 bits).
        chars[0]  = CrockfordBase32[(bytes[0] & 0xE0) >> 5];
        chars[1]  = CrockfordBase32[bytes[0] & 0x1F];
        chars[2]  = CrockfordBase32[(bytes[1] & 0xF8) >> 3];
        chars[3]  = CrockfordBase32[((bytes[1] & 0x07) << 2) | ((bytes[2] & 0xC0) >> 6)];
        chars[4]  = CrockfordBase32[(bytes[2] & 0x3E) >> 1];
        chars[5]  = CrockfordBase32[((bytes[2] & 0x01) << 4) | ((bytes[3] & 0xF0) >> 4)];
        chars[6]  = CrockfordBase32[((bytes[3] & 0x0F) << 1) | ((bytes[4] & 0x80) >> 7)];
        chars[7]  = CrockfordBase32[(bytes[4] & 0x7C) >> 2];
        chars[8]  = CrockfordBase32[((bytes[4] & 0x03) << 3) | ((bytes[5] & 0xE0) >> 5)];
        chars[9]  = CrockfordBase32[bytes[5] & 0x1F];

        // Randomness part (16 chars from last 10 bytes / 80 bits).
        chars[10] = CrockfordBase32[(bytes[6] & 0xF8) >> 3];
        chars[11] = CrockfordBase32[((bytes[6] & 0x07) << 2) | ((bytes[7] & 0xC0) >> 6)];
        chars[12] = CrockfordBase32[(bytes[7] & 0x3E) >> 1];
        chars[13] = CrockfordBase32[((bytes[7] & 0x01) << 4) | ((bytes[8] & 0xF0) >> 4)];
        chars[14] = CrockfordBase32[((bytes[8] & 0x0F) << 1) | ((bytes[9] & 0x80) >> 7)];
        chars[15] = CrockfordBase32[(bytes[9] & 0x7C) >> 2];
        chars[16] = CrockfordBase32[((bytes[9] & 0x03) << 3) | ((bytes[10] & 0xE0) >> 5)];
        chars[17] = CrockfordBase32[bytes[10] & 0x1F];
        chars[18] = CrockfordBase32[(bytes[11] & 0xF8) >> 3];
        chars[19] = CrockfordBase32[((bytes[11] & 0x07) << 2) | ((bytes[12] & 0xC0) >> 6)];
        chars[20] = CrockfordBase32[(bytes[12] & 0x3E) >> 1];
        chars[21] = CrockfordBase32[((bytes[12] & 0x01) << 4) | ((bytes[13] & 0xF0) >> 4)];
        chars[22] = CrockfordBase32[((bytes[13] & 0x0F) << 1) | ((bytes[14] & 0x80) >> 7)];
        chars[23] = CrockfordBase32[(bytes[14] & 0x7C) >> 2];
        chars[24] = CrockfordBase32[((bytes[14] & 0x03) << 3) | ((bytes[15] & 0xE0) >> 5)];
        chars[25] = CrockfordBase32[bytes[15] & 0x1F];

        return new string(chars);
    }
}
