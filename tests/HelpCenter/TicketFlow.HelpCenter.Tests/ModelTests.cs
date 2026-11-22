using TicketFlow.Access;
using TicketFlow.Testing;
using static TicketFlow.HelpCenter.StoreContracts.HelpCenterStoreContract;

namespace TicketFlow.HelpCenter.Tests;

[TestClass]
public sealed class ModelTests
{
    [TestMethod]
    [DataRow("en", "en")]
    [DataRow("PT-br", "pt-BR")]
    [DataRow("zh-hant-tw", "zh-Hant-TW")]
    [DataRow("es-419", "es-419")]
    [DataRow("de-CH-1996", "de-CH-1996")]
    public void LocalesAreCanonicalized(string input, string canonical) => Assert.AreEqual(canonical, new HelpCenterLocale(input).Value);

    [TestMethod]
    [DataRow("")]
    [DataRow("e")]
    [DataRow("english")]
    [DataRow("en_US")]
    [DataRow("en-")]
    [DataRow("en-a")]
    [DataRow("en-US-x-y-z")]
    [DataRow("1n")]
    public void InvalidLocalesAreRejected(string input)
    {
        Assert.IsFalse(HelpCenterLocale.TryParse(input, out _));
        Assert.ThrowsExactly<ArgumentException>(() => new HelpCenterLocale(input));
    }

    [TestMethod]
    public void SlugsTagsAndDomainsAreValidated()
    {
        Assert.IsTrue(HelpCenterSlug.IsValid("reset-your-password-2"));
        foreach (var bad in new[] { "", "-a", "a-", "a--b", "A", "a b", "ä", new string('a', 129) })
            Assert.IsFalse(HelpCenterSlug.IsValid(bad), bad);
        Assert.ThrowsExactly<ArgumentException>(() => Translation(English, "t", "s", "b", "Bad Tag"));
        Assert.ThrowsExactly<ArgumentException>(() => Translation(English, "t", "s", "b", [.. Enumerable.Range(0, 17).Select(index => "t" + index)]));
        Assert.HasCount(1, Translation(English, "t", "s", "b", "faq", "faq").Tags);
        Assert.AreEqual("help.example.com", Portal.RequireDomain("help.example.com"));
        foreach (var bad in new[] { "localhost", "Help.example.com", "-a.example.com", "a..b", "a.b/c", "javascript:alert(1)" })
            Assert.ThrowsExactly<ArgumentException>(() => Portal.RequireDomain(bad), bad);
    }

