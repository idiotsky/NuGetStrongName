using System.Security.Cryptography;
using JIT.NuGetStrongName.Acquisition;
using JIT.NuGetStrongName.Cli;
using JIT.NuGetStrongName.Packaging;
using JIT.NuGetStrongName.Reporting;
using JIT.NuGetStrongName.Signing;

namespace JIT.NuGetStrongName.Processing;

/// <summary>A processing or verification failure (exit code 1).</summary>
public sealed class ProcessingException(string message, IEnumerable<string>? details = null) : Exception(message)
{
    public IReadOnlyList<string> Details { get; } = details?.ToList() ?? [];
}

public sealed class PackageCommands(Action<string> diagnostics)
{
    public async Task SignAsync(CommandOptions options, Report report, CancellationToken cancellationToken)
    {
        using var workspace = Workspace.Create();
        string input = await AcquireAsync(options, report, workspace, cancellationToken);
        PackageArchive archive = PackageArchive.Extract(input, workspace.Directory("package"), cancellationToken);
        string nuspecPath = archive.NuspecEntry.FullPath;
        (report.PackageId, report.Version) = ManifestEditor.ReadIdentity(nuspecPath);

        List<PackageAsset> assets = PackageVerifier.CollectAssets(archive);
        Dictionary<string, string> assemblyMappings = options.AssemblyMappings;
        SigningPlan precheck = SigningPlanner.Plan(assets, null, assemblyMappings);
        IReadOnlyList<DependencyChange> dependencyChanges =
            ManifestEditor.PreviewDependencyChanges(nuspecPath, options.PackageMappings);

        if (!precheck.RequiresAssemblyWork && dependencyChanges.Count == 0)
        {
            report.Status = "already-strong-named";
            report.OutputPath = null;
            Describe(report, precheck, null);
            diagnostics($"{report.PackageId} {report.Version}: every managed assembly is already strong-named; " +
                        $"nothing to do.");
            return;
        }

        // Signing work is required: now (and only now) the signing inputs are mandatory.
        var missing = new List<string>();
        if (options.KeyFile is null)
        {
            missing.Add("--key-file");
        }

        if (options.Output is null)
        {
            missing.Add("--output");
        }

        if (options.OutputVersion is null)
        {
            missing.Add("--output-version");
        }

        if (missing.Count > 0)
        {
            Describe(report, precheck, null);
            throw new UsageException($"{report.PackageId} {report.Version} needs strong-name signing; " +
                                     $"missing required option(s): {string.Join(", ", missing)}.");
        }

        StrongNameKey key;
        try
        {
            key = StrongNameKey.Load(options.KeyFile!);
        }
        catch (StrongNameException ex)
        {
            throw new UsageException(ex.Message);
        }

        report.PublicKeyToken = key.PublicKeyTokenHex;
        string outputId = options.OutputId ?? report.PackageId!;
        report.OutputId = outputId;
        report.OutputVersion = options.OutputVersion;
        string outputDirectory = Path.GetFullPath(options.Output!);
        string outputPath = Path.Combine(outputDirectory, $"{outputId}.{options.OutputVersion}.nupkg");
        if (File.Exists(outputPath))
        {
            throw new ProcessingException($"Output '{outputPath}' already exists; it is never overwritten.");
        }

        Dictionary<string, string> mappings = assemblyMappings.ToDictionary(kv => kv.Key,
            kv => kv.Value.Length == 0 ? key.PublicKeyTokenHex : kv.Value, StringComparer.OrdinalIgnoreCase);
        SigningPlan plan = SigningPlanner.Plan(assets, key.PublicKeyTokenHex, mappings);
        if (plan.Failures.Any())
        {
            Describe(report, plan, null);
            throw new ProcessingException("The package cannot be strong-named as requested.", plan.Failures);
        }

        foreach (AssetPlan assetPlan in plan.Assets.Where(p => p.Action is AssetAction.Sign or AssetAction.Resign &&
                                                               p.Asset.Info!.HasAuthenticode))
        {
            report.Warnings.Add($"{assetPlan.Asset.EntryPath}: Authenticode signature removed by rewriting");
        }

        if (archive.Entries.Any(e => e.Name.EqualsIgnoreCase(PackageArchive.PackageSignatureEntry)))
        {
            report.Warnings.Add("NuGet package signature (.signature.p7s) removed: repacking invalidates it");
        }

        var results = new Dictionary<AssetPlan, RewriteResult>();
        foreach (AssetPlan assetPlan in plan.Assets.Where(p => p.Action is AssetAction.Sign or AssetAction.Resign))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.GetDirectoryName(assetPlan.Asset.FullPath)!;
            var searchDirectories = new List<string> { directory };
            if (assetPlan.Asset.Group.CompanionLibPath is { } lib && Directory.Exists(Path.Combine(archive.Root, lib)))
            {
                searchDirectories.Add(Path.Combine(archive.Root, lib));
            }

            try
            {
                results[assetPlan] = AssemblyRewriter.Rewrite(
                    assetPlan.Asset.FullPath, key, assetPlan.ReferenceRewrites, plan.SignedNames, searchDirectories);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ProcessingException($"Failed to rewrite or sign {assetPlan.Asset.EntryPath}: {ex.Message}");
            }

            diagnostics($"{assetPlan.Action.ToString().ToLowerInvariant()}: {assetPlan.Asset.EntryPath}");
        }

