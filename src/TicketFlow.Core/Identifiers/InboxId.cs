namespace TicketFlow.Core;

/// <summary>Validated opaque identifier of an inbox. <c>default</c> is uninitialized and rejected where an identifier is required.</summary>
public readonly record struct InboxId
{
    private readonly string? value;

    public InboxId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <summary>The exact validated identifier text.</summary>
    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The InboxId is uninitialized.");

    public override string ToString() => value ?? string.Empty;
}
