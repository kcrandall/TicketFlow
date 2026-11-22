using TicketFlow.Automation;
using TicketFlow.Core;
using TicketFlow.Routing;
using TicketFlow.Sla;
using TicketFlow.Workspaces;

namespace TicketFlow.UI.Blazor.Infrastructure;

public static partial class UiActions
{
    private static void RegisterOperations(Dictionary<string, UiActionHandler> handlers)
    {
        handlers["routing.status"] = context => WithAsync<IAgentAvailabilityService>(context, async availability =>
            UiOutcomes.From(await availability.SetStatusAsync(context.Actor, new UserId(context.Require("agent", "Agent")), Enum.Parse<AgentStatus>(context.Require("status", "Status"), ignoreCase: true),
                context.Aborted), profile => UiOutcome.Success($"Status set to {profile.Presence.Status}")));
        handlers["routing.policy.save"] = context => WithAsync<IRoutingPolicyService>(context, async policies =>
        {
            var (id, isNew) = DefinitionId(context);
            var policy = RoutingPolicyCodec.Deserialize(new RoutingPolicyId(id), context.RequireRaw("definition", "The definition"));
            return UiOutcomes.From(isNew ? await policies.CreateAsync(context.Actor, policy, context.Aborted) : await policies.UpdateAsync(context.Actor, policy, context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success(isNew ? "Routing policy created" : "Routing policy saved"));
        });
        handlers["routing.policy.delete"] = context => WithAsync<IRoutingPolicyService>(context, async policies =>
            UiOutcomes.From(await policies.DeleteAsync(context.Actor, new RoutingPolicyId(context.Require("id", "Policy")), context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Routing policy deleted")));

        handlers["sla.policy.save"] = context => WithAsync<ISlaPolicyService>(context, async policies =>
        {
            var (id, isNew) = DefinitionId(context);
            var policy = SlaPolicyCodec.Deserialize(new SlaPolicyId(id), context.RequireRaw("definition", "The definition"));
            return UiOutcomes.From(isNew ? await policies.CreateAsync(context.Actor, policy, context.Aborted) : await policies.UpdateAsync(context.Actor, policy, context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success(isNew ? "SLA policy created" : "SLA policy saved"));
        });
        handlers["sla.policy.delete"] = context => WithAsync<ISlaPolicyService>(context, async policies =>
            UiOutcomes.From(await policies.DeleteAsync(context.Actor, new SlaPolicyId(context.Require("id", "Policy")), context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("SLA policy deleted")));

        handlers["automation.rule.save"] = context => WithAsync<IAutomationRuleService>(context, async rules =>
        {
            var existing = context.Text("id");
            var id = existing ?? context.Text("newid") ?? NewId();
            var rule = AutomationCodec.DeserializeRule(new AutomationRuleId(id), context.RequireRaw("definition", "The definition"));
            return existing is null
                ? UiOutcomes.From(await rules.CreateAsync(context.Actor, rule, context.Aborted), stored => UiOutcome.Success("Rule created", $"automation/rules/{stored.Id.Value}"))
                : UiOutcomes.From(await rules.UpdateAsync(context.Actor, rule, context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Rule saved"));
        });
        handlers["automation.rule.delete"] = context => WithAsync<IAutomationRuleService>(context, async rules =>
            UiOutcomes.From(await rules.DeleteAsync(context.Actor, new AutomationRuleId(context.Require("id", "Rule")), context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Rule deleted", "automation")));
        handlers["automation.rule.dryrun"] = DryRunAsync;

        handlers["automation.macro.save"] = context => WithAsync<IMacroService>(context, async macros =>
        {
            var (id, isNew) = DefinitionId(context);
            var macro = AutomationCodec.DeserializeMacro(new MacroId(id), context.RequireRaw("definition", "The definition"));
            return UiOutcomes.From(isNew ? await macros.CreateAsync(context.Actor, macro, context.Aborted) : await macros.UpdateAsync(context.Actor, macro, context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success(isNew ? "Macro created" : "Macro saved"));
        });
        handlers["automation.macro.delete"] = context => WithAsync<IMacroService>(context, async macros =>
            UiOutcomes.From(await macros.DeleteAsync(context.Actor, new MacroId(context.Require("id", "Macro")), context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Macro deleted")));
        handlers["automation.canned.save"] = context => WithAsync<ICannedResponseService>(context, async canned =>
        {
            var (id, isNew) = DefinitionId(context);
            var response = AutomationCodec.DeserializeCannedResponse(new CannedResponseId(id), context.RequireRaw("definition", "The definition"));
            return UiOutcomes.From(isNew ? await canned.CreateAsync(context.Actor, response, context.Aborted) : await canned.UpdateAsync(context.Actor, response, context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success(isNew ? "Canned response created" : "Canned response saved"));
        });
        handlers["automation.canned.delete"] = context => WithAsync<ICannedResponseService>(context, async canned =>
            UiOutcomes.From(await canned.DeleteAsync(context.Actor, new CannedResponseId(context.Require("id", "Canned response")), context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success("Canned response deleted")));
    }

    /// <summary>The definition's identifier: an existing record when a revision was posted, otherwise a new one (posted id or generated).</summary>
    private static (string Id, bool IsNew) DefinitionId(UiActionContext context) =>
        context.Revision is null ? (context.Text("id") ?? NewId(), true) : (context.Require("id", "Identifier"), false);

    private static async Task<UiActionResult> DryRunAsync(UiActionContext context)
    {
        if (context.Session.Service<IAutomationRuleService>() is not { } rules)
            return UiOutcome.NotFound();
        var draft = AutomationCodec.DeserializeRule(new AutomationRuleId("draft"), context.RequireRaw("definition", "The definition"));
        var automationEvent = context.Text("event") switch
        {
            "conversation-created" => AutomationEvent.ConversationCreated(),
            "message-sent" => AutomationEvent.MessageSent(),
            "assignment-changed" => AutomationEvent.AssignmentChanged(),
            "contact-updated" => AutomationEvent.ContactUpdated(),
            _ => AutomationEvent.MessageReceived(),
        };
        var request = new AutomationDryRunRequest(new ConversationId(context.Require("conversation", "A conversation")), automationEvent, null, draft, context.Text("message"));
        return await rules.DryRunAsync(context.Actor, request, context.Aborted) switch
        {
            AutomationValue<AutomationDryRunResult> result => new UiActionResult(new UiOutcome(UiOutcomeKind.Info, "Dry run complete — nothing was changed",
                [result.Value.Rules.Any(rule => rule.Match == AutomationMatch.Matched) ? "The rule would run." : "The rule would not run for this conversation."]), result.Value),
            AutomationNotFound => UiOutcome.NotFound(),
            AutomationConflict conflict => UiOutcome.Conflict(conflict.CurrentRevision),
            AutomationRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
        };
    }
}
