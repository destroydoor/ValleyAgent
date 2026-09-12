#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValleyAgent.TestMod.Visual;

/// <summary>
///     One Kimi-produced visual-analysis result. <see cref="Verdict" /> uses
///     the strings <c>"PASS"</c>, <c>"FAIL"</c>, or <c>"NEEDS_REVIEW"</c>.
///     <see cref="Confidence" /> is a 0.0–1.0 score. <see cref="TestName" />
///     links the result back to a code-test entry in
///     <c>_summary.json</c> for the merge performed by
///     <see cref="VisualReportGenerator.GenerateFinalReport" />. When Kimi
///     does not echo <c>testName</c> back, the result is treated as an
///     orphan and still appears in the final report under
///     <c>[visual] {AnalysisId}</c>.
/// </summary>
public record VisualAnalysisResult
{
    public string AnalysisId { get; init; } = "";
    public string TestName { get; init; } = "";
    public string Verdict { get; init; } = "";
    public double Confidence { get; init; }
    public string Reasoning { get; init; } = "";
    public List<string> Observations { get; init; } = new();
    public List<string> Issues { get; init; } = new();

    /// <summary>
    ///     Worst severity among the reported <see cref="Issues" />:
    ///     "blocking" / "minor" / "cosmetic", or null when there are no
    ///     issues. Populated by Kimi in the experience-paradigm.
    /// </summary>
    public string? Severity { get; init; }
}

/// <summary>
///     Top-level container of the Kimi-produced
///     <c>_visual_results.json</c> file.
/// </summary>
public record VisualReportSummary
{
    public string RunTimestamp { get; init; } = "";
    public int TotalAnalyzed { get; init; }
    public List<VisualAnalysisResult> Results { get; init; } = new();
}

/// <summary>
///     DTO mirroring one entry of <c>_summary.json</c> produced by
///     <c>V3TestRunner</c>. The serialized property names are PascalCase
///     (no <see cref="JsonNamingPolicy" /> was applied at write time), so the
///     C# properties match the JSON keys directly without attributes.
/// </summary>
public record CodeSummaryEntry
{
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public int Passed { get; init; }
    public int Failed { get; init; }
    public string Status { get; init; } = "";
    public bool TimedOut { get; init; }
}

/// <summary>
///     DTO mirroring the outer shape of <c>_summary.json</c> produced by
///     <c>V3TestRunner</c>. The outer container uses snake_case JSON keys
///     (written by an anonymous type without a naming policy), matched here
///     via <see cref="JsonPropertyNameAttribute" />.
/// </summary>
public record CodeSummary
{
    [JsonPropertyName("run_timestamp")] public string RunTimestamp { get; init; } = "";
    [JsonPropertyName("total_tests")] public int TotalTests { get; init; }
    [JsonPropertyName("total_passed")] public int TotalPassed { get; init; }
    [JsonPropertyName("total_failed")] public int TotalFailed { get; init; }
    [JsonPropertyName("total_skipped")] public int TotalSkipped { get; init; }
    [JsonPropertyName("tests")] public List<CodeSummaryEntry> Tests { get; init; } = new();
}

/// <summary>
///     Parses Kimi-produced visual-analysis results and merges them with the
///     code-assertion summary produced by <c>V3TestRunner</c> to produce a
///     single <c>final_report.json</c>.
///     All file I/O is defensive — missing or malformed inputs yield partial
///     reports and never throw.
/// </summary>
public class VisualReportGenerator
{
    /// <summary>
    ///     Options for parsing <c>_visual_results.json</c> and serializing
    ///     <see cref="VisualReportSummary" />-shaped records. Uses CamelCase
    ///     policy to match the format produced by
    ///     <see cref="VisualAnalysisExporter" /> and consumed by Kimi.
    /// </summary>
    private static readonly JsonSerializerOptions s_visualJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    ///     Options for parsing <c>_summary.json</c>. No naming policy is
    ///     applied so PascalCase property names match the JSON keys written
    ///     by <c>V3TestRunner</c> verbatim; outer snake_case keys are matched
    ///     via <see cref="JsonPropertyNameAttribute" /> on
    ///     <see cref="CodeSummary" />.
    /// </summary>
    private static readonly JsonSerializerOptions s_codeSummaryOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    ///     Options for serializing the final report. No naming policy is
    ///     applied so the snake_case keys coming from the anonymous report
    ///     container are preserved verbatim in the JSON output.
    /// </summary>
    private static readonly JsonSerializerOptions s_reportOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly VisualReportSummary? _summary;

