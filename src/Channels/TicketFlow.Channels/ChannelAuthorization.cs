using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Channels;

internal static class ChannelAuthorization
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedWriter;

    public static async Task<bool> PermittedAsync(this IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken)
    {
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false);
        return decision.IsPermitted;
    }

    /// <summary>A short, printable, stable idempotency key for a conversation message submission.</summary>
    public static string IdempotencyKey(TicketScope scope, string kind, string conversation, string message)
    {
        var buffer = sharedBuffer;
        if (buffer is null)
            sharedBuffer = buffer = new ArrayBufferWriter<byte>(256);
        else
            buffer.ResetWrittenCount();
        var writer = sharedWriter;
        if (writer is null)
            sharedWriter = writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue(scope.TenantId);
        writer.WriteStringValue(scope.ProjectId);
        writer.WriteStringValue(scope.EnvironmentId);
        writer.WriteStringValue(scope.AppId);
        writer.WriteStringValue(scope.WorkspaceId.Value);
        writer.WriteStringValue(kind);
        writer.WriteStringValue(conversation);
        writer.WriteStringValue(message);
        writer.WriteEndArray();
        writer.Flush();
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer.WrittenSpan, hash);
        if (buffer.Capacity > 8192)
            sharedBuffer = null;
        return FormatIdempotencyKey(hash);
    }

    private static string FormatIdempotencyKey(ReadOnlySpan<byte> hash)
    {
        Span<char> chars = stackalloc char[68];
        chars[0] = 't';
        chars[1] = 'f';
        chars[2] = '1';
        chars[3] = '-';
        Convert.TryToHexStringLower(hash, chars[4..], out _);
        return new string(chars);
    }
}
