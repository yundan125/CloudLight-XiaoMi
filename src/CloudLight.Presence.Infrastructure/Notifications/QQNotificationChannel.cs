using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Infrastructure.Diagnostics;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.Infrastructure.Notifications;

public sealed class QQNotificationChannel : INotificationChannel, IAsyncDisposable
{
    private const string ApiBaseUrl = "https://api.bot.qq.com";
    private const string TokenEndpoint = "https://api.bot.qq.com/app/getAppAccessToken";
    private const string UserAgent = "CloudLight-XiaoMi/2.1.3";
    private const int GroupAndC2CIntent = 1 << 25;
    private const int DefaultRequestTimeoutSeconds = 15;
    private const int DefaultConnectTimeoutSeconds = 20;
    private const int TokenRefreshMarginMinutes = 5;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly SemaphoreSlim _socketWriteGate = new(1, 1);
    private readonly string? _logsDirectory;
    private readonly HttpMessageHandler? _httpMessageHandler;
    private QqNotificationSettings _settings = new();
    private string _secret = string.Empty;
    private HttpClient? _httpClient;
    private IWebProxy? _proxy;
    private string _accessToken = string.Empty;
    private DateTimeOffset _accessTokenExpiresAt;
    private string _sessionId = string.Empty;
    private long _sequence;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private int _runId;
    private NotificationChannelStatus _status = new(NotificationChannelType.QQ, false, false, false, NotificationConnectionState.NotConfigured);

    public QQNotificationChannel(string? logsDirectory = null, HttpMessageHandler? httpMessageHandler = null)
    {
        _logsDirectory = logsDirectory;
        _httpMessageHandler = httpMessageHandler;
    }

    public NotificationChannelType ChannelType => NotificationChannelType.QQ;
    public NotificationChannelStatus Status { get { lock (_sync) return _status; } }
    public string CurrentAppId { get { lock (_sync) return _settings.AppId; } }
    public bool IsRunning => Status.Running;
    public event EventHandler<NotificationChannelStatus>? StatusChanged;

