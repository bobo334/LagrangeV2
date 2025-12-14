using System.Net;
using System.Text;
using Lagrange.Milky.Configuration;
using Lagrange.Milky.Entity.OneBot;
using Lagrange.Milky.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Lagrange.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Lagrange.Milky.Api;

public class OneBotHttpApiService(ILogger<OneBotHttpApiService> logger, IOptions<OneBotConfiguration> options, IServiceProvider services) : IHostedService
{
    private readonly ILogger<OneBotHttpApiService> _logger = logger;

    private readonly string _host = options.Value.Host ?? throw new System.Exception("OneBot.Host cannot be null");
    private readonly ulong _port = options.Value.Port ?? throw new System.Exception("OneBot.Port cannot be null");
    private readonly string _prefix = $"{options.Value.Prefix}{(options.Value.Prefix.EndsWith('/') ? "" : "/")}v11";
    private readonly string? _token = options.Value.AccessToken;

    private readonly IServiceProvider _services = services;

    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _task;

    public Task StartAsync(CancellationToken token)
    {
        _listener.Prefixes.Add($"http://{_host}:{_port}{_prefix}/");
        _listener.Start();

        foreach (var prefix in _listener.Prefixes) _logger.LogServerRunning(prefix);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _task = GetHttpContextLoopAsync(_cts.Token);

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
        catch (System.Exception e)
        {
            _logger.LogGetHttpContextException(e);
            throw;
        }
    }

    private async Task HandleHttpContextAsync(HttpListenerContext context, CancellationToken token)
    {
        var request = context.Request;
        var identifier = request.RequestTraceIdentifier;
        var remote = request.RemoteEndPoint;
        var method = request.HttpMethod;
        var rawUrl = request.RawUrl;

        try
        {
            _logger.LogReceive(identifier, remote, method, rawUrl);

            if (!await ValidateHttpContextAsync(context, token)) return;

            var parameter = await GetParameterAsync<OneBotIncomingRequest>(context, token);
            if (parameter == null) return;

            object response = await HandleApiCallAsync(parameter, token);

            await SendWithLoggerAsync(context, new OneBotApiResponse<object>(0, "ok", response), token);
        }
        catch (OperationCanceledException) { throw; }
        catch (System.Exception e)
        {
            _logger.LogHandleHttpContextException(identifier, remote, e);

            var body = new OneBotApiResponse<object>(-1, "failed", new { message = e.Message });
            await SendWithLoggerAsync(context, body, token);
        }
    }

    public async Task StopAsync(CancellationToken token)
    {
        _cts?.Cancel();
        if (_task != null) await _task.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _listener.Stop();
    }

    private async Task<bool> ValidateHttpContextAsync(HttpListenerContext context, CancellationToken token)
    {
        var request = context.Request;
        var identifier = request.RequestTraceIdentifier;
        var remote = request.RemoteEndPoint;

        if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithLoggerAsync(context, HttpStatusCode.MethodNotAllowed, token);
            return false;
        }

        if (!ValidateAccessToken(context))
        {
            _logger.LogValidateAccessTokenFailed(identifier, remote);
            var body = new OneBotApiResponse<object>(-1, "failed", new { message = "unauthorized" });
            await SendWithLoggerAsync(context, body, token);
            return false;
        }

