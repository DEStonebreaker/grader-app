using System.Globalization;

namespace grader_app.Isolate;

public static class Isolate
{
    // private const string ISOLATE_BINARY = "/usr/local/bin/isolate";

    /* Isolate Commands */

    public static IReadOnlyList<string> Init(uint boxId)
    {
        return
        [
            "--cg",
            $"--box-id={boxId}",
            "--init"
        ];
    }

    public static IReadOnlyList<string> Cleanup(uint boxId)
    {
        return
        [
            "--cg",
            $"--box-id={boxId}",
            "--cleanup"
        ];
    }

    public static IReadOnlyList<string> Run(
        uint boxId,
        IsolateRunOptions opts,
        string executable,
        IEnumerable<string>? executableArguments,
        string? metadataPath = null,
        string? standardInputPath = null)
    {
        // Keep every option as a separate argument so executable-controlled values never reach a shell.
        ValidateOptions(opts);
        var arguments = new List<string>
        {
            "--cg",
            $"--box-id={boxId}",
            $"--time={opts.CpuSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"--wall-time={opts.WallSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"--cg-mem={opts.MemoryKb}"
        };

        if (metadataPath is not null)
        {
            arguments.Add($"--meta={metadataPath}");
            arguments.Add("--silent");
        }

        if (standardInputPath is not null)
        {
            arguments.Add($"--stdin={standardInputPath}");
        }

        if (opts.MaxProcesses > 1)
        {
            arguments.Add($"--processes={opts.MaxProcesses}");
        }

        foreach (var environmentVariable in opts.EnvironmentVariables
                     .OrderBy(variable => variable.Key, StringComparer.Ordinal))
        {
            arguments.Add(CreateEnvironmentArgument(
                environmentVariable.Key,
                environmentVariable.Value));
        }

        arguments.Add("--run");
        arguments.Add("--");
        arguments.Add(executable);

        if (executableArguments is not null)
        {
            arguments.AddRange(executableArguments);
        }

        return arguments;
    }

    private static string CreateEnvironmentArgument(string name, string value)
    {
        // Accept POSIX-style names only and keep the complete assignment in one CliWrap argument.
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!IsEnvironmentNameStart(name[0])
            || name.Skip(1).Any(character => !IsEnvironmentNameCharacter(character)))
        {
            throw new ArgumentException($"Invalid environment variable name: {name}", nameof(name));
        }

        if (value.Contains('\0'))
        {
            throw new ArgumentException("Environment variable value cannot contain a null character.", nameof(value));
        }

        return $"--env={name}={value}";
    }

    public static void ValidateOptions(IsolateRunOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (!double.IsFinite(opts.CpuSeconds) || opts.CpuSeconds <= 0 ||
            !double.IsFinite(opts.WallSeconds) || opts.WallSeconds <= 0 ||
            opts.MemoryKb == 0 || opts.MaxProcesses == 0)
            throw new ArgumentException("Sandbox time, memory, and process limits must be positive and finite.", nameof(opts));
        ArgumentNullException.ThrowIfNull(opts.EnvironmentVariables);
        foreach (var variable in opts.EnvironmentVariables)
            _ = CreateEnvironmentArgument(variable.Key, variable.Value);
    }

    private static bool IsEnvironmentNameStart(char character)
    {
        return character == '_' || char.IsAsciiLetter(character);
    }

    private static bool IsEnvironmentNameCharacter(char character)
    {
        return IsEnvironmentNameStart(character) || char.IsAsciiDigit(character);
    }
}
