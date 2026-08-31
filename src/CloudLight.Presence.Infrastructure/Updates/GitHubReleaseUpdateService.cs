using System.Net.Http.Headers;
using System.Text.Json;

namespace CloudLight.Presence.Infrastructure.Updates;

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, int Revision = 0) : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var parsed) ? parsed : throw new FormatException($"无效的语义版本：{value}");

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized[1..];
        normalized = normalized.Split('-', '+')[0];
        var parts = normalized.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
            if (!int.TryParse(parts[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[index]) || numbers[index] < 0)
                return false;
        version = new(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : Revision.CompareTo(other.Revision);
    }

    public override string ToString() => Revision == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}.{Revision}";
}

public sealed record GitHubReleaseInfo(
    string TagName,
    string Name,
    string HtmlUrl,
    SemanticVersion Version,
    DateTimeOffset? PublishedAt);

public sealed record AppUpdateCheckResult(
    SemanticVersion CurrentVersion,
    GitHubReleaseInfo? LatestRelease,
    DateTimeOffset CheckedAt,
    string? Error = null)
{
    public bool Succeeded => Error is null;
    public bool HasUpdate => LatestRelease is not null && LatestRelease.Version.CompareTo(CurrentVersion) > 0;
}

/// <summary>
/// Reads public GitHub Releases metadata only. It never downloads or replaces
/// an executable; the caller decides when to persist the check timestamp.
/// </summary>
public sealed class GitHubReleaseUpdateService : IDisposable
{
    public const string ReleasesEndpoint = "https://api.github.com/repos/Cloud-Light125/CloudLight-XiaoMi/releases";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemanticVersion _currentVersion;

    public GitHubReleaseUpdateService(HttpClient? httpClient = null, string? currentVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _currentVersion = SemanticVersion.TryParse(currentVersion, out var parsed)
            ? parsed
            : SemanticVersion.Parse(typeof(GitHubReleaseUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CloudLight-XiaoMi", _currentVersion.ToString()));
    }

    public SemanticVersion CurrentVersion => _currentVersion;
    public AppUpdateCheckResult? LastResult { get; private set; }

    public async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            using var response = await _httpClient.GetAsync(ReleasesEndpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failure = new AppUpdateCheckResult(_currentVersion, null, checkedAt, $"GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}");
                LastResult = failure;
                return failure;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var releases = new List<GitHubReleaseInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                if (item.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) continue;
                var tag = item.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
                if (!SemanticVersion.TryParse(tag, out var version)) continue;
                var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? tag! : tag!;
                var url = item.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;
                DateTimeOffset? published = null;
                if (item.TryGetProperty("published_at", out var publishedValue) && publishedValue.ValueKind is not JsonValueKind.Null)
                    if (publishedValue.TryGetDateTimeOffset(out var parsedPublished)) published = parsedPublished;
                releases.Add(new(tag!, name, url!, version, published));
            }

            var latest = releases.OrderByDescending(value => value.Version).FirstOrDefault();
            var success = new AppUpdateCheckResult(_currentVersion, latest, checkedAt);
            LastResult = success;
            return success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = new AppUpdateCheckResult(_currentVersion, null, checkedAt, exception.Message);
            LastResult = failure;
            return failure;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