        report.DependencyChanges.AddRange(ManifestEditor.RewriteManifest(nuspecPath, options.PackageMappings, outputId,
            options.OutputVersion!));
        foreach (ArchiveEntry psmdcp in archive.Entries
                     .Where(e => e.Name.StartsWithIgnoreCase("package/services/metadata/core-properties/")
                                 && e.Name.EndsWithIgnoreCase(".psmdcp")))
        {
            ManifestEditor.UpdateCoreProperties(psmdcp.FullPath, outputId, options.OutputVersion!);
        }

        Dictionary<string, string> renamed = RenameManifest(archive, outputId);
        string staged = Path.Combine(workspace.Directory("out"), "package.nupkg");
        archive.WritePackage(staged, renamed);

        // Verify the package exactly as it will be delivered.
        PackageArchive check = PackageArchive.Extract(staged, workspace.Directory("verify"), cancellationToken);
        List<PackageAsset> checkAssets = PackageVerifier.CollectAssets(check);
        List<VerificationFinding> findings = PackageVerifier.Verify(checkAssets, null);
        foreach (AssetPlan signedAsset in plan.Assets.Where(p => p.Action is AssetAction.Sign or AssetAction.Resign))
        {
            PackageAsset? produced = checkAssets.FirstOrDefault(a => a.EntryPath == signedAsset.Asset.EntryPath);
            if (produced is null)
            {
                findings.Add(new VerificationFinding(signedAsset.Asset.EntryPath, "missing from the repacked package"));
                continue;
            }

            if (produced.Info?.Signature != SignatureState.Valid ||
                produced.Info.PublicKeyToken != key.PublicKeyTokenHex)
            {
                findings.Add(new VerificationFinding(produced.EntryPath,
                    $"expected a valid signature with token {key.PublicKeyTokenHex}"));
            }

            if (produced.Info is not null && produced.Info.Version != signedAsset.Asset.Info!.Version)
            {
                findings.Add(new VerificationFinding(produced.EntryPath, "assembly version changed"));
            }
        }

        Describe(report, plan, results, checkAssets);
        report.Warnings.AddRange(findings.Where(f => f.IsWarning).Select(f => $"{f.EntryPath}: {f.Message}"));
        findings.RemoveAll(f => f.IsWarning);
        if (findings.Count > 0)
        {
            throw new ProcessingException("Verification of the signed package failed.",
                findings.Select(f => $"{f.EntryPath}: {f.Message}"));
        }

        Directory.CreateDirectory(outputDirectory);
        string pending = Path.Combine(outputDirectory, $".{outputId}.{options.OutputVersion}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(staged, pending);
            File.Move(pending, outputPath, false);
        }
        catch (IOException ex)
        {
            throw new ProcessingException($"Could not deliver '{outputPath}': {ex.Message}");
        }
        finally
        {
            File.Delete(pending);
        }

