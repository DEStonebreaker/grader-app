using grader_app.Isolate;
using grader_app.Tests.TestInfrastructure;

namespace grader_app.Tests;

public sealed class IsolateExecutorResultTests
{
    [Fact]
    public async Task SuccessfulExecutionReturnsAllRawFields()
    {
        using var fake = new FakeIsolate();
        var result = await ExecuteScenarioAsync(fake, 1, "success");

        Assert.Equal(TerminationReason.Exited, result.Termination);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.ExitSignal);
        Assert.Equal(TimeSpan.FromSeconds(0.125), result.CpuTime);
        Assert.Equal(TimeSpan.FromSeconds(0.25), result.WallTime);
        Assert.Equal(321, result.PeakMemoryKb);
        Assert.Equal("fake stdout", result.StandardOutput);
        Assert.Equal("fake stderr", result.StandardError);
        Assert.Null(result.SandboxMessage);
    }

    [Theory]
    [InlineData("exited-nonzero", TerminationReason.Exited, 23, null)]
    [InlineData("time-limit", TerminationReason.TimeLimitExceeded, null, null)]
    [InlineData("memory-limit", TerminationReason.MemoryLimitExceeded, null, null)]
    [InlineData("signaled", TerminationReason.Signaled, null, 11)]
    [InlineData("sandbox-error", TerminationReason.SandboxError, null, null)]
    public async Task MetadataMapsToExpectedTermination(
        string scenario,
        TerminationReason expectedTermination,
        int? expectedExitCode,
        int? expectedExitSignal)
    {
        using var fake = new FakeIsolate();
        var result = await ExecuteScenarioAsync(fake, 2, scenario);

        Assert.Equal(expectedTermination, result.Termination);
        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(expectedExitSignal, result.ExitSignal);
    }

    [Fact]
    public async Task SandboxMessagePreservesAdditionalColons()
    {
        using var fake = new FakeIsolate();
        var result = await ExecuteScenarioAsync(fake, 3, "sandbox-error");

        Assert.Equal("policy: denied: detail", result.SandboxMessage);
    }

    [Fact]
    public async Task FailedWrapperWithoutMetadataBecomesSandboxError()
    {
        using var fake = new FakeIsolate();
        var result = await ExecuteScenarioAsync(fake, 4, "wrapper-failure");

        Assert.Equal(TerminationReason.SandboxError, result.Termination);
        Assert.Contains("wrapper failed without metadata", result.SandboxMessage);
    }

    [Fact]
    public async Task MalformedMetadataUsesNullableValuesAndWallTimeFallback()
    {
        using var fake = new FakeIsolate();
        var result = await ExecuteScenarioAsync(fake, 5, "malformed-metadata");

        Assert.Equal(TerminationReason.Exited, result.Termination);
        Assert.Null(result.ExitCode);
        Assert.Equal(TimeSpan.Zero, result.CpuTime);
        Assert.True(result.WallTime > TimeSpan.Zero);
        Assert.Null(result.PeakMemoryKb);
    }

    [Theory]
    [InlineData("memory-fallback", 444)]
    [InlineData("memory-preference", 333)]
    public async Task MemoryUsesCgValueBeforeMaxRss(string scenario, long expectedMemoryKb)
    {
        using var fake = new FakeIsolate();
        var result = await ExecuteScenarioAsync(fake, 6, scenario);

        Assert.Equal(expectedMemoryKb, result.PeakMemoryKb);
    }

    [Fact]
    public async Task CapturesAndTruncatesBothNoisyStreamsWithoutDeadlock()
    {
        const int captureLimit = 1024 * 1024;
        using var fake = new FakeIsolate();
        fake.CreateLargeOutputFiles(captureLimit + 256);

        var result = await ExecuteScenarioAsync(fake, 7, "large-output");

        var marker = Environment.NewLine + "[output truncated]";
        Assert.Equal(captureLimit + marker.Length, result.StandardOutput.Length);
        Assert.Equal(captureLimit + marker.Length, result.StandardError.Length);
        Assert.EndsWith(marker, result.StandardOutput);
        Assert.EndsWith(marker, result.StandardError);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
    }

    private static async Task<ExecutionResult> ExecuteScenarioAsync(
        FakeIsolate fake,
        uint boxId,
        string scenario)
    {
        // Exercise metadata parsing through the public executor rather than testing private helpers.
        fake.SetScenario(boxId, scenario);
        var executor = new IsolateExecutor(fake.BinaryPath);
        return await executor.ExecuteAsync(
            boxId,
            CreateOptions(),
            "./main",
            [],
            CancellationToken.None);
    }

    private static IsolateRunOptions CreateOptions()
    {
        return new IsolateRunOptions(
            CpuSeconds: 1,
            WallSeconds: 2,
            MemoryKb: 1024,
            MaxProcesses: 1,
            EnvironmentVariables: new Dictionary<string, string>());
    }
}
