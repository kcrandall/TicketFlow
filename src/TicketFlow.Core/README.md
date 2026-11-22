# TicketFlow.Core

Validated scope, identifiers and principal references. Every `TicketScope` retains tenant, project, environment, app and workspace,
and every level participates in equality. No ASP.NET Core, database, provider SDK or sibling Flow dependency.

Identifiers contain at most 256 UTF-16 code units, exclude control characters, and require well-formed Unicode. Lone or reversed
surrogates are rejected before serialization can replace them and collapse distinct identities. Validation preserves exact text
without case or Unicode normalization. Strongly typed IDs (`WorkspaceId`, `InboxId`, `TeamId`, `ContactId`, `ConversationId`,
`MessageId`, `LabelId`, `UserId`, `ApiClientId`) cannot be confused at compile time; `default` values are uninitialized and rejected.

`PrincipalRef` is a union of `UserPrincipal`, `ApiClientPrincipal`, `AutomationPrincipal` and `SystemPrincipal`. Principals are
application-resolved references that carry scope and, for users, a bounded team-membership snapshot. They never carry credentials.
Switch over every case without a discard arm.

Time comes from an injected `TimeProvider`; TicketFlow defines no custom clock interface.

`TicketFlowBuilder` (namespace `TicketFlow`) wraps an `IServiceCollection` and is the extension point for feature registration
methods. It lives in Core so feature packages depend on Core and their abstractions only, never on the `TicketFlow` meta-package.
`Microsoft.Extensions.DependencyInjection.Abstractions` comes from the .NET 11 shared framework.
