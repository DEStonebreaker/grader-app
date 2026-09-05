using grader_app.Config;
using grader_app.Isolate;

namespace grader_app.Tests;

public sealed class RunOptionsFactoryTests
{
    [Fact]
    public void CompilationUsesCompilationLimitsAndMinimalToolPath()
    {
        var options = RunOptionsFactory.Create(CreateLimits(), ExecutionPhase.Compilation);

        Assert.Equal(7, options.CpuSeconds);
        Assert.Equal(14, options.WallSeconds);
        Assert.Equal((uint)262_144, options.MemoryKb);
        Assert.Equal((uint)16, options.MaxProcesses);
        Assert.Equal(
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" },
            options.EnvironmentVariables);
    }

    [Fact]
    public void RuntimeUsesRuntimeLimitsAndNoHostEnvironment()
    {
        var options = RunOptionsFactory.Create(CreateLimits(), ExecutionPhase.Runtime);

        Assert.Equal(2.5, options.CpuSeconds);
        Assert.Equal(5, options.WallSeconds);
        Assert.Equal((uint)65_536, options.MemoryKb);
        Assert.Equal((uint)1, options.MaxProcesses);
        Assert.Empty(options.EnvironmentVariables);
    }

    [Fact]
    public void UnknownPhaseIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RunOptionsFactory.Create(CreateLimits(), (ExecutionPhase)999));
    }

    private static LimitSection CreateLimits()
    {
        return new LimitSection
        {
            CtSeconds = 7,
            CtMemoryKb = 262_144,
            RtSeconds = 2.5,
            RtMemoryKb = 65_536
        };
    }
}
