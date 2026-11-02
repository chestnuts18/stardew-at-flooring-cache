using System;
using StardewModdingAPI;

namespace ATFlooringCache;

/// <summary>
/// Minimal Generic Mod Config Menu API surface used by this mod.
/// The actual API is provided by the GMCM mod at runtime.
/// </summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

    void AddBoolOption(
        IManifest mod,
        Func<bool> getValue,
        Action<bool> setValue,
        Func<string> getName,
        Func<string>? getTooltip = null,
        string? fieldId = null
    );

    void AddNumberOption(
        IManifest mod,
        Func<int> getValue,
        Action<int> setValue,
        Func<string> getName,
        Func<string>? getTooltip = null,
        int? min = null,
        int? max = null,
        int? interval = null,
        string? fieldId = null
    );
}