    [TestMethod]
    public void TextIsBounded()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Translation(English, new string('t', 257), "s", "b"));
        Assert.ThrowsExactly<ArgumentException>(() => Translation(English, "t", "s", new string('b', ArticleTranslation.MaximumBodyLength + 1)));
        Assert.ThrowsExactly<ArgumentException>(() => Translation(English, "line\nbreak", "s", "b"));
        Assert.ThrowsExactly<ArgumentException>(() => Translation(English, "t", "s", "bad \ud800"));
        Assert.ThrowsExactly<ArgumentException>(() => Translation(English, "t", "s", "bell\u0007"));
        Assert.AreEqual("multi\nline\tbody", Translation(English, "t", "s", "multi\nline\tbody").Body);
        Assert.ThrowsExactly<ArgumentException>(() => new HelpCenterSearchQuery(new PortalId("p"), English, new string('q', 257)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HelpCenterSearchQuery(new PortalId("p"), English, "q", limit: 51));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HelpCenterSearchQuery(new PortalId("p"), English, "q", offset: 201));
        Assert.ThrowsExactly<ArgumentException>(() => new Article(new ArticleId("a"), new PortalId("p"), []));
        Assert.ThrowsExactly<ArgumentException>(() => new Article(new ArticleId("a"), new PortalId("p"), [Translation(English, "a", "a", "a"), Translation(English, "b", "b", "b")]));
        Assert.ThrowsExactly<ArgumentException>(() => new HelpCollection(new CollectionId("c"), new PortalId("p"), [new CollectionTranslation(English, "c", new HelpCenterSlug("c"))], new CollectionId("c")));
        Assert.ThrowsExactly<ArgumentException>(() => new ArticleRecord(SampleArticle("a"), ArticleState.Published, null, Author, Now, Author, Now));
        Assert.ThrowsExactly<ArgumentException>(() => new Portal(new PortalId("p"), new HelpCenterSlug("p"), "P", English, Enumerable.Range(0, 40).Select(index => new HelpCenterLocale($"x{(char)('a' + index % 26)}-{index:D2}"))));
    }

    [TestMethod]
    public void SearchConfigurationUsesAllowList()
    {
        Assert.AreEqual("english", HelpCenterSearchConfiguration.ForLocale(new HelpCenterLocale("en-GB")));
        Assert.AreEqual("portuguese", HelpCenterSearchConfiguration.ForLocale(new HelpCenterLocale("pt-BR")));
        Assert.AreEqual("norwegian", HelpCenterSearchConfiguration.ForLocale(new HelpCenterLocale("nb")));
        Assert.AreEqual("simple", HelpCenterSearchConfiguration.ForLocale(new HelpCenterLocale("ja")));
        Assert.IsTrue(HelpCenterSearchConfiguration.Names.All(name => name.All(char.IsAsciiLetterLower)));
        CollectionAssert.AreEqual(new[] { "reset", "your", "password", "cafe", "123" }, HelpCenterSearchText.Terms("Reset YOUR password! Café, 123 reset").ToArray());
        Assert.IsLessThanOrEqualTo(ArticleSnippet.MaximumSnippetLength, HelpCenterSearchText.Snippet(new string('x', 1000) + " needle " + new string('y', 1000), ["needle"]).Length);
        StringAssert.Contains(HelpCenterSearchText.Snippet(new string('x', 1000) + " needle " + new string('y', 1000), ["needle"]), "needle");
    }

    [TestMethod]
    public void VisitorHashesAreScopedAndOpaque()
    {
        var scope = TestScopes.Create();
        var hash = VisitorHash.Create(scope, "visitor-123");
        Assert.AreEqual(hash, VisitorHash.Create(scope, "visitor-123"));
        Assert.AreNotEqual(hash, VisitorHash.Create(TestScopes.LevelVariants(scope)[1], "visitor-123"));
        Assert.HasCount(64, hash.Value);
        Assert.DoesNotContain("visitor", hash.Value, StringComparison.Ordinal);
        Assert.AreEqual(hash, VisitorHash.FromStored(hash.Value));
        Assert.ThrowsExactly<ArgumentException>(() => VisitorHash.Create(scope, "short"));
        Assert.ThrowsExactly<FormatException>(() => VisitorHash.FromStored("XYZ"));
        Assert.AreEqual("VisitorHash { <redacted> }", hash.ToString());
    }

    [TestMethod]
    public void CodecRoundTripsAndRejectsUnknownShapes()
    {
        var article = SampleArticle("a1");
        Assert.IsTrue(article.Translations.SequenceEqual(HelpCenterJsonCodec.DecodeArticleTranslations(HelpCenterJsonCodec.EncodeArticleTranslations(article.Translations))));
        var revision = RevisionOf(article, 1);
        Assert.IsTrue(revision.Translations.SequenceEqual(HelpCenterJsonCodec.DecodePublishedTranslations(HelpCenterJsonCodec.EncodePublishedTranslations(revision.Translations))));
        var collection = SampleCollection("c");
        Assert.IsTrue(collection.Translations.SequenceEqual(HelpCenterJsonCodec.DecodeCollectionTranslations(HelpCenterJsonCodec.EncodeCollectionTranslations(collection.Translations))));
        Assert.ThrowsExactly<FormatException>(() => HelpCenterJsonCodec.DecodeCollectionTranslations("""{"v":2,"items":[]}"""));
        Assert.ThrowsExactly<FormatException>(() => HelpCenterJsonCodec.DecodeCollectionTranslations("""{"v":1,"items":[],"extra":1}"""));
        Assert.ThrowsExactly<FormatException>(() => HelpCenterJsonCodec.DecodeCollectionTranslations("""{"v":1,"items":[{"locale":"en","name":"n","slug":"s","x":1}]}"""));
        Assert.ThrowsExactly<ArgumentException>(() => HelpCenterJsonCodec.DecodeCollectionTranslations("""{"v":1,"items":[{"locale":"en","name":"n","slug":"Bad Slug"}]}"""));
    }

    [TestMethod]
    public void PermissionsAreCatalogued()
    {
        CollectionAssert.AreEqual(new[] { "helpcenter:manage", "helpcenter:publish", "helpcenter:read", "helpcenter:write" },
            HelpCenterPermissions.Definitions.Select(definition => definition.Permission.Value).ToArray());
        Assert.IsTrue(HelpCenterPermissions.Definitions.Single(definition => definition.Permission == HelpCenterPermissions.Manage).IsSensitive);
        Assert.IsTrue(BuiltInRoles.Owner.Includes(HelpCenterPermissions.Read));
    }
}
