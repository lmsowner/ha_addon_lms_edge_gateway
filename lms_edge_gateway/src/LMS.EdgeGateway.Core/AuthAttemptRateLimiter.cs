using System.Collections.Concurrent;
using System.Net;

namespace LMS.EdgeGateway.Core;

public interface IAuthAttemptRateLimiter
{
    bool IsLimited(string key);

    void RecordFailure(string key);

    void RecordSuccess(string key);
}

public class AuthAttemptRateLimiter : IAuthAttemptRateLimiter
{
    private readonly ConcurrentDictionary<string, AttemptWindow> windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly int maxAttempts;
    private readonly TimeSpan window;

    public AuthAttemptRateLimiter(int maxAttempts = 8, TimeSpan? window = null)
    {
        this.maxAttempts = Math.Clamp(maxAttempts, 3, 50);
        this.window = window ?? TimeSpan.FromMinutes(15);
    }

    public bool IsLimited(string key)
    {
        var normalized = Normalize(key);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        Cleanup();
        return windows.TryGetValue(normalized, out var attempt) &&
               attempt.Count >= maxAttempts &&
               attempt.ExpiresUtc > DateTimeOffset.UtcNow;
    }

    public void RecordFailure(string key)
    {
        var normalized = Normalize(key);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        windows.AddOrUpdate(
            normalized,
            _ => new AttemptWindow(1, now.Add(window)),
            (_, existing) => existing.ExpiresUtc <= now
                ? new AttemptWindow(1, now.Add(window))
                : existing with { Count = existing.Count + 1 });
    }

    public void RecordSuccess(string key)
    {
        var normalized = Normalize(key);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            windows.TryRemove(normalized, out _);
        }
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in windows)
        {
            if (pair.Value.ExpiresUtc <= now)
            {
                windows.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string Normalize(string? key) =>
        string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim().ToLowerInvariant();

    private sealed record AttemptWindow(int Count, DateTimeOffset ExpiresUtc);
}

public sealed class EmailOtpSendRateLimiter() : AuthAttemptRateLimiter(3, TimeSpan.FromMinutes(10));

public static class AuthClientAddress
{
    public static string Resolve(string connectingIp, string forwardedFor, IPAddress? remoteIpAddress)
    {
        if (EdgeGatewayIpAddress.TryCanonicalize(connectingIp, out var cloudflare))
        {
            return cloudflare.ToString();
        }

        var firstForwarded = (forwardedFor ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (EdgeGatewayIpAddress.TryCanonicalize(firstForwarded, out var forwarded))
        {
            return forwarded.ToString();
        }

        return remoteIpAddress is null
            ? string.Empty
            : EdgeGatewayIpAddress.Canonicalize(remoteIpAddress).ToString();
    }
}
