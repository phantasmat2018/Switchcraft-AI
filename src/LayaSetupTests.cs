using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Switchcraft;

/// <summary>Small synthetic archives and injected transport only; never installs Python, CUDA or a real model.</summary>
internal static class LayaSetupTests
{
    private static readonly Uri DownloadUrl = new("https://github.com/switchcraft-test/fixtures/releases/download/test/model.zip");

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly byte[] bytes;
        public int Calls;
        public long? LastRange;
        public bool BlockStream;
        public Uri? RedirectTo;
        public readonly TaskCompletionSource<bool> StreamBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FixtureHandler(byte[] bytes) => this.bytes = bytes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            if (RedirectTo != null)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = RedirectTo;
                return Task.FromResult(redirect);
            }
            LastRange = request.Headers.Range?.Ranges.Single().From;
            int start = checked((int)(LastRange ?? 0));
            if (start < 0 || start > bytes.Length) throw new InvalidOperationException("Invalid fixture range.");
            byte[] content = bytes[start..];
            HttpContent body = BlockStream
                ? new StreamContent(new CancellableStream(content, StreamBlocked))
                : new ByteArrayContent(content);
            body.Headers.ContentLength = content.Length;
            if (LastRange.HasValue && content.Length > 0)
                body.Headers.ContentRange = new ContentRangeHeaderValue(start, bytes.Length - 1, bytes.Length);
            return Task.FromResult(new HttpResponseMessage(LastRange.HasValue ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = body });
        }
    }

    private sealed class CancellableStream : Stream
    {
        private readonly byte[] bytes;
        private readonly TaskCompletionSource<bool> blocked;
        private int offset;
        public CancellableStream(byte[] bytes, TaskCompletionSource<bool> blocked) { this.bytes = bytes; this.blocked = blocked; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        {
            if (offset >= bytes.Length) return 0;
            if (offset > 0)
            {
                blocked.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            int count = Math.Min(32, Math.Min(buffer.Length, bytes.Length - offset));
            bytes.AsMemory(offset, count).CopyTo(buffer); offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int index, int count, CancellationToken cancellation)
            => ReadAsync(buffer.AsMemory(index, count), cancellation).AsTask();
        public override int Read(byte[] buffer, int index, int count)
        {
            int read = Math.Min(count, bytes.Length - offset);
            bytes.AsSpan(offset, read).CopyTo(buffer.AsSpan(index, read)); offset += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static async Task Run(Action<string, bool, string> check, string directory)
    {
        string root = Path.Combine(directory, "laya-setup-tests");
        Directory.CreateDirectory(root);
        void Check(string name, bool passed, string actual = "") => check("Laya setup: " + name, passed, actual);
        foreach (var (requested, processors, expected) in new[] { (0, 1, 1), (0, 2, 1), (0, 8, 4), (0, 64, 4), (-1, 8, 4), (int.MaxValue, 8, 8), (2, 64, 2), (1, 0, 1) })
        {
            int actual = LayaRuntime.ResolveCpuThreads(requested, processors);
            Check($"CPU thread profile stays bounded: {requested}/{processors}", actual == expected, actual.ToString());
        }
        Check("GPU preference is explicit and invalid values use auto", LayaDetector.NormalizeDevice("cuda") == "cuda"
            && LayaDetector.NormalizeDevice("cpu") == "cpu" && LayaDetector.NormalizeDevice("invalid-device") == "auto");
        Check("CPU deadline adapts within bounded latency", LayaDetector.RequestTimeout(false, "cpu", 0) == 1600
            && LayaDetector.RequestTimeout(false, "cpu", 1000) == 2250 && LayaDetector.RequestTimeout(false, "cpu", 10000) == 3000);
        Check("invalid CPU timing cannot create an unbounded deadline", LayaDetector.RequestTimeout(false, "cpu", double.NaN) == 1600
            && LayaDetector.RequestTimeout(false, "cpu", double.PositiveInfinity) == 1600);
        Check("GPU and explicit diagnostics keep separate deadline budgets", LayaDetector.RequestTimeout(false, "cuda", 10000) == 1200
            && LayaDetector.RequestTimeout(true, "cpu", 10000) == 8000);
        Check("CUDA setup accepts supported hardware at minimum bounds", LayaSetup.SuitableCudaRow("7.5, 2048, 560.76")
            && LayaSetup.SuitableCudaRow("8.6, 8192, 578.00"));
        Check("old GPU, insufficient VRAM and old driver retain CPU fallback", !LayaSetup.SuitableCudaRow("7.4, 8192, 578.00")
            && !LayaSetup.SuitableCudaRow("8.6, 2047, 578.00") && !LayaSetup.SuitableCudaRow("8.6, 8192, 560.75")
            && !LayaSetup.SuitableCudaRow("not a device"));
        Check("newer GPU architectures unsupported by pinned cu126 retain CPU fallback", LayaSetup.SuitableCudaRow("9.0, 16384, 578.00")
            && !LayaSetup.SuitableCudaRow("10.0, 16384, 578.00") && !LayaSetup.SuitableCudaRow("12.0, 16384, 578.00"));
        byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Synthetic model archive bytes; no real weights.\n", 8)));
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var handler = new FixtureHandler(bytes);
        using var setup = new LayaSetup(Path.Combine(root, "runtime-placeholder"), handler);

        string complete = Path.Combine(root, "verified-download.bin");
        await setup.DownloadVerifiedAsync(DownloadUrl, complete, hash, bytes.Length, 4096, null, CancellationToken.None);
        Check("download is published only after hash verification", File.Exists(complete) && File.ReadAllBytes(complete).SequenceEqual(bytes));

        string resumed = Path.Combine(root, "resumed-download.bin");
        File.WriteAllBytes(resumed + ".part", bytes[..13]);
        await setup.DownloadVerifiedAsync(DownloadUrl, resumed, hash, bytes.Length, 4096, null, CancellationToken.None);
        Check("partial download resumes at its verified byte offset", handler.LastRange == 13 && File.ReadAllBytes(resumed).SequenceEqual(bytes), handler.LastRange?.ToString() ?? "no range");

        string wrongHash = Path.Combine(root, "wrong-hash.bin");
        bool rejected = await RejectsAsync(() => setup.DownloadVerifiedAsync(DownloadUrl, wrongHash, new string('0', 64), bytes.Length, 4096, null, CancellationToken.None));
        Check("checksum mismatch prevents final file publication", rejected && !File.Exists(wrongHash));

        string wrongSize = Path.Combine(root, "wrong-size.bin");
        rejected = await RejectsAsync(() => setup.DownloadVerifiedAsync(DownloadUrl, wrongSize, hash, bytes.Length + 1, 4096, null, CancellationToken.None));
        Check("declared size mismatch prevents publication", rejected && !File.Exists(wrongSize));

        string oversized = Path.Combine(root, "oversized-download.bin");
        rejected = await RejectsAsync(() => setup.DownloadVerifiedAsync(DownloadUrl, oversized, hash, null, 64, null, CancellationToken.None));
        Check("download size limit is enforced", rejected && !File.Exists(oversized));

        int before = handler.Calls;
        rejected = await RejectsAsync(() => setup.DownloadVerifiedAsync(new Uri("http://github.com/test/model.zip"), Path.Combine(root, "insecure.bin"), hash, bytes.Length, 4096, null, CancellationToken.None));
        Check("plain HTTP is rejected before transport", rejected && handler.Calls == before);
        rejected = await RejectsAsync(() => setup.DownloadVerifiedAsync(new Uri("https://example.com/model.zip"), Path.Combine(root, "foreign-host.bin"), hash, bytes.Length, 4096, null, CancellationToken.None));
        Check("unapproved download host is rejected before transport", rejected && handler.Calls == before);
        handler.RedirectTo = new Uri("https://example.com/redirected-model.zip");
        rejected = await RejectsAsync(() => setup.DownloadVerifiedAsync(DownloadUrl, Path.Combine(root, "redirected.bin"), hash, bytes.Length, 4096, null, CancellationToken.None));
        Check("redirect cannot bypass the approved-host restriction", rejected && handler.Calls == before + 1);
        handler.RedirectTo = null;

        var slowHandler = new FixtureHandler(bytes) { BlockStream = true };
        using (var slow = new LayaSetup(Path.Combine(root, "slow-runtime-placeholder"), slowHandler))
        using (var cancellation = new CancellationTokenSource())
        {
            string cancelled = Path.Combine(root, "cancelled-download.bin");
            var pending = slow.DownloadVerifiedAsync(DownloadUrl, cancelled, hash, bytes.Length, 4096, null, cancellation.Token);
            await slowHandler.StreamBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            bool wasCancelled = false;
            try { await pending; }
            catch (OperationCanceledException) { wasCancelled = true; }
            Check("stream cancellation cannot publish a partial model", wasCancelled && !File.Exists(cancelled));
        }

        string extraction = Path.Combine(root, "safe-extraction");
        string entry = LayaSetup.SafeEntryPath(extraction, "encoder/config.json");
        Check("safe nested archive entry stays inside destination", string.Equals(entry, Path.Combine(extraction, "encoder", "config.json"), StringComparison.OrdinalIgnoreCase));
        foreach (string unsafeName in new[] { "../outside.txt", "..\\outside.txt", "model/../../outside.txt", "/absolute.txt", "C:\\outside.txt", "\\\\server\\share\\file", "model.safetensors:stream", "encoder/file:stream" })
        {
            rejected = Rejects(() => LayaSetup.SafeEntryPath(extraction, unsafeName));
            Check("unsafe archive path rejected: " + unsafeName, rejected);
        }

        string goodArchive = Archive(root, "good.zip", ("encoder/config.json", "{\"fixture\":true}", false), ("tokenizer/config.json", "{}", false));
        await LayaSetup.ExtractSafeAsync(goodArchive, extraction, 4096, CancellationToken.None);
        Check("ordinary archive extracts intact", File.ReadAllText(Path.Combine(extraction, "encoder", "config.json")) == "{\"fixture\":true}");

        string outside = Path.Combine(root, "outside.txt");
        File.WriteAllText(outside, "do not replace");
        string traversal = Archive(root, "traversal.zip", ("../outside.txt", "malicious replacement", false));
        rejected = await RejectsAsync(() => LayaSetup.ExtractSafeAsync(traversal, Path.Combine(root, "traversal-extraction"), 4096, CancellationToken.None));
        Check("archive traversal leaves neighboring files unchanged", rejected && File.ReadAllText(outside) == "do not replace");

        string symlink = Archive(root, "symlink.zip", ("linked-model", "../outside.txt", true));
        rejected = await RejectsAsync(() => LayaSetup.ExtractSafeAsync(symlink, Path.Combine(root, "symlink-extraction"), 4096, CancellationToken.None));
        Check("archive symbolic links are rejected", rejected);

        string tooLarge = Archive(root, "too-large.zip", ("model.safetensors", new string('x', 256), false));
        rejected = await RejectsAsync(() => LayaSetup.ExtractSafeAsync(tooLarge, Path.Combine(root, "large-extraction"), 128, CancellationToken.None));
        Check("uncompressed archive size limit is enforced", rejected);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool wasCancelled = false;
            try { await LayaSetup.ExtractSafeAsync(goodArchive, Path.Combine(root, "cancelled-extraction"), 4096, cancelled.Token); }
            catch (OperationCanceledException) { wasCancelled = true; }
            Check("extraction observes cancellation", wasCancelled);
        }

        string existingModel = Path.Combine(root, "existing-model");
        Directory.CreateDirectory(existingModel);
        File.WriteAllText(Path.Combine(existingModel, "user-model.txt"), "preserve existing model");
        string staged = StageModel(root, "blocked-stage");
        rejected = Rejects(() => LayaSetup.CommitModel(staged, existingModel));
        Check("existing model directory is never overwritten", rejected && Directory.Exists(staged)
            && File.ReadAllText(Path.Combine(existingModel, "user-model.txt")) == "preserve existing model");
        string occupiedFile = Path.Combine(root, "occupied-model-target");
        File.WriteAllText(occupiedFile, "preserve existing file");
        rejected = Rejects(() => LayaSetup.CommitModel(staged, occupiedFile));
        Check("existing destination file is never overwritten", rejected && File.ReadAllText(occupiedFile) == "preserve existing file");

        string successStage = StageModel(root, "success-stage"), finalModel = Path.Combine(root, "new-model");
        Check("staged model structure is recognized", LayaSetup.HasModel(successStage));
        LayaSetup.CommitModel(successStage, finalModel);
        Check("verified model is committed intact to a new destination", LayaSetup.HasModel(finalModel)
            && File.ReadAllText(Path.Combine(finalModel, "model.safetensors")) == "synthetic fixture, never loaded");

        string plannedModel = Path.Combine(root, "plan-only-model");
        before = handler.Calls;
        int filesBefore = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Count();
        var cpuPlan = setup.CreatePlan(plannedModel, "cpu", suitableCuda: true);
        var gpuPlan = setup.CreatePlan(plannedModel, "auto", suitableCuda: true);
        var fallbackPlan = setup.CreatePlan(plannedModel, "cuda", suitableCuda: false);
        Check("setup plans select CPU, compatible CUDA and CPU fallback explicitly", cpuPlan.Profile == "cpu"
            && gpuPlan.Profile == "cu126" && fallbackPlan.Profile == "cpu");
        Check("viewing a setup plan creates no files or network requests", handler.Calls == before && !Directory.Exists(plannedModel)
            && !Directory.Exists(Path.Combine(root, "runtime-placeholder"))
            && Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Count() == filesBefore);
        var preservePlan = setup.CreatePlan(finalModel, "cpu", suitableCuda: false);
        Check("setup plan explicitly preserves an existing model", preservePlan.PreserveModel && !cpuPlan.PreserveModel
            && string.Equals(preservePlan.ModelPath, finalModel, StringComparison.OrdinalIgnoreCase));

        // The shared environment may exist on this PC, so only the model-adjacent lookup is asserted in isolation.
        bool sharedRuntime = File.Exists(Path.Combine(LayaSetup.DefaultRoot, "venv", "Scripts", "python.exe"));
        Check("a model without a Python environment needs preparation", sharedRuntime || !LayaSetup.IsPrepared(finalModel));
        Check("missing model always needs preparation", !LayaSetup.IsPrepared(plannedModel));
        Directory.CreateDirectory(Path.Combine(root, "venv", "Scripts"));
        File.WriteAllText(Path.Combine(root, "venv", "Scripts", "python.exe"), "synthetic fixture, never started");
        Check("model-adjacent environment counts as prepared, as in the runtime lookup", LayaSetup.IsPrepared(finalModel));

        string bundled = Path.Combine(AppContext.BaseDirectory, "Runtime");
        string[] missing = LayaSetup.CppRuntime.Where(name => !File.Exists(Path.Combine(bundled, name))).ToArray();
        Check("Switchcraft ships every C++ runtime DLL PyTorch needs on a clean Windows", missing.Length == 0, string.Join(", ", missing));
        string venv = Path.Combine(root, "runtime-venv"), home = Path.Combine(root, "runtime-python"), source = Path.Combine(root, "runtime-source");
        Directory.CreateDirectory(venv); Directory.CreateDirectory(home); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "home = " + home + "\nimplementation = CPython\n");
        foreach (string name in LayaSetup.CppRuntime) File.WriteAllText(Path.Combine(source, name), "switchcraft " + name);
        File.WriteAllText(Path.Combine(home, "vcruntime140.dll"), "python's own runtime");
        LayaSetup.EnsureCppRuntime(venv, source);
        Check("missing C++ runtime DLLs are placed next to the Laya interpreter", LayaSetup.CppRuntime.All(name => File.Exists(Path.Combine(home, name))));
        Check("the interpreter's own runtime is kept when it is not older", File.ReadAllText(Path.Combine(home, "vcruntime140.dll")) == "python's own runtime");
        File.Delete(Path.Combine(source, "msvcp140.dll")); File.Delete(Path.Combine(home, "msvcp140.dll"));
        rejected = Rejects(() => LayaSetup.EnsureCppRuntime(venv, source));
        Check("an incomplete runtime source is reported instead of leaving torch unloadable", rejected);
    }

    private static string StageModel(string root, string name)
    {
        string staged = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(staged, "encoder"));
        Directory.CreateDirectory(Path.Combine(staged, "tokenizer"));
        File.WriteAllText(Path.Combine(staged, "model.safetensors"), "synthetic fixture, never loaded");
        File.WriteAllText(Path.Combine(staged, "rl_agent_config.json"), "{}");
        File.WriteAllText(Path.Combine(staged, "encoder", "config.json"), "{}");
        File.WriteAllText(Path.Combine(staged, "tokenizer", "tokenizer.json"), "{}");
        return staged;
    }

    private static string Archive(string root, string name, params (string Name, string Text, bool Symlink)[] entries)
    {
        string file = Path.Combine(root, name);
        using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name);
            if (item.Symlink) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(item.Text);
        }
        return file;
    }

    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return true; }
    }
    private static async Task<bool> RejectsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return true; }
    }
}
