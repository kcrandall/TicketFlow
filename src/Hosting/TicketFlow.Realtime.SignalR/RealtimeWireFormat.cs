using System.Collections.Frozen;
using TicketFlow.Conversations;

namespace TicketFlow.Realtime.SignalR;

/// <summary>
/// The versioned client payload of one realtime event (<c>"v":1</c>), sent with the hub method <see cref="RealtimeWireFormat.EventMethod"/>.
/// <see cref="Topic"/> is <c>conversation:{id}</c>, <c>queue:{inbox}</c>, <c>agent:{user}</c> or <c>visitor</c> (session ids are never sent).
/// </summary>
public sealed record RealtimeWireEvent(int V, string Type, string Topic, IReadOnlyDictionary<string, object?> Data);

/// <summary>Maps realtime events to JSON-friendly client payloads. Visitor payloads never contain agent identities.</summary>
public static class RealtimeWireFormat
{
    public const string EventMethod = "tf.event";

    public static RealtimeWireEvent ToWire(RealtimeTopic topic, RealtimeEvent realtimeEvent)
    {
        var topicName = topic switch
        {
            ConversationTopic conversation => $"conversation:{conversation.Conversation.Value}",
            InboxQueueTopic queue => $"queue:{queue.Inbox.Value}",
            AgentTopic agent => $"agent:{agent.Agent.Value}",
            VisitorSessionTopic => "visitor",
        };
        var (type, data) = realtimeEvent switch
        {
            ConversationChangedEvent changed => ("conversation.changed", BuildConversationChanged(changed)),
            PresenceChangedEvent presence => ("presence.changed", new Dictionary<string, object?>(4, StringComparer.Ordinal)
            {
                ["conversation"] = presence.Conversation.Value,
                ["user"] = presence.User.Value,
                ["activity"] = presence.Activity is { } activity ? Token(activity) : null,
                ["at"] = presence.At,
            }),
            ReplyLockChangedEvent replyLock => ("replylock.changed", new Dictionary<string, object?>(4, StringComparer.Ordinal)
            {
                ["conversation"] = replyLock.Conversation.Value,
                ["holder"] = replyLock.Holder?.Value,
                ["expiresAt"] = replyLock.ExpiresAt,
                ["at"] = replyLock.At,
            }),
            AgentStatusChangedEvent status => ("agent.status", new Dictionary<string, object?>(3, StringComparer.Ordinal)
            {
                ["agent"] = status.Agent.Value,
                ["status"] = Token(status.Status),
                ["at"] = status.At,
            }),
            ConversationAssignedEvent assigned => ("conversation.assigned", new Dictionary<string, object?>(4, StringComparer.Ordinal)
            {
                ["conversation"] = assigned.Conversation.Value,
                ["agent"] = assigned.Agent.Value,
                ["revision"] = assigned.Revision,
                ["at"] = assigned.At,
            }),
            QueueChangedEvent queue => ("queue.changed", new Dictionary<string, object?>(4, StringComparer.Ordinal)
            {
                ["conversation"] = queue.Conversation.Value,
                ["inbox"] = queue.Inbox.Value,
                ["change"] = Token(queue.Change),
                ["at"] = queue.At,
            }),
            TypingEvent typing => ("typing", Actor(typing.Actor, new Dictionary<string, object?>(5, StringComparer.Ordinal)
            {
                ["conversation"] = typing.Conversation.Value,
                ["typing"] = typing.Typing,
                ["at"] = typing.At,
            })),
            ReadReceiptEvent read => ("read", Actor(read.Actor, new Dictionary<string, object?>(5, StringComparer.Ordinal)
            {
                ["conversation"] = read.Conversation.Value,
                ["upToSequence"] = read.UpToSequence,
                ["at"] = read.At,
            })),
            VisitorMessageEvent message => ("message", BuildVisitorMessage(message)),
            VisitorSessionEndedEvent ended => ("session.ended", new Dictionary<string, object?>(2, StringComparer.Ordinal)
            {
                ["reason"] = Token(ended.Reason),
                ["at"] = ended.At,
            }),
        };
        return new RealtimeWireEvent(1, type, topicName, data);
    }

