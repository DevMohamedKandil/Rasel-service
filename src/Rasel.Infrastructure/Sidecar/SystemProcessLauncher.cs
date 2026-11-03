using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Rasel.Infrastructure.Sidecar;

public sealed class SystemProcessLauncher(ILogger<SystemProcessLauncher> logger) : IProcessLauncher
{
    private sealed class Managed(Process process, TaskCompletionSource<int> exited) : IManagedProcess
    {
        public int Id => process.Id;
        public bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }
        public Task<int> Exited => exited.Task;
        public void KillTree() { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }

    private static ProcessStartInfo Build(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        return psi;
    }

    private static void Pump(StreamReader reader, string? logPath, System.Text.StringBuilder? capture)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var log = logPath is null ? null : new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    if (capture is not null && capture.Length < 8000) capture.AppendLine(line);
                    if (log is not null) await log.WriteLineAsync(line);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
    }

    public IManagedProcess Start(ProcessSpec spec)
    {
        var process = new Process { StartInfo = Build(spec), EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(SafeExit(process));
        process.Start();
        Pump(process.StandardOutput, spec.LogPath, null);
        Pump(process.StandardError, spec.LogPath, null);
        return new Managed(process, exited);
    }

    private static int SafeExit(Process p) { try { return p.ExitCode; } catch (InvalidOperationException) { return -1; } }

    public async Task<(int ExitCode, string Output)> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        var output = new System.Text.StringBuilder();
        try
        {
            process = new Process { StartInfo = Build(spec), EnableRaisingEvents = true };
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning("Could not start {File}: {Message}", spec.FileName, ex.Message);
            return (-1, "");
        }
        using (process)
        {
            Pump(process.StandardOutput, spec.LogPath, output);
            Pump(process.StandardError, spec.LogPath, null);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try { await process.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return (-1, output.ToString());
            }
            await Task.Delay(100, CancellationToken.None); // let the stdout pump drain
            return (process.ExitCode, output.ToString());
        }
    }
}

public sealed class HttpSidecarProbe(IHttpClientFactory factory) : ISidecarProbe
{
    public async Task<SidecarProbe> ProbeAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = factory.CreateClient("sidecar-probe");
            http.Timeout = TimeSpan.FromSeconds(3);
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz", ct);
            if (!response.IsSuccessStatusCode) return new SidecarProbe(false, false);
            var doc = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
            var ready = doc.TryGetProperty("ready", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True;
            return new SidecarProbe(true, ready);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) && !ct.IsCancellationRequested)
        {
            return new SidecarProbe(false, false);
        }
    }
}
