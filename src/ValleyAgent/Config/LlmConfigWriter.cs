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
    ///     序列化 9 组 provider 字段（3 角色 × 主备）+ 角色标记 + global 段。
    /// </summary>
    /// <param name="config">Mod 配置实例。</param>
    /// <param name="modDir">mod 根目录（IModHelper.DirectoryPath）。</param>
    /// <param name="monitor">可选的 SMAPI 日志器，传入时记录写入结果。</param>
    /// <returns>写入的文件路径；失败返回 null。</returns>
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
                    director = BuildRole(
                        config.DirectorPrimaryProvider, config.DirectorPrimaryApiKey,
                        config.DirectorPrimaryModel, config.DirectorPrimaryBaseUrl,
                        config.DirectorFallbackProvider, config.DirectorFallbackApiKey,
                        config.DirectorFallbackModel, config.DirectorFallbackBaseUrl),
                    protagonist = BuildRole(
                        config.ProtagonistPrimaryProvider, config.ProtagonistPrimaryApiKey,
                        config.ProtagonistPrimaryModel, config.ProtagonistPrimaryBaseUrl,
                        config.ProtagonistFallbackProvider, config.ProtagonistFallbackApiKey,
                        config.ProtagonistFallbackModel, config.ProtagonistFallbackBaseUrl),
                    npc = BuildRole(
                        config.NpcPrimaryProvider, config.NpcPrimaryApiKey,
                        config.NpcPrimaryModel, config.NpcPrimaryBaseUrl,
                        config.NpcFallbackProvider, config.NpcFallbackApiKey,
                        config.NpcFallbackModel, config.NpcFallbackBaseUrl)
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
            monitor?.Log($"Failed to write LLM runtime config: {ex.Message}", LogLevel.Error);
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
    ///     构造单个角色的匿名配置对象（primary + 可选 fallback）。
    ///     fallback 在 fProvider 或 fModel 为空时序列化为 null。
    /// </summary>
    private static object BuildRole(
        string pProvider, string pKey, string pModel, string pUrl,
        string fProvider, string fKey, string fModel, string fUrl)
    {
        var primary = new { provider = pProvider, apiKey = pKey, model = pModel, baseUrl = pUrl };
        object? fallback = null;
        if (!string.IsNullOrWhiteSpace(fProvider) && !string.IsNullOrWhiteSpace(fModel))
        {
            fallback = new { provider = fProvider, apiKey = fKey, model = fModel, baseUrl = fUrl };
        }

        return new { primary, fallback };
    }
}