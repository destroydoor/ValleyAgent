using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace ValleyAgent.i18n;

/// <summary>
///     Game-agnostic translation provider that loads localized strings from embedded JSON resources.
///     Design:
///     - Translations are loaded from embedded resources (default.json, zh.json) packed into the assembly.
///     - Thread-safe using
///     <see cref="ConcurrentDictionary{TKey,TValue}"/ u003e.
///         - Falls back to the default language ( English) when a key is missing in the current language.
///         - Returns the key itself as a last resort to avoid null-reference issues at runtime.
///         - Uses
///     <see cref="System.Text.Json"/ u003e for . NET 6 compatibility.
///         Usage:
///     <code>
/// var i18n = new TranslationProvider("zh");
/// string label = i18n.GetString("UI_State");
/// string formatted = i18n.GetString("ERR_NpcNotAllocated", npcName);
/// </code>
/// </summary>
public class TranslationProvider : ITranslationProvider
{
    private const string DefaultLanguage = "default";
    private const string ResourcePrefix = "ValleyAgent.i18n";

    private readonly ConcurrentDictionary<string, string> _defaultTranslations;
    private ConcurrentDictionary<string, string> _currentTranslations;

    /// <summary>
    ///     Creates a new
    ///     <see cref="TranslationProvider"/ u003e and loads the specified language.
    ///         Falls back to the default language if the requested language resource is not found.
    /// </summary>
    /// <param name="language">Language code, e.g. "default" or "zh".</param>
    public TranslationProvider(string language = DefaultLanguage)
    {
        _defaultTranslations = LoadTranslations(DefaultLanguage);
        CurrentLanguage = DefaultLanguage;
        _currentTranslations = _defaultTranslations;

        if (!string.Equals(language, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            _ = TrySetLanguage(language);
        }
    }

    /// <summary>
    ///     Gets the currently active language code (e.g., "default" or "zh").
    ///     Thread-safe: uses volatile read.
    /// </summary>
    public string CurrentLanguage { get; private set; }

    /// <inheritdoc />
    public string GetString(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        // Try current language first
        if (_currentTranslations.TryGetValue(key, out var value))
        {
            return value;
        }

        // Fall back to default language
        if (_defaultTranslations.TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        // Last resort: return the key itself
        return key;
    }

    /// <inheritdoc />
    public string GetString(string key, params object[] args)
    {
        var template = GetString(key);
        if (args == null || args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // If formatting fails, return the raw template
            return template;
        }
    }

    /// <summary>
    ///     Attempts to switch to the specified language.
    ///     Returns false if the language resource is not found; the provider remains on the current language.
    ///     Thread-safe.
    /// </summary>
    /// <param name="language">Language code, e.g. "zh".</param>
    /// <returns>True if the language was loaded successfully.</returns>
    public bool TrySetLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        var normalized = language.ToLowerInvariant();
        if (string.Equals(normalized, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalized, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            CurrentLanguage = DefaultLanguage;
            _currentTranslations = _defaultTranslations;
            return true;
        }

        var loaded = LoadTranslations(normalized);
        if (loaded == null || loaded.IsEmpty)
        {
            // Resource not found or empty 鈥?stay on current language
            return false;
        }

        CurrentLanguage = normalized;
        _currentTranslations = loaded;
        return true;
    }

    // --------------------------------------------------------------------
    // Private helpers
    // --------------------------------------------------------------------

    /// <summary>
    ///     Loads a translation dictionary from an embedded JSON resource.
    ///     Returns an empty dictionary if the resource is not found or parsing fails.
    /// </summary>
    private static ConcurrentDictionary<string, string> LoadTranslations(string language)
    {
        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{ResourcePrefix}.{language}.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return result;
        }

        try
        {
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (dict != null)
            {
                foreach (var kvp in dict)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (JsonException)
        {
            // Swallow parse errors and return empty dictionary
        }

        return result;
    }
}