#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleyAgent.TestMod.Visual;

/// <summary>
///     A deferred visual assertion that waits for Kimi analysis results to be
///     backfilled via <see cref="SetResult" /> or <see cref="SetExperienceResult" />.
///     Created by <see cref="V3TestBase.AssertVisual" /> and tracked by
///     <see cref="VisualAssertionQueue" />.
/// </summary>
public sealed class VisualAssertion
{
    public VisualAssertion(
        string analysisId,
        string testName,
        string label,
        string screenshotPath,
        string fileType,
        string criteria,
        string expectedResult,
        Dictionary<string, object>? context = null,
        string? focusQuestion = null)
    {
        AnalysisId = analysisId ?? throw new ArgumentNullException(nameof(analysisId));
        TestName = testName ?? throw new ArgumentNullException(nameof(testName));
        Label = label ?? throw new ArgumentNullException(nameof(label));
        ScreenshotPath = screenshotPath ?? throw new ArgumentNullException(nameof(screenshotPath));
        FileType = fileType ?? throw new ArgumentNullException(nameof(fileType));
        Criteria = criteria ?? throw new ArgumentNullException(nameof(criteria));
        FocusQuestion = string.IsNullOrEmpty(focusQuestion) ? Criteria : focusQuestion;
        ExpectedResult = expectedResult ?? throw new ArgumentNullException(nameof(expectedResult));
        Context = context != null
            ? new Dictionary<string, object>(context)
            : new Dictionary<string, object>();
        Verdict = "PENDING";
        Confidence = 0.0;
        Reasoning = null;
    }

    /// <summary>Unique id, e.g. "e1_long_pathfind_dialogue_window_visible".</summary>
    public string AnalysisId { get; }

    /// <summary>Parent test name.</summary>
    public string TestName { get; }

    /// <summary>Human-readable label.</summary>
    public string Label { get; }

    /// <summary>File path (PNG or MP4).</summary>
    public string ScreenshotPath { get; }

    /// <summary>"screenshot" or "video".</summary>
    public string FileType { get; }

    /// <summary>Judgment criteria text.</summary>
    public string Criteria { get; }

    /// <summary>
    ///     The player-experience question to ask Kimi. Defaults to
    ///     <see cref="Criteria" /> when not explicitly provided. This is the
    ///     primary prompt in the experience-paradigm; Kimi is expected to
    ///     answer it with qualitative observations rather than pass/fail.
    /// </summary>
    public string FocusQuestion { get; }

    /// <summary>
    ///     Legacy: expected verdict ("PASS" / "FAIL"). Retained for backward
    ///     compatibility. The experience-paradigm no longer judges pass/fail;
    ///     prefer <see cref="ExperienceIssues" /> / <see cref="Severity" />.
    /// </summary>
    public string ExpectedResult { get; }

    /// <summary>Test phase, npc name, etc.</summary>
    public Dictionary<string, object> Context { get; }

    /// <summary>
    ///     Legacy: "PENDING" initially, set to "PASS"/"FAIL"/"NEEDS_REVIEW" by
    ///     <see cref="SetResult" />. In the experience-paradigm this is
    ///     derived from <see cref="ExperienceIssues" /> via
    ///     <see cref="SetExperienceResult" /> (no issues = "PASS",
    ///     any issues = "NEEDS_REVIEW").
    /// </summary>
    public string Verdict { get; private set; }

    /// <summary>0.0 initially, set by <see cref="SetResult" /> / <see cref="SetExperienceResult" />.</summary>
    public double Confidence { get; private set; }

    /// <summary>null initially, set by <see cref="SetResult" /> / <see cref="SetExperienceResult" />.</summary>
    public string? Reasoning { get; private set; }

    /// <summary>
    ///     Player-experience issues collected from Kimi. Empty until
    ///     <see cref="SetExperienceResult" /> is called. Each entry is a
    ///     qualitative description of something that may harm the player
    ///     experience (not a pass/fail verdict).
    /// </summary>
    public List<string> ExperienceIssues { get; private set; } = new();

    /// <summary>
    ///     Free-form observations collected from Kimi. Empty until
    ///     <see cref="SetExperienceResult" /> is called. Observations are
    ///     neutral context (e.g. "dialogue box rendered at 800x600") and do
    ///     not by themselves indicate a problem.
    /// </summary>
    public List<string> Observations { get; private set; } = new();

    /// <summary>
    ///     Worst severity among the collected <see cref="ExperienceIssues" />:
    ///     "blocking" / "minor" / "cosmetic", or null when there are no issues.
    /// </summary>
    public string? Severity { get; private set; }

