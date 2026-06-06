using System.Text.RegularExpressions;
using LMS.EdgeGateway.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class TemporaryIpApprovalTests
{
    [Fact]
    public async Task Temporary_ip_approval_sends_once_throttles_and_allows_approved_source_ip()
    {
        var route = Route();
        var approvalStore = new InMemoryTemporaryIpApprovalStore();
        var emailDelivery = new RecordingEmailDeliveryService();
        var service = CreateService(route, approvalStore, emailDelivery);
        var context = new TemporaryIpApprovalCheckContext(
            "plex.example.com",
            "/",
            "https://plex.example.com/",
            "198.51.100.44",
            "GB",
            "Plex/1.0");

        var first = await service.EvaluateAsync(route, context);
        var second = await service.EvaluateAsync(route, context);
        var token = ExtractApprovalToken(emailDelivery.Messages.Single().PlainTextBody);
        var approval = await service.ApproveAsync(token);
        var approved = await service.EvaluateAsync(route, context);
        var otherIp = await service.EvaluateAsync(route, context with { SourceIp = "198.51.100.45" });

        Assert.False(first.IsAllowed);
        Assert.True(first.EmailAttempted);
        Assert.True(first.EmailSucceeded);
        Assert.False(second.IsAllowed);
        Assert.False(second.EmailAttempted);
        Assert.Contains("sent recently", second.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(approval.Success);
        Assert.True(approved.IsAllowed);
        Assert.False(otherIp.IsAllowed);
        Assert.Equal(2, emailDelivery.Messages.Count);
    }

    [Fact]
    public async Task Temporary_ip_approval_uses_explicit_route_recipients()
    {
        var route = Route() with
        {
            TemporaryIpApprovalRecipients = "approver@example.com, other@example.com"
        };
        var emailDelivery = new RecordingEmailDeliveryService();
        var service = CreateService(route, new InMemoryTemporaryIpApprovalStore(), emailDelivery);

        var result = await service.EvaluateAsync(route, Context(countryCode: "GB"));

        Assert.False(result.IsAllowed);
        Assert.True(result.EmailSucceeded);
        Assert.Equal(["approver@example.com", "other@example.com"], emailDelivery.Messages.Select(message => message.ToEmail).Order());
    }

    [Fact]
    public async Task Temporary_ip_approval_blocks_country_before_sending_email()
    {
        var route = Route() with
        {
            TemporaryIpApprovalAllowedCountryCodes = "GB, IE"
        };
        var emailDelivery = new RecordingEmailDeliveryService();
        var service = CreateService(route, new InMemoryTemporaryIpApprovalStore(), emailDelivery);

        var result = await service.EvaluateAsync(route, Context(countryCode: "US"));

        Assert.False(result.IsAllowed);
        Assert.False(result.EmailAttempted);
        Assert.Contains("does not allow requests from US", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(emailDelivery.Messages);
    }

    [Fact]
    public async Task Temporary_ip_approval_allows_configured_country()
    {
        var route = Route() with
        {
            TemporaryIpApprovalAllowedCountryCodes = "GB, IE"
        };
        var emailDelivery = new RecordingEmailDeliveryService();
        var service = CreateService(route, new InMemoryTemporaryIpApprovalStore(), emailDelivery);

        var result = await service.EvaluateAsync(route, Context(countryCode: "GB"));

        Assert.False(result.IsAllowed);
        Assert.True(result.EmailSucceeded);
        Assert.Single(emailDelivery.Messages);
    }

    private static string ExtractApprovalToken(string body)
    {
        var match = Regex.Match(body, @"token=([^\s]+)", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "Approval email should include an approval token URL.");
        return Uri.UnescapeDataString(match.Groups[1].Value.Trim());
    }

    private static PublishedApplicationDefinition Route() =>
        new(
            Guid.NewGuid(),
            "Plex",
            "plex.example.com",
            "http://192.168.1.50:32400",
            EdgeGatewayAccessPolicies.TemporaryIpApproval,
            true);

    private static TemporaryIpApprovalCheckContext Context(string countryCode) =>
        new(
            "plex.example.com",
            "/",
            "https://plex.example.com/",
            "198.51.100.44",
            countryCode,
            "Plex/1.0");

    private static EdgeGatewayTemporaryIpApprovalService CreateService(
        PublishedApplicationDefinition route,
        InMemoryTemporaryIpApprovalStore approvalStore,
        RecordingEmailDeliveryService emailDelivery) =>
        new(
            approvalStore,
            new InMemoryConfigurationStore(Configuration(route)),
            new InMemorySecurityStore(SecurityConfiguration("owner@example.com")),
            emailDelivery,
            Options.Create(new EdgeGatewayCoreOptions
            {
                TemporaryIpApprovalIdleTimeoutMinutes = 15,
                TemporaryIpApprovalMaxLifetimeMinutes = 120,
                TemporaryIpApprovalTokenLifetimeMinutes = 30,
                TemporaryIpApprovalEmailCooldownMinutes = 60,
                TemporaryIpApprovalMaxEmailsPerDay = 10
            }),
            NullLogger<EdgeGatewayTemporaryIpApprovalService>.Instance);

    private static EdgeGatewayConfiguration Configuration(PublishedApplicationDefinition route) =>
        new(
            [route],
            [],
            new CloudflareTunnelState("tunnel", "account", "tunnel-id", true, DateTimeOffset.UtcNow, "account-id"),
            DateTimeOffset.UtcNow);

    private static EdgeGatewaySecurityConfiguration SecurityConfiguration(string email)
    {
        var now = DateTimeOffset.UtcNow;
        return EdgeGatewaySecurityConfiguration.Empty with
        {
            Users =
            [
                new EdgeGatewaySecurityUser(
                    Guid.NewGuid(),
                    email,
                    email,
                    true,
                    SecuritySessionPolicy.DefaultSessionLifetimeMinutes,
                    "protected-secret",
                    now,
                    now,
                    null,
                    now)
            ]
        };
    }

    private sealed class InMemoryTemporaryIpApprovalStore : IEdgeGatewayTemporaryIpApprovalStore
    {
        private TemporaryIpApprovalConfiguration configuration = TemporaryIpApprovalConfiguration.Empty;

        public Task<TemporaryIpApprovalConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task SaveAsync(
            TemporaryIpApprovalConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            this.configuration = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryConfigurationStore(EdgeGatewayConfiguration configuration) : IEdgeGatewayConfigurationStore
    {
        public Task<EdgeGatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task SaveAsync(
            EdgeGatewayConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemorySecurityStore(EdgeGatewaySecurityConfiguration configuration) : IEdgeGatewaySecurityStore
    {
        public Task<EdgeGatewaySecurityConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task SaveAsync(
            EdgeGatewaySecurityConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingEmailDeliveryService : IEdgeGatewayEmailDeliveryService
    {
        public List<EmailMessage> Messages { get; } = [];

        public Task<EmailDeliveryResult> SendHtmlAsync(
            string recipientAddress,
            string subject,
            string htmlBody,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(new EmailMessage(string.Empty, string.Empty, recipientAddress, string.Empty, subject, string.Empty, htmlBody));
            return Task.FromResult(new EmailDeliveryResult(true, true, "sent"));
        }

        public Task<EmailSendResult> SendAsync(
            EmailMessage message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(EmailSendResult.Succeeded(MessagingEmailProvider.Brevo, 202));
        }

        public Task<EmailSendResult> SendAsync(
            EdgeGatewayMessagingSettings settings,
            EmailMessage message,
            CancellationToken cancellationToken = default) =>
            SendAsync(message, cancellationToken);
    }
}
