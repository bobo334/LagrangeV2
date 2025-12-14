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
        var messagePost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, message: System.Text.Json.JsonDocument.Parse("\"hello\"").RootElement, messageType: "private", userId: 1234);
        var rawMsg = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(messagePost);
        var ev2 = convert.FromOneBotPost(messagePost, rawMsg);
        Assert.NotNull(ev2);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(ev2);

        // message array with mention
        var arr = System.Text.Json.JsonDocument.Parse("[ { \"type\": \"text\", \"data\": { \"text\": \"hi \" } }, { \"type\": \"at\", \"data\": { \"qq\": 1234 } } ]");
        var messagePost2 = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("message", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, message: arr.RootElement, messageType: "group", groupId: 9999, userId: 1234);
        var ev3 = convert.FromOneBotPost(messagePost2, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(messagePost2));
        Assert.NotNull(ev3);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotMessageEvent>(ev3);

        // group increase meta event
        var meta = System.Text.Json.JsonDocument.Parse("{ \"type\": \"group_increase\", \"group_id\": 9999, \"user_id\": 1234, \"operator_id\": 4321 }");
        var metaPost = new Lagrange.Milky.Entity.OneBot.OneBotPostEvent("meta_event", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1000, rawEvent: meta.RootElement);
        var ev4 = convert.FromOneBotPost(metaPost, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(metaPost));
        Assert.NotNull(ev4);
        Assert.IsType<Lagrange.Core.Events.EventArgs.BotGroupMemberIncreaseEvent>(ev4);
    }
}
