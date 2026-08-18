using Tomlyn.Model;

namespace grader_app.Config;

public sealed record Problem
{
    public required ProblemMeta Meta { get; init; }
    public required ProblemLimits Limits { get; init; }
    public required CheckerConfig Checker { get; init; }
    public required IReadOnlyList<TestMeta> Tests { get; init; }
    public required string Directory { get; init; }
}

public sealed record ProblemMeta
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public int Revision { get; init; } = 1;
}

// Null means "inherit from the judge-wide default".
public sealed record ProblemLimits
{
    public double? TimeSec { get; init; }
    public int? CgMemoryKb { get; init; }
}

public enum CheckerType
{
    Exact,
    Token,
    Float,
}

public sealed record CheckerConfig
{
    public CheckerType Type { get; init; } = CheckerType.Token;
    public bool TrimTrailingWhitespace { get; init; } = true;
    public bool IgnoreTrailingNewline { get; init; } = true;
    public double? Tolerance { get; init; }
}

public sealed record TestMeta
{
    public required string Name { get; init; }
    public required string InputPath { get; init; }
    public required string AnsPath { get; init; }
    public bool IsSample { get; init; }
}