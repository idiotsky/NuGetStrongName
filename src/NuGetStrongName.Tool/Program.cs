using JIT.NuGetStrongName.Cli;

// Not disposed: the handler can still fire after RunAsync returns, and a CancellationTokenSource without a timer
// holds nothing that the process exit does not release anyway.
var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
                          {
                              e.Cancel = true;
                              cancellation.Cancel();
                          };

return await CliApp.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
