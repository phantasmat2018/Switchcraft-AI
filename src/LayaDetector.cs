using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchcraft;

/// <summary>Local word-choice inference. Only verified mechanical layout candidates can be returned.</summary>
public sealed class LayaDetector : IDisposable
{
    private static readonly string[] Languages = { "ru", "en", "uk" };
    private static readonly JsonSerializerOptions WordJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object gate = new();
    private readonly HttpClient client;
    private readonly LayaRuntime? runtime;
    private readonly SemaphoreSlim flight = new(1, 1);
    private readonly Dictionary<string, AiDecision> cache = new(StringComparer.Ordinal);
    private CancellationTokenSource lifetime = new();
    private int revision;
    private long nextRequest;
    private double averageLatency;
    private string model = "", status = "Локальну модель ще не завантажено";
    private string configuredDevice = "auto";
    private int configuredThreads;
    private bool disposed, preparing;
    public string Status => Volatile.Read(ref preparing) ? "Підготовка компонентів Laya"
        : runtime != null ? !runtime.Ready ? runtime.Status : Volatile.Read(ref status) + " · " + runtime.DeviceLabel : Volatile.Read(ref status);
    public double AverageLatencyMilliseconds => Volatile.Read(ref averageLatency);

    public LayaDetector(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        if (handler == null) runtime = new();
    }

    public void Configure(string device = "auto", int cpuThreads = 0)
    {
        string preference = NormalizeDevice(device);
        int threads = Math.Clamp(cpuThreads, 0, Math.Max(1, Environment.ProcessorCount));
        lock (gate)
        {
            if (disposed || configuredDevice == preference && configuredThreads == threads) return;
            ResetLocked(); configuredDevice = preference; configuredThreads = threads;
            runtime?.Configure(preference, threads);
        }
    }

    public void SetPreparing(bool value)
    {
        lock (gate)
        {
            if (disposed || preparing == value) return;
            preparing = value;
            ResetLocked();
        }
    }

    public void RetryLoading()
    {
        // Explicit user retry restores the startup budget after a previous RAM
        // shortage or crash, while retaining an already healthy loaded model.
        lock (gate)
            if (!disposed && !preparing && runtime != null && !runtime.Ready) ResetLocked();
    }

