using Bunit;
using Microsoft.AspNetCore.Http;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.UI.Blazor.Components;
using TicketFlow.UI.Blazor.Infrastructure;
using TicketFlow.Workspaces;

namespace TicketFlow.UI.Blazor.Tests;

[TestClass]
public sealed class OutcomeTests
{
    [TestMethod]
    public void ConversationResultsMapToMessages()
    {
        Assert.AreEqual(UiOutcomeKind.Success, UiOutcomes.From(new ConversationApplied(new ConversationId("c"), 2, [], false), "Reply sent").Kind);
        var conflict = UiOutcomes.From(new ConversationConflict(7), "Reply sent");
        Assert.AreEqual(UiOutcomeKind.Conflict, conflict.Kind);
        Assert.AreEqual(7, conflict.CurrentRevision);
        StringAssert.Contains(conflict.Messages[0], "revision 7", StringComparison.Ordinal);
        Assert.AreEqual(UiOutcomeKind.NotFound, UiOutcomes.From(ConversationNotFound.Instance, "x").Kind);
        Assert.AreEqual(UiOutcomeKind.Forbidden, UiOutcomes.From(ConversationRefused.Forbidden, "x").Kind);
        var closed = UiOutcomes.From(ConversationRefused.Closed, "x");
        Assert.AreEqual(UiOutcomeKind.Rejected, closed.Kind);
        StringAssert.Contains(closed.Title, "closed", StringComparison.Ordinal);
    }

    [TestMethod]
    public void WorkspaceAccessAndContactRefusalsKeepValidationMessagesAndRevision()
    {
        var invalid = UiOutcomes.From<StoredInbox>(new TicketFailure(TicketFailureKind.Invalid, ["Names are 1-128 characters."]), _ => UiOutcome.Success("ok"));
        Assert.AreEqual(UiOutcomeKind.Invalid, invalid.Kind);
        CollectionAssert.AreEqual(new[] { "Names are 1-128 characters." }, invalid.Messages.ToArray());

        var stale = UiOutcomes.From<StoredAccessRole>(new TicketFailure(TicketFailureKind.Conflict, [], 4), _ => UiOutcome.Success("ok"));
        Assert.AreEqual(UiOutcomeKind.Conflict, stale.Kind);
        Assert.AreEqual(4, stale.CurrentRevision);

        var taken = UiOutcomes.From<ContactView>(new ContactRefused(ContactRefusal.IdentityTaken, ["That email belongs to another contact."]), _ => UiOutcome.Success("ok"));
        Assert.AreEqual(UiOutcomeKind.Conflict, taken.Kind);

        var forbidden = UiOutcomes.From<StoredAccessRole>(new TicketFailure(TicketFailureKind.Forbidden, []), _ => UiOutcome.Success("ok"));
        Assert.AreEqual(UiOutcomeKind.Forbidden, forbidden.Kind);
        Assert.IsNotEmpty(forbidden.Messages);

        // Non-concurrency conflict codes preserve actual error message instead of concurrency-conflict banner
        var alreadyExists = UiOutcome.FromFailure(TicketFailure.Conflict(1, ["Service account 'bot' already exists."], code: "already-exists"));
        Assert.AreEqual(UiOutcomeKind.Conflict, alreadyExists.Kind);
        Assert.AreNotEqual("Someone else changed this record", alreadyExists.Title);
        CollectionAssert.Contains(alreadyExists.Messages.ToArray(), "Service account 'bot' already exists.");

        var disabled = UiOutcome.FromFailure(TicketFailure.Conflict(2, ["Cannot issue an API key for a disabled service account."], code: "service-account-disabled"));
        Assert.AreEqual(UiOutcomeKind.Conflict, disabled.Kind);
        StringAssert.Contains(disabled.Title, "disabled", StringComparison.Ordinal);
        CollectionAssert.Contains(disabled.Messages.ToArray(), "Cannot issue an API key for a disabled service account.");
    }

