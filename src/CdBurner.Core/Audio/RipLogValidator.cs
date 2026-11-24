using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CdBurner.Core.Audio;

/// <summary>
/// Outcome of validating an IgnisDisc extraction log's SHA-256 and HMAC-SHA256 integrity footer.
/// </summary>
public sealed record RipLogValidationResult(
    bool IsValid,
    string Message,
    string? RecordedHmacSha256 = null,
    string? ComputedHmacSha256 = null,
    string? RecordedSha256 = null,
    string? ComputedSha256 = null);

/// <summary>
/// Validates EAC-style extraction logs produced by <see cref="RipLogFormatter"/> against tampering or truncation.
/// </summary>
public static class RipLogValidator
{
    private static readonly Regex HmacFooterRegex = new(
        @"^==== Log checksum (?<hex>[0-9A-Fa-f]{64}) ====$",
        RegexOptions.Compiled);

    private static readonly Regex ShaFooterRegex = new(
        @"^SHA-256:\s*(?<hex>[0-9A-Fa-f]{64})$",
        RegexOptions.Compiled);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Validates <paramref name="logText"/> by verifying its SHA-256 body digest and HMAC-SHA256 checksum footer.
    /// </summary>
    public static RipLogValidationResult Validate(string? logText, byte[]? hmacKey = null)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return new RipLogValidationResult(false, "Log text is empty.");
        }

        string normalized = logText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');

        int lastNonEmpty = lines.Length - 1;
        while (lastNonEmpty >= 0 && string.IsNullOrWhiteSpace(lines[lastNonEmpty]))
        {
            lastNonEmpty--;
        }

        if (lastNonEmpty < 1)
        {
            return new RipLogValidationResult(false, "Missing integrity footer.");
        }

        int hmacIndex = -1;
        for (int i = lastNonEmpty; i >= 0; i--)
        {
            if (HmacFooterRegex.IsMatch(lines[i]))
            {
                hmacIndex = i;
                break;
            }
        }

        if (hmacIndex < 0)
        {
            return new RipLogValidationResult(false, "Missing HMAC-SHA256 log checksum footer.");
        }

        if (hmacIndex + 1 != lastNonEmpty)
        {
            return new RipLogValidationResult(false, "Unexpected trailing content or missing SHA-256 line after log checksum footer.");
        }

        Match hmacMatch = HmacFooterRegex.Match(lines[hmacIndex]);
        Match shaMatch = ShaFooterRegex.Match(lines[hmacIndex + 1]);
        if (!shaMatch.Success)
        {
            return new RipLogValidationResult(false, "Missing or malformed SHA-256 footer line.");
        }

        string recordedHmac = hmacMatch.Groups["hex"].Value.ToUpperInvariant();
        string recordedSha = shaMatch.Groups["hex"].Value.ToUpperInvariant();

        // Body ends at the last non-empty line preceding the blank separator before the footer.
        int bodyEnd = hmacIndex - 1;
        while (bodyEnd >= 0 && string.IsNullOrEmpty(lines[bodyEnd]))
        {
            bodyEnd--;
        }

        if (bodyEnd < 0)
        {
            return new RipLogValidationResult(false, "Log body before integrity footer is empty.", recordedHmac, null, recordedSha, null);
        }

        string bodyText = string.Join('\n', lines[..(bodyEnd + 1)]) + '\n';
        byte[] bodyBytes = Utf8NoBom.GetBytes(bodyText);

        byte[] computedShaBytes = SHA256.HashData(bodyBytes);
        byte[] computedHmacBytes = HMACSHA256.HashData(RipLogFormatter.ResolveHmacKey(hmacKey), bodyBytes);

        string computedSha = Convert.ToHexString(computedShaBytes);
        string computedHmac = Convert.ToHexString(computedHmacBytes);

        byte[] recordedShaBytes = Convert.FromHexString(recordedSha);
        byte[] recordedHmacBytes = Convert.FromHexString(recordedHmac);

        bool shaValid = CryptographicOperations.FixedTimeEquals(recordedShaBytes, computedShaBytes);
        bool hmacValid = CryptographicOperations.FixedTimeEquals(recordedHmacBytes, computedHmacBytes);

        if (!shaValid && !hmacValid)
        {
            return new RipLogValidationResult(
                false,
                "Log body has been modified (SHA-256 and HMAC-SHA256 checksums do not match).",
                recordedHmac,
                computedHmac,
                recordedSha,
                computedSha);
        }

        if (!shaValid)
        {
            return new RipLogValidationResult(
                false,
                "SHA-256 footer checksum does not match log body.",
                recordedHmac,
                computedHmac,
                recordedSha,
                computedSha);
        }

        if (!hmacValid)
        {
            return new RipLogValidationResult(
                false,
                "HMAC-SHA256 footer checksum does not match log body.",
                recordedHmac,
                computedHmac,
                recordedSha,
                computedSha);
        }

        return new RipLogValidationResult(
            true,
            "Log integrity verified (SHA-256 and HMAC-SHA256 match).",
            recordedHmac,
            computedHmac,
            recordedSha,
            computedSha);
    }

    /// <summary>
    /// Reads and validates an extraction log file from disk.
    /// </summary>
    public static RipLogValidationResult ValidateFile(string filePath, byte[]? hmacKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            return new RipLogValidationResult(false, $"Log file not found: '{filePath}'.");
        }

        string text = File.ReadAllText(filePath, Utf8NoBom);
        return Validate(text, hmacKey);
    }
}
