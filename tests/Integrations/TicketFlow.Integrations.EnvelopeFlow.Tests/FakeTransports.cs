using EnvelopeFlow.Chat;
using EnvelopeFlow.Email;
using EnvelopeFlow.Sms;
using EnvelopeFlow.WhatsApp;
using TicketFlow.Core;

namespace TicketFlow.Integrations.EnvelopeFlow.Tests;

internal sealed class FakeEmailTransport : IEmailTransport
{
    private int sent;
    public List<EmailMessage> Sent { get; } = [];
    public Queue<EmailSubmissionResult> Results { get; } = new();

    public Task<EmailSubmissionResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue()
            : new EmailSubmissionResult(EmailSubmissionStatus.Accepted, ProviderMessageId: $"<out-{Interlocked.Increment(ref sent)}@ticketflow.example>"));
    }
}

internal sealed class FakeSmsTransport : ISmsTransport
{
    public List<SmsMessage> Sent { get; } = [];
    public Queue<SmsSubmissionResult> Results { get; } = new();
    public SmsCapabilities Capabilities { get; } = new(true, false, true, false, false);

    public Task<SmsSubmissionResult> SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new SmsSubmissionResult(SmsSubmissionStatus.Accepted, ProviderMessageId: $"SMout{Sent.Count}"));
    }
}

/// <summary>Mimics MetaWhatsAppTransport: resolves the binding through the host delegate and enforces the 24-hour window for free text.</summary>
internal sealed class FakeWhatsAppTransport(Func<WhatsAppDestination, CancellationToken, Task<EnvelopeFlowDestinationContext?>> resolve, TimeProvider time) : IWhatsAppTransport
{
    public List<(WhatsAppSendRequest Request, EnvelopeFlowDestinationContext Context)> Sent { get; } = [];

    public void Validate(WhatsAppSendRequest request) => request.Validate();

    public async Task<WhatsAppSubmissionResult> SendAsync(WhatsAppSendRequest request, CancellationToken cancellationToken = default)
    {
        var context = await resolve(request.Destination, cancellationToken);
        if (context is null)
            return new WhatsAppSubmissionResult(WhatsAppSubmissionStatus.Rejected, FailureCode: "binding_not_found");
        if (request.Message.Template is null && (context.LastIncomingAt is not { } last || time.GetUtcNow() - last >= TimeSpan.FromHours(24)))
            return new WhatsAppSubmissionResult(WhatsAppSubmissionStatus.Rejected, FailureCode: "customer_service_window_closed");
        Sent.Add((request, context));
        return new WhatsAppSubmissionResult(WhatsAppSubmissionStatus.Accepted, $"wamid.out{Sent.Count}");
    }
}

internal sealed class FakeChatTransport : IChatTransport
{
    public List<ChatSendRequest> Sent { get; } = [];

    public void Validate(ChatSendRequest request) => request.Validate();

    public Task<ChatSubmissionResult> SendAsync(ChatSendRequest request, CancellationToken cancellationToken = default)
    {
        Sent.Add(request);
        return Task.FromResult(new ChatSubmissionResult(ChatSubmissionStatus.Accepted, $"T1:C1:17000000{Sent.Count:D2}.000900"));
    }
}

internal sealed class Senders : IEnvelopeFlowSenderDirectory
{
    public ValueTask<EnvelopeFlowEmailSender?> GetEmailSenderAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<EnvelopeFlowEmailSender?>(configurationId == "cfg-email" ? new EnvelopeFlowEmailSender("support@acme.example", "Acme Support") : null);

    public ValueTask<EnvelopeFlowSmsSender?> GetSmsSenderAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<EnvelopeFlowSmsSender?>(new EnvelopeFlowSmsSender(From: "+19995550000"));
}