        return true;
    }

    private bool ValidateAccessToken(HttpListenerContext context)
    {
        if (_token == null) return true;

        // accept either header or query string
        string? authorization = context.Request.Headers["Authorization"];
        if (authorization != null && authorization.StartsWith("Bearer"))
        {
            if (_token == string.Empty && authorization.Length == 6) return true;
            return authorization[7..] == _token;
        }

        string? query = context.Request.QueryString["access_token"];
        if (query != null) return query == _token;

        return false;
    }

    private async Task<T?> GetParameterAsync<T>(HttpListenerContext context, CancellationToken token) where T : class
    {
        var request = context.Request;
        var identifier = request.RequestTraceIdentifier;
        var remote = request.RemoteEndPoint;
        var input = request.InputStream;

        try
        {
            using var content = new StreamContent(input);
            byte[] body = await content.ReadAsByteArrayAsync(token);
            _logger.LogReceiveBody(identifier, remote, body);

            return JsonUtility.Deserialize<T>(body);
        }
        catch (System.Exception e)
        {
            _logger.LogDeserializeParameterException(identifier, remote, e);

            await SendWithLoggerAsync(context, HttpStatusCode.BadRequest, token);

            return default;
        }
    }

    private async Task<object> HandleApiCallAsync(OneBotIncomingRequest request, CancellationToken token)
    {
        // Minimal handler: send_message
        if (request.Api == "send_message")
        {
            // map to Milky handlers
            var services = _services.CreateScope().ServiceProvider;

            if (request.Params.MessageType == "group")
            {
                var apiHandler = services.GetService<Lagrange.Milky.Api.Handler.Message.SendGroupMessageHandler>();
                if (apiHandler == null) throw new System.Exception("SendGroupMessageHandler not available");

                var param = new Lagrange.Milky.Api.Handler.Message.SendGroupMessageParameter(request.Params.TargetId, new[] { new Lagrange.Milky.Entity.Segment.TextOutgoingSegment(new Lagrange.Milky.Entity.Segment.TextSegmentData(request.Params.Message)) { Data = new Lagrange.Milky.Entity.Segment.TextSegmentData(request.Params.Message) } });
                var result = await apiHandler.HandleAsync(param, token);
                return result;
            }
            else
            {
                var apiHandler = _services.CreateScope().ServiceProvider.GetService<Lagrange.Milky.Api.Handler.Message.SendPrivateMessageHandler>();
                if (apiHandler == null) throw new System.Exception("SendPrivateMessageHandler not available");

                var param = new Lagrange.Milky.Api.Handler.Message.SendPrivateMessageParameter(request.Params.TargetId, new[] { new Lagrange.Milky.Entity.Segment.TextOutgoingSegment(new Lagrange.Milky.Entity.Segment.TextSegmentData(request.Params.Message)) { Data = new Lagrange.Milky.Entity.Segment.TextSegmentData(request.Params.Message) } });
                var result = await apiHandler.HandleAsync(param, token);
                return result;
            }
        }

        if (request.Api == "get_status")
        {
            var bot = _services.CreateScope().ServiceProvider.GetRequiredService<BotContext>();
            return new { status = bot.IsOnline ? "online" : "offline", self_id = bot.BotUin };
        }

        if (request.Api == "get_message")
        {
            var svc = _services.CreateScope().ServiceProvider;
            var handler = svc.GetService<Lagrange.Milky.Api.Handler.Message.GetMessageHandler>();
            if (handler == null) throw new System.Exception("GetMessageHandler not available");

            var scene = request.Params.MessageType == "group" ? "group" : "friend";
            var param = new Lagrange.Milky.Api.Handler.Message.GetMessageParameter(scene, request.Params.TargetId, request.Params.MessageSeq);
            var result = await handler.HandleAsync(param, token);
            return result;
        }

        if (request.Api == "delete_msg" || request.Api == "delete_message" || request.Api == "recall_message")
        {
            var svc = _services.CreateScope().ServiceProvider;
            if (request.Params.MessageType == "group")
            {
                var handler = svc.GetService<Lagrange.Milky.Api.Handler.Message.RecallGroupMessageHandler>();
                if (handler == null) throw new System.Exception("RecallGroupMessageHandler not available");

                var param = new Lagrange.Milky.Api.Handler.Message.RecallGroupMessageParameter(request.Params.TargetId, request.Params.MessageSeq);
                await handler.HandleAsync(param, token);
                return new { message = "ok" };
            }
            else
            {
                var handler = svc.GetService<Lagrange.Milky.Api.Handler.Message.RecallPrivateMessageHandler>();
                if (handler == null) throw new System.Exception("RecallPrivateMessageHandler not available");

                var param = new Lagrange.Milky.Api.Handler.Message.RecallPrivateMessageParameter(request.Params.TargetId, request.Params.MessageSeq);
                await handler.HandleAsync(param, token);
                return new { message = "ok" };
            }
        }

        if (request.Api == "get_friend_list")
        {
            var svc = _services.CreateScope().ServiceProvider;
            var handler = svc.GetService<Lagrange.Milky.Api.Handler.System.GetFriendListHandler>();
            var param = new Lagrange.Milky.Api.Handler.System.GetFriendListParameter(false);
            if (handler == null) throw new System.Exception("GetFriendListHandler not available");
            var result = await handler.HandleAsync(param, token);
            return result;
        }

        if (request.Api == "get_group_list")
        {
            var svc = _services.CreateScope().ServiceProvider;
            var handler = svc.GetService<Lagrange.Milky.Api.Handler.System.GetGroupListHandler>();
            if (handler == null) throw new System.Exception("GetGroupListHandler not available");
            var param = new Lagrange.Milky.Api.Handler.System.GetGroupListParameter(false);
            var result = await handler.HandleAsync(param, token);
            return result;
        }

        return new { message = "unsupported" };
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
        catch (System.Exception e)
        {
            _logger.LogSendException(identifier, remote, e);
        }
    }

    private async Task SendWithLoggerAsync<TBody>(HttpListenerContext context, TBody body, CancellationToken token) where TBody : notnull
    {
        var request = context.Request;
        var identifier = request.RequestTraceIdentifier;
        var remote = request.RemoteEndPoint;

        var response = context.Response;
        var output = response.OutputStream;

        try
        {
            byte[] buffer = JsonUtility.SerializeToUtf8Bytes(body.GetType(), body);

            response.ContentType = "application/json; charset=utf-8";
            await output.WriteAsync(buffer, token);
            response.Close();

            _logger.LogSend(identifier, remote, buffer);
        }
        catch (System.Exception e)
        {
            _logger.LogSendException(identifier, remote, e);
        }
    }

    private class OneBotIncomingRequest
    {
        public string Api { get; set; } = string.Empty;
        public OneBotIncomingParams Params { get; set; } = new();
    }

    private class OneBotIncomingParams
    {
        public string MessageType { get; set; } = "private";
        public long TargetId { get; set; }
        public string Message { get; set; } = string.Empty;
        public long MessageSeq { get; set; }
        public long MessageId { get; set; }
        public string? MessageScene { get; set; }
    }
}

