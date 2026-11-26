using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Contacts.Tests;

[TestClass]
public sealed class NormalizationTests
{
    private static string Normalize(ContactIdentityKind kind, string value, string? ns = null, string? callingCode = null) =>
        ContactIdentityAddress.Normalize(kind, value, ns, callingCode) switch
        {
            ContactIdentityAddress address => address.Value,
            ContactAddressInvalid invalid => $"invalid:{invalid.Error}",
        };

    [TestMethod]
    [DataRow(" Ada.Lovelace@Example.COM ", "ada.lovelace@example.com")]
    [DataRow("user@bücher.example", "user@xn--bcher-kva.example")]
    [DataRow("user@example.com.", "user@example.com")]
    [DataRow("first+tag@sub.example.org", "first+tag@sub.example.org")]
    [DataRow("no-at-sign", "invalid:InvalidEmail")]
    [DataRow("a@b", "invalid:InvalidEmail")]
    [DataRow("a b@example.com", "invalid:InvalidEmail")]
    [DataRow("a..b@example.com", "invalid:InvalidEmail")]
    [DataRow("\"quoted\"@example.com", "invalid:InvalidEmail")]
    [DataRow("user@-bad.example", "invalid:InvalidEmail")]
    [DataRow("user@[127.0.0.1]", "invalid:InvalidEmail")]
    [DataRow("", "invalid:Empty")]
    public void EmailsAreNormalized(string input, string expected) => Assert.AreEqual(expected, Normalize(ContactIdentityKind.Email, input));

    [TestMethod]
    [DataRow("+44 20 7946 0000", null, "+442079460000")]
    [DataRow("0044 (20) 7946-0000", null, "+442079460000")]
    [DataRow("020 7946 0000", "44", "+442079460000")]
    [DataRow("(415) 555-0100", "1", "+14155550100")]
    [DataRow("4155550100", null, "invalid:InvalidPhone")]
    [DataRow("+0 123 456 789", null, "invalid:InvalidPhone")]
    [DataRow("+1234567890123456", null, "invalid:InvalidPhone")]
    [DataRow("+12345", null, "invalid:InvalidPhone")]
    [DataRow("+1 415 555 0100 ext 9", null, "invalid:InvalidPhone")]
    [DataRow("+1+4155550100", null, "invalid:InvalidPhone")]
    [DataRow("4155550100", "01", "invalid:InvalidCallingCode")]
    public void PhonesAreE164(string input, string? callingCode, string expected) =>
        Assert.AreEqual(expected, Normalize(ContactIdentityKind.Phone, input, callingCode: callingCode));

    [TestMethod]
    public void WhatsAppAcceptsBareInternationalDigits()
    {
        Assert.AreEqual("+14155550100", Normalize(ContactIdentityKind.WhatsApp, "14155550100"));
        Assert.AreEqual("+14155550100", Normalize(ContactIdentityKind.WhatsApp, "+1 415 555 0100"));
    }

    [TestMethod]
    public void OpaqueAndSocialIdentifiers()
    {
        Assert.AreEqual("Visitor-ABC", Normalize(ContactIdentityKind.WidgetVisitor, "Visitor-ABC"));
        Assert.AreEqual("Visitor-ABC", Normalize(ContactIdentityKind.WidgetVisitor, "Visitor-ABC", "widget-1"));
        Assert.AreEqual("invalid:NamespaceRequired", Normalize(ContactIdentityKind.ExternalUser, "sub-1"));
        Assert.AreEqual("Sub|1", Normalize(ContactIdentityKind.ExternalUser, "Sub|1", "identityflow"));
        Assert.AreEqual("invalid:InvalidNamespace", Normalize(ContactIdentityKind.ExternalUser, "sub", "Identity Flow"));
        Assert.AreEqual("ada_l", Normalize(ContactIdentityKind.SocialHandle, " @Ada_L ", "instagram"));
        Assert.AreEqual("invalid:InvalidCharacters", Normalize(ContactIdentityKind.SocialHandle, "ada l", "instagram"));
        Assert.AreEqual("invalid:NamespaceNotAllowed", Normalize(ContactIdentityKind.Email, "a@example.com", "x"));
        Assert.AreEqual("invalid:InvalidCharacters", Normalize(ContactIdentityKind.WidgetVisitor, "bad\u0001id"));
        Assert.AreEqual("invalid:InvalidCharacters", Normalize(ContactIdentityKind.WidgetVisitor, "bad\ud800"));
        Assert.AreEqual("invalid:TooLong", Normalize(ContactIdentityKind.WidgetVisitor, new string('x', 300)));
        Assert.AreEqual("invalid:UnknownKind", Normalize((ContactIdentityKind)99, "x"));
    }

