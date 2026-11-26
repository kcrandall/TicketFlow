using TicketFlow.Surveys;

namespace TicketFlow.SampleHost;

/// <summary>
/// Sample survey sender: logs the delivery key only (never the token, contact or question). Real hosts register an adapter such as
/// <c>AddEnvelopeFlowSurveySender()</c>.
/// </summary>
public sealed partial class DevelopmentSurveyInvitationSender(ILogger<DevelopmentSurveyInvitationSender> logger, SampleSurveyTokens tokens) : ISurveyInvitationSender
{
    public Task SendAsync(SurveyInvitationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogSurveyInvitation(logger, message.DeliveryKey);
        tokens.Add(message.Token.Value);   // only while the seeder answers demo surveys
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Survey invitation {DeliveryKey} would be sent (development sender).")]
    private static partial void LogSurveyInvitation(ILogger logger, string deliveryKey);
}
