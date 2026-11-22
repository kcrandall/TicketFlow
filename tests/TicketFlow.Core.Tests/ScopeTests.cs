using TicketFlow.Testing;

namespace TicketFlow.Core.Tests;

[TestClass]
public sealed class ScopeTests
{
    [TestMethod]
    public void EveryScopeLevelParticipatesInEquality()
    {
        var scope = TestScopes.Create();
        Assert.AreEqual(scope, TestScopes.Create());
        Assert.AreEqual(scope.GetHashCode(), TestScopes.Create().GetHashCode());

        var variants = TestScopes.LevelVariants(scope);
        Assert.HasCount(6, variants);
        for (var i = 0; i < variants.Count; i++)
            for (var j = 0; j < variants.Count; j++)
                Assert.AreEqual(i == j, variants[i] == variants[j], $"Variants {i} and {j}");
    }

    [TestMethod]
    public void ScopeExposesExactValuesAndRedactsToString()
    {
        var scope = new TicketScope("t", "p", "e", "a", new WorkspaceId("w"));
        Assert.AreEqual("t", scope.TenantId);
        Assert.AreEqual("p", scope.ProjectId);
        Assert.AreEqual("e", scope.EnvironmentId);
        Assert.AreEqual("a", scope.AppId);
        Assert.AreEqual(new WorkspaceId("w"), scope.WorkspaceId);
        Assert.AreEqual("TicketScope { <redacted> }", scope.ToString());
    }

    [TestMethod]
    [DataRow(0, "tenantId")]
    [DataRow(1, "projectId")]
    [DataRow(2, "environmentId")]
    [DataRow(3, "appId")]
    public void InvalidScopeLevelsReportTheirParameterName(int level, string parameterName)
    {
        foreach (var invalid in new[] { "", " ", "\t", "a\u0000", "a\u007F", "a\u0085", "\uD800", new string('x', 257) })
        {
            var parts = new[] { "t", "p", "e", "a" };
            parts[level] = invalid;
            var error = Assert.Throws<ArgumentException>(() => new TicketScope(parts[0], parts[1], parts[2], parts[3], new WorkspaceId("w")));
            Assert.AreEqual(parameterName, error.ParamName);
        }

        var nulls = new[] { "t", "p", "e", "a" };
        nulls[level] = null!;
        var missing = Assert.ThrowsExactly<ArgumentNullException>(() => new TicketScope(nulls[0], nulls[1], nulls[2], nulls[3], new WorkspaceId("w")));
        Assert.AreEqual(parameterName, missing.ParamName);
    }

    [TestMethod]
    public void UninitializedWorkspaceIsRejected()
    {
        var error = Assert.ThrowsExactly<ArgumentException>(() => new TicketScope("t", "p", "e", "a", default));
        Assert.AreEqual("workspaceId", error.ParamName);
    }

    [TestMethod]
    public void IdentifierLengthBoundaryIsInclusiveAt256CodeUnits()
    {
        var maximum = new string('x', 256);
        Assert.AreSame(maximum, TicketScope.RequireIdentifier(maximum, "identifier"));
        var longScope = TestScopes.LongIdentifier(7);
        Assert.AreEqual(256, longScope.Length);
        Assert.AreSame(longScope, TicketScope.RequireIdentifier(longScope, "identifier"));

        var oversized = Assert.ThrowsExactly<ArgumentException>(() => TicketScope.RequireIdentifier(maximum + "x", "identifier"));
        Assert.AreEqual("identifier", oversized.ParamName);

        // A surrogate pair counts as two code units: 128 pairs fit, one more code unit does not.
        var pairs = string.Concat(Enumerable.Repeat("😀", 128));
        Assert.AreSame(pairs, TicketScope.RequireIdentifier(pairs, "identifier"));
        Assert.ThrowsExactly<ArgumentException>(() => TicketScope.RequireIdentifier(pairs + "x", "identifier"));
    }

    [TestMethod]
    public void NullAndBlankIdentifiersKeepTheirParameterName()
    {
        Assert.AreEqual("identifier", Assert.ThrowsExactly<ArgumentNullException>(() => TicketScope.RequireIdentifier(null!, "identifier")).ParamName);
        foreach (var blank in new[] { "", " ", "   ", "　", " " })
            Assert.AreEqual("identifier", Assert.ThrowsExactly<ArgumentException>(() => TicketScope.RequireIdentifier(blank, "identifier")).ParamName);
    }

    [TestMethod]
    public void IdentifierValidationRejectsControlsAndIsolatedSurrogates()
    {
        // A prefix keeps whitespace characters from triggering the independent blank check.
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var value = "id" + (char)code;
            if (char.IsControl((char)code) || char.IsSurrogate((char)code))
                Assert.AreEqual("identifier", Assert.ThrowsExactly<ArgumentException>(() => TicketScope.RequireIdentifier(value, "identifier")).ParamName);
            else
                Assert.AreSame(value, TicketScope.RequireIdentifier(value, "identifier"));
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void MalformedUnicodeIsRejectedAtEveryLevel(int kind)
    {
        // Attribute metadata cannot carry lone surrogates (they become U+FFFD), so the values are built here.
        var value = kind switch
        {
            0 => "\uD800",
            1 => "\uDC00",
            2 => "prefix\uD800",
            3 => "prefix\uDC00suffix",
            4 => "\uDC00\uD800",
            _ => "\uD83D\uDE00\uD800",
        };
        Assert.ThrowsExactly<ArgumentException>(() => TestScopes.Create(tenantId: value));
        Assert.ThrowsExactly<ArgumentException>(() => TestScopes.Create(projectId: value));
        Assert.ThrowsExactly<ArgumentException>(() => TestScopes.Create(environmentId: value));
        Assert.ThrowsExactly<ArgumentException>(() => TestScopes.Create(appId: value));
        Assert.ThrowsExactly<ArgumentException>(() => TestScopes.Create(workspaceId: value));
    }

    [TestMethod]
    public void ValidUnicodeIdentifiersRemainExactAndUnnormalized()
    {
        foreach (var value in new[] { "�", "𐀀", "􏿿", "é", "é" })
            Assert.AreSame(value, TestScopes.Create(tenantId: value).TenantId);
        Assert.AreNotEqual(TestScopes.Create(tenantId: "é"), TestScopes.Create(tenantId: "é"));
        Assert.AreNotEqual(TestScopes.Create(workspaceId: "ABC"), TestScopes.Create(workspaceId: "abc"));
    }
}