    private static Dictionary<string, object?> BuildConversationChanged(ConversationChangedEvent changed)
    {
        var events = new Dictionary<string, object?>[changed.Events.Count];
        for (var i = 0; i < changed.Events.Count; i++)
        {
            var summary = changed.Events[i];
            events[i] = new Dictionary<string, object?>(4, StringComparer.Ordinal)
            {
                ["sequence"] = summary.Sequence,
                ["kind"] = Token(summary.Kind),
                ["message"] = summary.Message?.Value,
                ["direction"] = summary.Direction is { } direction ? Token(direction) : null,
            };
        }
        return new Dictionary<string, object?>(8, StringComparer.Ordinal)
        {
            ["conversation"] = changed.Conversation.Value,
            ["inbox"] = changed.Inbox.Value,
            ["revision"] = changed.Revision,
            ["kind"] = Token(changed.Kind),
            ["events"] = events,
            ["message"] = changed.Message?.Value,
            ["delivery"] = changed.Delivery is { } delivery ? Token(delivery) : null,
            ["occurredAt"] = changed.OccurredAt,
        };
    }

    private static Dictionary<string, object?> BuildVisitorMessage(VisitorMessageEvent message)
    {
        var attachments = message.Attachments.Count == 0
            ? Array.Empty<IReadOnlyDictionary<string, object?>>()
            : BuildAttachments(message.Attachments);
        return new Dictionary<string, object?>(8, StringComparer.Ordinal)
        {
            ["conversation"] = message.Conversation.Value,
            ["message"] = message.Message.Value,
            ["sequence"] = message.Sequence,
            ["direction"] = Token(message.Direction),
            ["body"] = message.Body,
            ["contentType"] = Token(message.ContentType),
            ["attachments"] = attachments,
            ["at"] = message.At,
        };
    }

    internal static IReadOnlyDictionary<string, object?>[] BuildAttachments(IReadOnlyList<AttachmentRef> attachments)
    {
        if (attachments.Count == 0)
            return [];
        var result = new IReadOnlyDictionary<string, object?>[attachments.Count];
        for (var i = 0; i < attachments.Count; i++)
            result[i] = Attachment(attachments[i]);
        return result;
    }

    /// <summary>The client shape of an attachment reference.</summary>
    public static IReadOnlyDictionary<string, object?> Attachment(AttachmentRef attachment) => new Dictionary<string, object?>(4, StringComparer.Ordinal)
    {
        ["storageRef"] = attachment.StorageRef,
        ["fileName"] = attachment.FileName,
        ["mediaType"] = attachment.MediaType,
        ["sizeBytes"] = attachment.SizeBytes,
    };

    private static Dictionary<string, object?> Actor(RealtimeActor actor, Dictionary<string, object?> data)
    {
        switch (actor)
        {
            case AgentActor agent:
                data["actor"] = "agent";
                data["agent"] = agent.Agent.Value;
                break;
            case VisitorActor:
                data["actor"] = "visitor";
                break;
            case SupportActor:
                data["actor"] = "support";
                break;
        }
        return data;
    }

    /// <summary>Cached kebab-case token for an enum value (<c>EventsAppended</c> → <c>events-appended</c>).</summary>
    internal static string Token<TEnum>(TEnum value) where TEnum : struct, Enum => KebabTokenCache<TEnum>.Get(value);

    /// <summary>PascalCase enum names as lowercase kebab tokens (<c>EventsAppended</c> → <c>events-appended</c>).</summary>
    internal static string Token(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index > 0)
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static class KebabTokenCache<TEnum> where TEnum : struct, Enum
    {
        private static readonly FrozenDictionary<TEnum, string> Tokens = Build();

        private static FrozenDictionary<TEnum, string> Build()
        {
            var values = Enum.GetValues<TEnum>();
            var map = new Dictionary<TEnum, string>(values.Length);
            for (var i = 0; i < values.Length; i++)
                map[values[i]] = Token(values[i].ToString());
            return map.ToFrozenDictionary();
        }

        public static string Get(TEnum value) => Tokens.TryGetValue(value, out var token) ? token : Token(value.ToString());
    }
}
