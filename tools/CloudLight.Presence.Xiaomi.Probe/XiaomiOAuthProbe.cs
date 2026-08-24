using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudLight.Presence.Xiaomi.Probe;

internal sealed class XiaomiOAuthProbe(HttpClient http, string region)
{
    private readonly string _uuid = Guid.NewGuid().ToString("N");
    private readonly string _webhookId = RandomNumberGenerator.GetInt32(int.MaxValue).ToString();

    public async Task<OAuthSession> AuthorizeAsync(CancellationToken cancellationToken)
    {
        var deviceId = $"ha.{_uuid}";
        var state = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"d={deviceId}")))
            .ToLowerInvariant();
        var redirectUri = $"http://homeassistant.local:8123/api/webhook/{_webhookId}";
        var authorizationUri = BuildAuthorizationUri(deviceId, state, redirectUri);

        await using var callback = new LoopbackCallbackReceiver(8123);
        callback.Start();

        Console.WriteLine("An isolated Microsoft Edge window will open Xiaomi's authorization page.");
        Console.WriteLine("Its process-only resolver maps homeassistant.local to 127.0.0.1.");
        Console.WriteLine("Fallback 1: replace only homeassistant.local with 127.0.0.1 in the final URL.");
        Console.WriteLine("Fallback 2: paste the complete final callback URL here and press Enter.");
        Console.WriteLine();

        await using var browser = OAuthBrowserSession.Start(authorizationUri);

        var callbackTask = callback.WaitForCallbackAsync(cancellationToken);
        using var pasteCancellation = new CancellationTokenSource();
        var pastedTask = StartPastedCallbackReader(pasteCancellation.Token);
        var completed = await Task.WhenAny(callbackTask, pastedTask);
        pasteCancellation.Cancel();
        var callbackUri = await completed;
        Console.WriteLine("OAuth callback input accepted; validating state.");
        var query = ParseQuery(callbackUri.Query);
        if (!query.TryGetValue("state", out var returnedState) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(returnedState)))
        {
            throw new InvalidOperationException("OAuth state validation failed.");
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            var error = query.GetValueOrDefault("error") ?? "authorization code missing";
            throw new InvalidOperationException($"Xiaomi authorization failed: {error}");
        }

        Console.WriteLine("OAuth state: valid; exchanging authorization code.");
        var token = await ExchangeCodeAsync(code, redirectUri, cancellationToken);
        return new OAuthSession(token, _uuid);
    }

    private static Task<Uri> StartPastedCallbackReader(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            while (true)
            {
                var input = Console.ReadLine();
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
                {
                    completion.TrySetResult(uri);
                    return;
                }

                Console.WriteLine("That was not a valid absolute callback URL. Try again.");
            }
        })
        {
            IsBackground = true,
            Name = "Xiaomi OAuth callback paste fallback"
        };
        thread.Start();
        return completion.Task;
    }

    private async Task<OAuthToken> ExchangeCodeAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            client_id = long.Parse(XiaomiEndpoints.OAuthClientId),
            redirect_uri = redirectUri,
            code,
            device_id = $"ha.{_uuid}"
        });
        var uri = new UriBuilder(Uri.UriSchemeHttps, XiaomiEndpoints.ApiHost(region))
        {
            Path = "/app/v2/ha/oauth/get_token",
            Query = $"data={Uri.EscapeDataString(data)}"
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Token endpoint returned HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.GetProperty("code").GetInt32() != 0 ||
            !root.TryGetProperty("result", out var result))
        {
            throw new InvalidOperationException("Xiaomi token endpoint rejected the authorization code.");
        }

        return new OAuthToken(
            result.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Access token missing."),
            result.GetProperty("refresh_token").GetString()
                ?? throw new InvalidOperationException("Refresh token missing."));
    }

    private static Uri BuildAuthorizationUri(string deviceId, string state, string redirectUri)
    {
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["redirect_uri"] = redirectUri,
            ["client_id"] = XiaomiEndpoints.OAuthClientId,
            ["response_type"] = "code",
            ["device_id"] = deviceId,
            ["state"] = state,
            ["skip_confirm"] = "false"
        }.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));

        return new Uri($"{XiaomiEndpoints.AuthorizationUrl}?{query}");
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.Ordinal);
}

