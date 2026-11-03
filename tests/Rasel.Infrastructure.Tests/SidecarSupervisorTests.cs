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
        public Task<SidecarProbe> ProbeAsync(int port, CancellationToken ct) => Task.FromResult(new SidecarProbe(Alive, Ready));
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

    private (SidecarSupervisor Sup, FakeLauncher Launcher, FakeProbe Probe, SidecarOptions Opt) Build(Action<SidecarOptions>? tweak = null, FakeLauncher? launcher = null, string data = "data")
    {
        var opt = new SidecarOptions { DataDir = Path.Combine(_root, data), SourceDir = _source, HealthIntervalMs = 20, BackoffBaseMs = 30, BackoffMaxMs = 200, MaxCrashes = 3, StableSeconds = 3600 };
        tweak?.Invoke(opt);
        launcher ??= new FakeLauncher { ChromePath = _chrome };
        var probe = new FakeProbe();
        var o = Options.Create(opt);
        return (new SidecarSupervisor(o, launcher, probe, new SidecarFiles(o), NullLogger<SidecarSupervisor>.Instance), launcher, probe, opt);
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

    [Fact]
    public async Task Prepares_then_starts_and_walks_NeedsQr_then_Connected_with_a_generated_token()
    {
        var (sup, l, probe, _) = Build();
        await Run(sup, async () =>
        {
            await Until(() => sup.Status.State == SidecarState.NeedsQr);
            Assert.Equal(1, l.CiRuns);
            var env = l.Last.Env!;
            Assert.Equal("127.0.0.1", env["HOST"]);
            Assert.True(env["SHARED_TOKEN"].Length >= 32);
            Assert.Equal(env["SHARED_TOKEN"], sup.Current!.Value.Token);
            probe.Ready = true;
            await Until(() => sup.Status.State == SidecarState.Connected);
        });
        Assert.True(l.Last.Killed); // killed on API shutdown
        Assert.Null(sup.Current);
    }

    [Fact]
    public async Task Npm_ci_runs_only_when_node_modules_is_missing_or_the_lockfile_changed()
    {
        var l = new FakeLauncher { ChromePath = _chrome };
        var (sup, _, _, opt) = Build(launcher: l);
        await Run(sup, async () => await Until(() => sup.Status.State == SidecarState.NeedsQr));
        Assert.Equal(1, l.CiRuns);

        Directory.CreateDirectory(Path.Combine(opt.DataDir, "app", "node_modules")); // what npm ci would have produced
        var (sup2, _, _, _) = Build(launcher: l);
        await Run(sup2, async () => await Until(() => sup2.Status.State == SidecarState.NeedsQr));
        Assert.Equal(1, l.CiRuns); // unchanged lockfile: no reinstall

        File.WriteAllText(Path.Combine(_source, "package-lock.json"), "{\"v\":2}");
        var (sup3, _, _, _) = Build(launcher: l);
        await Run(sup3, async () => await Until(() => sup3.Status.State == SidecarState.NeedsQr));
        Assert.Equal(2, l.CiRuns);
    }

    [Fact]
    public async Task Missing_or_old_node_and_failed_install_give_a_clear_Failed_status()
    {
        var (sup, _, _, _) = Build(launcher: new FakeLauncher { NodeVersion = "" });
        await Run(sup, async () => { await Until(() => sup.Status.State == SidecarState.Failed); Assert.Contains("Node.js", sup.Status.Reason); });

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
            await Until(() => sup.Status.State == SidecarState.NeedsQr);
            l.Last.Crash();
            await Until(() => l.StartCount == 2);
            l.Last.Crash();
            await Until(() => l.StartCount == 3);
            l.Last.Crash();
            await Until(() => sup.Status.State == SidecarState.Failed);
            Assert.Contains("crash loop", sup.Status.Reason);
            await Task.Delay(200);
            Assert.Equal(3, l.StartCount); // circuit open: no 4th start

            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStart()); // a human Start re-arms it
            await Until(() => l.StartCount == 4);
        });
    }

    [Fact]
    public async Task Manual_stop_is_not_a_crash_and_is_not_auto_restarted_and_start_resumes()
    {
        var (sup, l, _, _) = Build();
        await Run(sup, async () =>
        {
            await Until(() => sup.Status.State == SidecarState.NeedsQr);
            var first = l.Last;
            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStop());
            await Until(() => sup.Status.State == SidecarState.Stopped);
            Assert.True(first.Killed);
            Assert.Equal(0, sup.Status.Crashes);
            await Task.Delay(200);
            Assert.Equal(1, l.StartCount);
            Assert.Equal(SidecarCommandResult.AlreadyInState, sup.RequestStop()); // idempotent
            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStart());
            await Until(() => l.StartCount == 2);
            await Until(() => sup.Status.State == SidecarState.NeedsQr);
            Assert.Equal(SidecarCommandResult.AlreadyInState, sup.RequestStart());
        });
    }

    [Fact]
    public async Task AutoStart_false_prepares_then_waits_in_Ready_until_Start()
    {
        var (sup, l, _, _) = Build(o => o.AutoStart = false);
        await Run(sup, async () =>
        {
            await Until(() => sup.Status.State == SidecarState.Ready);
            await Task.Delay(150);
            Assert.Equal(0, l.StartCount);
            Assert.False(sup.Status.AutoStart);
            Assert.Equal(SidecarCommandResult.Accepted, sup.RequestStart());
            await Until(() => sup.Status.State == SidecarState.NeedsQr);
            Assert.Equal(1, l.StartCount);
        });
    }

    [Fact]
    public async Task Restart_replaces_the_process_without_counting_a_crash_and_concurrent_requests_are_safe()
    {
        var (sup, l, _, _) = Build();
        await Run(sup, async () =>
        {
            await Until(() => sup.Status.State == SidecarState.NeedsQr);
            var first = l.Last;
            await Task.WhenAll(Enumerable.Range(0, 9).Select(i => Task.Run(() => (i % 3) switch { 0 => sup.RequestRestart(), 1 => sup.RequestStart(), _ => sup.RequestRestart() })));
            await Until(() => l.StartCount >= 2 && sup.Status.State == SidecarState.NeedsQr);
            await Task.Delay(150);
            Assert.True(first.Killed);
            Assert.Equal(0, sup.Status.Crashes);
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
            await Until(() => a.Status.State == SidecarState.NeedsQr);
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
            await Until(() => sup.Status.State == SidecarState.NeedsQr);
            var first = l.Last;
            probe.Alive = false;
            await Until(() => first.Killed);
            probe.Alive = true;
            await Until(() => l.StartCount == 2);
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
    }

    [Fact]
    public void Node_version_parsing()
    {
        Assert.True(SidecarFiles.TryParseNodeMajor("v22.12.0\n", out var m) && m == 22);
        Assert.False(SidecarFiles.TryParseNodeMajor("oops", out _));
    }
}
