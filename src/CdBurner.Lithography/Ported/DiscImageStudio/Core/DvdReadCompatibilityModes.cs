// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Core/DvdReadCompatibilityModes.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Core/DvdReadCompatibilityModes.cs
// IgnisDisc change: namespace; the legacy bridge-9b-v1 mode is not ported, so its name is known (old calibration
// records mean it when they name no mode) but it is not a supported value. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography;

/// <summary>Versioned drawing-byte transforms recorded with DVD images.</summary>
public static class DvdReadCompatibilityModes
{
    public const string Off = "off";
    public const string LegacyBridge = "bridge-9b-v1";
    public const string Periodic9B16 = "periodic-9b-16-v1";
    public const string Current = Periodic9B16;

    public static bool IsSupported(string? mode) => mode is Periodic9B16;
}
