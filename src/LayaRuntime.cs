using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchcraft;

/// <summary>Owns only the private, offline Python process started by this instance.</summary>
internal sealed class LayaRuntime : IDisposable
{
    // CUDA availability only checks the driver. Test the tensor operations used
    // by this encoder before selecting a GPU whose kernels the wheel may lack.
    // Running this bootstrap in the server process imports torch only once.
    private const string StartupScript = """
import os, runpy, torch
if os.environ.get('LAYA_DEVICE', '').strip().lower() != 'cpu':
    usable = False
    try:
        if torch.cuda.is_available():
            dtype = torch.bfloat16 if torch.cuda.get_device_capability()[0] >= 8 else torch.float16
            x = torch.ones((1, 2, 16, 32), device='cuda', dtype=dtype)
            y = torch.nn.functional.scaled_dot_product_attention(x, x, x)
            z = torch.nn.functional.layer_norm(y @ x.transpose(-2, -1), (16,))
            torch.cuda.synchronize()
            del x, y, z
            usable = True
    except Exception:
        pass
    if not usable:
        os.environ['LAYA_DEVICE'] = 'cpu'
    try:
        torch.cuda.empty_cache()
    except Exception:
        pass
runpy.run_module('laya.serve', run_name='__main__')
""";
    internal sealed record Endpoint(Uri Address, string Token, string Device = "cpu");
    private readonly object gate = new();
    private readonly HttpClient health = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource lifetime = new();
    private Task<Endpoint?>? startup;
    private Process? process;
    private string path = "", status = "Локальну модель ще не завантажено";
    private string device = "auto", actualDevice = "cpu";
    private int threads = ResolveCpuThreads(0, Environment.ProcessorCount);
    private long retryAfter;
    private int attempts;
    private int revision;
    private bool disposed;
    internal string Status => Volatile.Read(ref status);
    internal string DeviceLabel => Volatile.Read(ref actualDevice) == "cpu" ? (device == "cuda" ? "CPU · GPU недоступний" : "CPU") : "GPU · " + Volatile.Read(ref actualDevice).ToUpperInvariant();
    internal void Configure(string preference, int cpuThreads)
    {
        lock (gate)
        {
            int count = ResolveCpuThreads(cpuThreads, Environment.ProcessorCount);
            if (device == preference && threads == count) return;
            ResetLocked(); device = preference; threads = count;
        }
    }
    internal bool Ready
    {
        get
        {
            lock (gate)
            {
                return startup is { IsCompletedSuccessfully: true } && startup.Result != null && IsRunning(process);
            }
        }
    }

