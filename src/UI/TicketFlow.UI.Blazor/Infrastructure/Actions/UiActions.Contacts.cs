using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Core;

namespace TicketFlow.UI.Blazor.Infrastructure;

public static partial class UiActions
{
    private static void RegisterContacts(Dictionary<string, UiActionHandler> handlers)
    {
        handlers["contact.create"] = CreateContactAsync;
        handlers["contact.profile"] = context => EditContactAsync(context, "Profile saved",
            new SetContactProfile(new ContactProfile(context.Text("name"), null, context.Text("locale"), context.Text("timezone"), context.Text("company"))));
        handlers["contact.block"] = context => EditContactAsync(context, context.Text("blocked") == "true" ? "Contact blocked" : "Contact unblocked",
            new SetContactBlocked(context.Text("blocked") == "true"));
        handlers["contact.labels.add"] = context => EditContactAsync(context, "Label added",
            new AddContactLabel(new LabelId(context.Require("label", "Label").ToLowerInvariant().Replace(' ', '-'))));
        handlers["contact.labels.remove"] = context => EditContactAsync(context, "Label removed", new RemoveContactLabel(new LabelId(context.Require("label", "Label"))));
        handlers["contact.reveal"] = RevealAsync;
        handlers["contact.merge"] = MergeAsync;
    }

    private static ContactId ContactFromPath(UiActionContext context) =>
        context.Session.Path.Split('/') is ["contacts", var id] ? new ContactId(Uri.UnescapeDataString(id)) : throw new UiInputException("The contact is missing.");

    private static async Task<UiActionResult> CreateContactAsync(UiActionContext context)
    {
        if (context.Session.Service<IContactService>() is not { } contacts)
            return UiOutcome.NotFound();
        var now = TimeProvider.System.GetUtcNow();
        var identities = new List<ContactIdentity>();
        if (context.Text("email") is { } email)
            identities.Add(new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.Email, email), new IdentityClaimed("agent", now)));
        if (context.Text("phone") is { } phone)
            identities.Add(new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.Phone, phone), new IdentityClaimed("agent", now)));
        var draft = new ContactDraft(new ContactProfile(context.Require("name", "Name"), company: context.Text("company")), identities);
        return UiOutcomes.From(await contacts.CreateAsync(context.Actor, draft, context.Aborted), view => UiOutcome.Success("Contact created", $"contacts/{view.Id.Value}"));
    }

    private static async Task<UiActionResult> EditContactAsync(UiActionContext context, string success, ContactEdit edit)
    {
        if (context.Session.Service<IContactService>() is not { } contacts)
            return UiOutcome.NotFound();
        return UiOutcomes.From(await contacts.UpdateAsync(context.Actor, ContactFromPath(context), context.RequireRevision(), [edit], context.Aborted), _ => UiOutcome.Success(success));
    }

    /// <summary>Reveals one masked field: the value is rendered once in this response (no-store) and never stored or redirected.</summary>
    private static async Task<UiActionResult> RevealAsync(UiActionContext context)
    {
        if (context.Session.Service<IContactService>() is not { } contacts)
            return UiOutcome.NotFound();
        var justification = context.Require("justification", "A justification");
        return await contacts.RevealAsync(context.Actor, ContactFromPath(context), new FieldPath(context.Require("field", "Field")), justification, context.Aborted) switch
        {
            ContactFieldRevealed revealed => new UiActionResult(new UiOutcome(UiOutcomeKind.Info, "Field revealed and audited", ["The value is shown once and is not kept in the page history."]), revealed),
            ContactRevealRefused refused => new UiOutcome(UiOutcomeKind.Forbidden, "Reveal refused", [refused.Reason switch
            {
                RevealRefusal.ReadDenied => "You can't read this contact.",
                RevealRefusal.NoPolicy => "No field policy allows revealing this field.",
                _ => "Your role does not allow revealing this field.",
            }, "The attempt was recorded in the audit log."]),
            ContactRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors, refused.CurrentRevision),
        };
    }

    private static async Task<UiActionResult> MergeAsync(UiActionContext context)
    {
        if (context.Session.Service<IContactService>() is not { } contacts)
            return UiOutcome.NotFound();
        var target = ContactFromPath(context);
        var source = new ContactId(context.Require("source", "The duplicate contact"));
        if (source == target)
            return UiOutcome.Invalid("Choose a different contact to merge.");
        if (await contacts.GetAsync(context.Actor, source, context.Aborted) is not ContactApplied<ContactView> duplicate)
            return UiOutcome.Invalid("The duplicate contact was not found.");
        var request = new ContactMergeRequest(target, context.RequireRevision(), source, duplicate.Value.Revision, ContactAttributeConflict.PreferTarget, context.Text("justification"));
        return UiOutcomes.From(await contacts.MergeAsync(context.Actor, request, context.Aborted), _ => UiOutcome.Success("Contacts merged"));
    }
}
