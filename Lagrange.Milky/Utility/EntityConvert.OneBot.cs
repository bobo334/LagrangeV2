using Lagrange.Core.Events.EventArgs;
using Lagrange.Milky.Entity.OneBot;
using System.Text.Json;

namespace Lagrange.Milky.Utility;

public partial class EntityConvert
{
    public OneBotPostEvent ToOneBotPost(object @event)
    {
        // Minimal conversions for message and bot offline events
        switch (@event)
        {
            case BotMessageEvent msgEvent:
                var msg = MessageReceiveEvent(msgEvent);
                return new OneBotPostEvent(
                    PostType: "message",
                    Time: new DateTimeOffset(msgEvent.Timestamp).ToUnixTimeSeconds(),
                    SelfId: msgEvent.Sender.BotUin,
                    MessageType: msg.Type == Lagrange.Core.Message.MessageType.Group ? "group" : "private",
                    SubType: msg.Type == Lagrange.Core.Message.MessageType.Group ? "normal" : "friend",
                    MessageId: (long?)msgEvent.Message.Sequence,
                    UserId: msg.Sender?.Uin,
                    GroupId: msg.Group?.Uin,
                    Message: JsonUtility.SerializeToUtf8String(msgEvent.Message.Entities)
                );
            case Lagrange.Core.Events.EventArgs.BotOfflineEvent offline:
                return new OneBotPostEvent(
                    PostType: "meta_event",
                    Time: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    SelfId: 0,
                    RawEvent: new { type = "offline", reason = offline.Reason.ToString() }
                );
            default:
                return new OneBotPostEvent(PostType: "unknown", Time: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), SelfId: 0);
        }
    }

    public Lagrange.Core.Events.EventArgs.EventBase? FromOneBotPost(OneBotPostEvent post, ReadOnlyMemory<byte> raw)
    {
        switch (post.PostType)
        {
            case "message":
                {
                    var time = DateTimeOffset.FromUnixTimeSeconds(post.Time).UtcDateTime;

                    var builder = new Lagrange.Core.Message.MessageBuilder();

                    if (post.Message.HasValue)
                    {
                        var msgElem = post.Message.Value;
                        if (msgElem.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            builder.Text(msgElem.GetString() ?? string.Empty);
                        }
                        else if (msgElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var seg in msgElem.EnumerateArray())
                            {
                                if (seg.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    builder.Text(seg.GetString() ?? string.Empty);
                                }
                                else if (seg.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (seg.TryGetProperty("type", out var typeProp))
                                    {
                                        var type = typeProp.GetString();
                                        if (type == "text")
                                        {
                                            if (seg.TryGetProperty("data", out var data) && data.TryGetProperty("text", out var text)) builder.Text(text.GetString() ?? string.Empty);
                                        }
                                        else if (type == "at")
                                        {
                                            if (seg.TryGetProperty("data", out var data) && data.TryGetProperty("qq", out var qq))
                                            {
                                                var uin = qq.GetInt64();
                                                builder.Mention(uin, null);
                                            }
                                        }
                                        else
                                        {
                                            // fallback: stringify
                                            builder.Text(seg.ToString());
                                        }
                                    }
                                    else
                                    {
                                        builder.Text(seg.ToString());
                                    }
                                }
                                else
                                {
                                    builder.Text(seg.ToString());
                                }
                            }
                        }
                        else
                        {
                            builder.Text(msgElem.ToString());
                        }
                    }
                    else
                    {
                        builder.Text(string.Empty);
                    }

                    var chain = builder.Build();

                    if (post.MessageType == "group")
                    {
                        var msg = Lagrange.Core.Message.BotMessage.CreateCustomGroup(post.GroupId ?? 0, post.UserId ?? 0, string.Empty, time, chain);
                        return new Lagrange.Core.Events.EventArgs.BotMessageEvent(msg, raw);
                    }
                    else
                    {
                        var msg = Lagrange.Core.Message.BotMessage.CreateCustomFriend(post.UserId ?? 0, string.Empty, post.SelfId, string.Empty, time, chain);
                        return new Lagrange.Core.Events.EventArgs.BotMessageEvent(msg, raw);
                    }
                }
            case "meta_event":
                {
                    if (post.RawEvent is JsonElement je && je.ValueKind == JsonValueKind.Object)
                    {
                        if (je.TryGetProperty("type", out var t))
                        {
                            var type = t.GetString();
                            if (type == "offline")
                            {
                                var reason = Lagrange.Core.Events.EventArgs.BotOfflineEvent.Reasons.Disconnected;
                                return new Lagrange.Core.Events.EventArgs.BotOfflineEvent(reason, (null, null));
                            }
                            else if (type == "group_increase" || type == "group_decrease")
                            {
                                // Try extract common fields
                                long groupId = 0; long userId = 0; long? operatorId = null;
                                if (je.TryGetProperty("group_id", out var g)) groupId = g.GetInt64();
                                if (je.TryGetProperty("user_id", out var u)) userId = u.GetInt64();
                                if (je.TryGetProperty("operator_id", out var o)) operatorId = o.GetInt64();

                                if (type == "group_increase")
                                {
                                    return new Lagrange.Core.Events.EventArgs.BotGroupMemberIncreaseEvent(groupId, userId, operatorId ?? 0, 0u, operatorId);
                                }
                                else
                                {
                                    return new Lagrange.Core.Events.EventArgs.BotGroupMemberDecreaseEvent(groupId, userId, operatorId);
                                }
                            }
                        }
                    }

                    return null;
                }
            default:
                return null;
        }
    }
}
