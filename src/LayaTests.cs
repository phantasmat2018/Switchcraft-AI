using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchcraft;

/// <summary>Offline policy/protocol tests: no model loading, server launch, or network access.</summary>
internal static class LayaTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Code = HttpStatusCode.OK;
        public string Choice = "uk", LastBody = "", Authorization = "", Endpoint = "";
        public string? Raw;
        public double Confidence = .98;
        public Dictionary<string, double>? Probabilities;
        public bool IgnoreCancellation, Cancelled;
        public TaskCompletionSource<bool> Entered = NewSignal();
        public TaskCompletionSource<bool>? Release;
        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Block(bool ignoreCancellation = false)
        { Entered = NewSignal(); Release = NewSignal(); IgnoreCancellation = ignoreCancellation; Cancelled = false; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            Endpoint = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            LastBody = await request.Content!.ReadAsStringAsync(cancellation);
            Entered.TrySetResult(true);
            if (Release != null)
            {
                try
                {
                    if (IgnoreCancellation) await Release.Task;
                    else await Release.Task.WaitAsync(cancellation);
                }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            using var body = JsonDocument.Parse(LastBody);
            string question = body.RootElement.GetProperty("questions").EnumerateObject().Single().Name;
            var probabilities = Probabilities ?? new[] { "ru", "en", "uk" }
                .ToDictionary(language => language, language => language == Choice ? .98 : .01);
            string json = Raw?.Replace("$QUESTION", question, StringComparison.Ordinal)
                ?? JsonSerializer.Serialize(new
                {
                    answers = new Dictionary<string, object>
                    {
                        [question] = new { type = "choice", choice = Choice, confidence = Confidence, probabilities }
                    }
                });
            return new HttpResponseMessage(Code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    internal static async Task Run(NativeEngine parentEngine, SettingsStore parentStore, Action<string, bool, string> check)
    {
        // This child store keeps the caller's language/mode/rule fixtures unchanged.
        var store = new SettingsStore(Path.Combine(parentStore.DirectoryPath, "laya-tests"));
        using var engine = new NativeEngine(store);
        var localHandler = new FakeHandler();
        var cloudHandler = new AiTests.FakeHandler();
        using var detection = new LanguageDetection(engine, store, new TypeSafeDetector(cloudHandler), new LayaDetector(localHandler));
        void Check(string name, bool passed, string actual = "") => check("Laya: " + name, passed, actual);
        store.Value.LayaModelPath = Path.Combine(store.DirectoryPath, "synthetic-model-not-on-disk");
        detection.Keys.Save("synthetic-cloud-decoy-not-a-real-key");

        // Switchcraft AI has no dictionary-only mode: turning autocorrection off is the way to stop both clients.
        store.Value.Enabled = false;
        var result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("disabled autocorrection uses no client", result.Text == "привіт" && localHandler.Calls == 0 && cloudHandler.Calls == 0);
        store.Value.Enabled = true;
        store.Value.DetectionMode = "laya";
        store.Save();
        Check("local model mode persists", new SettingsStore(store.DirectoryPath).Value.DetectionMode == "laya");
        Check("local model mode is distinct from cloud mode", detection.UsesAi && detection.UsesLocalModel && !detection.UsesCloud);
        result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("local model selects a deterministic Ukrainian candidate", result.Text == "привіт" && result.Reason == "laya_ai", result.Text + "/" + result.Reason);
        Check("local inference sends no cloud credential", cloudHandler.Calls == 0 && localHandler.Authorization.Length == 0);
        Check("inference endpoint stays on loopback", Uri.TryCreate(localHandler.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback, localHandler.Endpoint);

        foreach (string source in new[] { "en", "uk", "ru" })
        {
            detection.Reset(); localHandler.Choice = "uk";
            result = await detection.EvaluateAsync("стоъть", source, "uk", "ой чий то кінь", CancellationToken.None);
            Check("horse repair survives active layout " + source, result.Text == "стоїть" && result.Reason == "laya_ai", result.Text + "/" + result.Reason);
            using var body = JsonDocument.Parse(localHandler.LastBody);
            var questions = body.RootElement.GetProperty("questions");
            bool correctQuestion = questions.TryGetProperty("w5", out var question);
            Check("word question follows context position for " + source, correctQuestion);
            if (correctQuestion)
            {
                var criteria = question.GetProperty("criteria");
                Check("trained criteria contain current word for " + source,
                    criteria.GetProperty("uk").GetString()!.Contains("стоїть", StringComparison.Ordinal)
                    && !criteria.GetProperty("ru").GetString()!.Contains("Спасибо", StringComparison.Ordinal));
            }
        }

        detection.Reset(); localHandler.Choice = "ru";
        result = await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        Check("Russian candidate", result.Text == "привет" && result.Target == "ru", result.Text + "/" + result.Target);
        detection.Reset(); localHandler.Choice = "en";
        result = await detection.EvaluateAsync("руддщ", "ru", "", "Say", CancellationToken.None);
        Check("English candidate", result.Text == "hello" && result.Target == "en", result.Text + "/" + result.Target);
        detection.Reset();
        result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("reading equal to original maps to keep", result.Text == "ghbdsn" && !result.Changed, result.Text);

        int before = localHandler.Calls;
        foreach (var (word, preceding, following) in new[]
        {
            ("hello", "Вона сказала", "і усміхнулася"), ("привіт", "Она сказала", "и улыбнулась"),
            ("RGB", "Use", "color"), ("VNC", "Connect via", "today"),
            ("ghbdsn@example.com", "Напиши на", ""), ("https://ghbdtn.dev", "Відкрий", ""),
            ("api.token", "Read", ""), ("ы", "Буква", "есть в русском")
        })
        {
            result = await detection.EvaluateAsync(word, "en", "", preceding, CancellationToken.None, following);
            Check("protected or valid text remains literal: " + word, result.Text == word, result.Text);
        }
        Check("protected text bypasses model", before == localHandler.Calls);

        foreach (string bad in new[]
        {
            "{}", "not-json",
            "{\"answers\":{\"$QUESTION\":{\"type\":\"choice\",\"choice\":\"injected\",\"confidence\":1,\"probabilities\":{\"ru\":0,\"en\":0,\"uk\":1}}}}",
            "{\"answers\":{\"$QUESTION\":{\"type\":\"choice\",\"choice\":\"uk\",\"confidence\":1,\"probabilities\":{\"ru\":0,\"en\":0,\"uk\":1,\"uk\":1}}}}"
        })
        {
            detection.Reset(); localHandler.Raw = bad;
            result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
            Check("malformed local reply falls back safely: " + bad.Length, result.Text == "привіт" && result.Reason != "laya_ai", result.Text + "/" + result.Reason);
        }
        localHandler.Raw = null;
        foreach (var code in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.NotFound })
        {
            detection.Reset(); localHandler.Code = code;
            result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
            Check("local service failure has a dictionary fallback: " + (int)code, result.Text == "привіт" && result.Reason != "laya_ai", result.Text + "/" + result.Reason);
        }
        localHandler.Code = HttpStatusCode.OK;
        detection.Reset(); localHandler.Choice = "ru";
        localHandler.Probabilities = new() { ["ru"] = .55, ["en"] = .05, ["uk"] = .40 };
        result = await detection.EvaluateAsync("косматій", "uk", "ru", "ко мне постучался", CancellationToken.None, "незнакомец");
        Check("uncertain local model preserves a valid ambiguous original", result.Text == "косматій", result.Text);
        localHandler.Probabilities = null;

        detection.Reset(); store.Value.Languages.Remove("ru"); engine.Reload();
        result = await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        Check("disabled Russian is not restored by local model", result.Text == "ghbdtn" && !result.Changed, result.Text);
        store.Value.Languages.Add("ru"); engine.Reload();
        store.Value.Enabled = false; detection.Reset(); before = localHandler.Calls;
        result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("turning autocorrection off stops both clients", before == localHandler.Calls && cloudHandler.Calls == 0 && result.Text == "привіт");
        store.Value.Enabled = true; store.Value.DetectionMode = "laya"; detection.Reset(); localHandler.Choice = "uk"; localHandler.Block(ignoreCancellation: true);
        var modeChanged = detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        await localHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        store.Value.Enabled = false; detection.Reset(); localHandler.Release!.TrySetResult(true);
        result = await modeChanged;
        Check("in-flight reply is not applied after autocorrection is disabled", result.Reason != "laya_ai" && cloudHandler.Calls == 0, result.Reason);
        Check("no cloud requests across local-model failure paths", cloudHandler.Calls == 0);

        store.Value.Enabled = true; store.Value.DetectionMode = "laya"; localHandler.Release = null; localHandler.IgnoreCancellation = false;
        detection.Local.SetPreparing(true); before = localHandler.Calls;
        await detection.PrepareAsync();
        result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("environment preparation keeps dictionary corrections without neural traffic", result.Text == "привіт"
            && result.Reason != "laya_ai" && before == localHandler.Calls && cloudHandler.Calls == 0, result.Reason);
        detection.Reset(); detection.Local.Configure("cpu", 1);
        await detection.PrepareAsync();
        result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("reset and hardware settings cannot restart inference during preparation", result.Text == "привіт"
            && before == localHandler.Calls && cloudHandler.Calls == 0, result.Reason);
        detection.Local.SetPreparing(false);
        result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("ending preparation restores local inference", result.Text == "привіт" && result.Reason == "laya_ai"
            && localHandler.Calls == before + 1 && cloudHandler.Calls == 0, result.Reason);
        detection.Reset(); localHandler.Choice = "ru"; localHandler.Block(ignoreCancellation: true);
        var preparationChanged = detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        await localHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        detection.Local.SetPreparing(true); localHandler.Release!.TrySetResult(true);
        result = await preparationChanged;
        Check("starting preparation rejects an older in-flight model answer", result.Text == "привет"
            && result.Reason != "laya_ai" && cloudHandler.Calls == 0, result.Reason);
        detection.Local.SetPreparing(false);

        await ProtocolChecks(Check);
    }

    private static async Task ProtocolChecks(Action<string, bool, string> check)
    {
        void Check(string name, bool passed, string actual = "") => check(name, passed, actual);
        const string model = "synthetic-model-not-on-disk";
        var readings = new Dictionary<string, string> { ["ru"] = "собака", ["en"] = "cj,frf", ["uk"] = "собака" };
        var candidates = new Dictionary<string, string> { ["keep"] = "cj,frf", ["uk"] = "собака" };
        var handler = new FakeHandler { Choice = "ru", Confidence = .2, Probabilities = new() { ["ru"] = .48, ["en"] = .04, ["uk"] = .48 } };
        using var detector = new LayaDetector(handler);
        var answer = await detector.DecideAsync(model, "cj,frf", "", readings, candidates, CancellationToken.None, test: true);
        Check("identical RU/UK readings aggregate before confidence threshold", answer is { Choice: "uk", Certain: true } && answer.Confidence >= .95, answer?.ToString() ?? "null");

        detector.Reset(); handler.Choice = "ru"; handler.Confidence = .98; handler.Probabilities = null;
        readings = new() { ["ru"] = "привет", ["en"] = "ghbdtn", ["uk"] = "привєт" };
        candidates = new() { ["keep"] = "ghbdtn", ["uk"] = "привєт" };
        answer = await detector.DecideAsync(model, "ghbdtn", "", readings, candidates, CancellationToken.None, test: true);
        Check("model cannot return a reading excluded from eligible candidates", answer == null, answer?.ToString() ?? "null");

        detector.Reset(); candidates = new() { ["keep"] = "ghbdtn", ["ru"] = "привет" };
        handler.Raw = "{\"answers\":{\"$QUESTION\":{\"type\":\"choice\",\"choice\":\"ru\",\"confidence\":1,\"probabilities\":{\"ru\":1,\"en\":0,\"uk\":0}}},\"usage\":{\"truncated\":true}}";
        answer = await detector.DecideAsync(model, "ghbdtn", "", readings, candidates, CancellationToken.None, test: true);
        Check("truncated context cannot produce an accepted decision", answer == null || !answer.Certain, answer?.ToString() ?? "null");
        detector.Reset(); handler.Raw = null;

        handler.Block();
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = detector.DecideAsync(model, "ghbdtn", "", readings, candidates, cancellation.Token, test: true);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            bool cancelled = false;
            try { await pending; }
            catch (OperationCanceledException) { cancelled = true; }
            Check("caller cancellation cancels transport and yields no decision", cancelled && handler.Cancelled);
        }
        detector.Reset(); handler.Block(ignoreCancellation: true);
        var stale = detector.DecideAsync(model, "ghbdtn", "", readings, candidates, CancellationToken.None, test: true);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        detector.Reset(); handler.Release!.TrySetResult(true);
        answer = await stale;
        Check("reset rejects a response that arrives after settings change", answer == null, answer?.ToString() ?? "null");
    }
}
