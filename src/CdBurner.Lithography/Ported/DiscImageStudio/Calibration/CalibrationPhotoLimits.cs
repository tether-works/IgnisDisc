// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Core/Calibration/CalibrationPhotoLimits.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Core/Calibration/CalibrationPhotoLimits.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Calibration;

/// <summary>Shared bounds for decoded photos, rectification buffers and portable sessions.</summary>
public static class CalibrationPhotoLimits
{
    public const int MaxDimension = 8192;
    // A BGRA working buffer stays within 128 MiB, including for panoramic photos.
    public const int MaxPixelCount = 32 * 1024 * 1024;
    // Allow an incompressible 32-bit PNG plus Base64 and JSON escaping overhead.
    public const int MaxEncodedPhotoLength = 192 * 1024 * 1024;
    public const int MaxSessionLength = 256 * 1024 * 1024;
    public static int MaxRectifiedDimension => (int)Math.Sqrt(MaxPixelCount);

    public static bool Contains(int width, int height) => width > 0 && height > 0
        && width <= MaxDimension && height <= MaxDimension && (long)width * height <= MaxPixelCount;
}
