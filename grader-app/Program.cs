using grader_app;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await GraderCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
