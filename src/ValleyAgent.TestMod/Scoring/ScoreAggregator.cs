#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValleyAgent.TestMod.Scoring;

/// <summary>
///     体验问题聚合器：后处理 <c>_final_report.json</c>（由
///     <see cref="ValleyAgent.TestMod.Visual.VisualReportGenerator.GenerateFinalReport" />
///     产出），收集全部玩家体验问题并按严重度分类，写出
///     <c>_score_report.json</c>。
///     <para>
///         体验范式说明：本聚合器不再计算 S/A/B/C/D/F 等级，也不再产出
///         pass/fail 结论。视觉断言的结果是“玩家体验问题”集合（定性、
///         不可量化），本聚合器仅做汇总与按严重度分桶。代码断言仍保留
///         pass/fail 计数（NPC 是否移动到正确格子等二元事实）。
///     </para>
///     所有解析均为防御式 —— 输入缺失或格式错误时返回空汇总与错误信息，不抛异常。
/// </summary>
public sealed class ScoreAggregator
{
    /// <summary>
    ///     序列化 <c>_score_report.json</c> 使用的选项：缩进输出，并忽略 null 字段。
    ///     不应用 <see cref="JsonNamingPolicy" />，使匿名类型中显式写出的
    ///     snake_case 属性名原样输出（与 <c>VisualReportGenerator</c> 保持一致）。
    /// </summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _outputDir;
    private readonly string _runTimestamp;

    public ScoreAggregator(string outputDir, string runTimestamp)
    {
        if (string.IsNullOrEmpty(outputDir))
        {
            throw new ArgumentNullException(nameof(outputDir));
        }

        if (string.IsNullOrEmpty(runTimestamp))
        {
            throw new ArgumentNullException(nameof(runTimestamp));
        }

        _outputDir = outputDir;
        _runTimestamp = runTimestamp;
    }

