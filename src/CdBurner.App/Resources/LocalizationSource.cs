using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace CdBurner.App.Resources;

/// <summary>
/// Source of the text used by XAML (<c>{loc:Loc Key}</c>). Bindings point at the indexer; when the language changes
/// the indexer's change notification makes every bound label, button and tooltip re-read its text at once.
/// The text lives in <c>UiText.resx</c> and its per-language siblings.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private static readonly ResourceManager Manager =
        new("CdBurner.App.Resources.UiText", typeof(LocalizationSource).Assembly);

    /// <summary>The one instance every XAML binding uses.</summary>
    public static LocalizationSource Instance { get; } = new();

    private LocalizationSource()
    {
        LocalizationManager.LanguageChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Text for a key in the current UI language. A missing key shows as <c>!Key!</c> so the gap is visible.</summary>
    public string this[string key] => Lookup(key, CultureInfo.CurrentUICulture) ?? $"!{key}!";

    /// <summary>Text for a key; throws when the key does not exist, so code never shows a blank.</summary>
    public static string Get(string key) =>
        Lookup(key, CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException($"Missing UI text: {key}");

    /// <summary>Text for a key in one specific culture, or null when the key is missing (falls back to English).</summary>
    public static string? Lookup(string key, CultureInfo culture) =>
        // The .resx may be checked out with CRLF line endings; text is written with LF.
        LocalizationManager.GetText(Manager, key, culture)?.Replace("\r\n", "\n");

    /// <summary>The resource manager, for tests that enumerate the keys of each language.</summary>
    public static ResourceManager ResourceManager => Manager;
}
