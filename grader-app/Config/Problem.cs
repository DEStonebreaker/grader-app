using Tomlyn.Serialization;

namespace grader_app.Config;

public sealed class ProblemFile
{
    [TomlPropertyName("problem")] public ProblemSection Problem { get; set; } = new();
    [TomlPropertyName("limits")] public LimitSection Limits { get; set; } = new();
    [TomlPropertyName("tests")] public List<TestSection> Tests { get; set; } = [];
    [TomlPropertyName("grading")] public GradingSection Grading { get; set; } = new();
}

public sealed class TestSection
{
    [TomlPropertyName("name")] public string Name { get; set; } = string.Empty;
    [TomlPropertyName("input")] public string Input { get; set; } = string.Empty;
    [TomlPropertyName("expected")] public string Expected { get; set; } = string.Empty;
}

public sealed class GradingSection
{
    [TomlPropertyName("checker")] public string Checker { get; set; } = "tokens";
    [TomlPropertyName("driver_assembly")] public string? DriverAssembly { get; set; }
    [TomlPropertyName("driver_type")] public string? DriverType { get; set; }
}

public sealed class ProblemSection
{
    [TomlPropertyName("title")] public string Title { get; set; } = string.Empty;
    [TomlPropertyName("sample_tests")] public List<string> Samples { get; set; } = [];
}

public sealed class LimitSection
{
    [TomlPropertyName("rt_seconds")] public double RtSeconds { get; set; } = 0.0;
    [TomlPropertyName("rt_memory_kb")] public uint RtMemoryKb { get; set; }

    [TomlPropertyName("ct_seconds")] public double CtSeconds { get; set; } = 0.0;
    [TomlPropertyName("ct_memory_kb")] public uint CtMemoryKb { get; set; }
}
