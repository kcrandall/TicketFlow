using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.Tests;

[TestClass]
public sealed class ModelTests
{
    [TestMethod]
    public void CapabilitiesValidateReplyWindowsAndAttachments()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelCapabilities(ChannelFeatures.ReplyWindow));
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelCapabilities(ChannelFeatures.None, TimeSpan.FromHours(24)));
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelCapabilities(ChannelFeatures.ReplyWindow, TimeSpan.FromDays(31)));
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelCapabilities(ChannelFeatures.Attachments));
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelCapabilities(ChannelFeatures.None, maximumAttachments: 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ChannelCapabilities((ChannelFeatures)4096));
        var whatsapp = new ChannelCapabilities(ChannelFeatures.ReplyWindow | ChannelFeatures.Templates, TimeSpan.FromHours(24));
        Assert.IsTrue(whatsapp.Has(ChannelFeatures.Templates));
        Assert.IsFalse(whatsapp.Has(ChannelFeatures.Threading));
    }

    [TestMethod]
    public void ReplyWindowIsHalfOpen()
    {
        var capabilities = new ChannelCapabilities(ChannelFeatures.ReplyWindow, TimeSpan.FromHours(24));
        var last = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(last.AddHours(24), Is<ReplyWindowOpen>(ChannelReplyWindow.Evaluate(capabilities, last, last.AddHours(23)).Value).Until);
        Assert.AreEqual(last.AddHours(24), Is<ReplyWindowClosed>(ChannelReplyWindow.Evaluate(capabilities, last, last.AddHours(24)).Value).ClosedAt);
        Assert.IsNull(Is<ReplyWindowClosed>(ChannelReplyWindow.Evaluate(capabilities, null, last).Value).ClosedAt);
        Assert.IsNull(Is<ReplyWindowOpen>(ChannelReplyWindow.Evaluate(new ChannelCapabilities(ChannelFeatures.None), null, last).Value).Until);
    }

    [TestMethod]
    public void FailureCodesBecomeTokens()
    {
        Assert.AreEqual("customer_service_window_closed", ChannelText.FailureCode("customer_service_window_closed"));
        Assert.AreEqual("e-30007", ChannelText.FailureCode("30007"));
        Assert.AreEqual("smtp-550-5-1-1", ChannelText.FailureCode("SMTP 550 5.1.1"));
        Assert.AreEqual("failed", ChannelText.FailureCode("  "));
        Assert.AreEqual(64, ChannelText.FailureCode(new string('x', 200)).Length);
        Assert.AreEqual(64, ChannelText.FailureCode(new string('9', 200)).Length);
    }

    [TestMethod]
    public void ReferencesNormalizeAndParse()
    {
        Assert.AreEqual("abc@host", ChannelText.NormalizeReference(" <abc@host> "));
        Assert.AreEqual("wamid.X", ChannelText.NormalizeReference("wamid.X"));
        Assert.IsNull(ChannelText.NormalizeReference("<>"));
        Assert.IsNull(ChannelText.NormalizeReference("a\u0001b"));
        CollectionAssert.AreEqual(new[] { "a@x", "b@x", "c@x" }, ChannelText.ParseReferenceHeader("<a@x> <b@x>\r\n <c@x> <a@x>").ToArray());
        CollectionAssert.AreEqual(new[] { "b@x", "c@x" }, ChannelText.ParseReferenceHeader("<a@x> <b@x> <c@x>", 2).ToArray());
        Assert.AreEqual(0, ChannelText.ParseReferenceHeader(null).Count);
    }

    [TestMethod]
    public void BodiesAreSanitizedAndBounded()
    {
        Assert.AreEqual("a b\tc\r\nd", ChannelText.SanitizeBody("a\u000Cb\tc\r\nd", 100));
        Assert.AreEqual("ab", ChannelText.SanitizeBody("abé", 3));
        Assert.AreEqual("�x", ChannelText.SanitizeBody("\uD800x", 10));
    }

    [TestMethod]
    public void InboundMessagesValidateAndRedact()
    {
        var scope = TestScopes.Create();
        var sender = new ChannelSenderEvidence(ContactIdentityKind.Email, "ada@example.com", verified: false);
        Assert.IsFalse(sender.ToString().Contains("ada", StringComparison.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => new InboundChannelMessage(scope, new ChannelKind("email"), "cfg", "m1", sender, DateTimeOffset.UtcNow));
        Assert.ThrowsExactly<ArgumentException>(() => new InboundChannelMessage(scope, new ChannelKind("email"), "cfg", "m\u0001", sender, DateTimeOffset.UtcNow, "x"));
        Assert.ThrowsExactly<ArgumentException>(() => new InboundChannelMessage(scope, new ChannelKind("email"), "cfg", "m1", sender, DateTimeOffset.UtcNow, "x",
            references: Enumerable.Range(0, 33).Select(index => $"r{index}")));
        var message = new InboundChannelMessage(scope, new ChannelKind("email"), "cfg", "m1", sender, DateTimeOffset.UtcNow, "secret body", subject: "secret subject");
        Assert.IsFalse(message.ToString().Contains("secret", StringComparison.Ordinal));
        var attachmentOnly = new InboundChannelMessage(scope, new ChannelKind("sms"), "cfg", "m2", sender, DateTimeOffset.UtcNow,
            attachments: [new AttachmentRef("ref", "a.png", "image/png", 10)]);
        Assert.AreEqual(1, attachmentOnly.Attachments.Count);
    }

    [TestMethod]
    public void SendResultsValidateCodes()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelSendRejected("Bad Code", false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ChannelSendRejected("x", true, TimeSpan.FromDays(2)));
        Assert.AreEqual("outcome-unknown", new ChannelSendUnknown().Code);
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelDeliveryReport(TestScopes.Create(), new ChannelKind("sms"), "cfg", "x", ChannelDeliveryState.Sent,
            DateTimeOffset.UtcNow, "code"));
    }

    [TestMethod]
    public void RegistryRejectsDuplicateKinds()
    {
        var kind = new ChannelKind("email");
        var capabilities = new ChannelCapabilities(ChannelFeatures.None);
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelRegistry([new Testing.FakeChannel(kind, capabilities), new Testing.FakeChannel(kind, capabilities)]));
        var registry = new ChannelRegistry([new Testing.FakeChannel(kind, capabilities)]);
        Assert.IsNotNull(registry.Find(kind));
        Assert.IsNull(registry.Find(new ChannelKind("sms")));
        Assert.IsNull(registry.Find(default));
    }

    private static T Is<T>(object? value) where T : class
    {
        Assert.IsInstanceOfType<T>(value);
        return (T)value!;
    }
}
