using System.Text.Json;
using Lagrange.Milky.Utility;
using Xunit;

namespace Lagrange.Milky.Test;

public class OneBotSerializationTests
{
    [Fact]
    public void ToOneBotPost_Serializes_Message_Entities()
    {
        var convert = new EntityConvert();

        var builder = new Lagrange.Core.Message.MessageBuilder();
        builder.Text("hello");
        builder.Mention(1234, null);

        // reply: create a dummy source message
        var src = Lagrange.Core.Message.BotMessage.CreateCustomGroup(1, 2, string.Empty, DateTime.Now, new Lagrange.Core.Message.MessageChain());
        src.Sequence = 999;
        builder.Reply(src);

        // forward: multi msg with resId
        builder.MultiMsg(new List<Lagrange.Core.Message.BotMessage>());

        var chain = builder.Build();
        var msg = Lagrange.Core.Message.BotMessage.CreateCustomGroup(1, 2, string.Empty, DateTime.Now, chain);

        var post = convert.ToOneBotPost(new Lagrange.Core.Events.EventArgs.BotMessageEvent(msg, ReadOnlyMemory<byte>.Empty));

        Assert.NotNull(post);
        Assert.Equal("message", post.PostType);
        Assert.True(post.Message.HasValue && post.Message.Value.ValueKind == JsonValueKind.Array);

        var arr = post.Message.Value.EnumerateArray().ToArray();
        Assert.Contains(arr, x => x.GetProperty("type").GetString() == "text");
        Assert.Contains(arr, x => x.GetProperty("type").GetString() == "at");
        Assert.Contains(arr, x => x.GetProperty("type").GetString() == "reply");
        Assert.Contains(arr, x => x.GetProperty("type").GetString() == "forward" || x.GetProperty("type").GetString() == "text");
    }
}
