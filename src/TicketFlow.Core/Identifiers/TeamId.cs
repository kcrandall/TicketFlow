namespace TicketFlow.Core;

/// <summary>Validated opaque identifier of a team of agents. <c>default</c> is uninitialized and rejected where an identifier is required.</summary>
public readonly record struct TeamId
{
    private readonly string? value;

    public TeamId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <summary>The exact validated identifier text.</summary>
    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The TeamId is uninitialized.");

    public override string ToString() => value ?? string.Empty;
}
