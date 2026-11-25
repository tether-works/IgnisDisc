using System.Globalization;

namespace CdBurner.App.Resources;

/// <summary>
/// Owns the language of the app's own text. The chosen language is a culture name stored in the settings file
/// (<c>""</c> follows Windows). Applying it sets the UI culture of the whole process, so every later resource lookup,
/// in XAML or in code, picks the matching satellite resource file.
/// </summary>
public static class LocalizationManager
{
    /// <summary>Settings value meaning "follow the Windows display language".</summary>
    public const string SystemDefault = "";

    /// <summary>
    /// Pseudo-locale used by tests: every letter is accented and every string is lengthened, so unextracted text and
    /// layouts that cannot grow are easy to spot. Not offered in the language selector.
    /// </summary>
    public const string PseudoLocale = "qps-ploc";

    /// <summary>Languages the app ships text for, in the order the selector lists them.</summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["en", "de", "fr", "es", "ja", "zh-Hans"];

    /// <summary>The Windows display language, read once before the app overrides the UI culture.</summary>
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    private static string _currentLanguage = SystemDefault;

    /// <summary>Raised after <see cref="Apply"/> changed the UI culture, so bound text can refresh.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>The setting that is applied now (<c>""</c> for the system default).</summary>
    public static string CurrentLanguage => _currentLanguage;

    /// <summary>
    /// Turns any stored value into one the app knows: a supported language code (matched ignoring case), the
    /// pseudo-locale, or <see cref="SystemDefault"/> for empty, unknown or malformed values.
    /// </summary>
    public static string Normalize(string? code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return SystemDefault;
        foreach (var supported in SupportedLanguages)
        {
            if (string.Equals(supported, trimmed, StringComparison.OrdinalIgnoreCase)) return supported;
        }
        return string.Equals(PseudoLocale, trimmed, StringComparison.OrdinalIgnoreCase) ? PseudoLocale : SystemDefault;
    }

    /// <summary>The culture a stored value stands for.</summary>
    public static CultureInfo ResolveCulture(string? code)
    {
        var normalized = Normalize(code);
        return normalized.Length == 0 ? SystemCulture : CultureInfo.GetCultureInfo(normalized);
    }

    /// <summary>Applies a stored language value to the whole process and tells bound text to refresh.</summary>
    public static void Apply(string? code)
    {
        var normalized = Normalize(code);
        var culture = ResolveCulture(normalized);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        _currentLanguage = normalized;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Looks a key up in one resource set for a culture. The pseudo-locale has no file: it is built from the English
    /// text on the fly, so it always covers every key. Returns null when the key does not exist.
    /// </summary>
    public static string? GetText(System.Resources.ResourceManager manager, string key, CultureInfo culture)
    {
        if (string.Equals(culture.Name, PseudoLocale, StringComparison.OrdinalIgnoreCase))
        {
            var english = manager.GetString(key, CultureInfo.InvariantCulture);
            return english is null ? null : PseudoLocalizer.Convert(english);
        }
        return manager.GetString(key, culture);
    }
}