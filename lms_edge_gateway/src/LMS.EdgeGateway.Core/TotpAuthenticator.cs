using System.Security.Cryptography;
using System.Text;

namespace LMS.EdgeGateway.Core;

internal static class TotpAuthenticator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const string Issuer = "LMS HA Add-On";
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    public static string GenerateSecret(int length = 20)
    {
        Span<byte> buffer = stackalloc byte[length];
        RandomNumberGenerator.Fill(buffer);
        return EncodeBase32(buffer);
    }

    public static string BuildOtpUri(string email, string secret)
    {
        var encodedIssuer = Uri.EscapeDataString(Issuer);
        var encodedEmail = Uri.EscapeDataString(email.Trim());
        return $"otpauth://totp/{encodedIssuer}:{encodedEmail}?secret={secret}&issuer={encodedIssuer}&digits=6&period=30";
    }

    public static string FormatManualEntryKey(string secret)
    {
        var builder = new StringBuilder(secret.Length + secret.Length / 4);
        for (var index = 0; index < secret.Length; index++)
        {
            if (index > 0 && index % 4 == 0)
            {
                builder.Append(' ');
            }

            builder.Append(secret[index]);
        }

        return builder.ToString();
    }

    public static bool ValidateCode(string secret, string otpCode, DateTimeOffset? now = null, int allowedDriftSteps = 1)
    {
        var normalizedCode = new string((otpCode ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalizedCode.Length != 6)
        {
            return false;
        }

        var secretBytes = DecodeBase32(secret);
        var current = now ?? DateTimeOffset.UtcNow;
        var stepNumber = current.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

        for (var offset = -allowedDriftSteps; offset <= allowedDriftSteps; offset++)
        {
            if (GenerateCode(secretBytes, stepNumber + offset) == normalizedCode)
            {
                return true;
            }
        }

        return false;
    }

    private static string GenerateCode(byte[] secret, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        for (var index = 7; index >= 0; index--)
        {
            counterBytes[index] = (byte)(counter & 0xff);
            counter >>= 8;
        }

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var binaryCode = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);

        var otp = binaryCode % 1_000_000;
        return otp.ToString("D6");
    }

    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder((int)Math.Ceiling(bytes.Length / 5d) * 8);
        var bitBuffer = 0;
        var bitsLeft = 0;

        foreach (var value in bytes)
        {
            bitBuffer = (bitBuffer << 8) | value;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                builder.Append(Alphabet[(bitBuffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            builder.Append(Alphabet[(bitBuffer << (5 - bitsLeft)) & 31]);
        }

        return builder.ToString();
    }

    private static byte[] DecodeBase32(string input)
    {
        var normalized = input
            .Trim()
            .TrimEnd('=')
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var bytes = new List<byte>(normalized.Length * 5 / 8);
        var bitBuffer = 0;
        var bitsLeft = 0;

        foreach (var character in normalized)
        {
            var value = Alphabet.IndexOf(character);
            if (value < 0)
            {
                throw new InvalidOperationException("The OTP secret is not valid base32.");
            }

            bitBuffer = (bitBuffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((bitBuffer >> (bitsLeft - 8)) & 0xff));
                bitsLeft -= 8;
            }
        }

        return [.. bytes];
    }
}
