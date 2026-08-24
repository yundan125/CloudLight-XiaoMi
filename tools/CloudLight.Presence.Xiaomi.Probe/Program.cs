using CloudLight.Presence.Xiaomi.Probe;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var region = ArgumentReader.GetValue(args, "--region") ?? "cn";
if (!XiaomiEndpoints.SupportedRegions.Contains(region, StringComparer.Ordinal))
{
    Console.Error.WriteLine($"Unsupported region '{region}'. Use cn, de, i2, ru, sg, or us.");
    return 2;
}

Console.WriteLine("CloudLight Presence - Xiaomi Cloud Phase 1 probe");
Console.WriteLine("This tool opens Xiaomi's OAuth page and never asks for your password.");
Console.WriteLine("Tokens stay in process memory and are not written to disk.");
Console.WriteLine();

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));

try
{
    using var http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    var oauth = new XiaomiOAuthProbe(http, region);
    var session = await oauth.AuthorizeAsync(cancellation.Token);
    Console.WriteLine("OAuth callback: success");
    Console.WriteLine("OAuth token: acquired (not displayed or persisted)");

    var cloud = new XiaomiCloudProbe(http, region, session.Token.AccessToken);
    var result = await cloud.RunAsync(cancellation.Token);

    var mips = new XiaomiMipsProbe(
        region,
        session.ClientUuid,
        session.Token.AccessToken,
        result.Router.Did,
        result.RouterSpec.RouterServiceIid,
        result.RouterSpec.DeviceConnectEventIids,
        result.RouterSpec.DeviceDisconnectEventIids,
        result.RouterSpec.ConnectDeviceIdsPropertyIid ?? 20);
    await mips.ObserveEventsAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Probe timed out or was cancelled.");
    return 3;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Probe failed: {exception.Message}");
    return 1;
}

file static class ArgumentReader
{
    public static string? GetValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
