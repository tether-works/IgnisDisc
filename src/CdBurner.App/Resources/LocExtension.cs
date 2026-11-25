using System.Windows.Data;
using System.Windows.Markup;

namespace CdBurner.App.Resources;

/// <summary>
/// XAML markup extension for translated text: <c>Content="{loc:Loc UiSettingsResetToDefaults}"</c>.
/// It binds to <see cref="LocalizationSource"/>, so the text changes when the language is switched while the
/// window is open.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    /// <summary>Key in <c>UiText.resx</c>.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
