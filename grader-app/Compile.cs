using grader_app.Isolate;

namespace grader_app;

public enum SourceLanguage
{
    C,
    Cpp
}

public enum OptimizationLevel
{
    None,
    O1,
    O2
}

public enum StackProtection
{
    None,
    Strong,
    All
}

public sealed record CompilationOptions(
    SourceLanguage Language,
    OptimizationLevel Optimization = OptimizationLevel.O2,
    StackProtection StackProtection = StackProtection.Strong);

public enum CompilationOutcome
{
    Succeeded,
    CompilerError,
    TimeLimitExceeded,
    MemoryLimitExceeded,
    SandboxError,
    OutputMissing
}

public sealed record CompilationResult(
    CompilationOutcome Outcome,
    ExecutionResult Execution,
    string? ExecutablePath)
{
    public bool Succeeded => Outcome == CompilationOutcome.Succeeded;
}

public sealed class Compiler
{
    private const string SandboxExecutableName = "main";
    private readonly IsolateExecutor _executor;

    public Compiler(IsolateExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<CompilationResult> CompileAsync(
        uint boxId,
        string sourcePath,
        string executableOutputPath,
        CompilationOptions compilationOptions,
        IsolateRunOptions isolateOptions,
        CancellationToken cancellationToken)
    {
        // Compiler policy is converted to a closed set of arguments before reaching CliWrap.
        ArgumentNullException.ThrowIfNull(compilationOptions);
        ArgumentNullException.ThrowIfNull(isolateOptions);

        var sourceFullPath = Path.GetFullPath(sourcePath);
        var outputFullPath = Path.GetFullPath(executableOutputPath);
        var compiler = GetCompiler(compilationOptions.Language);
        var sandboxSourceName = GetSandboxSourceName(compilationOptions.Language);
        var compilerArguments = BuildCompilerArguments(
            sandboxSourceName,
            compilationOptions);

        var request = new IsolateExecutionRequest(
            boxId,
            isolateOptions,
            compiler,
            compilerArguments,
            [new SandboxInputFile(sourceFullPath, sandboxSourceName)],
            [new SandboxOutputFile(SandboxExecutableName, outputFullPath)]);

        var execution = await _executor.ExecuteAsync(request, cancellationToken);
        var outcome = DetermineOutcome(execution, outputFullPath);

        if (outcome != CompilationOutcome.Succeeded && File.Exists(outputFullPath))
        {
            File.Delete(outputFullPath);
        }

        return new CompilationResult(
            outcome,
            execution,
            outcome == CompilationOutcome.Succeeded ? outputFullPath : null);
    }

    private static IReadOnlyList<string> BuildCompilerArguments(
        string sandboxSourceName,
        CompilationOptions options)
    {
        // Do not accept arbitrary flags: every compiler argument comes from a grader-owned enum.
        return
        [
            GetLanguageStandard(options.Language),
            GetOptimizationFlag(options.Optimization),
            GetStackProtectionFlag(options.StackProtection),
            sandboxSourceName,
            "-o",
            SandboxExecutableName
        ];
    }

    private static CompilationOutcome DetermineOutcome(
        ExecutionResult execution,
        string outputPath)
    {
        // Keep compilation classification separate from the raw sandbox execution result.
        return execution.Termination switch
        {
            TerminationReason.TimeLimitExceeded => CompilationOutcome.TimeLimitExceeded,
            TerminationReason.MemoryLimitExceeded => CompilationOutcome.MemoryLimitExceeded,
            TerminationReason.SandboxError => CompilationOutcome.SandboxError,
            TerminationReason.Exited when execution.ExitCode != 0 => CompilationOutcome.CompilerError,
            TerminationReason.Signaled => CompilationOutcome.CompilerError,
            TerminationReason.Exited when IsUsableExecutable(outputPath) => CompilationOutcome.Succeeded,
            _ => CompilationOutcome.OutputMissing
        };
    }

    private static bool IsUsableExecutable(string outputPath)
    {
        // A zero-byte or linked artifact is not safe to hand to a later execution box.
        var output = new FileInfo(outputPath);
        output.Refresh();
        return output.Exists && output.Length > 0 && output.LinkTarget is null;
    }

    private static string GetCompiler(SourceLanguage language)
    {
        return language switch
        {
            SourceLanguage.C => "/usr/bin/gcc",
            SourceLanguage.Cpp => "/usr/bin/g++",
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        };
    }

    private static string GetSandboxSourceName(SourceLanguage language)
    {
        return language switch
        {
            SourceLanguage.C => "main.c",
            SourceLanguage.Cpp => "main.cpp",
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        };
    }

    private static string GetLanguageStandard(SourceLanguage language)
    {
        return language switch
        {
            SourceLanguage.C => "-std=c17",
            SourceLanguage.Cpp => "-std=c++23",
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        };
    }

    private static string GetOptimizationFlag(OptimizationLevel optimization)
    {
        return optimization switch
        {
            OptimizationLevel.None => "-O0",
            OptimizationLevel.O1 => "-O1",
            OptimizationLevel.O2 => "-O2",
            _ => throw new ArgumentOutOfRangeException(nameof(optimization))
        };
    }

    private static string GetStackProtectionFlag(StackProtection stackProtection)
    {
        return stackProtection switch
        {
            StackProtection.None => "-fno-stack-protector",
            StackProtection.Strong => "-fstack-protector-strong",
            StackProtection.All => "-fstack-protector-all",
            _ => throw new ArgumentOutOfRangeException(nameof(stackProtection))
        };
    }
}
