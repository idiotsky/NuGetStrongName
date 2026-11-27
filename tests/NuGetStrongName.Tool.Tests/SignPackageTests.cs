using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using JIT.NuGetStrongName.Inspection;
using JIT.NuGetStrongName.Signing;
using JIT.NuGetStrongName.Tool.Tests.Fixtures;

namespace JIT.NuGetStrongName.Tool.Tests;

[TestFixture]
public sealed class SignPackageTests : CliTestBase
{
    private static readonly string[] m_frameworks = ["net48", "netstandard2.0"];

    private string BuildMutualPackage(Action<FixturePackage>? customize = null)
    {
        var package = new FixturePackage { Id = "Vendor.Sdk", Version = "2.0.0", WithPackageSignature = true };
        foreach (string tfm in m_frameworks)
        {
            // Core <-> Data form a cycle; Service depends on both; IVT and typeof() point into the set.
            package.Add($"lib/{tfm}",
                new FixtureAssembly
                {
                    Name = "Vendor.Core", References = ["Vendor.Data"],
                    InternalsVisibleTo = ["Vendor.Service", "Vendor.Tests"]
                });
            package.Add($"lib/{tfm}", new FixtureAssembly { Name = "Vendor.Data", References = ["Vendor.Core"] });
            package.Add($"lib/{tfm}",
                new FixtureAssembly
                {
                    Name = "Vendor.Service", References = ["Vendor.Core", "Vendor.Data"],
                    TypeArgumentFrom = "Vendor.Core"
                });
        }

        package.Add("ref/netstandard2.0",
            new FixtureAssembly { Name = "Vendor.Core", References = ["Vendor.Data"], ReferenceAssembly = true });
        package.Add("runtimes/win-x64/lib/netstandard2.0",
            new FixtureAssembly { Name = "Vendor.Native.Interop", References = ["Vendor.Core"] });
        package.Add("runtimes/win-x64/native/vendor_native.dll", FixtureAssembly.NativeImage());
        package.Add("lib/netstandard2.0/vendor_helper_native.dll", FixtureAssembly.NativeImage());
        package.Add("lib/netstandard2.0/de/Vendor.Core.resources.dll",
            new FixtureAssembly { Name = "Vendor.Core.resources", Culture = "de" }.Build());
        package.Add("lib/netstandard2.0/Vendor.Core.xml", "<doc/>"u8.ToArray());
        package.Add("build/Vendor.Sdk.targets", "<Project/>"u8.ToArray());
        package.Add("LICENSE.txt", "license"u8.ToArray());
        package.Add("content/readme me.txt", "spaced name"u8.ToArray());
        package.Dependencies.Add(("netstandard2.0", "Other.Unsigned", "1.0.0"));
        package.Dependencies.Add(("netstandard2.0", "Newtonsoft.Json", "13.0.1"));
        customize?.Invoke(package);
        return package.Write(Path.Combine(Work, "input feed"));
    }

    [Test]
    public async Task Signs_every_managed_assembly_in_every_group_with_one_key_and_rewrites_references()
    {
        string input = BuildMutualPackage();
        string inputHash = Sha256(input);
        string key = WriteKey(TestKeys.SigningKey);
        string token = TokenOf(TestKeys.SigningKey);
        string output = Path.Combine(Work, "signed output");

        CliResult result = await RunAsync("sign-package", "--input", input, "--key-file", key, "--output", output,
            "--output-version", "2.0.0-signed", "--map-package", "Other.Unsigned=Other.Unsigned@1.0.0-signed");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.ToString());
        Assert.That(result.Status, Is.EqualTo("signed"));
        Assert.That(Sha256(input), Is.EqualTo(inputHash), "input must stay immutable");
        string outputPath = result.Json.GetProperty("outputPath").GetString()!;
        Assert.That(outputPath, Is.EqualTo(Path.Combine(Path.GetFullPath(output), "Vendor.Sdk.2.0.0-signed.nupkg")));
        Assert.That(result.Json.GetProperty("outputSha256").GetString(), Is.EqualTo(Sha256(outputPath)));
        Assert.That(result.Json.GetProperty("publicKeyToken").GetString(), Is.EqualTo(token));

        Dictionary<string, byte[]> original = ReadEntries(input);
        Dictionary<string, byte[]> signed = ReadEntries(outputPath);
        Assert.That(signed.Keys, Does.Not.Contain(".signature.p7s"));
        Assert.That(signed.Keys.Order(), Is.EquivalentTo(original.Keys.Where(k => k != ".signature.p7s").Order()));