internal sealed record OAuthToken(string AccessToken, string RefreshToken);
internal sealed record OAuthSession(OAuthToken Token, string ClientUuid);

internal sealed class OAuthBrowserSession : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly string? _profilePath;

    private OAuthBrowserSession(Process? process, string? profilePath)
    {
        _process = process;
        _profilePath = profilePath;
    }

    public static OAuthBrowserSession Start(Uri authorizationUri)
    {
        var edgePath = FindEdge();
        if (edgePath is null)
        {
            Process.Start(new ProcessStartInfo(authorizationUri.ToString()) { UseShellExecute = true });
            Console.WriteLine("Microsoft Edge was not found; using the default browser with manual fallbacks.");
            return new OAuthBrowserSession(null, null);
        }

        var profileRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CloudLightPresenceOAuth"));
        var profilePath = Path.GetFullPath(Path.Combine(profileRoot, Guid.NewGuid().ToString("N")));
        if (!profilePath.StartsWith(profileRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to create an OAuth browser profile outside the task temp directory.");
        }

        Directory.CreateDirectory(profilePath);
        var startInfo = new ProcessStartInfo(edgePath)
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"--user-data-dir={profilePath}");
        startInfo.ArgumentList.Add("--host-resolver-rules=MAP homeassistant.local 127.0.0.1");
        startInfo.ArgumentList.Add("--proxy-bypass-list=homeassistant.local;127.0.0.1;localhost");
        startInfo.ArgumentList.Add("--no-proxy-server");
        startInfo.ArgumentList.Add("--disable-features=HttpsUpgrades,msEdgeFirstRunExperience");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(authorizationUri.ToString());
        return new OAuthBrowserSession(Process.Start(startInfo), profilePath);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.CloseMainWindow();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await _process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _process.Kill(entireProcessTree: true);
                        await _process.WaitForExitAsync();
                    }
                }
            }
            finally
            {
                _process.Dispose();
            }
        }

        if (_profilePath is not null && Directory.Exists(_profilePath))
        {
            var profileRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CloudLightPresenceOAuth"));
            var resolvedProfile = Path.GetFullPath(_profilePath);
            if (!resolvedProfile.StartsWith(profileRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove an OAuth browser profile outside the task temp directory.");
            }

            for (var attempt = 0; attempt < 5 && Directory.Exists(resolvedProfile); attempt++)
            {
                try
                {
                    Directory.Delete(resolvedProfile, recursive: true);
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(250);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    await Task.Delay(250);
                }
            }

            if (Directory.Exists(resolvedProfile))
            {
                Console.Error.WriteLine("Warning: the isolated OAuth browser profile could not be removed immediately.");
            }
        }
    }

    private static string? FindEdge()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

internal sealed class LoopbackCallbackReceiver(int port) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private readonly TaskCompletionSource<Uri> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;

    public void Start()
    {
        _listener.Start();
        _loopCancellation = new CancellationTokenSource();
        _loopTask = AcceptLoopAsync(_loopCancellation.Token);
    }

    public Task<Uri> WaitForCallbackAsync(CancellationToken cancellationToken) =>
        _completion.Task.WaitAsync(cancellationToken);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                await HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken);
        var target = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ElementAtOrDefault(1);
        var callbackUri = Uri.TryCreate($"http://127.0.0.1:{port}{target}", UriKind.Absolute, out var uri)
            ? uri
            : null;
        var hasCode = callbackUri is not null && callbackUri.Query.Contains("code=", StringComparison.Ordinal);
        var hasError = callbackUri is not null && callbackUri.Query.Contains("error=", StringComparison.Ordinal);

        if (callbackUri is not null && (hasCode || hasError))
        {
            // Unblock token exchange before the browser finishes closing its HTTP connection.
            Console.WriteLine("OAuth callback received by the loopback listener.");
            _completion.TrySetResult(callbackUri);
        }

        var responseBody = hasCode
            ? "CloudLight Presence received the authorization response. You can close this tab."
            : "CloudLight Presence is waiting for a valid Xiaomi authorization response.";
        var responseBytes = Encoding.UTF8.GetBytes(responseBody);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(responseBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _loopCancellation?.Cancel();
        _listener.Stop();
        if (_loopTask is not null)
        {
            await _loopTask;
        }
        _loopCancellation?.Dispose();
    }
}
