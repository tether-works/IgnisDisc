// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DvdImageSolver/Solver/SolverEngine.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DvdImageSolver/Solver/SolverEngine.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.Dvd.Encoding;

namespace CdBurner.Lithography.Dvd.Solver;

internal static class SolverEngine
{
    internal static SolverResult Solve(
        ImageConstraint constraint,
        ReadOnlySpan<byte> initialPayloads,
        uint initialLba,
        uint psnOffset,
        ModulationBoundary initialBoundary,
        SolverOptions options,
        Action<int, int, int>? progress = null)
        => options.Algorithm switch
        {
            SolverAlgorithm.Full => new LocalSearchSolver().Solve(
                constraint,
                initialPayloads,
                initialLba,
                psnOffset,
                initialBoundary,
                options,
                progress),
            SolverAlgorithm.StateControl => new StateControlSolver().Solve(
                constraint,
                initialPayloads,
                initialLba,
                psnOffset,
                initialBoundary,
                options,
                progress),
            SolverAlgorithm.Dispersion => new DispersionPoolSolver().Solve(
                constraint,
                initialPayloads,
                initialLba,
                psnOffset,
                initialBoundary,
                options,
                progress),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unknown solver algorithm."),
        };
}
