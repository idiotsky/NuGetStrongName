using JIT.NuGetStrongName.Packaging;
using NuGet.Packaging;
using NuGet.Versioning;

namespace JIT.NuGetStrongName.Cli;

public sealed class UsageException(string message) : Exception(message);

public enum CommandKind
{
    Help,
    ToolVersion,
    SignPackage,
    VerifyPackage,
}

public sealed class CommandOptions
{
    public CommandKind Kind { get; init; }

    public string? Input { get; set; }

    public string? PackageId { get; set; }

    public string? Version { get; set; }

    public string RepositoryRoot { get; set; } = Directory.GetCurrentDirectory();

    public string? ConfigFile { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    public string? KeyFile { get; set; }

    public string? Output { get; set; }

    public string? OutputId { get; set; }

    public string? OutputVersion { get; set; }

    public string? PublicKeyToken { get; set; }

    public Dictionary<string, PackageDependencyMapping> PackageMappings { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assembly name → token; an empty token means "signed with the same --key-file".</summary>
    public Dictionary<string, string> AssemblyMappings { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ArgumentParser
{
    public const string HelpText =
        """
        nuget-strongname — strong-name sign the managed assemblies of a NuGet package.

        Usage:
          nuget-strongname sign-package (--input <nupkg> | --package-id <id> --version <version>)
                                        [--key-file <snk>] [--output <dir>] [--output-version <version>]
                                        [--output-id <id>] [--map-package <id>=<newId>@<version>]...
                                        [--map-assembly <name>[=<publicKeyToken>]]...
                                        [--repository <dir>] [--configfile <nuget.config>] [--timeout <seconds>]
          nuget-strongname verify-package (--input <nupkg> | --package-id <id> --version <version>)
                                          [--public-key-token <token>] [--repository <dir>]
                                          [--configfile <nuget.config>] [--timeout <seconds>]
          nuget-strongname --help
          nuget-strongname --version

        sign-package inspects the package first. When every managed assembly is already strong-named and no
        rewrite is required it reports "already-strong-named" without reading a key or writing output;
        otherwise --key-file, --output and --output-version are required. Output is written to
        <output>/<output-id>.<output-version>.nupkg and is never overwritten.

          --input             Local .nupkg to process (never modified).
          --package-id        Upstream package ID to download (with --version).
          --version           Upstream package version (inside a subcommand). At top level: print the tool version.
          --key-file          Strong-name key pair (.snk) used for every assembly that must be signed.
          --output            Directory that receives the signed package.
          --output-id         Package ID of the output (default: the input ID).
          --output-version    Package version of the output, e.g. 1.2.3-signed.
          --map-package       Rewrite a nuspec dependency, e.g. Foo=Foo@1.2.3-signed.
          --map-assembly      Token for an assembly from another package; without a token the --key-file token is used.
          --repository        Directory whose effective NuGet configuration is used (default: current directory).
          --configfile        Explicit NuGet configuration file.
          --timeout           Overall timeout in seconds (default: 600).
          --public-key-token  verify-package: require every managed assembly to carry this token.

        stdout receives one JSON report; diagnostics go to stderr.
        Exit codes: 0 success, 1 processing or verification failure, 2 invalid arguments.
        """;

    public static CommandOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new UsageException("No command given. Use --help for usage.");
        }

        switch (args[0])
        {
            case "--help" or "-h" or "-?" or "help":
                return new CommandOptions { Kind = CommandKind.Help };
            case "--version":
                if (args.Count > 1)
                {
                    throw new UsageException("--version at top level takes no arguments.");
                }

                return new CommandOptions { Kind = CommandKind.ToolVersion };
        }

        CommandKind kind = args[0] switch
        {
            "sign-package" => CommandKind.SignPackage,
            "verify-package" => CommandKind.VerifyPackage,
            _ => throw new UsageException($"Unknown command '{args[0]}'. Use --help for usage."),
        };

        var options = new CommandOptions { Kind = kind };
        for (var i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is "--help" or "-h")
            {
                return new CommandOptions { Kind = CommandKind.Help };
            }

            string name;
            string? inline = null;
            int eq = arg.IndexOf('=');
            if (arg.StartsWithIgnoreCase("--") && eq > 0)
            {
                name = arg[..eq];
                inline = arg[(eq + 1)..];
            }
            else
            {
                name = arg;
            }

            string Value()
            {
                if (inline is not null)
                {
                    return inline;
                }

                if (i + 1 >= args.Count || args[i + 1].StartsWithIgnoreCase("--"))
                {
                    throw new UsageException($"Option '{name}' requires a value.");
                }

                return args[++i];
            }

            bool signOnly = kind == CommandKind.SignPackage;
            switch (name)
            {
                case "--input":
                    options.Input = Value();
                    break;
                case "--package-id":
                    options.PackageId = Value();
                    break;
                case "--version":
                    options.Version = Value();
                    break;
                case "--repository":
                    options.RepositoryRoot = Value();
                    break;
                case "--configfile":
                    options.ConfigFile = Value();
                    break;
                case "--timeout":
                    string timeoutText = Value();
                    if (!int.TryParse(timeoutText, out int seconds) || seconds <= 0)
                    {
                        throw new UsageException(
                            $"--timeout must be a positive number of seconds, not '{timeoutText}'.");
                    }

                    options.Timeout = TimeSpan.FromSeconds(seconds);
                    break;
                case "--key-file" when signOnly:
                    options.KeyFile = Value();
                    break;
                case "--output" when signOnly:
                    options.Output = Value();
                    break;
                case "--output-id" when signOnly:
                    options.OutputId = Value();
                    break;
                case "--output-version" when signOnly:
                    options.OutputVersion = Value();
                    break;
                case "--map-package" when signOnly:
                    AddPackageMapping(options, Value());
                    break;
                case "--map-assembly" when signOnly:
                    AddAssemblyMapping(options, Value());
                    break;
                case "--public-key-token" when kind == CommandKind.VerifyPackage:
                    options.PublicKeyToken = ParseToken(Value(), "--public-key-token");
                    break;
                default:
                    throw new UsageException($"Unknown option '{name}' for {args[0]}.");
            }
        }

        bool hasInput = options.Input is not null;
        bool hasRemote = options.PackageId is not null || options.Version is not null;
        if (hasInput == hasRemote)
        {
            throw new UsageException("Specify either --input or --package-id with --version.");
        }

        if (hasRemote && (options.PackageId is null || options.Version is null))
        {
            throw new UsageException("--package-id and --version must be used together.");
        }

        RequireId(options.PackageId, "--package-id");
        RequireId(options.OutputId, "--output-id");
        RequireVersion(options.Version, "--version");
        RequireVersion(options.OutputVersion, "--output-version");
        foreach ((string id, PackageDependencyMapping mapping) in options.PackageMappings)
        {
            RequireId(id, "--map-package");
            RequireId(mapping.NewId, "--map-package");
            if (!VersionRange.TryParse(mapping.NewVersion, out _))
            {
                throw new UsageException(
                    $"--map-package: '{mapping.NewVersion}' is not a valid NuGet version or range.");
            }
        }

        return options;
    }

    private static void RequireId(string? id, string option)
    {
        if (id is not null && !PackageIdValidator.IsValidPackageId(id))
        {
            throw new UsageException($"{option}: '{id}' is not a valid NuGet package ID.");
        }
    }

    private static void RequireVersion(string? version, string option)
    {
        if (version is not null && !NuGetVersion.TryParse(version, out _))
        {
            throw new UsageException($"{option}: '{version}' is not a valid NuGet version.");
        }
    }

    private static void AddPackageMapping(CommandOptions options, string value)
    {
        int eq = value.IndexOf('=');
        int at = value.LastIndexOf('@');
        if (eq <= 0 || at <= eq + 1 || at == value.Length - 1)
        {
            throw new UsageException($"--map-package expects <id>=<newId>@<version>, not '{value}'.");
        }

        options.PackageMappings[value[..eq].Trim()] =
            new PackageDependencyMapping(value[(eq + 1)..at].Trim(), value[(at + 1)..].Trim());
    }

    private static void AddAssemblyMapping(CommandOptions options, string value)
    {
        int eq = value.IndexOf('=');
        switch (eq)
        {
            case < 0:
                options.AssemblyMappings[value.Trim()] = "";
                return;
            case 0:
                throw new UsageException($"--map-assembly expects <name>[=<publicKeyToken>], not '{value}'.");
            default:
                options.AssemblyMappings[value[..eq].Trim()] = ParseToken(value[(eq + 1)..], "--map-assembly");
                break;
        }
    }

    private static string ParseToken(string value, string option)
    {
        string token = value.Trim().ToLowerInvariant();
        if (token.Length != 16 || !token.All(Uri.IsHexDigit))
        {
            throw new UsageException($"{option}: '{value}' is not a 16-digit hexadecimal public key token.");
        }

        return token;
    }
}
