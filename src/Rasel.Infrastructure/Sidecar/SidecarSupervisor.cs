using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Enums;

namespace Rasel.Infrastructure.Sidecar;

/// <summary>Lists the sessions that must be re-adopted when the API starts (everything not stopped on purpose).</summary>
public interface ISessionCatalog
{
    Task<IReadOnlyList<Guid>> ListActiveSessionIdsAsync(CancellationToken ct);
}

/// <summary>
/// Prepares the bundled whatsapp-web.js sidecar once (copy, Node check, npm ci when needed, Chrome check) and then supervises ONE sidecar
/// child process PER Rasel session (Provider=Managed): own loopback port from Sidecar:PortRangeStart/End, own SESSION_DIR
/// (DataDir/sessions/id), own random token, own lock file and own state machine (start, health loop, restart on crash with back-off and a
/// crash-loop circuit; a manual Stop is never a crash). A crash or stop of one session never touches another. Sessions live in a
/// registry keyed by session id, limited by Sidecar:MaxSessions, and are re-adopted from the database at startup (same folders, so no new QR).
/// Single Rasel instance per DataDir (lock file).
/// </summary>
public sealed class SidecarSupervisor(
    IOptions<SidecarOptions> options, IProcessLauncher launcher, ISidecarProbe probe, SidecarFiles files,
    ISessionCatalog catalog, ILogger<SidecarSupervisor> logger) : BackgroundService, ISidecarControl, IBridgeEndpoint
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SessionWorker> _workers = [];
    private readonly TaskCompletionSource<bool> _engine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SidecarStatus _engineStatus = new(SidecarState.Preparing, "Waiting to start", AutoStart: options.Value.AutoStart);
    private FileStream? _lock;
    private CancellationToken _stop;
    private SidecarOptions Opts => options.Value;
    private SidecarFiles Files => files;
    private ILogger Log => logger;
    private IProcessLauncher Launcher => launcher;
    private ISidecarProbe Probe => probe;

    private void SetEngine(SidecarState s, string? step = null, string? reason = null)
    {
        lock (_gate) _engineStatus = new SidecarStatus(s, step, reason, 0, options.Value.AutoStart);
    }

    // ---- status ----
    public SidecarStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (!_engine.Task.IsCompletedSuccessfully || !_engine.Task.Result) return _engineStatus;
                if (_workers.Count == 0) return new SidecarStatus(SidecarState.Ready, "No session yet", AutoStart: options.Value.AutoStart);
                var all = _workers.Values.Select(w => w.Status).ToList();
                SidecarStatus? Pick(params SidecarState[] states) => all.FirstOrDefault(s => states.Contains(s.State));
                var best = Pick(SidecarState.Connected) ?? Pick(SidecarState.NeedsQr) ?? Pick(SidecarState.Starting, SidecarState.Preparing, SidecarState.InstallingDependencies)
                    ?? Pick(SidecarState.Degraded) ?? Pick(SidecarState.Failed) ?? Pick(SidecarState.Stopped) ?? all[0];
                return best with { Crashes = all.Max(s => s.Crashes), AutoStart = options.Value.AutoStart };
            }
        }
    }

    public IReadOnlyList<SidecarSessionStatus> Sessions
    {
        get { lock (_gate) return _workers.Values.OrderBy(w => w.Id).Select(w => new SidecarSessionStatus(w.Id, w.Status, w.Port)).ToList(); }
    }

    public (string BaseUrl, string Token)? Resolve(Guid sessionId)
    {
        lock (_gate) return _workers.TryGetValue(sessionId, out var w) ? w.Endpoint : null;
    }

    public SessionStatus? NotReadyStatus(Guid sessionId)
    {
        lock (_gate)
        {
            if (!_workers.TryGetValue(sessionId, out var w)) return SessionStatus.Stopped;
            return w.Status.State switch { SidecarState.Failed => SessionStatus.Failed, SidecarState.Stopped => SessionStatus.Stopped, _ => SessionStatus.Starting };
        }
    }

    // ---- commands (idempotent, safe under concurrency) ----
    private SidecarCommandResult ForTargets(Guid? sessionId, Func<SessionWorker, SidecarCommandResult> act)
    {
        List<SessionWorker> targets;
        lock (_gate)
        {
            if (sessionId is { } id)
            {
                if (!_workers.TryGetValue(id, out var one)) return SidecarCommandResult.SessionNotFound;
                targets = [one];
            }
            else targets = [.. _workers.Values];
        }
        var result = SidecarCommandResult.AlreadyInState;
        foreach (var w in targets) if (act(w) == SidecarCommandResult.Accepted) result = SidecarCommandResult.Accepted;
        return result;
    }

    public SidecarCommandResult RequestStart(Guid? sessionId) => ForTargets(sessionId, w => w.RequestStart());
    public SidecarCommandResult RequestStop(Guid? sessionId) => ForTargets(sessionId, w => w.RequestStop());
    public SidecarCommandResult RequestRestart(Guid? sessionId) => ForTargets(sessionId, w => w.RequestRestart());

    // ---- session registry ----
    public Task EnsureSessionAsync(Guid sessionId, CancellationToken ct)
    {
        Adopt(sessionId, autoStart: true);
        return Task.CompletedTask;
    }

    private void Adopt(Guid id, bool autoStart)
    {
        lock (_gate)
        {
            if (_workers.ContainsKey(id)) return;
            if (_workers.Count >= options.Value.MaxSessions)
                throw new AppException(ErrorKind.Conflict, "max_sessions_reached",
                    $"This Rasel installation already runs {options.Value.MaxSessions} WhatsApp sessions (Sidecar:MaxSessions). Delete one first or raise the limit if the server has enough memory.");
            var w = new SessionWorker(this, id, autoStart);
            _workers[id] = w;
            w.Begin(_stop);
        }
    }

    /// <summary>Stops the session's process, frees its slot and port; with <paramref name="purge"/> also deletes its folder once the process is dead.</summary>
    public async Task StopSessionAsync(Guid sessionId, bool purge, CancellationToken ct)
    {
        SessionWorker? w;
        lock (_gate) { _workers.Remove(sessionId, out w); }
        if (w is not null) await w.ShutdownAsync();
        if (!purge) return;
        for (var i = 0; i < 20; i++)
        {
            if (files.DeleteSessionData(sessionId)) return;
            await Task.Delay(250, ct); // Chrome may still be releasing its files
        }
        throw new AppException(ErrorKind.Conflict, "purge_incomplete", "The session was stopped but its data folder could not be fully deleted. Close anything using it and delete the session again with purge=true.");
    }

    // ---- main loop ----
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _stop = stop;
        try
        {
            if (!files.TryAcquireLock(out _lock))
            {
                SetEngine(SidecarState.Failed, reason: "Another Rasel instance already supervises this data folder (Sidecar:DataDir). Only one may run the sidecar.");
                _engine.TrySetResult(false);
                await Task.Delay(Timeout.Infinite, stop); // stay up so sessions created later report Failed instead of being torn down
            }
            if (!await PrepareAsync(stop)) { _engine.TrySetResult(false); await Task.Delay(Timeout.Infinite, stop); }
            _engine.TrySetResult(true);
            await AdoptExistingAsync(stop);
            await Task.Delay(Timeout.Infinite, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sidecar supervisor crashed");
            SetEngine(SidecarState.Failed, reason: "The supervisor hit an unexpected error; see the Rasel log.");
            _engine.TrySetResult(false);
        }
        finally
        {
            SessionWorker[] all;
            lock (_gate) { all = [.. _workers.Values]; }
            foreach (var w in all)
            {
                try { await w.ShutdownAsync(); }
                catch (Exception ex) { logger.LogWarning(ex, "Session worker shutdown failed"); }
            }
            _lock?.Dispose();
        }
    }

    private async Task AdoptExistingAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> ids;
        try { ids = await catalog.ListActiveSessionIdsAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not list sessions to re-adopt");
            return;
        }
        foreach (var id in ids)
        {
            try { Adopt(id, options.Value.AutoStart); }
            catch (AppException) { logger.LogWarning("Session {SessionId} not re-adopted: Sidecar:MaxSessions reached", id); break; }
        }
    }

    private async Task<bool> PrepareAsync(CancellationToken ct)
    {
        var o = options.Value;
        SetEngine(SidecarState.Preparing, "Copying the WhatsApp engine files");
        var copyError = files.PrepareFolders();
        if (copyError is not null) { SetEngine(SidecarState.Failed, reason: copyError); return false; }

        SetEngine(SidecarState.Preparing, "Checking Node.js");
        var node = await launcher.RunAsync(new ProcessSpec(SidecarFiles.NodeExecutable, ["--version"], files.AppDir), TimeSpan.FromSeconds(20), ct);
        if (node.ExitCode != 0 || !SidecarFiles.TryParseNodeMajor(node.Output, out var major))
        {
            SetEngine(SidecarState.Failed, reason: $"Node.js {o.MinNodeMajor} or newer was not found. Install Node.js (LTS) on this server and restart Rasel.");
            return false;
        }
        if (major < o.MinNodeMajor)
        {
            SetEngine(SidecarState.Failed, reason: $"Node.js {major} is too old; version {o.MinNodeMajor} or newer is required. Upgrade Node.js and restart Rasel.");
            return false;
        }

        if (files.NeedsInstall())
        {
            SetEngine(SidecarState.InstallingDependencies, "Installing WhatsApp engine dependencies (1-3 minutes the first time)");
            var npm = await launcher.RunAsync(new ProcessSpec(SidecarFiles.NpmExecutable, ["ci", "--no-audit", "--no-fund"], files.AppDir, files.ToolEnv, files.InstallLogPath),
                TimeSpan.FromMinutes(o.InstallTimeoutMinutes), ct);
            if (npm.ExitCode != 0)
            {
                SetEngine(SidecarState.Failed, reason: $"Installing dependencies failed (npm exit code {npm.ExitCode}). Check access to the npm registry and the install log in the data folder, then restart Rasel.");
                return false;
            }
            files.MarkInstalled();
        }

        SetEngine(SidecarState.Preparing, "Checking Chrome");
        var chrome = await launcher.RunAsync(new ProcessSpec(SidecarFiles.NodeExecutable, ["-e", "console.log(require('puppeteer').executablePath())"], files.AppDir, files.ToolEnv), TimeSpan.FromSeconds(30), ct);
        var hasChrome = chrome.ExitCode == 0 && (File.Exists(chrome.Output.Trim()) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH")));
        if (!hasChrome)
        {
            SetEngine(SidecarState.InstallingDependencies, "Downloading Chrome for the WhatsApp engine");
            var install = await launcher.RunAsync(new ProcessSpec(SidecarFiles.NpxExecutable, ["puppeteer", "browsers", "install", "chrome"], files.AppDir, files.ToolEnv, files.InstallLogPath),
                TimeSpan.FromMinutes(o.InstallTimeoutMinutes), ct);
            if (install.ExitCode != 0)
            {
                SetEngine(SidecarState.Failed, reason: "Chrome is missing and could not be downloaded. Install Google Chrome or set PUPPETEER_EXECUTABLE_PATH, then restart Rasel.");
                return false;
            }
        }
        return true;
    }

    /// <summary>Next loopback port for a session: from the configured range (never 5001, never one assigned to another session, never one already listening) or an OS-chosen free port.</summary>
    private int AllocatePort(SessionWorker who)
    {
        var o = options.Value;
        lock (_gate)
        {
            var used = _workers.Values.Where(w => w != who && w.Port is not null).Select(w => w.Port!.Value).ToHashSet();
            if (o.PortRangeStart == 0)
            {
                for (var i = 0; i < 20; i++) { var p = files.FreePort(); if (p != 5001 && !used.Contains(p)) return p; }
                throw new InvalidOperationException("no free port");
            }
            for (var p = o.PortRangeStart; p <= o.PortRangeEnd; p++)
                if (p != 5001 && !used.Contains(p) && files.IsPortFree(p)) return p;
            throw new InvalidOperationException("no free port in range");
        }
    }

    /// <summary>One session's engine: its own state machine and process. Independent of every other worker.</summary>
    private sealed class SessionWorker(SidecarSupervisor sup, Guid id, bool autoStartOnBegin)
    {
        private readonly object _w = new();
        private SidecarStatus _status = new(SidecarState.Preparing, "Waiting for the WhatsApp engine files", AutoStart: autoStartOnBegin);
        private readonly bool _autoStart = autoStartOnBegin;
        private bool _wantRunning = autoStartOnBegin;
        private bool _restart;
        private bool _hasRunOnce;
        private CancellationTokenSource? _run;
        private CancellationTokenSource? _life;
        private Task _loop = Task.CompletedTask;
        private TaskCompletionSource _wake = NewWake();
        private (string, string)? _endpoint;
        private int? _port;
        private IManagedProcess? _proc;
        private FileStream? _lock;

        public Guid Id { get; } = id;
        public SidecarStatus Status { get { lock (_w) return _status; } }
        public (string, string)? Endpoint { get { lock (_w) return _endpoint; } }
        public int? Port { get { lock (_w) return _port; } }

        private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Set(SidecarState s, string? step = null, string? reason = null, int crashes = 0)
        {
            lock (_w) _status = new SidecarStatus(s, step, reason, crashes, _autoStart);
        }

        public void Begin(CancellationToken supervisorStop)
        {
            _life = CancellationTokenSource.CreateLinkedTokenSource(supervisorStop);
            var token = _life.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }

        /// <summary>Ends the loop, kills the process tree and releases the folder lock. Returns when the process is dead.</summary>
        public async Task ShutdownAsync()
        {
            try { _life?.Cancel(); } catch (ObjectDisposedException) { }
            try { await _loop; } catch (OperationCanceledException) { }
        }

        public SidecarCommandResult RequestStart()
        {
            lock (_w)
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
            lock (_w)
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
            lock (_w)
            {
                _wantRunning = true;
                _restart = true;
                _run?.Cancel();
                _wake.TrySetResult();
                return SidecarCommandResult.Accepted;
            }
        }

        private async Task LoopAsync(CancellationToken stop)
        {
            try
            {
                if (!await sup._engine.Task.WaitAsync(stop))
                {
                    Set(SidecarState.Failed, reason: sup.Status.Reason ?? "The WhatsApp engine could not be prepared; see the Rasel log.");
                    return;
                }
                if (!sup.Files.TryAcquireSessionLock(Id, out _lock))
                {
                    Set(SidecarState.Failed, reason: "This session's data folder is in use by another process.");
                    return;
                }
                await SuperviseAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                sup.Log.LogError(ex, "Session {SessionId} supervisor crashed", Id);
                Set(SidecarState.Failed, reason: "The supervisor hit an unexpected error for this session; see the Rasel log.");
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
            lock (_w) { _endpoint = null; _port = null; }
            try { if (p is { HasExited: false }) p.KillTree(); }
            catch (Exception ex) { sup.Log.LogWarning(ex, "Could not kill the sidecar process tree of session {SessionId}", Id); }
        }

        private async Task SuperviseAsync(CancellationToken stop)
        {
            var o = sup.Opts;
            var crashes = 0;
            while (!stop.IsCancellationRequested)
            {
                Task wait;
                lock (_w)
                {
                    if (_wantRunning) wait = Task.CompletedTask;
                    else
                    {
                        _status = new SidecarStatus(_hasRunOnce ? SidecarState.Stopped : SidecarState.Ready, null, _status.State == SidecarState.Failed ? _status.Reason : null, crashes, _autoStart);
                        if (_wake.Task.IsCompleted) _wake = NewWake();
                        wait = _wake.Task;
                    }
                }
                await wait.WaitAsync(stop);

                CancellationTokenSource runCts;
                lock (_w)
                {
                    if (!_wantRunning) continue;
                    _wake = NewWake();
                    _restart = false;
                    runCts = _run = CancellationTokenSource.CreateLinkedTokenSource(stop);
                }

                _hasRunOnce = true;
                var stable = await RunOnceAsync(runCts.Token, crashes);
                KillCurrent();
                lock (_w) _run = null;
                runCts.Dispose();
                if (stop.IsCancellationRequested) return;

                bool restartNow, manualStop;
                lock (_w) { restartNow = _restart; manualStop = !_wantRunning; }
                if (restartNow) { crashes = 0; continue; }
                if (manualStop) { if (Status.State != SidecarState.Failed) Set(SidecarState.Stopped); crashes = 0; continue; }

                crashes = stable ? 1 : crashes + 1;
                if (crashes >= o.MaxCrashes)
                {
                    lock (_w) _wantRunning = false;
                    Set(SidecarState.Failed, reason: $"The WhatsApp engine crashed {crashes} times in a row and was stopped to avoid a crash loop. Check the sidecar log in the data folder, then press Start.", crashes: crashes);
                    await WaitForCommandAsync(stop);
                    continue;
                }
                var delay = TimeSpan.FromMilliseconds(Math.Min(o.BackoffMaxMs, o.BackoffBaseMs * Math.Pow(2, crashes - 1)));
                Set(SidecarState.Degraded, reason: $"The WhatsApp engine stopped unexpectedly; restarting in {(int)Math.Ceiling(delay.TotalSeconds)}s (attempt {crashes}/{o.MaxCrashes}).", crashes: crashes);
                Task wake;
                lock (_w) { if (_wake.Task.IsCompleted) _wake = NewWake(); wake = _wake.Task; }
                await Task.WhenAny(Task.Delay(delay, stop), wake); // Stop/Restart interrupt the back-off
            }
        }

        private async Task WaitForCommandAsync(CancellationToken stop)
        {
            Task wake;
            lock (_w) { if (_wake.Task.IsCompleted) _wake = NewWake(); wake = _wake.Task; }
            await wake.WaitAsync(stop);
        }

        /// <summary>Runs one process until it exits, hangs, or the run token is cancelled. Returns true when it ran long enough to count as stable.</summary>
        private async Task<bool> RunOnceAsync(CancellationToken run, int crashesSoFar)
        {
            var o = sup.Opts;
            var files = sup.Files;
            int port;
            try { port = sup.AllocatePort(this); }
            catch (InvalidOperationException)
            {
                Set(SidecarState.Failed, reason: "No free port is available for this session. Widen Sidecar:PortRangeStart/End or free a port, then press Start.", crashes: crashesSoFar);
                lock (_w) _wantRunning = false;
                return false;
            }
            lock (_w) _port = port;
            var token = files.GetOrCreateToken(Id);
            Set(SidecarState.Starting, "Starting the WhatsApp engine", crashes: crashesSoFar);
            var proc = sup.Launcher.Start(new ProcessSpec(SidecarFiles.NodeExecutable, ["src/server.js"], files.AppDir,
                new Dictionary<string, string>(files.ToolEnv) { ["PORT"] = port.ToString(), ["HOST"] = "127.0.0.1", ["SESSION_DIR"] = files.SessionDir(Id), ["SHARED_TOKEN"] = token },
                files.SidecarLogPath(Id)));
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

                    var p = await sup.Probe.ProbeAsync(port, run).ConfigureAwait(false);
                    if (p.Alive)
                    {
                        misses = 0;
                        if (!seenAlive) { seenAlive = true; lock (_w) _endpoint = ($"http://127.0.0.1:{port}", token); }
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
}

public sealed class OptionsBridgeEndpoint(IOptions<Rasel.Infrastructure.Configuration.WhatsAppOptions> options) : IBridgeEndpoint
{
    public (string BaseUrl, string Token)? Resolve(Guid sessionId) => (options.Value.Bridge.BaseUrl.TrimEnd('/'), options.Value.Bridge.SharedToken);
}

/// <summary>Used when the sidecar is not managed (Fake or external Bridge).</summary>
public sealed class NullSidecarControl : ISidecarControl
{
    public SidecarStatus Status { get; } = new(SidecarState.Disabled);
    public IReadOnlyList<SidecarSessionStatus> Sessions { get; } = [];
    public SidecarCommandResult RequestStart(Guid? sessionId) => SidecarCommandResult.NotManaged;
    public SidecarCommandResult RequestStop(Guid? sessionId) => SidecarCommandResult.NotManaged;
    public SidecarCommandResult RequestRestart(Guid? sessionId) => SidecarCommandResult.NotManaged;
}

/// <summary>Reads the sessions to re-adopt from the database (scoped repository used from the singleton supervisor).</summary>
public sealed class DbSessionCatalog(IServiceProvider services) : ISessionCatalog
{
    public async Task<IReadOnlyList<Guid>> ListActiveSessionIdsAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        return (await repo.ListActiveAsync(ct)).Select(s => s.Id).ToList();
    }
}

public sealed class SupervisorBridgeEndpoint(SidecarSupervisor supervisor) : IBridgeEndpoint
{
    public (string BaseUrl, string Token)? Resolve(Guid sessionId) => supervisor.Resolve(sessionId);
    public Task EnsureSessionAsync(Guid sessionId, CancellationToken ct) => supervisor.EnsureSessionAsync(sessionId, ct);
    public Task StopSessionAsync(Guid sessionId, bool purge, CancellationToken ct) => supervisor.StopSessionAsync(sessionId, purge, ct);
    public SessionStatus? NotReadyStatus(Guid sessionId) => supervisor.NotReadyStatus(sessionId);
}
