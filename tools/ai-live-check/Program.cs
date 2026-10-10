using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Switchcraft;

// Only synthetic fixtures are sent. Credentials stay in memory and are never logged.
var userDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchcraft");
if (args.Contains("--laya") && args.Contains("--corpus")) return await Corpus.Run(args, null);
var keys = new ApiKeyStore(userDirectory);
if (!keys.HasKey) { Console.WriteLine("No saved key."); return 2; }
if (args.Contains("--corpus")) return await Corpus.Run(args, keys);
var preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(userDirectory, "settings.json")))!;
var testDirectory = Path.Combine(Path.GetTempPath(), "Switchcraft-live-check-" + Guid.NewGuid().ToString("N"));
var store = new SettingsStore(testDirectory);
using var engine = new NativeEngine(store);
var handler = new InspectHandler();
using var api = new TypeSafeDetector(handler);
using var detection = new LanguageDetection(engine, store);
var results = new List<object>();
var fixtures = args.Contains("--horse")
    ? new[] { ("стоъть", "ru", "ой чий то кінь", ""), ("стоъть", "uk", "ой чий то кінь", ""), ("стоъть", "en", "ой чий то кінь", ""), ("стоїть", "uk", "ой чий то кінь", "") }
    : args.Contains("--context")
    ? new[] { ("Былий", "ru", "", ""), ("Былий", "ru", "", "і чорний"), ("Білий", "uk", "", "і чорний"), ("Белый", "ru", "", "и чёрный") }
    : args.Contains("--short")
    ? new[] { ("ы", "ru", "Червоний", ""), ("ы", "ru", "Червоний", "зелений"), ("ы", "ru", "Буква", ""), ("і", "uk", "Червоний", ""), ("и", "ru", "Красный", "") }
    : new[] { ("ghbdtn", "en", "", ""), ("ghbdsn", "en", "", ""), ("руддщ", "ru", "", ""), ("hello", "en", "", ""), ("косматій", "uk", "Когда я на почте служил ямщиком", "") };
foreach (var (word, source, context, following) in fixtures)
{
    var candidates = detection.Candidates(word, source, engine.Evaluate(word, source));
    var timer = Stopwatch.StartNew();
    handler.Decision = null;
    var decision = await api.DecideAsync(keys.Key, preferences.TypeSafeModel, word, context, candidates, CancellationToken.None, test:true, followingWords:following);
    results.Add(new { word, context, following, candidates, local=engine.Evaluate(word, source), elapsed_ms = timer.ElapsedMilliseconds, http_status=handler.Status, model_decision=handler.Decision, accepted_decision=decision, status=api.Status });
}
var json = JsonSerializer.Serialize(new { synthetic_only=true, model=preferences.TypeSafeModel, results }, new JsonSerializerOptions { WriteIndented=true });
File.WriteAllText(args.FirstOrDefault() ?? "ai-live-check.json", json);
Console.WriteLine(json);
return 0;

sealed class InspectHandler : DelegatingHandler
{
    public object? Decision;
    public int Status;
    public int Calls;
    public InspectHandler() : base(new HttpClientHandler { AllowAutoRedirect=false }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        var response = await base.SendAsync(request, token);
        Status=(int)response.StatusCode;
        if(response.IsSuccessStatusCode)
        {
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var decision=doc.RootElement.GetProperty("answers").GetProperty("decision");
            Decision=new { choice=decision.GetProperty("choice").GetString(), confidence=decision.GetProperty("confidence").GetDouble(), probabilities=decision.GetProperty("probabilities").Clone() };
        }
        return response;
    }
}
