# Third-party notices

IgnisDisc is licensed under the GNU General Public License v3.0 or later (`GPL-3.0-or-later`; see `LICENSE` and `TRADEMARKS.md`). Release builds include the following third-party components.

| Component | Used for | License |
| --- | --- | --- |
| .NET runtime and Windows Desktop runtime (self-contained builds) | Runs the app | MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| CommunityToolkit.Mvvm | MVVM helpers in the desktop app | MIT — https://github.com/CommunityToolkit/dotnet/blob/main/License.md |
| Microsoft.Extensions.DependencyInjection | Service wiring in the desktop app | MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| Spleen 5×8 bitmap font | Latin glyph strokes on Rosetta optical text pages | BSD-2-Clause — https://github.com/fcambus/spleen/blob/2.2.0/LICENSE |
| GNU Unifont bitmap font | Non-Latin scripts and reader instructions on Rosetta optical text pages | SIL Open Font License 1.1 — https://unifoundry.com/unifont/ |

Windows components used at runtime but **not** redistributed: IMAPIv2 (`imapi2.dll`, `imapi2fs.dll`), Media Foundation, and Windows Imaging Component (`windowscodecs.dll`).

## ffmpeg (optional, 1-click install available)

DVD-Video transcoding calls an external `ffmpeg.exe` as a separate program. It is **not** bundled by default.
You can download and install the official LGPL Windows build with 1 click from the **DVD-Video** or **Settings** workspace (installed per-user into `%LOCALAPPDATA%\Programs\ffmpeg\ffmpeg.exe` alongside `ffmpeg-LICENSE.txt`), or point IgnisDisc to an existing `ffmpeg.exe`.
If a release bundles it (via `scripts/Build-Installer.ps1 -FfmpegPath ... -FfmpegLicensePath ...`), that build's
license text ships as `ffmpeg-LICENSE.txt` next to it. ffmpeg is licensed under the LGPL v2.1+ (or GPL v2+ for
GPL-enabled builds); see https://ffmpeg.org/legal.html. Prefer an LGPL build, record exactly which build you shipped
in the release notes, and link to its source.

## par2cmdline-turbo / par2cmdline (optional, 1-click install available)

Archive recovery data is created and repaired by calling an external `par2.exe` as a separate program. It is **not**
bundled. The 1-click install in the **Archive** or **Settings** workspace downloads
[par2cmdline-turbo](https://github.com/animetosho/par2cmdline-turbo) 1.5.0 (GPL-2.0-or-later; a par2cmdline fork with
faster checksum and Reed-Solomon code, same command line and PAR 2.0 files), the official `win-x64` or `win-arm64`
release archive, into `%LOCALAPPDATA%\Programs\par2cmdline\par2.exe`. The archive's SHA-256 is pinned in IgnisDisc and
a download with any other hash is refused. The release archive holds only `par2.exe`; its license text and source are at
https://github.com/animetosho/par2cmdline-turbo/blob/v1.5.0/COPYING and the release's `.tar.xz`.

