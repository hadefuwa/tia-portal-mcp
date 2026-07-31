namespace TiaOpennessMcpServer.Models;

public enum DiagnosticSeverity { Error, Warning, Info, Hint }

public sealed record SclDiagnostic
{
    public required DiagnosticSeverity Severity    { get; init; }
    public required string             Code        { get; init; }
    public required string             Message     { get; init; }
    public required int                Line        { get; init; }
    public required int                Column      { get; init; }
    public required string             Suggestion  { get; init; }
}

public sealed record SclVariable
{
    public required string Name      { get; init; }
    public required string DataType  { get; init; }
    public required string Section   { get; init; }   // VAR_INPUT, VAR_OUTPUT, VAR, VAR_TEMP, etc.
    public required string InitValue { get; init; }
    public required string Comment   { get; init; }
    public required int    DeclLine  { get; init; }
}

public sealed record SclMetrics
{
    public required int LinesOfCode           { get; init; }
    public required int LinesOfComments       { get; init; }
    public required int CyclomaticComplexity  { get; init; }
    public required int NestingDepthMax       { get; init; }
    public required int VariableCount         { get; init; }
    public required int TimerCallCount        { get; init; }
    public required int CounterCallCount      { get; init; }
    public required int FunctionCallCount     { get; init; }
}

public sealed record SclAnalysisResult
{
    public required string                       BlockName   { get; init; }
    public required string                       BlockType   { get; init; }
    public required IReadOnlyList<SclDiagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<SclVariable>   Variables   { get; init; }
    public required SclMetrics                   Metrics     { get; init; }
    public required bool                         IsValid     { get; init; }
    public required string                       Summary     { get; init; }
}