        report.OutputPath = outputPath;
        report.OutputSha256 = Sha256(outputPath);
        report.Status = "signed";
    }

    public async Task VerifyAsync(CommandOptions options, Report report, CancellationToken cancellationToken)
    {
        using var workspace = Workspace.Create();
        string input = await AcquireAsync(options, report, workspace, cancellationToken);
        PackageArchive archive = PackageArchive.Extract(input, workspace.Directory("package"), cancellationToken);
        (report.PackageId, report.Version) = ManifestEditor.ReadIdentity(archive.NuspecEntry.FullPath);
        List<PackageAsset> assets = PackageVerifier.CollectAssets(archive);
        List<VerificationFinding> findings = PackageVerifier.Verify(assets, options.PublicKeyToken);
        report.PublicKeyToken = options.PublicKeyToken;
        foreach (IGrouping<string, PackageAsset> group in assets.GroupBy(a => a.Group.Path))
        {
            var groupReport = new AssetGroupReport { Path = group.Key };
            foreach (PackageAsset asset in group)
            {
                AssemblyReport assemblyReport = DescribeAsset(asset, asset.Info is null
                    ? "native"
                    : asset.Info.IsStrongNamedForRole(asset.Group.IsReference)
                        ? "strong-named"
                        : "not-strong-named");
                assemblyReport.Issues.AddRange(findings.Where(f => f.EntryPath == asset.EntryPath)
                    .Select(f => f.Message));
                groupReport.Assemblies.Add(assemblyReport);
            }

            report.AssetGroups.Add(groupReport);
        }

        report.Warnings.AddRange(findings.Where(f => f.IsWarning).Select(f => $"{f.EntryPath}: {f.Message}"));
        findings.RemoveAll(f => f.IsWarning);
        if (findings.Count > 0)
        {
            throw new ProcessingException("The package is not fully strong-named.",
                findings.Select(f => $"{f.EntryPath}: {f.Message}"));
        }

        report.Status = "verified";
    }

    private async Task<string> AcquireAsync(CommandOptions options, Report report, Workspace workspace,
        CancellationToken cancellationToken)
    {
        string path;
        if (options.Input is not null)
        {
            path = Path.GetFullPath(options.Input);
            if (!File.Exists(path))
            {
                throw new UsageException($"Input package '{path}' does not exist.");
            }
        }
        else
        {
            var request = new DownloadRequest(options.PackageId!, options.Version!,
                Path.GetFullPath(options.RepositoryRoot), options.ConfigFile);
            (path, string source) = await PackageDownloader.DownloadAsync(request, workspace.Directory("download"),
                diagnostics, cancellationToken);
            diagnostics($"Downloaded {options.PackageId} {options.Version} from '{source}'.");
            report.InputSource = source;
            report.InputSha256 = Sha256(path);
            return path; // a temporary download: no durable input path to report
        }

        report.InputPath = path;
        report.InputSha256 = Sha256(path);
        return path;
    }

    private static Dictionary<string, string> RenameManifest(PackageArchive archive, string outputId)
    {
        var renamed = new Dictionary<string, string>();
        string oldName = archive.NuspecEntry.Name;
        var newName = $"{outputId}.nuspec";
        if (!oldName.Equals(newName, StringComparison.Ordinal))
        {
            renamed[oldName] = newName;
            foreach (ArchiveEntry rels in archive.Entries.Where(e => e.Name.EqualsIgnoreCase("_rels/.rels")))
            {
                string text = File.ReadAllText(rels.FullPath);
                string updated = text.Replace($"/{oldName}\"", $"/{newName}\"", StringComparison.Ordinal);
                if (updated == text)
                {
                    throw new ProcessingException($"_rels/.rels does not reference '/{oldName}' in a form this tool " +
                                                  $"can rename; use the input ID.");
                }

                File.WriteAllText(rels.FullPath, updated);
            }
        }

        return renamed;
    }

    private static void Describe(Report report, SigningPlan plan, Dictionary<AssetPlan, RewriteResult>? results,
        List<PackageAsset>? produced = null)
    {
        report.AssetGroups.Clear();
        foreach (IGrouping<string, AssetPlan> group in plan.Assets.GroupBy(p => p.Asset.Group.Path))
        {
            var groupReport = new AssetGroupReport { Path = group.Key };
            foreach (AssetPlan assetPlan in group)
            {
                string outcome = assetPlan.Action switch
                {
                    AssetAction.Native => "native",
                    AssetAction.Preserve => "preserved",
                    AssetAction.Fail => "failed",
                    _ when results is null => assetPlan.Action == AssetAction.Sign
                        ? "requires-signing"
                        : "requires-resigning",
                    AssetAction.Sign => "signed",
                    _ => "resigned",
                };

                PackageAsset final = produced?.FirstOrDefault(a => a.EntryPath == assetPlan.Asset.EntryPath) ??
                                     assetPlan.Asset;
                AssemblyReport assemblyReport = DescribeAsset(final, outcome);
                assemblyReport.OriginalPublicKeyToken = assetPlan.Asset.Info?.PublicKeyToken;
                assemblyReport.Issues.AddRange(assetPlan.Issues);
                if (results is not null && results.TryGetValue(assetPlan, out RewriteResult? result))
                {
                    assemblyReport.ReferencesRewritten.AddRange(result.ReferencesRewritten);
                    foreach (string warning in result.Warnings)
                    {
                        report.Warnings.Add($"{assetPlan.Asset.EntryPath}: {warning}");
                    }
                }

                if (assetPlan.Action == AssetAction.Preserve)
                {
                    report.Warnings.AddRange(assetPlan.Issues.Select(i => $"{assetPlan.Asset.EntryPath}: {i}"));
                }

                groupReport.Assemblies.Add(assemblyReport);
            }

            report.AssetGroups.Add(groupReport);
        }
    }

    private static AssemblyReport DescribeAsset(PackageAsset asset, string outcome)
    {
        return new AssemblyReport
               {
                   File = asset.FileName,
                   Name = asset.Info?.Name,
                   AssemblyVersion = asset.Info?.Version.ToString(),
                   PublicKeyToken = asset.Info?.PublicKeyToken,
                   Signature = asset.Info?.Signature.ToString().ToLowerInvariant(),
                   Outcome = outcome,
               };
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>A unique temporary directory removed on dispose.</summary>
public sealed class Workspace : IDisposable
{
    private Workspace(string root)
    {
        Root = root;
    }

    private string Root { get; }

    public static Workspace Create()
    {
        string root = Path.Combine(Path.GetTempPath(), "nuget-strongname", Guid.NewGuid().ToString("N"));
        // Extracted package contents stay private to the current user (0700 on Unix).
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(root);
        }
        else
        {
            System.IO.Directory.CreateDirectory(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new Workspace(root);
    }

    public string Directory(string name)
    {
        string path = Path.Combine(Root, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a locked temp file must not turn a result into a failure.
        }
    }
}
