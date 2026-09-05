using grader_app.Isolate;
using grader_app.Tests.TestInfrastructure;

namespace grader_app.Tests;

public sealed class IsolateExecutorLifecycleTests
{
    [Fact]
    public async Task SuccessfulExecutionInitializesRunsAndCleansUp()
    {
        using var fake = new FakeIsolate();
        var executor = new IsolateExecutor(fake.BinaryPath);

        await executor.ExecuteAsync(20, CreateOptions(), "./main", [], CancellationToken.None);

        AssertLifecycle(fake, 20);
    }

    [Fact]
    public async Task InitFailureIsReturnedAndCleanupIsStillAttempted()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(21, "init-failure");
        var executor = new IsolateExecutor(fake.BinaryPath);

        var result = await executor.ExecuteAsync(
            21,
            CreateOptions(),
            "./main",
            [],
            CancellationToken.None);

        Assert.Equal(TerminationReason.SandboxError, result.Termination);
        Assert.Contains("failed to initialize", result.SandboxMessage);
        Assert.Equal(["init:21", "cleanup:21"], fake.ReadInvocations());
    }

    [Fact]
    public async Task CleanupFailureInvalidatesSuccessfulExecution()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(22, "cleanup-failure");
        var executor = new IsolateExecutor(fake.BinaryPath);

        var result = await executor.ExecuteAsync(
            22,
            CreateOptions(),
            "./main",
            [],
            CancellationToken.None);

        Assert.Equal(TerminationReason.SandboxError, result.Termination);
        Assert.Contains("failed to clean up", result.SandboxMessage);
        Assert.Contains("fake cleanup failure", result.StandardError);
    }

    [Fact]
    public async Task CancellationDuringRunStillCleansUp()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(23, "block");
        var executor = new IsolateExecutor(fake.BinaryPath);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var executionTask = executor.ExecuteAsync(
            23,
            CreateOptions(),
            "./main",
            [],
            cancellationSource.Token);
        await fake.WaitForRunStartAsync(23, cancellationSource.Token);
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executionTask);
        Assert.Contains("cleanup:23", fake.ReadInvocations());
    }

    [Fact]
    public async Task CallsOnOneExecutorAreSerialized()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(24, "slow-success");
        fake.SetScenario(25, "slow-success");
        var executor = new IsolateExecutor(fake.BinaryPath);

        await Task.WhenAll(
            executor.ExecuteAsync(24, CreateOptions(), "./main", [], CancellationToken.None),
            executor.ExecuteAsync(25, CreateOptions(), "./main", [], CancellationToken.None));

        var lifecycleEvents = fake.ReadInvocations()
            .Where(line => !line.StartsWith("arg:", StringComparison.Ordinal))
            .ToArray();
        var firstThenSecond = new[]
        {
            "init:24", "run-start:24", "run-end:24", "cleanup:24",
            "init:25", "run-start:25", "run-end:25", "cleanup:25"
        };
        var secondThenFirst = new[]
        {
            "init:25", "run-start:25", "run-end:25", "cleanup:25",
            "init:24", "run-start:24", "run-end:24", "cleanup:24"
        };
        Assert.True(
            lifecycleEvents.SequenceEqual(firstThenSecond)
            || lifecycleEvents.SequenceEqual(secondThenFirst),
            $"Lifecycles overlapped: {string.Join(", ", lifecycleEvents)}");
    }

    [Fact]
    public async Task StandardInputMustBeStagedAndIsPassedFromTheBox()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(26, "echo-stdin");
        var inputPath = fake.CreateHostFile("host input.txt", "hello from input");
        var executor = new IsolateExecutor(fake.BinaryPath);
        var request = new IsolateExecutionRequest(
            26,
            CreateOptions(),
            "./main",
            [],
            [new SandboxInputFile(inputPath, "nested/input.txt")],
            [],
            StandardInputPath: "nested/input.txt");

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal("hello from input", result.StandardOutput);
        AssertLifecycle(fake, 26);
    }

    private static void AssertLifecycle(FakeIsolate fake, uint boxId)
    {
        var lifecycleEvents = fake.ReadInvocations()
            .Where(line => !line.StartsWith("arg:", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            [$"init:{boxId}", $"run-start:{boxId}", $"run-end:{boxId}", $"cleanup:{boxId}"],
            lifecycleEvents);
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
