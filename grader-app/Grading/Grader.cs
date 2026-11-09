using grader_app.Isolate;

namespace grader_app.Grading;

public enum Verdict
{
    Passed, WrongAnswer, CompilationError, TimeLimitExceeded, MemoryLimitExceeded,
    RuntimeError, OutputLimitExceeded, DriverError, InfrastructureError
}

public sealed record CaseResult(string Name, Verdict Verdict, string? Message, ExecutionResult? Execution);

public sealed record GradeReport(
    string Problem, Verdict Verdict, CompilationResult? Compilation, IReadOnlyList<CaseResult> Cases,
    string? Message = null, int SchemaVersion = 1)
{
    public int ExitCode => Verdict switch
    {
        Verdict.Passed => 0,
        Verdict.CompilationError => 3,
        Verdict.InfrastructureError or Verdict.DriverError => 1,
        _ => 4
    };
}

public sealed class Grader(IsolateExecutor executor)
{
    public async Task<GradeReport> GradeAsync(LoadedProblem problem, string sourcePath, SourceLanguage language,
        uint boxId, CancellationToken cancellationToken)
    {
        DriverHandle handle;
        try { handle = new DriverHandle(problem); }
        catch (Exception exception)
        {
            return new GradeReport(problem.Title, Verdict.DriverError, null, [], exception.Message);
        }
        using (handle)
        {
            var workspace = Directory.CreateTempSubdirectory("valimark-grade-").FullName;
            try
            {
                var executable = Path.Combine(workspace, "main");
                var compilation = await new Compiler(executor).CompileAsync(boxId, Path.GetFullPath(sourcePath),
                    executable, new CompilationOptions(language),
                    RunOptionsFactory.Create(problem.Limits, ExecutionPhase.Compilation), cancellationToken);
                if (!compilation.Succeeded)
                {
                    var verdict = compilation.Outcome is CompilationOutcome.SandboxError or CompilationOutcome.OutputMissing
                        ? Verdict.InfrastructureError : Verdict.CompilationError;
                    return new GradeReport(problem.Title, verdict, compilation, [], compilation.Execution.SandboxMessage);
                }

                var results = new List<CaseResult>();
                foreach (var test in problem.Cases)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var expected = await File.ReadAllTextAsync(test.ExpectedPath, cancellationToken);
                    var context = new GradingContext(test.Name, expected, token => executor.ExecuteAsync(
                        new IsolateExecutionRequest(boxId,
                            RunOptionsFactory.Create(problem.Limits, ExecutionPhase.Runtime), "./main", [],
                            [new SandboxInputFile(executable, "main"), new SandboxInputFile(test.InputPath, "input.txt")],
                            [], "input.txt"), token), cancellationToken);
                    try
                    {
                        var check = await handle.Driver.GradeAsync(context, cancellationToken);
                        await context.FinishAsync();
                        cancellationToken.ThrowIfCancellationRequested();
                        var verdict = context.Execution is null
                            ? Verdict.DriverError : Classify(context.Execution) ??
                                (check.Passed ? Verdict.Passed : Verdict.WrongAnswer);
                        results.Add(new CaseResult(test.Name, verdict,
                            context.Execution is null ? "Driver did not run the submission." : check.Message, context.Execution));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (SubmissionExecutionException)
                    {
                        results.Add(new CaseResult(test.Name, Classify(context.Execution!)!.Value,
                            context.Execution!.SandboxMessage, context.Execution));
                    }
                    catch (SandboxExecutionException exception)
                    {
                        results.Add(new CaseResult(test.Name, Verdict.InfrastructureError, exception.Message, context.Execution));
                    }
                    catch (Exception exception)
                    {
                        results.Add(new CaseResult(test.Name, Verdict.DriverError, exception.Message, context.Execution));
                    }
                    finally
                    {
                        // A buggy trusted driver may start a run and throw without awaiting it.
                        // Drain that run before deleting artifacts or releasing the box lease.
                        try { await context.FinishAsync(); }
                        catch (Exception) { /* The role-specific failure is recorded above. */ }
                    }
                    if (results[^1].Verdict is Verdict.DriverError or Verdict.InfrastructureError)
                        break;
                }
                // Infrastructure/driver faults take precedence over ordinary submission failures.
                var failure = results.FirstOrDefault(result => result.Verdict is Verdict.InfrastructureError or Verdict.DriverError)
                              ?? results.FirstOrDefault(result => result.Verdict != Verdict.Passed);
                return new GradeReport(problem.Title, failure?.Verdict ?? Verdict.Passed, compilation, results);
            }
            finally { Directory.Delete(workspace, recursive: true); }
        }
    }

    internal static Verdict? Classify(ExecutionResult result) => result.Termination switch
    {
        TerminationReason.SandboxError => Verdict.InfrastructureError,
        TerminationReason.TimeLimitExceeded => Verdict.TimeLimitExceeded,
        TerminationReason.MemoryLimitExceeded => Verdict.MemoryLimitExceeded,
        TerminationReason.Signaled => Verdict.RuntimeError,
        TerminationReason.Exited when result.ExitCode is null => Verdict.InfrastructureError,
        TerminationReason.Exited when result.ExitCode != 0 => Verdict.RuntimeError,
        _ when result.StandardOutputTruncated || result.StandardErrorTruncated => Verdict.OutputLimitExceeded,
        _ => null
    };
}
