using System.Net.WebSockets;
using System.Text;
using Lagrange.Milky.Configuration;
using Lagrange.Milky.Utility;
using Lagrange.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lagrange.Milky.Event;

public class OneBotReverseWebSocketClientService(ILogger<OneBotReverseWebSocketClientService> logger, IOptions<OneBotConfiguration> options, BotContext bot, Lagrange.Milky.Cache.MessageCache cache, ResourceResolver resolver) : IHostedService
{
    private readonly ILogger<OneBotReverseWebSocketClientService> _logger = logger;

    private readonly OneBotConfiguration _options = options.Value;
    private readonly BotContext _bot = bot;
    private readonly Lagrange.Milky.Cache.MessageCache _cache = cache;
    private readonly ResourceResolver _resolver = resolver;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public Task StartAsync(CancellationToken token)
    {
        if (!(_options.Reverse?.Enabled ?? false) || string.IsNullOrEmpty(_options.Reverse?.Url)) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _task = ConnectLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task ConnectLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();

                    if (!string.IsNullOrEmpty(_options.Reverse?.AccessToken))
                    {
                        ws.Options.SetRequestHeader("Authorization", $"Bearer {_options.Reverse!.AccessToken!}");
                    }

                    await ws.ConnectAsync(new Uri(_options.Reverse!.Url!), token);

                    _logger.LogConnected(_options.Reverse!.Url!);

                byte[] buffer = new byte[4096];
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogRemoteClosed();
                        break;
                    }

                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogMessageReceived(text);

                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(text);
                        var post = JsonUtility.Deserialize<Lagrange.Milky.Entity.OneBot.OneBotPostEvent>(bytes);
                        if (post != null)
                        {
                            var ev = new EntityConvert(_bot, _cache, _resolver).FromOneBotPost(post, bytes);
                            if (ev != null) _bot.EventInvoker.PostEvent(ev);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogParseFailed(e);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _logger.LogConnectFailed(e);
                await Task.Delay(TimeSpan.FromSeconds(5), token).ContinueWith(_ => { });
            }
        }
    }

    public Task StopAsync(CancellationToken token)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }
}

public static partial class OneBotReverseClientLogger
{
    [LoggerMessage(LogLevel.Information, "OneBot reverse ws connected to {url}")]
    public static partial void LogConnected(this ILogger<OneBotReverseWebSocketClientService> logger, string url);

    [LoggerMessage(LogLevel.Debug, "Remote closed the connection")]
    public static partial void LogRemoteClosed(this ILogger<OneBotReverseWebSocketClientService> logger);

    [LoggerMessage(LogLevel.Debug, "Message from reverse ws: {text}")]
    public static partial void LogMessageReceived(this ILogger<OneBotReverseWebSocketClientService> logger, string text);

    [LoggerMessage(LogLevel.Error, "Connect failed")]
    public static partial void LogConnectFailed(this ILogger<OneBotReverseWebSocketClientService> logger, Exception e);

    [LoggerMessage(LogLevel.Error, "Parse incoming OneBot message failed")]
    public static partial void LogParseFailed(this ILogger<OneBotReverseWebSocketClientService> logger, Exception e);
}