    [TestMethod]
    public void NormalizationIsIdempotentAndRedacted()
    {
        foreach (var (kind, value, ns) in new (ContactIdentityKind, string, string?)[]
        {
            (ContactIdentityKind.Email, "User@Bücher.Example", null), (ContactIdentityKind.Phone, "+44 20 7946 0000", null),
            (ContactIdentityKind.WhatsApp, "14155550100", null), (ContactIdentityKind.SocialHandle, "@Ada", "x"),
            (ContactIdentityKind.ExternalUser, "Sub", "identityflow"),
        })
        {
            var once = ContactIdentityAddress.Parse(kind, value, ns);
            Assert.AreEqual(once, ContactIdentityAddress.Parse(kind, once.Value, ns));
            Assert.AreEqual(once, ContactJsonCodec.RestoreAddress(kind, once.Namespace, once.Value));
            Assert.DoesNotContain(once.Value, once.ToString(), StringComparison.Ordinal);
        }
        var error = Assert.ThrowsExactly<ArgumentException>(() => ContactIdentityAddress.Parse(ContactIdentityKind.Email, "secret-not-an-email"));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.ThrowsExactly<FormatException>(() => ContactJsonCodec.RestoreAddress(ContactIdentityKind.Email, "", "Upper@example.com"));
    }

    [TestMethod]
    public void FieldPathsMapIdentityKinds()
    {
        Assert.AreEqual(ContactFieldPaths.Email, ContactFieldPaths.ForIdentity(ContactIdentityKind.Email));
        Assert.AreEqual(ContactFieldPaths.Phone, ContactFieldPaths.ForIdentity(ContactIdentityKind.WhatsApp));
        Assert.AreEqual(ContactFieldPaths.Social, ContactFieldPaths.ForIdentity(ContactIdentityKind.SocialHandle));
        Assert.AreEqual(ContactFieldPaths.ExternalId, ContactFieldPaths.ForIdentity(ContactIdentityKind.WidgetVisitor));
        Assert.AreEqual("contact.attribute.national-id", ContactFieldPaths.Attribute("national-id").Value);
        Assert.ThrowsExactly<ArgumentException>(() => ContactFieldPaths.Attribute("Bad Key"));
    }
}

[TestClass]
public sealed class ModelAndCodecTests
{
    private static readonly DateTimeOffset At = TestClock.DefaultStart;

