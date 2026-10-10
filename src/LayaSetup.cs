using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchcraft;

public sealed record LayaSetupProgress(string Message, double? Percent = null);
public sealed record LayaSetupInspection(bool ModelReady, bool RuntimeReady, string Device, string Message);
public sealed record LayaSetupPlan(string ModelPath, string RuntimeRoot, string Profile, bool PreserveModel, long RequiredFreeBytes, string[] Sources);

/// <summary>Explicit, cancellable per-user bootstrap. Inference never invokes this installer.</summary>
public sealed class LayaSetup : IDisposable
{
    internal const string ModelSha256 = "6935b48c897a5bf18d9d74acf682cc980ad2ec6698afe092b9347611f2ae8893";
    private const string RequirementsSha256 = "8b687eda8f502aa76cac66acbeee3eaabad39cca29d6ed7d4b73dc211677aaab";
    private const string UvSha256 = "ace861f360c6de2babedc1607d0f454b6b09a820dbc8182dc15af927e4df9589";
    private const string ReleaseBase = "https://github.com/phantasmat2018/Switchcraft-AI/releases/download/laya-r3/";
    private const long ModelBytes = 599_689_347;
    private static readonly SemaphoreSlim installGate = new(1, 1);
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchcraft", "Laya");
    private readonly string root;
    private readonly HttpClient http;
    public LayaSetup() : this(DefaultRoot, new HttpClientHandler { AllowAutoRedirect = false }) { }
    internal LayaSetup(string root, HttpMessageHandler handler)
    {
        this.root = Path.GetFullPath(root);
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Switchcraft-Laya-Setup/1.6");
    }

