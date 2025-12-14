using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Lagrange.Milky.Configuration;
using Lagrange.Milky.Utility;
using Lagrange.Milky.Entity.OneBot;
using Lagrange.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lagrange.Milky.Event;

public class OneBotWebSocketEventService(ILogger<OneBotWebSocketEventService> logger, IOptions<OneBotConfiguration> options, BotContext bot, EntityConvert convert) : IHostedService
{
    private readonly ILogger<OneBotWebSocketEventService> _logger = logger;

    private readonly string _host = options.Value.Host ?? throw new Exception("OneBot.Host cannot be null");
    private readonly ulong _port = options.Value.Port ?? throw new Exception("OneBot.Port cannot be null");
    private readonly string _path = $"{options.Value.Prefix}{(options.Value.Prefix.EndsWith('/') ? "" : "/")}ws";
    private readonly string? _token = options.Value.AccessToken;

    private readonly EntityConvert _convert = convert;
    private readonly BotContext _bot = bot;

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<ConnectionContext, object?> _connections = [];
    private CancellationTokenSource? _cts;
    private Task? _task;

    public Task StartAsync(CancellationToken token)
    {
        _listener.Prefixes.Add($"http://{_host}:{_port}{_path}/");
        _listener.Start();

        foreach (var prefix in _listener.Prefixes) _logger.LogServerRunning(prefix);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _task = GetHttpContextLoopAsync(_cts.Token);

        // register to bot events
        _bot.EventInvoker.RegisterEvent<Lagrange.Core.Events.EventArgs.BotMessageEvent>(HandleMessageEvent);
        _bot.EventInvoker.RegisterEvent<Lagrange.Core.Events.EventArgs.BotOfflineEvent>(HandleOfflineEvent);

        return Task.CompletedTask;
    }

