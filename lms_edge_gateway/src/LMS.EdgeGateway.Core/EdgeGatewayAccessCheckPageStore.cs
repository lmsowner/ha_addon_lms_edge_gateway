using System.Collections.Concurrent;

namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewayAccessCheckPageStore
{
    string Store(EdgeGatewayAccessDiagnostics diagnostics, TimeSpan lifetime);

    EdgeGatewayAccessDiagnostics? TryGet(string token);

    int ClearAll();
}

public sealed class MemoryEdgeGatewayAccessCheckPageStore : IEdgeGatewayAccessCheckPageStore
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (EdgeGatewayAccessDiagnostics Diagnostics, DateTimeOffset ExpiresUtc)> entries = new();

    public string Store(EdgeGatewayAccessDiagnostics diagnostics, TimeSpan lifetime)
    {
        CleanupExpired();
        var token = Guid.NewGuid().ToString("N");
        entries[token] = (diagnostics, DateTimeOffset.UtcNow.Add(lifetime <= TimeSpan.Zero ? DefaultLifetime : lifetime));
        return token;
    }

    public EdgeGatewayAccessDiagnostics? TryGet(string token)
    {
        CleanupExpired();
        var value = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            !entries.TryGetValue(value, out var entry) ||
            entry.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return entry.Diagnostics;
    }

    public int ClearAll()
    {
        var count = entries.Count;
        entries.Clear();
        return count;
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in entries)
        {
            if (pair.Value.ExpiresUtc <= now)
            {
                entries.TryRemove(pair.Key, out _);
            }
        }
    }
}
