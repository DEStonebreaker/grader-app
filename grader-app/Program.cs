using grader_app;
using grader_app.Config;
using grader_app.Isolate;
using Tomlyn;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    // Keep this entry point as a one-shot smoke runner until a durable job source is designed.
    if (arguments.Length != 4)
    {
        PrintUsage();
        return 2;
    }

    if (!TryParseLanguage(arguments[2], out var language))
    {
        Console.Error.WriteLine("Language must be 'c' or 'cpp'.");
        PrintUsage();
        return 2;
    }

    using var cancellationSource = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellationSource.Cancel();
    };

    Console.CancelKeyPress += cancelHandler;

    try
    {
        return await CompileAndRunAsync(
            arguments[0],
            arguments[1],
            language,
            arguments[3],
            cancellationSource.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Grader execution was cancelled.");
        return 130;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("Grader failed:");
        Console.Error.WriteLine(exception);
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> CompileAndRunAsync(
    string problemPath,
    string sourcePath,
    SourceLanguage language,
    string inputPath,
    CancellationToken cancellationToken)
{
    // Compile and execute sequentially so the runtime always receives a fresh sandbox and artifact.
    var problemFullPath = RequireExistingFile(problemPath, "Problem TOML");
    var sourceFullPath = RequireExistingFile(sourcePath, "Source file");
    var inputFullPath = RequireExistingFile(inputPath, "Test input");

    var problemText = await File.ReadAllTextAsync(problemFullPath, cancellationToken);
    var problem = TomlSerializer.Deserialize<ProblemFile>(problemText)
                  ?? throw new InvalidDataException("Problem TOML did not produce a configuration.");

    ValidateLimits(problem.Limits);

    var artifactDirectory = Path.Combine(
        Path.GetTempPath(),
        $"valimark-artifact-{Guid.NewGuid():N}");
    Directory.CreateDirectory(artifactDirectory);

    var executablePath = Path.Combine(artifactDirectory, "main");

    try
    {
        var isolateBinary = Environment.GetEnvironmentVariable("VALIMARK_ISOLATE_PATH")
                            ?? "/usr/local/bin/isolate";
        var executor = new IsolateExecutor(isolateBinary);
        var compiler = new Compiler(executor);
        var compilation = await compiler.CompileAsync(
            boxId: 0,
            sourcePath: sourceFullPath,
            executableOutputPath: executablePath,
            compilationOptions: new CompilationOptions(language),
            isolateOptions: RunOptionsFactory.Create(
                problem.Limits,
                ExecutionPhase.Compilation),
            cancellationToken);

        PrintCompilationResult(compilation);

        if (!compilation.Succeeded)
        {
            return 3;
        }

        var executionRequest = new IsolateExecutionRequest(
            BoxId: 1,
            Options: RunOptionsFactory.Create(
                problem.Limits,
                ExecutionPhase.Runtime),
            Executable: "./main",
            ExecutableArguments: [],
            InputFiles:
            [
                new SandboxInputFile(executablePath, "main"),
                new SandboxInputFile(inputFullPath, "input.txt")
            ],
            OutputFiles: [],
            StandardInputPath: "input.txt");

        var execution = await executor.ExecuteAsync(executionRequest, cancellationToken);
        PrintExecutionResult("Execution", execution);

        return execution.Termination == TerminationReason.Exited && execution.ExitCode == 0
            ? 0
            : 4;
    }
    finally
    {
        CleanupArtifact(executablePath, artifactDirectory);
    }
}

static string RequireExistingFile(string path, string description)
{
    var fullPath = Path.GetFullPath(path);
    var file = new FileInfo(fullPath);
    file.Refresh();

    if (!file.Exists || file.LinkTarget is not null)
    {
        throw new FileNotFoundException(
            $"{description} must be an existing regular file, not a symbolic link.",
            fullPath);
    }

    return fullPath;
}

static void ValidateLimits(LimitSection limits)
{
    // Reject unusable limits before a zero-value configuration reaches Isolate.
    if (!double.IsFinite(limits.CtSeconds) || limits.CtSeconds <= 0)
    {
        throw new InvalidDataException("ct_seconds must be a positive finite number.");
    }

    if (!double.IsFinite(limits.RtSeconds) || limits.RtSeconds <= 0)
    {
        throw new InvalidDataException("rt_seconds must be a positive finite number.");
    }

    if (limits.CtMemoryKb == 0)
    {
        throw new InvalidDataException("ct_memory_kb must be greater than zero.");
    }

    if (limits.RtMemoryKb == 0)
    {
        throw new InvalidDataException("rt_memory_kb must be greater than zero.");
    }
}

static bool TryParseLanguage(string value, out SourceLanguage language)
{
    switch (value.ToLowerInvariant())
    {
        case "c":
            language = SourceLanguage.C;
            return true;
        case "cpp":
        case "c++":
            language = SourceLanguage.Cpp;
            return true;
        default:
            language = default;
            return false;
    }
}

static void PrintCompilationResult(CompilationResult result)
{
    Console.WriteLine("CompilationResult");
    Console.WriteLine($"  Outcome: {result.Outcome}");
    Console.WriteLine($"  ExecutablePath: {result.ExecutablePath ?? "<none>"}");
    PrintExecutionResult("  Execution", result.Execution);
}

static void PrintExecutionResult(string heading, ExecutionResult result)
{
    // Print every raw execution field without assigning a judge verdict.
    Console.WriteLine(heading);
    Console.WriteLine($"  Termination: {result.Termination}");
    Console.WriteLine($"  ExitCode: {result.ExitCode?.ToString() ?? "<none>"}");
    Console.WriteLine($"  ExitSignal: {result.ExitSignal?.ToString() ?? "<none>"}");
    Console.WriteLine($"  CpuTime: {result.CpuTime}");
    Console.WriteLine($"  WallTime: {result.WallTime}");
    Console.WriteLine($"  PeakMemoryKb: {result.PeakMemoryKb?.ToString() ?? "<none>"}");
    Console.WriteLine($"  SandboxMessage: {result.SandboxMessage ?? "<none>"}");
    Console.WriteLine("  StandardOutput:");
    Console.WriteLine(result.StandardOutput);
    Console.WriteLine("  StandardError:");
    Console.WriteLine(result.StandardError);
}

static void CleanupArtifact(string executablePath, string artifactDirectory)
{
    // Remove only the exact temporary paths created by this invocation.
    try
    {
        File.Delete(executablePath);
        Directory.Delete(artifactDirectory, recursive: false);
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine($"Warning: temporary artifact cleanup failed: {exception.Message}");
    }
    catch (UnauthorizedAccessException exception)
    {
        Console.Error.WriteLine($"Warning: temporary artifact cleanup failed: {exception.Message}");
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine(
        "Usage: grader-app <problem.toml> <source-file> <c|cpp> <test-input>");
}
