using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using JIT.NuGetStrongName.Acquisition;
using JIT.NuGetStrongName.Processing;
using JIT.NuGetStrongName.Reporting;

namespace JIT.NuGetStrongName.Cli;

public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int InvalidArguments = 2;
}

public static class CliApp
{
    private static readonly JsonSerializerOptions m_jsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // human-readable stdout; never embedded in HTML
        };

    private static string ToolVersion { get; } =
        (typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
         "0.0.0").Split('+')[0];

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        CommandOptions options;
        try
        {
            options = ArgumentParser.Parse(args);
        }
        catch (UsageException ex)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}", cancellationToken);
            var invalid = new Report
                          {
                              Command = args.Count > 0 ? args[0] : "", Status = "invalid-arguments",
                              ToolVersion = ToolVersion
                          };
            invalid.Errors.Add(ex.Message);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(invalid, m_jsonOptions), cancellationToken);
            return ExitCodes.InvalidArguments;
        }

        switch (options.Kind)
        {
            case CommandKind.Help:
                await stdout.WriteLineAsync(ArgumentParser.HelpText, cancellationToken);
                return ExitCodes.Success;
            case CommandKind.ToolVersion:
                await stdout.WriteLineAsync(ToolVersion, cancellationToken);
                return ExitCodes.Success;
        }

        var report = new Report
                     {
                         Command = options.Kind == CommandKind.SignPackage ? "sign-package" : "verify-package",
                         Status = "failed",
                         ToolVersion = ToolVersion,
                     };

        int exitCode;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var commands = new PackageCommands(stderr.WriteLine);
        try
        {
            if (options.Kind == CommandKind.SignPackage)
            {
                await commands.SignAsync(options, report, timeout.Token);
            }
            else
            {
                await commands.VerifyAsync(options, report, timeout.Token);
            }

            exitCode = ExitCodes.Success;
        }
        catch (UsageException ex)
        {
            report.Status = "invalid-arguments";
            report.Errors.Add(ex.Message);
            exitCode = ExitCodes.InvalidArguments;
        }
        catch (ProcessingException ex)
        {
            report.Errors.Add(ex.Message);
            report.Errors.AddRange(ex.Details);
            exitCode = ExitCodes.Failure;
        }
        catch (OperationCanceledException) when
            (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            report.Errors.Add($"Timed out after {options.Timeout.TotalSeconds:0} seconds.");
            exitCode = ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            report.Errors.Add("Cancelled.");
            exitCode = ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            // Every other failure (bad config, malformed package, library error) is a processing failure:
            // the JSON-on-stdout and exit-code contract must hold for unexpected exceptions too.
            report.Errors.Add(ex is PackageNotFoundException or InvalidDataException or IOException
                ? ex.Message
                : $"{ex.GetType().Name}: {ex.Message}");
            exitCode = ExitCodes.Failure;
        }

        foreach (string error in report.Errors)
        {
            await stderr.WriteLineAsync($"error: {error}", cancellationToken);
        }

        await stdout.WriteLineAsync(JsonSerializer.Serialize(report, m_jsonOptions), cancellationToken);
        return exitCode;
    }
}
