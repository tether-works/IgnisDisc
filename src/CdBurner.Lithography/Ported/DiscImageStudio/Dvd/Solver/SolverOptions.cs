// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DvdImageSolver/Solver/SolverOptions.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DvdImageSolver/Solver/SolverOptions.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.Dvd.Encoding;

namespace CdBurner.Lithography.Dvd.Solver;

public enum SolverAlgorithm
{
    Full,
    StateControl,
    Dispersion,
}

public sealed record SolverOptions(
    int Iterations,
    int MutationBytes,
    int RandomSeed,
    double InitialTemperature,
    double FinalTemperature,
    SolverAlgorithm Algorithm = SolverAlgorithm.Full,
    int BridgeBytes = 2,
    int BeamWidth = 8,
    int RetryBeamWidth = 32,
    double RetryFrameFraction = 0.25,
    int BatchFrames = 8,
    int BatchCandidates = 4,
    int SearchLanes = 1)
{
    public bool AdaptiveRetryEnabled
        => RetryBeamWidth > BeamWidth;
}

public sealed record SolverResult(
    byte[] Payloads,
    EncodedEccBlock Encoded,
    int Score,
    int ScoreMaximum,
    string ScoreMetric,
    int CompletedIterations,
    TimeSpan Elapsed)
{
    public int? GrayScore { get; init; }

    public int? GrayScoreMaximum { get; init; }

    public string? GrayScoreMetric { get; init; }

    public int? ControlledScore { get; init; }

    public int? ControlledScoreMaximum { get; init; }

    public string? ControlledScoreMetric { get; init; }

    public int? ControlledGrayScore { get; init; }

    public int? ControlledGrayScoreMaximum { get; init; }

    public string? ControlledGrayScoreMetric { get; init; }
}
