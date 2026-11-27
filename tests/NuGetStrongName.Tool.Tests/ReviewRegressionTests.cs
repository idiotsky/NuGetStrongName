using System.IO.Compression;
using System.Text;
using JIT.NuGetStrongName.Acquisition;
using JIT.NuGetStrongName.Inspection;
using JIT.NuGetStrongName.Signing;
using JIT.NuGetStrongName.Tool.Tests.Fixtures;

namespace JIT.NuGetStrongName.Tool.Tests;

/// <summary>Regression tests for the independent review findings of Slice 1.</summary>
[TestFixture]
public sealed class ReviewRegressionTests : CliTestBase
{
    private async Task<CliResult> SignAsync(string input)
    {
        return await RunAsync("sign-package", "--input", input, "--key-file", WriteKey(TestKeys.SigningKey),
            "--output", Path.Combine(Work, "out"), "--output-version", "1.0.0-signed");
    }

    [Test]
    public async Task Mixed_mode_image_is_never_rewritten()
    {
        string input = new FixturePackage { Id = "Mixed.Mode", Version = "1.0.0" }
            .Add("lib/net48", new FixtureAssembly { Name = "Cpp.Cli", NotILOnly = true })
            .Write(Work);

        CliResult result = await SignAsync(input);

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.StdOut, Does.Contain("mixed-mode or ReadyToRun"));
        Assert.That(Directory.Exists(Path.Combine(Work, "out")), Is.False);
    }

    [TestCase("\r\n")]
    [TestCase("\n")]
    public async Task Edited_manifests_keep_their_line_endings_on_every_platform(string newLine)
    {
        string input = new FixturePackage { Id = "Line.Endings", Version = "1.0.0", NewLine = newLine }
            .Add("lib/net48", new FixtureAssembly { Name = "Line.Endings" })
            .Write(Work);

        CliResult result = await SignAsync(input);

        Assert.That(result.ExitCode, Is.EqualTo(0), result.ToString());
        Dictionary<string, byte[]> entries = ReadEntries(result.Json.GetProperty("outputPath").GetString()!);
        foreach (string name in new[] { "Line.Endings.nuspec", "package/services/metadata/core-properties/abc.psmdcp" })
        {
            string text = Encoding.UTF8.GetString(entries[name]);
            Assert.That(text, Does.Contain("1.0.0-signed"), name);
            Assert.That(text.ReplaceLineEndings(newLine), Is.EqualTo(text), $"{name} changed its line endings");
            Assert.That(text, Does.Contain(newLine), name);
        }
    }

    [Test]
    public async Task Corrupt_managed_assembly_fails_instead_of_passing_as_native()
    {
        byte[] image = new FixtureAssembly { Name = "Corrupt" }.Build();
        int bsjb = image.AsSpan().IndexOf("BSJB"u8);
        image[bsjb] = (byte)'X';
        string input = new FixturePackage { Id = "Corrupt", Version = "1.0.0" }.Add("lib/net48/Corrupt.dll", image)
            .Write(Work);

        CliResult verify = await RunAsync("verify-package", "--input", input);
        CliResult sign = await SignAsync(input);

        Assert.That(verify.ExitCode, Is.EqualTo(1), verify.ToString());
        Assert.That(sign.ExitCode, Is.EqualTo(1), sign.ToString());
        Assert.That(sign.StdOut, Does.Contain("corrupt managed assembly"));
    }

    [Test]
    public void Truncated_signed_image_is_invalid_rather_than_a_crash()
    {
        byte[] image = new FixtureAssembly { Name = "Truncated", Key = TestKeys.SigningKey }.Build();
        byte[] truncated = image.AsSpan(0, image.Length - 16).ToArray();

        AssemblyInfo? info = AssemblyInspector.Inspect(new MemoryStream(truncated));

        Assert.That(info!.Signature, Is.EqualTo(SignatureState.Invalid));
    }

    [TestCase("lib/net48/../evil.dll")]
    [TestCase("lib/./net48/a.dll")]
    [TestCase("lib//net48/a.dll")]
    [TestCase("lib/net48/a.dll:stream")]
    public async Task Unsafe_entry_names_are_rejected(string entry)
    {
        string input = new FixturePackage { Id = "Unsafe", Version = "1.0.0" }
            .Add(entry, new FixtureAssembly { Name = "A" }.Build())
            .Write(Work);

        CliResult result = await SignAsync(input);

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.StdOut, Does.Contain("unsafe or invalid path"));
    }

    [Test]
    public async Task Entries_differing_only_in_case_are_rejected_on_every_platform()
    {
        string input = new FixturePackage { Id = "Cased", Version = "1.0.0" }
            .Add("lib/net48/A.dll", new FixtureAssembly { Name = "A" }.Build())
            .Add("lib/NET48/A.dll", new FixtureAssembly { Name = "A" }.Build())
            .Write(Work);

        CliResult result = await SignAsync(input);

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.StdOut, Does.Contain("differ only in case"));
    }

    [Test]
    public async Task Malformed_manifest_layout_reports_json_and_exit_1()
    {
        string input = new FixturePackage { Id = "TwoSpecs", Version = "1.0.0" }
            .Add("Extra.nuspec", "<package/>"u8.ToArray())
            .Add("lib/net48", new FixtureAssembly { Name = "A" })
            .Write(Work);

        CliResult result = await SignAsync(input);

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.Status, Is.EqualTo("failed"));
        Assert.That(result.StdOut, Does.Contain("exactly one root .nuspec"));
    }

    [Test]
    public async Task Nuspec_with_a_dtd_is_rejected()
    {
        string input = new FixturePackage { Id = "Dtd", Version = "1.0.0" }
            .Add("lib/net48", new FixtureAssembly { Name = "A" }).Write(Work);
        string path = Path.Combine(Work, "dtd.nupkg");
        await using (ZipArchive source = await ZipFile.OpenReadAsync(input))
        await using (ZipArchive target = await ZipFile.OpenAsync(path, ZipArchiveMode.Create))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                await using Stream from = await entry.OpenAsync();
                await using Stream to = await target.CreateEntry(entry.FullName).OpenAsync();
                if (entry.FullName == "Dtd.nuspec")
                {
                    to.Write("<?xml version=\"1.0\"?><!DOCTYPE package [<!ENTITY a \"aaaa\">]><package><metadata>"u8 +
                             "<id>Dtd</id><version>1.0.0</version></metadata></package>"u8);
                }
                else
                {
                    await from.CopyToAsync(to);
                }
            }
        }

        CliResult result = await SignAsync(path);

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.Status, Is.EqualTo("failed"));
    }

    [Test]
    public async Task Malformed_nuget_config_reports_json_and_exit_1()
    {
        string config = Path.Combine(Work, "nuget.config");
        await File.WriteAllTextAsync(config, "<configuration><packageSources><add key=");

        CliResult result = await RunAsync("verify-package", "--package-id", "A", "--version", "1.0.0", "--configfile",
            config);

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.StdOut, Does.Contain("Invalid NuGet configuration"));
    }

    [Test]
    public async Task Vendor_assembly_referencing_an_external_unsigned_assembly_is_consistent_in_sign_and_verify()
    {
        string input = new FixturePackage { Id = "Vendor.External", Version = "1.0.0" }
            .Add("lib/net48", new FixtureAssembly
                              {
                                  Name = "Vendor.External",
                                  References = ["Somebody.Else"],
                                  Key = TestKeys.VendorKey
                              })
            .Write(Work);

        CliResult sign = await RunAsync("sign-package", "--input", input);
        CliResult verify = await RunAsync("verify-package", "--input", input);

        Assert.That(sign.Status, Is.EqualTo("already-strong-named"), sign.ToString());
        Assert.That(verify.ExitCode, Is.EqualTo(0), verify.ToString());
        Assert.That(sign.StdOut, Does.Contain("Somebody.Else"));
        Assert.That(verify.StdOut, Does.Contain("Somebody.Else"));
    }

    [Test]
    public async Task Removed_package_signature_is_reported()
    {
        string input = new FixturePackage { Id = "Sig", Version = "1.0.0", WithPackageSignature = true }
            .Add("lib/net48", new FixtureAssembly { Name = "A" })
            .Write(Work);

        CliResult result = await SignAsync(input);

        Assert.That(result.ExitCode, Is.EqualTo(0), result.ToString());
        Assert.That(result.Json.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()),
            Has.Some.Contains(".signature.p7s"));
    }

    [TestCase("https://user:secret@feed.example/v3/index.json?code=token", "https://feed.example/v3/index.json")]
    [TestCase("https://api.nuget.org/v3/index.json", "https://api.nuget.org/v3/index.json")]
    public void Source_urls_are_redacted_before_logging(string source, string expected)
    {
        Assert.That(PackageDownloader.Redact(source), Is.EqualTo(expected));
    }
}
