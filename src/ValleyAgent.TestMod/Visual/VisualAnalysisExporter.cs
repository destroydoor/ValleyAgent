#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValleyAgent.TestMod.Visual;

/// <summary>
///     One visual-analysis request describing a single screenshot or video
///     that should be judged by Kimi. Serialized to
///     <c>_visual_analysis_request.json</c> as part of an
///     <see cref="VisualAnalysisExporter.Export" /> call.
/// </summary>
public record VisualAssertionRequest
{
    public string AnalysisId { get; init; } = "";
    public string TestName { get; init; } = "";
    public string Type { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Criteria { get; init; } = "";
    public string ExpectedResult { get; init; } = "";

    /// <summary>
    ///     The player-experience question Kimi should answer about this asset.
    ///     In the experience-paradigm this is the primary prompt; Kimi is
    ///     expected to respond with qualitative issues/observations rather
    ///     than a pass/fail verdict. Defaults to <see cref="Criteria" />.
    /// </summary>
    public string FocusQuestion { get; init; } = "";

    /// <summary>
    ///     Signals Kimi to analyze from the player's perspective (collect
    ///     experience problems) rather than judging pass/fail. Defaults to
    ///     true; the experience-paradigm always sets this.
    /// </summary>
    public bool PlayerPerspective { get; init; } = true;

    public Dictionary<string, object> Context { get; init; } = new();
}

/// <summary>
///     Collects visual-analysis assertions emitted by tests and serializes
///     them into a single <c>_visual_analysis_request.json</c> file consumed
///     by the Kimi visual-analysis pipeline.
/// </summary>
public class VisualAnalysisExporter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly List<VisualAssertionRequest> _assertions = new();

    /// <summary>Number of assertions collected so far.</summary>
    public int Count
    {
        get => _assertions.Count;
    }

    /// <summary>
    ///     Append a single visual assertion. <paramref name="context" /> is
    ///     defensively copied so later mutation by the caller cannot affect
    ///     the stored assertion. If <paramref name="context" /> is null an
    ///     empty dictionary is stored. <paramref name="focusQuestion" /> is
    ///     the player-experience question Kimi should answer; when null it
    ///     defaults to <paramref name="criteria" />. <paramref name="playerPerspective" />
    ///     signals Kimi to analyze from the player's perspective.
    ///     <para>
    ///         Parameter order preserves backward compatibility:
    ///         <paramref name="context" /> remains the 7th parameter so
    ///         existing positional callers continue to compile; the new
    ///         experience-paradigm parameters are appended after it.
    ///     </para>
    /// </summary>
    public void AddAssertion(
        string analysisId,
        string testName,
        string type,
        string filePath,
        string criteria,
        string expectedResult,
        Dictionary<string, object>? context = null,
        string? focusQuestion = null,
        bool playerPerspective = true)
    {
        ArgumentNullException.ThrowIfNull(analysisId);
        ArgumentNullException.ThrowIfNull(testName);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(expectedResult);

        var request = new VisualAssertionRequest
        {
            AnalysisId = analysisId,
            TestName = testName,
            Type = type,
            FilePath = filePath,
            Criteria = criteria,
            ExpectedResult = expectedResult,
            FocusQuestion = string.IsNullOrEmpty(focusQuestion) ? criteria : focusQuestion,
            PlayerPerspective = playerPerspective,
            Context = context != null
                ? new Dictionary<string, object>(context)
                : new Dictionary<string, object>()
        };
        _assertions.Add(request);
    }

    /// <summary>Remove every collected assertion.</summary>
    public void Clear() => _assertions.Clear();

    /// <summary>
    ///     Return a snapshot of the collected assertions. Modifications to the
    ///     returned list do not affect this exporter.
    /// </summary>
    public IReadOnlyList<VisualAssertionRequest> GetAssertions()
    {
        var snapshot = new VisualAssertionRequest[_assertions.Count];
        for (var i = 0; i < _assertions.Count; i++)
        {
            snapshot[i] = _assertions[i];
        }

        return snapshot;
    }

    /// <summary>
    ///     Serialize all collected assertions to <paramref name="outputPath" />
    ///     using the format:
    ///     <code>
    ///     {
    ///       "run_timestamp": "...",
    ///       "total_visual_assertions": N,
    ///       "assertions": [ {VisualAssertionRequest}, ... ]
    ///     }
    ///     </code>
    ///     Outer-container keys use snake_case; assertion records are
    ///     camelCased via <see cref="JsonNamingPolicy.CamelCase" />.
    ///     Returns <paramref name="outputPath" /> on success.
    /// </summary>
    public string Export(string outputPath, string runTimestamp)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(runTimestamp);

        var container = new
        {
            run_timestamp = runTimestamp,
            total_visual_assertions = _assertions.Count,
            assertions = _assertions
        };
        var json = JsonSerializer.Serialize(container, s_jsonOptions);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, json);
        return outputPath;
    }
}