    public VisualReportGenerator(VisualReportSummary? summary = null)
    {
        _summary = summary;
    }

    /// <summary>
    ///     Look up the verdict for <paramref name="analysisId" /> in the loaded
    ///     summary. Returns <c>"PENDING"</c> when no summary is loaded or the
    ///     id cannot be found (e.g. Kimi has not produced results yet).
    /// </summary>
    public string GetVerdict(string analysisId)
    {
        if (string.IsNullOrEmpty(analysisId) || _summary == null)
        {
            return "PENDING";
        }

        foreach (var r in _summary.Results)
        {
            if (r.AnalysisId == analysisId)
            {
                return r.Verdict;
            }
        }

        return "PENDING";
    }

    /// <summary>
    ///     Load <paramref name="resultsJsonPath" /> (the Kimi-produced
    ///     <c>_visual_results.json</c>) and return the parsed
    ///     <see cref="VisualReportSummary" />. Returns null on any I/O or
    ///     JSON error — callers should treat null as "no Kimi results yet".
    /// </summary>
    public static VisualReportSummary? LoadResults(string resultsJsonPath)
    {
        if (string.IsNullOrEmpty(resultsJsonPath) || !File.Exists(resultsJsonPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(resultsJsonPath);
            return JsonSerializer.Deserialize<VisualReportSummary>(json, s_visualJsonOptions);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Merge the code-assertion summary at
    ///     <paramref name="codeAssertionsJsonPath" /> (the
    ///     <c>_summary.json</c> produced by V3TestRunner) with the visual
    ///     results at <paramref name="visualResultsJsonPath" /> (the
    ///     <c>_visual_results.json</c> produced by Kimi) and write a single
    ///     <c>final_report.json</c> to <paramref name="outputPath" />.
    ///     Missing or malformed inputs produce a partial report and never
    ///     throw. Returns <paramref name="outputPath" /> on success.
    /// </summary>
    public static string GenerateFinalReport(
        string codeAssertionsJsonPath,
        string visualResultsJsonPath,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(outputPath);

        var codeSummary = LoadCodeSummary(codeAssertionsJsonPath);
        if (codeSummary == null)
        {
            Console.Error.WriteLine(
                $"[VisualReportGenerator] Code summary not found or unreadable: {codeAssertionsJsonPath}");
        }

        var visualSummary = LoadResults(visualResultsJsonPath);
        if (visualSummary == null)
        {
            Console.Error.WriteLine(
                $"[VisualReportGenerator] Visual results not found or unreadable: {visualResultsJsonPath}");
        }

        var json = BuildReport(codeSummary, visualSummary);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    /// <summary>
    ///     Parse the code-assertion summary JSON. Returns null on any I/O or
    ///     JSON error.
    /// </summary>
    private static CodeSummary? LoadCodeSummary(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CodeSummary>(json, s_codeSummaryOptions);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Build the merged <c>final_report.json</c> content from the two
    ///     summaries. Both inputs may be null (defensive). The returned
    ///     string is ready to write to disk.
    ///     <para>
    ///         Experience-paradigm: visual results no longer produce
    ///         pass/fail verdicts or a letter grade. Instead each test
    ///         carries an <c>experience_issues</c> list (collected from
    ///         Kimi), an <c>experience_severity</c> bucket, and a
    ///         <c>visual_observations</c> count. Code assertions remain
    ///         genuinely binary (passed/failed). The top-level report
    ///         carries <c>total_code_passed</c> / <c>total_code_failed</c>
    ///         / <c>total_experience_issues</c> and no grade.
    ///     </para>
    /// </summary>
    private static string BuildReport(CodeSummary? codeSummary, VisualReportSummary? visualSummary)
    {
        // Group visual results by test name for per-test lookup. Results
        // without a TestName become orphans and are appended as separate
        // test entries at the end.
        var visualByTest = new Dictionary<string, List<VisualAnalysisResult>>(StringComparer.Ordinal);
        var orphanResults = new List<VisualAnalysisResult>();
        if (visualSummary?.Results != null)
        {
            foreach (var r in visualSummary.Results)
            {
                if (string.IsNullOrEmpty(r.TestName))
                {
                    orphanResults.Add(r);
                    continue;
                }

                if (!visualByTest.TryGetValue(r.TestName, out var list))
                {
                    list = new List<VisualAnalysisResult>();
                    visualByTest[r.TestName] = list;
                }

                list.Add(r);
            }
        }

        var tests = new List<object>();
        int totalCodePassed = 0, totalCodeFailed = 0, totalExperienceIssues = 0;
        var codeTests = codeSummary?.Tests ?? new List<CodeSummaryEntry>();

        foreach (var codeEntry in codeTests)
        {
            totalCodePassed += codeEntry.Passed;
            totalCodeFailed += codeEntry.Failed;

            var visualResults = visualByTest.TryGetValue(codeEntry.Name, out var list)
                ? list
                : new List<VisualAnalysisResult>();

            var experienceIssues = new List<string>();
            var observationCount = 0;
            string? worstSeverity = null;
            foreach (var v in visualResults)
            {
                if (v.Issues != null)
                {
                    experienceIssues.AddRange(v.Issues);
                }

                if (v.Observations != null)
                {
                    observationCount += v.Observations.Count;
                }

                worstSeverity = WorstSeverity(worstSeverity, v.Severity);
            }

            totalExperienceIssues += experienceIssues.Count;

            tests.Add(new
            {
                test_name = codeEntry.Name,
                group = codeEntry.Group,
                code_assertions = new { passed = codeEntry.Passed, failed = codeEntry.Failed },
                visual_observations = observationCount,
                experience_issues = experienceIssues,
                experience_severity = worstSeverity
            });
        }

        // Add orphan visual results (no matching code test) as separate entries
        // so that Kimi's collected issues are never silently dropped.
        foreach (var orphan in orphanResults)
        {
            var issues = orphan.Issues ?? new List<string>();
            var observationCount = orphan.Observations?.Count ?? 0;
            totalExperienceIssues += issues.Count;

            tests.Add(new
            {
                test_name = $"[visual] {orphan.AnalysisId}",
                group = "Visual",
                code_assertions = new { passed = 0, failed = 0 },
                visual_observations = observationCount,
                experience_issues = issues,
                experience_severity = orphan.Severity
            });
        }

        var totalTests = tests.Count;
        var runTimestamp = codeSummary?.RunTimestamp;
        if (string.IsNullOrEmpty(runTimestamp) && visualSummary != null)
        {
            runTimestamp = visualSummary.RunTimestamp;
        }

        if (string.IsNullOrEmpty(runTimestamp))
        {
            runTimestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        var report = new
        {
            run_timestamp = runTimestamp,
            total_tests = totalTests,
            total_code_passed = totalCodePassed,
            total_code_failed = totalCodeFailed,
            total_experience_issues = totalExperienceIssues,
            tests
        };

        return JsonSerializer.Serialize(report, s_reportOptions);
    }

    /// <summary>
    ///     Return the more severe of two severity strings, where
    ///     "blocking" &gt; "minor" &gt; "cosmetic" &gt; null. Unknown /
    ///     empty values are treated as the least severe. A null
    ///     <paramref name="candidate" /> leaves <paramref name="current" />
    ///     unchanged.
    /// </summary>
    private static string? WorstSeverity(string? current, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return current;
        }

        return SeverityRank(current) >= SeverityRank(candidate) ? current : candidate;
    }

    /// <summary>
    ///     Map a severity string to an ordinal rank for comparison. Higher
    ///     means more severe. Unknown / null values map to 0.
    /// </summary>
    private static int SeverityRank(string? severity)
    {
        return severity switch
        {
            "blocking" => 3,
            "minor" => 2,
            "cosmetic" => 1,
            _ => 0
        };
    }
}