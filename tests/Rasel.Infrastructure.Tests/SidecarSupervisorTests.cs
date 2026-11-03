using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Sidecar;

namespace Rasel.Infrastructure.Tests;

public class SidecarSupervisorTests : IDisposable
{
    private sealed class FakeProcess : IManagedProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id { get; init; }
        public bool HasExited => _exit.Task.IsCompleted;
        public Task<int> Exited => _exit.Task;
        public bool Killed { get; private set; }
        public IReadOnlyDictionary<string, string>? Env { get; init; }
        public void KillTree() { Killed = true; _exit.TrySetResult(-1); }
        public void Crash(int code = 1) => _exit.TrySetResult(code);
    }

    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<FakeProcess> Started { get; } = [];
        public List<string> Runs { get; } = [];
        public string NodeVersion { get; set; } = "v22.1.0";
        public int NpmExit { get; set; }
        public string ChromePath { get; set; } = "";
        private int _id;

        public IManagedProcess Start(ProcessSpec spec)
        {
            var p = new FakeProcess { Id = Interlocked.Increment(ref _id), Env = spec.Env };
            lock (Started) Started.Add(p);
            return p;
        }

        public Task<(int ExitCode, string Output)> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
        {
            var cmd = spec.FileName + " " + string.Join(' ', spec.Args);
            lock (Runs) Runs.Add(cmd);
            if (spec.Args.Count > 0 && spec.Args[0] == "--version") return Task.FromResult((NodeVersion.Length == 0 ? 1 : 0, NodeVersion));
            if (spec.Args.Count > 0 && spec.Args[0] == "ci") return Task.FromResult((NpmExit, ""));
            if (spec.Args.Count > 0 && spec.Args[0] == "-e") return Task.FromResult((0, ChromePath));
            return Task.FromResult((0, ""));
        }

        public FakeProcess Last { get { lock (Started) return Started[^1]; } }
        public int StartCount { get { lock (Started) return Started.Count; } }
        public int LiveCount { get { lock (Started) return Started.Count(p => !p.HasExited); } }
        public int CiRuns { get { lock (Runs) return Runs.Count(r => r.Contains(" ci ")); } }
    }

    private sealed class FakeProbe : ISidecarProbe
    {
        public volatile bool Alive = true;
        public volatile bool Ready;
        public System.Collections.Concurrent.ConcurrentDictionary<int, bool> DeadPorts { get; } = new();
        public Task<SidecarProbe> ProbeAsync(int port, CancellationToken ct) => Task.FromResult(new SidecarProbe(Alive && !DeadPorts.ContainsKey(port), Ready));
    }

    private sealed class FakeCatalog : ISessionCatalog
    {
        public List<Guid> Ids { get; } = [];
        public Task<IReadOnlyList<Guid>> ListActiveSessionIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([.. Ids]);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rasel-sup-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _chrome;

    public SidecarSupervisorTests()
    {
        _source = Path.Combine(_root, "src");
        Directory.CreateDirectory(_source);
        File.WriteAllText(Path.Combine(_source, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_source, "package-lock.json"), "{\"v\":1}");
        _chrome = Path.Combine(_root, "chrome.exe");
        File.WriteAllText(_chrome, "x");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static int _nextRange = 5821;
    private FakeCatalog _catalog = new();

    private (SidecarSupervisor Sup, FakeLauncher Launcher, FakeProbe Probe, SidecarOptions Opt) Build(Action<SidecarOptions>? tweak = null, FakeLauncher? launcher = null, string data = "data", FakeCatalog? catalog = null)
    {
        var opt = new SidecarOptions { DataDir = Path.Combine(_root, data), SourceDir = _source, HealthIntervalMs = 20, BackoffBaseMs = 30, BackoffMaxMs = 200, MaxCrashes = 3, StableSeconds = 3600, MaxSessions = 3, PortRangeStart = Interlocked.Add(ref _nextRange, 10), };
        opt.PortRangeEnd = opt.PortRangeStart + 8;
        tweak?.Invoke(opt);
        launcher ??= new FakeLauncher { ChromePath = _chrome };
        var probe = new FakeProbe();
        var o = Options.Create(opt);
        return (new SidecarSupervisor(o, launcher, probe, new SidecarFiles(o), catalog ?? (_catalog = new FakeCatalog()), NullLogger<SidecarSupervisor>.Instance), launcher, probe, opt);
    }

    private static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException("condition not reached"); await Task.Delay(10); }
    }

    private static async Task Run(SidecarSupervisor sup, Func<Task> body)
    {
        await sup.StartAsync(default);
        try { await body(); }
        finally { await sup.StopAsync(default); }
    }

    private static async Task<Guid> Open(SidecarSupervisor sup)
    {
        var id = Guid.NewGuid();
        await sup.EnsureSessionAsync(id, default);
        return id;
    }

    private static SidecarState State(SidecarSupervisor sup, Guid id) => sup.Sessions.Single(x => x.SessionId == id).Status.State;
    private static SidecarStatus Stat(SidecarSupervisor sup, Guid id) => sup.Sessions.Single(x => x.SessionId == id).Status;
    private static int PortOf(SidecarSupervisor sup, Guid id) => sup.Sessions.Single(x => x.SessionId == id).Port!.Value;

    [Fact]
    public async Task Prepares_then_starts_and_walks_NeedsQr_then_Connected_with_its_own_token_port_and_folder()
    {
        var (sup, l, probe, opt) = Build();
        await Run(sup, async () =>
        {
            var id = await Open(sup);
            await Until(() => State(sup, id) == SidecarState.NeedsQr);
            Assert.Equal(1, l.CiRuns);
            var env = l.Last.Env!;
            Assert.Equal("127.0.0.1", env["HOST"]);
            Assert.True(env["SHARED_TOKEN"].Length >= 32);
            Assert.Equal(env["SHARED_TOKEN"], sup.Resolve(id)!.Value.Token);
            Assert.InRange(int.Parse(env["PORT"]), opt.PortRangeStart, opt.PortRangeEnd);
            Assert.Equal(Path.Combine(Path.GetFullPath(opt.DataDir), "sessions", id.ToString("D")), env["SESSION_DIR"]);
            probe.Ready = true;
            await Until(() => State(sup, id) == SidecarState.Connected);
            Assert.Equal(SidecarState.Connected, sup.Status.State);
        });
        Assert.True(l.Last.Killed); // killed on API shutdown
    }

    [Fact]
    public async Task Npm_ci_runs_only_when_node_modules_is_missing_or_the_lockfile_changed()
    {
        var l = new FakeLauncher { ChromePath = _chrome };
        var (sup, _, _, opt) = Build(launcher: l);
        await Run(sup, async () => { var id = await Open(sup); await Until(() => State(sup, id) == SidecarState.NeedsQr); });
        Assert.Equal(1, l.CiRuns);

        Directory.CreateDirectory(Path.Combine(opt.DataDir, "app", "node_modules")); // what npm ci would have produced
        var (sup2, _, _, _) = Build(launcher: l);
        await Run(sup2, async () => { var id = await Open(sup2); await Until(() => State(sup2, id) == SidecarState.NeedsQr); });
        Assert.Equal(1, l.CiRuns); // unchanged lockfile: no reinstall

        File.WriteAllText(Path.Combine(_source, "package-lock.json"), "{\"v\":2}");
        var (sup3, _, _, _) = Build(launcher: l);
        await Run(sup3, async () => { var id = await Open(sup3); await Until(() => State(sup3, id) == SidecarState.NeedsQr); });
        Assert.Equal(2, l.CiRuns);
    }

    [Fact]
    public async Task Missing_or_old_node_and_failed_install_give_a_clear_Failed_status_for_the_engine_and_its_sessions()
    {
        var (sup, _, _, _) = Build(launcher: new FakeLauncher { NodeVersion = "" });
        await Run(sup, async () => { var id = await Open(sup); await Until(() => sup.Status.State == SidecarState.Failed); Assert.Contains("Node.js", sup.Status.Reason); await Until(() => State(sup, id) == SidecarState.Failed); });

        var (old, _, _, _) = Build(launcher: new FakeLauncher { NodeVersion = "v16.0.0" }, data: "d2");
        await Run(old, async () => { await Until(() => old.Status.State == SidecarState.Failed); Assert.Contains("too old", old.Status.Reason); });

        var (bad, _, _, _) = Build(launcher: new FakeLauncher { NpmExit = 1 }, data: "d3");
        await Run(bad, async () => { await Until(() => bad.Status.State == SidecarState.Failed); Assert.Contains("Installing dependencies failed", bad.Status.Reason); });
    }

    [Fact]
    public async Task Crash_restarts_with_backoff_and_a_crash_loop_stops_at_MaxCrashes_until_Start()
    {
        var (sup, l, _, _) = Build();
        await Run(sup, async () =>
        {
            var id = await Open(sup);
            await Until(() => State(sup, id) == SidecarState.NeedsQr);
            l.Last.Crash();
            await Until(() => l.StartCount == 2);
            l.Last.Crash();
            await Until(() => l.StartCount == 3);
            l.Last.Crash();
            await Until(() => State(sup, id) == SidecarState.Failed);
            Assert.Contains("crash loop", Stat(sup, id).Reason);
            await Task.Delay(200);
            Assert.Equal(3, l.StartCount); // circuit open: no 4th start

            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStart(id)); // a human Start re-arms it
            await Until(() => l.StartCount == 4);
        });
    }

    [Fact]
    public async Task Manual_stop_is_not_a_crash_and_is_not_auto_restarted_and_start_resumes()
    {
        var (sup, l, _, _) = Build();
        await Run(sup, async () =>
        {
            var id = await Open(sup);
            await Until(() => State(sup, id) == SidecarState.NeedsQr);
            var first = l.Last;
            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStop(id));
            await Until(() => State(sup, id) == SidecarState.Stopped);
            Assert.True(first.Killed);
            Assert.Equal(0, Stat(sup, id).Crashes);
            await Task.Delay(200);
            Assert.Equal(1, l.StartCount);
            Assert.Equal(SidecarCommandResult.AlreadyInState, sup.RequestStop(id)); // idempotent
            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStart(id));
            await Until(() => l.StartCount == 2);
            await Until(() => State(sup, id) == SidecarState.NeedsQr);
            Assert.Equal(SidecarCommandResult.AlreadyInState, sup.RequestStart(id));
            Assert.Equal(SidecarCommandResult.SessionNotFound, sup.RequestStart(Guid.NewGuid()));
        });
    }

    [Fact]
    public async Task Adopted_sessions_with_AutoStart_false_wait_in_Ready_until_Start()
    {
        var cat = new FakeCatalog();
        var id = Guid.NewGuid();
        cat.Ids.Add(id);
        var (sup, l, _, _) = Build(o => o.AutoStart = false, catalog: cat);
        await Run(sup, async () =>
        {
            await Until(() => sup.Sessions.Count == 1 && State(sup, id) == SidecarState.Ready);
            await Task.Delay(150);
            Assert.Equal(0, l.StartCount);
            Assert.False(Stat(sup, id).AutoStart);
            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStart(null)); // all
            await Until(() => State(sup, id) == SidecarState.NeedsQr);
            Assert.Equal(1, l.StartCount);
        });
    }

    [Fact]
    public async Task Restart_replaces_the_process_without_counting_a_crash_and_concurrent_requests_are_safe()
    {
        var (sup, l, _, _) = Build();
        await Run(sup, async () =>
        {
            var id = await Open(sup);
            await Until(() => State(sup, id) == SidecarState.NeedsQr);
            var first = l.Last;
            await Task.WhenAll(Enumerable.Range(0, 9).Select(i => Task.Run(() => (i % 3) switch { 0 => sup.RequestRestart(id), 1 => sup.RequestStart(id), _ => sup.RequestRestart(id) })));
            await Until(() => l.StartCount >= 2 && State(sup, id) == SidecarState.NeedsQr);
            await Task.Delay(150);
            Assert.True(first.Killed);
            Assert.Equal(0, Stat(sup, id).Crashes);
            Assert.Equal(1, l.LiveCount); // exactly one live process, no orphan
        });
        Assert.Equal(0, l.LiveCount);
    }

    [Fact]
    public async Task Second_instance_on_the_same_data_dir_does_not_spawn_a_second_sidecar()
    {
        var (a, _, _, _) = Build();
        var (b, lb, _, _) = Build();
        await Run(a, async () =>
        {
            var ida = await Open(a);
            await Until(() => State(a, ida) == SidecarState.NeedsQr);
            await Run(b, async () => { await Until(() => b.Status.State == SidecarState.Failed); Assert.Contains("Another Rasel instance", b.Status.Reason); });
            Assert.Equal(0, lb.StartCount);
        });
    }

    [Fact]
    public async Task A_hung_sidecar_that_stops_answering_is_killed_and_restarted()
    {
        var (sup, l, probe, _) = Build();
        await Run(sup, async () =>
        {
            var id = await Open(sup);
            await Until(() => State(sup, id) == SidecarState.NeedsQr);
            var first = l.Last;
            probe.Alive = false;
            await Until(() => first.Killed);
            probe.Alive = true;
            await Until(() => l.StartCount == 2);
        });
    }

    [Fact]
    public async Task Two_sessions_get_their_own_port_folder_token_and_endpoint()
    {
        var (sup, l, _, opt) = Build();
        await Run(sup, async () =>
        {
            var a = await Open(sup);
            var b = await Open(sup);
            await Until(() => State(sup, a) == SidecarState.NeedsQr && State(sup, b) == SidecarState.NeedsQr);
            Assert.Equal(2, l.StartCount);
            var envs = l.Started.Select(p => p.Env!).ToList();
            Assert.NotEqual(envs[0]["PORT"], envs[1]["PORT"]);
            Assert.NotEqual(envs[0]["SESSION_DIR"], envs[1]["SESSION_DIR"]);
            Assert.NotEqual(envs[0]["SHARED_TOKEN"], envs[1]["SHARED_TOKEN"]);
            Assert.All(envs, e => Assert.NotEqual("5001", e["PORT"]));
            Assert.NotEqual(PortOf(sup, a), PortOf(sup, b));
            Assert.NotEqual(sup.Resolve(a)!.Value.BaseUrl, sup.Resolve(b)!.Value.BaseUrl); // routing: each session resolves to its own sidecar
            Assert.NotEqual(sup.Resolve(a)!.Value.Token, sup.Resolve(b)!.Value.Token);
            Assert.True(Directory.Exists(Path.Combine(opt.DataDir, "sessions", a.ToString("D"))));
            Assert.Null(sup.Resolve(Guid.NewGuid()));
        });
    }

    [Fact]
    public async Task Port_allocation_skips_a_port_that_is_already_listening()
    {
        var (sup, l, _, opt) = Build();
        using var busy = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, opt.PortRangeStart);
        busy.Start();
        await Run(sup, async () =>
        {
            var id = await Open(sup);
            await Until(() => State(sup, id) == SidecarState.NeedsQr);
            Assert.Equal((opt.PortRangeStart + 1).ToString(), l.Last.Env!["PORT"]);
        });
    }

    [Fact]
    public async Task One_sidecar_crashing_does_not_affect_the_other_session()
    {
        var (sup, l, _, _) = Build();
        await Run(sup, async () =>
        {
            var a = await Open(sup);
            var b = await Open(sup);
            await Until(() => State(sup, a) == SidecarState.NeedsQr && State(sup, b) == SidecarState.NeedsQr);
            var procA = l.Started.First(p => p.Env!["PORT"] == PortOf(sup, a).ToString());
            var procB = l.Started.First(p => p.Env!["PORT"] == PortOf(sup, b).ToString());
            var urlB = sup.Resolve(b)!.Value.BaseUrl;
            procA.Crash();
            await Until(() => l.StartCount == 3); // A restarted
            Assert.False(procB.HasExited);
            Assert.False(procB.Killed);
            Assert.Equal(0, Stat(sup, b).Crashes);
            Assert.Equal(urlB, sup.Resolve(b)!.Value.BaseUrl);
        });
    }

    [Fact]
    public async Task Stopping_one_session_stops_only_that_process_and_keeps_its_data_and_frees_the_slot()
    {
        var (sup, l, _, opt) = Build();
        await Run(sup, async () =>
        {
            var a = await Open(sup);
            await Open(sup);
            await Open(sup);
            await Until(() => sup.Sessions.All(x => x.Status.State == SidecarState.NeedsQr));
            var ex = await Assert.ThrowsAsync<Rasel.Application.Common.AppException>(() => sup.EnsureSessionAsync(Guid.NewGuid(), default)); // MaxSessions=3
            Assert.Equal("max_sessions_reached", ex.Code);
            var procA = l.Started.First(p => p.Env!["PORT"] == PortOf(sup, a).ToString());
            var folderA = Path.Combine(opt.DataDir, "sessions", a.ToString("D"));

            await sup.StopSessionAsync(a, purge: false, default);
            Assert.True(procA.Killed);
            Assert.Equal(2, l.LiveCount);
            Assert.True(Directory.Exists(folderA)); // data kept
            Assert.Equal(2, sup.Sessions.Count);
            await sup.EnsureSessionAsync(Guid.NewGuid(), default); // slot is free again
        });
    }

    [Fact]
    public async Task Purge_deletes_the_session_folder_only_after_the_process_is_dead()
    {
        var (sup, l, _, opt) = Build();
        await Run(sup, async () =>
        {
            var a = await Open(sup);
            var b = await Open(sup);
            await Until(() => sup.Sessions.All(x => x.Status.State == SidecarState.NeedsQr));
            var procA = l.Started.First(p => p.Env!["PORT"] == PortOf(sup, a).ToString());
            var folderA = Path.Combine(opt.DataDir, "sessions", a.ToString("D"));
            var folderB = Path.Combine(opt.DataDir, "sessions", b.ToString("D"));
            await sup.StopSessionAsync(a, purge: true, default);
            Assert.True(procA.HasExited);
            Assert.False(Directory.Exists(folderA));
            Assert.True(Directory.Exists(folderB));
        });
    }

    [Fact]
    public async Task Sessions_are_re_adopted_after_a_restart_with_the_same_folder_and_token()
    {
        var launcher = new FakeLauncher { ChromePath = _chrome };
        var (first, _, _, opt) = Build(launcher: launcher);
        Guid a = default, b = default;
        string tokenA = "", dirA = "";
        await Run(first, async () =>
        {
            a = await Open(first);
            b = await Open(first);
            await Until(() => first.Sessions.All(x => x.Status.State == SidecarState.NeedsQr));
            var env = launcher.Started.First(p => p.Env!["PORT"] == PortOf(first, a).ToString()).Env!;
            tokenA = env["SHARED_TOKEN"]; dirA = env["SESSION_DIR"];
        });
        Assert.Equal(0, launcher.LiveCount); // nothing left running after shutdown

        var cat = new FakeCatalog();
        cat.Ids.AddRange([a, b]);
        var launcher2 = new FakeLauncher { ChromePath = _chrome };
        var o2 = Options.Create(new SidecarOptions { DataDir = opt.DataDir, SourceDir = _source, HealthIntervalMs = 20, BackoffBaseMs = 30, BackoffMaxMs = 200, MaxCrashes = 3, StableSeconds = 3600, MaxSessions = 3, PortRangeStart = opt.PortRangeStart, PortRangeEnd = opt.PortRangeEnd });
        var sup2 = new SidecarSupervisor(o2, launcher2, new FakeProbe(), new SidecarFiles(o2), cat, NullLogger<SidecarSupervisor>.Instance);
        await Run(sup2, async () =>
        {
            await Until(() => sup2.Sessions.Count == 2 && sup2.Sessions.All(x => x.Status.State == SidecarState.NeedsQr));
            Assert.Contains(sup2.Sessions, x => x.SessionId == a);
            var envA = launcher2.Started.First(p => p.Env!["SESSION_DIR"] == dirA).Env!;
            Assert.Equal(tokenA, envA["SHARED_TOKEN"]); // same folder and secret: the linked number reconnects without a QR
        });
        Assert.Equal(0, launcher2.LiveCount);
    }

    [Fact]
    public async Task Commands_without_a_target_apply_to_all_and_with_one_apply_to_that_one_only()
    {
        var (sup, l, _, _) = Build();
        await Run(sup, async () =>
        {
            var a = await Open(sup);
            var b = await Open(sup);
            await Until(() => sup.Sessions.All(x => x.Status.State == SidecarState.NeedsQr));
            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStop(a));
            await Until(() => State(sup, a) == SidecarState.Stopped);
            Assert.Equal(SidecarState.NeedsQr, State(sup, b));
            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStop(null));
            await Until(() => State(sup, b) == SidecarState.Stopped);
            Assert.Equal(0, l.LiveCount);
        });
    }

    [Fact]
    public void Options_validator_rejects_reserved_and_bad_values()
    {
        var v = new SidecarOptionsValidator();
        Assert.True(v.Validate(null, new SidecarOptions()).Succeeded);
        Assert.True(v.Validate(null, new SidecarOptions { Port = 5001 }).Failed);
        Assert.True(v.Validate(null, new SidecarOptions { MaxCrashes = 0 }).Failed);
        Assert.True(v.Validate(null, new SidecarOptions { MinNodeMajor = 10 }).Failed);
        Assert.True(v.Validate(null, new SidecarOptions { MaxSessions = 0 }).Failed);
        Assert.True(v.Validate(null, new SidecarOptions { PortRangeStart = 4990, PortRangeEnd = 5010 }).Failed); // contains 5001
        Assert.True(v.Validate(null, new SidecarOptions { PortRangeStart = 5811 }).Failed);
        Assert.True(v.Validate(null, new SidecarOptions { PortRangeStart = 5811, PortRangeEnd = 5812, MaxSessions = 5 }).Failed);
        Assert.True(v.Validate(null, new SidecarOptions { PortRangeStart = 5811, PortRangeEnd = 5819, MaxSessions = 5 }).Succeeded);
    }

    [Fact]
    public void Node_version_parsing()
    {
        Assert.True(SidecarFiles.TryParseNodeMajor("v22.12.0\n", out var m) && m == 22);
        Assert.False(SidecarFiles.TryParseNodeMajor("oops", out _));
    }
}
