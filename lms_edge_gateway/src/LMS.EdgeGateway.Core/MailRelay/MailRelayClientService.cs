using System.Text;

namespace LMS.EdgeGateway.Core;

public sealed class MailRelayClientService(
    IMailRelayStore store,
    IMailRelayHostCommand hostCommand,
    MailRelayPaths paths) : IMailRelayClientService
{
    public string GeneratePassword() => MailRelayProvisioningService.GeneratePassword();

    public async Task<MailRelayClientSaveResult> SaveAsync(
        MailRelayClientSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var configuration = await store.GetConfigurationAsync(cancellationToken);
        if (configuration?.Enabled != true)
        {
            return Failed("Mail Relay must be running before SMTP users can be changed.");
        }

        var clients = await store.ListClientsAsync(cancellationToken);
        var domains = await store.ListDomainsAsync(cancellationToken);
        var existing = request.ClientId is { } clientId
            ? clients.FirstOrDefault(item => item.Id == clientId)
            : null;
        if (request.ClientId is not null && existing is null)
        {
            return Failed("The SMTP user no longer exists. Refresh Mail Relay and try again.");
        }

        var name = request.Name?.Trim() ?? string.Empty;
        var username = request.Username?.Trim().ToLowerInvariant() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        if (name.Length is < 1 or > 80)
        {
            return Failed("Enter a name up to 80 characters.");
        }

        if (!MailRelayProvisioningService.IsValidClientUsername(username))
        {
            return Failed("SMTP username must start with a letter or number and contain only letters, numbers, dots, underscores and hyphens.");
        }

        if (existing is not null && !existing.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
        {
            return Failed("SMTP usernames cannot be renamed. Create a new user instead.");
        }

        if (clients.Any(item => item.Id != existing?.Id && item.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
        {
            return Failed("That SMTP username already exists.");
        }

        var passwordChanged = !string.IsNullOrEmpty(password);
        if (existing is null && !passwordChanged)
        {
            return Failed("Enter or generate a password for the new SMTP user.");
        }

        if (passwordChanged && (password.Length is < 16 or > 256 || password.IndexOfAny(['\r', '\n', '\0']) >= 0))
        {
            return Failed("SMTP passwords must be between 16 and 256 characters and cannot contain line breaks.");
        }

        var allowedDomains = domains
            .Where(item => item.Enabled && request.AllowedDomainIds.Contains(item.Id))
            .Select(item => item.DomainName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowedDomains.Length == 0)
        {
            return Failed("Choose at least one configured sending domain.");
        }

        var now = DateTimeOffset.UtcNow;
        var candidate = existing is null
            ? new MailRelayClient(
                Guid.NewGuid(),
                configuration.Id,
                name,
                username,
                MailRelayProvisioningService.HashCredentialPassword(password),
                true,
                allowedDomains,
                [],
                configuration.DefaultMessagesPerMinute,
                configuration.DefaultMessagesPerDay,
                string.Empty,
                now,
                now,
                null)
            : existing with
            {
                Name = name,
                PasswordHash = passwordChanged
                    ? MailRelayProvisioningService.HashCredentialPassword(password)
                    : existing.PasswordHash,
                AllowedSenderDomains = allowedDomains,
                UpdatedUtc = now
            };

        var allClients = clients.Where(item => item.Id != candidate.Id).Append(candidate).ToArray();
        var senderLoginMap = MailRelayProvisioningService.BuildPostfixSenderLoginMaps(allClients, configuration.RelayHostname);

        try
        {
            Directory.CreateDirectory(paths.ConfigDirectory);
            var mapPath = Path.Combine(paths.ConfigDirectory, "sender_login_maps");
            await File.WriteAllTextAsync(mapPath, senderLoginMap.EndsWith('\n') ? senderLoginMap : senderLoginMap + "\n", cancellationToken);
            await RunRequiredAsync("chmod", ["0644", mapPath], cancellationToken);

            if (File.Exists(paths.ApplyScriptPath))
            {
                await RunRequiredAsync(paths.ApplyScriptPath, [], cancellationToken);
            }

            if (passwordChanged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths.SaslDatabasePath)!);
                await RunRequiredAsync(
                    "saslpasswd2",
                    ["-p", "-c", "-f", paths.SaslDatabasePath, "-u", configuration.RelayHostname, username],
                    cancellationToken,
                    Encoding.UTF8.GetBytes(password + "\n"));
                await RunRequiredAsync("chmod", ["0640", paths.SaslDatabasePath], cancellationToken);
            }

            await store.SaveClientAsync(candidate, cancellationToken);
            return new MailRelayClientSaveResult(
                true,
                candidate,
                passwordChanged,
                existing is null
                    ? $"SMTP user {username} was added to Postfix."
                    : passwordChanged
                        ? $"SMTP user {username} and its password were updated."
                        : $"SMTP user {username} sender permissions were updated.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed($"SMTP user could not be saved: {FirstUsefulLine(exception.Message)}");
        }
    }

    private async Task RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        byte[]? standardInput = null)
    {
        var result = await hostCommand.RunAsync(
            fileName,
            arguments,
            cancellationToken,
            standardInput: standardInput,
            timeout: TimeSpan.FromSeconds(30));
        if (!result.Succeeded && result.ExitCode != 127)
        {
            throw new InvalidOperationException($"{fileName} failed: {FirstUsefulLine(result.StandardError, result.StandardOutput)}");
        }

        if (result.ExitCode == 127)
        {
            throw new InvalidOperationException("Mail Relay user changes require the Home Assistant add-on image, where Postfix and SASL are installed.");
        }
    }

    private static MailRelayClientSaveResult Failed(string message) => new(false, null, false, message);

    private static string FirstUsefulLine(params string[] values) => values
        .SelectMany(value => (value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .FirstOrDefault() ?? "The operation did not return an error message.";
}