    private (int Revision, CancellationToken Token)? SelectModel(string modelPath)
    {
        lock (gate)
        {
            if (disposed || preparing) return null;
            if (!string.Equals(model, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                ResetLocked(); model = modelPath;
            }
            return (revision, lifetime.Token);
        }
    }

    public async Task PrepareAsync(string modelPath, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(modelPath));
            var selected = SelectModel(path);
            if (selected == null) return;
            var snapshot = selected.Value;
            if (runtime == null) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, snapshot.Token);
            var endpoint = await runtime.GetAsync(path, true, linked.Token).ConfigureAwait(false);
            if (endpoint != null) SetStatus(snapshot.Revision, "Laya · модель готова, обробка на цьому ПК");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { Volatile.Write(ref status, "Не вдалося завантажити Laya · перевірте шлях до моделі"); }
    }

    public async Task<AiDecision?> DecideAsync(string modelPath, string word, string context, Dictionary<string, string> readings,
        Dictionary<string, string> candidates, CancellationToken cancellation, string followingWords = "", bool test = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (word.Length is < 1 or > 80 || Languages.Any(x => !readings.TryGetValue(x, out string? value) || string.IsNullOrEmpty(value) || value.Length > 160)
            || !candidates.TryGetValue("keep", out string? original) || original != word) return null;
        string path;
        try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(modelPath)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
        var selected = SelectModel(path);
        if (selected == null) return null;
        var snapshot = selected.Value;
        if (!await flight.WaitAsync(0, cancellation).ConfigureAwait(false)) return null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, snapshot.Token);
            var endpoint = runtime == null ? new LayaRuntime.Endpoint(new Uri("http://127.0.0.1:1/"), "")
                : await runtime.GetAsync(path, test, linked.Token).ConfigureAwait(false);
            if (endpoint == null) return null;
            linked.Token.ThrowIfCancellationRequested();
            string[] preceding = ContextWords(context, 6, 180, last: true);
            string[] following = ContextWords(followingWords, 3, 160, last: false);
            int target = preceding.Length + 1;
            string questionId = "w" + target.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string cacheKey = JsonSerializer.Serialize(new { path, word, preceding, following, readings, candidates });
            lock (gate)
            {
                if (snapshot.Revision != revision || disposed) return null;
                if (!test && cache.TryGetValue(cacheKey, out var remembered)) return remembered;
                if (!test && runtime != null && Environment.TickCount64 < nextRequest) return null;
                nextRequest = Environment.TickCount64 + (long)Math.Clamp(AverageLatencyMilliseconds, 175, 1000);
            }
            string Line(string token) => string.Join(" ", preceding.Concat(new[] { token }).Concat(following));
            var state = new { typed = Line(word), ru = Line(readings["ru"]), en = Line(readings["en"]), uk = Line(readings["uk"]), target_word = target };
            var criteria = new Dictionary<string, string>
            {
                ["ru"] = "Russian: " + JsonSerializer.Serialize(readings["ru"], WordJson),
                ["en"] = "English: " + JsonSerializer.Serialize(readings["en"], WordJson),
                ["uk"] = "Ukrainian: " + JsonSerializer.Serialize(readings["uk"], WordJson)
            };
            var body = new
            {
                model = "switchcraft-local", state,
                questions = new Dictionary<string, object>
                {
                    [questionId] = new
                    {
                        type = "choice",
                        instructions = $"Word #{target} of the line: which decoding did the user mean? Judge it in the context of the whole line: pick the decoding that is a real word or name in its language and fits the sentence (an English term inside Ukrainian text stays English).",
                        criteria
                    }
                }
            };
            int budget = RequestTimeout(test, endpoint.Device, AverageLatencyMilliseconds);
            linked.CancelAfter(budget);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.Address, "v1/systemone"));
            if (endpoint.Token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var elapsed = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (runtime != null) Interlocked.Exchange(ref nextRequest, Environment.TickCount64 + 500);
                SetStatus(snapshot.Revision, "Laya тимчасово недоступна");
                return null;
            }
            using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 65_536) throw new InvalidDataException();
                buffer.Write(chunk, 0, count);
            }
            var answer = Parse(buffer.ToArray(), questionId, word, readings, candidates);
            cancellation.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (snapshot.Revision != revision || disposed || linked.IsCancellationRequested) return null;
                double previousLatency = AverageLatencyMilliseconds;
                Volatile.Write(ref averageLatency, previousLatency == 0 ? elapsed.Elapsed.TotalMilliseconds : previousLatency * .75 + elapsed.Elapsed.TotalMilliseconds * .25);
                if (answer != null && !test)
                {
                    if (cache.Count >= 64) cache.Clear();
                    cache[cacheKey] = answer;
                }
            }
            SetStatus(snapshot.Revision, answer is not { Certain: true } ? "Laya не впевнена"
                : answer.Choice == "keep" ? "Laya · текст збережено" : "Laya · " + answer.Choice.ToUpperInvariant());
            return answer;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            lock (gate)
                if (runtime != null && revision == snapshot.Revision && !snapshot.Token.IsCancellationRequested)
                {
                    // A cold/slow CPU may exceed the initial allowance. Increase
                    // the next bounded budget without retaining any input text.
                    Volatile.Write(ref averageLatency, Math.Clamp(AverageLatencyMilliseconds * 1.4, 1050, 1500));
                    Interlocked.Exchange(ref nextRequest, Environment.TickCount64 + 500);
                }
            SetStatus(snapshot.Revision, "Laya не встигла відповісти"); return null;
        }
        catch (OperationCanceledException)
        { if (runtime != null) Interlocked.Exchange(ref nextRequest, Environment.TickCount64 + 250); throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException
            or FormatException or KeyNotFoundException or ArgumentException)
        { SetStatus(snapshot.Revision, "Некоректна відповідь Laya"); return null; }
        finally { flight.Release(); }
    }

    internal static string NormalizeDevice(string device) => device is "cpu" or "cuda" ? device : "auto";
    internal static int RequestTimeout(bool test, string device, double latency) => test ? 8000 : device == "cpu"
        ? (int)Math.Clamp(double.IsFinite(latency) ? latency * 2 + 250 : 1600, 1600, 3000) : 1200;

    private static string[] ContextWords(string text, int count, int maxLength, bool last)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(LanguageDetection.Sendable);
        var bounded = (last ? words.TakeLast(count) : words.Take(count)).ToList();
        while (string.Join(" ", bounded).Length > maxLength && bounded.Count > 0) bounded.RemoveAt(last ? 0 : bounded.Count - 1);
        return bounded.ToArray();
    }

    internal static AiDecision? Parse(byte[] json, string questionId, string word, Dictionary<string, string> readings, Dictionary<string, string> candidates)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean()) return null;
            if (usage.TryGetProperty("state_tokens_dropped", out var dropped) && dropped.GetInt32() != 0) return null;
            if (usage.TryGetProperty("truncated_questions", out var questions) && questions.GetArrayLength() != 0) return null;
        }
        var answer = root.GetProperty("answers").GetProperty(questionId);
        if (answer.GetProperty("type").GetString() != "choice") throw new InvalidDataException();
        string choice = answer.GetProperty("choice").GetString() ?? "";
        if (!Languages.Contains(choice)) throw new InvalidDataException();
        double entropyConfidence = answer.GetProperty("confidence").GetDouble();
        if (!double.IsFinite(entropyConfidence) || entropyConfidence < 0 || entropyConfidence > 1) throw new InvalidDataException();
        if (answer.TryGetProperty("low_confidence", out var low) && low.GetBoolean()) return null;
        if (answer.TryGetProperty("action", out var action) && action.TryGetProperty("act_probability", out var act))
        {
            double probability = act.GetDouble();
            if (!double.IsFinite(probability) || probability < .5 || probability > 1) return null;
        }
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in answer.GetProperty("probabilities").EnumerateObject())
        {
            double value = item.Value.GetDouble();
            if (!Languages.Contains(item.Name) || !double.IsFinite(value) || value < 0 || value > 1 || !probabilities.TryAdd(item.Name, value)) throw new InvalidDataException();
        }
        if (probabilities.Count != 3 || Math.Abs(probabilities.Values.Sum() - 1) > .05
            || probabilities[choice] + .0001 < probabilities.Values.Max()) throw new InvalidDataException();
        // RU/UK can spell a word identically. Entropy over language labels is not
        // uncertainty over the replacement: combine those labels before deciding.
        var spellings = Languages.GroupBy(language => readings[language], StringComparer.Ordinal)
            .Select(group => new { Text = group.Key, Probability = group.Sum(language => probabilities[language]) })
            .OrderByDescending(group => group.Probability).ToArray();
        var selected = spellings[0];
        string? eligible = selected.Text == word ? "keep"
            : candidates.FirstOrDefault(candidate => candidate.Key != "keep" && candidate.Value == selected.Text).Key;
        if (eligible == null) return null;
        double probabilityMass = Math.Min(1, selected.Probability);
        double runner = spellings.Length > 1 ? spellings[1].Probability : 0;
        return new(eligible, probabilityMass, probabilityMass >= .90 && probabilityMass - runner >= .25);
    }

    private void SetStatus(int version, string value) { lock (gate) { if (!disposed && revision == version) Volatile.Write(ref status, value); } }
    private void ResetLocked()
    {
        revision++; cache.Clear(); model = ""; Interlocked.Exchange(ref nextRequest, 0); Volatile.Write(ref averageLatency, 0);
        lifetime.Cancel(); lifetime.Dispose(); lifetime = new();
        runtime?.Reset();
        Volatile.Write(ref status, "Налаштування локальної моделі оновлено");
    }
    public void Reset() { lock (gate) { if (!disposed) ResetLocked(); } }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            ResetLocked(); disposed = true; lifetime.Dispose();
        }
        runtime?.Dispose(); client.Dispose();
    }
}
