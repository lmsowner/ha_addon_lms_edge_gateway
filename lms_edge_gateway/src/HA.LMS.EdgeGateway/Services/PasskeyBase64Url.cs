namespace HA.LMS.EdgeGateway.Services;

public static class PasskeyBase64Url
{
    public static string Encode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var padded = value
            .Replace('-', '+')
            .Replace('_', '/');

        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
