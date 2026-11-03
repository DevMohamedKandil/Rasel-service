using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Rasel.Infrastructure.Sidecar;

/// <summary>File-system side of the supervisor: data folders, bundled-source copy, dependency hash, token, free port, single-instance lock.</summary>
public sealed partial class SidecarFiles(IOptions<SidecarOptions> options)
{
    public static string NpmExecutable => Resolve(OperatingSystem.IsWindows() ? "npm.cmd" : "npm");
    public static string NpxExecutable => Resolve(OperatingSystem.IsWindows() ? "npx.cmd" : "npx");
    public static string NodeExecutable => Resolve(OperatingSystem.IsWindows() ? "node.exe" : "node");

    /// <summary>Full path of a tool found on PATH. A bare name is resolved relative to the child's working directory by npm.cmd on Windows, which breaks it, so always pass the full path.</summary>
    public static string Resolve(string file)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(dir.Trim('"'), file); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        return file;
    }

    private string Root => string.IsNullOrWhiteSpace(options.Value.DataDir)
        ? Path.Combine(AppContext.BaseDirectory, "sidecar-data") : Path.GetFullPath(options.Value.DataDir);
    private string Source => string.IsNullOrWhiteSpace(options.Value.SourceDir)
        ? Path.Combine(AppContext.BaseDirectory, "sidecar") : Path.GetFullPath(options.Value.SourceDir);

    /// <summary>Caches live in the data folder so a service account without a user profile (LocalService) still works.</summary>
    public IReadOnlyDictionary<string, string> ToolEnv => new Dictionary<string, string>
    {
        ["npm_config_cache"] = Path.Combine(Root, "npm-cache"),
        ["PUPPETEER_CACHE_DIR"] = Path.Combine(Root, "browsers"),
        ["HOME"] = Root,
    };

    public string AppDir => Path.Combine(Root, "app");
    public string SessionDir => Path.Combine(Root, "session");
    private string LogDir => Path.Combine(Root, "logs");
    public string SidecarLogPath => Path.Combine(LogDir, "sidecar.log");
    public string InstallLogPath => Path.Combine(LogDir, "install.log");
    private string TokenPath => Path.Combine(Root, "shared-token");
    private string HashPath => Path.Combine(Root, "deps.sha256");

    /// <summary>Creates the folders and copies the bundled sources (never node_modules or the session). Returns an error text, or null.</summary>
    public string? PrepareFolders()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(AppDir);
            Directory.CreateDirectory(SessionDir);
            Directory.CreateDirectory(LogDir);
            Restrict(Root);
            if (!Directory.Exists(Source) || !File.Exists(Path.Combine(Source, "package.json")))
                return $"The bundled WhatsApp engine files were not found (looked in '{Source}'). Reinstall Rasel or set Sidecar:SourceDir.";
            CopyTree(Source, AppDir);
            foreach (var log in new[] { SidecarLogPath, InstallLogPath })
                if (File.Exists(log) && new FileInfo(log).Length > 5_000_000) File.Move(log, log + ".old", true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not prepare the data folder '{Root}' ({ex.GetType().Name}). Give the Rasel service account write access or change Sidecar:DataDir.";
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (name is "node_modules" or "sessions" or ".wwebjs_auth" or ".wwebjs_cache") continue;
            Directory.CreateDirectory(Path.Combine(to, name));
            CopyTree(dir, Path.Combine(to, name));
        }
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
    }

    private string LockHash() => File.Exists(Path.Combine(AppDir, "package-lock.json"))
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppDir, "package-lock.json")))) : "";

    public bool NeedsInstall() =>
        !Directory.Exists(Path.Combine(AppDir, "node_modules")) || !File.Exists(HashPath) || File.ReadAllText(HashPath).Trim() != LockHash();

    public void MarkInstalled() => File.WriteAllText(HashPath, LockHash());

    /// <summary>A random shared secret per install, persisted with owner-only access.</summary>
    public string GetOrCreateToken()
    {
        if (File.Exists(TokenPath) && File.ReadAllText(TokenPath).Trim() is { Length: >= 32 } existing) return existing;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(TokenPath, token);
        Restrict(TokenPath);
        return token;
    }

    public int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public bool TryAcquireLock(out FileStream? stream)
    {
        try
        {
            Directory.CreateDirectory(Root);
            stream = new FileStream(Path.Combine(Root, "supervisor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { stream = null; return false; }
    }

    public static bool TryParseNodeMajor(string output, out int major)
    {
        var m = NodeVersion().Match(output);
        major = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        return m.Success;
    }

    [GeneratedRegex(@"v?(\d+)\.\d+\.\d+")]
    private static partial Regex NodeVersion();

    /// <summary>Best effort: owner-only on Unix (0700/0600); on Windows inheritance is cut and the Users/Everyone/Authenticated Users entries are removed from the token file.</summary>
    private static void Restrict(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    var sec = info.GetAccessControl();
                    sec.SetAccessRuleProtection(true, true);
                    foreach (System.Security.AccessControl.FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                        if (rule.IdentityReference is System.Security.Principal.SecurityIdentifier sid &&
                            (sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinUsersSid) || sid.IsWellKnown(System.Security.Principal.WellKnownSidType.WorldSid) || sid.IsWellKnown(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid)))
                            sec.RemoveAccessRule(rule);
                    info.SetAccessControl(sec);
                }
            }
            else if (File.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }
}
