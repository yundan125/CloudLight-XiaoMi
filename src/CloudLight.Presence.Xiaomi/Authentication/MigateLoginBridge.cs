using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CloudLight.Presence.Core.Interfaces;

namespace CloudLight.Presence.Xiaomi.Authentication;

internal sealed class MigateLoginBridge(string pythonPath)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public Task<MigateSessionMaterial> LoginAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(new BridgeRequest("login", "xiaomiio", null), cancellationToken);

    public Task<MigateSessionMaterial> RefreshAsync(XiaomiSession stored, CancellationToken cancellationToken) =>
        ExecuteAsync(new BridgeRequest("service", "xiaomiio", new BridgeCookies(
            stored.AccountUserId ?? stored.UserId, stored.DeviceId, stored.PassToken)), cancellationToken);

    private async Task<MigateSessionMaterial> ExecuteAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(pythonPath))
        {
            throw new InvalidOperationException(
                "缺少隔离的 migate 运行环境。请先按 Probe 文档安装 MiForge/migate 1.1.10。");
        }
        var script = Path.Combine(AppContext.BaseDirectory, "migate_bridge.py");
        if (!File.Exists(script)) throw new FileNotFoundException("migate_bridge.py 未复制到程序目录。", script);

        var pipeName = $"CloudLightPresence-Migate-{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(pythonPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };
        start.ArgumentList.Add(script); start.ArgumentList.Add($@"\\.\pipe\{pipeName}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 migate 登录桥。 ");
        await pipe.WaitForConnectionAsync(cancellationToken);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Options));
        var line = await reader.ReadLineAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line)) throw new AuthenticationRequiredException("Xiaomi 登录桥没有返回结果。");
        var response = JsonSerializer.Deserialize<BridgeResponse>(line, Options);
        if (response?.Ok != true || response.Result is null)
            throw new AuthenticationRequiredException(response?.Error ?? "Xiaomi 登录或服务会话获取失败。");
        return response.Result;
    }

    private sealed record BridgeRequest(string Operation, string Sid, BridgeCookies? AuthCookies);
    private sealed record BridgeCookies(string UserId, string DeviceId, string PassToken);
    private sealed record BridgeResponse(bool Ok, MigateSessionMaterial? Result, string? Error);
}
