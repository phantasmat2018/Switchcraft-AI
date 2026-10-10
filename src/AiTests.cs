using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchcraft;

internal static class AiTests
{
    internal sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls, DelayMs;
        public HttpStatusCode Code = HttpStatusCode.OK;
        public string Choice = "uk", LastBody = "", Authorization = "", Endpoint = "";
        public string? Raw;
        public double Confidence = .98, Probability = .98;
        public readonly ConcurrentDictionary<string, (string Choice, double Confidence, double Probability)> WordAnswers = new();
        public bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); Endpoint = request.RequestUri!.ToString(); Authorization = request.Headers.Authorization?.Scheme ?? "";
            LastBody = await request.Content!.ReadAsStringAsync(token);
            try { if (DelayMs > 0) await Task.Delay(DelayMs, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            using var doc = JsonDocument.Parse(LastBody);
            var state = doc.RootElement.GetProperty("state");
            var keys = state.GetProperty("candidates").EnumerateObject().Select(x => x.Name).ToArray();
            var answer = WordAnswers.TryGetValue(state.GetProperty("word").GetString() ?? "", out var configured)
                ? configured : (Choice, Confidence, Probability);
            var probabilities = keys.ToDictionary(x => x, x => x == answer.Choice ? answer.Probability : (1 - answer.Probability) / (keys.Length - 1));
            string json = Raw ?? JsonSerializer.Serialize(new { answers = new { decision = new { type = "choice", choice = answer.Choice, confidence = answer.Confidence, probabilities } } });
            return new HttpResponseMessage(Code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
    internal static async Task Run(Action<string, bool, string> check, string directory)
    {
        void Check(string name, bool value) => check("AI: " + name, value, "");
        var store = new SettingsStore(Path.Combine(directory, "ai"));
        using var engine = new NativeEngine(store);
        var handler = new FakeHandler();
        using var detection = new LanguageDetection(engine, store, new TypeSafeDetector(handler));
        const string fakeKey = "synthetic-test-key-not-a-credential";
        detection.Keys.Save(fakeKey);
        Check("key encrypted at rest", !File.ReadAllText(Path.Combine(store.DirectoryPath, "typesafe-key.dat")).Contains(fakeKey));
        Check("key reload through Windows protection", new ApiKeyStore(store.DirectoryPath).Key == fakeKey);
        var result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("local default sends zero requests", handler.Calls == 0 && result.Text == "привіт");
        store.Value.DetectionMode = "typesafe"; store.Save();
        Check("enabled mode persists", new SettingsStore(store.DirectoryPath).Value.DetectionMode == "typesafe");
        result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("AI candidate applies deterministic layout conversion", result.Text == "привіт" && result.Reason == "typesafe_ai");
        foreach (string source in new[] { "uk", "ru", "en" })
        {
            detection.Api.Reset(); handler.Choice = "uk";
            result = await detection.EvaluateAsync("стоъть", source, "uk", "ой чий то кінь", CancellationToken.None);
            using var horseRequest = JsonDocument.Parse(handler.LastBody);
            var candidates = horseRequest.RootElement.GetProperty("state").GetProperty("candidates");
            Check("dictionary repair offered with active " + source + " layout",
                candidates.TryGetProperty("uk", out var ukrainian) && ukrainian.GetString() == "стоїть");
            Check("AI accepts repair with active " + source + " layout", result.Text == "стоїть" && result.Reason == "typesafe_ai");
        }
        detection.Api.Reset(); handler.Choice = "uk";
        result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("native endpoint and bearer authentication", handler.Endpoint == "https://api.typesafe.ai/v1/systemone" && handler.Authorization == "Bearer");
        using (var body = JsonDocument.Parse(handler.LastBody))
        {
            Check("native choice request and explicit model", body.RootElement.GetProperty("questions").GetProperty("decision").GetProperty("type").GetString() == "choice" && body.RootElement.GetProperty("model").GetString() == "jev-latest");
        }
        int before = handler.Calls;
        await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("repeated decision is cached", handler.Calls == before);
        detection.Api.Reset(); handler.Choice = "ru";
        result = await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        Check("Russian candidate", result.Text == "привет" && result.Target == "ru");
        detection.Api.Reset(); handler.Choice = "en";
        result = await detection.EvaluateAsync("руддщ", "ru", "", "", CancellationToken.None);
        Check("English candidate", result.Text == "hello" && result.Target == "en");
        detection.Api.Reset(); handler.Choice = "keep";
        result = await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("AI keep vetoes local correction", result.Text == "ghbdsn" && result.Reason == "ai_keep");
        before = handler.Calls;
        await detection.EvaluateAsync("hello@example.com", "en", "", "", CancellationToken.None);
        await detection.EvaluateAsync("secret_123", "en", "", "", CancellationToken.None);
        Check("email and technical tokens not sent", before == handler.Calls);
        store.AddNever("ghbdsn"); engine.Reload();
        await detection.EvaluateAsync("ghbdsn", "en", "", "", CancellationToken.None);
        Check("user exception bypasses network", before == handler.Calls);
        store.Pairs["customword"] = "Київ"; store.UserPairs["customword"] = "Київ"; engine.Reload();
        result = await detection.EvaluateAsync("customword", "en", "", "", CancellationToken.None);
        Check("explicit user rule precedes AI", before == handler.Calls && result.Text == "Київ");
        store.Value.Sensitivity = "careful";
        await detection.EvaluateAsync("hello", "en", "", "", CancellationToken.None);
        Check("careful mode preserves known original without request", before == handler.Calls);
        store.Value.Sensitivity = "balanced"; detection.Api.Reset(); handler.Choice = "ru";
        await detection.EvaluateAsync("ghbdtn", "en", "", "один два три чотири п’ять шість сім hello@example.com", CancellationToken.None);
        using (var body = JsonDocument.Parse(handler.LastBody))
        {
            string context = body.RootElement.GetProperty("state").GetProperty("preceding_words").GetString()!;
            Check("context limited and technical tokens filtered", context.Split(' ').Length <= 6 && context.Length <= 180 && !context.Contains('@'));
        }
        store.Value.DetectionMode = "laya"; store.Save(); before = handler.Calls;
        await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        Check("disabled mode persists and sends zero requests", new SettingsStore(store.DirectoryPath).Value.DetectionMode == "laya" && before == handler.Calls);
        store.Value.DetectionMode = "typesafe";
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            detection.Api.Reset(); handler.Code = code;
            result = await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
            Check("HTTP " + (int)code + " falls back locally", result.Text == "привет" && result.Reason == "ai_fallback");
            before = handler.Calls;
            await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
            Check("HTTP " + (int)code + " backs off", before == handler.Calls);
        }
        handler.Code = HttpStatusCode.OK;
        foreach (string bad in new[] { "{}", "not-json", "{\"answers\":{\"decision\":{\"type\":\"choice\",\"choice\":\"injected\",\"confidence\":1}}}", "{\"answers\":{\"decision\":{\"type\":\"choice\",\"choice\":\"ru\",\"confidence\":0.99,\"probabilities\":{\"keep\":0.01,\"ru\":0.99,\"ru\":0.99}}}}" })
        {
            detection.Api.Reset(); handler.Raw = bad;
            result = await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
            Check("malformed or unknown answer rejected: " + bad.Length, result.Text == "привет" && result.Reason == "ai_fallback");
        }
        detection.Api.Reset(); handler.Raw = "{\"answers\":{\"decision\":{\"type\":\"choice\",\"choice\":\"ru\",\"confidence\":0.5,\"probabilities\":{\"keep\":0.5,\"ru\":0.5}}}}";
        result = await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        Check("low confidence uses verified local correction", result.Text == "привет" && result.Reason == "ai_uncertain");
        detection.Api.Reset(); handler.Raw = "{\"answers\":{\"decision\":{\"type\":\"choice\",\"choice\":\"keep\",\"confidence\":0.28,\"probabilities\":{\"keep\":0.62,\"ru\":0.38}}}}";
        result = await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        Check("uncertain keep does not veto the local dictionary", result.Text == "привет" && result.Reason == "ai_uncertain");
        detection.Api.Reset(); handler.Raw = null; handler.DelayMs = 5000;
        using (var cancel = new CancellationTokenSource(50))
        {
            bool cancelled = false;
            try { await detection.EvaluateAsync("ghbdtn", "en", "", "", cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check("cancellation propagates to HTTP and returns no correction", cancelled && handler.Cancelled);
        }
        detection.Api.Reset();
        result = await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        Check("timeout falls back without blocking input", result.Text == "привет" && result.Reason == "ai_fallback");
        handler.DelayMs = 0; detection.Api.Reset(); store.Value.Languages.Remove("ru"); engine.Reload(); handler.Choice = "ru";
        result = await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        Check("disabled language cannot be selected by AI", result.Reason != "typesafe_ai");
        detection.Keys.Clear();
        Check("key deletion persists", !new ApiKeyStore(store.DirectoryPath).HasKey);
        before = handler.Calls;
        await detection.EvaluateAsync("ghbdtn", "en", "", "", CancellationToken.None);
        Check("missing key uses local engine without network", before == handler.Calls);
        var rateHandler = new FakeHandler(); using var limited = new TypeSafeDetector(rateHandler);
        for (int i = 0; i < 65; i++) await limited.DecideAsync(fakeKey, "jev-latest", "word" + i, "", new() { ["keep"] = "word", ["uk"] = "слово" }, CancellationToken.None);
        Check("request rate has a hard upper bound", rateHandler.Calls == 60);
        await ShortWords(check, directory);
        await TrilingualPolicy(check, directory);
    }
    private static async Task TrilingualPolicy(Action<string, bool, string> check, string directory)
    {
        var store = new SettingsStore(Path.Combine(directory, "ai-trilingual")); store.Value.DetectionMode = "typesafe";
        using var engine = new NativeEngine(store);
        var handler = new FakeHandler();
        using var detection = new LanguageDetection(engine, store, new TypeSafeDetector(handler));
        detection.Keys.Save("synthetic-trilingual-key");
        void Check(string name, bool passed, string actual = "") => check("AI three languages: " + name, passed, actual);
        foreach (string word in new[] { "hello", "привет", "привіт", "Білий", "Белый", "мир", "сир", "сыр", "on", "такси" })
        {
            int calls = handler.Calls;
            var result = await detection.EvaluateAsync(word, "en", "", "hello Привіт привет", CancellationToken.None);
            Check("valid mixed-language word " + word, result.Text == word && calls == handler.Calls, result.Text);
        }
        foreach (var (word, expected) in new[] { ("[jxe", "хочу"), ("cfvs[", "самых"), ("erhf]ycmrbq", "український") })
        {
            Check("layout punctuation eligible " + word, LanguageDetection.Sendable(word));
            var candidates = detection.Candidates(word, "en", engine.Evaluate(word, "en"));
            Check("layout punctuation candidate " + word, candidates.Values.Contains(expected), string.Join(" | ", candidates.Values));
        }
        Check("canonical Unicode accepted", LanguageDetection.Sendable("косматій"));
        Check("punctuation alone is never sent", !LanguageDetection.Sendable("[]{}!?"));
        foreach (string acronym in new[] { "RGB", "RBC", "RTL", "VNC", "(RGB)", "API," })
        {
            int calls = handler.Calls;
            var result = await detection.EvaluateAsync(acronym, "en", "uk", "Use", CancellationToken.None, "color settings");
            Check("short Latin acronym preserved " + acronym, result.Text == acronym && calls == handler.Calls, result.Text);
        }
        Check("unknown technical candidates are not offered", detection.Candidates("qzxqzx", "en", engine.Evaluate("qzxqzx")).Count == 1);
        handler.Choice = "uk"; detection.Api.Reset();
        var shared = await detection.EvaluateAsync("cj,frf", "en", "", "", CancellationToken.None);
        Check("shared spelling does not force arbitrary layout", shared.Text == "собака" && shared.Target == "", shared.Text + "/" + shared.Target);
        using (var body = JsonDocument.Parse(handler.LastBody))
        {
            var state = body.RootElement.GetProperty("state");
            Check("shared spelling offered only once", state.GetProperty("candidates").EnumerateObject().Count(x => x.Value.GetString() == "собака") == 1);
            Check("both lexical languages sent", state.GetProperty("dictionary_languages").GetProperty("uk").GetArrayLength() == 2);
        }
        detection.Api.Reset();
        shared = await detection.EvaluateAsync("cj,frf", "ru", "", "", CancellationToken.None);
        Check("shared spelling retains matching current layout", shared.Text == "собака" && shared.Target == "ru", shared.Text + "/" + shared.Target);
        handler.Choice = "ru"; detection.Api.Reset();
        var contextual = await detection.EvaluateAsync("косматій", "uk", "ru", "Когда я на почте служил ямщиком", CancellationToken.None);
        Check("confident contextual ambiguity can be corrected", contextual.Text == "косматый" && contextual.Reason == "typesafe_ai", contextual.Text);
        handler.Confidence = .45; handler.Probability = .6; detection.Api.Reset();
        contextual = await detection.EvaluateAsync("косматій", "uk", "ru", "Когда я на почте служил ямщиком", CancellationToken.None);
        Check("uncertain AI preserves valid ambiguous original", contextual.Text == "косматій", contextual.Text);
        handler.Code = HttpStatusCode.ServiceUnavailable; detection.Api.Reset();
        contextual = await detection.EvaluateAsync("косматій", "uk", "ru", "Когда я на почте служил ямщиком", CancellationToken.None);
        Check("offline preserves valid ambiguous original", contextual.Text == "косматій", contextual.Text);
        handler.Code = HttpStatusCode.OK; detection.Api.Reset();
        var wrapped = await detection.EvaluateAsync("(руддщ)", "ru", "", "The greeting", CancellationToken.None, "appears here");
        Check("unique verified fallback preserves parentheses", wrapped.Text == "(hello)", wrapped.Text);
        handler.Choice = "ru"; handler.Confidence = .98; handler.Probability = .98; detection.Api.Reset();
        contextual = await detection.EvaluateAsync("косматій", "uk", "", "ко мне постучался", CancellationToken.None, "незнакомец");
        Check("nearby grammar allows AI to evaluate a valid inflection", contextual.Text == "косматый" && contextual.Reason == "typesafe_ai", contextual.Text);
        handler.Confidence = .45; handler.Probability = .6; detection.Api.Reset();
        contextual = await detection.EvaluateAsync("косматій", "uk", "", "ко мне постучался", CancellationToken.None, "незнакомец");
        Check("uncertain nearby grammar still preserves original", contextual.Text == "косматій", contextual.Text);
        handler.Confidence = .98; handler.Probability = .98; detection.Api.Reset();
        int callCount = handler.Calls;
        contextual = await detection.EvaluateAsync("косматій", "ru", "ru", "Вона стоїть у", CancellationToken.None, "шапці");
        Check("actual Ukrainian neighbors override stale Russian context", contextual.Text == "косматій" && callCount == handler.Calls, contextual.Text);
        store.Value.Languages.Remove("ru"); engine.Reload();
        Check("disabled language not offered", !detection.Candidates("ghbdtn", "en", engine.Evaluate("ghbdtn")).ContainsKey("ru"));
        int before = handler.Calls;
        contextual = await detection.EvaluateAsync("привет", "en", "", "", CancellationToken.None);
        Check("valid word in disabled language preserved", contextual.Text == "привет" && before == handler.Calls);
    }
    private static async Task ShortWords(Action<string, bool, string> check, string directory)
    {
        var store = new SettingsStore(Path.Combine(directory, "ai-short"));
        store.Value.DetectionMode = "typesafe";
        using var engine = new NativeEngine(store);
        var handler = new FakeHandler { Choice = "keep", Confidence = .45, Probability = .6 };
        using var detection = new LanguageDetection(engine, store, new TypeSafeDetector(handler));
        detection.Keys.Save("synthetic-short-word-key");
        async Task Check(string name, string word, string before, string after, string expected)
        {
            detection.Api.Reset();
            var result = await detection.EvaluateAsync(word, "ru", "", before, CancellationToken.None, after);
            check("AI short: " + name, result.Text == expected, result.Text + " / " + result.Reason);
        }
        await Check("Ukrainian preceding context", "ы", "Червоний", "", "і");
        await Check("Ukrainian following context", "ы", "", "зелений", "і");
        await Check("both sides of user's phrase", "ы", "Червоний", "зелений", "і");
        await Check("capitalization", "Ы", "Червоний", "зелений", "І");
        await Check("punctuation", "ы,", "Червоний", "зелений", "і,");
        await Check("no evidence", "ы", "", "", "ы");
        await Check("Russian context", "ы", "Красный", "зелёный", "ы");
        await Check("mixed-language context", "ы", "Червоний", "зелёный", "ы");
        await Check("letter discussion", "ы", "Українська буква", "", "ы");
        await Check("following letter discussion", "ы", "Червоний", "літера", "ы");
        await Check("quoted letter", "«ы»", "Червоний", "зелений", "«ы»");
        await Check("correct Ukrainian conjunction", "і", "Червоний", "зелений", "і");
        await Check("correct Russian conjunction", "и", "Красный", "зелёный", "и");
        await Check("whole-word dictionary repair", "Былий", "", "", "Білий");
        await Check("whole-word with two following words", "Былий", "", "і чорний", "Білий");
        await Check("whole-word capitalization", "БЫЛИЙ", "", "і чорний", "БІЛИЙ");
        await Check("valid Ukrainian adjective", "Білий", "", "і чорний", "Білий");
        await Check("valid Russian adjective", "Белый", "", "и чёрный", "Белый");
        await Check("valid Russian word is not spelling-corrected", "Былой", "", "і чорний", "Былой");
        detection.Api.Reset();
        await detection.EvaluateAsync("ы", "ru", "", "Червоний", CancellationToken.None, "зелений білий чорний четвертий secret_123");
        using (var body = JsonDocument.Parse(handler.LastBody))
            check("AI context: at most three following words", body.RootElement.GetProperty("state").GetProperty("following_words").GetString() == "зелений білий чорний", "");
        int calls = handler.Calls;
        await detection.EvaluateAsync("ы", "ru", "", "Червоний", CancellationToken.None, "синій");
        check("AI short: following context changes cache key", handler.Calls == calls + 1, "");
        detection.Api.Reset();
        await detection.EvaluateAsync("Былий", "ru", "", "", CancellationToken.None, new string('а', 80) + " " + new string('б', 80));
        using (var body = JsonDocument.Parse(handler.LastBody))
            check("AI context: following words limited to 160 characters", body.RootElement.GetProperty("state").GetProperty("following_words").GetString() == "", "");
        handler.Confidence = .98; handler.Probability = .98;
        await Check("confident keep veto", "ы", "Червоний", "зелений", "ы");
        handler.Confidence = .45; handler.Probability = .6;
        store.Value.Languages.Remove("uk"); engine.Reload();
        await Check("disabled Ukrainian", "ы", "Червоний", "зелений", "ы");
        store.Value.Languages.Add("uk"); engine.Reload();
        store.Value.Enabled = false;
        calls = handler.Calls;
        await Check("mode off", "ы", "Червоний", "зелений", "ы");
        check("AI short: mode off sends no request", calls == handler.Calls, "");
        store.Value.Enabled = true; store.Value.DetectionMode = "typesafe"; handler.Code = HttpStatusCode.ServiceUnavailable;
        await Check("offline contextual fallback", "ы", "Червоний", "зелений", "і");
        store.AddNever("ы"); engine.Reload(); calls = handler.Calls;
        await Check("user exception wins", "ы", "Червоний", "зелений", "ы");
        check("AI short: exception sends no request", calls == handler.Calls, "");
    }
}
