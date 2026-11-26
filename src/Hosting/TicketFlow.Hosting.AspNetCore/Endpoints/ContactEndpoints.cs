using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore.Contracts;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class ContactEndpoints
{
    public const string AuditEventHeader = "X-TicketFlow-Audit-Event";

    private const int NdjsonFlushThresholdBytes = 32_768;

    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/contacts", async (TicketFlowActor actor, [FromServices] IContactService service, HttpContext http, string? q, string? kind, string? @namespace, int? limit, string? cursor) =>
        {
            var size = limit ?? ContactSearch.DefaultLimit;
            var search = kind is null
                ? ContactSearch.ByName(q, size)
                : ContactSearch.ByIdentity(TicketFlowHttp.Parse<ContactIdentityKind>(kind, "kind"), q ?? string.Empty, @namespace, size);
            return await service.SearchAsync(actor.Principal, search, cursor, http.RequestAborted) switch
            {
                ContactSearchPage page => TicketFlowHttp.Ok(http, new PageResponse<ContactResponse>([.. page.Contacts.Select(Dto)], page.NextCursor)),
                ContactRefused refused => Refused(http, refused),
            };
        }).WithName("SearchContacts").Produces<PageResponse<ContactResponse>>();
        group.MapGet("/contacts/{contactId}", async (TicketFlowActor actor, string contactId, [FromServices] IContactService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new ContactId(contactId), http.RequestAborted), view => TicketFlowHttp.Ok(http, Dto(view), view.Revision)))
            .WithName("GetContact").Produces<ContactResponse>();
        group.MapPost("/contacts", async (TicketFlowActor actor, ContactCreateRequest request, [FromServices] IContactService service, [FromServices] TimeProvider time, HttpContext http) =>
        {
            var draft = new ContactDraft(Profile(request.Profile), request.Identities?.Select(identity => Identity(identity, time)).ToList(),
                request.Attributes?.ToDictionary(pair => pair.Key, pair => Attributes.Contact(pair.Value), StringComparer.Ordinal),
                request.Labels?.Select(label => new LabelId(label)).ToList(), request.Blocked, request.Id is null ? null : new ContactId(request.Id));
            return Map(http, await service.CreateAsync(actor.Principal, draft, http.RequestAborted),
                view => TicketFlowHttp.Created(http, $"contacts/{view.Id.Value}", Dto(view), view.Revision));
        }).WithName("CreateContact").Produces<ContactResponse>(StatusCodes.Status201Created);
        group.MapPatch("/contacts/{contactId}", async (TicketFlowActor actor, string contactId, ContactUpdateRequest request, [FromServices] IContactService service, [FromServices] TimeProvider time, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            var edits = (request.Edits ?? throw new ArgumentException("Edits are required.", nameof(request))).Select(edit => Edit(edit, time)).ToList();
            return Map(http, await service.UpdateAsync(actor.Principal, new ContactId(contactId), revision, edits, http.RequestAborted), view => TicketFlowHttp.Ok(http, Dto(view), view.Revision));
        }).WithName("UpdateContact").Produces<ContactResponse>();
        group.MapDelete("/contacts/{contactId}", async (TicketFlowActor actor, string contactId, string? justification, [FromServices] IContactService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new ContactId(contactId), revision, justification, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteContact").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/contacts/{contactId}/reveal", async (TicketFlowActor actor, string contactId, FieldRevealRequest request, [FromServices] IContactService service, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await service.RevealAsync(actor.Principal, new ContactId(contactId), new FieldPath(request.Field), request.Justification, http.RequestAborted) switch
            {
                ContactFieldRevealed revealed => TicketFlowHttp.Ok(http, new ContactRevealResponse(revealed.Field.Value,
                    [.. revealed.Identities.Select(identity => new ContactIdentityValueResponse(TicketFlowHttp.Token(identity.Kind), identity.Namespace, identity.Value))],
                    revealed.Attribute is { } attribute ? Attributes.Dto(attribute) : null, revealed.AuditEventId)),
                ContactRevealRefused refused => TicketFlowHttp.Problem(StatusCodes.Status403Forbidden, "reveal-refused", [TicketFlowHttp.Token(refused.Reason)],
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["auditEventId"] = refused.AuditEventId }),
                ContactRefused refused => Refused(http, refused),
            };
        }).WithName("RevealContactField").Produces<ContactRevealResponse>();
        group.MapPost("/contacts/merge", async (TicketFlowActor actor, ContactsMergeRequest request, [FromServices] IContactService service, HttpContext http) =>
        {
            var merge = new ContactMergeRequest(new ContactId(request.Target), request.TargetRevision, new ContactId(request.Source), request.SourceRevision,
                request.AttributeConflict is null ? ContactAttributeConflict.PreferTarget : TicketFlowHttp.Parse<ContactAttributeConflict>(request.AttributeConflict, "attributeConflict"),
                request.Justification);
            return Map(http, await service.MergeAsync(actor.Principal, merge, http.RequestAborted),
                outcome => TicketFlowHttp.Ok(http, new ContactMergeResponse(Dto(outcome.Target), outcome.MergeId, outcome.AuditEventId), outcome.Target.Revision));
        }).WithName("MergeContacts").Produces<ContactMergeResponse>();
        group.MapPost("/contacts/merges/{mergeId:guid}/unmerge", async (TicketFlowActor actor, Guid mergeId, [FromServices] IContactService service, HttpContext http) =>
            Map(http, await service.UnmergeAsync(actor.Principal, mergeId, http.RequestAborted),
                outcome => TicketFlowHttp.Ok(http, new ContactUnmergeResponse(Dto(outcome.Target), Dto(outcome.Source), outcome.AuditEventId))))
            .WithName("UnmergeContacts").Produces<ContactUnmergeResponse>();
        group.MapPost("/contacts/export", async (TicketFlowActor actor, ContactExportStartRequest request, [FromServices] IContactService service, [FromServices] IOptions<TicketFlowEndpointOptions> options, HttpContext http) =>
        {
            if (request.MaximumContacts > options.Value.MaximumExportContacts)
                throw new ArgumentException($"Exports are limited to {options.Value.MaximumExportContacts} contacts.", nameof(request));
            return await service.ExportAsync(actor.Principal, new ContactExportRequest(request.MaximumContacts, request.Justification), http.RequestAborted) switch
            {
                ContactExportStarted started => Stream(http, started),
                ContactRefused refused => Refused(http, refused),
            };
        }).WithName("ExportContacts").Produces<ContactResponse>(StatusCodes.Status200OK, "application/x-ndjson");

        group.MapGet("/contact-attributes", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] IContactAttributeDefinitionService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Definition.Key, Definition, limit, cursor))))
            .WithName("ListContactAttributes").Produces<PageResponse<AttributeDefinitionResponse>>();
        group.MapPost("/contact-attributes", async (TicketFlowActor actor, AttributeDefinitionRequest request, [FromServices] IContactAttributeDefinitionService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Definition(request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"contact-attributes/{stored.Definition.Key}", Definition(stored), stored.Revision)))
            .WithName("CreateContactAttribute").Produces<AttributeDefinitionResponse>(StatusCodes.Status201Created);
        group.MapPut("/contact-attributes/{key}", async (TicketFlowActor actor, string key, AttributeDefinitionRequest request, [FromServices] IContactAttributeDefinitionService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            if (!string.Equals(request.Key, key, StringComparison.Ordinal))
                throw new ArgumentException("The body key must match the route.", nameof(key));
            return Map(http, await service.UpdateAsync(actor.Principal, Definition(request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Definition(stored), stored.Revision));
        }).WithName("UpdateContactAttribute").Produces<AttributeDefinitionResponse>();
        group.MapDelete("/contact-attributes/{key}", async (TicketFlowActor actor, string key, [FromServices] IContactAttributeDefinitionService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, key, revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteContactAttribute").Produces(StatusCodes.Status204NoContent);
        return group;
    }

    private static NdjsonContactExportResult Stream(HttpContext http, ContactExportStarted started)
    {
        http.Response.Headers[AuditEventHeader] = started.AuditEventId.ToString("D");
        http.Response.Headers.CacheControl = "no-store";
        var json = http.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions ?? JsonSerializerOptions.Web;
        return new NdjsonContactExportResult(started.Contacts, json);
    }

    private sealed class NdjsonContactExportResult(IAsyncEnumerable<ContactView> contacts, JsonSerializerOptions options) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = "application/x-ndjson";
            var pipe = httpContext.Response.BodyWriter;
            var typeInfo = options.GetTypeInfo<ContactResponse>();
            var cancellationToken = httpContext.RequestAborted;
            using var writer = new Utf8JsonWriter(pipe);
            long buffered = 0;
            await foreach (var contact in contacts.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                writer.Reset(pipe);
                JsonSerializer.Serialize(writer, Dto(contact), typeInfo);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                var span = pipe.GetSpan(1);
                span[0] = (byte)'\n';
                pipe.Advance(1);
                buffered += writer.BytesCommitted + 1;
                if (buffered >= NdjsonFlushThresholdBytes)
                {
                    var flush = await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (flush.IsCanceled || flush.IsCompleted)
                        return;
                    buffered = 0;
                }
            }
            if (buffered > 0)
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static IResult Map<T>(HttpContext http, TicketResult<T, ContactRefused> result, Func<T, IResult> success) => result switch
    {
        TicketValue<T> applied => success(applied.Value),
        ContactRefused refused => Refused(http, refused),
    };

    internal static IResult Refused(HttpContext http, ContactRefused refused) => refused.Reason switch
    {
        ContactRefusal.Forbidden => TicketFlowHttp.Refused(http, RefusalKind.Forbidden, refused.Errors),
        ContactRefusal.Invalid => TicketFlowHttp.Refused(http, RefusalKind.Invalid, refused.Errors),
        ContactRefusal.NotFound => TicketFlowHttp.Refused(http, RefusalKind.NotFound, refused.Errors),
        ContactRefusal.Conflict => TicketFlowHttp.Refused(http, RefusalKind.Conflict, refused.Errors, refused.CurrentRevision),
        ContactRefusal.IdentityTaken => TicketFlowHttp.Problem(StatusCodes.Status409Conflict, "identity-taken", refused.Errors),
        ContactRefusal.Merged => TicketFlowHttp.Problem(StatusCodes.Status409Conflict, "merged", refused.Errors,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["mergedInto"] = refused.MergedInto?.Value }),
        _ => TicketFlowHttp.Refused(http, RefusalKind.Rejected, refused.Errors),
    };

    internal static ContactResponse Dto(ContactView view) => new(
        view.Id.Value,
        view.Revision,
        new ContactProfileModel(view.Profile.DisplayName, view.Profile.AvatarRef, view.Profile.Locale, view.Profile.TimeZone, view.Profile.Company),
        [.. view.Identities.Select(identity => new ContactIdentityResponse(TicketFlowHttp.Token(identity.Kind), identity.Namespace, identity.Value, identity.Verification.IsVerified,
            identity.Verification.Source, identity.Verification.At, identity.Masked, identity.Revealable))],
        [.. view.Attributes.Select(attribute => new ContactAttributeResponse(attribute.Key, TicketFlowHttp.Token(attribute.Type), attribute.Sensitive,
            attribute.Value is { } value ? Attributes.Dto(value) : null, attribute.Masked, attribute.Revealable))],
        [.. view.Labels.Select(label => label.Value)],
        view.Blocked,
        view.CreatedAt,
        view.UpdatedAt,
        Attributes.Masked(view.MaskedFields));

    private static ContactProfile Profile(ContactProfileModel? profile) =>
        profile is null ? ContactProfile.Empty : new ContactProfile(profile.DisplayName, profile.AvatarRef, profile.Locale, profile.TimeZone, profile.Company);

    private static ContactIdentity Identity(ContactIdentityInput? input, [FromServices] TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(input);
        var address = ContactIdentityAddress.Parse(TicketFlowHttp.Parse<ContactIdentityKind>(input.Kind, "kind"), input.Value, input.Namespace);
        var now = time.GetUtcNow();
        IdentityVerification verification = input.Verified ? new IdentityVerified(input.Source, now) : new IdentityClaimed(input.Source, now);
        return new ContactIdentity(address, verification);
    }

    private static ContactEdit Edit(ContactEditInput edit, [FromServices] TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return edit.Kind switch
        {
            "setProfile" => new SetContactProfile(Profile(edit.Profile)),
            "setAttribute" => new SetContactAttribute(edit.Key ?? throw Missing("key"), Attributes.Contact(edit.Value)),
            "removeAttribute" => new RemoveContactAttribute(edit.Key ?? throw Missing("key")),
            "addLabel" => new AddContactLabel(new LabelId(edit.Label ?? throw Missing("label"))),
            "removeLabel" => new RemoveContactLabel(new LabelId(edit.Label ?? throw Missing("label"))),
            "setBlocked" => new SetContactBlocked(edit.Blocked ?? throw Missing("blocked")),
            "addIdentity" => new AddContactIdentity(Identity(edit.Identity, time)),
            "removeIdentity" => new RemoveContactIdentity(Identity(edit.Identity, time).Address),
            _ => throw new ArgumentException("Unknown contact edit kind.", nameof(edit)),
        };
    }

    private static ArgumentException Missing(string property) => new($"The '{property}' property is required for this edit.", property);

    private static ContactAttributeDefinition Definition(AttributeDefinitionRequest request) =>
        new(request.Key, TicketFlowHttp.Parse<ContactAttributeType>(request.Type, "type"), request.Sensitive, request.DisplayName);

    private static AttributeDefinitionResponse Definition(StoredContactAttributeDefinition stored) =>
        new(stored.Definition.Key, TicketFlowHttp.Token(stored.Definition.Type), stored.Definition.Sensitive, stored.Definition.DisplayName, stored.Revision);
}
