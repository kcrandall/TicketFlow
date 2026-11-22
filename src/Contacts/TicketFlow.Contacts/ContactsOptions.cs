using TicketFlow.Core;

namespace TicketFlow.Contacts;

public sealed class ContactsOptions
{
    public const int ExportCeiling = 1_000_000;

    /// <summary>How claimed identities match during resolve-or-create when no verified identity matches. Default: unique claimed holder.</summary>
    public ClaimedIdentityMatching ClaimedIdentityMatching { get; set; } = ClaimedIdentityMatching.WhenUnique;

    /// <summary>Generates identifiers for new contacts. Default: a version 7 GUID in "N" format.</summary>
    public Func<ContactId> IdFactory { get; set; } = () => new ContactId(Guid.CreateVersion7().ToString("N"));

    /// <summary>Upper bound for <see cref="ContactExportRequest.MaximumContacts"/> (default 10,000, ceiling 1,000,000).</summary>
    public int MaximumExportContacts { get; set; } = 10_000;

    /// <summary>Contacts read per store page while exporting (1-100, default 100).</summary>
    public int ExportPageSize { get; set; } = 100;

    internal void Validate()
    {
        if (!Enum.IsDefined(ClaimedIdentityMatching) || IdFactory is null || MaximumExportContacts is < 1 or > ExportCeiling
            || ExportPageSize is < 1 or > ContactSearch.MaximumLimit)
            throw new InvalidOperationException("Contact options are invalid.");
    }
}
