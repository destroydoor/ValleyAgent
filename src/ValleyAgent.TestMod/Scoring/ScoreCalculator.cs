#nullable enable
namespace ValleyAgent.TestMod.Scoring;

/// <summary>
///     评分计算器：等级判定 + 降级规则。
/// </summary>
public static class ScoreCalculator
{
    /// <summary>
    ///     根据总分和降级标记判定等级。
    ///     S(4.5-5.0)、A(3.5-4.4)、B(2.5-3.4)、C(1.5-2.4)、D(1.0-1.4)
    ///     降级规则：任何单项得 1 分，整体降一级。
    /// </summary>
    public static string DetermineGrade(double score, bool hasOnePoint = false)
    {
        var grade = score switch
        {
            >= 4.5 => "S",
            >= 3.5 => "A",
            >= 2.5 => "B",
            >= 1.5 => "C",
            _ => "D"
        };

        if (hasOnePoint)
        {
            grade = grade switch
            {
                "S" => "A",
                "A" => "B",
                "B" => "C",
                "C" => "D",
                _ => "D"
            };
        }

        return grade;
    }
}