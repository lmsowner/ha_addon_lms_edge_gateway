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
            return await FailedAsync(request, client, testedAt, "Mail Relay is not running. Finish or retry relay setup first.", cancellationToken);
        }

        if (client is null || !client.Enabled)
        {
            return await FailedAsync(request, client, testedAt, "Choose an enabled Mail Relay application.", cancellationToken);
        }

        if (!MailAddress.TryCreate(request.FromAddress?.Trim(), out var sender))
        {
            return await FailedAsync(request, client, testedAt, "Enter a valid From address.", cancellationToken);
        }

        if (!MailAddress.TryCreate(request.RecipientAddress?.Trim(), out var recipient))
        {
            return await FailedAsync(request, client, testedAt, "Enter a valid recipient address.", cancellationToken);
        }

        if (!client.AllowedSenderDomains.Contains(sender.Host, StringComparer.OrdinalIgnoreCase))
        {
            return await FailedAsync(
                request,
                client,
                testedAt,
                $"{client.Name} is not allowed to send as {sender.Host}. Choose an address in one of its allowed sender domains.",
                cancellationToken);
        }

        var domains = await store.ListDomainsAsync(cancellationToken);
        if (!domains.Any(domain =>
                domain.Enabled &&
                domain.DomainName.Equals(sender.Host, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(domain.CurrentDkimPrivateKeySecretReference)))
        {
            return await FailedAsync(
                request,
                client,
                testedAt,
                $"{sender.Host} is not an enabled Mail Relay sending domain with a DKIM key. Configure the domain before testing it.",
                cancellationToken);
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
            return await FailedAsync(request, client, testedAt, "The relay did not complete the SMTP test within 30 seconds.", cancellationToken, messageId);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return await FailedAsync(request, client, testedAt, $"Mail Relay test failed: {CleanDetail(exception.Message)}", cancellationToken, messageId);
        }

        if (submission.ExitCode == 127)
        {
            return await FailedAsync(request, client, testedAt, "Mail Relay tests require the Home Assistant add-on image, where sendmail is installed.", cancellationToken, messageId);
        }

        if (submission.ExitCode != 0)
        {
            return await ResultAsync(
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
                testedAt,
                cancellationToken,
                messageId);
        }

        var queueId = await FindQueueIdAsync(messageId, cancellationToken);
        await store.SaveClientAsync(client with { LastUsedUtc = testedAt, UpdatedUtc = testedAt }, cancellationToken);
        await hostCommand.RunAsync("postqueue", ["-f"], cancellationToken, timeout: TimeSpan.FromSeconds(10));
        var delivery = await InspectDeliveryAsync(queueId, cancellationToken);
        var summary = delivery.Status switch
        {
            MailRelayTestStatus.Sent => "The selected user policy passed, Postfix accepted the message, and the destination mail server accepted delivery.",
            MailRelayTestStatus.Deferred when IsResolverFailure(delivery.Detail) => "The selected user policy passed and Postfix accepted the message, but the relay could not resolve the recipient domain.",
            MailRelayTestStatus.Deferred when IsPort25Failure(delivery.Detail) => "The selected user policy passed and Postfix accepted the message, but this Home Assistant network cannot reach the destination MX on TCP/25. Full LMS works because that host can. Outlook MX does not accept mail on 587.",
            MailRelayTestStatus.Deferred => "The selected user policy and relay are working, but Internet delivery was deferred. The diagnostic below is the reason returned by Postfix or the destination server.",
            MailRelayTestStatus.Bounced => "The selected user policy and relay accepted the message, but downstream delivery bounced.",
            _ => "The selected user policy passed and Postfix accepted the message. It is still trying the recipient MX on TCP/25, the same path full LMS uses."
        };

        return await ResultAsync(
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
            cancellationToken,
            messageId,
            dmarcIdentityAligned: true);
    }

    public async Task<MailRelayLogSnapshot> GetLogAsync(
        string? queueId = null,
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        var logText = await ReadCombinedMailLogsAsync(cancellationToken);
        var entries = ParseLogEntries(logText, messageId, queueId, 200);
        var lines = (logText ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(200)
            .ToList();

        var queue = await hostCommand.RunAsync(
            "postqueue",
            ["-p"],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(10));
        var queueLines = (queue.StandardOutput ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var path = File.Exists(paths.MailLogPath) ? paths.MailLogPath : paths.SystemMailLogPath;
        var available = File.Exists(paths.MailLogPath) || File.Exists(paths.SystemMailLogPath);
        var summary = lines.Count == 0 && queueLines.Length == 0
            ? available
                ? "The send log is empty. Send a test, then refresh."
                : "Postfix has not written a send log yet."
            : string.IsNullOrWhiteSpace(queueId) && string.IsNullOrWhiteSpace(messageId)
                ? $"{lines.Count} mail.log line(s). Queue has {queueLines.Length} line(s)."
                : $"{lines.Count} mail.log line(s) for this send.";
        return new MailRelayLogSnapshot(available || lines.Count > 0, path, summary, lines, entries, queueLines);
    }

    public async Task<MailRelayQueueResult> ClearQueueAsync(CancellationToken cancellationToken = default)
    {
        var result = await hostCommand.RunAsync(
            "postsuper",
            ["-d", "ALL"],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
        {
            return new(
                false,
                FirstUsefulLine(
                    FirstUsefulLine(result.StandardError, result.StandardOutput),
                    "Could not clear the Postfix queue."));
        }

        return new(true, "The Postfix queue is empty.");
    }

    private async Task<string> ReadCombinedMailLogsAsync(CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chunks = new List<string>();
        foreach (var path in new[] { paths.MailLogPath, paths.SystemMailLogPath, paths.MessagesLogPath })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            string resolved;
            try
            {
                resolved = Path.GetFullPath(path);
                if (File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target)
                {
                    resolved = target.FullName;
                }
            }
            catch (IOException)
            {
                resolved = Path.GetFullPath(path);
            }

            if (!seen.Add(resolved))
            {
                continue;
            }

            chunks.Add(await ReadMailLogTailAsync(path, cancellationToken));
        }

        return string.Join('\n', chunks);
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

            var logs = await ReadCombinedMailLogsAsync(cancellationToken);
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

        var delays = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(16)
        };
        MailRelayDeliveryEvidence? lastEvidence = null;
        foreach (var delay in delays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var logs = await ReadCombinedMailLogsAsync(cancellationToken);
            var logEvidence = ParseLogEvidence(logs, queueId);
            if (logEvidence?.Status is MailRelayTestStatus.Sent or MailRelayTestStatus.Bounced or MailRelayTestStatus.Deferred)
            {
                return logEvidence;
            }

            var queue = await hostCommand.RunAsync(
                "postqueue",
                ["-j"],
                cancellationToken,
                timeout: TimeSpan.FromSeconds(10));
            lastEvidence = ParseQueueEvidence(queueId, queue.StandardOutput) ?? logEvidence ?? lastEvidence;
            if (lastEvidence?.Status == MailRelayTestStatus.Deferred)
            {
                return lastEvidence;
            }
        }

        return lastEvidence ?? new MailRelayDeliveryEvidence(
            MailRelayTestStatus.Queued,
            null,
            "Postfix is still trying to deliver. No MX response was logged during the test window. Home ISPs often block outbound TCP/25; full LMS usually runs on a host that is not blocked.");
    }

    internal static MailRelayDeliveryEvidence? ParseLogEvidence(string output, string? queueId = null)
    {
        var scoped = string.IsNullOrWhiteSpace(queueId)
            ? output ?? string.Empty
            : string.Join('\n', (output ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(queueId, StringComparison.OrdinalIgnoreCase)));
        var matches = DeliveryStatusRegex().Matches(scoped);
        if (matches.Count > 0)
        {
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

        var connect = ConnectAttemptRegex().Matches(scoped);
        if (connect.Count == 0)
        {
            return null;
        }

        var last = connect[^1];
        return new MailRelayDeliveryEvidence(
            MailRelayTestStatus.Deferred,
            last.Groups["relay"].Value,
            CleanDetail($"connect to {last.Groups["relay"].Value}: {last.Groups["detail"].Value}"));
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

    private Task<MailRelayTestResult> FailedAsync(
        MailRelayTestRequest request,
        MailRelayClient? client,
        DateTimeOffset testedAt,
        string summary,
        CancellationToken cancellationToken,
        string? messageId = null) =>
        ResultAsync(
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
            testedAt,
            cancellationToken,
            messageId);

    private async Task<MailRelayTestResult> ResultAsync(
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
        CancellationToken cancellationToken,
        string? messageId = null,
        bool dmarcIdentityAligned = false)
    {
        var log = await GetLogAsync(queueId, messageId, cancellationToken);
        return new(
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
            dmarcIdentityAligned,
            log.Lines);
    }

    internal static IReadOnlyList<MailRelayLogEntry> ParseLogEntries(
        string logText,
        string? messageId,
        string? queueId,
        int maxEntries)
    {
        var entries = new List<MailRelayLogEntry>();
        foreach (var line in (logText ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = PostfixLogLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var entryQueueId = match.Groups["queueId"].Success ? match.Groups["queueId"].Value : null;
            var detail = match.Groups["detail"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(queueId) &&
                !string.Equals(entryQueueId, queueId, StringComparison.OrdinalIgnoreCase) &&
                !detail.Contains(queueId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(messageId) &&
                !detail.Contains(messageId, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(entryQueueId))
            {
                continue;
            }

            entries.Add(new MailRelayLogEntry(
                match.Groups["timestamp"].Value,
                match.Groups["service"].Value,
                entryQueueId,
                detail,
                ClassifyLogDetail(detail)));
        }

        if (entries.Count <= maxEntries)
        {
            return entries;
        }

        return entries.Skip(entries.Count - maxEntries).ToArray();
    }

    internal static IReadOnlyList<string> SelectMailLogLines(
        string logText,
        string? messageId,
        string? queueId,
        int maxLines)
    {
        var all = (logText ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keyed = all
            .Where(line =>
                (!string.IsNullOrWhiteSpace(queueId) && line.Contains(queueId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(messageId) && line.Contains(messageId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var source = (keyed.Length > 0 ? keyed : all)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return source.Length <= maxLines
            ? source
            : source[^maxLines..];
    }

    internal static async Task<string> ReadMailLogTailAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int maxBytes = 256 * 1024;
        var trimmed = stream.Length > maxBytes;
        if (trimmed)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (!trimmed)
        {
            return text;
        }

        var firstBreak = text.IndexOf('\n');
        return firstBreak >= 0 ? text[(firstBreak + 1)..] : text;
    }

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

    private static MailRelayLogSeverity ClassifyLogDetail(string detail)
    {
        if (detail.Contains("status=sent", StringComparison.OrdinalIgnoreCase))
        {
            return MailRelayLogSeverity.Sent;
        }

        if (detail.Contains("status=bounced", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("fatal:", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("reject:", StringComparison.OrdinalIgnoreCase))
        {
            return MailRelayLogSeverity.Error;
        }

        if (detail.Contains("status=deferred", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("warning:", StringComparison.OrdinalIgnoreCase))
        {
            return MailRelayLogSeverity.Warning;
        }

        return MailRelayLogSeverity.Info;
    }

    private static bool IsPort25Failure(string detail) =>
        detail.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("No route to host", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains(":25", StringComparison.Ordinal);

    [GeneratedRegex(@"relay=(?<relay>[^,\s]+).*status=(?<status>sent|deferred|bounced)\s+\((?<detail>[^\r\n]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeliveryStatusRegex();

    [GeneratedRegex(@"connect to (?<relay>\S+): (?<detail>Connection timed out|Connection refused|Network is unreachable|No route to host|Host or domain name not found[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectAttemptRegex();

    [GeneratedRegex(@"postfix/(?:cleanup|pickup)\[\d+\]:\s+(?<queueId>[A-F0-9]+):\s+message-id=<(?<messageId>[^>]+)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueueMessageIdRegex();

    [GeneratedRegex(@"^(?<timestamp>[A-Z][a-z]{2}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2})\s+\S+\s+postfix/(?<service>[A-Za-z0-9_-]+)(?:\[\d+\])?:\s+(?:(?<queueId>[A-F0-9]+):\s+)?(?<detail>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostfixLogLineRegex();
}

internal sealed record MailRelayDeliveryEvidence(
    MailRelayTestStatus Status,
    string? DestinationServer,
    string Detail);