public static partial class OneBotApiServiceLoggerExtension
{
    [LoggerMessage(LogLevel.Information, "OneBot http server is running on {prefix}")]
    public static partial void LogServerRunning(this ILogger<OneBotHttpApiService> logger, string prefix);

    [LoggerMessage(LogLevel.Debug, "{identifier} {remote} -->> {method} {path}")]
    public static partial void LogReceive(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote, string method, string? path);

    [LoggerMessage(LogLevel.Debug, "{identifier} {remote} -->> {body}")]
    private static partial void LogReceiveBody(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote, string body);
    public static void LogReceiveBody(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote, Span<byte> body)
    {
        if (logger.IsEnabled(LogLevel.Debug)) logger.LogReceiveBody(identifier, remote, Encoding.UTF8.GetString(body));
    }

    [LoggerMessage(LogLevel.Debug, "{identifier} {remote} <<-- {status}")]
    public static partial void LogSend(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote, HttpStatusCode status);

    [LoggerMessage(LogLevel.Debug, "{identifier} {remote} <<-- {body}", SkipEnabledCheck = true)]
    private static partial void LogSend(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote, string body);
    public static void LogSend(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote, Span<byte> body)
    {
        if (logger.IsEnabled(LogLevel.Debug)) logger.LogSend(identifier, remote, Encoding.UTF8.GetString(body));
    }

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> Send failed")]
    public static partial void LogSendException(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote, System.Exception e);

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> Handle http context failed")]
    public static partial void LogHandleHttpContextException(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote, System.Exception e);

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> Deserialize parameter failed")]
    public static partial void LogDeserializeParameterException(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote, System.Exception e);

    [LoggerMessage(LogLevel.Error, "{identifier} {remote} <!!> Validate access token failed")]
    public static partial void LogValidateAccessTokenFailed(this ILogger<OneBotHttpApiService> logger, Guid identifier, IPEndPoint remote);

    [LoggerMessage(LogLevel.Error, "Get http context failed")]
    public static partial void LogGetHttpContextException(this ILogger<OneBotHttpApiService> logger, System.Exception e);
}
