using grader_app.Isolate;
using grader_app.Tests.TestInfrastructure;

namespace grader_app.Tests;

public sealed class IsolateExecutorValidationTests
{
    [Fact]
    public void ExecutorRequiresAnAbsoluteIsolatePath()
    {
        Assert.Throws<ArgumentException>(() => new IsolateExecutor("isolate"));
    }

    [Fact]
    public async Task NullRequestIsRejectedBeforeInitialization()
    {
        using var fake = new FakeIsolate();
        var executor = new IsolateExecutor(fake.BinaryPath);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            executor.ExecuteAsync(null!, CancellationToken.None));
        Assert.Empty(fake.ReadInvocations());
    }

    [Fact]
    public async Task MissingOptionsAreRejectedBeforeInitialization()
    {
        using var fake = new FakeIsolate();
        var request = CreateRequest() with { Options = null! };

        await AssertValidationFailureAsync<ArgumentNullException>(fake, request);
    }

    [Fact]
    public async Task BlankExecutableIsRejectedBeforeInitialization()
    {
        using var fake = new FakeIsolate();
        var request = CreateRequest() with { Executable = " " };

        await AssertValidationFailureAsync<ArgumentException>(fake, request);
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("inputs")]
    [InlineData("outputs")]
    public async Task NullRequestCollectionsAreRejectedBeforeInitialization(string collection)
    {
        using var fake = new FakeIsolate();
        var request = collection switch
        {
            "arguments" => CreateRequest() with { ExecutableArguments = null! },
            "inputs" => CreateRequest() with { InputFiles = null! },
            "outputs" => CreateRequest() with { OutputFiles = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(collection))
        };

        await AssertValidationFailureAsync<ArgumentNullException>(fake, request);
    }

    [Fact]
    public async Task RelativeHostInputPathIsRejectedBeforeInitialization()
    {
        using var fake = new FakeIsolate();
        var request = CreateRequest(
            inputFiles: [new SandboxInputFile("relative.c", "main.c")]);

        await AssertValidationFailureAsync<ArgumentException>(fake, request);
    }

    [Fact]
    public async Task MissingHostInputIsRejectedBeforeInitialization()
    {
        using var fake = new FakeIsolate();
        var missingPath = Path.Combine(fake.RootPath, "missing.c");
        var request = CreateRequest(
            inputFiles: [new SandboxInputFile(missingPath, "main.c")]);

        await AssertValidationFailureAsync<FileNotFoundException>(fake, request);
    }

    [Fact]
    public async Task SymbolicLinkHostInputIsRejectedBeforeInitialization()
    {
        using var fake = new FakeIsolate();
        var target = fake.CreateHostFile("target.c", "int main(void) { return 0; }");
        var link = Path.Combine(fake.RootPath, "link.c");
        File.CreateSymbolicLink(link, target);
        var request = CreateRequest(
            inputFiles: [new SandboxInputFile(link, "main.c")]);

        await AssertValidationFailureAsync<FileNotFoundException>(fake, request);
    }

    [Fact]
    public async Task NullInputEntryIsRejectedBeforeInitialization()
    {
        using var fake = new FakeIsolate();
        var request = CreateRequest(inputFiles: [null!]);

        await AssertValidationFailureAsync<ArgumentNullException>(fake, request);
    }

    [Theory]
    [InlineData("/absolute/path")]
    [InlineData("../escape")]
    [InlineData("nested/../escape")]
    [InlineData("./main")]
    public async Task UnsafeSandboxInputPathIsRejectedBeforeInitialization(string sandboxPath)
    {
        using var fake = new FakeIsolate();
        var input = fake.CreateHostFile("main.c", "source");
        var request = CreateRequest(
            inputFiles: [new SandboxInputFile(input, sandboxPath)]);

        await AssertValidationFailureAsync<ArgumentException>(fake, request);
    }

    [Fact]
    public async Task StandardInputMustReferToAStagedFile()
    {
        using var fake = new FakeIsolate();
        var input = fake.CreateHostFile("input.txt", "input");
        var request = CreateRequest(
            inputFiles: [new SandboxInputFile(input, "different.txt")],
            standardInputPath: "input.txt");

        await AssertValidationFailureAsync<ArgumentException>(fake, request);
    }

    [Fact]
    public async Task DuplicateSandboxInputsAreRejected()
    {
        using var fake = new FakeIsolate();
        var first = fake.CreateHostFile("one.txt", "one");
        var second = fake.CreateHostFile("two.txt", "two");
        var request = CreateRequest(
            inputFiles:
            [
                new SandboxInputFile(first, "same.txt"),
                new SandboxInputFile(second, "same.txt")
            ]);

        await AssertValidationFailureAsync<ArgumentException>(fake, request);
    }

    [Fact]
    public async Task DuplicateSandboxOutputsAreRejected()
    {
        using var fake = new FakeIsolate();
        var request = CreateRequest(
            outputFiles:
            [
                new SandboxOutputFile("same.txt", Path.Combine(fake.RootPath, "one.txt")),
                new SandboxOutputFile("same.txt", Path.Combine(fake.RootPath, "two.txt"))
            ]);

        await AssertValidationFailureAsync<ArgumentException>(fake, request);
    }

    [Fact]
    public async Task NullOutputEntryIsRejectedBeforeInitialization()
    {
        using var fake = new FakeIsolate();
        var request = CreateRequest(outputFiles: [null!]);

        await AssertValidationFailureAsync<ArgumentNullException>(fake, request);
    }

    [Fact]
    public async Task DuplicateHostOutputsAreRejected()
    {
        using var fake = new FakeIsolate();
        var hostOutput = Path.Combine(fake.RootPath, "result.txt");
        var request = CreateRequest(
            outputFiles:
            [
                new SandboxOutputFile("one.txt", hostOutput),
                new SandboxOutputFile("two.txt", hostOutput)
            ]);

        await AssertValidationFailureAsync<ArgumentException>(fake, request);
    }

    [Fact]
    public async Task ExistingHostOutputIsNeverOverwritten()
    {
        using var fake = new FakeIsolate();
        var hostOutput = fake.CreateHostFile("result.txt", "keep me");
        var request = CreateRequest(
            outputFiles: [new SandboxOutputFile("result.txt", hostOutput)]);

        await AssertValidationFailureAsync<IOException>(fake, request);
        Assert.Equal("keep me", await File.ReadAllTextAsync(hostOutput));
    }

    [Fact]
    public async Task HostOutputCannotReplaceAnExistingDirectory()
    {
        using var fake = new FakeIsolate();
        var hostOutput = Path.Combine(fake.RootPath, "existing-directory");
        Directory.CreateDirectory(hostOutput);
        var request = CreateRequest(
            outputFiles: [new SandboxOutputFile("result.txt", hostOutput)]);

        await AssertValidationFailureAsync<IOException>(fake, request);
    }

    [Fact]
    public async Task MissingHostOutputDirectoryIsRejected()
    {
        using var fake = new FakeIsolate();
        var hostOutput = Path.Combine(fake.RootPath, "missing", "result.txt");
        var request = CreateRequest(
            outputFiles: [new SandboxOutputFile("result.txt", hostOutput)]);

        await AssertValidationFailureAsync<DirectoryNotFoundException>(fake, request);
    }

    [Fact]
    public async Task RelativeHostOutputPathIsRejectedBeforeInitialization()
    {
        using var fake = new FakeIsolate();
        var request = CreateRequest(
            outputFiles: [new SandboxOutputFile("result.txt", "relative.txt")]);

        await AssertValidationFailureAsync<ArgumentException>(fake, request);
    }

    [Theory]
    [InlineData("/absolute/result")]
    [InlineData("../result")]
    [InlineData("nested/../result")]
    public async Task UnsafeSandboxOutputPathIsRejectedBeforeInitialization(string sandboxPath)
    {
        using var fake = new FakeIsolate();
        var request = CreateRequest(
            outputFiles:
            [new SandboxOutputFile(sandboxPath, Path.Combine(fake.RootPath, "result.txt"))]);

        await AssertValidationFailureAsync<ArgumentException>(fake, request);
    }

    [Fact]
    public async Task RegularOutputIsCopiedToANewHostFile()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(31, "create-output");
        var hostOutput = Path.Combine(fake.RootPath, "result.txt");
        var request = CreateRequest(
            boxId: 31,
            outputFiles: [new SandboxOutputFile("result.txt", hostOutput)]);
        var executor = new IsolateExecutor(fake.BinaryPath);

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(TerminationReason.Exited, result.Termination);
        Assert.Equal("artifact contents\n", await File.ReadAllTextAsync(hostOutput));
        Assert.Contains("cleanup:31", fake.ReadInvocations());
    }

    [Fact]
    public async Task OutputIsNotCopiedAfterANonzeroProgramExit()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(35, "create-output-nonzero");
        var hostOutput = Path.Combine(fake.RootPath, "result.txt");
        var request = CreateRequest(
            boxId: 35,
            outputFiles: [new SandboxOutputFile("result.txt", hostOutput)]);
        var executor = new IsolateExecutor(fake.BinaryPath);

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(9, result.ExitCode);
        Assert.False(File.Exists(hostOutput));
        Assert.Contains("cleanup:35", fake.ReadInvocations());
    }

    [Fact]
    public async Task SymbolicLinkOutputIsRejectedAndBoxIsCleanedUp()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(32, "symlink-output");
        var request = CreateRequest(
            boxId: 32,
            outputFiles:
            [new SandboxOutputFile("result.txt", Path.Combine(fake.RootPath, "result.txt"))]);
        var executor = new IsolateExecutor(fake.BinaryPath);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            executor.ExecuteAsync(request, CancellationToken.None));
        Assert.Contains("cleanup:32", fake.ReadInvocations());
    }

    [Fact]
    public async Task SymbolicLinkParentOfOutputIsRejectedAndBoxIsCleanedUp()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(33, "symlink-parent-output");
        var request = CreateRequest(
            boxId: 33,
            outputFiles:
            [new SandboxOutputFile("out/passwd", Path.Combine(fake.RootPath, "result.txt"))]);
        var executor = new IsolateExecutor(fake.BinaryPath);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            executor.ExecuteAsync(request, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(fake.RootPath, "result.txt")));
        Assert.Contains("cleanup:33", fake.ReadInvocations());
    }

    [Fact]
    public async Task ExecutablePermissionIsPreservedWithoutUnsafeWriteBits()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fake = new FakeIsolate();
        fake.SetScenario(34, "copy-executable");
        var hostInput = fake.CreateHostFile("program", "program");
        File.SetUnixFileMode(hostInput, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                      UnixFileMode.UserExecute | UnixFileMode.GroupWrite |
                                      UnixFileMode.OtherWrite);
        var hostOutput = Path.Combine(fake.RootPath, "result");
        var request = CreateRequest(
            boxId: 34,
            inputFiles: [new SandboxInputFile(hostInput, "program")],
            outputFiles: [new SandboxOutputFile("result", hostOutput)]);
        var executor = new IsolateExecutor(fake.BinaryPath);

        await executor.ExecuteAsync(request, CancellationToken.None);

        var mode = File.GetUnixFileMode(hostOutput);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
        Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
    }

    private static IsolateExecutionRequest CreateRequest(
        uint boxId = 30,
        IReadOnlyList<SandboxInputFile>? inputFiles = null,
        IReadOnlyList<SandboxOutputFile>? outputFiles = null,
        string? standardInputPath = null)
    {
        // Supply a valid baseline so each validation test changes only the field under test.
        return new IsolateExecutionRequest(
            boxId,
            new IsolateRunOptions(
                CpuSeconds: 1,
                WallSeconds: 2,
                MemoryKb: 1024,
                MaxProcesses: 1,
                EnvironmentVariables: new Dictionary<string, string>()),
            "./main",
            [],
            inputFiles ?? [],
            outputFiles ?? [],
            standardInputPath);
    }

    private static async Task AssertValidationFailureAsync<TException>(
        FakeIsolate fake,
        IsolateExecutionRequest request)
        where TException : Exception
    {
        // Validation must fail before a box is initialized, leaving the fake invocation log empty.
        var executor = new IsolateExecutor(fake.BinaryPath);
        await Assert.ThrowsAsync<TException>(() =>
            executor.ExecuteAsync(request, CancellationToken.None));
        Assert.Empty(fake.ReadInvocations());
    }
}
