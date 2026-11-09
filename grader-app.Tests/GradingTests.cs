using System.Text.Json;
using System.Diagnostics;
using grader_app.Grading;
using grader_app.Isolate;
using grader_app.Tests.TestInfrastructure;

namespace grader_app.Tests;

[Collection("CLI")]
public sealed class GradingTests
{
    [Fact]
    public async Task CliCompilesOnceAndRunsTwoFreshCasesInManifestOrder()
    {
        using var fixture = new ProblemFixture();
        fixture.AddCase("z-first", "3\n", "3\n");
        fixture.AddCase("a-second", "-5\n", "-5\n");
        var (code, stdout, stderr) = await fixture.RunCliAsync();
        Assert.Equal(0, code);
        Assert.Empty(stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("Passed", json.RootElement.GetProperty("verdict").GetString());
        var cases = json.RootElement.GetProperty("cases");
        Assert.Equal("z-first", cases[0].GetProperty("name").GetString());
        Assert.Equal("a-second", cases[1].GetProperty("name").GetString());
        Assert.Equal(3, fixture.Fake.ReadInvocations().Count(line => line == "init:731"));
        Assert.Equal(3, fixture.Fake.ReadInvocations().Count(line => line == "cleanup:731"));
        Assert.Single(fixture.Fake.ReadInvocations(), line => line == "arg:731:/usr/bin/g++");
    }

    [Theory]
    [InlineData("tokens", "  one\t two\r\n", "one two\n", 0)]
    [InlineData("tokens", "ONE", "one", 4)]
    [InlineData("exact", "one\n", "one\r\n", 4)]
    [InlineData("exact", "one\n", "one\n", 0)]
    public async Task BuiltInCheckersFollowTheirDocumentedComparison(string checker, string actual, string expected, int exit)
    {
        using var fixture = new ProblemFixture();
        fixture.Checker = checker;
        fixture.AddCase("case", actual, expected);
        var (code, stdout, _) = await fixture.RunCliAsync();
        Assert.Equal(exit, code);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(exit == 0 ? "Passed" : "WrongAnswer", json.RootElement.GetProperty("verdict").GetString());
    }

    [Theory]
    [InlineData("TIME", "TimeLimitExceeded", 4)]
    [InlineData("MEMORY", "MemoryLimitExceeded", 4)]
    [InlineData("CRASH", "RuntimeError", 4)]
    [InlineData("BROKEN", "InfrastructureError", 1)]
    public async Task FailedSubmissionDoesNotReachTheAnswerChecker(string input, string verdict, int expectedCode)
    {
        using var fixture = new ProblemFixture();
        fixture.AddCase("failure", input, input);
        var (code, stdout, _) = await fixture.RunCliAsync();
        Assert.Equal(expectedCode, code);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(verdict, json.RootElement.GetProperty("cases")[0].GetProperty("verdict").GetString());
    }

    [Theory]
    [InlineData("wrong", 2, "WrongAnswer")]
    [InlineData("BROKEN", 1, "InfrastructureError")]
    public async Task CaseFailuresContinueButInfrastructureFailuresStop(string input, int caseCount, string verdict)
    {
        using var fixture = new ProblemFixture();
        fixture.AddCase("first", input, "expected");
        fixture.AddCase("second", "3", "3");
        var (_, stdout, _) = await fixture.RunCliAsync();
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(caseCount, json.RootElement.GetProperty("cases").GetArrayLength());
        Assert.Equal(verdict, json.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task TruncatedOutputCannotPassEvenWhenExpectedMatchesCapturedText()
    {
        using var fixture = new ProblemFixture();
        fixture.Fake.CreateLargeOutputFiles(1024 * 1024 + 1);
        fixture.AddCase("large", "LARGE", new string('o', 1024 * 1024) + Environment.NewLine + "[output truncated]");
        var (code, stdout, _) = await fixture.RunCliAsync();
        Assert.Equal(4, code);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("OutputLimitExceeded", json.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task RealCustomAssemblyRunsThroughSharedDriverContract()
    {
        using var fixture = new ProblemFixture();
        fixture.UseDriver(typeof(SumDriver.NumericDriver));
        fixture.AddCase("number", "0003\n", "3\n");
        var (code, stdout, _) = await fixture.RunCliAsync();
        Assert.Equal(0, code); // Token comparison would fail; this exercises the custom numeric checker.
        Assert.Contains("Passed", stdout);
    }

    [Fact]
    public async Task DriverDiagnosticsDoNotCorruptJson()
    {
        using var fixture = new ProblemFixture();
        fixture.UseDriver(typeof(LoggingDriver));
        fixture.AddCase("case", "3", "3");
        var (code, stdout, stderr) = await fixture.RunCliAsync();
        Assert.Equal(0, code);
        using var json = JsonDocument.Parse(stdout);
        Assert.Contains("driver diagnostic", stderr);
    }

    [Fact]
    public async Task AnotherProcessCannotUseALeasedBoxAndCanReuseItAfterRelease()
    {
        using var fixture = new ProblemFixture();
        fixture.AddCase("case", "3", "3");
        fixture.WriteManifest();
        var source = fixture.Fake.CreateHostFile("source.cpp", "int main() {}");
        using (BoxLease.Acquire(731))
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "grader-app.Tests.runtimeconfig.json"));
            start.ArgumentList.Add(typeof(GraderCli).Assembly.Location);
            foreach (var arg in new[] { "grade", fixture.Root, source, "--box-id", "731", "--format", "json", "--isolate", fixture.Fake.BinaryPath })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, process.ExitCode);
            using var json = JsonDocument.Parse(await stdout);
            Assert.Contains("busy", json.RootElement.GetProperty("message").GetString());
            Assert.Empty(await stderr);
            Assert.Empty(fixture.Fake.ReadInvocations());
        }
        var (code, _, _) = await fixture.RunCliAsync();
        Assert.Equal(0, code);
    }

    [Theory]
    [InlineData(typeof(ThrowingDriver))]
    [InlineData(typeof(NoRunDriver))]
    [InlineData(typeof(UnsupportedDriver))]
    [InlineData(typeof(NullDriver))]
    [InlineData(typeof(DoubleRunDriver))]
    public async Task InvalidDriversAreGraderFaults(Type driver)
    {
        using var fixture = new ProblemFixture();
        fixture.UseDriver(driver);
        fixture.AddCase("case", "3", "3");
        var (code, stdout, _) = await fixture.RunCliAsync();
        Assert.Equal(1, code);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("DriverError", json.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task CompilerFailureHasDiagnosticsAndDoesNotRunCases()
    {
        using var fixture = new ProblemFixture();
        fixture.Fake.SetScenario(731, "compiler-error");
        fixture.AddCase("case", "3", "3");
        var (code, stdout, _) = await fixture.RunCliAsync(format: "text");
        Assert.Equal(3, code);
        Assert.Contains("syntax error", stdout);
        Assert.Single(fixture.Fake.ReadInvocations(), line => line == "init:731");
    }

    [Theory]
    [InlineData("../outside.in")]
    [InlineData("/etc/passwd")]
    public void EscapingPathsAreRejectedBeforeAnySandboxInitialization(string input)
    {
        using var fixture = new ProblemFixture();
        fixture.AddCase("case", "3", "3");
        var manifest = fixture.WriteManifest().Replace("case.in", input, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(fixture.Root, "problem.toml"), manifest);
        Assert.Throws<InvalidDataException>(() => ProblemLoader.Load(fixture.Root));
        Assert.Empty(fixture.Fake.ReadInvocations());
    }

    [Fact]
    public void LinkedAssetDirectoryIsRejected()
    {
        using var fixture = new ProblemFixture();
        fixture.AddCase("case", "3", "3");
        var directory = Path.Combine(fixture.Root, "assets");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "data"), "3");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "link"), directory);
        var manifest = fixture.WriteManifest().Replace("case.in", "link/data", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(fixture.Root, "problem.toml"), manifest);
        Assert.Throws<InvalidDataException>(() => ProblemLoader.Load(fixture.Root));
    }

    [Fact]
    public async Task InvalidConfigurationIsJsonAndAllocatesNoBox()
    {
        using var fixture = new ProblemFixture();
        var (code, stdout, _) = await fixture.RunCliAsync();
        Assert.Equal(2, code);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("InvalidInput", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(fixture.Fake.ReadInvocations());
    }

    [Theory]
    [InlineData("--unknown", "x")]
    [InlineData("--box-id", "1000")]
    [InlineData("--box-id", "-1")]
    [InlineData("--language", "python")]
    public async Task InvalidCliOptionsReturnStructuredErrors(string option, string value)
    {
        var stdout = new StringWriter();
        var code = await GraderCli.RunAsync(["grade", ".", "main.cpp", "--format", "json", option, value], stdout, new StringWriter());
        Assert.Equal(2, code);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(2, json.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task CancellationReturns130AndCleansUpTheBox()
    {
        using var fixture = new ProblemFixture();
        fixture.AddCase("case", "3", "3");
        fixture.Fake.SetScenario(731, "block");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = fixture.RunCliAsync(token: cancellation.Token);
        await fixture.Fake.WaitForRunStartAsync(731, cancellation.Token);
        cancellation.Cancel();
        var (code, stdout, _) = await run;
        Assert.Equal(130, code);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("Cancelled", json.RootElement.GetProperty("error").GetString());
        Assert.Contains("cleanup:731", fixture.Fake.ReadInvocations());
        using var released = BoxLease.Acquire(731);
    }

    internal sealed class ProblemFixture : IDisposable
    {
        public FakeIsolate Fake { get; } = new();
        public string Root => Fake.RootPath;
        public string Checker { get; set; } = "tokens";
        private readonly List<string> _cases = [];
        private string _driver = "";

        public ProblemFixture() => Fake.SetScenario(731, "grading");

        public void AddCase(string name, string input, string expected)
        {
            Fake.CreateHostFile(name + ".in", input);
            Fake.CreateHostFile(name + ".answer", expected);
            _cases.Add($"\n[[tests]]\nname = \"{name}\"\ninput = \"{name}.in\"\nexpected = \"{name}.answer\"\n");
        }

        public void UseDriver(Type type)
        {
            var path = Fake.CreateHostFile("driver.dll", "");
            File.Copy(type.Assembly.Location, path, overwrite: true);
            _driver = $"driver_assembly = \"driver.dll\"\ndriver_type = \"{type.FullName}\"\n";
        }

        public string WriteManifest()
        {
            var manifest = $"""
                [problem]
                title = "Test problem"
                [limits]
                ct_seconds = 10.0
                ct_memory_kb = 262144
                rt_seconds = 1.0
                rt_memory_kb = 65536
                [grading]
                checker = "{Checker}"
                {_driver}
                """ + string.Join("", _cases);
            Fake.CreateHostFile("problem.toml", manifest);
            return manifest;
        }

        public async Task<(int Code, string Stdout, string Stderr)> RunCliAsync(string format = "json", CancellationToken token = default)
        {
            WriteManifest();
            var source = Fake.CreateHostFile("source.cpp", "int main() {}");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = await GraderCli.RunAsync(["grade", Root, source, "--format", format,
                "--isolate", Fake.BinaryPath, "--box-id", "731"], stdout, stderr, token);
            return (code, stdout.ToString(), stderr.ToString());
        }

        public void Dispose() => Fake.Dispose();
    }
}

public sealed class ThrowingDriver : IGradingDriver
{
    public Task<CheckResult> GradeAsync(GradingContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("driver broke");
}

public sealed class LoggingDriver : IGradingDriver
{
    public async Task<CheckResult> GradeAsync(GradingContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine("driver diagnostic");
        await context.RunAsync(cancellationToken);
        return new CheckResult(true);
    }
}

public sealed class NoRunDriver : IGradingDriver
{
    public Task<CheckResult> GradeAsync(GradingContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new CheckResult(true));
}

public sealed class UnsupportedDriver : IGradingDriver
{
    public int ApiVersion => 42;
    public Task<CheckResult> GradeAsync(GradingContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new CheckResult(true));
}

public sealed class NullDriver : IGradingDriver
{
    public async Task<CheckResult> GradeAsync(GradingContext context, CancellationToken cancellationToken)
    {
        await context.RunAsync(cancellationToken);
        return null!;
    }
}

public sealed class DoubleRunDriver : IGradingDriver
{
    public async Task<CheckResult> GradeAsync(GradingContext context, CancellationToken cancellationToken)
    {
        await context.RunAsync(cancellationToken);
        await context.RunAsync(cancellationToken);
        return new CheckResult(true);
    }
}

[CollectionDefinition("CLI", DisableParallelization = true)]
public sealed class CliCollection;
