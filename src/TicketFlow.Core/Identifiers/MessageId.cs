namespace TicketFlow.Core;

/// <summary>Validated opaque identifier of a timeline message or note. <c>default</c> is uninitialized and rejected where an identifier is required.</summary>
public readonly record struct MessageId
{
    private readonly string? value;

    public MessageId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <summary>The exact validated identifier text.</summary>
    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The MessageId is uninitialized.");

    public override string ToString() => value ?? string.Empty;
}
