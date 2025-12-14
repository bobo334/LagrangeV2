using System;
using Lagrange.Milky.Utility;
using Xunit;

namespace Lagrange.Milky.Test;

public class EntityConvertOneBotTests
{
    [Fact]
    public void Convert_MessageEvent_To_OneBotPost()
    {
        var convert = new EntityConvert();

        var offline = new Lagrange.Core.Events.EventArgs.BotOfflineEvent(Lagrange.Core.Events.EventArgs.BotOfflineEvent.Reasons.Disconnected, ("test","reason"));

        var post = convert.ToOneBotPost(offline);

        Assert.NotNull(post);
        Assert.Equal("meta_event", post.PostType);

        // Test reverse conversion
        var raw = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(post);
        var ev = convert.FromOneBotPost(post, raw);
        Assert.NotNull(ev);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotOfflineEvent>(ev);

        // message event
        var messagePost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, Message: System.Text.Json.JsonDocument.Parse("\"hello\"").RootElement, MessageType: "private", UserId: 1234);
        var rawMsg = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(messagePost);
        var ev2 = convert.FromOneBotPost(messagePost, rawMsg);
        Assert.NotNull(ev2);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(ev2);

        // message array with mention
        var arr = System.Text.Json.JsonDocument.Parse("[ { \"type\": \"text\", \"data\": { \"text\": \"hi \" } }, { \"type\": \"at\", \"data\": { \"qq\": 1234 } } ]");
        var messagePost2 = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, Message: arr.RootElement, MessageType: "group", GroupId: 9999, UserId: 1234);
        var ev3 = convert.FromOneBotPost(messagePost2, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(messagePost2));
        Assert.NotNull(ev3);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(ev3);

        // group increase meta event
        var meta = System.Text.Json.JsonDocument.Parse("{ \"type\": \"group_increase\", \"group_id\": 9999, \"user_id\": 1234, \"operator_id\": 4321 }");
        var metaPost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("meta_event", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, RawEvent: meta.RootElement);
        var ev4 = convert.FromOneBotPost(metaPost, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(metaPost));
        Assert.NotNull(ev4);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotGroupMemberIncreaseEvent>(ev4);

        // image segment
        var img = System.Text.Json.JsonDocument.Parse("[ { \"type\": \"image\", \"data\": { \"url\": \"https://example.com/pic.jpg\" } } ]");
        var imgPost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, Message: img.RootElement, MessageType: "group", GroupId: 1, UserId: 2);
        var eImg = convert.FromOneBotPost(imgPost, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(imgPost));
        Assert.NotNull(eImg);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(eImg);
        var msgImg = ((Lagrange.Core.Events.EventArgs.BotMessageEvent)eImg).Message;
        Assert.Contains(msgImg.Entities, x => x is Lagrange.Core.Message.Entities.TextEntity te && te.Text.Contains("[图片]"));

        // file segment
        var file = System.Text.Json.JsonDocument.Parse("[ { \"type\": \"file\", \"data\": { \"name\": \"doc.txt\" } } ]");
        var filePost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, Message: file.RootElement, MessageType: "private", UserId: 3);
        var eFile = convert.FromOneBotPost(filePost, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(filePost));
        Assert.NotNull(eFile);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(eFile);
        var msgFile = ((Lagrange.Core.Events.EventArgs.BotMessageEvent)eFile).Message;
        Assert.Contains(msgFile.Entities, x => x is Lagrange.Core.Message.Entities.TextEntity te && te.Text.Contains("[文件]"));

        // reply segment
        var reply = System.Text.Json.JsonDocument.Parse("[ { \"type\": \"reply\", \"data\": { \"message_id\": 999 } } ]");
        var replyPost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, Message: reply.RootElement, MessageType: "group", GroupId: 5, UserId: 6);
        var eReply = convert.FromOneBotPost(replyPost, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(replyPost));
        Assert.NotNull(eReply);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(eReply);
        var msgReply = ((Lagrange.Core.Events.EventArgs.BotMessageEvent)eReply).Message;
        Assert.Contains(msgReply.Entities, x => x is Lagrange.Core.Message.Entities.ReplyEntity);

        // forward segment
        var forward = System.Text.Json.JsonDocument.Parse("[ { \"type\": \"forward\", \"data\": { \"id\": \"res123\" } } ]");
        var forwardPost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, Message: forward.RootElement, MessageType: "group", GroupId: 5, UserId: 6);
        var eForward = convert.FromOneBotPost(forwardPost, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(forwardPost));
        Assert.NotNull(eForward);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(eForward);
        var msgForward = ((Lagrange.Core.Events.EventArgs.BotMessageEvent)eForward).Message;
        Assert.Contains(msgForward.Entities, x => x is Lagrange.Core.Message.Entities.TextEntity te && te.Text.Contains("[转发]"));

        // video segment
        var video = System.Text.Json.JsonDocument.Parse("[ { \"type\": \"video\", \"data\": { \"summary\": \"sample video\" } } ]");
        var videoPost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, Message: video.RootElement, MessageType: "group", GroupId: 10, UserId: 11);
        var evVideo = convert.FromOneBotPost(videoPost, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(videoPost));
        Assert.NotNull(evVideo);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(evVideo);
        var msgVideo = ((Lagrange.Core.Events.EventArgs.BotMessageEvent)evVideo).Message;
        Assert.Contains(msgVideo.Entities, x => x is Lagrange.Core.Message.Entities.TextEntity te && te.Text.Contains("[视频]"));

        // record segment
        var record = System.Text.Json.JsonDocument.Parse("[ { \"type\": \"record\", \"data\": { \"summary\": \"sample record\" } } ]");
        var recordPost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, Message: record.RootElement, MessageType: "private", UserId: 12);
        var evRecord = convert.FromOneBotPost(recordPost, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(recordPost));
        Assert.NotNull(evRecord);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(evRecord);
        var msgRecord = ((Lagrange.Core.Events.EventArgs.BotMessageEvent)evRecord).Message;
        Assert.Contains(msgRecord.Entities, x => x is Lagrange.Core.Message.Entities.TextEntity te && te.Text.Contains("[语音]"));

        // mention_all
        var ma = System.Text.Json.JsonDocument.Parse("[ { \"type\": \"mention_all\" } ]");
        var maPost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, Message: ma.RootElement, MessageType: "group", GroupId: 99, UserId: 100);
        var evMA = convert.FromOneBotPost(maPost, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(maPost));
        Assert.NotNull(evMA);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(evMA);
        var msgMA = ((Lagrange.Core.Events.EventArgs.BotMessageEvent)evMA).Message;
        Assert.Contains(msgMA.Entities, x => x is Lagrange.Core.Message.Entities.MentionEntity me && me.Uin == 0);
    }
}
