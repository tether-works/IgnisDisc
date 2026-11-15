// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DvdImageSolver/Encoding/ModulationBoundary.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DvdImageSolver/Encoding/ModulationBoundary.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Dvd.Encoding;

public readonly record struct ModulationBoundary(byte State, int Dsv, bool IsLand)
{
    public static ModulationBoundary Validate(byte state, int dsv, bool isLand)
    {
        if (state is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "EFMPlus state must be 1..4.");
        }

        return new ModulationBoundary(state, dsv, isLand);
    }

    public string LevelName => IsLand ? "land" : "pit";
}