    public async Task ConfigureAsync(QqNotificationSettings settings, string? appSecret, CancellationToken cancellationToken)
    {
        var normalized = NormalizeSettings(settings);
        if (IsRunning) await StopAsync(cancellationToken);
        HttpClient? previous;
        bool configured;
        lock (_sync)
        {
            _settings = normalized;
            if (appSecret is not null) _secret = appSecret.Trim();
            configured = normalized.Enabled && !string.IsNullOrWhiteSpace(normalized.AppId) && !string.IsNullOrWhiteSpace(_secret);
            previous = _httpClient;
            _proxy = BuildProxy(normalized.ProxyMode, normalized.ProxyUrl);
            _httpClient = BuildHttpClient(normalized.ProxyMode, _proxy, _httpMessageHandler);
            _accessToken = string.Empty; _accessTokenExpiresAt = default; _sessionId = string.Empty; _sequence = 0;
        }
        previous?.Dispose();
        PublishStatus(_status with
        {
            Configured = configured,
            Running = false, Connected = false,
            ConnectionState = configured
                ? NotificationConnectionState.Stopped : NotificationConnectionState.NotConfigured,
            LastError = null, AccessTokenExpiresAt = null
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        NotificationChannelStatus? changed = null;
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false }) return Task.CompletedTask;
            if (!_status.Configured || _httpClient is null)
            {
                _status = _status with { Running = false, Connected = false, ConnectionState = NotificationConnectionState.NotConfigured };
                changed = _status;
            }
            else
            {
                _runCancellation?.Dispose();
                _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var runId = ++_runId;
                var runToken = _runCancellation.Token;
                _status = _status with { Running = true, Connected = false, ConnectionState = NotificationConnectionState.Authenticating, LastError = null };
                changed = _status;
                _runTask = Task.Run(() => RunLoopAsync(runId, runToken));
            }
        }
        if (changed is { } status) StatusChanged?.Invoke(this, status);
        return Task.CompletedTask;
    }

    public void ReportConfigurationError(string message)
    {
        var safe = SafeError(message);
        LogError("configuration", safe);
        PublishStatus(Status with
        {
            Configured = false,
            Running = false,
            Connected = false,
            ConnectionState = NotificationConnectionState.AuthenticationFailed,
            LastError = safe
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? task;
        CancellationTokenSource? source;
        ClientWebSocket? socket;
        NotificationChannelStatus? changed = null;
        lock (_sync)
        {
            task = _runTask; source = _runCancellation; socket = _socket;
            if (task is { IsCompleted: false })
            {
                _status = _status with { ConnectionState = NotificationConnectionState.Stopping };
                changed = _status;
            }
        }
        if (changed is { } status) StatusChanged?.Invoke(this, status);
        if (source is not null) await source.CancelAsync();
        socket?.Abort();
        if (task is not null)
        {
            try { await task.WaitAsync(cancellationToken); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        }
    }

    public async Task<NotificationSendResult> SendAsync(NotificationRequest request, int startPart, CancellationToken cancellationToken)
    {
        if (request.Channel != NotificationChannelType.QQ) return Failed("通知通道类型不匹配。", 0, 0, NotificationFailureKind.InvalidRequest);
        if (string.IsNullOrWhiteSpace(request.TargetId)) return Failed("QQ 通知目标为空。", 0, 0, NotificationFailureKind.InvalidRequest);
        if (request.TargetId.Contains('*', StringComparison.Ordinal)) return Failed("QQ 通知目标不能使用脱敏占位符，请输入完整 OpenID。", 0, 0, NotificationFailureKind.InvalidRequest);
        if (request.TargetType is not (NotificationTargetType.Private or NotificationTargetType.Group)) return Failed("QQ 通知目标类型无效。", 0, 0, NotificationFailureKind.InvalidRequest);
        var status = Status;
        if (!status.Connected) return Failed("QQ 当前未连接，通知已等待重试。", 0, 0, NotificationFailureKind.Transient);
        var parts = QQMessageSplitter.Split(request.Message, 5000);
        if (parts.Count == 0) return Failed("通知内容为空。", 0, 0, NotificationFailureKind.InvalidRequest);
        startPart = Math.Clamp(startPart, 0, parts.Count);
        var sent = 0; var ids = new List<string>();
        for (var index = startPart; index < parts.Count; index++)
        {
            try
            {
                var id = await SendTextPartAsync(request.TargetType, request.TargetId, parts[index], cancellationToken);
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                sent++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (QQNotificationException exception)
            {
                var safeError = SafeError(exception.Message, request.TargetId);
                LogError("send_failure", FormatSendFailureLog(request, exception));
                SetLastError(safeError);
                return new NotificationSendResult(false, sent, parts.Count, safeError, ids,
                    exception.FailureKind, exception.HttpStatusCode, exception.QqErrorCode,
                    exception.TraceId, exception.EndpointCategory);
            }
            catch (Exception exception)
            {
                var safeError = SafeError(exception.Message, request.TargetId);
                LogError("send_failure", $"endpoint={QQNotificationEndpoint.CategoryFor(request.TargetType)}; method=POST; targetLength={request.TargetId.Length}; targetSha256={Fingerprint(request.TargetId)}; failureKind={NotificationFailureKind.Unknown}; message={safeError}");
                SetLastError(safeError);
                return new NotificationSendResult(false, sent, parts.Count, safeError, ids, NotificationFailureKind.Unknown,
                    EndpointCategory: QQNotificationEndpoint.CategoryFor(request.TargetType));
            }
        }
        LogError("send_success", FormatSendSuccessLog(request, parts.Count, sent));
        return new NotificationSendResult(true, sent, parts.Count, null, ids);
    }

    public Task<NotificationSendResult> SendTestAsync(NotificationTargetType targetType, string targetId, CancellationToken cancellationToken) =>
        SendTestAsync(targetType, targetId, "CloudLight XiaoMi QQ 通知测试成功。", cancellationToken);

    public Task<NotificationSendResult> SendTestAsync(NotificationTargetType targetType, string targetId, string message, CancellationToken cancellationToken) =>
        SendAsync(new NotificationRequest(0, 0, 0, "test", NotificationChannelType.QQ, targetType, targetId, message, DateTimeOffset.UtcNow), 0, cancellationToken);

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await GetAccessTokenAsync(forceRefresh: true, cancellationToken);
            _ = await GetGatewayUrlAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            SetLastError(SafeError(exception.Message));
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        HttpClient? client;
        lock (_sync) { client = _httpClient; _httpClient = null; _secret = string.Empty; _accessToken = string.Empty; _accessTokenExpiresAt = default; }
        client?.Dispose(); _tokenGate.Dispose(); _socketWriteGate.Dispose();
    }

    private async Task RunLoopAsync(int runId, CancellationToken cancellationToken)
    {
        var backoffs = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };
        var attempt = 0; var resume = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var connection = await ConnectGatewayAsync(runId, resume, cancellationToken);
                    attempt = 0; resume = true;
                    await ReadConnectionAsync(connection.Socket, connection.HeartbeatInterval, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    var error = exception as QQNotificationException ?? new QQNotificationException("gateway_closed", "QQ 连接已断开。", exception);
                    bool reconnectEnabled;
                    lock (_sync) reconnectEnabled = _settings.GatewayReconnectEnabled;
                    if (error.Code is "gateway_session_invalid") { ClearSession(); resume = false; }
                    if (error.Code is "token_expired") { InvalidateAccessToken(); resume = false; }
                    var canReconnect = reconnectEnabled && error.Code is not ("credentials_missing" or "secret_invalid" or "auth_failed");
                    SetConnectionFailure(error, canReconnect);
                    if (!canReconnect) break;
                    var delay = error.Code == "rate_limited" ? TimeSpan.FromSeconds(60) : backoffs[Math.Min(attempt, backoffs.Length - 1)];
                    attempt++;
                    PublishStatus(Status with { Running = true, Connected = false, ConnectionState = NotificationConnectionState.Reconnecting, LastError = SafeError($"{error.Message}；{delay.TotalSeconds:0} 秒后重试"), ReconnectCount = Status.ReconnectCount + 1 });
                    try { await Task.Delay(delay, cancellationToken); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                }
            }
        }
        finally
        {
            NotificationChannelStatus? changed = null;
            lock (_sync)
            {
                if (_runId == runId)
                {
                    _socket = null; _runTask = null; _runCancellation = null;
                    var terminalState = cancellationToken.IsCancellationRequested
                        ? _status.Configured ? NotificationConnectionState.Stopped : NotificationConnectionState.NotConfigured
                        : _status.ConnectionState;
                    _status = _status with { Running = false, Connected = false, ConnectionState = terminalState };
                    changed = _status;
                }
            }
            if (changed is { } status) StatusChanged?.Invoke(this, status);
        }
    }

    private async Task<(ClientWebSocket Socket, TimeSpan HeartbeatInterval)> ConnectGatewayAsync(int runId, bool resume, CancellationToken cancellationToken)
    {
        PublishStatus(Status with { ConnectionState = NotificationConnectionState.Authenticating, Running = true, Connected = false });
        var token = await GetAccessTokenAsync(forceRefresh: false, cancellationToken);
        PublishStatus(Status with { ConnectionState = NotificationConnectionState.Connecting, Running = true, Connected = false });
        var gateway = await GetGatewayUrlAsync(cancellationToken);
        var socket = new ClientWebSocket();
        ConfigureSocket(socket);
        lock (_sync)
        {
            if (_runId != runId || _runCancellation?.IsCancellationRequested != false) { socket.Dispose(); throw new OperationCanceledException(cancellationToken); }
            _socket = socket;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(DefaultConnectTimeoutSeconds));
            try { await socket.ConnectAsync(new Uri(gateway), timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new QQNotificationException("network_timeout", "连接 QQ Gateway 超时。", null); }
            catch (Exception exception) { throw new QQNotificationException("network_error", "无法连接 QQ Gateway。", exception); }

            using var hello = await ReceivePayloadAsync(socket, cancellationToken);
            if (ReadInt(hello.RootElement, "op") != 10) throw new QQNotificationException("protocol_incompatible", "QQ Gateway 未发送 Hello。", null);
            var heartbeatMs = ReadInt(hello.RootElement.GetProperty("d"), "heartbeat_interval");
            if (heartbeatMs <= 0) throw new QQNotificationException("protocol_incompatible", "QQ Gateway 心跳间隔无效。", null);
            PublishStatus(Status with { ConnectionState = NotificationConnectionState.Identifying, Running = true, Connected = false });
            var useResume = resume && !string.IsNullOrWhiteSpace(_sessionId) && _sequence > 0;
            object payload = useResume
                ? new { op = 6, d = new { token = "QQBot " + token, session_id = _sessionId, seq = _sequence } }
                : new { op = 2, d = new { token = "QQBot " + token, intents = GroupAndC2CIntent, shard = new[] { 0, 1 } } };
            await SendPayloadAsync(socket, payload, cancellationToken);
            while (true)
            {
                using var response = await ReceivePayloadAsync(socket, cancellationToken);
                UpdateSequence(response.RootElement);
                var op = ReadInt(response.RootElement, "op");
                if (op == 9) throw new QQNotificationException("gateway_session_invalid", "QQ Gateway 会话无效。", null);
                var eventName = response.RootElement.TryGetProperty("t", out var t) ? t.GetString() : null;
                if (op == 0 && (eventName == "READY" || eventName == "RESUMED"))
                {
                    if (eventName == "READY" && response.RootElement.GetProperty("d").TryGetProperty("session_id", out var session))
                    {
                        lock (_sync) _sessionId = session.GetString()?.Trim() ?? string.Empty;
                    }
                    break;
                }
            }
            PublishStatus(Status with { Running = true, Connected = true, ConnectionState = NotificationConnectionState.Connected, LastError = null, LastConnectedAt = DateTimeOffset.UtcNow, LastHeartbeatAt = null, LastHeartbeatAckAt = null, AccessTokenExpiresAt = _accessTokenExpiresAt });
            return (socket, TimeSpan.FromMilliseconds(heartbeatMs));
        }
        catch
        {
            socket.Abort(); socket.Dispose(); lock (_sync) { if (ReferenceEquals(_socket, socket)) _socket = null; }
            throw;
        }
    }

    private async Task ReadConnectionAsync(ClientWebSocket socket, TimeSpan heartbeatInterval, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = HeartbeatLoopAsync(socket, heartbeatInterval, linked.Token, heartbeatFailure);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = ReceivePayloadAsync(socket, linked.Token);
                var completed = await Task.WhenAny(received, heartbeatFailure.Task);
                if (completed == heartbeatFailure.Task) throw await heartbeatFailure.Task;
                using var payload = await received;
                UpdateSequence(payload.RootElement);
                var op = ReadInt(payload.RootElement, "op");
                if (op == 1) { await SendHeartbeatAsync(socket, cancellationToken); continue; }
                if (op == 11) { PublishStatus(Status with { LastHeartbeatAckAt = DateTimeOffset.UtcNow }); continue; }
                if (op == 7) throw new QQNotificationException("gateway_closed", "QQ Gateway 请求重新连接。", null);
                if (op == 9) throw new QQNotificationException("gateway_session_invalid", "QQ Gateway 会话无效。", null);
            }
        }
        catch (WebSocketException exception) { throw new QQNotificationException("gateway_closed", "QQ Gateway 连接已断开。", exception); }
        finally
        {
            linked.Cancel();
            try { await heartbeat; }
            catch (Exception exception) { LogError("heartbeat_cleanup", SafeError(exception.Message)); }
            socket.Abort(); socket.Dispose();
            lock (_sync) { if (ReferenceEquals(_socket, socket)) _socket = null; }
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, TimeSpan interval, CancellationToken cancellationToken, TaskCompletionSource<Exception> failure)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var status = Status;
                if (status.LastHeartbeatAt is not null && (status.LastHeartbeatAckAt is null || status.LastHeartbeatAckAt < status.LastHeartbeatAt))
                {
                    var error = new QQNotificationException("gateway_closed", "QQ Gateway 心跳确认超时。", null); failure.TrySetResult(error); socket.Abort(); return;
                }
                await SendHeartbeatAsync(socket, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { failure.TrySetResult(exception); socket.Abort(); }
    }

    private async Task SendHeartbeatAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        long sequence; lock (_sync) sequence = _sequence;
        await SendPayloadAsync(socket, new { op = 1, d = sequence > 0 ? sequence : (long?)null }, cancellationToken);
        PublishStatus(Status with { LastHeartbeatAt = DateTimeOffset.UtcNow });
    }

    private async Task<string> SendTextPartAsync(NotificationTargetType targetType, string targetId, string text, CancellationToken cancellationToken)
    {
        var endpointCategory = QQNotificationEndpoint.CategoryFor(targetType);
        var path = QQNotificationEndpoint.BuildMessagePath(targetType, targetId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetAccessTokenAsync(forceRefresh: false, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiBaseUrl + path) { Content = new StringContent(JsonSerializer.Serialize(new { content = text, msg_type = 0 }), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token); request.Headers.UserAgent.ParseAdd(UserAgent);
            try
            {
                using var response = await HttpClient.SendAsync(request, cancellationToken); var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { InvalidateAccessToken(); continue; }
                if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, raw, "QQ 消息发送失败。", endpointCategory: endpointCategory, responseTraceId: ReadTraceHeader(response));
                if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    return ReadString(document.RootElement, "id") ?? ReadString(document.RootElement, "msg_id") ?? string.Empty;
                }
                catch (JsonException exception)
                {
                    throw new QQNotificationException("protocol_incompatible", "QQ API 成功响应格式无效。", exception,
                        NotificationFailureKind.Transient, (int)response.StatusCode, traceId: ReadTraceHeader(response), endpointCategory: endpointCategory);
                }
            }
            catch (QQNotificationException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { throw new QQNotificationException("network_timeout", "QQ 消息发送请求超时。", null, NotificationFailureKind.Transient, endpointCategory: endpointCategory); }
            catch (HttpRequestException exception) { throw new QQNotificationException("network_error", "QQ 消息发送网络请求失败。", exception, NotificationFailureKind.Transient, endpointCategory: endpointCategory); }
        }
        throw new QQNotificationException("token_expired", "QQ Access Token 已失效。", null, NotificationFailureKind.Authentication, endpointCategory: "token");
    }

    private async Task<string> GetGatewayUrlAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetAccessTokenAsync(false, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBaseUrl + "/gateway"); request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token); request.Headers.UserAgent.ParseAdd(UserAgent);
            try
            {
                using var response = await HttpClient.SendAsync(request, cancellationToken); var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { InvalidateAccessToken(); continue; }
                if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, raw, "获取 QQ Gateway 失败。", gateway: true, responseTraceId: ReadTraceHeader(response), endpointCategory: "gateway");
                using var document = JsonDocument.Parse(raw); var url = ReadString(document.RootElement, "url")?.Trim();
                if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeWss || string.IsNullOrWhiteSpace(parsed.Host)) throw new QQNotificationException("gateway_response_invalid", "QQ Gateway 地址无效。", null);
                return url;
            }
            catch (QQNotificationException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { throw new QQNotificationException("network_timeout", "获取 QQ Gateway 请求超时。", null, NotificationFailureKind.Transient, endpointCategory: "gateway"); }
            catch (HttpRequestException exception) { throw new QQNotificationException("network_error", "无法获取 QQ Gateway。", exception, NotificationFailureKind.Transient, endpointCategory: "gateway"); }
        }
        throw new QQNotificationException("token_expired", "QQ Access Token 已失效。", null, NotificationFailureKind.Authentication, endpointCategory: "token");
    }

    private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!forceRefresh && !string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _accessTokenExpiresAt.AddMinutes(-TokenRefreshMarginMinutes)) return _accessToken;
        }
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            lock (_sync)
            {
                if (!forceRefresh && !string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _accessTokenExpiresAt.AddMinutes(-TokenRefreshMarginMinutes)) return _accessToken;
            }
            string appId, secret; lock (_sync) { appId = _settings.AppId; secret = _secret; }
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret)) throw new QQNotificationException("credentials_missing", "缺少 QQ AppID 或 AppSecret。", null, NotificationFailureKind.Authentication, endpointCategory: "token");
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new StringContent(JsonSerializer.Serialize(new { appId, clientSecret = secret }), Encoding.UTF8, "application/json") }; request.Headers.UserAgent.ParseAdd(UserAgent);
            try
            {
                using var response = await HttpClient.SendAsync(request, cancellationToken); var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw CreateApiException(response.StatusCode, raw, "QQ 凭据认证失败。", responseTraceId: ReadTraceHeader(response), endpointCategory: "token");
                }
                using var document = JsonDocument.Parse(raw); var token = ReadString(document.RootElement, "access_token")?.Trim();
                var expiresIn = ReadLong(document.RootElement, "expires_in");
                if (string.IsNullOrWhiteSpace(token) || expiresIn <= 0) throw new QQNotificationException("protocol_incompatible", "QQ Access Token 响应格式无效。", null);
                var expiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
                lock (_sync) { _accessToken = token; _accessTokenExpiresAt = expiry; }
                PublishStatus(Status with { AccessTokenExpiresAt = expiry });
                return token;
            }
            catch (QQNotificationException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { throw new QQNotificationException("network_timeout", "QQ Token 请求超时。", null, NotificationFailureKind.Transient, endpointCategory: "token"); }
            catch (HttpRequestException exception) { throw new QQNotificationException("network_error", "无法连接 QQ Token 服务。", exception, NotificationFailureKind.Transient, endpointCategory: "token"); }
        }
        finally { _tokenGate.Release(); }
    }

    private void ConfigureSocket(ClientWebSocket socket)
    {
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        lock (_sync) socket.Options.Proxy = _proxy;
    }

    private async Task SendPayloadAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _socketWriteGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        catch (WebSocketException exception) { throw new QQNotificationException("gateway_closed", "QQ Gateway 写入失败。", exception); }
        finally { _socketWriteGate.Release(); }
    }

    private static async Task<JsonDocument> ReceivePayloadAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024]; using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) throw GatewayCloseError(result.CloseStatus, result.CloseStatusDescription);
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 256 * 1024) throw new QQNotificationException("protocol_incompatible", "QQ Gateway 消息过大。", null);
            if (result.EndOfMessage) break;
        }
        try { return JsonDocument.Parse(stream.ToArray()); } catch (JsonException exception) { throw new QQNotificationException("protocol_incompatible", "QQ Gateway 消息格式无效。", exception); }
    }

    private void UpdateSequence(JsonElement payload)
    {
        if (payload.TryGetProperty("s", out var sequence) && sequence.ValueKind == JsonValueKind.Number && sequence.TryGetInt64(out var value)) lock (_sync) _sequence = value;
    }

    private void SetConnectionFailure(QQNotificationException exception, bool reconnectEnabled)
    {
        var state = exception.FailureKind == NotificationFailureKind.Authentication
            || exception.Code is "credentials_missing" or "secret_invalid" or "auth_failed"
            ? NotificationConnectionState.AuthenticationFailed
            : reconnectEnabled ? NotificationConnectionState.Reconnecting : NotificationConnectionState.GatewayFailed;
        var safe = SafeError(exception.Message);
        LogError($"connection/{exception.Code}", safe);
        PublishStatus(Status with { Running = reconnectEnabled, Connected = false, ConnectionState = state, LastError = safe });
    }

    private void SetLastError(string message)
    {
        var safe = SafeError(message);
        LogError("operation", safe);
        PublishStatus(Status with { LastError = safe });
    }
    private void InvalidateAccessToken() { lock (_sync) { _accessToken = string.Empty; _accessTokenExpiresAt = default; } PublishStatus(Status with { AccessTokenExpiresAt = null }); }
    private void ClearSession() { lock (_sync) { _sessionId = string.Empty; _sequence = 0; } }

    private HttpClient HttpClient { get { lock (_sync) return _httpClient ?? throw new QQNotificationException("not_configured", "QQ 通知通道尚未配置。", null); } }

    private static HttpClient BuildHttpClient(string mode, IWebProxy? proxy, HttpMessageHandler? messageHandler)
    {
        if (messageHandler is not null) return new HttpClient(messageHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds) };
        var handler = new HttpClientHandler();
        if (mode == "direct") handler.UseProxy = false;
        else if (mode == "custom-http") { handler.UseProxy = true; handler.Proxy = proxy; }
        else handler.UseProxy = true;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds) };
    }

    private static IWebProxy? BuildProxy(string mode, string url)
    {
        if (mode == "direct") return null;
        if (mode == "custom-http") return new WebProxy(new Uri(url));
        return WebRequest.DefaultWebProxy;
    }

    private static QqNotificationSettings NormalizeSettings(QqNotificationSettings settings)
    {
        var appId = (settings.AppId ?? string.Empty).Trim();
        if (settings.Enabled && (appId.Length < 5 || appId.Length > 32 || appId.Any(value => value is < '0' or > '9'))) throw new ArgumentException("QQ AppID 必须是 5 到 32 位数字。", nameof(settings));
        var mode = (settings.ProxyMode ?? string.Empty).Trim().ToLowerInvariant() switch { "direct" => "direct", "custom-http" => "custom-http", _ => "environment" };
        var proxyUrl = (settings.ProxyUrl ?? string.Empty).Trim();
        if (mode == "custom-http")
        {
            if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp || string.IsNullOrWhiteSpace(parsed.Host) || parsed.UserInfo.Length > 0 || (parsed.AbsolutePath is not "" and not "/") || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)) throw new ArgumentException("QQ 自定义代理必须是没有账号、路径、查询和片段的 HTTP 地址。", nameof(settings));
        }
        else proxyUrl = string.Empty;
        return settings with { AppId = appId, ProxyMode = mode, ProxyUrl = proxyUrl };
    }

    private void PublishStatus(NotificationChannelStatus status)
    {
        EventHandler<NotificationChannelStatus>? handler;
        lock (_sync) { _status = status; handler = StatusChanged; }
        handler?.Invoke(this, status);
    }

    private void LogError(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(_logsDirectory)) return;
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            File.AppendAllText(Path.Combine(_logsDirectory, "qq-notification.log"), $"[{DateTimeOffset.UtcNow:O}] {category}: {SafeError(message)}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Logging must never turn a recoverable QQ failure into an application failure.
        }
    }

    private static NotificationSendResult Failed(string error, int sent, int total, NotificationFailureKind failureKind) => new(false, sent, total, error, FailureKind: failureKind);
    private static string? ReadString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var textNumber) ? textNumber : 0;
    }
    private static int ReadInt(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    private static QQNotificationException CreateApiException(
        HttpStatusCode status,
        string raw,
        string fallback,
        bool gateway = false,
        string? responseTraceId = null,
        string? endpointCategory = null)
    {
        var apiError = QQNotificationApiErrorParser.Parse(raw);
        var traceId = string.IsNullOrWhiteSpace(apiError.TraceId) ? responseTraceId : apiError.TraceId;
        var failureKind = QQNotificationFailureClassifier.Classify(status, apiError.ErrorCode, endpointCategory);
        var code = gateway
            ? status switch { HttpStatusCode.Unauthorized => "gateway_auth_failed", HttpStatusCode.Forbidden => "gateway_permission_denied", HttpStatusCode.NotFound => "gateway_endpoint_not_found", (HttpStatusCode)429 => "rate_limited", _ => "qq_api_error" }
            : failureKind switch
            {
                NotificationFailureKind.Authentication => endpointCategory == "token" ? "secret_invalid" : "auth_failed",
                NotificationFailureKind.PermanentTarget => "permanent_target",
                NotificationFailureKind.InvalidRequest => "qq_invalid_request",
                NotificationFailureKind.Transient when status == (HttpStatusCode)429 => "rate_limited",
                _ => "qq_api_error"
            };
        var detail = string.IsNullOrWhiteSpace(apiError.Message) ? fallback : SafeError(apiError.Message);
        var message = BuildApiErrorMessage(failureKind, detail, status, apiError.ErrorCode, traceId, gateway);
        return new QQNotificationException(code, message, null, failureKind, (int)status, apiError.ErrorCode,
            SafeError(traceId ?? string.Empty), endpointCategory);
    }

    private static string BuildApiErrorMessage(
        NotificationFailureKind failureKind,
        string detail,
        HttpStatusCode status,
        int? qqErrorCode,
        string? traceId,
        bool gateway)
    {
        var prefix = gateway
            ? "QQ Gateway 请求失败。"
            : failureKind switch
            {
                NotificationFailureKind.PermanentTarget => "QQ API 未找到当前接收目标。可能原因包括 OpenID 已失效、当前 Bot 与目标关系发生变化，或目标与当前 AppID 不匹配。",
                NotificationFailureKind.Authentication => "QQ API 认证或权限检查失败。",
                NotificationFailureKind.InvalidRequest => "QQ API 拒绝了这次请求，请根据错误码检查接收目标、Bot 关系、权限和请求参数。",
                NotificationFailureKind.Transient => "QQ API 暂时不可用或请求受限。",
                _ => "QQ API 请求失败。"
            };
        var code = qqErrorCode is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "未知";
        var trace = string.IsNullOrWhiteSpace(traceId) ? "无" : SafeError(traceId);
        return $"{prefix} QQ API 返回：{detail}；HTTP 状态：{(int)status}；QQ 错误码：{code}；Trace ID：{trace}";
    }

    private string FormatSendFailureLog(NotificationRequest request, QQNotificationException exception) =>
        $"endpoint={exception.EndpointCategory ?? QQNotificationEndpoint.CategoryFor(request.TargetType)}; method=POST; pathTemplate={FailurePathTemplate(request, exception)}; appIdLength={CurrentAppIdLength()}; appIdSha256={CurrentAppIdFingerprint()}; targetLength={request.TargetId.Length}; targetSha256={Fingerprint(request.TargetId)}; httpStatus={exception.HttpStatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; qqCode={exception.QqErrorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; traceId={SafeError(exception.TraceId ?? string.Empty)}; failureKind={exception.FailureKind}; code={exception.Code}; message={SafeError(exception.Message, request.TargetId)}";

    private string FormatSendSuccessLog(NotificationRequest request, int totalParts, int sentParts) =>
        $"endpoint={QQNotificationEndpoint.CategoryFor(request.TargetType)}; method=POST; pathTemplate={QQNotificationEndpoint.MessagePathTemplate(request.TargetType)}; appIdLength={CurrentAppIdLength()}; appIdSha256={CurrentAppIdFingerprint()}; targetLength={request.TargetId.Length}; targetSha256={Fingerprint(request.TargetId)}; payload=msg_type=0,content; sentParts={sentParts}; totalParts={totalParts}";

    private int CurrentAppIdLength()
    {
        lock (_sync) return _settings.AppId.Length;
    }

    private string CurrentAppIdFingerprint()
    {
        lock (_sync) return Fingerprint(_settings.AppId);
    }

    private static string FailurePathTemplate(NotificationRequest request, QQNotificationException exception) => exception.EndpointCategory switch
    {
        "token" => "/app/getAppAccessToken",
        "gateway" => "/gateway",
        _ => QQNotificationEndpoint.MessagePathTemplate(request.TargetType)
    };

    private static string ReadTraceHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Tps-trace-ID", out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    private static QQNotificationException GatewayCloseError(WebSocketCloseStatus? status, string? description)
    {
        var code = status switch { (WebSocketCloseStatus)4004 => "token_expired", (WebSocketCloseStatus)4006 or (WebSocketCloseStatus)4007 or (WebSocketCloseStatus)4009 => "gateway_session_invalid", (WebSocketCloseStatus)4008 => "rate_limited", _ => "gateway_closed" };
        return new QQNotificationException(code, string.IsNullOrWhiteSpace(description) ? "QQ Gateway 连接已关闭。" : SafeError(description), null);
    }

    private static string SafeError(string value, string? targetId = null)
    {
        var sanitized = new string((value ?? string.Empty).Trim().Select(character => character is '\r' or '\n' or '\t' || character < 0x20 ? ' ' : character).ToArray());
        sanitized = DiagnosticsRedaction.RedactText(sanitized);
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            var rawTarget = targetId.Trim();
            sanitized = sanitized.Replace(rawTarget, DiagnosticsRedaction.MaskOpenId(rawTarget), StringComparison.Ordinal);
        }
        return sanitized.Length > 500 ? sanitized[..500] : sanitized;
    }
}

