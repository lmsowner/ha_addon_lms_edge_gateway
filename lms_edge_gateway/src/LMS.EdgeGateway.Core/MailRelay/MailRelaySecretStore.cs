namespace LMS.EdgeGateway.Core;

public sealed class MailRelaySecretStore(
    MailRelayPaths paths,
    IEdgeGatewaySecretProtector protector) : IMailRelaySecretStore
{
    public async Task<string> SaveAsync(string name, string secret, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.SecretsDirectory);
        var safeName = Sanitize(name);
        var path = Path.Combine(paths.SecretsDirectory, safeName);
        await File.WriteAllTextAsync(path, protector.Protect(secret), cancellationToken);
        return safeName;
    }

    public async Task<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var path = Path.Combine(paths.SecretsDirectory, Sanitize(reference));
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedSecret = await File.ReadAllTextAsync(path, cancellationToken);
        return string.IsNullOrWhiteSpace(protectedSecret) ? null : protector.Unprotect(protectedSecret);
    }

    public Task DeleteAsync(string? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reference))
        {
            return Task.CompletedTask;
        }

        var path = Path.Combine(paths.SecretsDirectory, Sanitize(reference));
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static string Sanitize(string name)
    {
        var trimmed = name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '-');
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "secret" : trimmed;
    }
}
