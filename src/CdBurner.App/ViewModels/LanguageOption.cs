using CdBurner.App.Mvvm;
using CdBurner.App.Resources;

namespace CdBurner.App.ViewModels;

/// <summary>One entry of the UI language selector in the settings.</summary>
public sealed class LanguageOption : ObservableObject
{
    private static readonly Dictionary<string, string> NativeNames = new(StringComparer.Ordinal)
    {
        ["en"] = "English",
        ["de"] = "Deutsch",
        ["fr"] = "Français",
        ["es"] = "Español",
        ["ja"] = "日本語",
        ["zh-Hans"] = "简体中文"
    };

    public LanguageOption(string code)
    {
        Code = code;
    }

    /// <summary>Value stored in the settings: a culture name, or <c>""</c> for the system default.</summary>
    public string Code { get; }

    /// <summary>
    /// What the selector shows. Languages are named in their own language so a user who cannot read the current one
    /// can still find theirs; only "System Default" is translated.
    /// </summary>
    public string DisplayName =>
        NativeNames.TryGetValue(Code, out var native) ? native : LocalizationSource.Get("UiLanguageSystemDefault");

    /// <summary>Re-reads <see cref="DisplayName"/> after the UI language changed.</summary>
    public void Refresh() => OnPropertyChanged(nameof(DisplayName));
}