public sealed class QQNotificationException(
    string code,
    string message,
    Exception? innerException,
    NotificationFailureKind failureKind = NotificationFailureKind.Unknown,
    int? httpStatusCode = null,
    int? qqErrorCode = null,
    string? traceId = null,
    string? endpointCategory = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public NotificationFailureKind FailureKind { get; } = failureKind;
    public int? HttpStatusCode { get; } = httpStatusCode;
    public int? QqErrorCode { get; } = qqErrorCode;
    public string? TraceId { get; } = traceId;
    public string? EndpointCategory { get; } = endpointCategory;
}

public static class QQNotificationEndpoint
{
    public static string CategoryFor(NotificationTargetType targetType) => targetType switch
    {
        NotificationTargetType.Private => "private-user",
        NotificationTargetType.Group => "group",
        _ => "unknown"
    };

    public static string BuildMessagePath(NotificationTargetType targetType, string targetId) => targetType switch
    {
        NotificationTargetType.Private => $"/v2/users/{Uri.EscapeDataString(targetId)}/messages",
        NotificationTargetType.Group => $"/v2/groups/{Uri.EscapeDataString(targetId)}/messages",
        _ => throw new ArgumentOutOfRangeException(nameof(targetType), targetType, "QQ 通知目标类型无效。")
    };

