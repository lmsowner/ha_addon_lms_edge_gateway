using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LMS.EdgeGateway.Core;

public sealed partial class MailRelayTestService(
    IMailRelayStore store,
    IMailRelayHostCommand hostCommand,
    MailRelayPaths paths) : IMailRelayTestService
{
    public async Task<MailRelayTestResult> SendAsync(
        MailRelayTestRequest request,
        CancellationToken cancellationToken = default)
    {
        var testedAt = DateTimeOffset.UtcNow;
        var configuration = await store.GetConfigurationAsync(cancellationToken);
        var clients = await store.ListClientsAsync(cancellationToken);
        var client = clients.FirstOrDefault(item => item.Id == request.ClientId);

        if (configuration?.Enabled != true)
        {
            return Failed(request, client, testedAt, "Mail Relay is not running. Finish or retry relay setup first.");
        }

        if (client is null || !client.Enabled)
        {
            return Failed(request, client, testedAt, "Choose an enabled Mail Relay application.");
        }

        if (!MailAddress.TryCreate(request.FromAddress?.Trim(), out var sender))
        {
            return Failed(request, client, testedAt, "Enter a valid From address.");
        }

        if (!MailAddress.TryCreate(request.RecipientAddress?.Trim(), out var recipient))
        {
            return Failed(request, client, testedAt, "Enter a valid recipient address.");
        }

        if (!client.AllowedSenderDomains.Contains(sender.Host, StringComparer.OrdinalIgnoreCase))
        {
            return Failed(
                request,
                client,
                testedAt,
                $"{client.Name} is not allowed to send as {sender.Host}. Choose an address in one of its allowed sender domains.");
        }

        var domains = await store.ListDomainsAsync(cancellationToken);
        if (!domains.Any(domain =>
                domain.Enabled &&
                domain.DomainName.Equals(sender.Host, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(domain.CurrentDkimPrivateKeySecretReference)))
        {
            return Failed(
                request,
                client,
                testedAt,
                $"{sender.Host} is not an enabled Mail Relay sending domain with a DKIM key. Configure the domain before testing it.");
        }

        var messageId = $"lms-test-{Guid.NewGuid():N}@{configuration.RelayHostname}";
        MailRelayHostCommandResult submission;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            submission = await hostCommand.RunAsync(
                "sendmail",
                ["-i", "-f", sender.Address, "--", recipient.Address],
                timeout.Token,
                standardInput: Encoding.UTF8.GetBytes(BuildInternalDiagnosticMessage(
                    configuration,
                    client,
                    sender,
                    recipient,
                    messageId)),
                timeout: TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(request, client, testedAt, "The relay did not complete the SMTP test within 30 seconds.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return Failed(request, client, testedAt, $"Mail Relay test failed: {CleanDetail(exception.Message)}");
        }

        if (submission.ExitCode == 127)
        {
            return Failed(request, client, testedAt, "Mail Relay tests require the Home Assistant add-on image, where sendmail is installed.");
        }

        if (submission.ExitCode != 0)
        {
            return Result(
                MailRelayTestStatus.Rejected,
                true,
                false,
                client,
                sender.Address,
                recipient.Address,
                null,
                null,
                FirstUsefulLine(submission.StandardError, submission.StandardOutput),
                "The selected SMTP user and sender policy are valid, but the relay rejected the LMS internal test message.",
                testedAt);
        }

        var queueId = await FindQueueIdAsync(messageId, cancellationToken);
        await store.SaveClientAsync(client with { LastUsedUtc = testedAt, UpdatedUtc = testedAt }, cancellationToken);
        var delivery = await InspectDeliveryAsync(queueId, cancellationToken);
        var summary = delivery.Status switch
        {
            MailRelayTestStatus.Sent => "The selected user policy passed, Postfix accepted the message, and the destination mail server accepted delivery.",
            MailRelayTestStatus.Deferred when IsResolverFailure(delivery.Detail) => "The selected user policy passed and Postfix accepted the message, but the relay could not resolve the recipient domain.",
            MailRelayTestStatus.Deferred => "The selected user policy and relay are working, but Internet delivery was deferred. The diagnostic below is the reason returned by Postfix or the destination server.",
            MailRelayTestStatus.Bounced => "The selected user policy and relay accepted the message, but downstream delivery bounced.",
            _ => "The selected user policy passed and Postfix accepted the message. It is still queued, so inbox delivery is not yet known."
        };

        return Result(
            delivery.Status,
            true,
            true,
            client,
            sender.Address,
            recipient.Address,
            queueId,
            delivery.DestinationServer,
            string.IsNullOrWhiteSpace(delivery.Detail)
                ? FirstUsefulLine(submission.StandardOutput, "Submitted through the LMS internal relay test path.")
                : delivery.Detail,
            summary,
            testedAt,
            dmarcIdentityAligned: true);
    }

    private async Task<string?> FindQueueIdAsync(string messageId, CancellationToken cancellationToken)
    {
        var delays = new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(600) };
        foreach (var delay in delays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var logs = File.Exists(paths.MailLogPath)
                ? await File.ReadAllTextAsync(paths.MailLogPath, cancellationToken)
                : string.Empty;
            var queueId = ParseQueueIdForMessage(logs, messageId);
            if (!string.IsNullOrWhiteSpace(queueId))
            {
                return queueId;
            }
        }

        return null;
    }

    private async Task<MailRelayDeliveryEvidence> InspectDeliveryAsync(
        string? queueId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queueId))
        {
            return new MailRelayDeliveryEvidence(MailRelayTestStatus.Queued, null, "Postfix accepted the message but did not return a queue ID.");
        }

        var delays = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) };
        MailRelayDeliveryEvidence? lastQueueEvidence = null;
        foreach (var delay in delays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var logs = File.Exists(paths.MailLogPath)
                ? await File.ReadAllTextAsync(paths.MailLogPath, cancellationToken)
                : string.Empty;
            var logEvidence = ParseLogEvidence(logs);
            if (logEvidence?.Status is MailRelayTestStatus.Sent or MailRelayTestStatus.Bounced)
            {
                return logEvidence;
            }

            var queue = await hostCommand.RunAsync(
                "postqueue",
                ["-j"],
                cancellationToken,
                timeout: TimeSpan.FromSeconds(10));
            lastQueueEvidence = ParseQueueEvidence(queueId, queue.StandardOutput) ?? logEvidence ?? lastQueueEvidence;
            if (lastQueueEvidence?.Status == MailRelayTestStatus.Deferred)
            {
                return lastQueueEvidence;
            }
        }

        return lastQueueEvidence ?? new MailRelayDeliveryEvidence(
            MailRelayTestStatus.Queued,
            null,
            "Postfix accepted the message. No final delivery response was available during the test window.");
    }

    internal static MailRelayDeliveryEvidence? ParseLogEvidence(string output)
    {
        var matches = DeliveryStatusRegex().Matches(output ?? string.Empty);
        if (matches.Count == 0)
        {
            return null;
        }

        var match = matches[^1];
        var status = match.Groups["status"].Value.ToLowerInvariant() switch
        {
            "sent" => MailRelayTestStatus.Sent,
            "bounced" => MailRelayTestStatus.Bounced,
            _ => MailRelayTestStatus.Deferred
        };
        var destination = match.Groups["relay"].Success ? match.Groups["relay"].Value : null;
        return new MailRelayDeliveryEvidence(status, destination, CleanDetail(match.Groups["detail"].Value));
    }

    internal static MailRelayDeliveryEvidence? ParseQueueEvidence(string queueId, string output)
    {
        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("queue_id", out var id) ||
                    !queueId.Equals(id.GetString(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (root.TryGetProperty("recipients", out var recipients) && recipients.ValueKind == JsonValueKind.Array)
                {
                    foreach (var recipient in recipients.EnumerateArray())
                    {
                        if (recipient.TryGetProperty("delay_reason", out var reason) && !string.IsNullOrWhiteSpace(reason.GetString()))
                        {
                            return new MailRelayDeliveryEvidence(MailRelayTestStatus.Deferred, null, CleanDetail(reason.GetString()!));
                        }
                    }
                }

                return new MailRelayDeliveryEvidence(MailRelayTestStatus.Queued, null, "The message is waiting in the Postfix queue.");
            }
            catch (JsonException)
            {
                // Ignore unrelated or partial output and continue looking for this queue ID.
            }
        }

        return null;
    }

    internal static string? ParseQueueIdForMessage(string output, string messageId)
    {
        foreach (Match match in QueueMessageIdRegex().Matches(output ?? string.Empty))
        {
            if (match.Groups["messageId"].Value.Equals(messageId, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["queueId"].Value;
            }
        }

        return null;
    }

    internal static string BuildInternalDiagnosticMessage(
        MailRelayConfiguration configuration,
        MailRelayClient client,
        MailAddress sender,
        MailAddress recipient,
        string messageId)
    {
        var now = DateTimeOffset.UtcNow;
        var body = string.Join("\r\n",
        [
            "This is a Linux Made Sane Mail Relay diagnostic message.",
            string.Empty,
            $"Relay hostname: {configuration.RelayHostname}",
            $"Application: {client.Name}",
            $"SMTP username: {client.Username}",
            $"Timestamp: {now:O}"
        ]);
        var encodedBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        var bodyLines = Enumerable.Range(0, (encodedBody.Length + 75) / 76)
            .Select(index => encodedBody.Substring(index * 76, Math.Min(76, encodedBody.Length - (index * 76))));
        return string.Join("\r\n",
        [
            $"Date: {now:R}",
            $"Message-ID: <{messageId}>",
            $"From: {sender.Address}",
            $"To: {recipient.Address}",
            "Subject: LMS Mail Relay Test",
            "MIME-Version: 1.0",
            "Content-Type: text/plain; charset=utf-8",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            .. bodyLines,
            string.Empty
        ]);
    }

    private static MailRelayTestResult Failed(
        MailRelayTestRequest request,
        MailRelayClient? client,
        DateTimeOffset testedAt,
        string summary) =>
        Result(
            MailRelayTestStatus.Failed,
            false,
            false,
            client,
            request.FromAddress?.Trim() ?? string.Empty,
            request.RecipientAddress?.Trim() ?? string.Empty,
            null,
            null,
            string.Empty,
            summary,
            testedAt);

    private static MailRelayTestResult Result(
        MailRelayTestStatus status,
        bool clientPolicyValidated,
        bool accepted,
        MailRelayClient? client,
        string from,
        string recipient,
        string? queueId,
        string? destination,
        string smtpResponse,
        string summary,
        DateTimeOffset testedAt,
        bool dmarcIdentityAligned = false) =>
        new(
            status,
            clientPolicyValidated,
            accepted,
            client?.Name ?? string.Empty,
            from,
            recipient,
            queueId,
            destination,
            CleanDetail(smtpResponse),
            summary,
            testedAt,
            dmarcIdentityAligned);

    private static string CleanDetail(string value)
    {
        var clean = string.Join(' ', (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= 800 ? clean : clean[..800] + "…";
    }

    private static string FirstUsefulLine(string primary, string fallback) =>
        (primary ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault() ?? fallback;

    private static bool IsResolverFailure(string detail) =>
        detail.Contains("Name service error", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("Temporary failure in name resolution", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"relay=(?<relay>[^,\s]+).*status=(?<status>sent|deferred|bounced)\s+\((?<detail>[^\r\n]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeliveryStatusRegex();

    [GeneratedRegex(@"postfix/(?:cleanup|pickup)\[\d+\]:\s+(?<queueId>[A-F0-9]+):\s+message-id=<(?<messageId>[^>]+)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueueMessageIdRegex();
}

internal sealed record MailRelayDeliveryEvidence(
    MailRelayTestStatus Status,
    string? DestinationServer,
    string Detail);
