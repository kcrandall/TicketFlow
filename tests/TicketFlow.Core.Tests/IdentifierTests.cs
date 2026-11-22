namespace TicketFlow.Core.Tests;

[TestClass]
public sealed class IdentifierTests
{
    private static readonly Func<string, object>[] Factories =
    [
        value => new WorkspaceId(value), value => new InboxId(value), value => new TeamId(value),
        value => new ContactId(value), value => new ConversationId(value), value => new MessageId(value),
        value => new LabelId(value), value => new UserId(value), value => new ApiClientId(value),
    ];

    [TestMethod]
    public void IdentifierKindsAreDistinctTypesThatNeverCompareEqual()
    {
        var ids = Factories.Select(create => create("same")).ToArray();
        Assert.AreEqual(ids.Length, ids.Select(id => id.GetType()).Distinct().Count());
        for (var i = 0; i < ids.Length; i++)
            for (var j = 0; j < ids.Length; j++)
                Assert.AreEqual(i == j, ids[i].Equals(ids[j]), $"{ids[i].GetType().Name} vs {ids[j].GetType().Name}");

        // No implicit conversions exist between kinds or from string.
        foreach (var type in ids.Select(id => id.GetType()))
            Assert.IsFalse(type.GetMethods().Any(method => method.Name is "op_Implicit" or "op_Explicit"), type.Name);
    }

    [TestMethod]
    public void SameKindComparesByExactOrdinalValue()
    {
        Assert.AreEqual(new ConversationId("c-1"), new ConversationId("c-1"));
        Assert.AreEqual(new ConversationId("c-1").GetHashCode(), new ConversationId("c-1").GetHashCode());
        Assert.AreNotEqual(new ConversationId("C-1"), new ConversationId("c-1"));
        Assert.AreEqual("c-1", new ConversationId("c-1").Value);
        Assert.AreEqual("c-1", new ConversationId("c-1").ToString());
    }

    [TestMethod]
    public void EveryKindValidatesWithStableParameterName()
    {
        foreach (var create in Factories)
        {
            Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentNullException>(() => create(null!)).ParamName);
            foreach (var invalid in new[] { "", " ", "a\nb", "\uDC00", new string('x', 257) })
                Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentException>(() => create(invalid)).ParamName);
            Assert.IsNotNull(create(new string('x', 256)));
        }
    }

    [TestMethod]
    public void DefaultIdentifierIsUninitialized()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => default(InboxId).Value);
        Assert.AreEqual(string.Empty, default(InboxId).ToString());
        Assert.AreNotEqual(default, new InboxId("i"));
    }
}