    private async Task GetHttpContextLoopAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                _ = HandleHttpContextAsync(await _listener.GetContextAsync().WaitAsync(token), token);

                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _logger.LogGetHttpContextException(e);
            throw;
        }
    }

    private async Task HandleHttpContextAsync(HttpListenerContext httpContext, CancellationToken token)
    {
        var request = httpContext.Request;
        var identifier = request.RequestTraceIdentifier;
        var remote = request.RemoteEndPoint;
        string method = request.HttpMethod;
        string? rawUrl = request.RawUrl;

        try
        {
            _logger.LogHttpContext(identifier, remote, method, rawUrl);

            if (!await ValidateHttpContextAsync(httpContext, token)) return;

            var connection = await GetConnectionContextAsync(httpContext, token);
            if (connection == null) return;

            _ = WaitConnectionCloseLoopAsync(connection, connection.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            await SendWithLoggerAsync(httpContext, HttpStatusCode.InternalServerError, token);
            throw;
        }
        catch (Exception e)
        {
            _logger.LogHandleHttpContextException(identifier, remote, e);
            await SendWithLoggerAsync(httpContext, HttpStatusCode.InternalServerError, token);
        }
    }

    private async Task WaitConnectionCloseLoopAsync(ConnectionContext connection, CancellationToken token)
    {
        var identifier = connection.HttpContext.Request.RequestTraceIdentifier;
        var remote = connection.HttpContext.Request.RemoteEndPoint;

        try
        {
            byte[] buffer = new byte[1024];
            while (true)
            {
                ValueTask<ValueWebSocketReceiveResult> resultTask = connection.WsContext.WebSocket
                        .ReceiveAsync(buffer.AsMemory(), default);

                ValueWebSocketReceiveResult result = !resultTask.IsCompleted ?
                    await resultTask.AsTask().WaitAsync(token) :
                    resultTask.Result;

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseConnectionAsync(connection, WebSocketCloseStatus.NormalClosure, token);
                    return;
                }

                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            await CloseConnectionAsync(connection, WebSocketCloseStatus.NormalClosure, default);
        }
        catch (Exception e)
        {
            _logger.LogWaitWebSocketCloseException(identifier, remote, e);

            await CloseConnectionAsync(connection, WebSocketCloseStatus.InternalServerError, token);
        }
    }

    private async Task CloseConnectionAsync(ConnectionContext connection, WebSocketCloseStatus status, CancellationToken token)
    {
        var identifier = connection.HttpContext.Request.RequestTraceIdentifier;
        var remote = connection.HttpContext.Request.RemoteEndPoint;

        try
        {
            _connections.Remove(connection, out _);

            await connection.WsContext.WebSocket.CloseAsync(status, null, token);
            connection.HttpContext.Response.Close();

            _logger.LogWebSocketClosed(identifier, remote);
        }
        catch (Exception e)
        {
            _logger.LogWebSocketCloseException(identifier, remote, e);
        }
        finally
        {
            connection.Tcs.SetResult();
        }
    }

    private async void HandleMessageEvent(BotContext bot, Lagrange.Core.Events.EventArgs.BotMessageEvent @event)
    {
        try
        {
            if (_connections.IsEmpty) return;

            var payload = _convert.ToOneBotPost(@event);
            byte[] bytes = JsonUtility.SerializeToUtf8Bytes(payload.GetType(), payload);
            _logger.LogSend(bytes);

            foreach (var connection in _connections.Keys)
            {
                var identifier = connection.HttpContext.Request.RequestTraceIdentifier;
                var remote = connection.HttpContext.Request.RemoteEndPoint;
                var ws = connection.WsContext.WebSocket;

                try
                {
                    await connection.SendSemaphoreSlim.WaitAsync(connection.Cts.Token);
                    try
                    {
                        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, connection.Cts.Token);
                    }
                    finally
                    {
                        connection.SendSemaphoreSlim.Release();
                    }
                }
                catch (Exception e)
                {
                    _logger.LogSendException(identifier, remote, e);

                    await CloseConnectionAsync(connection, WebSocketCloseStatus.InternalServerError, connection.Cts.Token);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogHandleEventException(e);
        }
    }

    private async void HandleOfflineEvent(BotContext bot, Lagrange.Core.Events.EventArgs.BotOfflineEvent @event)
    {
        try
        {
            if (_connections.IsEmpty) return;

            var payload = _convert.ToOneBotPost(@event);
            byte[] bytes = JsonUtility.SerializeToUtf8Bytes(payload.GetType(), payload);

            foreach (var connection in _connections.Keys)
            {
                var identifier = connection.HttpContext.Request.RequestTraceIdentifier;
                var remote = connection.HttpContext.Request.RemoteEndPoint;
                var ws = connection.WsContext.WebSocket;

                try
                {
                    await connection.SendSemaphoreSlim.WaitAsync(connection.Cts.Token);
                    try
                    {
                        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, connection.Cts.Token);
                    }
                    finally
                    {
                        connection.SendSemaphoreSlim.Release();
                    }
                }
                catch (Exception e)
                {
                    _logger.LogSendException(identifier, remote, e);
                    await CloseConnectionAsync(connection, WebSocketCloseStatus.InternalServerError, connection.Cts.Token);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogHandleEventException(e);
        }
    }

    public async Task StopAsync(CancellationToken token)
    {
        _bot.EventInvoker.UnregisterEvent<Lagrange.Core.Events.EventArgs.BotMessageEvent>(HandleMessageEvent);
        _bot.EventInvoker.UnregisterEvent<Lagrange.Core.Events.EventArgs.BotOfflineEvent>(HandleOfflineEvent);

        _cts?.Cancel();
        if (_task != null) await _task.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Task.WhenAll(_connections.Keys.Select(connection => connection.Tcs.Task));

        _listener.Stop();
    }

    private async Task<bool> ValidateHttpContextAsync(HttpListenerContext httpContext, CancellationToken token)
    {
        var request = httpContext.Request;
        var identifier = request.RequestTraceIdentifier;
        var remote = request.RemoteEndPoint;

        if (request.Url?.LocalPath != _path)
        {
            await SendWithLoggerAsync(httpContext, HttpStatusCode.NotFound, token);
        }

        if (!httpContext.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithLoggerAsync(httpContext, HttpStatusCode.MethodNotAllowed, token);
            return false;
        }

        if (!ValidateApiAccessToken(httpContext))
        {
            _logger.LogValidateAccessTokenFailed(identifier, remote);
            await SendWithLoggerAsync(httpContext, HttpStatusCode.Unauthorized, token);
            return false;
        }

        if (!request.IsWebSocketRequest)
        {
            await SendWithLoggerAsync(httpContext, HttpStatusCode.BadRequest, token);
            return false;
        }

        return true;
    }

    private bool ValidateApiAccessToken(HttpListenerContext httpContext)
    {
        if (_token == null) return true;

        // accept header or query parameter
        string? authorization = httpContext.Request.Headers["Authorization"];
        if (authorization != null && authorization.StartsWith("Bearer"))
        {
            if (_token == string.Empty && authorization.Length == 6) return true;
            return authorization[7..] == _token;
        }

        string? query = httpContext.Request.QueryString["access_token"];
        if (query != null) return query == _token;

        return false;
    }

    private async Task<ConnectionContext?> GetConnectionContextAsync(HttpListenerContext httpContext, CancellationToken token)
    {
        var request = httpContext.Request;
        var identifier = request.RequestTraceIdentifier;
        var remote = request.RemoteEndPoint;

        try
        {
            var wsContext = await httpContext.AcceptWebSocketAsync(null).WaitAsync(token);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var connection = new ConnectionContext(httpContext, wsContext, cts);
            _connections.TryAdd(connection, null);
            return connection;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _logger.LogUpgradeWebSocketException(identifier, remote, e);
            await SendWithLoggerAsync(httpContext, HttpStatusCode.InternalServerError, token);
        }

        return null;
    }

    private async Task SendWithLoggerAsync(HttpListenerContext context, HttpStatusCode status, CancellationToken token)
    {
        var request = context.Request;
        var identifier = request.RequestTraceIdentifier;
        var remote = request.RemoteEndPoint;

        var response = context.Response;
        var output = response.OutputStream;

        try
        {
            int code = (int)status;

            response.StatusCode = code;
            await output.WriteAsync(Encoding.UTF8.GetBytes($"{code} {status}"), token);
            response.Close();

            _logger.LogSend(identifier, remote, status);
        }
        catch (Exception e)
        {
            _logger.LogSendException(identifier, remote, e);
        }
    }

    private class ConnectionContext(HttpListenerContext httpContext, WebSocketContext wsContext, CancellationTokenSource cts)
    {
        public HttpListenerContext HttpContext { get; } = httpContext;
        public WebSocketContext WsContext { get; } = wsContext;

        public SemaphoreSlim SendSemaphoreSlim { get; } = new(1);

        public CancellationTokenSource Cts { get; } = cts;
        public TaskCompletionSource Tcs { get; } = new();
    }
}

public static partial class OneBotWebSocketEventServiceLoggerExtension
{
    [LoggerMessage(LogLevel.Information, "OneBot event websocket server is running on {prefix}")]
    public static partial void LogServerRunning(this ILogger<OneBotWebSocketEventService> logger, string prefix);

    [LoggerMessage(LogLevel.Debug, "{identifier} {remote} -->> {method} {path}")]
    public static partial void LogHttpContext(this ILogger<OneBotWebSocketEventService> logger, Guid identifier, IPEndPoint remote, string method, string? path);

    [LoggerMessage(LogLevel.Debug, "WebSockets <<-- {payload}")]
    private static partial void LogSend(this ILogger<OneBotWebSocketEventService> logger, string payload);
    public static void LogSend(this ILogger<OneBotWebSocketEventService> logger, Span<byte> payload)
    {
        if (logger.IsEnabled(LogLevel.Debug)) logger.LogSend(Encoding.UTF8.GetString(payload));
    }

    [LoggerMessage(LogLevel.Debug, "{identifier} {remote} <//> WebSocket closed")]
    public static partial void LogWebSocketClosed(this ILogger<OneBotWebSocketEventService> logger, Guid identifier, IPEndPoint remote);

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> WebSocket close failed")]
    public static partial void LogWebSocketCloseException(this ILogger<OneBotWebSocketEventService> logger, Guid identifier, IPEndPoint remote, Exception e);

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> Wait websocket close failed")]
    public static partial void LogWaitWebSocketCloseException(this ILogger<OneBotWebSocketEventService> logger, Guid identifier, IPEndPoint remote, Exception e);

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> Send failed")]
    public static partial void LogSendException(this ILogger<OneBotWebSocketEventService> logger, Guid identifier, IPEndPoint remote, Exception e);

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> Handle http context failed")]
    public static partial void LogHandleHttpContextException(this ILogger<OneBotWebSocketEventService> logger, Guid identifier, IPEndPoint remote, Exception e);

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> Upgrade websocket failed")]
    public static partial void LogUpgradeWebSocketException(this ILogger<OneBotWebSocketEventService> logger, Guid identifier, IPEndPoint remote, Exception e);

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> Validate access token failed")]
    public static partial void LogValidateAccessTokenFailed(this ILogger<OneBotWebSocketEventService> logger, Guid identifier, IPEndPoint remote);

    [LoggerMessage(LogLevel.Error, "Get http context failed")]
    public static partial void LogGetHttpContextException(this ILogger<OneBotWebSocketEventService> logger, Exception e);

    [LoggerMessage(LogLevel.Error, "Handle event failed")]
    public static partial void LogHandleEventException(this ILogger<OneBotWebSocketEventService> logger, Exception e);
}
