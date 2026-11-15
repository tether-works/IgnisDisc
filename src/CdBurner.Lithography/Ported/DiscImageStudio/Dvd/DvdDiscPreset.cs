// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Dvd/DvdDiscPreset.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Dvd/DvdDiscPreset.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Dvd;

public sealed record DvdDiscPreset(
    string Id,
    string DisplayName,
    string Description,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    string? DisplayNameResourceKey = null,
    string? DescriptionResourceKey = null,
    bool IsCustom = false,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    string? Contributor = null,
    uint? UsableSectors = null)
{
    public uint ResolvedUsableSectors => ResolveUsableSectors(TotalSectors, UsableSectors);

    // A conservative fallback for legacy presets, not a deduction from device-reported free space.
    public static uint ResolveUsableSectors(uint totalSectors, uint? usableSectors = null)
        => usableSectors ?? (totalSectors > 608 ? (totalSectors - 608) / 16 * 16 : 0);

    public static DvdDiscPreset Manual { get; } = new(
        "__manual__",
        "自定义参数（未保存）",
        "生成参数已在界面中手动调整；如需长期保存，请写入用户预设 JSON。",
        2_295_104,
        24.0,
        58.0,
        IsCustom: true);
}
