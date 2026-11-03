using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Sidecar;

/// <summary>
/// Prepares and supervises the bundled whatsapp-web.js sidecar as a child process (Provider=Managed). Runs in the background so the
/// API answers immediately. Prepare (copy, Node check, npm ci when needed, Chrome check) -> start -> health loop -> restart on crash
/// with back-off and a crash-loop circuit; a manual Stop is never treated as a crash. Single instance per DataDir (lock file).
/// </summary>
public sealed class SidecarSupervisor(
    IOptions<SidecarOptions> options, IProcessLauncher launcher, ISidecarProbe probe, SidecarFiles files,
    ILogger<SidecarSupervisor> logger) : BackgroundService, ISidecarControl, IBridgeEndpoint
{
    private readonly object _gate = new();
    private SidecarStatus _status = new(SidecarState.Preparing, "Waiting to start", AutoStart: options.Value.AutoStart);
    private bool _wantRunning = options.Value.AutoStart;
    private bool _restart;
    private bool _hasRunOnce;
    private CancellationTokenSource? _run;
    private TaskCompletionSource _wake = NewWake();
    private (string, string)? _endpoint;
    private FileStream? _lock;
    private IManagedProcess? _proc;

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SidecarStatus Status { get { lock (_gate) return _status; } }
    public (string BaseUrl, string Token)? Current { get { lock (_gate) return _endpoint; } }

    private void Set(SidecarState s, string? step = null, string? reason = null, int crashes = 0)
    {
        lock (_gate) _status = new SidecarStatus(s, step, reason, crashes, options.Value.AutoStart);
    }

    // ---- commands (idempotent, safe under concurrency) ----
    public SidecarCommandResult RequestStart()
    {
        lock (_gate)
        {
            if (_wantRunning && _status.State is SidecarState.Starting or SidecarState.NeedsQr or SidecarState.Connected or SidecarState.Degraded)
                return SidecarCommandResult.AlreadyInState;
            _wantRunning = true;
            _wake.TrySetResult();
            return SidecarCommandResult.Accepted;
        }
    }

    public SidecarCommandResult RequestStop()
    {
        lock (_gate)
        {
            if (!_wantRunning && _status.State is SidecarState.Stopped or SidecarState.Ready) return SidecarCommandResult.AlreadyInState;
            _wantRunning = false;
            _restart = false;
            _run?.Cancel();
            _wake.TrySetResult();
            return SidecarCommandResult.Accepted;
        }
    }

    public SidecarCommandResult RequestRestart()
    {
        lock (_gate)
        {
            _wantRunning = true;
            _restart = true;
            _run?.Cancel();
            _wake.TrySetResult();
            return SidecarCommandResult.Accepted;
        }
    }

    // ---- main loop ----
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            if (!files.TryAcquireLock(out _lock))
            {
                Set(SidecarState.Failed, reason: "Another Rasel instance already supervises this data folder (Sidecar:DataDir). Only one may run the sidecar.");
                return;
            }
            if (!await PrepareAsync(stop)) return;
            await SuperviseAsync(stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sidecar supervisor crashed");
            Set(SidecarState.Failed, reason: "The supervisor hit an unexpected error; see the Rasel log.");
        }
        finally
        {
            KillCurrent();
            _lock?.Dispose();
        }
    }

    private void KillCurrent()
    {
        var p = _proc;
        _proc = null;
        lock (_gate) _endpoint = null;
        try { if (p is { HasExited: false }) p.KillTree(); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not kill the sidecar process tree"); }
    }

    private async Task<bool> PrepareAsync(CancellationToken ct)
    {
        var o = options.Value;
        Set(SidecarState.Preparing, "Copying the WhatsApp engine files");
        var copyError = files.PrepareFolders();
        if (copyError is not null) { Set(SidecarState.Failed, reason: copyError); return false; }

        Set(SidecarState.Preparing, "Checking Node.js");
        var node = await launcher.RunAsync(new ProcessSpec("node", ["--version"], files.AppDir), TimeSpan.FromSeconds(20), ct);
        if (node.ExitCode != 0 || !SidecarFiles.TryParseNodeMajor(node.Output, out var major))
        {
            Set(SidecarState.Failed, reason: $"Node.js {o.MinNodeMajor} or newer was not found. Install Node.js (LTS) on this server and restart Rasel.");
            return false;
        }
        if (major < o.MinNodeMajor)
        {
            Set(SidecarState.Failed, reason: $"Node.js {major} is too old; version {o.MinNodeMajor} or newer is required. Upgrade Node.js and restart Rasel.");
            return false;
        }

        if (files.NeedsInstall())
        {
            Set(SidecarState.InstallingDependencies, "Installing WhatsApp engine dependencies (1-3 minutes the first time)");
            var npm = await launcher.RunAsync(new ProcessSpec(SidecarFiles.NpmExecutable, ["ci", "--no-audit", "--no-fund"], files.AppDir, LogPath: files.InstallLogPath),
                TimeSpan.FromMinutes(o.InstallTimeoutMinutes), ct);
            if (npm.ExitCode != 0)
            {
                Set(SidecarState.Failed, reason: $"Installing dependencies failed (npm exit code {npm.ExitCode}). Check access to the npm registry and the install log in the data folder, then restart Rasel.");
                return false;
            }
            files.MarkInstalled();
        }

        Set(SidecarState.Preparing, "Checking Chrome");
        var chrome = await launcher.RunAsync(new ProcessSpec("node", ["-e", "console.log(require('puppeteer').executablePath())"], files.AppDir), TimeSpan.FromSeconds(30), ct);
        var hasChrome = chrome.ExitCode == 0 && (File.Exists(chrome.Output.Trim()) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH")));
        if (!hasChrome)
        {
            Set(SidecarState.InstallingDependencies, "Downloading Chrome for the WhatsApp engine");
            var install = await launcher.RunAsync(new ProcessSpec(SidecarFiles.NpxExecutable, ["puppeteer", "browsers", "install", "chrome"], files.AppDir, LogPath: files.InstallLogPath),
                TimeSpan.FromMinutes(o.InstallTimeoutMinutes), ct);
            if (install.ExitCode != 0)
            {
                Set(SidecarState.Failed, reason: "Chrome is missing and could not be downloaded. Install Google Chrome or set PUPPETEER_EXECUTABLE_PATH, then restart Rasel.");
                return false;
            }
        }
        return true;
    }

    private async Task SuperviseAsync(CancellationToken stop)
    {
        var o = options.Value;
        var crashes = 0;
        while (!stop.IsCancellationRequested)
        {
            Task wait;
            lock (_gate)
            {
                if (_wantRunning) wait = Task.CompletedTask;
                else
                {
                    _status = new SidecarStatus(_hasRunOnce ? SidecarState.Stopped : SidecarState.Ready, null, _status.State == SidecarState.Failed ? _status.Reason : null, crashes, o.AutoStart);
                    if (_wake.Task.IsCompleted) _wake = NewWake();
                    wait = _wake.Task;
                }
            }
            await wait.WaitAsync(stop);

            CancellationTokenSource runCts;
            lock (_gate)
            {
                if (!_wantRunning) continue;
                _wake = NewWake();
                _restart = false;
                runCts = _run = CancellationTokenSource.CreateLinkedTokenSource(stop);
            }

            _hasRunOnce = true;
            var stable = await RunOnceAsync(runCts.Token, crashes);
            KillCurrent();
            lock (_gate) _run = null;
            runCts.Dispose();
            if (stop.IsCancellationRequested) return;

            bool restartNow, manualStop;
            lock (_gate) { restartNow = _restart; manualStop = !_wantRunning; }
            if (restartNow) { crashes = 0; continue; }
            if (manualStop) { Set(SidecarState.Stopped); crashes = 0; continue; }

            crashes = stable ? 1 : crashes + 1;
            if (crashes >= o.MaxCrashes)
            {
                lock (_gate) _wantRunning = false;
                Set(SidecarState.Failed, reason: $"The WhatsApp engine crashed {crashes} times in a row and was stopped to avoid a crash loop. Check the sidecar log in the data folder, then press Start.", crashes: crashes);
                await WaitForCommandAsync(stop);
                continue;
            }
            var delay = TimeSpan.FromMilliseconds(Math.Min(o.BackoffMaxMs, o.BackoffBaseMs * Math.Pow(2, crashes - 1)));
            Set(SidecarState.Degraded, reason: $"The WhatsApp engine stopped unexpectedly; restarting in {(int)Math.Ceiling(delay.TotalSeconds)}s (attempt {crashes}/{o.MaxCrashes}).", crashes: crashes);
            Task wake;
            lock (_gate) { if (_wake.Task.IsCompleted) _wake = NewWake(); wake = _wake.Task; }
            await Task.WhenAny(Task.Delay(delay, stop), wake); // Stop/Restart interrupt the back-off
        }
    }

    private async Task WaitForCommandAsync(CancellationToken stop)
    {
        Task wake;
        lock (_gate) { if (_wake.Task.IsCompleted) _wake = NewWake(); wake = _wake.Task; }
        await wake.WaitAsync(stop);
    }

    /// <summary>Runs one process until it exits, hangs, or the run token is cancelled. Returns true when it ran long enough to count as stable.</summary>
    private async Task<bool> RunOnceAsync(CancellationToken run, int crashesSoFar)
    {
        var o = options.Value;
        var port = o.Port != 0 ? o.Port : files.FreePort();
        var token = files.GetOrCreateToken();
        Set(SidecarState.Starting, "Starting the WhatsApp engine", crashes: crashesSoFar);
        var proc = launcher.Start(new ProcessSpec("node", ["src/server.js"], files.AppDir,
            new Dictionary<string, string> { ["PORT"] = port.ToString(), ["HOST"] = "127.0.0.1", ["SESSION_DIR"] = files.SessionDir, ["SHARED_TOKEN"] = token },
            files.SidecarLogPath));
        _proc = proc;
        var started = DateTimeOffset.UtcNow;
        bool Stable() => DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(o.StableSeconds);
        var misses = 0;
        var seenAlive = false;
        try
        {
            while (!run.IsCancellationRequested)
            {
                var tick = Task.Delay(o.HealthIntervalMs, run);
                if (await Task.WhenAny(proc.Exited, tick) == proc.Exited) return Stable();
                run.ThrowIfCancellationRequested();

                var p = await probe.ProbeAsync(port, run).ConfigureAwait(false);
                if (p.Alive)
                {
                    misses = 0;
                    if (!seenAlive) { seenAlive = true; lock (_gate) _endpoint = ($"http://127.0.0.1:{port}", token); }
                    Set(p.Ready ? SidecarState.Connected : SidecarState.NeedsQr);
                    continue;
                }
                misses++;
                if (seenAlive ? misses >= 5 : DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(o.StartTimeoutSeconds))
                    return false; // hung or never came up: the caller kills it and counts a crash
            }
        }
        catch (OperationCanceledException) { }
        return Stable();
    }
}

public sealed class SupervisorBridgeEndpoint(SidecarSupervisor supervisor) : IBridgeEndpoint
{
    public (string BaseUrl, string Token)? Current => supervisor.Current;
}

public sealed class OptionsBridgeEndpoint(IOptions<Rasel.Infrastructure.Configuration.WhatsAppOptions> options) : IBridgeEndpoint
{
    public (string BaseUrl, string Token)? Current => (options.Value.Bridge.BaseUrl.TrimEnd('/'), options.Value.Bridge.SharedToken);
}

/// <summary>Used when the sidecar is not managed (Fake or external Bridge).</summary>
public sealed class NullSidecarControl : ISidecarControl
{
    public SidecarStatus Status { get; } = new(SidecarState.Disabled);
    public SidecarCommandResult RequestStart() => SidecarCommandResult.NotManaged;
    public SidecarCommandResult RequestStop() => SidecarCommandResult.NotManaged;
    public SidecarCommandResult RequestRestart() => SidecarCommandResult.NotManaged;
}
