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
            ConversationChangedEvent changed => ("conversation.changed", new Dictionary<string, object?>
            {
                ["conversation"] = changed.Conversation.Value,
                ["inbox"] = changed.Inbox.Value,
                ["revision"] = changed.Revision,
                ["kind"] = Token(changed.Kind.ToString()),
                ["events"] = changed.Events.Select(summary => new Dictionary<string, object?>
                {
                    ["sequence"] = summary.Sequence,
                    ["kind"] = Token(summary.Kind.ToString()),
                    ["message"] = summary.Message?.Value,
                    ["direction"] = summary.Direction is { } direction ? Token(direction.ToString()) : null,
                }).ToList(),
                ["message"] = changed.Message?.Value,
                ["delivery"] = changed.Delivery is { } delivery ? Token(delivery.ToString()) : null,
                ["occurredAt"] = changed.OccurredAt,
            }),
            PresenceChangedEvent presence => ("presence.changed", new Dictionary<string, object?>
            {
                ["conversation"] = presence.Conversation.Value,
                ["user"] = presence.User.Value,
                ["activity"] = presence.Activity is { } activity ? Token(activity.ToString()) : null,
                ["at"] = presence.At,
            }),
            ReplyLockChangedEvent replyLock => ("replylock.changed", new Dictionary<string, object?>
            {
                ["conversation"] = replyLock.Conversation.Value,
                ["holder"] = replyLock.Holder?.Value,
                ["expiresAt"] = replyLock.ExpiresAt,
                ["at"] = replyLock.At,
            }),
            AgentStatusChangedEvent status => ("agent.status", new Dictionary<string, object?>
            {
                ["agent"] = status.Agent.Value,
                ["status"] = Token(status.Status.ToString()),
                ["at"] = status.At,
            }),
            ConversationAssignedEvent assigned => ("conversation.assigned", new Dictionary<string, object?>
            {
                ["conversation"] = assigned.Conversation.Value,
                ["agent"] = assigned.Agent.Value,
                ["revision"] = assigned.Revision,
                ["at"] = assigned.At,
            }),
            QueueChangedEvent queue => ("queue.changed", new Dictionary<string, object?>
            {
                ["conversation"] = queue.Conversation.Value,
                ["inbox"] = queue.Inbox.Value,
                ["change"] = Token(queue.Change.ToString()),
                ["at"] = queue.At,
            }),
            TypingEvent typing => ("typing", Actor(typing.Actor, new Dictionary<string, object?>
            {
                ["conversation"] = typing.Conversation.Value,
                ["typing"] = typing.Typing,
                ["at"] = typing.At,
            })),
            ReadReceiptEvent read => ("read", Actor(read.Actor, new Dictionary<string, object?>
            {
                ["conversation"] = read.Conversation.Value,
                ["upToSequence"] = read.UpToSequence,
                ["at"] = read.At,
            })),
            VisitorMessageEvent message => ("message", new Dictionary<string, object?>
            {
                ["conversation"] = message.Conversation.Value,
                ["message"] = message.Message.Value,
                ["sequence"] = message.Sequence,
                ["direction"] = Token(message.Direction.ToString()),
                ["body"] = message.Body,
                ["contentType"] = Token(message.ContentType.ToString()),
                ["attachments"] = message.Attachments.Select(Attachment).ToList(),
                ["at"] = message.At,
            }),
            VisitorSessionEndedEvent ended => ("session.ended", new Dictionary<string, object?>
            {
                ["reason"] = Token(ended.Reason.ToString()),
                ["at"] = ended.At,
            }),
        };
        return new RealtimeWireEvent(1, type, topicName, data);
    }

    /// <summary>The client shape of an attachment reference.</summary>
    public static IReadOnlyDictionary<string, object?> Attachment(AttachmentRef attachment) => new Dictionary<string, object?>
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
}
