using grader_app.Isolate;
using grader_app.Tests.TestInfrastructure;

namespace grader_app.Tests;

public sealed class CompilerTests
{
    [Theory]
    [InlineData("compiler-success", CompilationOutcome.Succeeded, true)]
    [InlineData("compiler-error", CompilationOutcome.CompilerError, false)]
    [InlineData("compiler-time-limit", CompilationOutcome.TimeLimitExceeded, false)]
    [InlineData("compiler-memory-limit", CompilationOutcome.MemoryLimitExceeded, false)]
    [InlineData("compiler-sandbox-error", CompilationOutcome.SandboxError, false)]
    [InlineData("compiler-output-missing", CompilationOutcome.OutputMissing, false)]
    [InlineData("compiler-signaled", CompilationOutcome.CompilerError, false)]
    public async Task CompileClassifiesEveryOutcome(
        string scenario,
        CompilationOutcome expectedOutcome,
        bool expectsExecutable)
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(40, scenario);
        var sourcePath = fake.CreateHostFile("submission.cpp", "int main() { return 0; }");
        var outputPath = Path.Combine(fake.RootPath, "artifact", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var compiler = new Compiler(new IsolateExecutor(fake.BinaryPath));

        var result = await compiler.CompileAsync(
            40,
            sourcePath,
            outputPath,
            new CompilationOptions(SourceLanguage.Cpp),
            CreateCompilationOptions(),
            CancellationToken.None);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectsExecutable, result.Succeeded);
        Assert.Equal(expectsExecutable, File.Exists(outputPath));
        Assert.Equal(expectsExecutable ? outputPath : null, result.ExecutablePath);
    }

    [Theory]
    [InlineData(SourceLanguage.C, "/usr/bin/gcc", "-std=c17", "main.c")]
    [InlineData(SourceLanguage.Cpp, "/usr/bin/g++", "-std=c++23", "main.cpp")]
    public async Task DefaultPolicyBuildsExpectedCompilerCommand(
        SourceLanguage language,
        string expectedCompiler,
        string expectedStandard,
        string expectedSourceName)
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(41, "compiler-success");
        var sourcePath = fake.CreateHostFile("submission", "source");
        var outputPath = Path.Combine(fake.RootPath, "output", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var compiler = new Compiler(new IsolateExecutor(fake.BinaryPath));

        await compiler.CompileAsync(
            41,
            sourcePath,
            outputPath,
            new CompilationOptions(language),
            CreateCompilationOptions(),
            CancellationToken.None);

        var runArguments = GetRunArguments(fake, 41);
        var executableSeparator = Array.LastIndexOf(runArguments, "--");
        Assert.True(executableSeparator >= 0);
        Assert.Equal(
            [
                expectedCompiler,
                expectedStandard,
                "-O2",
                "-fstack-protector-strong",
                expectedSourceName,
                "-o",
                "main"
            ],
            runArguments[(executableSeparator + 1)..]);
    }

    [Fact]
    public async Task ExplicitPolicyMapsOnlyToGraderOwnedFlags()
    {
        using var fake = new FakeIsolate();
        fake.SetScenario(42, "compiler-success");
        var sourcePath = fake.CreateHostFile("submission.c", "source");
        var outputPath = Path.Combine(fake.RootPath, "output", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var compiler = new Compiler(new IsolateExecutor(fake.BinaryPath));

        await compiler.CompileAsync(
            42,
            sourcePath,
            outputPath,
            new CompilationOptions(
                SourceLanguage.C,
                OptimizationLevel.None,
                StackProtection.All),
            CreateCompilationOptions(),
            CancellationToken.None);

        var runArguments = GetRunArguments(fake, 42);
        Assert.Contains("-O0", runArguments);
        Assert.Contains("-fstack-protector-all", runArguments);
        Assert.DoesNotContain("-O2", runArguments);
        Assert.DoesNotContain("-fstack-protector-strong", runArguments);
    }

    [Theory]
    [InlineData("language")]
    [InlineData("optimization")]
    [InlineData("stack-protection")]
    public async Task UnknownCompilerPolicyValueIsRejected(string invalidField)
    {
        using var fake = new FakeIsolate();
        var sourcePath = fake.CreateHostFile("submission.c", "source");
        var outputPath = Path.Combine(fake.RootPath, "output", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var compiler = new Compiler(new IsolateExecutor(fake.BinaryPath));
        var options = invalidField switch
        {
            "language" => new CompilationOptions((SourceLanguage)999),
            "optimization" => new CompilationOptions(
                SourceLanguage.C,
                (OptimizationLevel)999,
                StackProtection.Strong),
            "stack-protection" => new CompilationOptions(
                SourceLanguage.C,
                OptimizationLevel.O2,
                (StackProtection)999),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField))
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            compiler.CompileAsync(
                43,
                sourcePath,
                outputPath,
                options,
                CreateCompilationOptions(),
                CancellationToken.None));
        Assert.Empty(fake.ReadInvocations());
    }

    private static string[] GetRunArguments(FakeIsolate fake, uint boxId)
    {
        // The fake logs one structured line per argument, preserving the order sent through CliWrap.
        var prefix = $"arg:{boxId}:";
        return fake.ReadInvocations()
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..])
            .ToArray();
    }

    private static IsolateRunOptions CreateCompilationOptions()
    {
        return new IsolateRunOptions(
            CpuSeconds: 5,
            WallSeconds: 10,
            MemoryKb: 262_144,
            MaxProcesses: 16,
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["PATH"] = "/usr/bin:/bin"
            });
    }
}
