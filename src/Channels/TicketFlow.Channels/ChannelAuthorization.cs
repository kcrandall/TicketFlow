using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Channels;

internal static class ChannelAuthorization
{
    public static async Task<bool> PermittedAsync(this IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken)
    {
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false);
        return decision.IsPermitted;
    }

    /// <summary>A short, printable, stable idempotency key for a conversation message submission.</summary>
    public static string IdempotencyKey(TicketScope scope, string kind, string conversation, string message)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value, kind, conversation, message,
        });
        return "tf1-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