    /// <summary>
    ///     读取 <paramref name="finalReportPath" /> 指向的
    ///     <c>_final_report.json</c>，聚合全部玩家体验问题并按严重度分桶，
    ///     写出 <c>_score_report.json</c>，返回输出路径。
    ///     输入缺失或格式错误时，仍会写出一个含错误信息的空汇总报告，不抛异常。
    /// </summary>
    public string Aggregate(string finalReportPath)
    {
        ArgumentNullException.ThrowIfNull(finalReportPath);

        _ = Directory.CreateDirectory(_outputDir);
        var outputPath = Path.Combine(_outputDir, "_score_report.json");

        var data = ParseFinalReport(finalReportPath);

        int blocking = 0, minor = 0, cosmetic = 0;
        foreach (var issue in data.Issues)
        {
            switch (issue.Severity)
            {
                case "blocking":
                    blocking++;
                    break;
                case "minor":
                    minor++;
                    break;
                case "cosmetic":
                    cosmetic++;
                    break;
            }
        }

        string summary;
        if (data.ErrorMessage != null)
        {
            summary = $"解析失败：{data.ErrorMessage}";
        }
        else if (data.TotalExperienceIssues == 0)
        {
            summary = "未发现玩家体验问题。";
        }
        else
        {
            summary = $"发现 {data.TotalExperienceIssues} 个体验问题，" +
                      $"其中 {blocking} 个阻断级、{minor} 个一般级、{cosmetic} 个外观级。";
        }

        var report = new
        {
            run_timestamp = data.RunTimestamp.Length > 0 ? data.RunTimestamp : _runTimestamp,
            total_tests = data.TotalTests,
            total_code_passed = data.TotalCodePassed,
            total_code_failed = data.TotalCodeFailed,
            total_experience_issues = data.TotalExperienceIssues,
            issues_by_severity = new { blocking, minor, cosmetic },
            issues = data.Issues
                .Select(i => new { test = i.Test, severity = i.Severity, description = i.Description })
                .ToList(),
            summary,
            error = data.ErrorMessage
        };

        var json = JsonSerializer.Serialize(report, s_jsonOptions);
        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    // ── 2026-08-20 Phase 4：双轨报告（L3 人评收尾，不合成等级）───────────

    /// <summary>
    ///     自动指标报告（<c>_auto_report.json</c>）：代码断言通过率 + 复杂场景
    ///     幻觉率/执行成功率 + 硬门槛违规检测。自动指标只做"违规检测"，
    ///     不做"合格判定"——最终结论由人看 _auto_report + _human_report 两份报告下。
    /// </summary>
    /// <param name="summaryPath">测试运行 _summary.json 路径。</param>
    /// <param name="complexReportPath">可选：ComplexScenario _complex_scenario_report.json 路径。</param>
    public string AggregateAuto(string summaryPath, string? complexReportPath = null)
    {
        ArgumentNullException.ThrowIfNull(summaryPath);

        _ = Directory.CreateDirectory(_outputDir);
        var outputPath = Path.Combine(_outputDir, "_auto_report.json");

        var verdicts = new List<string>();
        var report = new
        {
            run_timestamp = DateTime.Now.ToString("s"),
            code_assertions = ParseSummaryCode(summaryPath, out var codePassed, out var codeFailed),
            code_passed = codePassed,
            code_failed = codeFailed,
            complex_scenario = ParseComplexReport(complexReportPath, out var complexData),
            verdicts
        };

        // 硬门槛违规检测（只报违规，不下结论）
        if (complexData != null)
        {
            if (complexData.HardGateToolCall is false)
            {
                verdicts.Add("hard_gate_failed: tool_call_success_rate < 0.95");
            }

            if (complexData.HardGateLedger is false)
            {
                verdicts.Add("hard_gate_failed: ledger_consistency_rate < 0.95");
            }

            if (complexData.HardGateReaction is false)
            {
                verdicts.Add("hard_gate_failed: dialogue_reaction_rate < 1.0");
            }
        }

        // verdicts 是匿名类型闭包引用，序列化前重建 report（匿名类型属性在构造后不可变）
        var finalReport = new
        {
            report.run_timestamp,
            report.code_assertions,
            report.code_passed,
            report.code_failed,
            report.complex_scenario,
            verdicts
        };
        var json = JsonSerializer.Serialize(finalReport, s_jsonOptions);
        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    /// <summary>
    ///     人评报告模板（<c>_human_report.json</c>）：5 项量表 + 回放链接。
    ///     大模型只能给参考分——最终体验分由人填写 scores/notes。
    /// </summary>
    /// <param name="replayDirs">回放目录列表（如 logs/complex_scenario/{ts}）。</param>
    public string WriteHumanReportTemplate(IReadOnlyList<string>? replayDirs = null)
    {
        _ = Directory.CreateDirectory(_outputDir);
        var outputPath = Path.Combine(_outputDir, "_human_report.json");

        var report = new
        {
            run_timestamp = DateTime.Now.ToString("s"),
            replay_links = replayDirs ?? new List<string>(),
            questionnaire = new[]
            {
                new { id = "q1", question = "这段对话里 NPC 像不像本人？（性格/口吻/偏好）", scale = "1-5" },
                new { id = "q2", question = "导演编排的剧情自不自洽？（事件/相遇/氛围）", scale = "1-5" },
                new { id = "q3", question = "玩家说过的事 NPC 有没有记住？（记忆一致性）", scale = "1-5" },
                new { id = "q4", question = "反应延迟是否合理？（对话/动作响应节奏）", scale = "1-5" },
                new { id = "q5", question = "有没有任何出戏瞬间？（状态-记忆分裂，海莉事件类）", scale = "1-5" }
            },
            scores = new Dictionary<string, int>(), // 人填：q1..q5
            notes = "" // 人填：自由备注
        };

        var json = JsonSerializer.Serialize(report, s_jsonOptions);
        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    // ── 双轨报告解析辅助 ──

    private static object ParseSummaryCode(string summaryPath, out int passed, out int failed)
    {
        passed = 0;
        failed = 0;
        try
        {
            if (!File.Exists(summaryPath))
            {
                return new { parsed = false, error = "summary not found" };
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("total_passed", out var p))
            {
                passed = p.GetInt32();
            }

            if (root.TryGetProperty("total_failed", out var f))
            {
                failed = f.GetInt32();
            }

            var total = passed + failed;
            return new
            {
                parsed = true,
                pass_rate = total == 0 ? 1.0 : Math.Round((double)passed / total, 4),
                passed,
                failed,
                total
            };
        }
        catch (Exception ex)
        {
            return new { parsed = false, error = ex.Message };
        }
    }

    private static object? ParseComplexReport(string? complexReportPath, out ComplexData? data)
    {
        data = null;
        if (string.IsNullOrEmpty(complexReportPath) || !File.Exists(complexReportPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(complexReportPath));
            var root = doc.RootElement;
            var hallucinationRate = root.TryGetProperty("hallucination_rate", out var hr) ? hr.GetDouble() : 0.0;
            var hallucinationCount = root.TryGetProperty("hallucination_count", out var hc) ? hc.GetInt32() : 0;
            data = new ComplexData
            {
                HallucinationRate = hallucinationRate,
                HallucinationCount = hallucinationCount,
                ToolCallRate = ReadRate(root, "execution_success", "tool_call_success_rate"),
                LedgerRate = ReadRate(root, "execution_success", "ledger_consistency_rate"),
                ReactionRate = ReadRate(root, "execution_success", "dialogue_reaction_rate")
            };
            return new
            {
                parsed = true,
                llm_mode = root.TryGetProperty("llm_mode", out var lm) ? lm.GetString() : "unknown",
                hallucination_rate = hallucinationRate,
                hallucination_count = hallucinationCount,
                tool_call_success_rate = data.ToolCallRate,
                ledger_consistency_rate = data.LedgerRate,
                dialogue_reaction_rate = data.ReactionRate
            };
        }
        catch (Exception ex)
        {
            return new { parsed = false, error = ex.Message };
        }
    }

    private static double ReadRate(JsonElement root, string section, string field)
    {
        if (root.TryGetProperty(section, out var s) && s.TryGetProperty(field, out var v))
        {
            return v.GetDouble();
        }

        return 1.0;
    }

    private sealed class ComplexData
    {
        public double HallucinationRate { get; set; }
        public int HallucinationCount { get; set; }
        public double ToolCallRate { get; set; }
        public double LedgerRate { get; set; }
        public double ReactionRate { get; set; }
        public bool? HardGateToolCall => ToolCallRate >= 0.95;
        public bool? HardGateLedger => LedgerRate >= 0.95;
        public bool? HardGateReaction => ReactionRate >= 1.0;
    }

    /// <summary>
    ///     防御式解析 <c>_final_report.json</c>（体验范式格式）。文件缺失或
    ///     格式错误时返回空数据并设置 <see cref="ScoreReportData.ErrorMessage" />，不抛异常。
    ///     读取每个测试的 <c>experience_issues</c> 列表与
    ///     <c>experience_severity</c>，将每条问题展开为一条
    ///     <see cref="ExperienceIssueEntry" />（继承所属测试的严重度）。
    /// </summary>
    private static ScoreReportData ParseFinalReport(string finalReportPath)
    {
        if (string.IsNullOrEmpty(finalReportPath) || !File.Exists(finalReportPath))
        {
            return new ScoreReportData
            {
                ErrorMessage = $"Final report not found: {finalReportPath ?? "<null>"}"
            };
        }

        string json;
        try
        {
            json = File.ReadAllText(finalReportPath);
        }
        catch (IOException ex)
        {
            return new ScoreReportData { ErrorMessage = $"I/O error: {ex.Message}" };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ScoreReportData { ErrorMessage = $"Access error: {ex.Message}" };
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ScoreReportData { ErrorMessage = $"Malformed JSON: {ex.Message}" };
        }

        using (doc)
        {
            var root = doc.RootElement;
            var runTimestamp = TryGetPropertyString(root, "run_timestamp");
            var totalTests = TryGetPropertyInt(root, "total_tests");
            var totalCodePassed = TryGetPropertyInt(root, "total_code_passed");
            var totalCodeFailed = TryGetPropertyInt(root, "total_code_failed");
            var totalExperienceIssues = TryGetPropertyInt(root, "total_experience_issues");

            var issues = new List<ExperienceIssueEntry>();

            if (root.TryGetProperty("tests", out var testsEl) && testsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var test in testsEl.EnumerateArray())
                {
                    var name = TryGetPropertyString(test, "test_name");
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    var severity = TryGetPropertyStringOrNull(test, "experience_severity");

                    if (test.TryGetProperty("experience_issues", out var issuesEl)
                        && issuesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var issue in issuesEl.EnumerateArray())
                        {
                            if (issue.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            issues.Add(new ExperienceIssueEntry
                            {
                                Test = name,
                                Severity = severity,
                                Description = issue.GetString() ?? ""
                            });
                        }
                    }
                }
            }

            return new ScoreReportData
            {
                RunTimestamp = runTimestamp,
                TotalTests = totalTests,
                TotalCodePassed = totalCodePassed,
                TotalCodeFailed = totalCodeFailed,
                TotalExperienceIssues = totalExperienceIssues,
                Issues = issues
            };
        }
    }

    private static string TryGetPropertyString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? "";
        }

        return "";
    }

    /// <summary>
    ///     读取一个可为 null 的字符串属性。属性缺失、值为 null 或非字符串时
    ///     返回 null（用于 <c>experience_severity</c> 这类可空字段）。
    /// </summary>
    private static string? TryGetPropertyStringOrNull(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static int TryGetPropertyInt(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.TryGetInt32(out var value) ? value : 0;
        }

        return 0;
    }

    /// <summary>
    ///     内部数据载体：从 final_report.json 解析出的汇总数据。
    ///     <see cref="ErrorMessage" /> 非 null 表示输入缺失或格式错误。
    /// </summary>
    private sealed class ScoreReportData
    {
        public string RunTimestamp { get; set; } = "";
        public int TotalTests { get; set; }
        public int TotalCodePassed { get; set; }
        public int TotalCodeFailed { get; set; }
        public int TotalExperienceIssues { get; set; }
        public List<ExperienceIssueEntry> Issues { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    ///     单条玩家体验问题：归属测试名、严重度（继承自测试的
    ///     <c>experience_severity</c>）、问题描述文本。
    /// </summary>
    private sealed class ExperienceIssueEntry
    {
        public string Test { get; set; } = "";
        public string? Severity { get; set; }
        public string Description { get; set; } = "";
    }
}