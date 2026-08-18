namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewayEmailDeliveryService : IEmailSender
{
    Task<EmailDeliveryResult> SendHtmlAsync(
        string recipientAddress,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);

    Task<EmailSendResult> SendAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default);
}