        foreach ((string entry, byte[] bytes) in signed.Where(e => e.Key.EndsWith(".dll")))
        {
            AssemblyInfo? info = Inspect(bytes);
            if (info is null)
            {
                Assert.That(bytes, Is.EqualTo(original[entry]), $"native {entry} must be byte-identical");
                continue;
            }

            Assert.That(info.PublicKeyToken, Is.EqualTo(token), entry);
            Assert.That(info.Version, Is.EqualTo(new Version(1, 2, 3, 4)), entry);
            Assert.That(info.Signature, Is.EqualTo(info.IsReferenceAssembly ? info.Signature : SignatureState.Valid),
                entry);
            foreach (AssemblyReferenceInfo reference in info.References.Where(r => r.Name.StartsWith("Vendor.")))
            {
                Assert.That(reference.PublicKeyToken, Is.EqualTo(token), $"{entry} -> {reference.Name}");
            }
        }

        foreach (string name in new[]
                                {
                                    "LICENSE.txt", "build/Vendor.Sdk.targets", "content/readme me.txt",
                                    "lib/netstandard2.0/Vendor.Core.xml"
                                })
        {
            Assert.That(signed[name], Is.EqualTo(original[name]), name);
        }

        string nuspec = Encoding.UTF8.GetString(signed["Vendor.Sdk.nuspec"]);
        Assert.That(nuspec, Does.Contain("<version>2.0.0-signed</version>"));
        Assert.That(nuspec, Does.Contain("id=\"Other.Unsigned\" version=\"1.0.0-signed\""));
        Assert.That(nuspec, Does.Contain("id=\"Newtonsoft.Json\" version=\"13.0.1\""));
        Assert.That(Encoding.UTF8.GetString(signed["package/services/metadata/core-properties/abc.psmdcp"]),
            Does.Contain("<version>2.0.0-signed</version>"));

