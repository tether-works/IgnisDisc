using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;
using CdBurner.App.Resources;
using CdBurner.App.ViewModels;
using CdBurner.Core.Persistence;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>Tests that change the process-wide UI language must not overlap with any other test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiLanguageCollection
{
    public const string Name = "UiLanguage";
}

/// <summary>
/// Checks the UI language support: every language has every key, the numbered placeholders match the English text
/// (a missing or reordered <c>{0}</c> would crash or garble a message), the pseudo-locale covers everything, and the
/// language choice is saved and applied.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LocalizationTests : IDisposable
{
    private static readonly string[] Locales = ["de", "fr", "es", "ja", "zh-Hans"];

    private static readonly Regex Placeholder = new(@"\{(\d+)(?:,[^}:]*)?(?::[^}]*)?\}", RegexOptions.Compiled);

    private static readonly (string Name, ResourceManager Manager)[] ResourceSets =
    [
        ("UiText", LocalizationSource.ResourceManager),
        ("Strings", new ResourceManager("CdBurner.App.Resources.Strings", typeof(Strings).Assembly))
    ];

    public void Dispose() => LocalizationManager.Apply(LocalizationManager.SystemDefault);

    private static Dictionary<string, string> Load(ResourceManager manager, CultureInfo culture)
    {
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.True(set is not null, $"No resource file for '{culture.Name}'");
        return set!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }

    private static List<int> PlaceholderIndexes(string text) =>
        Placeholder.Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Order().ToList();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CdBurner.sln")))
        {
            dir = dir.Parent;
        }
        Assert.True(dir is not null, "Repository root not found");
        return dir!.FullName;
    }

    [Fact]
    public void Every_locale_has_exactly_the_keys_of_the_English_text_with_no_empty_values()
    {
        foreach (var (name, manager) in ResourceSets)
        {
            var english = Load(manager, CultureInfo.InvariantCulture);
            Assert.NotEmpty(english);
            foreach (var locale in Locales)
            {
                var translated = Load(manager, CultureInfo.GetCultureInfo(locale));
                Assert.True(english.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(translated.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                    $"{name}.{locale}: keys differ from English. Missing: [{string.Join(", ", english.Keys.Except(translated.Keys))}] Extra: [{string.Join(", ", translated.Keys.Except(english.Keys))}]");
                foreach (var (key, value) in translated)
                {
                    Assert.False(string.IsNullOrWhiteSpace(value), $"{name}.{locale}.{key} is empty");
                }
            }
        }
    }

    [Fact]
    public void Every_locale_keeps_the_numbered_placeholders_of_the_English_text_and_formats_cleanly()
    {
        foreach (var (name, manager) in ResourceSets)
        {
            var english = Load(manager, CultureInfo.InvariantCulture);
            foreach (var locale in Locales)
            {
                var translated = Load(manager, CultureInfo.GetCultureInfo(locale));
                foreach (var (key, englishText) in english)
                {
                    var expected = PlaceholderIndexes(englishText);
                    var actual = PlaceholderIndexes(translated[key]);
                    Assert.True(expected.SequenceEqual(actual), $"{name}.{locale}.{key}: placeholders {{{string.Join(",", actual)}}} differ from English {{{string.Join(",", expected)}}}");

                    if (expected.Count > 0)
                    {
                        var args = Enumerable.Range(0, expected.Max() + 1).Select(i => (object)$"<{i}>").ToArray();
                        var formatted = string.Format(CultureInfo.InvariantCulture, translated[key], args);
                        foreach (var index in expected)
                        {
                            Assert.Contains($"<{index}>", formatted);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void Every_key_used_in_xaml_exists_and_the_converted_views_have_no_literal_text_left()
    {
        var english = Load(LocalizationSource.ResourceManager, CultureInfo.InvariantCulture);
        var root = Path.Combine(RepoRoot(), "src", "CdBurner.App");
        var used = new Regex(@"\{loc:Loc\s+(\w+)\s*\}");
        var literal = new Regex(@"(?<![\w.:])((?:AutomationProperties\.)?(?:Content|Text|Header|ToolTip|HelpText))=""([^{""][^""]*)""");
        var convertedViews = new[] { "MainWindow.xaml", Path.Combine("Views", "SettingsView.xaml"), Path.Combine("Views", "BurnProgressView.xaml"), Path.Combine("Views", "VirtualDriveView.xaml") };
        // Product and media names, a version-stamped title, a URL line and a separator that are the same in every language.
        var sameEverywhere = new HashSet<string> { "IgnisDisc Optical Studio v1.18", " (Virtual)", "CD-R", "CD-RW", "DVD-R", "Source Code &amp; Issues: https://github.com/tether-works/IgnisDisc" };

        var files = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var keysFound = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in used.Matches(text))
            {
                keysFound++;
                Assert.True(english.ContainsKey(m.Groups[1].Value), $"{Path.GetFileName(file)} uses unknown key {m.Groups[1].Value}");
            }
        }
        Assert.True(keysFound >= 100, "Expected the converted views to use many keys");

        foreach (var view in convertedViews)
        {
            var text = File.ReadAllText(Path.Combine(root, view));
            foreach (Match m in literal.Matches(text))
            {
                var value = m.Groups[2].Value;
                if (sameEverywhere.Contains(value) || !Regex.IsMatch(value, "[A-Za-z]{2}")) continue;
                Assert.Fail($"{view} still has literal text: {m.Value}");
            }
        }
    }

    [Fact]
    public void Pseudo_locale_covers_every_key_keeps_placeholders_and_lengthens_the_text()
    {
        foreach (var (name, manager) in ResourceSets)
        {
            var english = Load(manager, CultureInfo.InvariantCulture);
            var pseudoCulture = CultureInfo.GetCultureInfo(LocalizationManager.PseudoLocale);
            foreach (var (key, englishText) in english)
            {
                var pseudo = LocalizationManager.GetText(manager, key, pseudoCulture)!;
                Assert.True(PlaceholderIndexes(englishText).SequenceEqual(PlaceholderIndexes(pseudo)), $"{name}.{key}: pseudo text lost a placeholder");
                Assert.True(pseudo.Length >= englishText.Length * 1.3, $"{name}.{key}: pseudo text is not at least 30 % longer");
                Assert.StartsWith("[", pseudo);
                Assert.EndsWith("]", pseudo);

                // Whatever is not a placeholder or an environment token must have been changed to accented letters.
                var rest = Regex.Replace(pseudo, @"\{[^}]*\}|%[A-Za-z0-9_]+%", string.Empty);
                Assert.False(Regex.IsMatch(rest, "[A-Za-z]"), $"{name}.{key}: plain letters left in pseudo text: {pseudo}");
                if (PlaceholderIndexes(englishText).Count > 0)
                {
                    var args = Enumerable.Range(0, 5).Select(i => (object)i).ToArray();
                    _ = string.Format(CultureInfo.InvariantCulture, pseudo, args);
                }
            }
        }
    }

    [Fact]
    public void Pseudo_locale_converts_a_known_string_as_specified()
    {
        // Letters become accented look-alikes, the text grows by 40 % (rounded up) and is wrapped in brackets.
        Assert.Equal("[Åƀç ~~]", PseudoLocalizer.Convert("Abc"));
        // Numbered placeholders and %ENVIRONMENT% tokens stay exactly as written so formatting and paths still work.
        Assert.Equal("[Ĝö {0} ~~~]", PseudoLocalizer.Convert("Go {0}"));
        Assert.Equal("[Ĝö {1:N0} ~~~~]", PseudoLocalizer.Convert("Go {1:N0}"));
        Assert.Equal("[%TEMP% ~~~]", PseudoLocalizer.Convert("%TEMP%"));
        // A brace that is not a placeholder is ordinary text.
        Assert.Equal("[{ö} ~~]", PseudoLocalizer.Convert("{o}"));    }

    [Fact]
    public void Real_translations_stay_within_a_sane_length_of_the_English_text()
    {
        foreach (var (name, manager) in ResourceSets)
        {
            var english = Load(manager, CultureInfo.InvariantCulture);
            foreach (var locale in Locales)
            {
                foreach (var (key, text) in Load(manager, CultureInfo.GetCultureInfo(locale)))
                {
                    var limit = english[key].Length * 2.5 + 8;
                    Assert.True(text.Length <= limit, $"{name}.{locale}.{key}: {text.Length} characters against English {english[key].Length}");
                }
            }
        }
    }

    [Theory]
    [InlineData("de", "UiShellSettings", "Einstellungen")]
    [InlineData("fr", "UiShellSettings", "Paramètres")]
    [InlineData("es", "UiShellSettings", "Ajustes")]
    [InlineData("ja", "UiShellSettings", "設定")]
    [InlineData("zh-Hans", "UiShellSettings", "设置")]
    [InlineData("en", "UiShellSettings", "Settings")]
    public void Applying_a_language_changes_the_text_that_xaml_and_code_read(string language, string key, string expected)
    {
        LocalizationManager.Apply(language);
        Assert.Equal(expected, LocalizationSource.Instance[key]);
        Assert.Equal(expected, LocalizationSource.Get(key));
        Assert.Equal(language, CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public void Dialog_text_follows_the_chosen_language_and_still_formats()
    {
        LocalizationManager.Apply("de");
        Assert.Equal("Ein unerwarteter Fehler ist aufgetreten:\n\nboom", Strings.Format(nameof(Strings.DialogUnexpectedErrorBody), "boom"));
        LocalizationManager.Apply("en");
        Assert.Equal("An unexpected error occurred:\n\nboom", Strings.Format(nameof(Strings.DialogUnexpectedErrorBody), "boom"));
    }

    [Fact]
    public void Changing_the_language_tells_bound_text_to_refresh()
    {
        var raised = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName);
        LocalizationSource.Instance.PropertyChanged += handler;
        try
        {
            LocalizationManager.Apply("fr");
        }
        finally
        {
            LocalizationSource.Instance.PropertyChanged -= handler;
        }
        Assert.Contains("Item[]", raised);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("DE", "de")]
    [InlineData("zh-hans", "zh-Hans")]
    [InlineData("qps-ploc", "qps-ploc")]
    [InlineData("tlh", "")]
    [InlineData("not a language!", "")]
    public void Stored_language_values_are_normalised_to_a_known_value(string? stored, string expected)
    {
        Assert.Equal(expected, LocalizationManager.Normalize(stored));
    }

    [Fact]
    public void Settings_offer_the_system_default_and_the_six_languages_in_their_own_names()
    {
        var vm = new SettingsViewModel();
        Assert.Equal(new[] { "", "en", "de", "fr", "es", "ja", "zh-Hans" }, vm.AvailableLanguages.Select(o => o.Code));
        Assert.Equal(new[] { "English", "Deutsch", "Français", "Español", "日本語", "简体中文" }, vm.AvailableLanguages.Skip(1).Select(o => o.DisplayName));
        Assert.Equal("System Default", vm.AvailableLanguages[0].DisplayName);

        vm.UiLanguage = "de";
        Assert.Equal("Systemstandard", vm.AvailableLanguages[0].DisplayName);
    }

    [Fact]
    public void SettingsViewModel_saves_the_language_applies_it_and_reset_goes_back_to_the_system_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ignisdisc_lang_{Guid.NewGuid():N}.json");
        try
        {
            var service = new AppSettingsService(path);
            var vm = new SettingsViewModel(service);
            Assert.Equal("", vm.UiLanguage);
            vm.AutoSave = true;

            vm.UiLanguage = "ja";
            Assert.Equal("ja", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("設定", LocalizationSource.Instance["UiShellSettings"]);

            var reloaded = new AppSettingsService(path);
            reloaded.Load();
            Assert.Equal("ja", reloaded.CurrentSettings.UiLanguage);

            // A value that is not a known language is stored as the system default.
            vm.UiLanguage = "xx-bogus";
            Assert.Equal("", vm.UiLanguage);

            vm.UiLanguage = "fr";
            var next = new AppSettingsService(path);
            next.Load();
            var secondVm = new SettingsViewModel(next);
            Assert.Equal("fr", secondVm.UiLanguage);
            Assert.Equal("Paramètres", LocalizationSource.Instance["UiShellSettings"]);

            vm.ResetSettings();
            Assert.Equal("", vm.UiLanguage);
            Assert.Equal(LocalizationManager.ResolveCulture("").Name, CultureInfo.CurrentUICulture.Name);
            var afterReset = new AppSettingsService(path);
            afterReset.Load();
            Assert.Equal("", afterReset.CurrentSettings.UiLanguage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_pseudo_locale_can_be_selected_and_makes_every_label_longer()
    {
        var english = Load(LocalizationSource.ResourceManager, CultureInfo.InvariantCulture);
        var vm = new SettingsViewModel { UiLanguage = LocalizationManager.PseudoLocale };
        Assert.Equal("qps-ploc", vm.UiLanguage);
        foreach (var (key, englishText) in english)
        {
            var shown = LocalizationSource.Instance[key];
            Assert.True(shown.Length >= englishText.Length * 1.3, $"{key} did not grow in the pseudo-locale");
        }
    }

    [Fact]
    public void Settings_file_without_a_language_reads_as_the_system_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ignisdisc_lang_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"SettingsVersion\": 3, \"ThemeMode\": 0 }");
            var service = new AppSettingsService(path);
            service.Load();
            Assert.Equal("", service.CurrentSettings.UiLanguage);
            Assert.Equal("", service.CurrentSettings.Clone().UiLanguage);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
