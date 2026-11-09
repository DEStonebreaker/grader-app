using System.Reflection;
using System.Runtime.Loader;
using grader_app.Isolate;

namespace grader_app.Grading;

// Version 1: trusted, in-process drivers. Compile against the same grader release.
public interface IGradingDriver
{
    int ApiVersion => 1;
    Task<CheckResult> GradeAsync(GradingContext context, CancellationToken cancellationToken);
}

public sealed record CheckResult(bool Passed, string? Message = null);

public sealed class GradingContext
{
    private readonly Func<CancellationToken, Task<ExecutionResult>> _run;
    private readonly CancellationToken _jobCancellation;
    private int _started;
    private Task<ExecutionResult>? _executionTask;

    internal GradingContext(string name, string expectedOutput,
        Func<CancellationToken, Task<ExecutionResult>> run, CancellationToken jobCancellation)
    {
        CaseName = name;
        ExpectedOutput = expectedOutput;
        _run = run;
        _jobCancellation = jobCancellation;
    }

    public string CaseName { get; }
    public string ExpectedOutput { get; }
    public ExecutionResult? Execution { get; private set; }

    // Exactly one bounded, fresh sandbox run per case in API v1.
    public Task<ExecutionResult> RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A driver must run each case exactly once.");
        return _executionTask = ExecuteAsync(cancellationToken);
    }

    private async Task<ExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _jobCancellation);
        try { Execution = await _run(linked.Token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { throw new SandboxExecutionException(exception); }
        if (Grader.Classify(Execution) is not null)
            throw new SubmissionExecutionException();
        return Execution;
    }

    internal async Task FinishAsync()
    {
        if (_executionTask is not null)
            await _executionTask;
    }
}

internal sealed class SubmissionExecutionException : Exception;
internal sealed class SandboxExecutionException(Exception inner) : Exception(inner.Message, inner);

public sealed class OutputDriver(string checker = "tokens") : IGradingDriver
{
    public async Task<CheckResult> GradeAsync(GradingContext context, CancellationToken cancellationToken)
    {
        var run = await context.RunAsync(cancellationToken);
        var matches = checker switch
        {
            "exact" => run.StandardOutput == context.ExpectedOutput,
            "tokens" => Tokens(run.StandardOutput).SequenceEqual(Tokens(context.ExpectedOutput), StringComparer.Ordinal),
            _ => throw new InvalidOperationException("Unknown output checker.")
        };
        return new CheckResult(matches, matches ? null : "Output differs from the expected answer.");
    }

    private static string[] Tokens(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

internal sealed class DriverHandle : IDisposable
{
    private readonly DriverLoadContext? _loadContext;
    public IGradingDriver Driver { get; }

    public DriverHandle(LoadedProblem problem)
    {
        if (problem.DriverAssembly is null)
        {
            Driver = new OutputDriver(problem.Checker);
            return;
        }
        _loadContext = new DriverLoadContext(problem.DriverAssembly);
        try
        {
            var assembly = _loadContext.LoadFromAssemblyPath(problem.DriverAssembly);
            var type = assembly.GetType(problem.DriverType!, throwOnError: true)!;
            if (!typeof(IGradingDriver).IsAssignableFrom(type) || type.IsAbstract)
                throw new InvalidDataException("Driver type must implement IGradingDriver.");
            Driver = (IGradingDriver)(Activator.CreateInstance(type)
                     ?? throw new InvalidDataException("Driver requires a public parameterless constructor."));
            if (Driver.ApiVersion != 1)
                throw new InvalidDataException("Unsupported driver API version; expected 1.");
        }
        catch
        {
            _loadContext.Unload();
            throw;
        }
    }

    public void Dispose() => _loadContext?.Unload();

    private sealed class DriverLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Share the host's contract types rather than loading a second copy from the driver folder.
            var contract = typeof(IGradingDriver).Assembly;
            if (name.Name == contract.GetName().Name)
                return contract;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
