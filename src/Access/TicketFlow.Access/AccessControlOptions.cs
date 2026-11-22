namespace TicketFlow.Access;

/// <summary>Access engine options.</summary>
public sealed class AccessControlOptions
{
    /// <summary>Hard upper bound for <see cref="MaximumBreakGlassDuration"/>.</summary>
    public static readonly TimeSpan BreakGlassDurationCeiling = TimeSpan.FromHours(24);

    /// <summary>Longest break-glass grant that can be activated. Default 4 hours; at most 24 hours.</summary>
    public TimeSpan MaximumBreakGlassDuration { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Minimum non-whitespace justification length for break-glass activation. Default 10.</summary>
    public int MinimumBreakGlassJustificationLength { get; set; } = 10;

    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public void Validate()
    {
        if (MaximumBreakGlassDuration <= TimeSpan.Zero || MaximumBreakGlassDuration > BreakGlassDurationCeiling)
            throw new ArgumentOutOfRangeException(nameof(MaximumBreakGlassDuration), "Break-glass duration must be positive and at most 24 hours.");
        if (MinimumBreakGlassJustificationLength is < 1 or > AccessGrant.MaximumJustificationLength)
            throw new ArgumentOutOfRangeException(nameof(MinimumBreakGlassJustificationLength));
    }
}
