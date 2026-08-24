using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CloudLight.Presence.RouterCloud.Probe;

internal static class MigateLoginBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Task<MigateSessionMaterial> LoginAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(new BridgeRequest("login", "xiaoqiang", null), cancellationToken);

    public static Task<MigateSessionMaterial> RefreshServiceAsync(
        XiaomiStoredSession stored,
        CancellationToken cancellationToken) =>
        RefreshServiceAsync(stored, "xiaoqiang", cancellationToken);

    public static Task<MigateSessionMaterial> RefreshServiceAsync(
        XiaomiStoredSession stored,
        string sid,
        CancellationToken cancellationToken) =>
        ExecuteAsync(new BridgeRequest(
            "service",
            sid,
            new BridgeAuthCookies(
                stored.AccountUserId ?? stored.UserId,
                stored.DeviceId,
                stored.PassToken)),
            cancellationToken);

    private static async Task<MigateSessionMaterial> ExecuteAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        var pythonPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight Presence", "migate-python", "Scripts", "python.exe");
        if (!File.Exists(pythonPath))
        {
            throw new ProbeException(
                ProbeErrorCategory.Unknown,
                "The isolated migate Python environment is missing. Run the Probe setup command first.");
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "migate_bridge.py");
        if (!File.Exists(scriptPath))
        {
            throw new ProbeException(
                ProbeErrorCategory.Unknown,
                "migate_bridge.py is missing from the Probe output directory.");
        }

        var pipeName = $"CloudLightPresence-Migate-{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var startInfo = new ProcessStartInfo(pythonPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add($@"\\.\pipe\{pipeName}");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the migate bridge process.");
        await pipe.WaitForConnectionAsync(cancellationToken);

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        var responseLine = await reader.ReadLineAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new ProbeException(
                ProbeErrorCategory.InvalidResponse,
                $"migate bridge exited without a result (exit={process.ExitCode}).");
        }

        var response = JsonSerializer.Deserialize<BridgeResponse>(responseLine, JsonOptions)
            ?? throw new JsonException("migate bridge response was empty.");
        if (!response.Ok || response.Result is null)
        {
            throw new ProbeException(
                ProbeErrorCategory.AuthenticationExpired,
                response.Error ?? "migate login/service acquisition failed.");
        }
        return response.Result;
    }

    private sealed record BridgeRequest(
        string Operation,
        string Sid,
        BridgeAuthCookies? AuthCookies);

    private sealed record BridgeAuthCookies(
        string UserId,
        string DeviceId,
        string PassToken);

    private sealed record BridgeResponse(
        bool Ok,
        MigateSessionMaterial? Result,
        string? Error);
}

internal sealed record MigateSessionMaterial(
    string AccountUserId,
    string UserId,
    string DeviceId,
    string PassToken,
    string ServiceToken,
    string Ssecurity,
    string? CUserId)
{
    public XiaomiRouterSession ToRouterSession() =>
        new(UserId, CUserId, PassToken, ServiceToken, Ssecurity);
}
