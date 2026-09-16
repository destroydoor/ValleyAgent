using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StardewModdingAPI;

namespace ValleyAgent.Config;

/// <summary>
///     把 ModConfig 的多 Provider 字段序列化成 runtime JSON，
///     供 TS 端 valley-ai-server.exe --llm-config 读取。
///     文件路径：{modDir}/llm-config.runtime.json。
/// </summary>
public static class LlmConfigWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>
    ///     计算 runtime JSON 文件的完整路径。
    /// </summary>
    /// <param name="modDir">mod 根目录（IModHelper.DirectoryPath）。</param>
    /// <returns>runtime JSON 文件路径。</returns>
    public static string GetRuntimeConfigPath(string modDir)
        => Path.Combine(modDir, "llm-config.runtime.json");

    /// <summary>
    ///     写 runtime JSON。仅在 <see cref="ModConfig.MultiProviderEnabled" />=true 时调用。
    ///     序列化 3 角色 ×（主 + 备用链）+ 角色标记 + global 段。
    ///     备用链 = 第一备 + 第二备，任何一项四字段不齐（含 apiKey 空）即整条跳过——
    ///     TS 端校验器对空 apiKey 直接抛错拒绝启动，宁缺勿坏（跳过项留 Info 日志）。
    /// </summary>
    public static string? WriteRuntimeConfig(ModConfig config, string modDir, IMonitor? monitor = null)
    {
        try
        {
            var protagonistNpcs = ParseProtagonistList(config.ProtagonistNpcs);

            var runtimeConfig = new
            {
                version = 1,
                roles = new
                {
                    director = BuildRole(monitor,
                        (config.DirectorPrimaryProvider, config.DirectorPrimaryApiKey, config.DirectorPrimaryModel, config.DirectorPrimaryBaseUrl),
                        FallbackEntries(monitor, "director",
                            (config.DirectorFallbackProvider, config.DirectorFallbackApiKey, config.DirectorFallbackModel, config.DirectorFallbackBaseUrl),
                            (config.DirectorFallback2Provider, config.DirectorFallback2ApiKey, config.DirectorFallback2Model, config.DirectorFallback2BaseUrl))),
                    protagonist = BuildRole(monitor,
                        (config.ProtagonistPrimaryProvider, config.ProtagonistPrimaryApiKey, config.ProtagonistPrimaryModel, config.ProtagonistPrimaryBaseUrl),
                        FallbackEntries(monitor, "protagonist",
                            (config.ProtagonistFallbackProvider, config.ProtagonistFallbackApiKey, config.ProtagonistFallbackModel, config.ProtagonistFallbackBaseUrl),
                            (config.ProtagonistFallback2Provider, config.ProtagonistFallback2ApiKey, config.ProtagonistFallback2Model, config.ProtagonistFallback2BaseUrl))),
                    npc = BuildRole(monitor,
                        (config.NpcPrimaryProvider, config.NpcPrimaryApiKey, config.NpcPrimaryModel, config.NpcPrimaryBaseUrl),
                        FallbackEntries(monitor, "npc",
                            (config.NpcFallbackProvider, config.NpcFallbackApiKey, config.NpcFallbackModel, config.NpcFallbackBaseUrl),
                            (config.NpcFallback2Provider, config.NpcFallback2ApiKey, config.NpcFallback2Model, config.NpcFallback2BaseUrl)))
                },
                protagonistNpcs,
                enableProtagonistMapping = config.EnableProtagonistMapping,
                global = new
                {
                    timeoutMs = config.LLMTimeoutSeconds * 1000,
                    maxRetries = config.MaxRetries,
                    temperature = config.Temperature,
                    maxTokens = 800
                }
            };

            var path = GetRuntimeConfigPath(modDir);
            var json = JsonSerializer.Serialize(runtimeConfig, s_jsonOptions);
            File.WriteAllText(path, json);
            monitor?.Log($"Wrote LLM runtime config to {path}", LogLevel.Debug);
            return path;
        }
        catch (Exception ex)
        {
            monitor?.Log($"Failed to write LLM runtime config: {ex}", LogLevel.Error);
            return null;
        }
    }

    /// <summary>
    ///     解析逗号分隔的主角 NPC 列表：trim + 空值过滤 + OrdinalIgnoreCase 去重。
    ///     保留原始大小写；仅用于去重比较时忽略大小写。
    /// </summary>
    /// <param name="raw">原始逗号分隔字符串（可能为 null 或空）。</param>
    /// <returns>去重后的主角 NPC 名称列表（保留首次出现的原始大小写）。</returns>
    public static List<string> ParseProtagonistList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(name);
        }

        return new List<string>(set);
    }

    /// <summary>
    ///     收集一个角色的备用链条目：任何一项四字段不齐（provider/apiKey/model/baseUrl 任一为空）
    ///    即整条跳过并留 Info 日志（§3.6：过滤/丢弃必须 log 被丢弃项和原因）。
    /// </summary>
    private static (string provider, string apiKey, string model, string baseUrl)[] FallbackEntries(
        IMonitor? monitor, string role,
        (string provider, string apiKey, string model, string baseUrl) first,
        (string provider, string apiKey, string model, string baseUrl) second)
    {
        var list = new List<(string, string, string, string)>();
        foreach (var (entry, label) in new[] { (first, "fallback1"), (second, "fallback2") })
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(entry.provider)) missing.Add(nameof(entry.provider));
            if (string.IsNullOrWhiteSpace(entry.apiKey)) missing.Add(nameof(entry.apiKey));
            if (string.IsNullOrWhiteSpace(entry.model)) missing.Add(nameof(entry.model));
            if (string.IsNullOrWhiteSpace(entry.baseUrl)) missing.Add(nameof(entry.baseUrl));
            if (missing.Count > 0)
            {
                monitor?.Log(
                    $"[llm-config] {role}.{label} ({entry.provider}/{entry.model}) skipped: empty {string.Join(",", missing)}",
                    LogLevel.Info);
                continue;
            }

            list.Add(entry);
        }

        return list.ToArray();
    }

    /// <summary>
    ///     构造单个角色的匿名配置对象：primary + fallback（旧字段=链首，旧 exe 兼容）+ fallbacks（完整备用链）。
    /// </summary>
    private static object BuildRole(
        IMonitor? monitor,
        (string provider, string apiKey, string model, string baseUrl) primary,
        (string provider, string apiKey, string model, string baseUrl)[] fallbacks)
    {
        var primaryObj = new { provider = primary.provider, apiKey = primary.apiKey, model = primary.model, baseUrl = primary.baseUrl };
        var entries = new List<object>();
        foreach (var fb in fallbacks)
        {
            entries.Add(new { provider = fb.provider, apiKey = fb.apiKey, model = fb.model, baseUrl = fb.baseUrl });
        }

        object? legacyFallback = entries.Count > 0 ? entries[0] : null;
        return new { primary = primaryObj, fallback = legacyFallback, fallbacks = entries };
    }
}