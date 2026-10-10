using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Switchcraft;

internal static class Corpus
{
    internal sealed record Fixture(string Name, string Word, string Source, string Preceding, string Following, string Expected, string Category);
    internal static async Task<int> Run(string[] args, ApiKeyStore? savedKeys)
    {
        var path = args.SkipWhile(x => x != "--corpus").Skip(1).First();
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        string directory = Path.Combine(Path.GetTempPath(), "Switchcraft-corpus-" + Guid.NewGuid().ToString("N"));
        var results = new List<object>(); int failed = 0;
        try
        {
            bool laya = args.Contains("--laya");
            var store = new SettingsStore(directory); store.Value.DetectionMode = laya ? "laya" : "typesafe";
            string? modelPath = args.SkipWhile(x => x != "--laya-model").Skip(1).FirstOrDefault();
            if (laya && modelPath != null) store.Value.LayaModelPath = modelPath;
            string? cpuThreads = args.SkipWhile(x => x != "--laya-threads").Skip(1).FirstOrDefault();
            if (laya && int.TryParse(cpuThreads, out int requestedThreads))
            { store.Value.LayaDevice = "cpu"; store.Value.LayaCpuThreads = Math.Clamp(requestedThreads, 1, 8); }
            using var engine = new NativeEngine(store);
            var handler = new InspectHandler();
            using var detection = new LanguageDetection(engine, store, new TypeSafeDetector(handler));
            if (!laya) detection.Keys.Save(savedKeys!.Key);
            var warmupWatch = Stopwatch.StartNew();
            AiDecision? warmup = laya ? await detection.TestLocalAsync(CancellationToken.None) : null;
            long warmupMs = warmupWatch.ElapsedMilliseconds;
            int paceMs = int.TryParse(args.SkipWhile(x => x != "--pace-ms").Skip(1).FirstOrDefault(), out int pace) ? Math.Clamp(pace, 0, 5000) : 0;
            foreach (var fixture in fixtures)
            {
                if (paceMs > 0) await Task.Delay(paceMs);
                if (!laya) detection.Api.Reset();
                handler.Decision = null;
                int before = handler.Calls;
                var watch = Stopwatch.StartNew();
                string context = detection.ContextLanguage(fixture.Preceding);
                var result = await detection.EvaluateAsync(fixture.Word, fixture.Source, context, fixture.Preceding, CancellationToken.None, fixture.Following);
                bool passed = result.Text == fixture.Expected;
                if (!passed) failed++;
                results.Add(new { fixture, passed, actual = result.Text, result.Target, result.Reason, context, cloud_requests = handler.Calls - before, elapsed_ms = watch.ElapsedMilliseconds, decision = handler.Decision, provider_status = detection.Status });
            }
            var report = new { synthetic_only = true, provider = laya ? "laya" : "typesafe", model_ready = !laya || warmup != null, device = store.Value.LayaDevice, cpu_threads = store.Value.LayaCpuThreads, pace_ms = paceMs, warmup_ms = warmupMs, total = fixtures.Length, passed = fixtures.Length - failed, failed, requests = handler.Calls, results };
            File.WriteAllText(args[0], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { report.total, report.passed, report.failed, report.requests }));
            if (laya && warmup == null) return 2;
        }
        finally
        {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("Switchcraft-corpus-", StringComparison.Ordinal)
                && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        return failed == 0 ? 0 : 1;
    }
}
