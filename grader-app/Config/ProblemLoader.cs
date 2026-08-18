namespace grader_app.Config;

using Tomlyn;
using Tomlyn.Model;

public static class ProblemLoader
{
    public static Problem Load(string tomlPath)
    {
        string fullPath = Path.GetFullPath(tomlPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        TomlTable root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(fullPath))!;

        return new Problem
        {
            Meta = ReadMeta(root),
            Limits = ReadLimits(root),
            Checker = ReadChecker(root),
            Tests = ReadTests(root, directory),
            Directory = directory,
        };
    }

    // ── [problem] ─────────────────────────────────────────────

    private static ProblemMeta ReadMeta(TomlTable root)
    {
        TomlTable table = (TomlTable)root["problem"];

        return new ProblemMeta
        {
            Id = (string)table["id"],
            Title = (string)table["title"],
            Revision = GetInt(table, "revision") ?? 1,
        };
    }

    // ── [limits] ──────────────────────────────────────────────

    private static ProblemLimits ReadLimits(TomlTable root)
    {
        if (!root.TryGetValue("limits", out object? value))
        {
            return new ProblemLimits();
        }

        TomlTable table = (TomlTable)value;

        return new ProblemLimits
        {
            TimeSec = GetDouble(table, "time_sec"),
            CgMemoryKb = GetInt(table, "cg_memory_kb"),
        };
    }

    // ── [checker] ─────────────────────────────────────────────

    private static CheckerConfig ReadChecker(TomlTable root)
    {
        if (!root.TryGetValue("checker", out object? value))
        {
            return new CheckerConfig();
        }

        TomlTable table = (TomlTable)value;
        string type = GetString(table, "type") ?? "token";

        return new CheckerConfig
        {
            Type = Enum.Parse<CheckerType>(type, ignoreCase: true),
            TrimTrailingWhitespace = GetBool(table, "trim_trailing_whitespace", true),
            IgnoreTrailingNewline = GetBool(table, "ignore_trailing_newline", true),
            Tolerance = GetDouble(table, "tolerance"),
        };
    }

    // ── [[tests]] ─────────────────────────────────────────────

    private static List<TestMeta> ReadTests(TomlTable root, string directory)
    {
        TomlTableArray array = (TomlTableArray)root["tests"];
        List<TestMeta> tests = new();

        foreach (TomlTable table in array)
        {
            TestMeta test = new()
            {
                Name = (string)table["name"],
                InputPath = Path.Combine(directory, (string)table["input"]),
                AnsPath = Path.Combine(directory, (string)table["answer"]),
                IsSample = GetBool(table, "sample", false),
            };

            tests.Add(test);
        }

        return tests;
    }

    // ── Getters for optional keys ─────────────────────────────

    private static string? GetString(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out object? value))
        {
            return null;
        }

        return (string)value;
    }

    private static int? GetInt(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out object? value))
        {
            return null;
        }

        return (int)(long)value;
    }

    // TOML reads `2` as long and `2.0` as double, so accept either.
    private static double? GetDouble(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out object? value))
        {
            return null;
        }

        if (value is long whole)
        {
            return whole;
        }

        return (double)value;
    }

    private static bool GetBool(TomlTable table, string key, bool fallback)
    {
        if (!table.TryGetValue(key, out object? value))
        {
            return fallback;
        }

        return (bool)value;
    }
}