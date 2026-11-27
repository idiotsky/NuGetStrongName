using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Plugins;
using NuGet.Versioning;

namespace JIT.NuGetStrongName.Acquisition;

public sealed record DownloadRequest(string PackageId, string Version, string RepositoryRoot, string? ConfigFile);

public sealed class PackageNotFoundException(string message) : Exception(message);

/// <summary>
///     Downloads an exact package version using the effective NuGet configuration of the caller's repository:
///     nuget.config hierarchy (or an explicit config file), package source mapping, stored credentials and
///     installed credential-provider plugins, all non-interactive.
/// </summary>
public static class PackageDownloader
{
    private static int m_credentialsConfigured;

    public static async Task<(string Path, string Source)> DownloadAsync(
        DownloadRequest request, string destinationDirectory, Action<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!NuGetVersion.TryParse(request.Version, out NuGetVersion? version))
        {
            throw new ArgumentException($"'{request.Version}' is not a valid NuGet version.");
        }

        var logger = new DiagnosticsLogger(diagnostics);
        ISettings settings;
        try
        {
            settings = request.ConfigFile is not null
                ? Settings.LoadSpecificSettings(Path.GetDirectoryName(Path.GetFullPath(request.ConfigFile))!,
                    Path.GetFileName(request.ConfigFile))
                : Settings.LoadDefaultSettings(request.RepositoryRoot);
        }
        catch (NuGetConfigurationException ex)
        {
            throw new InvalidDataException($"Invalid NuGet configuration: {ex.Message}", ex);
        }

        ConfigureCredentials(logger);

        var sourceProvider = new PackageSourceProvider(settings);
        List<PackageSource> sources = sourceProvider.LoadPackageSources().Where(s => s.IsEnabled).ToList();
        var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        if (mapping.IsEnabled)
        {
            IReadOnlyList<string> allowed = mapping.GetConfiguredPackageSources(request.PackageId);
            sources = sources.Where(s => allowed.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            diagnostics($"Package source mapping allows: {string.Join(", ", allowed)}");
        }

        if (sources.Count == 0)
        {
            throw new PackageNotFoundException($"No enabled package source is configured for '{request.PackageId}'.");
        }

        string destination = Path.Combine(destinationDirectory,
            $"{request.PackageId}.{version.ToNormalizedString()}.nupkg");
        using var cache = new SourceCacheContext { NoCache = true, DirectDownload = true };
        var failures = new List<string>();
        foreach (PackageSource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics($"Looking for {request.PackageId} {version} in '{source.Name}' ({Redact(source.Source)})");
            try
            {
                SourceRepository repository = Repository.Factory.GetCoreV3(source);
                FindPackageByIdResource resource =
                    await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken)
                    ?? throw new InvalidOperationException("The source does not support package download.");
                await using (var stream = new FileStream(destination, FileMode.Create, FileAccess.Write))
                {
                    if (await resource.CopyNupkgToStreamAsync(request.PackageId, version, stream, cache, logger,
                            cancellationToken))
                    {
                        return (destination, source.Name);
                    }
                }

                File.Delete(destination);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{source.Name}: {ex.GetBaseException().Message}");
                File.Delete(destination);
            }
        }

        string detail = failures.Count > 0 ? $" Source errors: {string.Join("; ", failures)}" : "";
        throw new PackageNotFoundException(
            $"Package {request.PackageId} {version} was not found in the configured sources.{detail}");
    }

    /// <summary>Drops user info and query strings, which some feeds use to carry tokens.</summary>
    public static string Redact(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && !uri.IsFile
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : source;
    }

    private static void ConfigureCredentials(ILogger logger)
    {
        if (Interlocked.Exchange(ref m_credentialsConfigured, 1) == 1)
        {
            return;
        }

        // Credentials stored in nuget.config are applied by NuGet.Protocol per source; plugins
        // (e.g. Azure Artifacts) are discovered here and never allowed to prompt.
        var providers = new AsyncLazy<IEnumerable<ICredentialProvider>>
        (async () =>
         {
             var builder = new SecurePluginCredentialProviderBuilder(PluginManager.Instance, false, logger);
             return await builder.BuildAllAsync();
         });
        HttpHandlerResourceV3.CredentialService =
            new Lazy<ICredentialService>(() => new CredentialService(providers, true, true));
    }

    private sealed class DiagnosticsLogger(Action<string> write) : LoggerBase
    {
        public override void Log(ILogMessage message)
        {
            if (message.Level >= LogLevel.Information)
            {
                write(message.Message);
            }
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}
