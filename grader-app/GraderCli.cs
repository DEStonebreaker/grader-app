using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using grader_app.Grading;
using grader_app.Isolate;

namespace grader_app;

public static class GraderCli
{
    private const string Usage = """
        Usage:
          grader-app grade <problem-folder> <source-file> [options]
          grader-app --help

        Options:
          --language c|cpp      Default: infer from .c, .cpp, .cc, or .cxx
          --format text|json    Default: text (JSON schema version 1)
          --isolate <path>      Absolute path; default: VALIMARK_ISOLATE_PATH or /usr/local/bin/isolate
          --box-id <0..999>     Default: 0; held exclusively for the entire grading job

        Problem folders contain problem.toml and named input/expected-output files.
        Custom C# drivers are trusted host code. Compile student C/C++ only through Isolate.
        Exit codes: 0 passed, 1 grader/driver fault, 2 invalid usage/configuration,
                    3 compilation failed, 4 submission failed, 130 cancelled.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        // Detect JSON early so even argument/configuration failures have a machine-readable response.
        var json = args.Zip(args.Skip(1)).Any(pair => pair.First == "--format" && pair.Second == "json");
        try
        {
            if ((args.Length == 1 && args[0] is "--help" or "-h") ||
                (args.Length == 2 && args[0] == "grade" && args[1] is "--help" or "-h"))
            {
                await output.WriteLineAsync(Usage);
                return 0;
            }
            var options = Parse(args);
            json = options.Format == "json";
            var problem = ProblemLoader.Load(options.Problem);
            var source = Path.GetFullPath(options.Source);
            if (!File.Exists(source) || new FileInfo(source).LinkTarget is not null)
                throw new ArgumentException("Source must be an existing regular file, not a symbolic link.");
            var isolatePath = options.Isolate;
            if (!Path.IsPathFullyQualified(isolatePath) || !File.Exists(isolatePath))
                throw new ArgumentException("Isolate must be an existing absolute executable path. Use --isolate or VALIMARK_ISOLATE_PATH.");

            GradeReport report;
            using (BoxLease.Acquire(options.BoxId))
            {
                // Driver Console output is diagnostic, including constructors and module initializers.
                // This is a single-job CLI, not an API for concurrent invocations in one process.
                var savedOut = Console.Out;
                try
                {
                    Console.SetOut(error);
                    report = await new Grader(new IsolateExecutor(isolatePath)).GradeAsync(
                        problem, source, options.Language, options.BoxId, cancellationToken);
                }
                finally { Console.SetOut(savedOut); }
            }
            if (json)
                await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions));
            else
                await WriteTextAsync(report, output);
            return report.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteErrorAsync("Cancelled", "Grading cancelled.", 130, json, output, error);
            return 130;
        }
        catch (Exception exception)
        {
            var invalid = exception is ArgumentException or InvalidDataException or FileNotFoundException or DirectoryNotFoundException
                          || exception.GetType().Namespace?.StartsWith("Tomlyn", StringComparison.Ordinal) == true;
            var code = invalid ? 2 : 1;
            await WriteErrorAsync(invalid ? "InvalidInput" : "InfrastructureError", exception.Message, code, json, output, error);
            return code;
        }
    }

    private sealed record Options(string Problem, string Source, SourceLanguage Language, string Format,
        string Isolate, uint BoxId);

    private static Options Parse(string[] args)
    {
        if (args.Length < 3 || args[0] != "grade")
            throw new ArgumentException("Expected: grader-app grade <problem-folder> <source-file>. Use --help for options.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Length; i += 2)
        {
            if (args[i] is not ("--language" or "--format" or "--isolate" or "--box-id") || i + 1 >= args.Length)
                throw new ArgumentException($"Unknown or incomplete option: {args[i]}. Use --help.");
            if (!values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException($"Duplicate option: {args[i]}.");
        }
        var languageText = values.GetValueOrDefault("--language") ?? (Path.GetExtension(args[2]).ToLowerInvariant() switch
        {
            ".c" => "c",
            ".cpp" or ".cc" or ".cxx" => "cpp",
            _ => ""
        });
        var language = languageText switch
        {
            "c" => SourceLanguage.C,
            "cpp" or "c++" => SourceLanguage.Cpp,
            _ => throw new ArgumentException("Use --language c|cpp, or a recognized C/C++ file extension.")
        };
        var format = values.GetValueOrDefault("--format", "text");
        if (format is not ("text" or "json"))
            throw new ArgumentException("--format must be text or json.");
        if (!uint.TryParse(values.GetValueOrDefault("--box-id", "0"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var boxId) || boxId > 999)
            throw new ArgumentException("--box-id must be between 0 and 999.");
        return new Options(args[1], args[2], language, format, values.GetValueOrDefault("--isolate") ??
            Environment.GetEnvironmentVariable("VALIMARK_ISOLATE_PATH") ?? "/usr/local/bin/isolate", boxId);
    }

    private static async Task WriteTextAsync(GradeReport report, TextWriter output)
    {
        await output.WriteLineAsync($"{report.Problem}: {report.Verdict}");
        if (report.Compilation is { } compilation && !compilation.Succeeded)
        {
            await output.WriteLineAsync($"Compilation: {compilation.Outcome}");
            await output.WriteLineAsync(compilation.Execution.StandardError);
        }
        foreach (var test in report.Cases)
        {
            await output.WriteLineAsync($"  {test.Name}: {test.Verdict}" +
                (string.IsNullOrEmpty(test.Message) ? "" : $" — {test.Message}"));
        }
        await output.WriteLineAsync($"{report.Cases.Count(test => test.Verdict == Verdict.Passed)}/{report.Cases.Count} cases passed.");
        if (report.Message is not null)
            await output.WriteLineAsync(report.Message);
    }

    private static async Task WriteErrorAsync(string kind, string message, int code, bool json,
        TextWriter output, TextWriter error)
    {
        if (json)
            await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, error = kind, message, exitCode = code }, JsonOptions));
        else
            await error.WriteLineAsync($"{kind}: {message}");
    }
}
