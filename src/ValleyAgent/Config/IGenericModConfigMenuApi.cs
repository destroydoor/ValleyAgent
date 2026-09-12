using System;
using StardewModdingAPI;

namespace ValleyAgent.Config;

/// <summary>
///     Local mirror of the Generic Mod Config Menu (GMCM) API interface.
///     Signatures MUST exactly match the installed GMCM version so that
///     SMAPI's mod-provided API proxy can map them. Even minor differences
///     (e.g. optional vs required parameter) cause GetApi to silently return null.
///     Verified against GMCM 1.16.0 via reflection on 2026-08-04.
/// </summary>
public interface IGenericModConfigMenuApi
{
    public void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

    public void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);

    public void AddParagraph(IManifest mod, Func<string> text);

    public void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name,
        Func<string>? tooltip = null, string? fieldId = null);

    public void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name,
        Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null,
        Func<int, string>? formatValue = null, string? fieldId = null);

    public void AddNumberOption(IManifest mod, Func<float> getValue, Action<float> setValue, Func<string> name,
        Func<string>? tooltip = null, float? min = null, float? max = null, float? interval = null,
        Func<float, string>? formatValue = null, string? fieldId = null);

    public void AddTextOption(IManifest mod, Func<string> getValue, Action<string> setValue, Func<string> name,
        Func<string>? tooltip = null, string[]? allowedValues = null, Func<string, string>? formatAllowedValue = null,
        string? fieldId = null);

    public void AddPage(IManifest mod, string pageId, Func<string>? pageTitle = null);

    public void AddPageLink(IManifest mod, string pageId, Func<string> text, Func<string>? tooltip = null);
}