    /// <summary>True once <see cref="SetResult" /> or <see cref="SetExperienceResult" /> has been called.</summary>
    public bool IsResolved
    {
        get => Verdict != "PENDING";
    }

    /// <summary>
    ///     Legacy: backfill a pass/fail verdict. Prefer
    ///     <see cref="SetExperienceResult" /> in the experience-paradigm.
    /// </summary>
    public void SetResult(string verdict, double confidence, string reasoning)
    {
        Verdict = verdict ?? throw new ArgumentNullException(nameof(verdict));
        Confidence = confidence;
        Reasoning = reasoning ?? string.Empty;
    }

    /// <summary>
    ///     Backfill the experience-paradigm result: a list of player-experience
    ///     issues, free-form observations, and a severity bucket. A legacy
    ///     <see cref="Verdict" /> is derived for backward compatibility
    ///     (no issues = "PASS", any issues = "NEEDS_REVIEW").
    /// </summary>
    public void SetExperienceResult(List<string> issues, List<string> observations, string? severity)
    {
        ExperienceIssues = issues ?? new List<string>();
        Observations = observations ?? new List<string>();
        Severity = severity;
        // Derive a legacy verdict for backward compat: no issues = PASS, issues = NEEDS_REVIEW
        Verdict = ExperienceIssues.Count == 0 ? "PASS" : "NEEDS_REVIEW";
        Confidence = 1.0;
        Reasoning = ExperienceIssues.Count > 0
            ? string.Join("; ", ExperienceIssues)
            : "No issues observed";
    }
}

/// <summary>
///     Tracks pending and resolved <see cref="VisualAssertion" /> instances.
///     Pending assertions wait for Kimi analysis; resolved assertions have
///     been backfilled via <see cref="Resolve" /> or
///     <see cref="ResolveExperience" />.
/// </summary>
public sealed class VisualAssertionQueue
{
    /// <summary>All unresolved assertions.</summary>
    public List<VisualAssertion> Pending { get; } = new();

    /// <summary>All resolved assertions.</summary>
    public List<VisualAssertion> Resolved { get; } = new();

    /// <summary>Total assertions (pending + resolved).</summary>
    public int Count
    {
        get => Pending.Count + Resolved.Count;
    }

    /// <summary>Number of unresolved assertions.</summary>
    public int PendingCount
    {
        get => Pending.Count;
    }

    /// <summary>Number of resolved assertions.</summary>
    public int ResolvedCount
    {
        get => Resolved.Count;
    }

    /// <summary>Add an assertion to the pending list.</summary>
    public void Enqueue(VisualAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        Pending.Add(assertion);
    }

    /// <summary>
    ///     Legacy: find an assertion by analysis id, move it from pending to
    ///     resolved, and backfill a pass/fail verdict. No-op if the id is not
    ///     found or already resolved. Prefer <see cref="ResolveExperience" />
    ///     in the experience-paradigm.
    /// </summary>
    public void Resolve(string analysisId, string verdict, double confidence, string reasoning)
    {
        var assertion = Find(analysisId);
        if (assertion == null || assertion.IsResolved)
        {
            return;
        }

        Pending.Remove(assertion);
        assertion.SetResult(verdict, confidence, reasoning);
        Resolved.Add(assertion);
    }

    /// <summary>
    ///     Find an assertion by analysis id, move it from pending to resolved,
    ///     and backfill its experience result (issues / observations /
    ///     severity). No-op if the id is not found or already resolved.
    /// </summary>
    public void ResolveExperience(string analysisId, List<string> issues, List<string> observations, string? severity)
    {
        var assertion = Find(analysisId);
        if (assertion == null || assertion.IsResolved)
        {
            return;
        }

        Pending.Remove(assertion);
        assertion.SetExperienceResult(issues, observations, severity);
        Resolved.Add(assertion);
    }

    /// <summary>Search both pending and resolved lists for the given analysis id.</summary>
    public VisualAssertion? Find(string analysisId)
    {
        if (string.IsNullOrEmpty(analysisId))
        {
            return null;
        }

        return Pending.FirstOrDefault(a => a.AnalysisId == analysisId)
               ?? Resolved.FirstOrDefault(a => a.AnalysisId == analysisId);
    }

    /// <summary>Clear both pending and resolved lists.</summary>
    public void Clear()
    {
        Pending.Clear();
        Resolved.Clear();
    }

    /// <summary>Enumerate all assertions (pending first, then resolved).</summary>
    public IEnumerable<VisualAssertion> All()
    {
        foreach (var a in Pending)
        {
            yield return a;
        }

        foreach (var a in Resolved)
        {
            yield return a;
        }
    }
}