[par2cmdline](https://github.com/Parchive/par2cmdline) 1.4.0 (GPL-2.0-or-later), which earlier versions installed, also
works: recovery files made by either program verify and repair with the other. When IgnisDisc finds par2cmdline 1.4.0
it offers the 1-click update. You can also put any `par2.exe` on `PATH`; the 1-click install location is checked first.

## DiscImageStudio and CDImage (ported source code, GPL-3.0-or-later)

The lithography code under `src/CdBurner.Lithography/Ported/DiscImageStudio/` is ported from
[DiscImageStudio](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio) at commit
`619ec7505ac8955f35fe2f353ebda74b6ea90dda` (Copyright (c) 2026 Disc Image Studio contributors,
GPL-3.0-or-later). Its CD-DA delay interleaver and delay table come from
[CDImage](https://github.com/arduinocelentano/cdimage) revision `de5ca19f403b86934cfedb6793ca75f6716080c0`
(`src/converter.cpp`, `src/converter.h`; Copyright (C) 2008-2022 arduinocelentano, GPL-3.0-or-later), as adapted by
DiscImageStudio. CDImage acknowledges [unDEFER] / CD PAINT as an earlier source for its converter. Both licences are
the GNU General Public License version 3 or later, the same as IgnisDisc's; the full text is in `LICENSE`.

Ported files (each keeps its upstream copyright lines and names its source file and the changes made):

| IgnisDisc file | Upstream file |
| --- | --- |
| `Cd/CddaInterleaver.cs`, `Cd/CddaInterleaveTable.cs` | `src/DiscImageStudio.Cd/`, same names (adapted from CDImage) |
| `Cd/CdTrackGenerator.cs`, `Cd/CdImageSampler.cs`, `Cd/CdDiscParameters.cs`, `Cd/RasterImage.cs` | `src/DiscImageStudio.Cd/`, same names |
| `Cd/CdToneMapping.cs`, `Cd/CdWaveFile.cs`, `Cd/CdAudioByteOrder.cs`, `Cd/CdBlockPipeline.cs`, `Cd/CdPreviewMask.cs` | `src/DiscImageStudio.Cd/`, same names |
| `Cd/CdMode1Drawing.cs` | `src/DiscImageStudio.Cd/CdDataImageGenerator.cs` (drawing part only) |
| `Core/ToneProfile.cs` | `src/DiscImageStudio.Core/ToneProfile.cs` |
| `Presets/DiscPresetJsonStore.cs`, `Presets/built-in-disc-presets.json` | `src/DiscImageStudio.Core/DiscPresetJsonStore.cs`, `src/DiscImageStudio.Core/Presets/built-in-disc-presets.json` |
| `Core/BinaryGrayscale.cs`, `Core/DvdTrackGeometry.cs`, `Core/DvdReadCompatibilityModes.cs` | `src/DiscImageStudio.Core/`, same names |
| `Core/OpticalDiscContracts.cs` | `src/DiscImageStudio.Core/OpticalDiscContracts.cs` (two records only) |
| `Dvd/Encoding/*.cs` (12 files: data frame, ECC block, Reed-Solomon, EFMPlus tables, encoder, word scorer, NRZI metrics) | `src/DvdImageSolver/Encoding/`, same names |
| `Dvd/Solver/*.cs` except the two files named below | `src/DvdImageSolver/Solver/`, same names |
| `Dvd/Solver/SampleRectangle.cs` | `src/DvdImageSolver/Solver/CalibrationReferenceImageSampler.cs` (two helper types only) |
| `Dvd/BitPacking.cs`, `Dvd/IsoImageWriter.cs`, `Dvd/RawIsoImageWriter.cs` | `src/DvdImageSolver/`, same names |
| `Dvd/DvdStreamingGenerator.cs`, `Dvd/DvdImageGenerator.cs`, `Dvd/DvdImageMetadata.cs`, `Dvd/DvdDiscPreset.cs`, `Dvd/DvdTonePreview.cs` | `src/DiscImageStudio.Dvd/`, same names |
| `Dvd/dispersion-byte-pools.json` | `dispersion-byte-pools.json` |
| `BluRay/Encoding/*.cs` (8 files: EDC, scrambler, Reed-Solomon, LDC/BIS cluster, 17PP, recording frame, dc-control, recording unit) | `src/DiscImageStudio.BluRay/Encoding/`, same names |
| `BluRay/BluRayDiscParameters.cs`, `BluRay/BluRayTrackGeometry.cs`, `BluRay/BluRayImageMapping.cs`, `BluRay/BluRayPatternRangeMapping.cs`, `BluRay/BluRayRunLengthMapping.cs`, `BluRay/BluRayToneMapping.cs` | `src/DiscImageStudio.BluRay/`, same names |
| `BluRay/BluRayStreamingGenerator.cs`, `BluRay/BluRayImageGenerator.cs`, `BluRay/BluRayIsoMetadata.cs` | `src/DiscImageStudio.BluRay/`, same names |
| `BluRay/BluRayIsoGenerator.cs` | `src/DiscImageStudio.BluRay/BluRayIsoGenerator.cs` (without the file system plan and writer) |
| `BluRay/BluRayImageLoader.cs` | `src/Shared/BluRayImageLoader.cs` |
| `tests/CdBurner.Lithography.Tests/Dvd/DvdUpstreamSelfTests.cs` (test code, not shipped) | `src/DvdImageSolver/SelfTests.cs` |
| `tests/CdBurner.Lithography.Tests/BluRay/BluRayUpstream*.cs` (9 files; test code, not shipped) | `tests/DiscImageStudio.ArchitectureTests/`: `BluRayEncodingTests.cs`, `BluRayImageMappingTests.cs`, `BluRayParallelGenerationTests.cs`, `BluRayPixelReuseTests.cs`, `BluRayPatternRangeTests.cs`, `BluRayCubicPitchTests.cs`, `BluRaySidecarTests.cs`, `BluRayDrawingModeTests.cs`, `BluRayModuleTests.cs` (geometry and streaming tests only) |

The DVD EFMPlus table (`Dvd/Encoding/EfmPlusTables.Generated.cs`) is generated by DiscImageStudio from ECMA-267,
Annex G.

`Dvd/Solver/DvdReadCompatibilityGuard.cs` is IgnisDisc's own file (Copyright (c) 2026 tetherworks), not a port. It
implements the `periodic-9b-16-v1` rule from its one-sentence description; DiscImageStudio credits
[bczhc/dvd-tools](https://github.com/bczhc/dvd-tools) for that rule, and no code from that project is used.
DiscImageStudio's older `bridge-9b-v1` rule (adapted by upstream from disc-probe) is not ported.

Not ported: upstream's ISO 9660/UDF writer (`DiscImageStudio.Iso`) and the DVD code that writes a picture into an
image holding files, image codecs, calibration targets, the DVD and Blu-ray command lines and module catalog, and
upstream's burning code. The Blu-ray port is the picture generator only: a Blu-ray picture image that also holds
files is refused, and no CDImage code is used in it. `DvdTrackGeometry` is ported for the DVD solver and for the
Blu-ray variable-pitch geometry, so that their output matches upstream byte for byte. Preset cubic pitch coefficients are converted into IgnisDisc's unified `SpiralGeometry`
(`src/CdBurner.Lithography/Geometry/DiscPresetGeometry.cs`), and PNG input and output in
`src/CdBurner.Lithography/Imaging/` uses IgnisDisc's framing validator and Windows Imaging Component
(`windowscodecs.dll`).

## Rosetta bitmap fonts (Spleen 5×8 and GNU Unifont)

IgnisDisc bundles two bitmap fonts as embedded resources in `CdBurner.Lithography` for rendering optical text pages:

- **Spleen 5×8** (`src/CdBurner.Lithography/Fonts/Spleen/spleen-5x8.bdf`, version 2.2.0) and its licence text (`src/CdBurner.Lithography/Fonts/Spleen/LICENSE`): Copyright (c) 2018-2026, Frederic Cambus, licensed under the BSD 2-Clause License (`BSD-2-Clause`).
- **GNU Unifont** (`src/CdBurner.Lithography/Fonts/Unifont/unifont.hex.gz`, version 17.0.05 BMP precompiled hex) and its licence text (`src/CdBurner.Lithography/Fonts/Unifont/OFL.txt`): IgnisDisc distributes the Unifont glyph data under the **SIL Open Font License 1.1** (`OFL-1.1`) option of GNU Unifont's dual licence (as stated in upstream `COPYING`: *"License for font files except the jiskan16 files mentioned above are dual-licensed under the SIL Open Font License version 1.1, and under this GNU license: All glyphs are released under the GNU General Public License (GPL) version 2 or (at your option) a later version, with the GNU font embedding exception"*). The font software remains under SIL OFL 1.1 (Reserved Font Name: `Unifont`), may not be sold by itself, and its full licence text ships in `src/CdBurner.Lithography/Fonts/Unifont/OFL.txt`.

## CUETools / CUETools Database (reimplemented algorithms, GPL-2.0-or-later)

The CUETools Database (`CTDB`) TOCID, sample-window CRC-32 calculation, and `lookup2.php` v3 XML client in
`src/CdBurner.Core/Audio/CueToolsDatabase.cs` and `src/CdBurner.Core/Audio/DiscIdCalculator.cs` are clean-room
reimplementations of the algorithms and wire protocol from [CUETools](https://github.com/gchudov/cuetools.net)
(Copyright (C) 2008–2025 Grigory Chudov, licensed under `GPL-2.0-or-later`).

## Online services (optional, 1-click lookup & configurable defaults)

Audio ripping can look up track names from [MusicBrainz](https://musicbrainz.org) (automatically during ripping or on demand via the 1-click **Look Up Disc (MusicBrainz)** button), check rips against the
[AccurateRip](http://www.accuraterip.com) database, and (when opted in) cross-reference track and disc CRC-32s against the [CUETools Database](http://db.cuetools.net). All three lookups can be toggled in the **Audio CD Ripper** and **Settings** workspaces, and only send the disc's table-of-contents
identifiers, never audio or personal data.

## Test-only dependencies (not shipped)

xUnit, Microsoft.NET.Test.Sdk, coverlet, and LTRData.DiscUtils (used as an independent ISO 9660 / Joliet / UDF
reader to validate generated images).