    private static Contact Sample(ContactId? mergedInto = null) => new(
        new ContactId("contact-1"),
        new ContactProfile("Ada Lovelace", "avatars/ada.png", "en-GB", "Europe/London", "Analytical Engines"),
        mergedInto is null
            ? [new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.Phone, "+44 20 7946 0000"), new IdentityClaimed("widget-form", At)),
               new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.Email, "ada@example.com"), new IdentityVerified("dkim", At.AddTicks(12345)))]
            : [],
        new Dictionary<string, ContactAttributeValue>
        {
            ["tier"] = new TextAttributeValue("gold \"quoted\" ü"),
            ["score"] = new NumberAttributeValue(-1234567.890123m),
            ["vip"] = new BooleanAttributeValue(false),
            ["birthday"] = new DateAttributeValue(new DateOnly(1815, 12, 10)),
            ["tags"] = new ListAttributeValue(["b", "a"]),
        },
        [new LabelId("vip"), new LabelId("alpha")],
        true,
        At,
        At.AddMinutes(5),
        mergedInto);

    [TestMethod]
    public void ContactsAreCanonicalAndBounded()
    {
        var contact = Sample();
        CollectionAssert.AreEqual(new[] { ContactIdentityKind.Email, ContactIdentityKind.Phone }, contact.Identities.Select(identity => identity.Address.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { "alpha", "vip" }, contact.Labels.Select(label => label.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "birthday", "score", "tags", "tier", "vip" }, contact.AttributeKeys.ToArray());
        Assert.AreEqual(At.AddTicks(12340), contact.Identities[0].Verification.At);
        Assert.DoesNotContain("Ada", contact.ToString(), StringComparison.Ordinal);
        var email = contact.Identities[0];
        Assert.ThrowsExactly<ArgumentException>(() => contact.With(identities: [email, email]));
        Assert.ThrowsExactly<ArgumentException>(() => contact.With(labels: Enumerable.Range(0, 65).Select(index => new LabelId($"l{index}"))));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactProfile(locale: "en_US"));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactProfile(timeZone: "Europe London"));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactProfile(displayName: "bad\u0007name"));
        Assert.ThrowsExactly<ArgumentException>(() => new ListAttributeValue(["a", "a"]));
        Assert.ThrowsExactly<ArgumentException>(() => contact.Tombstone(contact.Id, At));
        var tombstone = contact.Tombstone(new ContactId("target"), At.AddHours(1));
        Assert.IsEmpty(tombstone.Identities);
        Assert.AreEqual(new ContactId("target"), tombstone.MergedInto);
    }

    [TestMethod]
    public void AttributeAndContactCodecsRoundTrip()
    {
        var contact = Sample();
        Assert.AreEqual(contact, ContactJsonCodec.DeserializeContact(ContactJsonCodec.SerializeContact(contact)));
        var tombstone = Sample(new ContactId("target"));
        Assert.AreEqual(tombstone, ContactJsonCodec.DeserializeContact(ContactJsonCodec.SerializeContact(tombstone)));
        var attributes = ContactJsonCodec.DeserializeAttributes(ContactJsonCodec.SerializeAttributes(contact.Attributes));
        Assert.HasCount(5, attributes);
        foreach (var key in contact.AttributeKeys)
            Assert.AreEqual(contact.Attributes[key], attributes[key]);
        StringAssert.Contains(ContactJsonCodec.SerializeAttributes(contact.Attributes), "\"t\":\"number\",\"v\":\"-1234567.890123\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public void MergeRecordsRoundTrip()
    {
        var source = Sample();
        var actor = new AccessActor(AccessActorKind.User, "sam");
        var record = new ContactMergeRecord(Guid.NewGuid(), new ContactId("target"), source.Id, At, actor, source,
            [source.Identities[1].Address], [new ContactIdentity(source.Identities[0].Address, new IdentityClaimed("agent", At))],
            [new ContactMergeAttributeChange("tier", null, new TextAttributeValue("gold")), new ContactMergeAttributeChange("vip", new BooleanAttributeValue(true), new BooleanAttributeValue(false))],
            [new LabelId("vip")], [ContactProfileField.Company, ContactProfileField.Locale], true);
        var json = ContactJsonCodec.SerializeMergeContributions(record);
        Assert.AreEqual(record, ContactJsonCodec.DeserializeMergeRecord(json, record.Id, record.Target, record.Source, record.MergedAt, actor));
    }

    [TestMethod]
    [DataRow("{\"v\":2,\"a\":{}}")]
    [DataRow("{\"v\":1,\"a\":{},\"extra\":1}")]
    [DataRow("{\"v\":1,\"a\":{\"Bad Key\":{\"t\":\"text\",\"v\":\"x\"}}}")]
    [DataRow("{\"v\":1,\"a\":{\"k\":{\"t\":\"blob\",\"v\":\"x\"}}}")]
    [DataRow("{\"v\":1,\"a\":{\"k\":{\"t\":\"number\",\"v\":\"1e5\"}}}")]
    [DataRow("{\"v\":1,\"a\":{\"k\":{\"t\":\"date\",\"v\":\"2026-13-01\"}}}")]
    [DataRow("not json")]
    public void CodecRejectsUnknownOrInvalidPayloads(string json) =>
        Assert.ThrowsExactly<FormatException>(() => ContactJsonCodec.DeserializeAttributes(json));

    [TestMethod]
    public void UnionsAreExhaustiveAndTyped()
    {
        ContactAttributeValue[] values = [new TextAttributeValue("x"), new NumberAttributeValue(1), new BooleanAttributeValue(true), new DateAttributeValue(default), new ListAttributeValue([])];
        CollectionAssert.AreEqual(Enum.GetValues<ContactAttributeType>(), values.Select(value => value.Type).ToArray());
        var definition = new ContactAttributeDefinition("score", ContactAttributeType.Number, sensitive: true);
        Assert.IsTrue(definition.Accepts(new NumberAttributeValue(3)));
        Assert.IsFalse(definition.Accepts(new TextAttributeValue("3")));
        Assert.AreEqual("contact.attribute.score", definition.Field.Value);
        IdentityVerification verified = new IdentityVerified("otp", At);
        IdentityVerification claimed = new IdentityClaimed("agent", At);
        Assert.IsTrue(verified.IsVerified);
        Assert.IsFalse(claimed.IsVerified);
        Assert.AreEqual("agent", claimed.Source);
        Assert.ThrowsExactly<ArgumentException>(() => new IdentityVerified("Bad Source", At));
    }

    [TestMethod]
    public void SearchInputsAreBoundedAndFolded()
    {
        Assert.AreEqual("ada", ContactSearch.ByName("  ADA ").Prefix);
        Assert.AreEqual("+4420", ContactSearch.ByIdentity(ContactIdentityKind.Phone, "+44 20").Prefix);
        Assert.AreEqual("+4420", ContactSearch.ByIdentity(ContactIdentityKind.Phone, "0044 20").Prefix);
        Assert.AreEqual("ada", ContactSearch.ByIdentity(ContactIdentityKind.SocialHandle, "@ADA").Prefix);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ContactSearch.ByName(limit: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ContactSearch.ByName(limit: ContactSearch.MaximumLimit + 1));
        Assert.ThrowsExactly<ArgumentException>(() => ContactSearch.ByIdentity(ContactIdentityKind.Email, " "));
        Assert.ThrowsExactly<ArgumentException>(() => ContactSearch.ByName(new string('a', ContactSearch.MaximumPrefixLength + 1)));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactWriteBatch());
        var contact = new Contact(new ContactId("c"), ContactProfile.Empty, [], [], [], false, At, At);
        Assert.ThrowsExactly<ArgumentException>(() => new ContactWriteBatch(puts: [new ContactPut(contact, null), new ContactPut(contact, 1)]));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactResolveRequest([]));
    }

    [TestMethod]
    public void PermissionsAndAccessResource()
    {
        CollectionAssert.AreEqual(new[] { "contact-attribute:manage", "contact-linking:manage", "contact:create", "contact:delete", "contact:link" },
            ContactPermissions.Definitions.Select(definition => definition.Permission.Value).Order(StringComparer.Ordinal).ToArray());
        Assert.IsTrue(ContactPermissions.Definitions.Single(definition => definition.Permission == ContactPermissions.Delete).IsSensitive);
        Assert.AreEqual(AccessPermissions.ContactMerge, ContactPermissions.Merge);
        var scope = TestScopes.Create();
        var resource = ContactAccess.Resource(scope, Sample());
        Assert.AreEqual("contact-1", resource.ResourceId);
        Assert.IsNull(resource.TeamId);
        Assert.IsTrue(resource.Labels!.SetEquals([new LabelId("vip"), new LabelId("alpha")]));
        Assert.IsEmpty(resource.Attributes);
        Assert.IsNull(ContactAccess.Workspace(scope).Labels);
    }
}
