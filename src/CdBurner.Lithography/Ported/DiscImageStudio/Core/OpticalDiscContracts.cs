// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Core/OpticalDiscContracts.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Core/OpticalDiscContracts.cs
// IgnisDisc change: namespace; only the two records the DVD generator uses are ported (no module catalog). See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography;

public sealed record DiscJobProgress(
    string ModuleId,
    string Stage,
    string Message,
    double? Fraction = null,
    long? CompletedUnits = null,
    long? TotalUnits = null,
    TimeSpan? Elapsed = null);

/// <summary>A completed file-generation operation. Failures propagate as exceptions;
/// command-line exit codes belong to the CLI adapter, not the engine API.</summary>
public sealed record DiscImageResult(string OutputPath, string? Summary = null);