        Assert.That(result.Assembly("lib/net48", "Vendor.Core.dll").GetProperty("outcome").GetString(),
            Is.EqualTo("signed"));
        Assert.That(
            result.Assembly("runtimes/win-x64/lib/netstandard2.0", "Vendor.Native.Interop.dll").GetProperty("outcome")
                .GetString(), Is.EqualTo("signed"));
        Assert.That(
            result.Assembly("lib/netstandard2.0", "vendor_helper_native.dll").GetProperty("outcome").GetString(),
            Is.EqualTo("native"));
        Assert.That(
            result.Assembly("lib/netstandard2.0", "de/Vendor.Core.resources.dll").GetProperty("outcome").GetString(),
            Is.EqualTo("signed"));
        Assert.That(result.Json.GetProperty("dependencyChanges").GetArrayLength(), Is.EqualTo(1));
        Assert.That(result.StdOut, Does.Contain($"Vendor.Data, Version=1.2.3.4 -> {token}"),
            "JSON stays human-readable");
    }

    [Test]
    public async Task Rewrites_internals_visible_to_and_type_arguments_for_signed_friends()
    {
        string input = BuildMutualPackage();
        string key = WriteKey(TestKeys.SigningKey);
        CliResult result = await RunAsync("sign-package", "--input", input, "--key-file", key, "--output", Work,
            "--output-version", "2.0.0-signed");
        Assert.That(result.ExitCode, Is.EqualTo(0), result.ToString());

        Dictionary<string, byte[]> signed = ReadEntries(result.Json.GetProperty("outputPath").GetString()!);
        string publicKeyHex = Convert.ToHexStringLower(StrongNameKey.FromBlob(TestKeys.SigningKey).PublicKey);
        List<string> core = AttributeBlobs(signed["lib/net48/Vendor.Core.dll"]);
        Assert.That(core, Has.Some.Contains($"Vendor.Service, PublicKey={publicKeyHex}"));
        Assert.That(core.Single(b => b.Contains("Vendor.Tests")), Does.Not.Contain("PublicKey"),
            "friends outside the package are left unchanged");

        List<string> service = AttributeBlobs(signed["lib/net48/Vendor.Service.dll"]);
        Assert.That(service, Has.Some.Contains(
            $"Vendor.Core, Version=1.2.3.4, Culture=neutral, PublicKeyToken={TokenOf(TestKeys.SigningKey)}"));
        Assert.That(service, Has.None.Contains("PublicKeyToken=null"));
    }

    [Test]
    public async Task Already_strong_named_package_is_a_no_op_without_key_or_output()
    {
        string vendorToken = TokenOf(TestKeys.VendorKey);
        var package = new FixturePackage { Id = "Signed.Vendor", Version = "3.1.0" };
        foreach (string tfm in m_frameworks)
        {
            package.Add($"lib/{tfm}",
                new FixtureAssembly
                { Name = "Signed.A", References = [$"Signed.B={vendorToken}"], Key = TestKeys.VendorKey });
            package.Add($"lib/{tfm}",
                new FixtureAssembly
                { Name = "Signed.B", References = [$"Signed.A={vendorToken}"], Key = TestKeys.VendorKey });
        }

        // Public-signed reference assemblies follow the reference-assembly convention.
        package.Add("ref/netstandard2.0",
            new FixtureAssembly
            { Name = "Signed.A", Key = TestKeys.VendorKey, PublicSignOnly = true, ReferenceAssembly = true });
        string input = package.Write(Work);
        string output = Path.Combine(Work, "never created");

        CliResult result = await RunAsync("sign-package", "--input", input, "--output", output);

        Assert.That(result.ExitCode, Is.EqualTo(0), result.ToString());
        Assert.That(result.Status, Is.EqualTo("already-strong-named"));
        Assert.That(result.Json.GetProperty("outputPath").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(result.Json.GetProperty("packageId").GetString(), Is.EqualTo("Signed.Vendor"));
        Assert.That(result.Json.GetProperty("version").GetString(), Is.EqualTo("3.1.0"));
        Assert.That(Directory.Exists(output), Is.False);
        Assert.That(result.Assemblies.Select(a => a.GetProperty("outcome").GetString()), Is.All.EqualTo("preserved"));
    }

    [Test]
    public async Task Unsigned_package_without_signing_options_fails_with_exit_2_and_names_missing_options()
    {
        string input = BuildMutualPackage();
        CliResult result = await RunAsync("sign-package", "--input", input);

        Assert.That(result.ExitCode, Is.EqualTo(2), result.ToString());
        Assert.That(result.Status, Is.EqualTo("invalid-arguments"));
        string? error = result.Json.GetProperty("errors")[0].GetString();
        Assert.That(error, Does.Contain("--key-file").And.Contain("--output").And.Contain("--output-version"));
        Assert.That(result.StdErr, Does.Contain("--key-file"));
    }

    [Test]
    public async Task Mixed_package_preserves_valid_vendor_signatures_and_signs_the_rest()
    {
        string vendorToken = TokenOf(TestKeys.VendorKey);
        string input = new FixturePackage { Id = "Mixed", Version = "1.0.0" }
            .Add("lib/netstandard2.0", new FixtureAssembly { Name = "Mixed.Signed", Key = TestKeys.VendorKey })
            .Add("lib/netstandard2.0",
                new FixtureAssembly { Name = "Mixed.Unsigned", References = [$"Mixed.Signed={vendorToken}"] })
            .Write(Work);
        string key = WriteKey(TestKeys.SigningKey);

        CliResult result = await RunAsync("sign-package", "--input", input, "--key-file", key, "--output", Work,
            "--output-version", "1.0.0-signed");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.ToString());
        Assert.That(result.Assembly("lib/netstandard2.0", "Mixed.Signed.dll").GetProperty("outcome").GetString(),
            Is.EqualTo("preserved"));
        Assert.That(result.Assembly("lib/netstandard2.0", "Mixed.Signed.dll").GetProperty("publicKeyToken").GetString(),
            Is.EqualTo(vendorToken));
        Assert.That(result.Assembly("lib/netstandard2.0", "Mixed.Unsigned.dll").GetProperty("outcome").GetString(),
            Is.EqualTo("signed"));
        Dictionary<string, byte[]> original = ReadEntries(input);
        Dictionary<string, byte[]> signed = ReadEntries(result.Json.GetProperty("outputPath").GetString()!);
        Assert.That(signed["lib/netstandard2.0/Mixed.Signed.dll"],
            Is.EqualTo(original["lib/netstandard2.0/Mixed.Signed.dll"]));
    }

    [Test]
    public async Task Vendor_signed_assembly_that_needs_a_rewrite_fails_without_its_key()
    {
        string input = new FixturePackage { Id = "NeedsVendorKey", Version = "1.0.0" }
            .Add("lib/netstandard2.0",
                new FixtureAssembly { Name = "V.Signed", References = ["V.Unsigned"], Key = TestKeys.VendorKey })
            .Add("lib/netstandard2.0", new FixtureAssembly { Name = "V.Unsigned" })
            .Write(Work);
        string key = WriteKey(TestKeys.SigningKey);
        string output = Path.Combine(Work, "out");

        CliResult result = await RunAsync("sign-package", "--input", input, "--key-file", key, "--output", output,
            "--output-version", "1.0.0-signed");

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.Status, Is.EqualTo("failed"));
        Assert.That(string.Join("\n", result.Json.GetProperty("errors").EnumerateArray().Select(e => e.GetString())),
            Does.Contain("V.Signed.dll").And.Contain(TokenOf(TestKeys.VendorKey)));
        Assert.That(Directory.Exists(output), Is.False);
    }

    [Test]
    public async Task Invalid_signature_with_a_foreign_key_fails_but_with_the_matching_key_is_resigned()
    {
        byte[] tampered = new FixtureAssembly { Name = "Tampered", Key = TestKeys.SigningKey }.Build();
        tampered[^20] ^= 0xFF;
        Assert.That(Inspect(tampered)!.Signature, Is.EqualTo(SignatureState.Invalid));
        string input = new FixturePackage { Id = "Tampered", Version = "1.0.0" }
            .Add("lib/netstandard2.0/Tampered.dll", tampered).Write(Work);

        CliResult foreign = await RunAsync("sign-package", "--input", input, "--key-file",
            WriteKey(TestKeys.VendorKey, "vendor.snk"),
            "--output", Work, "--output-version", "1.0.0-signed");
        Assert.That(foreign.ExitCode, Is.EqualTo(1), foreign.ToString());

        CliResult matching = await RunAsync("sign-package", "--input", input, "--key-file",
            WriteKey(TestKeys.SigningKey),
            "--output", Work, "--output-version", "1.0.0-signed");
        Assert.That(matching.ExitCode, Is.EqualTo(0), matching.ToString());
        Assert.That(matching.Assembly("lib/netstandard2.0", "Tampered.dll").GetProperty("outcome").GetString(),
            Is.EqualTo("resigned"));
        Assert.That(matching.Assembly("lib/netstandard2.0", "Tampered.dll").GetProperty("signature").GetString(),
            Is.EqualTo("valid"));
    }

    [Test]
    public async Task Unmapped_unsigned_dependency_fails_and_explicit_mapping_resolves_it()
    {
        string input = new FixturePackage { Id = "Dependent", Version = "1.0.0" }
            .Add("lib/netstandard2.0",
                new FixtureAssembly { Name = "Dependent", References = ["Other.Lib", "Third.Lib"] })
            .Write(Work);
        string key = WriteKey(TestKeys.SigningKey);

        CliResult missing = await RunAsync("sign-package", "--input", input, "--key-file", key, "--output", Work,
            "--output-version", "1.0.0-signed");
        Assert.That(missing.ExitCode, Is.EqualTo(1), missing.ToString());
        Assert.That(missing.StdOut, Does.Contain("Other.Lib").And.Contain("--map-assembly"));

        CliResult mapped = await RunAsync("sign-package", "--input", input, "--key-file", key, "--output", Work,
            "--output-version", "1.0.0-signed", "--map-assembly", "Other.Lib", "--map-assembly",
            "Third.Lib=0123456789abcdef");
        Assert.That(mapped.ExitCode, Is.EqualTo(0), mapped.ToString());
        AssemblyInfo? info = Inspect(ReadEntries(mapped.Json.GetProperty("outputPath").GetString()!)
            ["lib/netstandard2.0/Dependent.dll"]);
        Assert.That(info!.References.Single(r => r.Name == "Other.Lib").PublicKeyToken,
            Is.EqualTo(TokenOf(TestKeys.SigningKey)));
        Assert.That(info.References.Single(r => r.Name == "Third.Lib").PublicKeyToken, Is.EqualTo("0123456789abcdef"));
    }

    [Test]
    public async Task Existing_output_is_never_overwritten()
    {
        string input = BuildMutualPackage();
        string existing = Path.Combine(Work, "Vendor.Sdk.2.0.0-signed.nupkg");
        await File.WriteAllTextAsync(existing, "keep me");

        CliResult result = await RunAsync("sign-package", "--input", input, "--key-file", WriteKey(TestKeys.SigningKey),
            "--output", Work, "--output-version", "2.0.0-signed");

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(await File.ReadAllTextAsync(existing), Is.EqualTo("keep me"));
    }

    [Test]
    public async Task Output_id_renames_the_manifest()
    {
        string input = BuildMutualPackage();
        CliResult result = await RunAsync("sign-package", "--input", input, "--key-file", WriteKey(TestKeys.SigningKey),
            "--output", Work, "--output-id", "Vendor.Sdk.Signed", "--output-version", "2.0.0");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.ToString());
        Dictionary<string, byte[]> entries = ReadEntries(result.Json.GetProperty("outputPath").GetString()!);
        Assert.That(entries.Keys, Does.Contain("Vendor.Sdk.Signed.nuspec").And.Not.Contain("Vendor.Sdk.nuspec"));
        Assert.That(Encoding.UTF8.GetString(entries["_rels/.rels"]),
            Does.Contain("Target=\"/Vendor.Sdk.Signed.nuspec\""));
        Assert.That(Encoding.UTF8.GetString(entries["Vendor.Sdk.Signed.nuspec"]),
            Does.Contain("<id>Vendor.Sdk.Signed</id>"));
    }

    [Test]
    public async Task Portable_pdb_is_rewritten_to_match_the_signed_assembly()
    {
        string input = new FixturePackage { Id = "WithPdb", Version = "1.0.0" }
            .Add("lib/netstandard2.0", new FixtureAssembly { Name = "WithPdb", PortablePdb = true })
            .Write(Work);

        CliResult result = await RunAsync("sign-package", "--input", input, "--key-file", WriteKey(TestKeys.SigningKey),
            "--output", Work, "--output-version", "1.0.0-signed");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.ToString());
        Dictionary<string, byte[]> entries = ReadEntries(result.Json.GetProperty("outputPath").GetString()!);
        using var pe = new PEReader(new MemoryStream(entries["lib/netstandard2.0/WithPdb.dll"]));
        DebugDirectoryEntry cvEntry = pe.ReadDebugDirectory().First(e => e.Type == DebugDirectoryEntryType.CodeView);
        CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(cvEntry);
        using MetadataReaderProvider pdb = MetadataReaderProvider.FromPortablePdbStream(
            new MemoryStream(entries["lib/netstandard2.0/WithPdb.pdb"]));
        var pdbId = new BlobContentId(pdb.GetMetadataReader().DebugMetadataHeader!.Id);
        Assert.That(pdbId.Guid, Is.EqualTo(codeView.Guid));
        Assert.That(pdbId.Stamp, Is.EqualTo(cvEntry.Stamp));
        Assert.That(codeView.Age, Is.EqualTo(1));
    }

    [Test]
    public async Task Corrupt_input_package_fails_with_exit_1()
    {
        string input = Path.Combine(Work, "broken package.nupkg");
        await File.WriteAllTextAsync(input, "this is not a zip");

        CliResult result = await RunAsync("sign-package", "--input", input, "--key-file", WriteKey(TestKeys.SigningKey),
            "--output", Work, "--output-version", "1.0.0");

        Assert.That(result.ExitCode, Is.EqualTo(1), result.ToString());
        Assert.That(result.Status, Is.EqualTo("failed"));
    }

    [Test]
    public async Task Key_file_without_private_key_is_an_argument_error()
    {
        string input = BuildMutualPackage();
        string publicOnly = WriteKey(StrongNameKey.FromBlob(TestKeys.SigningKey).PublicKey, "public.snk");

        CliResult result = await RunAsync("sign-package", "--input", input, "--key-file", publicOnly, "--output", Work,
            "--output-version", "1.0.0");

        Assert.That(result.ExitCode, Is.EqualTo(2), result.ToString());
        Assert.That(result.StdOut, Does.Not.Contain(Convert.ToHexString(TestKeys.SigningKey.AsSpan(20, 16))));
    }

    private static List<string> AttributeBlobs(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        MetadataReader md = pe.GetMetadataReader();
        return md.CustomAttributes.Select(h => Encoding.UTF8.GetString(md.GetBlobBytes(md.GetCustomAttribute(h).Value)))
            .ToList();
    }
}
