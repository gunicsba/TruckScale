using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace TruckScale.Core;

/// <summary>
/// Looks up the latest release on GitHub at startup. Only the public release list is read; nothing is sent
/// about the computer or the data. Any failure (offline, timeout, rate limit) is silent.
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "gunicsba/TruckScale";
    public const string ReleasesUrl = $"https://github.com/{Repository}/releases";

    public sealed record Release(Version Version, string Tag, string Url);

    /// <summary>Version of the running program (set from the release tag when building a release).</summary>
    public static Version Current(Assembly? assembly = null)
    {
        var a = assembly ?? Assembly.GetEntryAssembly() ?? typeof(UpdateCheck).Assembly;
        var info = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Parse(info) ?? a.GetName().Version ?? new Version(0, 0);
    }

    /// <summary>"v1.2.0", "1.2", "1.2.0-dev.5+abc" → 1.2.0; null if it is not a version.</summary>
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static bool IsNewer(Version current, Version latest) =>
        new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build)) > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>Reads the GitHub "latest release" answer.</summary>
    public static Release? ParseLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var url = r.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        return Parse(tag) is { } v ? new Release(v, tag!, url ?? ReleasesUrl) : null;
    }

    /// <summary>The newer release, or null (up to date, offline or any error).</summary>
    public static async Task<Release?> NewerAsync(Version current, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TruckScale", current.ToString(3)));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            return ParseLatest(json) is { } latest && IsNewer(current, latest.Version) ? latest : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
