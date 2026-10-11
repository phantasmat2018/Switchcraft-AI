using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchcraft;

/// <summary>Checks for newer releases on GitHub and optionally opens the download page.</summary>
public sealed class UpdateChecker : IDisposable
{
    private const string Owner = "phantasmat2018";
    private const string Repo = "Switchcraft-AI";
    private static readonly Uri ReleasesApi = new($"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30");

    private readonly HttpClient http;

    public UpdateChecker() : this(new HttpClientHandler()) { }

    internal UpdateChecker(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Switchcraft-UpdateCheck/1.6");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Current application version from assembly metadata.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attr != null && Version.TryParse(attr.InformationalVersion.Split('+')[0], out var v)) return v;
            return asm.GetName().Version ?? new Version(1, 6, 0);
        }
    }

    /// <summary>Result of an update check. <paramref name="Error"/> is set when the check itself failed, so a failure
    /// is never reported as "up to date".</summary>
    public sealed record UpdateInfo(bool UpdateAvailable, Version? LatestVersion, string? ReleaseUrl, string? ReleaseNotes, string? Error = null);

    /// <summary>Queries the GitHub Releases API for the newest application release.</summary>
    public Task<UpdateInfo> CheckAsync(CancellationToken cancellation = default) => CheckAsync(CurrentVersion, cancellation);

    internal async Task<UpdateInfo> CheckAsync(Version current, CancellationToken cancellation)
    {
        static UpdateInfo Failed(string error) => new(false, null, null, null, error);
        try
        {
            using var response = await http.GetAsync(ReleasesApi, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Failed((int)response.StatusCode is 403 or 429
                    ? $"GitHub тимчасово обмежив кількість запитів (HTTP {(int)response.StatusCode}), спробуйте пізніше."
                    : $"GitHub відповів HTTP {(int)response.StatusCode}.");
            var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return Evaluate(json, current) ?? Failed("на GitHub не знайдено релізів програми.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return Failed("перевірку скасовано."); }
        catch (OperationCanceledException) { return Failed("GitHub не відповів вчасно."); }
        catch (HttpRequestException) { return Failed("немає з’єднання з GitHub."); }
        catch (JsonException) { return Failed("GitHub повернув відповідь у невідомому форматі."); }
        catch (Exception ex) { return Failed(ex.Message); }
    }

    /// <summary>Picks the highest stable application release. Model and asset tags (laya-r3), drafts and pre-releases
    /// are skipped, and a hotfix published after a newer version does not hide that newer version.</summary>
    internal static UpdateInfo? Evaluate(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a release list.");
        static bool Flag(JsonElement release, string name) => release.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        UpdateInfo? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (Flag(release, "draft") || Flag(release, "prerelease")) continue;
            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || best != null && Normalize(best.LatestVersion!) >= Normalize(version)) continue;
            var url = release.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            var body = release.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
            best = new(Normalize(version) > Normalize(current), version, url ?? $"https://github.com/{Owner}/{Repo}/releases/latest", body);
        }
        return best;
    }

    // 1.6.2 and 1.6.2.0 are the same version.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Opens the release page in the default browser.</summary>
    public static void OpenReleasePage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* browser not available */ }
    }

    public void Dispose() => http.Dispose();
}
