using grader_app.Config;

namespace grader_app.Isolate;

public enum ExecutionPhase
{
    Compilation,
    Runtime
}

public sealed record IsolateRunOptions(
    double CpuSeconds,
    double WallSeconds,
    uint MemoryKb,
    uint MaxProcesses,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

public static class RunOptionsFactory
{
    public static IsolateRunOptions Create(
        LimitSection limits,
        ExecutionPhase phase)
    {
        // Give compiler subprocesses a minimal tool path without exposing host variables at runtime.
        return phase switch
        {
            ExecutionPhase.Compilation => new(
                CpuSeconds: limits.CtSeconds,
                WallSeconds: limits.CtSeconds * 2,
                MemoryKb: limits.CtMemoryKb,
                MaxProcesses: 16,
                EnvironmentVariables: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PATH"] = "/usr/bin:/bin"
                }),

            ExecutionPhase.Runtime => new(
                CpuSeconds: limits.RtSeconds,
                WallSeconds: limits.RtSeconds * 2,
                MemoryKb: limits.RtMemoryKb,
                MaxProcesses: 1,
                EnvironmentVariables: new Dictionary<string, string>(StringComparer.Ordinal)),

            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
    }
}
