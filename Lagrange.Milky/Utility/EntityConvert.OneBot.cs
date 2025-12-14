
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
                // serialize message chain to OneBot-style message array when possible
                JsonElement? messageElement = null;
                try
                {
                    var segs = new List<object>();
                    foreach (var ent in msgEvent.Message.Entities)
                    {
                        switch (ent)
                        {
                            case Lagrange.Core.Message.Entities.TextEntity t:
                                segs.Add(new { type = "text", data = new { text = t.Text } });
                                break;
                            case Lagrange.Core.Message.Entities.MentionEntity m:
                                segs.Add(new { type = "at", data = new { qq = (long)m.Uin } });
                                break;
                            case Lagrange.Core.Message.Entities.ReplyEntity r:
                                segs.Add(new { type = "reply", data = new { message_id = (long)r.SrcUid, seq = (long)r.SrcSequence } });
                                break;
                            case Lagrange.Core.Message.Entities.MultiMsgEntity mm when mm.ResId != null:
                                segs.Add(new { type = "forward", data = new { id = mm.ResId } });
                                break;
                            case Lagrange.Core.Message.Entities.ImageEntity img when !string.IsNullOrEmpty(img.FileUrl):
                                segs.Add(new { type = "image", data = new { url = img.FileUrl, summary = img.Summary } });
                                break;
                            case Lagrange.Core.Message.Entities.VideoEntity video:
                                segs.Add(new { type = "video", data = new { summary = video.ToPreviewString() } });
                                break;
                            case Lagrange.Core.Message.Entities.RecordEntity record:
                                segs.Add(new { type = "record", data = new { summary = record.ToPreviewString() } });
                                break;
                                break;
                            default:
                                segs.Add(new { type = "text", data = new { text = ent.ToString() } });
                                break;
                        }
                    }

                    var bytes = JsonUtility.SerializeToUtf8Bytes(segs.GetType(), segs);
                    using var doc = JsonDocument.Parse(bytes);
                    messageElement = doc.RootElement.Clone();
                }
                catch { messageElement = null; }

                long selfId = _bot.BotUin;
                return new OneBotPostEvent(
                    PostType: "message",
                    Time: new DateTimeOffset(msgEvent.Message.Time).ToUnixTimeSeconds(),
                    SelfId: selfId,
                    MessageType: msgEvent.Message.Type == Lagrange.Core.Message.MessageType.Group ? "group" : "private",
                    SubType: msgEvent.Message.Type == Lagrange.Core.Message.MessageType.Group ? "normal" : "friend",
                    MessageId: (long?)msgEvent.Message.Sequence,
                    UserId: msgEvent.Message.Contact?.Uin,
                    GroupId: msgEvent.Message.Receiver?.Uin,
                    Message: messageElement
                );
            case Lagrange.Core.Events.EventArgs.BotOfflineEvent offline:
                JsonElement? offlineElem = null;
                try
                {
                    var bytes = JsonUtility.SerializeToUtf8Bytes(typeof(object), new { type = "offline", reason = offline.Reason.ToString() });
                    using var doc = JsonDocument.Parse(bytes);
                    offlineElem = doc.RootElement.Clone();
                }
                catch { offlineElem = null; }

                return new OneBotPostEvent(
                    PostType: "meta_event",
                    Time: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    SelfId: 0,
                    RawEvent: offlineElem
                );
            default:
                return new OneBotPostEvent(PostType: "unknown", Time: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), SelfId: 0);
        }
    }

    public Lagrange.Core.Events.EventBase? FromOneBotPost(OneBotPostEvent post, ReadOnlyMemory<byte> raw)
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
                                        else if (type == "image")
                                        {
                                            // try common fields for image url
                                            if (seg.TryGetProperty("data", out var data))
                                            {
                                                string? url = null;
                                                if (data.TryGetProperty("url", out var u)) url = u.GetString();
                                                if (data.TryGetProperty("temp_url", out var tu)) url = tu.GetString() ?? url;
                                                if (data.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("url", out var fu)) url = fu.GetString() ?? url;

                                                builder.Text(url != null ? $"[图片] {url}" : "[图片]");
                                            }
                                            else builder.Text("[图片]");
                                        }
                                        else if (type == "file")
                                        {
                                            if (seg.TryGetProperty("data", out var data))
                                            {
                                                string? name = null; string? url = null;
                                                if (data.TryGetProperty("name", out var n)) name = n.GetString();
                                                if (data.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.Object)
                                                {
                                                    if (f.TryGetProperty("name", out var fn)) name = fn.GetString() ?? name;
                                                    if (f.TryGetProperty("url", out var fu)) url = fu.GetString() ?? url;
                                                }
                                                builder.Text(name != null ? $"[文件] {name}" : (url != null ? $"[文件] {url}" : "[文件]"));
                                            }
                                            else builder.Text("[文件]");
                                        }
                                        else if (type == "reply")
                                        {
                                            if (seg.TryGetProperty("data", out var data))
                                            {
                                                long seq = 0;
                                                if (data.TryGetProperty("message_id", out var mid)) seq = mid.GetInt64();
                                                else if (data.TryGetProperty("id", out var id)) seq = id.GetInt64();
                                                else if (data.TryGetProperty("seq", out var s)) seq = s.GetInt64();

                                                // create a placeholder source message
                                                if (post.MessageType == "group")
                                                {
                                                    var src = Lagrange.Core.Message.BotMessage.CreateCustomGroup(post.GroupId ?? 0, post.UserId ?? 0, string.Empty, DateTime.Now, new Lagrange.Core.Message.MessageChain());
                                                    src.Sequence = (ulong)seq;
                                                    builder.Reply(src);
                                                }
                                                else
                                                {
                                                    var src = Lagrange.Core.Message.BotMessage.CreateCustomFriend(post.UserId ?? 0, string.Empty, post.SelfId, string.Empty, DateTime.Now, new Lagrange.Core.Message.MessageChain());
                                                    src.Sequence = (ulong)seq;
                                                    builder.Reply(src);
                                                }
                                            }
                                            else builder.Text("[回复]");
                                        }
                                        else if (type == "forward")
                                        {
                                            if (seg.TryGetProperty("data", out var data))
                                            {
                                                if (data.TryGetProperty("id", out var id)) builder.Text($"[转发] {id.GetString()}");
                                                else builder.Text("[转发]");
                                            }
                                            else builder.Text("[转发]");
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
