using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using JIT.NuGetStrongName.Inspection;
using JIT.NuGetStrongName.Signing;
using JIT.NuGetStrongName.Tool.Tests.Fixtures;

namespace JIT.NuGetStrongName.Tool.Tests;

[TestFixture]
public sealed class CliArgumentTests : CliTestBase
{
    [Test]
    public async Task Top_level_version_prints_the_tool_version()
    {
        CliResult result = await RunAsync("--version");
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.StdOut.Trim(), Does.Match(@"^\d+\.\d+\.\d+"));
    }

    [Test]
    public async Task Help_lists_both_commands()
    {
        CliResult result = await RunAsync("--help");
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.StdOut, Does.Contain("sign-package").And.Contain("verify-package"));
    }

    [TestCase]
    [TestCase("bogus-command")]
    [TestCase("sign-package")]
    [TestCase("sign-package", "--input", "a.nupkg", "--package-id", "A", "--version", "1.0.0")]
    [TestCase("sign-package", "--package-id", "A")]
    [TestCase("sign-package", "--input", "a.nupkg", "--unknown", "x")]
    [TestCase("sign-package", "--input", "a.nupkg", "--timeout", "0")]
    [TestCase("sign-package", "--input", "a.nupkg", "--map-package", "NoVersion")]
    [TestCase("sign-package", "--input", "a.nupkg", "--map-assembly", "A=nothex")]
    [TestCase("verify-package", "--input", "a.nupkg", "--key-file", "k.snk")]
    [TestCase("sign-package", "--input", "does not exist.nupkg")]
    [TestCase("sign-package", "--input", "a.nupkg", "--output-version", "not a version")]
    [TestCase("sign-package", "--input", "a.nupkg", "--output-id", "../escape")]
    [TestCase("sign-package", "--package-id", "bad/id", "--version", "1.0.0")]
    [TestCase("sign-package", "--input", "a.nupkg", "--map-package", "A=B@not-a-range[")]
    public async Task Invalid_arguments_exit_2_with_json(params string[] args)
    {
        CliResult result = await RunAsync(args);
        Assert.That(result.ExitCode, Is.EqualTo(2), result.ToString());
        Assert.That(result.Status, Is.EqualTo("invalid-arguments"));
        Assert.That(result.StdErr, Does.StartWith("error:"));
    }
}

[TestFixture]
public sealed class VerifyPackageTests : CliTestBase
{
    [Test]
    public async Task Verify_accepts_a_signed_package_and_rejects_an_unsigned_one()
    {
        string unsigned = new FixturePackage { Id = "Pair", Version = "1.0.0" }
            .Add("lib/net48", new FixtureAssembly { Name = "Pair.A", References = ["Pair.B"] })
            .Add("lib/net48", new FixtureAssembly { Name = "Pair.B", References = ["Pair.A"] })
            .Write(Path.Combine(Work, "in"));

        CliResult rejected = await RunAsync("verify-package", "--input", unsigned);
        Assert.That(rejected.ExitCode, Is.EqualTo(1), rejected.ToString());
        Assert.That(rejected.Assemblies.Select(a => a.GetProperty("outcome").GetString()),
            Is.All.EqualTo("not-strong-named"));

        CliResult signed = await RunAsync("sign-package", "--input", unsigned, "--key-file",
            WriteKey(TestKeys.SigningKey), "--output", Path.Combine(Work, "out"), "--output-version", "1.0.0-signed");
        Assert.That(signed.ExitCode, Is.EqualTo(0), signed.ToString());
        string output = signed.Json.GetProperty("outputPath").GetString()!;

        CliResult accepted = await RunAsync("verify-package", "--input", output, "--public-key-token",
            TokenOf(TestKeys.SigningKey));
        Assert.That(accepted.ExitCode, Is.EqualTo(0), accepted.ToString());
        Assert.That(accepted.Status, Is.EqualTo("verified"));

        CliResult wrongToken = await RunAsync("verify-package", "--input", output, "--public-key-token",
            "0123456789abcdef");
        Assert.That(wrongToken.ExitCode, Is.EqualTo(1));

        // Feeding the signed output back is an idempotent no-op.
        CliResult again = await RunAsync("sign-package", "--input", output);
        Assert.That(again.Status, Is.EqualTo("already-strong-named"), again.ToString());
    }

    [Test]
    public async Task Verify_detects_same_named_assemblies_with_different_tokens_across_frameworks()
    {
        string input = new FixturePackage { Id = "Split", Version = "1.0.0" }
            .Add("lib/net48", new FixtureAssembly { Name = "Split", Key = TestKeys.VendorKey })
            .Add("lib/netstandard2.0", new FixtureAssembly { Name = "Split", Key = TestKeys.SigningKey })
            .Write(Work);

        CliResult result = await RunAsync("verify-package", "--input", input);
        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.StdOut, Does.Contain("different public key tokens"));
    }
}

