using System.Text;

namespace CdBurner.App.Resources;

/// <summary>
/// Builds the <c>qps-ploc</c> pseudo-language from English text: letters become accented look-alikes (so text that
/// was never extracted into a resource stays plain and stands out) and the string is lengthened by 40 % (so layouts
/// that cannot hold longer languages show clipping). Numbered placeholders such as <c>{0}</c> or <c>{1:N0}</c> and
/// <c>%NAME%</c> environment tokens are left untouched so formatting still works.
/// </summary>
public static class PseudoLocalizer
{
    private const string Plain = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string Accented = "ÅßÇÐÉƑĜĤÌĴĶĻṀÑÖÞǪŖŠŢÛṼŴẊÝŽåƀçðéƒĝĥìĵķļṁñöþǫŕšţûṽŵẋýž";

    /// <summary>How much longer than the English text the pseudo text is, as a fraction of its length.</summary>
    public const double Expansion = 0.4;

    /// <summary>Converts one English string.</summary>
    public static string Convert(string english)
    {
        var text = new StringBuilder(english.Length * 2 + 2);
        text.Append('[');
        var i = 0;
        while (i < english.Length)
        {
            var c = english[i];
            if (c == '{' && TryPlaceholderEnd(english, i, out var end))
            {
                text.Append(english, i, end - i + 1);
                i = end + 1;
            }
            else if (c == '%' && TryEnvironmentTokenEnd(english, i, out end))
            {
                text.Append(english, i, end - i + 1);
                i = end + 1;
            }
            else
            {
                var index = Plain.IndexOf(c);
                text.Append(index >= 0 ? Accented[index] : c);
                i++;
            }
        }
        var padding = (int)Math.Ceiling(english.Length * Expansion);
        if (padding > 0)
        {
            text.Append(' ').Append('~', padding);
        }
        text.Append(']');
        return text.ToString();
    }

    /// <summary>True at <c>{digits}</c> or <c>{digits:format}</c>; <paramref name="end"/> is the closing brace.</summary>
    private static bool TryPlaceholderEnd(string s, int start, out int end)
    {
        end = -1;
        var j = start + 1;
        while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        if (j == start + 1 || j >= s.Length) return false;
        if (s[j] == ':')
        {
            while (j < s.Length && s[j] != '}') j++;
        }
        if (j >= s.Length || s[j] != '}') return false;
        end = j;
        return true;
    }

    /// <summary>True at <c>%LETTERS_OR_DIGITS%</c>; <paramref name="end"/> is the closing percent sign.</summary>
    private static bool TryEnvironmentTokenEnd(string s, int start, out int end)
    {
        end = -1;
        var j = start + 1;
        while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_')) j++;
        if (j == start + 1 || j >= s.Length || s[j] != '%') return false;
        end = j;
        return true;
    }
}