    public async Task InstallAsync(string modelPath, string device, IProgress<LayaSetupProgress> progress, CancellationToken cancellation)
    {
        if (!await installGate.WaitAsync(0, cancellation).ConfigureAwait(false)) throw new InvalidOperationException("Підготовка Laya вже виконується.");
        string? modelStage = null, uvStage = null, venvStage = null;
        try
        {
            modelPath = Environment.ExpandEnvironmentVariables(modelPath);
            if (!Path.IsPathFullyQualified(modelPath) || device is not ("auto" or "cpu" or "cuda")) throw new InvalidOperationException("Перевірте теку моделі та пристрій.");
            modelPath = Path.GetFullPath(modelPath);
            if (File.Exists(modelPath) || Directory.Exists(modelPath) && Directory.EnumerateFileSystemEntries(modelPath).Any() && !HasModel(modelPath))
                throw new InvalidOperationException("Обрана тека вже містить файли. Виберіть порожню теку або готову модель Laya; наявні файли не замінюються.");
            Directory.CreateDirectory(root);
            await using var setupLock = new FileStream(Path.Combine(root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            void Report(string text, double? percent = null)
            {
                progress.Report(new(text, percent));
                Log(text);
            }
            Report("Перевіряємо модель і доступне обладнання…");
            bool cuda = device != "cpu" && await SuitableNvidiaAsync(cancellation).ConfigureAwait(false);
            string profile = cuda ? "cu126" : "cpu";
            if (!cuda && device != "cpu") Report("Сумісна NVIDIA CUDA недоступна — підготовка для CPU.");
            var existing = await InspectExistingAsync(modelPath, cancellation).ConfigureAwait(false);
            if (existing.ModelReady && existing.RuntimeReady && (!cuda || existing.Device == "cuda"))
            { Report("Laya вже підготовлена · " + (device == "cpu" ? "CPU" : existing.Device.ToUpperInvariant()), 100); return; }
            var plan = CreatePlan(modelPath, device, cuda);
            EnsureDiskSpace(root, plan.RequiredFreeBytes);
            if (!plan.PreserveModel && !string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(modelPath), StringComparison.OrdinalIgnoreCase))
                EnsureDiskSpace(modelPath, 2_000_000_000);

            string cache = Path.Combine(root, "download"); Directory.CreateDirectory(cache);
            string requirements = Path.Combine(cache, "laya-requirements-r3.txt");
            Report("Завантажуємо перевірений список компонентів…");
            await DownloadVerifiedAsync(new(ReleaseBase + "laya-requirements.txt"), requirements, RequirementsSha256, null, 64_000, progress, cancellation).ConfigureAwait(false);
            if (!HasModel(modelPath))
            {
                Report("Завантажуємо модель Laya · приблизно 600 МБ…");
                string archive = Path.Combine(cache, "switchcraft-laya-r3.zip");
                await DownloadVerifiedAsync(new(ReleaseBase + "switchcraft-laya-r3.zip"), archive, ModelSha256, ModelBytes, ModelBytes, progress, cancellation).ConfigureAwait(false);
                string parent = Path.GetDirectoryName(modelPath) ?? throw new InvalidOperationException("Некоректна тека моделі.");
                Directory.CreateDirectory(parent);
                modelStage = Path.Combine(parent, ".switchcraft-laya-" + Guid.NewGuid().ToString("N"));
                Report("Перевіряємо й розпаковуємо модель…");
                await ExtractSafeAsync(archive, modelStage, 2_000_000_000, cancellation).ConfigureAwait(false);
                var models = Directory.EnumerateFiles(modelStage, "rl_agent_config.json", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName).Where(x => x != null && HasModel(x)).ToArray();
                if (models.Length != 1) throw new InvalidDataException("Архів не містить єдиної коректної моделі Laya.");
                string stagedModel = models[0]!;
                await File.WriteAllTextAsync(Path.Combine(stagedModel, ".sha256"), ModelSha256, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested(); CommitModel(stagedModel, modelPath);
                if (stagedModel == modelStage) modelStage = null;
            }
            else Report("Наявну модель збережено. Готуємо лише середовище.");

            string uv = Path.Combine(root, "uv", "uv.exe");
            if (!File.Exists(uv))
            {
                Report("Завантажуємо uv 0.11.21 із перевіркою SHA-256…");
                string archive = Path.Combine(cache, "uv-0.11.21.zip");
                await DownloadVerifiedAsync(new("https://github.com/astral-sh/uv/releases/download/0.11.21/uv-x86_64-pc-windows-msvc.zip"), archive, UvSha256, 23_452_481, 30_000_000, progress, cancellation).ConfigureAwait(false);
                uvStage = Path.Combine(root, ".uv-stage-" + Guid.NewGuid().ToString("N"));
                await ExtractSafeAsync(archive, uvStage, 250_000_000, cancellation).ConfigureAwait(false);
                var executables = Directory.GetFiles(uvStage, "uv.exe", SearchOption.AllDirectories);
                if (executables.Length != 1) throw new InvalidDataException("Архів uv некоректний.");
                Directory.CreateDirectory(Path.GetDirectoryName(uv)!);
                File.Move(executables[0], uv, false);
            }
            string python = Path.Combine(root, "venv", "Scripts", "python.exe");
            if (!File.Exists(python))
            {
                if (Directory.Exists(Path.Combine(root, "venv")) && Directory.EnumerateFileSystemEntries(Path.Combine(root, "venv")).Any())
                    throw new InvalidOperationException("Тека середовища Laya містить незавершене встановлення. Збережені файли залишено для перевірки.");
                Report("Встановлюємо окремий Python 3.12 для Laya…");
                venvStage = Path.Combine(root, ".venv-stage-" + Guid.NewGuid().ToString("N"));
                await RunAsync(uv, new[] { "venv", "--python", "3.12", "--managed-python", "--relocatable", "--no-config", venvStage }, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (!File.Exists(Path.Combine(venvStage, "Scripts", "python.exe"))) throw new IOException("Не вдалося створити Python для Laya.");
                string targetVenv = Path.Combine(root, "venv");
                if (Directory.Exists(targetVenv)) Directory.Delete(targetVenv, false);
                Directory.Move(venvStage, targetVenv); venvStage = null;
            }
            Report("Встановлюємо PyTorch 2.14.1 · " + (cuda ? "NVIDIA CUDA, великий пакет" : "CPU") + "…");
            await InstallTorchAsync(uv, python, profile, cancellation).ConfigureAwait(false);
            string constraints = Path.Combine(cache, "torch-" + profile + ".txt");
            await File.WriteAllTextAsync(constraints, "torch==2.14.1+" + profile + Environment.NewLine, cancellation).ConfigureAwait(false);
            Report("Встановлюємо зафіксовані компоненти Laya…");
            // uv splits --constraint values at spaces (like UV_CONSTRAINT), which breaks profiles such as
            // "C:\Users\Іван Петренко". The path relative to uv's working directory (root) has no spaces.
            await RunAsync(uv, new[] { "pip", "install", "--python", python, "--no-config", "--only-binary", ":all:", "--index-url", "https://pypi.org/simple", "-r", requirements, "--constraint", Path.GetRelativePath(root, constraints) }, cancellation).ConfigureAwait(false);
            EnsureCppRuntime(Path.Combine(root, "venv"), Path.Combine(AppContext.BaseDirectory, "Runtime"));
            Report("Перевіряємо встановлене середовище…");
            var ready = await InspectExistingAsync(modelPath, cancellation).ConfigureAwait(false);
            if (cuda && (!ready.RuntimeReady || ready.Device != "cuda"))
            {
                Report("CUDA не пройшла перевірку. Готуємо надійний резерв CPU…");
                await InstallTorchAsync(uv, python, "cpu", cancellation).ConfigureAwait(false);
                ready = await InspectExistingAsync(modelPath, cancellation).ConfigureAwait(false);
            }
            if (!ready.ModelReady || !ready.RuntimeReady) throw new InvalidOperationException("Компоненти встановлено, але перевірка Laya не завершилася. Повторіть підготовку.");
            // Archives are kept after a failure so the next run can resume; once installed they only occupy ~600 MB.
            foreach (string archive in new[] { "switchcraft-laya-r3.zip", "uv-0.11.21.zip" })
                try { File.Delete(Path.Combine(cache, archive)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Report(cuda && ready.Device != "cuda" ? "Laya готова · CUDA недоступна, працюватиме CPU." : "Laya готова · " + (device == "cpu" ? "CPU" : ready.Device.ToUpperInvariant()), 100);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Log("Підготовку скасовано."); throw; }
        catch (Exception ex) { Log("Підготовку не завершено: " + ex.Message); throw; }
        finally
        {
            DeleteOwnedStage(modelStage, ".switchcraft-laya-"); DeleteOwnedStage(uvStage, ".uv-stage-");
            DeleteOwnedStage(venvStage, ".venv-stage-");
            installGate.Release();
        }
    }

    // Progress and failures are kept for support on other PCs; the log never contains typed text.
    private void Log(string text)
    {
        try
        {
            if (Directory.Exists(root))
                File.AppendAllText(Path.Combine(root, "setup-progress.log"), DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static readonly string[] CppRuntime = { "vcruntime140.dll", "vcruntime140_1.dll", "vcruntime140_threads.dll", "msvcp140.dll", "msvcp140_1.dll",
        "msvcp140_2.dll", "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll", "concrt140.dll" };
    // PyTorch needs the MSVC C++ runtime (msvcp140, vcruntime140_threads…). uv's Python ships only vcruntime140*.dll
    // and a clean Windows has no Visual C++ Redistributable, so `import torch` fails there. Switchcraft's own
    // app-local runtime set goes next to the interpreter (the DLL application directory); nothing system-wide changes.
    internal static void EnsureCppRuntime(string venv, string runtimeSource)
    {
        string? home = File.ReadLines(Path.Combine(venv, "pyvenv.cfg")).Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2 && x[0].Trim() == "home").Select(x => x[1].Trim()).FirstOrDefault();
        if (string.IsNullOrEmpty(home) || !Directory.Exists(home)) throw new IOException("Не вдалося знайти Python для Laya. Повторіть підготовку.");
        static Version Of(string file)
        {
            if (!File.Exists(file)) return new(0, 0);
            var info = FileVersionInfo.GetVersionInfo(file);
            return new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        foreach (string name in CppRuntime)
        {
            string source = Path.Combine(runtimeSource, name), target = Path.Combine(home, name);
            if (!File.Exists(source)) throw new FileNotFoundException("У теці Switchcraft бракує компонента Visual C++: " + name);
            // Python's own newer vcruntime stays; a missing or older file gets Switchcraft's matched copy.
            if (File.Exists(target) && Of(target) >= Of(source)) continue;
            try { File.Copy(source, target, true); }
            catch (IOException) when (File.Exists(target)) { } // Loaded by a running Laya process, so it is usable.
        }
    }

    /// <summary>Same lookup as <see cref="LayaRuntime"/>: the model plus the shared or model-adjacent environment.</summary>
    public static bool IsPrepared(string modelPath) => HasModel(modelPath)
        && (File.Exists(Path.Combine(DefaultRoot, "venv", "Scripts", "python.exe"))
            || Directory.GetParent(Path.GetFullPath(modelPath)) is { } parent && File.Exists(Path.Combine(parent.FullName, "venv", "Scripts", "python.exe")));

    // A reviewable plan only: no directories, processes, settings or network requests.
    public LayaSetupPlan CreatePlan(string modelPath, string device, bool suitableCuda)
    {
        if (!Path.IsPathFullyQualified(modelPath) || device is not ("auto" or "cpu" or "cuda")) throw new InvalidOperationException("Перевірте теку моделі та пристрій.");
        bool cuda = device != "cpu" && suitableCuda;
        return new(Path.GetFullPath(modelPath), root, cuda ? "cu126" : "cpu", HasModel(modelPath), cuda ? 12_000_000_000 : 5_000_000_000,
            new[] { ReleaseBase + "switchcraft-laya-r3.zip", ReleaseBase + "laya-requirements.txt", "https://github.com/astral-sh/uv/releases/download/0.11.21/uv-x86_64-pc-windows-msvc.zip", "https://download.pytorch.org/whl/" + (cuda ? "cu126" : "cpu"), "https://pypi.org/simple" });
    }
    private static void EnsureDiskSpace(string destination, long required)
    {
        string? driveRoot = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrEmpty(driveRoot)) throw new IOException("Не вдалося визначити диск для Laya.");
        var drive = new DriveInfo(driveRoot);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"Недостатньо місця на диску {driveRoot}. Для підготовки Laya потрібно приблизно {required / 1_000_000_000} ГБ вільного місця.");
    }
    private Task<string> InstallTorchAsync(string uv, string python, string profile, CancellationToken cancellation)
    {
        string hash = profile == "cu126" ? "97e3bc87ea74367518b740d912a7f5259c25a3c8115bf5916abee1d53ff115a4" : "c52adc3bd526ff7fc5bdcc58fc4a76e6214d09951b646ffe5256ebd7ba60cef1";
        string wheel = $"https://download.pytorch.org/whl/{profile}/torch-2.14.1%2B{profile}-cp312-cp312-win_amd64.whl#sha256={hash}";
        return RunAsync(uv, new[] { "pip", "install", "--python", python, "--no-config", "--only-binary", ":all:", wheel, "--index-url", "https://download.pytorch.org/whl/" + profile }, cancellation);
    }

    public async Task<LayaSetupInspection> InspectExistingAsync(string modelPath, CancellationToken cancellation = default)
    {
        bool model = HasModel(modelPath);
        string python = Path.Combine(root, "venv", "Scripts", "python.exe");
        if (!File.Exists(python)) return new(model, false, "cpu", "Потрібне середовище Laya.");
        const string probe = "import json,importlib.metadata as m,torch,laya\nd='cpu'; safe=True\ntry:\n if torch.cuda.is_available():\n  x=torch.ones(1,device='cuda'); (x+x).cpu(); d='cuda'\nexcept Exception: safe=False\nprint(json.dumps({'ready':safe and m.version('laya')=='0.4.1' and torch.__version__.startswith('2.14.1+'),'device':d}))";
        try
        {
            // The first torch import after installation can take minutes while antivirus scans its DLLs.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(180));
            string output = await RunAsync(python, new[] { "-c", probe }, timeout.Token, log: false).ConfigureAwait(false);
            using var json = JsonDocument.Parse(output.Trim());
            bool ready = json.RootElement.GetProperty("ready").GetBoolean();
            string device = json.RootElement.GetProperty("device").GetString() == "cuda" ? "cuda" : "cpu";
            return new(model, ready, device, model && ready ? "Модель і середовище готові." : "Потрібна підготовка Laya.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new(model, false, "cpu", "Перевірка середовища не встигла завершитися."); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        { return new(model, false, "cpu", "Потрібна підготовка середовища Laya."); }
    }

    internal static bool HasModel(string directory) => Directory.Exists(directory)
        && File.Exists(Path.Combine(directory, "rl_agent_config.json")) && File.Exists(Path.Combine(directory, "model.safetensors"))
        && Directory.Exists(Path.Combine(directory, "encoder")) && Directory.Exists(Path.Combine(directory, "tokenizer"));

    internal async Task DownloadVerifiedAsync(Uri url, string destination, string expectedSha256, long? expectedSize, long maxBytes,
        IProgress<LayaSetupProgress>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); ValidateDownloadUri(url);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (File.Exists(destination) && await ValidFileAsync(destination, expectedSha256, expectedSize, maxBytes, cancellation).ConfigureAwait(false)) return;
        string partial = destination + ".part";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > 0 && (offset >= maxBytes || expectedSize != null && offset >= expectedSize.Value))
        {
            if (await ValidFileAsync(partial, expectedSha256, expectedSize, maxBytes, cancellation).ConfigureAwait(false))
            { File.Move(partial, destination, true); return; }
            File.Delete(partial); offset = 0;
        }
        using var response = await SendDownloadAsync(url, offset, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
        {
            File.Delete(partial);
            await DownloadVerifiedAsync(url, destination, expectedSha256, expectedSize, maxBytes, progress, cancellation).ConfigureAwait(false);
            return;
        }
        response.EnsureSuccessStatusCode();
        long? advertised = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.From != offset) throw new InvalidDataException("Сервер повернув некоректне продовження завантаження.");
        }
        else offset = 0; // A server may deliberately ignore Range; restart safely.
        if (advertised > maxBytes || expectedSize != null && advertised != null && advertised != expectedSize.Value)
            throw new InvalidDataException("Розмір файлу не відповідає перевіреному випуску.");
        long total = offset; long? length = expectedSize ?? advertised;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
        await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
        {
            var buffer = new byte[131072]; long lastReport = 0;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation); idle.CancelAfter(TimeSpan.FromSeconds(60));
                int read;
                try { read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { throw new IOException("Завантаження не відповідає протягом хвилини. Перевірте інтернет і повторіть — збережену частину буде продовжено."); }
                if (read == 0) break;
                total += read; if (total > maxBytes || expectedSize != null && total > expectedSize.Value) throw new InvalidDataException("Завантаження перевищило очікуваний розмір.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                if (Environment.TickCount64 - lastReport > 250)
                {
                    lastReport = Environment.TickCount64;
                    progress?.Report(new($"Завантаження: {total / 1_000_000.0:0.0} МБ" + (length > 0 ? $" / {length / 1_000_000.0:0.0} МБ" : ""), length > 0 ? 100.0 * total / length.Value : null));
                }
            }
            await output.FlushAsync(cancellation).ConfigureAwait(false);
        }
        progress?.Report(new("Перевіряємо SHA-256 завантаженого файлу…"));
        if (!await ValidFileAsync(partial, expectedSha256, expectedSize, maxBytes, cancellation).ConfigureAwait(false))
        { File.Delete(partial); throw new InvalidDataException("Контрольна сума файлу не збігається. Повторіть завантаження."); }
        File.Move(partial, destination, true);
    }

    private async Task<HttpResponseMessage> SendDownloadAsync(Uri url, long offset, CancellationToken cancellation)
    {
        for (int redirect = 0; redirect < 8; redirect++)
        {
            ValidateDownloadUri(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new IOException("Сервер не відповідає. Перевірте інтернет і повторіть підготовку Laya."); }
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (location == null) throw new InvalidDataException("Сервер не вказав адресу файлу.");
            url = location.IsAbsoluteUri ? location : new Uri(url, location);
        }
        throw new InvalidDataException("Забагато перенаправлень під час завантаження.");
    }
    private static void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("Завантаження дозволене лише з офіційного HTTPS-випуску.");
    }
    private static async Task<bool> ValidFileAsync(string path, string hash, long? expectedSize, long maxBytes, CancellationToken cancellation)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > maxBytes || expectedSize != null && info.Length != expectedSize.Value) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    internal static string SafeEntryPath(string root, string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || Path.IsPathRooted(entryName) || entryName.Contains(':')) throw new InvalidDataException("Небезпечний шлях у ZIP.");
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(canonicalRoot, entryName.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP намагається записати файл за межами теки.");
        return path;
    }
    internal static async Task ExtractSafeAsync(string archive, string destination, long maxUncompressedBytes, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Directory.Exists(destination) && ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(destination).Any())) throw new IOException("Тека розпакування вже містить файли або є посиланням.");
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 10_000) throw new InvalidDataException("Забагато файлів у ZIP.");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            total = checked(total + entry.Length);
            if (total > maxUncompressedBytes || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("ZIP містить небезпечні або надмірні дані.");
            string path = SafeEntryPath(destination, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072]; long written = 0; int read;
            while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
            {
                written += read;
                if (written > entry.Length) throw new InvalidDataException("ZIP перевищив заявлену довжину файлу.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
            }
            if (output.Length != entry.Length) throw new InvalidDataException("ZIP містить некоректну довжину файлу.");
        }
    }
    internal static void CommitModel(string stagedModel, string destination)
    {
        if (!HasModel(stagedModel)) throw new InvalidDataException("Підготовлена модель неповна.");
        if (File.Exists(destination) || Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Наявна модель або інші файли не замінюються.");
        if (Directory.Exists(destination)) Directory.Delete(destination, false);
        Directory.Move(stagedModel, destination);
    }
    private static void DeleteOwnedStage(string? path, string prefix)
    {
        if (path == null || !Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal) || !Directory.Exists(path)) return;
        try { Directory.Delete(Path.GetFullPath(path), true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static bool SuitableCudaRow(string row)
    {
        var parts = row.Split(',').Select(x => x.Trim()).ToArray();
        return parts.Length == 3 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var capability) && capability >= 7.5 && capability < 10
            && long.TryParse(parts[1], out var memory) && memory >= 2048 && Version.TryParse(parts[2], out var driver) && driver >= new Version(560, 76);
    }
    private async Task<bool> SuitableNvidiaAsync(CancellationToken cancellation)
    {
        string? executable = new[] { Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe") }.FirstOrDefault(File.Exists);
        if (executable == null) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string output = await RunAsync(executable, new[] { "--query-gpu=compute_cap,memory.total,driver_version", "--format=csv,noheader,nounits" }, timeout.Token, log: false).ConfigureAwait(false);
            return output.Split('\n').Any(SuitableCudaRow);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellation, bool log = true)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(root, "python");
        start.Environment["UV_CACHE_DIR"] = Path.Combine(root, "cache");
        start.Environment["UV_NO_PROGRESS"] = "1";
        start.Environment["UV_HTTP_TIMEOUT"] = "60";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не вдалося запустити підготовку Laya.");
        using var cancelled = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellation), stderr = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        string output = await stdout.ConfigureAwait(false), errors = await stderr.ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (log) await File.AppendAllTextAsync(Path.Combine(root, "setup-install.log"), output + errors + Environment.NewLine, cancellation).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Не вдалося підготувати компоненти Laya. Подробиці: setup-install.log у теці Laya.");
        return output;
    }
    public void Dispose() => http.Dispose();
}
