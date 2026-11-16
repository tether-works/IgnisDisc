// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Core/DiagnosticMessage.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Core/DiagnosticMessage.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

using System.Globalization;
using System.Runtime.CompilerServices;

namespace CdBurner.Lithography;

/// <summary>Preserves an application-owned diagnostic template until a UI can
/// translate it. The exception type, parameter name and original message remain
/// available to callers and diagnostic logs.</summary>
public static class DiagnosticMessage
{
    private static readonly ConditionalWeakTable<Exception, FormattableString> Templates = new();

    public static ArgumentException Argument(FormattableString message, string? parameterName = null)
        => Attach(new ArgumentException(Render(message), parameterName), message);

    public static InvalidDataException InvalidData(FormattableString message, Exception? innerException = null)
        => Attach(new InvalidDataException(Render(message), innerException), message);

    public static FormattableString? Template(Exception exception)
        => Templates.TryGetValue(exception, out FormattableString? template) ? template : null;

    // Only marked templates are translated recursively. Names, paths and other
    // ordinary string arguments are user data and must remain untouched.
    public static ExceptionDetail Detail(Exception exception) => new(exception);

    public sealed record ExceptionDetail(Exception Exception)
    {
        public override string ToString() => Exception.Message;
    }

    private static T Attach<T>(T exception, FormattableString message) where T : Exception
    {
        Templates.Add(exception, message);
        return exception;
    }

    private static string Render(FormattableString message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.ToString(CultureInfo.CurrentCulture);
    }
}