    public static string MessagePathTemplate(NotificationTargetType targetType) => targetType switch
    {
        NotificationTargetType.Private => "/v2/users/{openid}/messages",
        NotificationTargetType.Group => "/v2/groups/{group_openid}/messages",
        _ => "unknown"
    };
}

public sealed record QQNotificationApiError(int? ErrorCode, string? Message, string? TraceId);

public static class QQNotificationApiErrorParser
{
    public static QQNotificationApiError Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(null, null, null);
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var code = ReadInt(root, "err_code") ?? ReadInt(root, "code") ?? ReadInt(root, "error_code");
            var message = ReadString(root, "message") ?? ReadString(root, "error");
            var trace = ReadString(root, "trace_id") ?? ReadString(root, "traceId") ?? ReadString(root, "request_id");
            return new(code, message, trace);
        }
        catch (JsonException)
        {
            return new(null, null, null);
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var textNumber)
            ? textNumber
            : null;
    }
}

public static class QQNotificationFailureClassifier
{
    public static NotificationFailureKind Classify(HttpStatusCode status, int? qqErrorCode, string? endpointCategory)
    {
        if (IsPermanentTarget(qqErrorCode, endpointCategory)) return NotificationFailureKind.PermanentTarget;
        if (IsAuthentication(status, qqErrorCode, endpointCategory)) return NotificationFailureKind.Authentication;
        if (status == (HttpStatusCode)429 || status == HttpStatusCode.RequestTimeout || (int)status >= 500 || IsTransientQqCode(qqErrorCode)) return NotificationFailureKind.Transient;
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed || IsInvalidRequestQqCode(qqErrorCode)) return NotificationFailureKind.InvalidRequest;
        return NotificationFailureKind.Transient;
    }

    private static bool IsPermanentTarget(int? code, string? endpointCategory) =>
        (endpointCategory == "private-user" && code is 40054004 or 40054013)
        || (endpointCategory == "group" && code is 40034101 or 40054003);

    private static bool IsAuthentication(HttpStatusCode status, int? code, string? endpointCategory) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        || (endpointCategory == "token" && code is 100007 or 100016 or 10004)
        || code is 11251 or 11253 or 11254 or 11274 or 40034105;

    private static bool IsTransientQqCode(int? code) => code is 11252 or 11263 or 11281 or 11242 or 40054006 or 40054016 or 40034100 or 50055002;

    private static bool IsInvalidRequestQqCode(int? code) => code is 12002 or 50006 or 50035 or 22006 or 40054007 or 40054018;
}

