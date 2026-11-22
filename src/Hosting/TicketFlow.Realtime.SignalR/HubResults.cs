namespace TicketFlow.Realtime.SignalR;

/// <summary>Result of a hub call: <see cref="Ok"/> with optional <see cref="Data"/>, or an error token (<c>forbidden</c>, <c>not-found</c>, ...).</summary>
public sealed record HubResult(bool Ok, string? Error = null, object? Data = null)
{
    public static HubResult Success(object? data = null) => new(true, null, data);

    public static HubResult Failure(string error) => new(false, error);
}
