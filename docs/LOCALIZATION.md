# Localization guide

IgnisDisc shows its own text in English, German, French, Spanish, Japanese and Simplified Chinese. The language is
chosen in **Settings > Appearance & Diagnostics > Language** (`System Default` follows the Windows display language),
takes effect at once and is stored as `UiLanguage` in the settings file (`""`, `en`, `de`, `fr`, `es`, `ja`, `zh-Hans`).

## Where the text lives

| Resource | Used for | How it is read |
| --- | --- | --- |
| `src/CdBurner.App/Resources/UiText.resx` (+ `UiText.<lang>.resx`) | Labels, buttons, tooltips and accessibility names in XAML | `Content="{loc:Loc UiSettingsResetToDefaults}"` |
| `src/CdBurner.App/Resources/Strings.resx` (+ `Strings.<lang>.resx`) | Dialog and status text built in code | `Strings.DialogAboutTitle`, `Strings.Format(nameof(Strings.DialogReplaceDiscBody), a, b)` |

`UiText.resx` and `Strings.resx` hold the English text. Each language has a sibling file with exactly the same keys.
`{loc:Loc Key}` binds to `LocalizationSource`, so open windows re-read their text when the language is switched.
`Strings.Get` reads the current UI culture on every call, so dialogs always use the language that is active now.

## Adding or changing text

1. Add the key and English text to the resource file (`UiText.resx` for XAML, `Strings.resx` plus a property in
   `Strings.cs` for code). Key names are `Ui<Area><Words>` or `<Dialog|Status><Area><Words>`; names must be unique
   even when upper and lower case are ignored.
2. Add the same key to **every** language file. Use the same numbered placeholders (`{0}`, `{1}`) in the same count;
   word order may differ. Do not translate product and format names (IgnisDisc, ISO, CD-R, FFmpeg, AccurateRip).
3. Keep leading and trailing spaces and line breaks that the English text has. Keep emoji prefixes on headings.
4. Run `dotnet test CdBurner.sln -c Release --filter "FullyQualifiedName~LocalizationTests"`.

Do not leave literal `Content`, `Text`, `Header` or `ToolTip` text in a view that is already converted; the tests fail
on it. Text that is identical in every language (a product name, `CD-R`) may stay literal and goes on the allow list
in `LocalizationTests`.

## Adding a language

1. Add the culture name to `LocalizationManager.SupportedLanguages` and its own-language name to `LanguageOption`.
2. Copy `UiText.resx` and `Strings.resx` to `UiText.<culture>.resx` and `Strings.<culture>.resx` and translate the values.
3. Add the culture to the `Locales` list in `LocalizationTests`.

## Pseudo-locale (`qps-ploc`)

`qps-ploc` is generated from the English text by `PseudoLocalizer`; it has no file to maintain, so it always covers every
key. Letters become accented look-alikes (`Settings` becomes `[Šéţţìñĝš ~~~~]`), the text is 40 % longer, and placeholders and
`%ENV%` tokens are kept. Text that stays plain in this mode was never moved into a resource, and clipped text shows a
layout that cannot hold a longer language. To look at it, set `"UiLanguage": "qps-ploc"` in the settings file.

## What the tests check

`tests/CdBurner.Integration.Tests/LocalizationTests.cs` checks that:

- every language file has exactly the keys of the English file and no empty value;
- every translation has the same numbered placeholders as the English text and formats without error;
- every `{loc:Loc Key}` in a XAML file names an existing key, and the converted views have no literal text left;
- the pseudo-locale covers every key, keeps placeholders, changes every letter and is at least 30 % longer;
- translations stay within a sane length of the English text;
- the language setting is saved, reloaded, applied to the process culture and reset to the system default.

## Current coverage

Converted: the shell (navigation, drive bar, virtual media buttons), Settings, Burn Progress, Virtual Drive, and all
dialog and status text in `Strings.resx`. The other workspaces (Data Disc, Audio CD, DVD-Video, Disc Image Studio,
Archive, Drive Utilities, Calibration, Disc Art), the chart controls, and text composed inside view models stay English
until they are moved over the same way; pull requests that convert one view at a time are welcome.
