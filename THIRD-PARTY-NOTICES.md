# Third-party notices

IgnisDisc is licensed under the GNU General Public License v3.0 or later (`GPL-3.0-or-later`; see `LICENSE` and `TRADEMARKS.md`). Release builds include the following third-party components.

| Component | Used for | License |
| --- | --- | --- |
| .NET runtime and Windows Desktop runtime (self-contained builds) | Runs the app | MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| CommunityToolkit.Mvvm | MVVM helpers in the desktop app | MIT — https://github.com/CommunityToolkit/dotnet/blob/main/License.md |
| Microsoft.Extensions.DependencyInjection | Service wiring in the desktop app | MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |

Windows components used at runtime but **not** redistributed: IMAPIv2 (`imapi2.dll`, `imapi2fs.dll`) and Media Foundation.

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

The CD picture-track code in `src/CdBurner.Lithography/Ported/DiscImageStudio/Cd/` is ported from
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
| `CddaInterleaver.cs`, `CddaInterleaveTable.cs` | `src/DiscImageStudio.Cd/`, same names (adapted from CDImage) |
| `CdTrackGenerator.cs`, `CdImageSampler.cs`, `CdDiscParameters.cs`, `RasterImage.cs` | `src/DiscImageStudio.Cd/`, same names |
| `CdToneMapping.cs`, `CdWaveFile.cs`, `CdAudioByteOrder.cs`, `CdBlockPipeline.cs`, `CdPreviewMask.cs` | `src/DiscImageStudio.Cd/`, same names |
| `CdMode1Drawing.cs` | `src/DiscImageStudio.Cd/CdDataImageGenerator.cs` (drawing part only) |
| `ToneProfile.cs` | `src/DiscImageStudio.Core/ToneProfile.cs` |

Not ported: upstream's ISO writer, image codecs, calibration targets and variable track-pitch geometry. The PNG
reader and writer in `src/CdBurner.Lithography/Imaging/` are IgnisDisc's own code.

## Online services (optional, 1-click lookup & configurable defaults)

Audio ripping can look up track names from [MusicBrainz](https://musicbrainz.org) (automatically during ripping or on demand via the 1-click **Look Up Disc (MusicBrainz)** button) and check rips against the
[AccurateRip](http://www.accuraterip.com) database. Both lookups can be toggled in the **Audio CD Ripper** and **Settings** workspaces, and only send the disc's table-of-contents
identifiers, never audio or personal data.

## Test-only dependencies (not shipped)

xUnit, Microsoft.NET.Test.Sdk, coverlet, and LTRData.DiscUtils (used as an independent ISO 9660 / Joliet / UDF
reader to validate generated images).