    [TestMethod]
    public void AlertRendersOutcomeKindAndMessages()
    {
        using var context = UiTestSupport.Context();
        var alert = context.Render<OutcomeAlert>(parameters => parameters.Add(p => p.Outcome, UiOutcome.Conflict(9)));

        var root = alert.Find(".tf-alert");
        Assert.AreEqual("conflict", root.GetAttribute("data-outcome"));
        Assert.AreEqual("alert", root.GetAttribute("role"));
        StringAssert.Contains(alert.Find(".tf-alert__title").TextContent, "Someone else changed this record", StringComparison.Ordinal);
        StringAssert.Contains(alert.Find(".tf-alert__messages").TextContent, "revision 9", StringComparison.Ordinal);

        var success = context.Render<OutcomeAlert>(parameters => parameters.Add(p => p.Outcome, UiOutcome.Success("Reply sent")));
        Assert.AreEqual("status", success.Find(".tf-alert").GetAttribute("role"));
        Assert.IsEmpty(success.FindAll(".tf-alert__messages"));
    }

    [TestMethod]
    public void FlashRoundTripsThroughACookieAndIsConsumedOnce()
    {
        var options = new TicketFlowBlazorUIOptions();
        var post = new DefaultHttpContext();
        UiFlash.Write(post, options, new UiOutcome(UiOutcomeKind.Invalid, "Check the values", ["Name is required."]));
        var cookie = post.Response.Headers.SetCookie.ToString();
        StringAssert.Contains(cookie, "httponly", StringComparison.Ordinal);
        var value = cookie.Split(';')[0];

        var get = new DefaultHttpContext();
        get.Request.Headers.Cookie = value;
        var flash = UiFlash.Take(get, options);
        Assert.IsNotNull(flash);
        Assert.AreEqual(UiOutcomeKind.Invalid, flash.Kind);
        CollectionAssert.AreEqual(new[] { "Name is required." }, flash.Messages.ToArray());
        StringAssert.Contains(get.Response.Headers.SetCookie.ToString(), "expires=Thu, 01 Jan 1970", StringComparison.Ordinal, "The flash cookie is deleted after it is shown.");

        var tampered = new DefaultHttpContext();
        tampered.Request.Headers.Cookie = UiFlash.CookieName + "=not-base64!";
        Assert.IsNull(UiFlash.Take(tampered, options));
    }

    [TestMethod]
    public void ReturnUrlsStayInsideTheUi()
    {
        Assert.AreEqual("/support/conversations?status=open", UiUrls.SafeReturn("/support", "/support/conversations?status=open"));
        Assert.AreEqual("/support", UiUrls.SafeReturn("/support", "https://evil.test/support"));
        Assert.AreEqual("/support", UiUrls.SafeReturn("/support", "//evil.test"));
        Assert.AreEqual("/support", UiUrls.SafeReturn("/support", "/supportx/../admin"));
        Assert.AreEqual("/support", UiUrls.SafeReturn("/support", "/admin"));
        Assert.AreEqual("/support", UiUrls.SafeReturn("/support", "/support/x\r\nSet-Cookie: a=b"), "Control characters are rejected.");
        Assert.AreEqual("/support", UiUrls.SafeReturn("/support", "/support/\tx"));
        Assert.AreEqual("/support", UiUrls.SafeReturn("/support", "/support/\u007fx"));
    }

    [TestMethod]
    public void ReportRangeHandlesBoundaryDatesWithoutThrowing()
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        foreach (var (from, to) in new (string?, string?)[]
        {
            (null, "9999-12-31"),
            (null, "0001-01-01"),
            ("0001-01-01", "9999-12-31"),
            ("9999-12-31", "9999-12-31"),
            ("0001-01-01", "0001-01-01"),
            ("9999-12-31", null),
        })
        {
            var (start, end) = UiFormat.Range(from, to, now);
            Assert.IsTrue(start < end, $"from={from}, to={to}");
            Assert.IsTrue(end - start <= TimeSpan.FromDays(366), $"from={from}, to={to}");
        }
        var (extremeStart, extremeEnd) = UiFormat.Range(null, null, DateTimeOffset.MaxValue);
        Assert.IsTrue(extremeStart < extremeEnd);
        var (minStart, minEnd) = UiFormat.Range(null, null, DateTimeOffset.MinValue);
        Assert.IsTrue(minStart < minEnd);
    }

    [TestMethod]
    public async Task UiActionsRunAsyncCatchesOverflowExceptionAsInvalidOutcome()
    {
        var result = await UiActions.RunAsync(_ => throw new OverflowException("TimeSpan overflow"), context: null!);
        Assert.AreEqual(UiOutcomeKind.Invalid, result.Outcome.Kind);
        CollectionAssert.AreEqual(new[] { "A numeric value is out of range." }, result.Outcome.Messages.ToArray());
    }
}
