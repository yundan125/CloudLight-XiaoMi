namespace CloudLight.Presence.Xiaomi.Probe;

internal static class XiaomiEndpoints
{
    public const string OAuthClientId = "2882303761520251711";
    public const string AuthorizationUrl = "https://account.xiaomi.com/oauth2/authorize";

    public static readonly string[] SupportedRegions = ["cn", "de", "i2", "ru", "sg", "us"];

    public static string ApiHost(string region) =>
        region == "cn" ? "ha.api.io.mi.com" : $"{region}.ha.api.io.mi.com";
}

