namespace Rasel.Api.Tests;

/// <summary>
/// docs/openapi.json is the published contract. This fails when the code and the file drift apart.
/// To refresh the file after an intentional API change: set RASEL_UPDATE_OPENAPI=1 and run this test once.
/// </summary>
public class OpenApiSyncTests
{
    private static string FindDoc([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Rasel.sln"))) return Path.Combine(dir.FullName, "docs", "openapi.json");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var docs = Path.Combine(dir.FullName, "docs");
            if (Directory.Exists(docs) && File.Exists(Path.Combine(dir.FullName, "Rasel.sln"))) return Path.Combine(docs, "openapi.json");
        }
        throw new InvalidOperationException("Repository root (Rasel.sln) not found.");
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    [Fact]
    public async Task Committed_openapi_json_matches_the_running_code()
    {
        await using var f = new RaselFactory();
        var actual = Normalize(await f.CreateClient().GetStringAsync("/swagger/v1/swagger.json"));
        var path = FindDoc();
        if (Environment.GetEnvironmentVariable("RASEL_UPDATE_OPENAPI") == "1")
        {
            File.WriteAllText(path, actual + "\n");
            return;
        }
        Assert.True(File.Exists(path), "docs/openapi.json is missing; run once with RASEL_UPDATE_OPENAPI=1.");
        Assert.Equal(Normalize(File.ReadAllText(path)), actual);
    }
}
