using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Switchcraft;

internal static class UpdateCheckerTests
{
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond = () => new(HttpStatusCode.OK);
        public int DelayMs;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (DelayMs > 0) await Task.Delay(DelayMs, token);
            return Respond();
        }
    }

    private static string Release(string tag, bool prerelease = false, bool draft = false) =>
        $"{{\"tag_name\":\"{tag}\",\"prerelease\":{(prerelease ? "true" : "false")},\"draft\":{(draft ? "true" : "false")},\"html_url\":\"https://github.com/phantasmat2018/Switchcraft-AI/releases/tag/{tag}\",\"body\":\"notes\"}}";

    internal static async Task Run(Action<string, bool, string> check)
    {
        void Check(string name, bool value, string actual = "") => check("Update: " + name, value, actual);
        var github = new FakeGitHub();
        using var checker = new UpdateChecker(github, TimeSpan.FromMilliseconds(300));
        Task<UpdateChecker.UpdateInfo> Answer(string json, string current = "1.6.2")
        {
            github.Respond = () => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            return checker.CheckAsync(Version.Parse(current), CancellationToken.None);
        }
        string Show(UpdateChecker.UpdateInfo info) => $"available={info.UpdateAvailable} latest={info.LatestVersion} error={info.Error}";

        var info = await Answer($"[{Release("laya-r3", prerelease: true)},{Release("v1.6.1")}]");
        Check("published 1.6.1 is not offered to 1.6.2", !info.UpdateAvailable && info.LatestVersion == new Version(1, 6, 1) && info.Error == null, Show(info));
        info = await Answer($"[{Release("laya-r3", prerelease: true)},{Release("v1.6.2")}]", "1.6.0");
        Check("newer release is offered with its page", info.UpdateAvailable && info.LatestVersion == new Version(1, 6, 2)
            && info.ReleaseUrl == "https://github.com/phantasmat2018/Switchcraft-AI/releases/tag/v1.6.2", Show(info));
        info = await Answer($"[{Release("v1.6.3")},{Release("v1.7.0")},{Release("v1.6.2")}]");
        Check("a later hotfix does not hide a higher version", info.UpdateAvailable && info.LatestVersion == new Version(1, 7, 0), Show(info));
        info = await Answer($"[{Release("v1.9.0", prerelease: true)},{Release("v1.8.0", draft: true)},{Release("v1.6.1")}]");
        Check("pre-releases and drafts are not offered", !info.UpdateAvailable && info.LatestVersion == new Version(1, 6, 1), Show(info));
        info = await Answer($"[{Release("v1.6.2")}]", "1.6.2.0");
        Check("same version with a revision is up to date", !info.UpdateAvailable && info.Error == null, Show(info));
        info = await Answer($"[{Release("laya-r3", prerelease: true)}]");
        Check("no application release is an error, not up to date", !info.UpdateAvailable && info.Error != null, Show(info));
        info = await Answer("{\"message\":\"Not Found\"}");
        Check("unexpected response is an error", !info.UpdateAvailable && info.Error != null, Show(info));

        github.Respond = () => new(HttpStatusCode.Forbidden);
        info = await checker.CheckAsync(Version.Parse("1.6.0"), CancellationToken.None);
        Check("rate limit is an error", !info.UpdateAvailable && info.Error?.Contains("403") == true, Show(info));
        github.Respond = () => throw new HttpRequestException("offline");
        info = await checker.CheckAsync(Version.Parse("1.6.0"), CancellationToken.None);
        Check("offline is an error, not up to date", !info.UpdateAvailable && info.Error?.Contains("з’єднання") == true, Show(info));
        github.Respond = () => new(HttpStatusCode.OK); github.DelayMs = 5000;
        info = await checker.CheckAsync(Version.Parse("1.6.0"), CancellationToken.None);
        Check("timeout is an error", !info.UpdateAvailable && info.Error?.Contains("вчасно") == true, Show(info));
    }
}
