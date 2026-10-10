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
    private static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    private readonly HttpClient http;

    public UpdateChecker()
    {
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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

    /// <summary>Result of an update check.</summary>
    public sealed record UpdateInfo(bool UpdateAvailable, Version? LatestVersion, string? ReleaseUrl, string? ReleaseNotes);

    /// <summary>Queries the GitHub Releases API for the latest release.</summary>
    public async Task<UpdateInfo> CheckAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var response = await http.GetAsync(LatestReleaseApi, cancellation).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new UpdateInfo(false, null, null, $"HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var versionStr = tagName.TrimStart('v', 'V');
            if (!Version.TryParse(versionStr, out var latest))
                return new UpdateInfo(false, null, null, $"Не вдалося розпізнати версію: {tagName}");

            var htmlUrl = root.GetProperty("html_url").GetString() ?? $"https://github.com/{Owner}/{Repo}/releases/latest";
            var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;

            var current = CurrentVersion;
            var updateAvailable = latest > current;

            return new UpdateInfo(updateAvailable, latest, htmlUrl, body);
        }
        catch (TaskCanceledException)
        {
            return new UpdateInfo(false, null, null, "Перевірку скасовано.");
        }
        catch (HttpRequestException ex)
        {
            return new UpdateInfo(false, null, null, $"Помилка мережі: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new UpdateInfo(false, null, null, $"Помилка: {ex.Message}");
        }
    }

    /// <summary>Opens the release page in the default browser.</summary>
    public static void OpenReleasePage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* browser not available */ }
    }

    public void Dispose() => http.Dispose();
}
