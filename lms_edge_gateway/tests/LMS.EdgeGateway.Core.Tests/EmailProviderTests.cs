using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LMS.EdgeGateway.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class EmailProviderTests
{
    [Fact]
    public async Task Resend_maps_json_payload()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"resend-123"}""")
        });
        var provider = new ResendEmailProvider(new FakeHttpClientFactory(handler), new PlainSecretProtector());

        var result = await provider.SendAsync(Settings(MessagingEmailProvider.Resend), Message());

        Assert.True(result.Success);
        Assert.Equal("resend-123", result.ProviderMessageId);
        Assert.Equal("https://api.resend.com/emails", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", handler.Request.Headers.Authorization.Parameter);
        using var document = JsonDocument.Parse(handler.Body);
        Assert.Equal("Linux Made Sane <noreply@example.com>", document.RootElement.GetProperty("from").GetString());
        Assert.Equal("user@example.com", document.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("<p>Hello</p>", document.RootElement.GetProperty("html").GetString());
        Assert.Equal("Hello", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Brevo_maps_json_payload()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"messageId":"brevo-123"}""")
        });
        var provider = new BrevoEmailProvider(new FakeHttpClientFactory(handler), new PlainSecretProtector());

        var result = await provider.SendAsync(Settings(MessagingEmailProvider.Brevo, apiKey: "xkeysib-test-key"), Message());

        Assert.True(result.Success);
        Assert.Equal("brevo-123", result.ProviderMessageId);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", handler.Request!.RequestUri!.ToString());
        Assert.Equal("xkeysib-test-key", handler.Request.Headers.GetValues("api-key").Single());
        using var document = JsonDocument.Parse(handler.Body);
        Assert.Equal("noreply@example.com", document.RootElement.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("user@example.com", document.RootElement.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("User", document.RootElement.GetProperty("to")[0].GetProperty("name").GetString());
        Assert.Equal("<p>Hello</p>", document.RootElement.GetProperty("htmlContent").GetString());
    }

    [Fact]
    public async Task Brevo_omits_blank_recipient_name()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"messageId":"brevo-123"}""")
        });
        var provider = new BrevoEmailProvider(new FakeHttpClientFactory(handler), new PlainSecretProtector());

        var result = await provider.SendAsync(
            Settings(MessagingEmailProvider.Brevo, apiKey: "xkeysib-test-key"),
            Message() with { ToName = "" });

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(handler.Body);
        Assert.Equal("user@example.com", document.RootElement.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.False(document.RootElement.GetProperty("to")[0].TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Brevo_rejects_smtp_key_before_http_call()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Created));
        var provider = new BrevoEmailProvider(new FakeHttpClientFactory(handler), new PlainSecretProtector());

        var result = await provider.SendAsync(
            Settings(MessagingEmailProvider.Brevo, apiKey: " xsmtpsib-not-an-api-key "),
            Message());

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("Brevo SMTP key", result.ErrorMessage);
        Assert.Contains("xkeysib-", result.ErrorMessage);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task MailerSend_maps_json_payload()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        response.Headers.Add("X-Message-Id", "mailer-123");
        var handler = new CaptureHandler(response);
        var provider = new MailerSendEmailProvider(new FakeHttpClientFactory(handler), new PlainSecretProtector());

        var result = await provider.SendAsync(Settings(MessagingEmailProvider.MailerSend), Message());

        Assert.True(result.Success);
        Assert.Equal("mailer-123", result.ProviderMessageId);
        Assert.Equal("https://api.mailersend.com/v1/email", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        using var document = JsonDocument.Parse(handler.Body);
        Assert.Equal("noreply@example.com", document.RootElement.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("user@example.com", document.RootElement.GetProperty("to")[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task Mailgun_maps_form_payload_and_region_endpoint()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"mailgun-123","message":"Queued"}""")
        });
        var provider = new MailgunEmailProvider(new FakeHttpClientFactory(handler), new PlainSecretProtector());

        var result = await provider.SendAsync(
            Settings(MessagingEmailProvider.Mailgun, domain: "mg.example.com", region: MailgunRegion.Eu),
            Message());

        Assert.True(result.Success);
        Assert.Equal("mailgun-123", result.ProviderMessageId);
        Assert.Equal("https://api.eu.mailgun.net/v3/mg.example.com/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Basic", handler.Request.Headers.Authorization!.Scheme);
        Assert.Contains("from=Linux+Made+Sane", handler.Body, StringComparison.Ordinal);
        Assert.Contains("to=User+%3Cuser%40example.com%3E", handler.Body, StringComparison.Ordinal);
        Assert.Contains("subject=Subject", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_http_response_returns_status_and_helpful_error()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"sender domain is not verified"}""")
        });
        var provider = new ResendEmailProvider(new FakeHttpClientFactory(handler), new PlainSecretProtector());

        var result = await provider.SendAsync(Settings(MessagingEmailProvider.Resend), Message());

        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains("sender domain is not verified", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("From address are verified", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_api_key_validation_rejects_api_provider()
    {
        var service = BuildSecurityService();
        var editor = new SecurityMessagingSettingsEditor
        {
            IsEnabled = true,
            Provider = MessagingEmailProvider.Resend,
            SenderAddress = "noreply@example.com",
            SenderDisplayName = "Linux Made Sane"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveMessagingSettingsAsync(editor));

        Assert.Contains("API key is required", exception.Message);
    }

    [Fact]
    public async Task Missing_mailgun_domain_validation_rejects_mailgun()
    {
        var service = BuildSecurityService();
        var editor = new SecurityMessagingSettingsEditor
        {
            IsEnabled = true,
            Provider = MessagingEmailProvider.Mailgun,
            SenderAddress = "noreply@example.com",
            SenderDisplayName = "Linux Made Sane",
            ApiKey = "secret-key"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveMessagingSettingsAsync(editor));

        Assert.Contains("Mailgun domain is required", exception.Message);
    }

    [Fact]
    public async Task Test_email_uses_current_editor_api_key_and_trims_it()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"messageId":"brevo-current"}""")
        });
        var store = new InMemorySecurityStore(EdgeGatewaySecurityConfiguration.Empty with
        {
            Messaging = Settings(MessagingEmailProvider.Brevo, apiKey: "old-saved-key")
        });
        var service = new EdgeGatewaySecurityService(
            store,
            NoopTemporaryIpApprovalService.Instance,
            new PlainSecretProtector(),
            BuildEmailSender(store, handler),
            NullLogger<EdgeGatewaySecurityService>.Instance);

        var result = await service.SendMessagingTestAsync(
            new SecurityMessagingSettingsEditor
            {
                IsEnabled = true,
                Provider = MessagingEmailProvider.Brevo,
                SenderAddress = "noreply@example.com",
                SenderDisplayName = "Linux Made Sane",
                ApiKey = " current-key \n",
                HasApiKey = true
            },
            "user@example.com");

        Assert.True(result.Succeeded);
        Assert.Equal("current-key", handler.Request!.Headers.GetValues("api-key").Single());
        using var document = JsonDocument.Parse(handler.Body);
        var html = document.RootElement.GetProperty("htmlContent").GetString();
        var text = document.RootElement.GetProperty("textContent").GetString();
        Assert.Contains("Linux Made Sane - Edge Gateway", html, StringComparison.Ordinal);
        Assert.Contains("Home Assistant Add-on", html, StringComparison.Ordinal);
        Assert.Contains("Messaging is ready", html, StringComparison.Ordinal);
        Assert.Contains("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("raw.githubusercontent.com/lmsowner/linuxmadesanerelease", html, StringComparison.Ordinal);
        Assert.Contains("lms-logo-192.png", html, StringComparison.Ordinal);
        Assert.Contains("lms-splash.png", html, StringComparison.Ordinal);
        Assert.Contains("width=\"308\"", html, StringComparison.Ordinal);
        Assert.Contains(">HA<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Your test code is: 123456", text, StringComparison.Ordinal);
        var saved = await store.LoadAsync();
        Assert.Equal("current-key", saved.Messaging.ApiKeyProtected);
        Assert.True(saved.Messaging.LastVerifiedAtUtc.HasValue);
    }

    [Fact]
    public async Task Saving_unchanged_verified_provider_keeps_email_enabled()
    {
        var verifiedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var store = new InMemorySecurityStore(EdgeGatewaySecurityConfiguration.Empty with
        {
            Messaging = Settings(MessagingEmailProvider.Brevo, apiKey: "xkeysib-saved-key") with
            {
                LastVerifiedAtUtc = verifiedAt
            }
        });
        var service = new EdgeGatewaySecurityService(
            store,
            NoopTemporaryIpApprovalService.Instance,
            new PlainSecretProtector(),
            BuildEmailSender(store, new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK))),
            NullLogger<EdgeGatewaySecurityService>.Instance);

        await service.SaveMessagingSettingsAsync(new SecurityMessagingSettingsEditor
        {
            IsEnabled = true,
            Provider = MessagingEmailProvider.Brevo,
            SenderAddress = "noreply@example.com",
            SenderDisplayName = "Linux Made Sane",
            ApiKey = string.Empty,
            HasApiKey = true
        });

        var saved = await store.LoadAsync();
        Assert.True(saved.Messaging.IsEnabled);
        Assert.Equal(MessagingEmailProvider.Brevo, saved.Messaging.Provider);
        Assert.Equal(verifiedAt, saved.Messaging.LastVerifiedAtUtc);
    }

    [Fact]
    public async Task Saving_different_provider_stores_it_unverified_until_test_succeeds()
    {
        var verifiedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var store = new InMemorySecurityStore(EdgeGatewaySecurityConfiguration.Empty with
        {
            Messaging = Settings(MessagingEmailProvider.Brevo, apiKey: "xkeysib-saved-key") with
            {
                LastVerifiedAtUtc = verifiedAt
            }
        });
        var service = new EdgeGatewaySecurityService(
            store,
            NoopTemporaryIpApprovalService.Instance,
            new PlainSecretProtector(),
            BuildEmailSender(store, new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK))),
            NullLogger<EdgeGatewaySecurityService>.Instance);

        await service.SaveMessagingSettingsAsync(new SecurityMessagingSettingsEditor
        {
            IsEnabled = true,
            Provider = MessagingEmailProvider.Resend,
            SenderAddress = "noreply@example.com",
            SenderDisplayName = "Linux Made Sane",
            ApiKey = "resend-key"
        });

        var saved = await store.LoadAsync();
        Assert.False(saved.Messaging.IsEnabled);
        Assert.Equal(MessagingEmailProvider.Resend, saved.Messaging.Provider);
        Assert.Null(saved.Messaging.LastVerifiedAtUtc);
        Assert.Equal("resend-key", saved.Messaging.ApiKeyProtected);
    }

    [Fact]
    public async Task Test_email_enables_selected_provider_even_when_editor_enabled_is_false()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"messageId":"brevo-current"}""")
        });
        var store = new InMemorySecurityStore(EdgeGatewaySecurityConfiguration.Empty);
        var service = new EdgeGatewaySecurityService(
            store,
            NoopTemporaryIpApprovalService.Instance,
            new PlainSecretProtector(),
            BuildEmailSender(store, handler),
            NullLogger<EdgeGatewaySecurityService>.Instance);

        var result = await service.SendMessagingTestAsync(
            new SecurityMessagingSettingsEditor
            {
                IsEnabled = false,
                Provider = MessagingEmailProvider.Brevo,
                SenderAddress = "noreply@example.com",
                SenderDisplayName = "Linux Made Sane",
                ApiKey = "xkeysib-current-key"
            },
            "user@example.com");

        Assert.True(result.Succeeded);
        var saved = await store.LoadAsync();
        Assert.True(saved.Messaging.IsEnabled);
        Assert.Equal(MessagingEmailProvider.Brevo, saved.Messaging.Provider);
        Assert.True(saved.Messaging.LastVerifiedAtUtc.HasValue);
        Assert.Equal("xkeysib-current-key", handler.Request!.Headers.GetValues("api-key").Single());
    }

    [Fact]
    public async Task Test_email_uses_current_editor_graph_secret_and_trims_it()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"graph-token","expires_in":3600}""")
            },
            new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(string.Empty)
            });
        var store = new InMemorySecurityStore(EdgeGatewaySecurityConfiguration.Empty with
        {
            Messaging = EdgeGatewayMessagingSettings.CreateDefault(DateTimeOffset.UtcNow) with
            {
                IsEnabled = true,
                Provider = MessagingEmailProvider.MicrosoftGraph,
                SenderAddress = "noreply@example.com",
                SenderDisplayName = "Linux Made Sane",
                GraphTenantId = "tenant-id",
                GraphClientId = "client-id",
                GraphClientSecretProtected = "old-saved-secret"
            }
        });
        var service = new EdgeGatewaySecurityService(
            store,
            NoopTemporaryIpApprovalService.Instance,
            new PlainSecretProtector(),
            BuildEmailSender(store, handler),
            NullLogger<EdgeGatewaySecurityService>.Instance);

        var result = await service.SendMessagingTestAsync(
            new SecurityMessagingSettingsEditor
            {
                IsEnabled = true,
                Provider = MessagingEmailProvider.MicrosoftGraph,
                SenderAddress = "noreply@example.com",
                SenderDisplayName = "Linux Made Sane",
                GraphTenantId = "tenant-id",
                GraphClientId = "client-id",
                GraphClientSecret = " current-graph-secret \n",
                HasGraphClientSecret = true,
                GraphAuthority = "https://login.microsoftonline.com/",
                GraphBaseUrl = "https://graph.microsoft.com/v1.0"
            },
            "user@example.com");

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("client_secret=current-graph-secret", handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("old-saved-secret", handler.Bodies[0], StringComparison.Ordinal);
        var authorization = handler.Requests[1].Headers.Authorization;
        Assert.NotNull(authorization);
        Assert.Equal("Bearer", authorization.Scheme);
        Assert.Equal("graph-token", authorization.Parameter);
    }

    [Fact]
    public async Task Cloudflare_token_validator_trims_token_before_verification()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"success":true,"result":{"status":"active"}}""")
        });
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/")
        };
        var validator = new CloudflareApiTokenValidator(client);

        var result = await validator.ValidateAsync(" cloudflare-token \n");

        Assert.True(result.IsValid);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("cloudflare-token", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Email_sender_uses_configured_api_provider_for_mfa_path()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"resend-mfa"}""")
        });
        var store = new InMemorySecurityStore(EdgeGatewaySecurityConfiguration.Empty with
        {
            Messaging = Settings(MessagingEmailProvider.Resend)
        });
        var sender = BuildEmailSender(store, handler);

        var result = await sender.SendHtmlAsync("user@example.com", "Subject", "<p>Hello</p>");

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.resend.com/emails", handler.Request!.RequestUri!.ToString());
        using var document = JsonDocument.Parse(handler.Body);
        Assert.Equal("Subject", document.RootElement.GetProperty("subject").GetString());
        Assert.Equal("user@example.com", document.RootElement.GetProperty("to")[0].GetString());
    }

    [Fact]
    public async Task Created_user_otp_uri_uses_lms_ha_addon_authenticator_name()
    {
        var service = BuildSecurityService();

        var result = await service.CreateUserAsync(new SecurityUserEditor
        {
            Email = " user@example.com ",
            DisplayName = "User",
            IsEnabled = true,
            SessionLifetimeMinutes = SecuritySessionPolicy.DefaultSessionLifetimeMinutes
        });

        Assert.StartsWith(
            "otpauth://totp/LMS%20HA%20Add-On:user%40example.com?",
            result.OtpUri,
            StringComparison.Ordinal);
        Assert.Contains("issuer=LMS%20HA%20Add-On", result.OtpUri, StringComparison.Ordinal);
        Assert.DoesNotContain("issuer=Linux%20Made%20Sane", result.OtpUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_setup_email_uses_branded_template()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"messageId":"setup-123"}""")
        });
        var now = DateTimeOffset.UtcNow;
        var store = new InMemorySecurityStore(EdgeGatewaySecurityConfiguration.Empty with
        {
            Messaging = Settings(MessagingEmailProvider.Brevo, apiKey: "xkeysib-test-key") with
            {
                LastVerifiedAtUtc = now
            }
        });
        var service = new EdgeGatewaySecurityService(
            store,
            NoopTemporaryIpApprovalService.Instance,
            new PlainSecretProtector(),
            BuildEmailSender(store, handler),
            NullLogger<EdgeGatewaySecurityService>.Instance);

        var result = await service.CreateUserAsync(
            new SecurityUserEditor
            {
                Email = "user@example.com",
                DisplayName = "User",
                IsEnabled = true,
                SessionLifetimeMinutes = SecuritySessionPolicy.DefaultSessionLifetimeMinutes
            },
            "https://edge.example/login");

        Assert.True(result.EmailSucceeded);
        using var document = JsonDocument.Parse(handler.Body);
        Assert.Equal("Your Linux Made Sane Edge Gateway login is ready", document.RootElement.GetProperty("subject").GetString());
        var html = document.RootElement.GetProperty("htmlContent").GetString();
        Assert.Contains("Linux Made Sane - Edge Gateway", html, StringComparison.Ordinal);
        Assert.Contains("Home Assistant Add-on", html, StringComparison.Ordinal);
        Assert.Contains("Authenticator setup", html, StringComparison.Ordinal);
        Assert.Contains("Manual key", html, StringComparison.Ordinal);
        Assert.Contains("OTP URI", html, StringComparison.Ordinal);
        Assert.Contains("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("raw.githubusercontent.com/lmsowner/linuxmadesanerelease", html, StringComparison.Ordinal);
        Assert.Contains("lms-logo-192.png", html, StringComparison.Ordinal);
        Assert.Contains("lms-splash.png", html, StringComparison.Ordinal);
        Assert.Contains("width=\"308\"", html, StringComparison.Ordinal);
        Assert.Contains("Scan this QR code", html, StringComparison.Ordinal);
        Assert.Contains("Authenticator QR code", html, StringComparison.Ordinal);
        Assert.Contains("table-layout:fixed", html, StringComparison.Ordinal);
        Assert.Contains("width:3px", html, StringComparison.Ordinal);
        Assert.Contains("height:3px", html, StringComparison.Ordinal);
        Assert.Contains("line-height:0", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&nbsp;</td>", html, StringComparison.Ordinal);
        Assert.Contains(">MFA<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://edge.example/login", html, StringComparison.Ordinal);
    }

    private static EdgeGatewaySecurityService BuildSecurityService()
    {
        var store = new InMemorySecurityStore(EdgeGatewaySecurityConfiguration.Empty);
        return new EdgeGatewaySecurityService(
            store,
            NoopTemporaryIpApprovalService.Instance,
            new PlainSecretProtector(),
            BuildEmailSender(store, new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK))),
            NullLogger<EdgeGatewaySecurityService>.Instance);
    }

    private static EdgeGatewayEmailDeliveryService BuildEmailSender(
        IEdgeGatewaySecurityStore store,
        HttpMessageHandler handler)
    {
        var secretProtector = new PlainSecretProtector();
        var httpClientFactory = new FakeHttpClientFactory(handler);
        IEmailApiProvider[] providers =
        [
            new ResendEmailProvider(httpClientFactory, secretProtector),
            new BrevoEmailProvider(httpClientFactory, secretProtector),
            new MailerSendEmailProvider(httpClientFactory, secretProtector),
            new MailgunEmailProvider(httpClientFactory, secretProtector)
        ];

        return new EdgeGatewayEmailDeliveryService(
            store,
            secretProtector,
            httpClientFactory,
            new EmailProviderFactory(providers),
            NullLogger<EdgeGatewayEmailDeliveryService>.Instance);
    }

    private static EdgeGatewayMessagingSettings Settings(
        MessagingEmailProvider provider,
        string apiKey = "secret-key",
        string domain = "",
        MailgunRegion region = MailgunRegion.Us)
    {
        var now = DateTimeOffset.UtcNow;
        return EdgeGatewayMessagingSettings.CreateDefault(now) with
        {
            IsEnabled = true,
            Provider = provider,
            SenderAddress = "noreply@example.com",
            SenderDisplayName = "Linux Made Sane",
            ApiKeyProtected = apiKey,
            MailgunDomain = domain,
            MailgunRegion = region
        };
    }

    private static EmailMessage Message() =>
        new(
            "noreply@example.com",
            "Linux Made Sane",
            "user@example.com",
            "User",
            "Subject",
            "Hello",
            "<p>Hello</p>");

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int index;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return responses[index++];
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class PlainSecretProtector : IEdgeGatewaySecretProtector
    {
        public string Protect(string secret) => secret;
        public string Unprotect(string protectedSecret) => protectedSecret;
    }

    private sealed class NoopTemporaryIpApprovalService : IEdgeGatewayTemporaryIpApprovalService
    {
        public static NoopTemporaryIpApprovalService Instance { get; } = new();

        public Task<IReadOnlyList<TrustedIpAddressViewModel>> ListTrustedIpAddressesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrustedIpAddressViewModel>>([]);

        public Task<bool> RevokeTrustedIpAddressAsync(
            Guid grantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<TemporaryIpApprovalEvaluationResult> EvaluateAsync(
            PublishedApplicationDefinition route,
            TemporaryIpApprovalCheckContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TemporaryIpApprovalEvaluationResult(false, "Not implemented."));

        public Task<TemporaryIpApprovalCompletionResult> ApproveAsync(
            string token,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TemporaryIpApprovalCompletionResult(false, "Not implemented", "Not implemented."));
    }

    private sealed class InMemorySecurityStore(EdgeGatewaySecurityConfiguration configuration) : IEdgeGatewaySecurityStore
    {
        private EdgeGatewaySecurityConfiguration current = configuration;

        public Task<EdgeGatewaySecurityConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(current);

        public Task SaveAsync(
            EdgeGatewaySecurityConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            current = configuration;
            return Task.CompletedTask;
        }
    }
}