public static class QQMessageSplitter
{
    public static IReadOnlyList<string> Split(string text, int limit)
    {
        text = text.Trim(); if (text.Length == 0) return [];
        limit = limit < 32 ? 5000 : limit;
        var plain = SplitRunes(text, limit); if (plain.Count == 1) return plain;
        var content = SplitRunes(text, limit - 16);
        return content.Select((part, index) => $"[{index + 1}/{content.Count}] {part}").ToArray();
    }

    private static List<string> SplitRunes(string text, int limit)
    {
        var runes = text.Trim().EnumerateRunes().ToArray(); var result = new List<string>();
        while (runes.Length > limit)
        {
            var cut = FindParagraphBoundary(runes, limit);
            if (cut <= limit / 2) cut = FindSeparator(runes, limit, new[] { '\n' });
            if (cut <= limit / 2) cut = FindSeparator(runes, limit, new[] { '。', '！', '？', '.', '!', '?' });
            if (cut <= limit / 2) cut = FindSeparator(runes, limit, new[] { ' ', '\t' });
            if (cut <= 0) cut = limit;
            result.Add(ToText(runes[..cut])); runes = runes[cut..];
        }
        if (runes.Length > 0) result.Add(ToText(runes));
        return result.Where(value => value.Length > 0).ToList();
    }

    private static int FindParagraphBoundary(System.Text.Rune[] runes, int limit)
    {
        for (var index = limit; index > 1; index--) if (runes[index - 2].Value == '\n' && runes[index - 1].Value == '\n') return index;
        return 0;
    }

    private static int FindSeparator(System.Text.Rune[] runes, int limit, IReadOnlyCollection<char> separators)
    {
        for (var index = limit; index > limit / 2; index--) if (separators.Contains((char)runes[index - 1].Value)) return index;
        return 0;
    }

    private static string ToText(IEnumerable<System.Text.Rune> runes)
    {
        var builder = new StringBuilder(); foreach (var rune in runes) builder.Append(rune.ToString()); return builder.ToString().Trim();
    }
}
