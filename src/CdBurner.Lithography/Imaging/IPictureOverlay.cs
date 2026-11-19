// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

namespace CdBurner.Lithography.Imaging;

/// <summary>
/// Marks drawn over a picture in a ring of the disc, before the picture is encoded. The CD, DVD and BD picture
/// encoders ask the overlay first and fall back to the picture where it has nothing to say.
/// <para>
/// Coordinates are millimetres from the disc centre in the picture's own frame: x to the right, y down, the way the
/// picture is seen. Whatever an encoder does to place the picture on the disc (start angle, spiral direction) is
/// then done to the overlay too.
/// </para>
/// <para>Implementations must be immutable: encoders call <see cref="TrySample"/> from several threads at once.</para>
/// </summary>
public interface IPictureOverlay
{
    /// <summary>No point closer to the centre than this belongs to the overlay, in millimetres.</summary>
    double InnerRadiusMm { get; }

    /// <summary>No point further from the centre than this belongs to the overlay, in millimetres.</summary>
    double OuterRadiusMm { get; }

    /// <summary>
    /// True when the point belongs to the overlay; <paramref name="gray"/> is then the luminance to draw (0 dark,
    /// 255 light). False when the picture shows through.
    /// </summary>
    bool TrySample(double xMm, double yDownMm, out byte gray);
}