    internal async Task<Endpoint?> GetAsync(string modelPath, bool wait, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Task<Endpoint?> pending;
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (disposed) return null;
            if (!string.Equals(path, modelPath, StringComparison.OrdinalIgnoreCase)) ResetLocked();
            path = modelPath;
            if (startup is { IsCompleted: true } && (!startup.IsCompletedSuccessfully || startup.Result == null || !IsRunning(process)))
            {
                if (retryAfter == 0)
                {
                    retryAfter = Environment.TickCount64 + 5000;
                    Volatile.Write(ref status, "Laya зупинилася · повторне завантаження через 5 с");
                }
                if (Environment.TickCount64 < retryAfter || attempts >= 3) return null;
                Stop(process); process = null; startup = null; retryAfter = 0;
            }
            if (startup == null)
            {
                attempts++;
                int version = revision;
                var token = lifetime.Token;
                Volatile.Write(ref status, "Завантаження локальної моделі…");
                // Cancelling a word never cancels model loading. Reset owns that lifetime.
                string selectedDevice = device; int selectedThreads = threads;
                startup = Task.Run(() => StartAsync(modelPath, selectedDevice, selectedThreads, version, token));
            }
            pending = startup;
        }
        if (!wait && !pending.IsCompleted) return null;
        return await pending.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private async Task<Endpoint?> StartAsync(string modelPath, string selectedDevice, int selectedThreads, int version, CancellationToken cancellation)
    {
        Process? owned = null;
        try
        {
            if (!Directory.Exists(modelPath) || !File.Exists(Path.Combine(modelPath, "model.safetensors"))
                || !File.Exists(Path.Combine(modelPath, "rl_agent_config.json"))
                || !Directory.Exists(Path.Combine(modelPath, "encoder")) || !Directory.Exists(Path.Combine(modelPath, "tokenizer")))
                throw new FileNotFoundException();
            // Loading weights needs host RAM even when CUDA will run inference.
            // Avoid paging on small machines just to enable the neural mode.
            if (!HasLoadMemory())
            {
                lock (gate)
                    if (revision == version && !disposed)
                    {
                        retryAfter = Environment.TickCount64 + 30_000;
                        Volatile.Write(ref status, "Для Laya потрібно близько 2 ГБ вільної RAM");
                    }
                return null;
            }
            string defaultRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchcraft", "Laya");
            string adjacent = Path.Combine(Directory.GetParent(modelPath)!.FullName, "venv", "Scripts", "python.exe");
            string installed = Path.Combine(defaultRuntime, "venv", "Scripts", "python.exe");
            string python = File.Exists(installed) ? installed : adjacent;
            if (!File.Exists(python)) throw new FileNotFoundException();
            int port;
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            try { port = ((IPEndPoint)reservation.LocalEndpoint).Port; }
            finally { reservation.Stop(); }
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var endpoint = new Endpoint(new Uri($"http://127.0.0.1:{port}/"), secret);
            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = modelPath
            };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(StartupScript);
            start.Environment["LAYA_HOST"] = "127.0.0.1";
            start.Environment["LAYA_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["LAYA_EXTRA_MODELS"] = JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string> { ["switchcraft-local"] = modelPath });
            start.Environment["LAYA_MODELS"] = "switchcraft-local";
            start.Environment["LAYA_DEFAULT_MODEL"] = "switchcraft-local";
            start.Environment["LAYA_PRELOAD"] = "1";
            // Empty means automatic CUDA/CPU selection in Laya; literal "auto"
            // is not accepted by the server's torch.device environment parser.
            start.Environment["LAYA_DEVICE"] = selectedDevice == "auto" ? "" : selectedDevice;
            start.Environment["LAYA_THREADS"] = selectedThreads.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["LAYA_MAX_LOADED"] = "1";
            start.Environment["LAYA_MAX_CONCURRENT"] = "1";
            start.Environment["LAYA_AUTO_TASK"] = "0";
            start.Environment["LAYA_LOG_LEVEL"] = "warning";
            start.Environment["LAYA_API_KEY"] = secret;
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["TRANSFORMERS_OFFLINE"] = "1";
            start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            start.Environment["TOKENIZERS_PARALLELISM"] = "false";
            owned = new Process { StartInfo = start };
            // Drain diagnostics without retaining model input, paths, or credentials in logs.
            owned.OutputDataReceived += (_, _) => { };
            owned.ErrorDataReceived += (_, _) => { };
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (disposed || revision != version) return null;
                if (!owned.Start()) throw new InvalidOperationException();
                process = owned;
                owned.BeginOutputReadLine(); owned.BeginErrorReadLine();
            }
            // A cold first start on a slow disk or under antivirus scanning can exceed a minute.
            // Dictionaries keep working meanwhile, so waiting longer never blocks typing.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(180));
            while (!deadline.IsCancellationRequested && !owned.HasExited)
            {
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    attempt.CancelAfter(1000);
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.Address, "health"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                    using var response = await health.SendAsync(request, attempt.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false));
                        if (document.RootElement.TryGetProperty("loaded", out var loaded))
                            foreach (var item in loaded.EnumerateArray())
                                if (item.GetString() == "switchcraft-local")
                                {
                                    lock (gate)
                                    {
                                        if (revision != version || disposed || cancellation.IsCancellationRequested) return null;
                                        string actual = document.RootElement.TryGetProperty("device", out var reported) ? reported.GetString() ?? "cpu" : "cpu";
                                        Volatile.Write(ref actualDevice, actual);
                                        Volatile.Write(ref status, "Laya · модель готова · " + DeviceLabel);
                                        return endpoint with { Device = actual };
                                    }
                                }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { }
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            }
            throw new TimeoutException();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception or OperationCanceledException or TimeoutException)
        {
            lock (gate)
            {
                if (revision == version && !disposed)
                {
                    Volatile.Write(ref status, ex is FileNotFoundException ? "Не знайдено локальну модель або Python" : "Не вдалося завантажити Laya");
                    retryAfter = Environment.TickCount64 + 30_000;
                }
                if (ReferenceEquals(process, owned)) process = null;
            }
            Stop(owned);
            return null;
        }
    }

    internal static int ResolveCpuThreads(int requested, int logicalProcessors) => requested <= 0
        ? Math.Clamp(logicalProcessors / 2, 1, 4) : Math.Clamp(requested, 1, Math.Max(1, logicalProcessors));
    private static bool IsRunning(Process? owned)
    {
        try { return owned is { HasExited: false }; }
        catch (InvalidOperationException) { return false; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    private static bool HasLoadMemory()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return !GlobalMemoryStatusEx(ref memory) || memory.AvailablePhysical >= 2UL * 1024 * 1024 * 1024;
    }
    private static void Stop(Process? owned)
    {
        if (owned == null) return;
        try { if (!owned.HasExited) owned.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        try { owned.Dispose(); } catch (InvalidOperationException) { }
    }
    private void ResetLocked()
    {
        revision++;
        lifetime.Cancel();
        // Old asynchronous startup still holds its token; it is safe to dispose the source.
        lifetime.Dispose(); lifetime = new();
        startup = null; path = ""; attempts = 0; retryAfter = 0;
        Stop(process); process = null;
        Volatile.Write(ref status, "Локальну модель зупинено");
    }
    internal void Reset() { lock (gate) { if (!disposed) ResetLocked(); } }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            ResetLocked(); disposed = true; lifetime.Dispose();
        }
        health.Dispose();
    }
}
