using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Events.EventArgs;
using Lagrange.Core.Message.Entities;
using Lagrange.Milky.Configuration;
using Lagrange.Milky.Event;
using Lagrange.Milky.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lagrange.Milky.Test;

public class OneBotReverseIntegrationTests
{
    [Fact]
    public async Task ReverseWebSocket_Client_Receives_Message_And_Posts_Event()
    {
        // pick an ephemeral port
        var tl = new TcpListener(IPAddress.Loopback, 0);
        tl.Start();
        var port = ((IPEndPoint)tl.LocalEndpoint).Port;
        tl.Stop();

        var prefix = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var acceptTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            var ws = wsCtx.WebSocket;

            // Wait a moment for the client to be ready
            await Task.Delay(50);

            var postJson = $"{{\"post_type\":\"message\",\"time\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()},\"self_id\":0,\"message_type\":\"private\",\"message\":[{{\"type\":\"text\",\"data\":{{\"text\":\"hello from reverse ws\"}}}}]}}";
            var bytes = Encoding.UTF8.GetBytes(postJson);

            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

            // give client time to process then close
            await Task.Delay(200);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        });

        var onebot = new OneBotConfiguration { Reverse = new OneBotReverseConfiguration { Enabled = true, Url = $"ws://127.0.0.1:{port}/" } };
        var options = Options.Create(onebot);

        var bot = BotFactory.Create(new BotConfig());
        var cache = new Lagrange.Milky.Cache.MessageCache(bot, Options.Create(new MilkyConfiguration()));
        var resolver = new ResourceResolver();

        var logger = new LoggerFactory().CreateLogger<OneBotReverseWebSocketClientService>();
        var svc = new OneBotReverseWebSocketClientService(logger, options, bot, cache, resolver);

        var tcs = new TaskCompletionSource<BotMessageEvent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bot.EventInvoker.RegisterEvent<BotMessageEvent>((_, e) => tcs.TrySetResult(e));

        await svc.StartAsync(CancellationToken.None);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(received);
        Assert.Contains("hello from reverse ws", received!.Message.Entities.OfType<TextEntity>().Select(t => t.Text));

        await svc.StopAsync(CancellationToken.None);
        listener.Stop();
    }
}
