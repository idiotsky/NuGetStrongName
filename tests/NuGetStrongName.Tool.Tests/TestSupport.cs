using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using JIT.NuGetStrongName.Cli;
using JIT.NuGetStrongName.Inspection;
using JIT.NuGetStrongName.Signing;

namespace JIT.NuGetStrongName.Tool.Tests;

public sealed record CliResult(int ExitCode, JsonElement Json, string StdOut, string StdErr)
{
    public string Status => Json.GetProperty("status").GetString()!;

    public IEnumerable<JsonElement> Assemblies => Json.GetProperty("assetGroups").EnumerateArray()
        .SelectMany(g => g.GetProperty("assemblies").EnumerateArray());

    public JsonElement Assembly(string group, string file)
    {
        return Json.GetProperty("assetGroups").EnumerateArray()
            .Single(g => g.GetProperty("path").GetString() == group)
            .GetProperty("assemblies").EnumerateArray()
            .Single(a => a.GetProperty("file").GetString() == file);
    }

    public override string ToString()
    {
        return $"exit {ExitCode}\n{StdOut}\n{StdErr}";
    }
}

/// <summary>Runs once before any test in this namespace: removes work directories left over from earlier runs.</summary>
[SetUpFixture]
public sealed class WorkRootCleanup
{
    public static readonly string WorkRoot = Path.Combine(Path.GetTempPath(), "nuget strongname tests");

    [OneTimeSetUp]
    public void DeleteWorkRoot()
    {
        try
        {
            if (Directory.Exists(WorkRoot))
            {
                Directory.Delete(WorkRoot, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another test run may still hold files open; stale leftovers are harmless.
        }
    }
}

/// <summary>Base fixture: a unique working directory whose path contains spaces.</summary>
public abstract class CliTestBase
{
    protected string Work { get; private set; } = "";

    [SetUp]
    public void CreateWorkDirectory()
    {
        Work = Path.Combine(WorkRootCleanup.WorkRoot, Guid.NewGuid().ToString("N")[..8] + " work dir");
        Directory.CreateDirectory(Work);
    }

    [TearDown]
    public void DeleteWorkDirectory()
    {
        try
        {
            Directory.Delete(Work, true);
        }
        catch (IOException)
        {
        }
    }

    protected static async Task<CliResult> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliApp.RunAsync(args, stdout, stderr);
        var text = stdout.ToString();
        JsonElement json = default;
        if (text.TrimStart().StartsWith('{'))
        {
            json = JsonDocument.Parse(text).RootElement.Clone();
        }

        return new CliResult(exit, json, text, stderr.ToString());
    }

    protected string WriteKey(byte[] key, string name = "signing key.snk")
    {
        string path = Path.Combine(Work, name);
        File.WriteAllBytes(path, key);
        return path;
    }

    protected static string TokenOf(byte[] key)
    {
        return StrongNameKey.FromBlob(key).PublicKeyTokenHex;
    }

    protected static string Sha256(string path)
    {
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    protected static Dictionary<string, byte[]> ReadEntries(string nupkg)
    {
        using ZipArchive zip = ZipFile.OpenRead(nupkg);
        return zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e =>
            {
                using Stream s = e.Open();
                using var m = new MemoryStream();
                s.CopyTo(m);
                return m.ToArray();
            });
    }

    protected static AssemblyInfo? Inspect(byte[] image)
    {
        return AssemblyInspector.Inspect(new MemoryStream(image));
    }
}
