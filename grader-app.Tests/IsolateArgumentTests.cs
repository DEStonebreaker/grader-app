using System.Globalization;
using grader_app.Isolate;
using IsolateCommand = grader_app.Isolate.Isolate;

namespace grader_app.Tests;

public sealed class IsolateArgumentTests
{
    [Fact]
    public void InitBuildsExpectedArguments()
    {
        Assert.Equal(["--cg", "--box-id=17", "--init"], IsolateCommand.Init(17));
    }

    [Fact]
    public void CleanupBuildsExpectedArguments()
    {
        Assert.Equal(["--cg", "--box-id=17", "--cleanup"], IsolateCommand.Cleanup(17));
    }

    [Fact]
    public void RunBuildsExpectedArgumentsInDeterministicOrder()
    {
        var options = new IsolateRunOptions(
            CpuSeconds: 1.5,
            WallSeconds: 3.25,
            MemoryKb: 65_536,
            MaxProcesses: 4,
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["Z_LAST"] = "z value",
                ["A_FIRST"] = "/usr/bin:/bin"
            });

        var arguments = IsolateCommand.Run(
            8,
            options,
            "./main",
            ["--looks-like-an-isolate-option", "two words"],
            "/tmp/result.meta",
            "input.txt");

        Assert.Equal(
            [
                "--cg",
                "--box-id=8",
                "--time=1.5",
                "--wall-time=3.25",
                "--cg-mem=65536",
                "--meta=/tmp/result.meta",
                "--silent",
                "--stdin=input.txt",
                "--processes=4",
                "--env=A_FIRST=/usr/bin:/bin",
                "--env=Z_LAST=z value",
                "--run",
                "--",
                "./main",
                "--looks-like-an-isolate-option",
                "two words"
            ],
            arguments);
    }

    [Fact]
    public void RunFormatsDecimalLimitsWithInvariantCulture()
    {
        // Isolate always expects decimal points, even when the grader host uses decimal commas.
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var arguments = IsolateCommand.Run(
                1,
                CreateOptions(cpuSeconds: 1.5, wallSeconds: 2.75),
                "./main",
                []);

            Assert.Contains("--time=1.5", arguments);
            Assert.Contains("--wall-time=2.75", arguments);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("9NAME")]
    [InlineData("HAS-DASH")]
    [InlineData("HAS SPACE")]
    [InlineData("HAS=EQUALS")]
    public void RunRejectsInvalidEnvironmentVariableNames(string name)
    {
        var options = CreateOptions(
            environmentVariables: new Dictionary<string, string> { [name] = "value" });

        Assert.ThrowsAny<ArgumentException>(() => IsolateCommand.Run(1, options, "./main", []));
    }

    [Fact]
    public void RunRejectsNullCharactersInEnvironmentValues()
    {
        var options = CreateOptions(
            environmentVariables: new Dictionary<string, string> { ["VALID_NAME"] = "a\0b" });

        Assert.Throws<ArgumentException>(() => IsolateCommand.Run(1, options, "./main", []));
    }

    [Fact]
    public void RunAcceptsPosixEnvironmentVariableNames()
    {
        var options = CreateOptions(
            environmentVariables: new Dictionary<string, string>
            {
                ["_"] = "one",
                ["A_2"] = "two"
            });

        var arguments = IsolateCommand.Run(1, options, "./main", []);

        Assert.Contains("--env=_=one", arguments);
        Assert.Contains("--env=A_2=two", arguments);
    }

    private static IsolateRunOptions CreateOptions(
        double cpuSeconds = 1,
        double wallSeconds = 2,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        // Keep unrelated run settings stable so each test isolates one command-line behavior.
        return new IsolateRunOptions(
            cpuSeconds,
            wallSeconds,
            MemoryKb: 1024,
            MaxProcesses: 1,
            environmentVariables ?? new Dictionary<string, string>());
    }
}
