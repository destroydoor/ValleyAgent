namespace ValleyAgent.i18n;

/// <summary>
///     Provides localized string lookups for ValleyAgent.
///     Design:
///     - Game-agnostic: no SMAPI types.
///     - Thread-safe: implementations must be safe for concurrent access.
///     - Supports parameterized strings via <see cref="GetString(string, object[])" />.
///     - Falls back to the default language (English) when a key is missing in the current language.
/// </summary>
public interface ITranslationProvider
{
    /// <summary>
    ///     Gets the currently active language code (e.g., "default" or "zh").
    /// </summary>
    public string CurrentLanguage { get; }

    /// <summary>
    ///     Retrieves a localized string by key.
    ///     Falls back to the default language if the key is not found in the current language.
    ///     Returns the key itself if not found in either language.
    /// </summary>
    /// <param name="key">The translation key.</param>
    /// <returns>The localized string, or the key if not found.</returns>
    public string GetString(string key);

    /// <summary>
    ///     Retrieves a localized string by key and formats it with the provided arguments.
    ///     Falls back to the default language if the key is not found in the current language.
    /// </summary>
    /// <param name="key">The translation key.</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>The formatted localized string, or the key if not found.</returns>
    public string GetString(string key, params object[] args);
}