[TestFixture]
public sealed class DownloadTests : CliTestBase
{
    private string WriteConfig(string source, string? mappingPattern = null)
    {
        string mapping = mappingPattern is null
            ? ""
            : $"""
               <packageSourceMapping>
                 <packageSource key="local feed"><package pattern="{mappingPattern}" /></packageSource>
               </packageSourceMapping>
               """;
        string path = Path.Combine(Work, "repo root", "nuget.config");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
                                 <?xml version="1.0" encoding="utf-8"?>
                                 <configuration>
                                   <packageSources><clear /><add key="local feed" value="{source}" /></packageSources>
                                   {mapping}
                                 </configuration>
                                 """);
        return path;
    }

    [Test]
    public async Task Downloads_from_the_repository_configuration_and_signs()
    {
        string feed = Path.Combine(Work, "local feed");
        new FixturePackage { Id = "Remote.Pkg", Version = "1.4.0" }
            .Add("lib/netstandard2.0", new FixtureAssembly { Name = "Remote.Pkg" })
            .Write(feed);
        string config = WriteConfig(feed);

        CliResult result = await RunAsync("sign-package", "--package-id", "Remote.Pkg", "--version", "1.4.0",
            "--repository", Path.GetDirectoryName(config)!, "--key-file", WriteKey(TestKeys.SigningKey),
            "--output", Path.Combine(Work, "out"), "--output-version", "1.4.0-signed");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.ToString());
        Assert.That(result.Json.GetProperty("packageId").GetString(), Is.EqualTo("Remote.Pkg"));
        Assert.That(result.Json.GetProperty("inputSource").GetString(), Is.EqualTo("local feed"));
        Assert.That(result.Json.GetProperty("inputPath").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(result.Json.GetProperty("inputSha256").GetString(),
            Is.EqualTo(Sha256(Path.Combine(feed, "Remote.Pkg.1.4.0.nupkg"))));
    }

    [Test]
    public async Task Source_mapping_is_honoured()
    {
        string feed = Path.Combine(Work, "local feed");
        new FixturePackage { Id = "Remote.Pkg", Version = "1.4.0" }
            .Add("lib/netstandard2.0", new FixtureAssembly { Name = "Remote.Pkg" })
            .Write(feed);
        string config = WriteConfig(feed, "Something.Else.*");

        CliResult result = await RunAsync("verify-package", "--package-id", "Remote.Pkg", "--version", "1.4.0",
            "--configfile", config);

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.StdOut, Does.Contain("No enabled package source"));
    }

    [Test]
    public async Task Missing_package_is_a_processing_failure()
    {
        string config = WriteConfig(Path.Combine(Work, "empty feed"));
        Directory.CreateDirectory(Path.Combine(Work, "empty feed"));

        CliResult result = await RunAsync("verify-package", "--package-id", "Not.There", "--version", "1.0.0",
            "--configfile", config);

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.StdOut, Does.Contain("was not found"));
    }

    [Test]
    public async Task Unresponsive_source_times_out_with_exit_1()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new List<TcpClient>();
        Task acceptLoop = AcceptWithoutAnswering(listener, accepted);
        CliResult result;
        var watch = Stopwatch.StartNew();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string config = WriteConfig($"http://127.0.0.1:{port}/v3/index.json");
            result = await RunAsync("verify-package", "--package-id", "Slow.Pkg", "--version", "1.0.0",
                "--configfile", config, "--timeout", "2");
            watch.Stop();
        }
        finally
        {
            listener.Stop(); // ends the pending accept, so the loop returns
            await acceptLoop;
            accepted.ForEach(c => c.Dispose());
        }

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.StdOut, Does.Contain("Timed out"));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)));

        // Static and parameter-based, so nothing disposable is captured by a closure.
        static async Task AcceptWithoutAnswering(TcpListener server, List<TcpClient> sink)
        {
            while (true)
            {
                try
                {
                    sink.Add(await server.AcceptTcpClientAsync()); // accept, never answer
                }
                catch
                {
                    return; // the listener was stopped
                }
            }
        }
    }
}

[TestFixture]
public sealed class StrongNameVerifierTests
{
    [Test]
    public void Verifier_agrees_with_valid_tampered_and_unsigned_images()
    {
        byte[] signed = new FixtureAssembly { Name = "Verify.Me", Key = TestKeys.SigningKey }.Build();
        var tampered = (byte[])signed.Clone();
        tampered[^20] ^= 0xFF;

        Assert.That(AssemblyInspectorFor(signed), Is.EqualTo(SignatureState.Valid));
        Assert.That(AssemblyInspectorFor(tampered), Is.EqualTo(SignatureState.Invalid));
        Assert.That(AssemblyInspectorFor(new FixtureAssembly { Name = "Plain" }.Build()),
            Is.EqualTo(SignatureState.Unsigned));
        Assert.That(AssemblyInspectorFor(
                new FixtureAssembly { Name = "Public", Key = TestKeys.SigningKey, PublicSignOnly = true }.Build()),
            Is.EqualTo(SignatureState.Invalid));
    }

    [Test]
    public void Verifier_matches_sn_exe_on_windows()
    {
        string? sn = Directory.Exists(@"C:\Program Files (x86)\Microsoft SDKs\Windows")
            ? Directory.EnumerateFiles(@"C:\Program Files (x86)\Microsoft SDKs\Windows", "sn.exe",
                SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (!OperatingSystem.IsWindows() || sn is null)
        {
            Assert.Ignore("sn.exe oracle is only available on Windows with the .NET Framework SDK.");
        }

        string dir = Path.Combine(Path.GetTempPath(), $"nsn sn {Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] signed = new FixtureAssembly { Name = "Oracle", Key = TestKeys.SigningKey }.Build();
            var tampered = (byte[])signed.Clone();
            tampered[^20] ^= 0xFF;
            File.WriteAllBytes(Path.Combine(dir, "good.dll"), signed);
            File.WriteAllBytes(Path.Combine(dir, "bad.dll"), tampered);

            Assert.That(RunSn(sn, Path.Combine(dir, "good.dll")), Is.EqualTo(0));
            Assert.That(RunSn(sn, Path.Combine(dir, "bad.dll")), Is.Not.EqualTo(0));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static int RunSn(string sn, string file)
    {
        using Process process = Process.Start(new ProcessStartInfo(sn, ["-q", "-vf", file])
                                              { RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit(30_000);
        return process.ExitCode;
    }

    private static SignatureState AssemblyInspectorFor(byte[] image)
    {
        return AssemblyInspector.Inspect(new MemoryStream(image))!.Signature;
    }
}
