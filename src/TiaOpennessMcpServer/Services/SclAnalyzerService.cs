using System.Text.RegularExpressions;
using TiaOpennessMcpServer.Models;

namespace TiaOpennessMcpServer.Services;

/// <summary>
/// Static analysis engine for Siemens SCL (Structured Control Language),
/// targeting IEC 61131-3 Structured Text as implemented on S7-1200/1500.
/// </summary>
public sealed class SclAnalyzerService
{
    // ── S7-1200 built-in data types ───────────────────────────────────────────
    private static readonly HashSet<string> KnownDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL","BYTE","WORD","DWORD","LWORD",
        "SINT","INT","DINT","LINT","USINT","UINT","UDINT","ULINT",
        "REAL","LREAL","TIME","DATE","TIME_OF_DAY","TOD","DATE_AND_TIME","DT",
        "CHAR","WCHAR","STRING","WSTRING","ARRAY","STRUCT","VARIANT",
        "S5TIME","COUNTER","TIMER","IEC_TIMER","IEC_COUNTER","DB_ANY",
    };

    private static readonly Regex ReBlockOpen   = new Regex(@"^(IF|FOR|WHILE|REPEAT|CASE)\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReBlockClose  = new Regex(@"^(END_IF|END_FOR|END_WHILE|END_REPEAT|END_CASE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReVarSection  = new Regex(@"^(VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_STATIC|VAR_TEMP|VAR_CONSTANT|VAR|END_VAR)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReVarDecl     = new Regex(@"^\s*(\w+)\s*:\s*([\w\[\],\s]+?)\s*(?::=\s*(.+?))?\s*;", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReIfThen      = new Regex(@"^IF\b",                            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReTimerCall   = new Regex(@"\b(TON|TOF|TP|TONR)\s*\(",        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReCounterCall = new Regex(@"\b(CTU|CTD|CTUD)\s*\(",           RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReFcCall      = new Regex(@"\b[A-Z_]\w+\s*\(",                RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReComment     = new Regex(@"^\s*\(\*",                         RegexOptions.Compiled);
    private static readonly Regex ReAssign      = new Regex(@":=\s*(-?\d+(?:\.\d+)?)\s*;",      RegexOptions.Compiled);
    private static readonly Regex ReNamingConv  = new Regex(@"^[a-zA-Z][a-zA-Z0-9_]*$",        RegexOptions.Compiled);

    public Task<SclAnalysisResult> AnalyzeAsync(
        string sclCode, string blockName = "", string blockType = "FB")
    {
        var diagnostics = new List<SclDiagnostic>();
        var variables   = new List<SclVariable>();
        var lines       = sclCode.Split('\n');

        // ── Pass 1: metrics and structural checks ──────────────────────────────
        int loc = 0, commentLines = 0, maxDepth = 0, depth = 0;
        int timerCalls = 0, counterCalls = 0, funcCalls = 0;
        int complexity = 1;  // base cyclomatic complexity

        string currentSection = "";
        var openBlocks = new Stack<(string Name, int Line)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line    = lines[i];
            var trimmed = line.TrimStart();
            int lineNo  = i + 1;

            if (trimmed.StartsWith("//") || ReComment.IsMatch(trimmed))
            {
                commentLines++;
                continue;
            }

            if (trimmed.Length > 0) loc++;

            // Track variable sections
            var secMatch = ReVarSection.Match(trimmed);
            if (secMatch.Success)
            {
                currentSection = secMatch.Groups[1].Value.ToUpperInvariant();
                if (trimmed.StartsWith("END_VAR", StringComparison.OrdinalIgnoreCase))
                    currentSection = "";
            }

            // Collect variable declarations
            var varMatch = ReVarDecl.Match(trimmed);
            if (varMatch.Success && !string.IsNullOrEmpty(currentSection))
            {
                var varName  = varMatch.Groups[1].Value;
                var dataType = varMatch.Groups[2].Value;
                var initVal  = varMatch.Groups[3].Value;

                variables.Add(new SclVariable
                {
                    Name      = varName,
                    DataType  = dataType,
                    Section   = currentSection,
                    InitValue = initVal,
                    Comment   = ExtractInlineComment(trimmed),
                    DeclLine  = lineNo,
                });

                if (!KnownDataTypes.Contains(dataType.Split('[')[0].Trim()))
                {
                    diagnostics.Add(Diag(DiagnosticSeverity.Hint, "SCL001",
                        $"Data type '{dataType}' is not a built-in S7-1200 type — " +
                        $"ensure a matching UDT exists.", lineNo,
                        "If this is a UDT, verify its definition exists in the project."));
                }

                if (!ReNamingConv.IsMatch(varName))
                {
                    diagnostics.Add(Diag(DiagnosticSeverity.Info, "SCL002",
                        $"Variable '{varName}' does not follow PascalCase/camelCase convention.",
                        lineNo, "Rename using PascalCase (e.g. MotorRunning) or camelCase."));
                }
            }

            // Structural depth and open-block tracking
            if (ReBlockOpen.IsMatch(trimmed))
            {
                openBlocks.Push((trimmed.Split(' ')[0], lineNo));
                depth++;
                if (depth > maxDepth) maxDepth = depth;
            }

            if (ReBlockClose.IsMatch(trimmed) && openBlocks.Count > 0)
            {
                openBlocks.Pop();
                depth = Math.Max(0, depth - 1);
            }

            // Cyclomatic complexity: each branch adds 1
            if (ReIfThen.IsMatch(trimmed)                      ||
                trimmed.StartsWith("ELSIF",   StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("ELSE",    StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("CASE",    StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("FOR",     StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("WHILE",   StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("REPEAT",  StringComparison.OrdinalIgnoreCase))
            {
                complexity++;
            }

            if (ReTimerCall.IsMatch(trimmed))   timerCalls++;
            if (ReCounterCall.IsMatch(trimmed)) counterCalls++;
            if (ReFcCall.IsMatch(trimmed))      funcCalls++;

            // Rule: no GOTO
            if (trimmed.StartsWith("GOTO", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Diag(DiagnosticSeverity.Warning, "SCL010",
                    "GOTO statement detected — avoid in structured PLC code.",
                    lineNo, "Restructure using IF/WHILE/FOR to improve readability and safety."));
            }

            // Rule: RETURN in OB blocks
            if (trimmed.StartsWith("RETURN", StringComparison.OrdinalIgnoreCase) &&
                blockType.Equals("OB", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Diag(DiagnosticSeverity.Warning, "SCL011",
                    "RETURN in OB block — early exit in an OB may cause unexpected cycle behaviour.",
                    lineNo, "Review whether early return is intended."));
            }

            // Rule: magic numbers
            var assignMatch = ReAssign.Match(trimmed);
            if (assignMatch.Success)
            {
                var rhs = assignMatch.Groups[1].Value.Trim();
                if (int.TryParse(rhs, out int n) && n != 0 && n != 1 && n != -1)
                {
                    diagnostics.Add(Diag(DiagnosticSeverity.Info, "SCL012",
                        $"Magic number {rhs} — consider a named constant.",
                        lineNo, "Define the value in VAR_CONSTANT or a shared DB."));
                }
            }

            // Rule: division without zero-check nearby
            if (trimmed.Contains('/') && !trimmed.TrimStart().StartsWith("//"))
            {
                bool hasDivCheck = lines.Skip(Math.Max(0, i - 3))
                    .Take(6)
                    .Any(l => l.Contains("<> 0") || l.Contains("!= 0") || l.Contains("> 0"));
                if (!hasDivCheck)
                {
                    diagnostics.Add(Diag(DiagnosticSeverity.Warning, "SCL013",
                        "Division without nearby zero-check — potential runtime divide-by-zero.",
                        lineNo, "Guard with IF divisor <> 0 THEN … END_IF before dividing."));
                }
            }
        }

        // ── Pass 2: unclosed structure check ──────────────────────────────────
        foreach (var (name, openLine) in openBlocks)
        {
            diagnostics.Add(Diag(DiagnosticSeverity.Error, "SCL020",
                $"Unclosed block '{name}' — missing END_{name.ToUpperInvariant()}.",
                openLine, $"Add END_{name.ToUpperInvariant()} to close the block."));
        }

        // ── Pass 3: unused variable detection ─────────────────────────────────
        foreach (var v in variables.Where(v =>
            v.Section is "VAR" or "VAR_INPUT" or "VAR_OUTPUT" or "VAR_STATIC"))
        {
            int usageCount = lines
                .Select((l, idx) => (l, idx))
                .Where(t => t.idx + 1 != v.DeclLine)
                .Count(t => Regex.IsMatch(t.l, $@"\b{Regex.Escape(v.Name)}\b"));

            if (usageCount == 0)
            {
                diagnostics.Add(Diag(DiagnosticSeverity.Warning, "SCL030",
                    $"Variable '{v.Name}' declared but never used.",
                    v.DeclLine, "Remove or use the variable to avoid dead symbols."));
            }
        }

        // ── Complexity gate ────────────────────────────────────────────────────
        if (complexity > 15)
        {
            diagnostics.Add(Diag(DiagnosticSeverity.Warning, "SCL040",
                $"Cyclomatic complexity {complexity} exceeds recommended limit of 15.",
                1, "Split the block into smaller FB/FC units to improve testability."));
        }

        // ── Summary ────────────────────────────────────────────────────────────
        int errors   = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        var summary = errors > 0
            ? $"Analysis failed: {errors} error(s), {warnings} warning(s)."
            : warnings > 0
                ? $"Analysis passed with {warnings} warning(s)."
                : "Analysis passed — no issues found.";

        return Task.FromResult(new SclAnalysisResult
        {
            BlockName   = blockName,
            BlockType   = blockType,
            Diagnostics = diagnostics,
            Variables   = variables,
            Metrics     = new SclMetrics
            {
                LinesOfCode          = loc,
                LinesOfComments      = commentLines,
                CyclomaticComplexity = complexity,
                NestingDepthMax      = maxDepth,
                VariableCount        = variables.Count,
                TimerCallCount       = timerCalls,
                CounterCallCount     = counterCalls,
                FunctionCallCount    = funcCalls,
            },
            IsValid  = errors == 0,
            Summary  = summary,
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SclDiagnostic Diag(
        DiagnosticSeverity severity, string code, string message,
        int line, string suggestion) => new()
    {
        Severity   = severity,
        Code       = code,
        Message    = message,
        Line       = line,
        Column     = 1,
        Suggestion = suggestion,
    };

    private static string ExtractInlineComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line.Substring(idx + 2).Trim() : "";
    }
}
