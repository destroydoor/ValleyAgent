#nullable enable
using System;
using System.Collections.Generic;

namespace ValleyAgent.TestMod.Scoring;

/// <summary>
///     评分报告：收集评分项，计算加权总分和等级（S/A/B/C/D）。
///     遵循 player-experience-design.md 的评分制规则：
///     - 1-5 分制（1=不可接受，5=优秀）
///     - 综合评分 = Σ(score × weight) / Σ(weight)
///     - 降级规则：任何单项得 1 分，整体降一级
///     - 等级：S(4.5-5.0)、A(3.5-4.4)、B(2.5-3.4)、C(1.5-2.4)、D(1.0-1.4)
/// </summary>
public sealed class ScoreReport
{
    private readonly List<ScoreItem> _items = new();
    private readonly string _sceneName;

    public ScoreReport(string sceneName)
    {
        _sceneName = sceneName;
    }

    /// <summary>添加一个评分项。</summary>
    /// <param name="name">指标名称。</param>
    /// <param name="score">1-5 分。</param>
    /// <param name="weight">权重（>0）。</param>
    /// <param name="detail">详情。</param>
    public void AddItem(string name, int score, double weight, string detail = "")
    {
        if (score < 1 || score > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(score), "score must be between 1 and 5");
        }

        if (weight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "weight must be > 0");
        }

        _items.Add(new ScoreItem(name, score, weight, detail));
    }

    /// <summary>计算加权总分和等级。</summary>
    public ScoreReportResult Calculate()
    {
        double weightedSum = 0;
        double totalWeight = 0;
        var hasOnePoint = false;

        foreach (var item in _items)
        {
            weightedSum += item.Score * item.Weight;
            totalWeight += item.Weight;
            if (item.Score <= 1)
            {
                hasOnePoint = true;
            }
        }

        var totalScore = totalWeight > 0 ? weightedSum / totalWeight : 0;
        var grade = ScoreCalculator.DetermineGrade(totalScore, hasOnePoint);

        return new ScoreReportResult
        {
            Scene = _sceneName,
            Items = new List<ScoreItem>(_items),
            TotalScore = Math.Round(totalScore, 2),
            Grade = grade,
            HasOnePoint = hasOnePoint
        };
    }

    /// <summary>断言至少达到指定等级。</summary>
    public void AssertAtLeast(string minimum)
    {
        var result = Calculate();
        if (!IsGradeAtLeast(result.Grade, minimum))
        {
            throw new ScoreAssertionException(
                $"{_sceneName} 评分 {result.Grade}({result.TotalScore}) 未达到 {minimum} 级门槛。" +
                $" 评分项: {string.Join(", ", _items)}");
        }
    }

    private static bool IsGradeAtLeast(string actual, string minimum)
    {
        var order = new[] { "S", "A", "B", "C", "D" };
        var actualIdx = Array.IndexOf(order, actual);
        var minimumIdx = Array.IndexOf(order, minimum);
        return actualIdx >= 0 && minimumIdx >= 0 && actualIdx <= minimumIdx;
    }
}

public sealed class ScoreItem
{
    public ScoreItem(string name, int score, double weight, string detail)
    {
        Name = name;
        Score = score;
        Weight = weight;
        Detail = detail;
    }

    public string Name { get; }
    public int Score { get; }
    public double Weight { get; }
    public string Detail { get; }

    public override string ToString() => $"{Name}={Score}×{Weight}";
}

public sealed class ScoreReportResult
{
    public string Scene { get; init; } = "";
    public List<ScoreItem> Items { get; init; } = new();
    public double TotalScore { get; init; }
    public string Grade { get; init; } = "D";
    public bool HasOnePoint { get; init; }
}

public sealed class ScoreAssertionException : Exception
{
    public ScoreAssertionException(string message) : base(message)
    {
    }
}