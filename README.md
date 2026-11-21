# IgnisDisc Optical Studio

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-lightgrey.svg)]()
[![.NET](https://img.shields.io/badge/.NET-10-blue.svg)](https://dotnet.microsoft.com/)

A free, open-source CD and DVD burning and mastering tool for Windows 10 and 11, with a WPF desktop app
(`IgnisDisc.exe`) and a scriptable command-line tool (`ignisdisc-cli.exe`). It burns through Windows' own
burning API (IMAPIv2) and IgnisDisc's own SCSI MMC drive commands, reads discs through SCSI pass-through, and ships a virtual optical drive so the whole
pipeline can be tested without hardware or blank media.

> Internal project and namespace names still use `CdBurner.*`; the product name is IgnisDisc.

---

## What works today

<!-- SUPPORT-TABLE-START -->
| Feature | Physical drives | Virtual drive | Notes |
| --- | :---: | :---: | --- |
| Burn data disc from files / folders | ✅ | ✅ | ISO 9660 + Joliet + UDF bridge; UDF 1.02 by default, 1.50 to 2.60 on request (`--udf-rev`). With the Windows writer (the default) on real discs on one drive (LG GP60NB50), every file read back identical: a DVD+RW, a DVD-R and a DVD+R (two sessions each on the DVD-R and DVD+R), and a dual-layer DVD+R holding 5.5 GB across both layers (Windows puts the layer break at half the data). A DVD-RW (Ritek) too, with one difference: Windows' writer formats a blank DVD-RW for overwriting before it writes, so it is never left open for more sessions (the burn says so when that was asked for); every file identical, also after writing it over past the part formatted first |
| Burn an existing `.iso` | ✅ | ✅ | |
| Create `.iso` image files | ✅ | — | Validated against an independent reader (DiscUtils) in the test suite |
| Verify after burn | ✅ | ✅ | Reads every sector back and compares it with the image. The drive is told to read the disc itself, not its memory, where burn data can still be held; `--strict-verify` does that for every read. Damage is reported by file |
| Hash lists on data discs (`SHA256SUMS` / `BLAKE3SUMS`) | ✅ | ✅ | Optional (`--sha256sums`, `--blake3sums`, or checkboxes on the Data Disc screen); written to the disc root and checked file by file after the burn |
| Rip data disc to `.iso` / `.bin` + `.cue` | ✅ | ✅ | |
| Burn audio CD (Disc-at-Once / Track-at-Once) | ✅ | ✅ | WAV, FLAC (built-in decoder, incl. 24-bit / hi-res / surround), MP3/M4A via Media Foundation; `.cue` sheets (per-track or single-file albums) and `.m3u`/`.m3u8`/`.pls` playlists; loudness leveling or an optional -18 LUFS normalisation with a -1 dBTP limiter, crossfade, preview, printable jewel-case insert. Opus, Ogg and MP3 can also be decoded through the optional ffmpeg download, trimmed to the exact length for gapless albums; the lossy decoding and the normalisation have only been tested on files and the virtual drive, with one 2021 ffmpeg build, not on a burned disc |
| Burn / verify / convert legacy images | ✅ | ✅ | Nero `.nrg`, Alcohol 120% `.mdf`/`.mds`, CloneCD `.ccd`/`.img`, incl. Mode 2 / XA; streamed, no size limit. DiscJuggler `.cdi` (v2, v3, v3.5) is read as well, checked only on hand-built files that follow the published layout, not on an image made by DiscJuggler; the first data track is used |
| Disc copy (one or two drives, multi-copy) | ⚠️ | ✅ | Swap prompts between copies; audio CDs copied track by track. A mixed-mode CD (data track first, then audio) is copied whole with direct drive commands turned on, otherwise only its audio; done once on a real drive (one drive, a CD-RW copied to a CD-R). An Enhanced CD (audio session, then a data session) is copied whole with direct drive commands on; that copy has run only in the built-in drive simulator. The source audio is read with error checks and re-reads, and a copy stops before writing when sectors still read wrong |
| Rip audio CD to `.wav` / `.flac` / `.mp3` / `.opus` / `.m4a` + `.cue` | ✅ | ✅ | Reads the real TOC and CD-Text; optional MusicBrainz track names, release year and front cover (Cover Art Archive). When MusicBrainz lists editions that name tracks differently, the rip stops before reading the disc and asks which edition you have. Files can go straight into a library folder (`Artist\Album (Year)\NN - Title` by default, or your own naming template), in several formats from one rip, with `folder.jpg` and a `.cue` per album; MP3 can be constant bitrate or VBR (V0-V9). `convert-audio` (and the app's Convert Audio tab) converts files already on disk, keeping their tags and cover. AAC uses the encoder built into Windows; FLAC, MP3 and Opus use the optional ffmpeg download (no encoder is bundled). Ripping has run on a real drive (LG GP60NB50): tracks from CD-Rs and a CD-RW burned by IgnisDisc came back identical to the source audio. Gap detection (INDEX 00 marks in the `.cue`, hidden audio before track 1, pre-emphasis) has been tested on the virtual drive only, not on a real disc with such gaps. The encoders and output formats are otherwise tested on the virtual drive only; the built-in FLAC decoder (used for burning) is also checked against files made by the official flac 1.5.0 encoder |
| AccurateRip verification + read-offset correction | ✅ | ✅ | Detects your drive's offset from a disc in the AccurateRip database; optional opt-in CUETools Database (CTDB) track and disc CRC-32 lookup (`--verify-ctdb`, off by default) that also repairs a rip from CTDB parity and keeps the fix only when AccurateRip then matches. On a real CD the CTDB check matched the database and its repair data matched IgnisDisc's own calculation; the repair has fixed a real rip with damage added afterwards (zeroed sectors, flipped samples) against the live database, confirmed by AccurateRip, but has not yet met a physically scratched disc |
| Secure audio ripping (C2 error pointers + re-reads) | ✅ | ✅ | Flags suspect sectors; can be slow on badly scratched discs |
| Archive Studio: span folders across discs | ✅ | ✅ | SHA-256 manifest on every disc; optional PAR2 recovery data (needs `par2.exe`) fills the space left on each disc, split over an inner, middle and outer zone with a copy of the volume descriptor in each; `recover` copies the files off a damaged archive disc and rebuilds them, also with sector 16, the first UDF anchor and one whole zone destroyed (tested on images). An archive burn can also write a HEALTH.json and HEALTH.svg report, and `check-disc --compare` sets a later read against it; this has only been tested on the virtual drive, not run on a real drive |
| Quick / full erase of rewritable media | ✅ | ✅ | |
| Re-burn a used DVD+RW / DVD-RAM | ✅ | ✅ | Asks before replacing the files on the disc (`--overwrite` on the command line); finalized CD-RW / sequential DVD-RW require `erase` first; a DVD-RW written by Windows' writer is in overwrite mode and is written over like a DVD+RW |
| Surface scan | ✅ | ✅ | Per-track Full TOC (`0x02`) scan skips unrecorded multi-session gaps and auto-selects 2352B raw mode on audio tracks |
| Append a session (multi-session) | ✅ | ✅ | Open and appended sessions omit UDF 1.02 so Windows mounts Joliet across all sessions; names longer than 64 characters are shortened there, and the burn result says so. With the Windows writer on real discs: a DVD-R and a DVD+R, two sessions each, every file of both sessions identical; the DVD+R showed the new session at once, the DVD-R only after the disc was put back in |
| Bootable discs (El Torito BIOS + UEFI) | ✅ | ✅ | Hybrid BIOS + UEFI Boot Catalog verified via physical burn, readback, and rip; boot behaviour depends on your loaders |
| DVD-Video authoring | ⚠️ | ✅ | Requires `ffmpeg.exe` (the 1-click download). Long movies are compressed to fit: before converting, the DVD-Video screen reads each movie's running time and says whether they fit a 4.7 GB DVD at full quality, compressed (with the video bitrate and how it will look), only on a dual-layer DVD, or on no DVD at all (Burn is then off). When a 4.7 GB DVD would have to compress and a dual-layer DVD would keep a clearly higher bitrate, it offers both (for example 3,587 kbps or full quality for a 2 h 20 min film), and a burn is refused before converting when the disc in the drive is too small. Each film becomes a title with a chapter every 5 minutes, English subtitles from an `.srt` beside it (or generated with the optional subtitle tool; the DVD-Video screen shows each movie's subtitle source with *Choose .srt...* and *No subtitles*), and a title menu with a still from each film and Play / Play with subtitles buttons. The navigation tables follow libdvdread's structures and were checked with libdvdnav (mpv, VLC). Played on one stand-alone player so far, an Xbox Series X: a film with menu, chapters and subtitles on a DVD+RW, and two films on a dual-layer DVD+R played across the layer change. Discs authored before 2026-11-14 did not play there. The authoring options (autoplay, action after a film, chapter interval, subtitles, video quality, title menu) have been checked in the test suite only; no disc made with a non-default choice has been burned or played yet |
| Generate subtitles for DVD-Video (speech recognition) | ⚠️ | — | Optional 1-click download, nothing bundled: whisper.cpp 1.9.5 and its models, every file pinned by SHA-256 (`install-subtitle-tool`, or the button on the DVD-Video screen). On a PC with an NVIDIA GPU and driver 551.61 or newer it gets the CUDA build and the large-v3-turbo model (2.3 GB download); every other PC gets the processor build and the smaller base model (160 MB). AMD and Intel GPUs are not used: whisper.cpp publishes no Windows GPU build for them. Measured on one PC, on an 86-minute animated feature with English dialogue, against its human-made subtitles: on an RTX 3090, 3.3 min from movie file to .srt (26 times real time) with a word error rate of 24.9%; processor build only (16-core desktop processor, base model), 3.4 min and 34.3%. The large model on that processor ran at 1.7 times real time (178 s for 5 minutes, about 50 minutes for this film), so the better model needs a GPU. The error rate counts wording the human subtitles shorten too; an invented place name was never heard right. The subtitles are machine-generated: check them before relying on them. `burn-dvd --generate-subtitles` ("Generate subtitles" on the DVD-Video screen) makes them for movies with no .srt beside them and puts them on the disc as its subtitle track. Authored end to end on the test PC from a 5-minute excerpt: the generated lines appear as the DVD subtitle track and on the menu's "Play with subtitles" button. A full-length film with generated subtitles was burned to a worn DVD+RW on one drive (LG GP60NB50, 2026-11-14), but the read-back check of that disc failed, so it is not counted as a good burn; no disc with generated subtitles has been played in a stand-alone player yet. Muxing the subtitle track needs a recent FFmpeg (the 1-click download): a 2021 build crashed on it |
| Blu-ray (BD-R / BD-RE) | ❌ | — | Not yet: Blu-ray discs are recognised (type, size, layers) and archive sets can be planned for them, and the experimental direct writer handles single-layer BD-R and BD-RE in the drive simulator, but burning one has never been tried on a real drive |
| Burn with direct drive commands (experimental) | ⚠️ | ✅ | Audio CDs Session-at-Once and data/video discs without the Windows writer (`--native` on the command line, or "Direct drive commands" in the app — on by default in the app for audio CDs, falling back to the Windows writer if the drive does not offer Session-at-Once, for dual-layer DVD-Video, and for a DVD-RW, in the app and with burn-data and burn-iso, where a quick-erased disc that cannot be left open still goes to the Windows writer (a Settings switch, or --windows-writer on the command line, turns the DVD-RW default off); off by default elsewhere); the disc is always read back and compared. Run on real hardware on one drive so far (LG GP60NB50), on CD-R and CD-RW. Mixed-mode CDs (a Mode 1 data track, then audio) from a `.cue` file too: one CD-RW so far, where Windows read the data track's files back identical. A Mode 2 / CD-ROM XA data track (`MODE2/2352` or `MODE2/2336`) as track 1, and the write-offset correction on the audio part of a mixed-mode disc, have run only in the drive simulator, not on a real disc. Blank single-layer DVD-R, DVD-RW and DVD+R can also be written this way. A further session on a CD-R, CD-RW, DVD-R, DVD-RW or DVD+R left open is added the same way: on real discs on the same drive, three sessions each on a CD-RW, a CD-R and a DVD+R and four on a DVD-R, every file of every session identical (the first DVD-R try was refused with nothing written, because the drive rejects the writer's old track query on DVD; fixed before these runs); on a DVD-RW too, which must be fully erased before it can be left open: a second session added and the disc closed, every file of both sessions identical (Windows showed the new session after the disc was put back in). On real hardware so far two DVD-Rs on the same drive (Verbatim): one closed in one pass (372 MB), one left open, after which Windows' own writer added a second session; every file of each session identical when read through Windows. Two DVD+RWs too (Verbatim, 372 MB each, every file identical): a used one written over with leave to replace its data, and a new one, formatted first by the drive. A single-layer DVD+R too (Verbatim, 198 MB, finalized by the drive, every file identical). A DVD-RW too (Ritek): closed in one pass after a quick erase (63 MB; the drive took 6 minutes to close it), left open after a full erase (200 MB), and in overwrite mode written over past its formatted part, which the writer expanded first (230 MB); every file identical each time. A DVD-Video disc was written to a used DVD+RW this way too (a 60 s test card, 44 MB): every sector read back and the title decodes in full, this test card was not played in a stand-alone player (a later film disc was; see DVD-Video authoring). A dual-layer DVD+R too (Verbatim, the Ubuntu 26.04 image, 6.5 GB, with the layer break moved to where it was asked for; the drive reported it there and the disc's SHA-256 equalled the image). A two-film DVD-9 (103 and 86 minutes) went onto a dual-layer DVD+R the same way, with the layer break on the cell where the second film starts: every sector read back, the drive reported the break there, and an Xbox Series X played through the first film into the second across the layer change without a stop. Dual-layer DVD-R (layer jump recording, on drives that offer it), DVD-RAM, single-layer BD-R and BD-RE, leaving an audio CD open for a second data session (CD-Extra), that session's optional Blue Book layout (off by default: CD-ROM XA Mode 2 Form 1 with CDPLUS/INFO.CDP and PICTURES), and the optional Audio CD PAR2 recovery session (`--recovery-session` / `rip-audio --repair`) have run only in the built-in drive simulator, not on a real disc. Dual-layer DVD-R on a drive without layer jump recording, and multi-layer Blu-ray discs, still use the Windows writer |
| CD-Text / ISRC on burned audio CDs | ⚠️ | ✅ | The Windows writer cannot write lead-in CD-Text. The direct writer (on by default on the Audio CD screen when the drive offers Session-at-Once, or `--native` on `burn-audio`) writes CD-Text, ISRC and catalog codes, then reads the disc back and compares it. Run on real hardware on one drive so far (LG GP60NB50): a CD-RW and a CD-R, where CD-Text, ISRC and catalog codes and the audio all read back correctly; not yet checked in a stand-alone CD player |
| DVD+R book type (bitsetting) | ❌ | ✅ | Needs drive-vendor-specific commands; reports "not supported" instead of pretending |
| Test-write / simulation | ⚠️ | ✅ | With the experimental direct writer (`--native --test-write`): the whole burn with the laser off. Run on one drive (LG GP60NB50) on CD-R and CD-RW. DVD-R has passed a test write on the same drive (a Verbatim DVD-R, still blank after a reload); DVD-RW only in the drive simulator so far; DVD+R discs have none. The Windows writer has no test write |
<!-- SUPPORT-TABLE-END -->

✅ supported · ⚠️ works but needs more real-hardware testing · ❌ not available on physical drives yet

Reading from physical drives uses SCSI pass-through when Windows allows it and falls back to standard volume reads
(data) or `IOCTL_CDROM_RAW_READ` (audio) otherwise, so no administrator rights are needed.

---

## Install

Download `IgnisDisc-Setup.exe` (per-user install, no admin rights) or the portable `.zip` from the Releases page.
The builds are not code-signed yet, so Windows SmartScreen may show a warning on first run; check the SHA-256 checksum
listed on the release page first. IgnisDisc has no telemetry. It goes online only to look up an audio CD
(MusicBrainz titles and AccurateRip checksums — both on by default when ripping and switchable off; CUETools Database
(CTDB) checksums and repair data — strictly off by default until you turn it on) and, if you turn it
on, to check GitHub for a newer release. [SECURITY.md](SECURITY.md) lists exactly what is sent.

---

## Command-line usage

```text
ignisdisc-cli <command> [options]

  drives [--json]                              List optical drives (physical and virtual)
  media-info --drive <id>                      Disc status, capacity, sessions, speeds
  build-iso  --input <folder> --output <file.iso> [--label <vol>] [--udf] [--joliet]
             [--boot-bios <img>] [--boot-uefi <img>] [--fixed-time <unix|iso8601>] [--sha256sums]
             [--blake3sums] [--udf-rev <1.02|1.50|2.00|2.01|2.50|2.60>]
                                               --udf-rev picks the UDF revision (default 1.02; 2.50/2.60 add the
                                               metadata partition BD-Video needs);
                                               --fixed-time (or SOURCE_DATE_EPOCH) gives byte-identical images;
                                               --sha256sums adds a root SHA256SUMS that `sha256sum -c` can check;
                                               --blake3sums adds a BLAKE3SUMS that `b3sum -c` can check
  burn-iso   --drive <id> --iso <file.iso|.nrg|.mdf|.mds|.ccd|.img|.cdi> [--speed <X|max>] [--no-verify | --strict-verify] [--no-close] [--no-burnfree] [--overwrite] [--post-action none|eject|sound|eject-sound|shutdown]
             [--native [--test-write | --test-first] [--layer-break <sectors>] [--log-dir <folder>]]
  burn-data  --drive <id> --input <folder> [--label <vol>] [--speed <X|max>] [--udf] [--joliet]
             [--boot-bios <img>] [--boot-uefi <img>] [--no-verify | --strict-verify] [--no-close] [--append] [--no-burnfree] [--overwrite] [--post-action none|eject|sound|eject-sound|shutdown] [--fixed-time <...>]
             [--sha256sums] [--blake3sums] [--udf-rev <...>] [--fill-picture <file.png> [--disc-preset <id>]] [--native [--test-write | --test-first] [--layer-break <sectors>] [--log-dir <folder>]]
  burn-audio (--drive <id> | --plan-only) (--tracks <a.wav,b.flac,album.cue,list.m3u,...> | --tracks-file <file.json>) [--title <album>] [--artist <name>]
             [--songwriter <name>] [--upc <13digits>] [--speed <X|max>] [--dao] [--pregap <frames>] [--normalize] [--rms-level] [--loudness track|album|off] [--crossfade <sec>]
             [--no-cd-text] [--no-burnfree] [--post-action none|eject|sound|eject-sound|shutdown] [--allow-damaged] [--native [--test-write | --test-first] [--recovery-session | --fill-picture <picture>|auto [--lookup]] [--write-offset <n|auto>] [--log-dir <folder>]]
  burn-dvd   --drive <id> --video-ts <folder|movie> [--label <vol>] [--menu-title <title>] [--no-menu] [--standard ntsc|pal] [--aspect 16:9|4:3] [--chapters <sec>] [--dual-layer]
             [--generate-subtitles [--subtitle-language <code|auto>]] [--speed <X|max>] [--no-verify] [--no-burnfree] [--overwrite] [--fill-picture <file.png>|auto] [--post-action none|eject|sound|eject-sound|shutdown]
             [--native [--test-write | --test-first] [--log-dir <folder>]]
  burn-queue --drive <id> --images <f1.iso,f2.iso,...> [--speed <X|max>] [--no-verify | --strict-verify] [--no-close] [--no-burnfree] [--overwrite] [--continue-on-error] [--wait-minutes <n>] [--post-action none|eject|sound|eject-sound|shutdown]
  image-info --image <file.iso|.nrg|.mdf|.mds|.ccd|.img|.cdi|.bin|.cue>
                                               Inspect a disc image's format, payload offset, sectors, and tracks
  convert-image --input <file> --output <file> [--cue <file.cue>] [--overwrite]
                                               Convert legacy disc images (NRG/MDF/MDS/CCD/IMG/CDI) or BIN+CUE to ISO, or ISO/legacy to BIN+CUE
  author-dvd --input <folder|movies> --output <folder|file.iso> [--label <vol>] [--menu-title <title>] [--no-menu] [--standard ntsc|pal] [--aspect 16:9|4:3] [--chapters <sec>] [--dual-layer]
             [--generate-subtitles [--subtitle-language <code|auto>]] [--overwrite]
                                               Author a DVD-Video VIDEO_TS folder or ISO without burning
  jewel-cover (--tracks <files> | --tracks-file <file.json>) --output <file.html> [--title <album>] [--artist <name>] [--songwriter <name>] [--upc <13digits>] [--dao] [--pregap <frames>] [--overwrite]
                                               Generate a printable HTML CD jewel-case cover and tray inlay
  audio-plan (--tracks <files|.cue|.m3u|.pls> | --tracks-file <file.json>) [--title <album>] [--artist <name>] [--dao] [--pregap <frames>] [--normalize] [--rms-level] [--loudness track|album|off] [--crossfade <sec>] [--media 74|80]
                                               Show resolved audio CD track lengths, pre-gaps, normalization gain, and 74/80-min fit
  rip        --drive <id> --output <file.iso|file.bin> [--raw]
  rip-audio  --drive <id> --output <folder> [--format wav|flac|mp3|opus|aac[,...]] [--bitrate <kbps>] [--tracks 1,3,5] [--lookup]
             [--release <n|id>] [--library <folder>] [--naming <template>] [--mp3-bitrate <kbps> | --mp3-quality V0..V9]
             [--opus-bitrate <kbps>] [--aac-bitrate <kbps>] [--no-cover-lookup] [--no-folder-image] [--overwrite]
             [--read-offset <samples>] [--verify-accuraterip] [--verify-ctdb] [--no-ctdb-repair] [--no-secure] [--repair] [--continue-on-error] [--cover <image>]
                                               --repair rebuilds damaged audio from a session-2 PAR2 recovery session;
                                               exit code 2 when sectors may be wrong (still suspect or unreadable)
  convert-audio --input <file|folder>[;...] --output <folder> --format flac|mp3|opus|aac[,...] [--recursive]
             [--naming <template>] [--mp3-bitrate <kbps> | --mp3-quality V0..V9] [--opus-bitrate <kbps>] [--aac-bitrate <kbps>] [--overwrite]
                                               Convert audio files on disk, keeping tags and cover; never writes over an input
  detect-offset --drive <id>                   Find the drive's audio read offset (AccurateRip)
  copy-disc  --source <id> [--target <id>] [--copies <n>] [--speed <X|max>] [--raw] [--native [--log-dir <folder>]]
             [--allow-suspect] [--keep-image <folder>] [--wait-minutes <n>]
                                               Copy a disc with one drive (the tray opens, it waits for a blank) or two;
                                               stops before writing if source audio still reads wrong
  check-disc --drive <id> [--compare <file>]   Read a CD once with C2 error pointers: where it is weak (may skip); --compare sets it against a HEALTH.json
  archive-stage --input <folder> (--staging <folder> | --plan-only) [--media cd|dvd|dvd-dl|bd|bd-dl|bdxl|bdxl-ql]
             [--redundancy <min %>] [--no-fill]      Recovery data fills each disc; --no-fill keeps exactly that %
             [--keep-folders | --order size|path]    Largest-first packing (default); whole folders; or path order.
                                                     Every disc gets CATALOG.JSON and INDEX.HTML listing the whole set
  archive-burn --drive <id> --disc <staged disc folder> [--speed <X|max>] [--no-verify | --strict-verify] [--no-burnfree] [--overwrite] [--post-action none|eject|sound|eject-sound|shutdown]
             [--native [--test-write | --test-first] [--log-dir <folder>]] [--health-certificate]
                                               Burn one staged archive disc; --native uses direct drive commands; --health-certificate writes discNN.HEALTH.json + .svg (off by default)
  archive-verify --path <folder|drive>         Check an archive disc against its SHA-256 manifest
  recover (--drive <id>|--image <file.iso>) --out <folder>
                                               Copy a damaged archive disc's files out, rebuild them from its
                                               PAR2 recovery data and check them (exit code 2 if any are lost)
  repair-par2 --path <folder> [--delete-backups]
                                               Run PAR2 repair in place; keeps .1 backup copies unless --delete-backups is passed
  verify     --drive <id> --iso <file.iso|.nrg|.mdf|.ccd|.img|.cdi>
  verify     --drive <id> --cue <file.cue> [--title <album>] [--artist <name>] [--no-cd-text] [--write-offset <n|auto>]
                                               Check an audio or mixed-mode CD burned with --native against its .cue,
                                               without writing (track table, CD-Text, codes, every sample)
  erase      --drive <id> [--full]
  scan       --drive <id> [--block-size <sectors>] [--csv <file.csv>]
  book-type  --drive <id> [--set dvd-rom|dvd+r|dvd+rw|dvd+r-dl|dvd-r|dvd-rw|dvd-ram]
                                               Query or set the DVD+R / DVD+R DL book-type bitsetting on supported drives
  drive-report --drive <id>                    Build a drive compatibility report (capabilities and media ID)
  eject      --drive <id>                      Open the tray
  tools                                        Check availability of optional tools (ffmpeg, par2, whisper.cpp)
  install-ffmpeg [--plan-only]                 Optional download for FFmpeg (audio/DVD tools), SHA-256 pinned
  install-par2   [--plan-only]                 Optional download for par2cmdline (archival recovery), SHA-256 pinned
  install-subtitle-tool [--plan-only]          Optional download for subtitles: whisper.cpp and speech models, SHA-256 pinned
  settings (get | set <key> <value> | reset) [--settings-file <path>]
                                               Inspect, update, or reset IgnisDisc persistent settings
  check-update                                 Check GitHub Releases for a newer IgnisDisc version
  generate-subtitles --input <movie> [--output <file.srt>] [--language <code|auto>] [--ffmpeg <path>]
                                               Machine-generated subtitles by speech recognition (see below)
  load       --drive <id>                      Close the tray and wait until the drive is ready; on a drive that
                                               cannot close its own tray (a spring tray, as on slim USB drives) it
                                               sends nothing, says to push the tray in by hand and exits 1

`eject` and `load` have been tested on the virtual drive only, not on a real drive.

Every command accepts --json for machine-readable output. --drive takes a drive letter (e.g. D:) or the ID
shown by `drives`.
```

`--udf` / `--joliet`: by default an image contains ISO 9660, Joliet and UDF. Passing either flag limits the image to
ISO 9660 plus the file systems you name.

Verify (on by default; `--no-verify` skips it): after the burn every sector is read back and compared with the image. A drive can answer a read from
its own memory instead of the disc, so the check tells it to read the disc itself (the FUA bit) wherever burn data
can still be in that memory: the last part written and the start of the disc, each twice the size of the buffer the
drive reports. The rest is read normally. `--strict-verify` sets that bit on every read instead; it leaves nothing
to the drive's buffer size, and is several times slower (measured on one USB DVD writer: 1.2 MB/s against 3.5 MB/s).
The app has the same choice under Settings ("Strict verify").

Write speed (when `--speed` is not given, and in the app unless Settings says otherwise): the fastest speed the drive
writes at a constant linear velocity for the inserted disc, up to 16x for CD and 8x for DVD, taken from the drive's
own speed list. CAV and partial-CAV speeds rise towards the outer edge, so the drive keeps adjusting the laser; the
slowest speed is not automatically the best either. On a slim USB writer that offers CD-R at 10x CLV, 16x and 24x CAV,
this is 10x.
A small built-in table of media IDs caps that default for discs known to read weakly: the FORNET CD-R (ATIP 97m26s0x) from the batch that failed the read-back check on an LG GP60NB50 (see the status section below). The app reads the media ID when a disc changes and shows the reason next to the speed picker. The cap is tested with unit tests only, and the burn commands on the command line do not apply it yet.
`media-info` shows the choice and why; `--speed <X>`, `--speed slowest` and `--speed max` override it.

`--overwrite`: a used DVD+RW or DVD-RAM is rewritten from the start, so a burn replaces every file on it. Burns refuse
to do that unless you pass `--overwrite`. Sequential rewritable discs (CD-RW, sequential DVD-RW) cannot be overwritten
in place once finalized—run `ignisdisc-cli erase --drive <id>` before burning them again. Windows' writer formats a
blank DVD-RW for overwriting before it writes (seen on a real disc, with or without `--no-close`), so a DVD-RW it has
written is never left open for more sessions and is written over like a DVD+RW; the burn says so when `--no-close` was
asked for. An erase returns the DVD-RW to sequential mode.

`--native` (opt-in on the command line; in the desktop app, direct drive commands start on by default on the Audio CD screen—falling back to the Windows writer with a note if the drive does not report Session-at-Once—and turn on automatically on the DVD-Video screen while the movies need a dual-layer disc): burns the audio CD with IgnisDisc's own drive commands instead of Windows' burning
service, Session-at-Once. It writes CD-Text into the lead-in, ISRC and catalog (UPC/EAN) codes, pre-emphasis and
copy flags from a `.cue` sheet's `FLAGS`, the sheet's `INDEX 00` gap marks, and audio in front of track 1. After
writing it reads the whole disc back and compares the track list, the CD-Text, the catalog and ISRC codes and every
audio sample; the burn only
counts as a success if that check passes. It needs a blank CD-R or CD-RW and a drive that reports Session-at-Once,
and saves a log of every drive command under `%LOCALAPPDATA%\IgnisDisc\logs` (or `--log-dir`).

- `--native --test-write`: the same burn with the laser off; the disc stays blank.
- `--native --test-first`: a test write, then the real burn. Most drives keep showing the simulated session after
  a test write and refuse to record until the disc is reloaded. A drive that says it can close its own tray reloads
  the disc and goes on to the real burn; any other (slim drives with spring trays included) stops after the test and
  says so. The automatic reload has run only in the simulator.
- `--native --write-offset <samples|auto>`: the drive's write offset is corrected so that a rip with the drive's
  AccurateRip read offset gives the source back sample for sample. `auto` (the default) takes the read offset from
  AccurateRip's drive list (downloaded when needed) and the combined offset measured by earlier direct burns on the
  same drive; the first burn on a drive only measures it. `0` turns the correction off.

Status: experimental. On real hardware it has run on one drive (an LG GP60NB50): test writes on CD-R and CD-RW, a
two-track CD-RW with CD-Text, a four-track CD-R with CD-Text, catalog and ISRC codes, pre-emphasis and copy
flags, gap marks and audio in front of track 1, and an eight-track MP3 album on several CD-Rs (the read-back check
failed on two discs of an old batch that read weakly towards the outer edge, and passed on a newer one; a secure rip
of each disc matched the source). A mixed-mode CD-RW (data track, then audio) and its copies on CD-R read back
identical to the source. The write-offset correction was checked on one CD-R: an offset-corrected rip matched the source sample for sample. On a mixed-mode CD the correction moves the audio part only, and a Mode 2 / CD-ROM XA data track 1 is accepted; both have run only in the drive simulator. Other drives have
only been run against the built-in drive simulator, and no disc has been tried in a stand-alone CD player yet.

DVD-RW is the one data disc burned with direct drive commands by default, in the app (Data Disc, Disc Image, Archive
and DVD-Video screens tick "Direct drive commands" by themselves while a DVD-RW is in the drive; your own tick wins)
and with `burn-data` and `burn-iso`, which say so. Windows' writer formats every blank DVD-RW for overwriting, so it
can never add a session to one; the direct writer leaves it open and adds the next session. A quick-erased DVD-RW that
the drive will not leave open still goes to Windows' writer, and the burn says why. Settings → "Use direct drive
commands for DVD-RW" (or `ignisdisc-cli settings set directWritingForDvdRw false`) turns this off in the app;
`--windows-writer` does so for one command.

`--native` on `burn-iso` and `burn-data` (experimental, also "Direct drive commands" on the app's Data Disc and Disc
Image screens) writes the image to a blank CD-R or CD-RW with IgnisDisc's own drive commands: one Mode 1 data track,
Track-at-Once, at a speed picked from the drive's own list for that disc (by default its fastest constant-velocity speed up to 16x, or the `--speed` given,
rounded down to one the drive offers), never left to Windows. The disc is always read back sector by sector from the
disc itself afterwards, even with `--no-verify`. A blank single-layer DVD-R or DVD-RW is written Disc-At-Once (or in
packets when `--no-close` leaves it open) and a DVD+R in sequence, then closed or finalized; this DVD path has
run on one drive so far. On DVD-Rs: a test write, a 372 MB burn closed in one pass, and a disc left open to which
Windows' own writer then added a second session, every file of both sessions reading back identical. On a DVD+R: a
198 MB burn finalized by the drive, every file reading back identical. On a DVD-RW: 63 MB closed in one pass after a
quick erase (the drive took 6 minutes to close it) and 200 MB left open after a full erase, every file reading back
identical. Leaving a DVD-RW open needs a full erase first (`erase --full`, about 30 minutes at 2x): after a quick erase
the drive offers only the one-pass mode, and the burn stops before writing and says so. A CD-R, CD-RW, DVD-R or DVD+R
left open gets its next
session the same way, laid out from the disc's next writable address with the earlier files kept. On real discs on
the same drive: three sessions each on a CD-RW, a CD-R and a DVD+R, and four on a DVD-R, every file of every session
identical when read through Windows and straight from the drive, before and after reinserting the disc. A DVD-RW
left open gets its next session this way too, although Windows reports an open DVD-RW as a disc to write over: the
direct writer asks the drive itself. On a real DVD-RW a second session went 28,688 sectors after the first (as on
DVD-R) and closed the disc; all 13 files of both sessions read back identical, and Windows showed the new session
after the disc was put back in. Windows' own writer refuses that disc without `--overwrite`, because it would write
it over. A dual-layer DVD-R is written with layer jump recording when the drive offers it (simulator only so far). Any
other disc (dual-layer DVD-R on a drive without layer jump, multi-layer Blu-ray, a Blu-ray or dual-layer disc left
open) is burned by the Windows writer as before, and the result says so. A DVD+RW is
written from sector 0 (formatted first if it is new); one that holds data is only written over with `--overwrite`.
A used DVD+RW (written over) and a new one (formatted first) have been burned this way on real hardware. On a real used DVD+RW, a second burn without `--overwrite` was refused before anything was written, by the direct writer and by the Windows writer, and the first burn's files stayed identical.
A DVD-RW in overwrite mode and a DVD-RAM are handled the same way (a partly formatted DVD-RW is expanded first; a
never-formatted DVD-RAM is formatted first). A real DVD-RW that Windows' writer had formatted to 65,264 sectors was
expanded and written over with 230 MB, every file reading back identical; DVD-RAM has run only in the simulator. A single-layer BD-R is written like a DVD+R and
finalized, and a BD-RE is formatted first when new and then written like a DVD+RW; Blu-ray has also only run in the
simulator, and multi-layer Blu-ray discs go to the Windows writer. On a dual-layer DVD+R the layer break is set before
writing: an image that fits on the first layer stays there, a larger one is split in half (the first layer never
smaller than the second), or the break goes where `--layer-break <sectors>` puts it (`burn-iso`, `burn-data`). On real
hardware: the Ubuntu 26.04 image (6.5 GB) on a Verbatim DVD+R DL with the break moved to 1,800,000 sectors; the drive
reported the break there afterwards and the disc's SHA-256 matched the image. For DVD-Video ("Direct drive commands" on
the DVD-Video screen, or `burn-dvd --native`), the break goes on a cell boundary read from the title sets' IFO files,
where players expect the layer change. On real hardware: two films (103 and 86 minutes, authored with `--dual-layer`)
on a Verbatim DVD+R DL with the break where the second film starts; an Xbox Series X played both, and playing on
through the end of the first film into the second crossed the layer change without a stop.
An audio CD written this way can be left open for a second session, and a data session can be added after it (CD-Extra); copying an Enhanced CD whole (audio session, then data session) works the same way. The data session can optionally use the Blue Book layout (`--blue-book`, or "Blue Book layout" in the app; off by default): CD-ROM XA Mode 2 Form 1 with `CDPLUS/INFO.CDP` holding the album and track titles, and a `PICTURES` folder. These, and the optional recovery-data session on an audio CD, have run only in the built-in drive simulator, not on a real disc.
The picture-disc and Rosetta commands (`disc-art generate/preview/burn`, `rosetta build/burn`, including the hybrid DVD-R with an archive inside and text pages outside, and the rim header, which `disc-art generate --rim-header` also draws on CD, DVD and BD-R picture discs, narrowing to the ring and refusing below 50x75 µm cells on CD and 26x34 µm on DVD and BD) have been tested on images and in the drive simulator. One picture disc has been burned on a real drive: a CD-RW in the LG GP60NB50 with `disc-art burn`, using the uncalibrated `cd-80` geometry and a rim header (2026-11-18). No picture showed; the surface had fine concentric stripes, which is what an angle drift from an uncalibrated track pitch or speed looks like, and the read-back stopped partway, so a calibration with `disc-art calibrate` is needed first. No other picture disc has been burned on a real drive, and no result has been looked at under a microscope. The same goes for placing the picture (`--scale`, `--offset-x-mm`, `--offset-y-mm`, `--rotate`, `--fit ring|disc`), the art band between two radii (`--art-inner-radius-mm`, `--art-outer-radius-mm`, which also ends the image early), the title ring with the title and date in large letters at the outer edge (`--title-ring`, DVD), the experimental audio CD picture hidden in the pre-gap before track 1 (`--pregap-art`; some players and rippers can play it as noise), the two-session burn of a picture with the files at the outer edge (`--files-outer`, then `disc-art burn` on a blank DVD-R or DVD+R: the picture session left open, the files laid out again for the drive's next writable address) and the Disc Art page: they were checked on generated images and on the simulated drive only, so where the picture lands on a real disc, and whether a burn stopped at the band's edge reads back normally, has not been seen.
The same holds for "Fill empty space with a picture" on the Data Disc and DVD-Video screens (`burn-data --fill-picture <file.png>`, `burn-dvd --fill-picture <file.png>|auto`; off by default): the files stay on the inner part of a single-layer DVD or BD and a picture is drawn on the rest, so the whole disc is written (a longer burn) and it is closed; appended or open burns refuse the option. `auto` on a DVD-Video takes one frame of the main movie with FFmpeg, and burns without the picture, saying so, when FFmpeg is not installed. In the simulator a filled DVD-R of each kind was read back with every file identical and no picture sector inside the file system.
Audio CDs have the same option, also off by default and also tested only in the simulator ("Fill empty space with a picture" on the Audio CD screen, `burn-audio --native --fill-picture <picture>|auto`): the music session is written as before, then a second session holds a small ISO 9660 volume (`CDPLUS/INFO.CDP` and the picture in `PICTURES`) and Mode 1 data sectors drawn as the picture to the end of the disc, which is then closed. CD players read only the first session, so the track list is the same and the picture is never played as sound. `auto` uses cover art embedded in the music files, else `cover.jpg` / `folder.jpg` next to them, else, only when the online lookup is allowed (`--lookup`, or "Look up the cover online"), the Cover Art Archive front cover of the album found on MusicBrainz by its title and artist; with no picture the disc is burned without it, and the result says so. In the simulator the music session passed `verify --cue`, the track list matched a burn without the picture, and the picture read back from the disc.
`--test-write` and
`--test-first` work as for audio (DVD+R has no test write). On real hardware it has run on one drive so far; other
drives are still to be tried. A second DVD writer (Lite-On iHAS124) has been probed read-only (profiles, write speeds, tray load) and has burned one CD-R with direct drive commands (206 MB, 497 files, read back and every file identical, 2026-11-20); the same data through Windows' writer on a second CD-R took 338 s against 126 s. DVD and audio have not been tried on it.

Examples:

```powershell
ignisdisc-cli drives
ignisdisc-cli build-iso --input "C:\Photos\2026" --output C:\temp\photos.iso --label PHOTOS_2026
ignisdisc-cli burn-iso --drive D: --iso C:\Images\ubuntu.iso --speed 8
ignisdisc-cli burn-data --drive D: --input "C:\Deploy" --label INSTALLER --boot-bios etfsboot.com --boot-uefi efisys.bin
ignisdisc-cli burn-audio --drive D: --tracks "01.wav,02.wav,03.wav" --title "Demo Album" --artist "Me" --dao
ignisdisc-cli rip --drive D: --output C:\Backup\disc.iso
ignisdisc-cli copy-disc --source D: --native
ignisdisc-cli rip-audio --drive D: --output C:\Music\Album --format flac --lookup --verify-accuraterip
ignisdisc-cli archive-stage --input "C:\Photos" --staging C:\temp\archive --media dvd --redundancy 10
ignisdisc-cli archive-burn --drive D: --disc C:\temp\archive\disc01 --native
ignisdisc-cli recover --drive D: --out C:\Rescued\disc01
ignisdisc-cli erase --drive D:
```

### Subtitles by speech recognition (optional)

IgnisDisc can make English (or other-language) subtitles for a movie that has none, with
[whisper.cpp](https://github.com/ggml-org/whisper.cpp) (MIT). It is not bundled: `install-subtitle-tool` or the
*Install subtitle tool (1-Click)* button on the DVD-Video screen downloads whisper.cpp 1.9.5 and its models into
`%LOCALAPPDATA%\Programs\whisper.cpp`, each file checked against a SHA-256 pinned in IgnisDisc. The build is picked
from the display adapters Windows lists: NVIDIA with driver 551.61 or newer gets the CUDA build and the large-v3-turbo
model (2.3 GB); any other PC runs on the processor with the base model (160 MB). AMD and Intel GPUs are not used,
because whisper.cpp publishes no Windows GPU build for them.

```powershell
ignisdisc-cli install-subtitle-tool --plan-only          # what would be downloaded for this PC, and why
ignisdisc-cli install-subtitle-tool
ignisdisc-cli generate-subtitles --input "D:\Movies\Film.mp4" --output "D:\Movies\Film.srt"
ignisdisc-cli burn-dvd --drive E: --video-ts "D:\Movies\Film.mp4" --generate-subtitles
```

FFmpeg extracts the first audio track, whisper.cpp writes the subtitles with voice activity detection (so music and
silence are skipped instead of filled with invented lines), and long or repeated lines are tidied. The result is kept in
`%LOCALAPPDATA%\IgnisDisc\SubtitleCache`, never next to the movie, and reused while the movie file is unchanged.
`burn-dvd` only generates subtitles for movies with no `.srt` beside them, and puts them on the disc as its subtitle
track with a "Play with subtitles" menu button (authored end to end from a 5-minute excerpt on the test PC). A full-length film with
generated subtitles was burned to a worn DVD+RW on one drive (LG GP60NB50, 2026-11-14), but the read-back check of
that disc failed; no disc with generated subtitles has been played in a stand-alone player yet. Muxing the subtitle track needs a recent FFmpeg, such as the 1-click
download: a 2021 build crashed on it.

Measured on one PC, on an 86-minute animated feature, against its human-made subtitles:

| Hardware | Model | Movie to `.srt` | Word error rate |
| --- | --- | ---: | ---: |
| NVIDIA RTX 3090 | large-v3-turbo | 3.3 min (26x real time) | 24.9% |
| 16-core desktop processor, no GPU | base | 3.4 min | 34.3% |

The error rate also counts places where the human subtitles shorten the dialogue. Names suffer most: an invented place
name was never heard right. The large model on that processor ran at 1.7 times real time (178 s for 5 minutes of
audio, about 50 minutes for this film), so the better model needs a GPU; a laptop processor will be slower than the
figures above. Read the subtitles
before relying on them.

---

## Desktop app

Workspaces: Data Disc (including bootable discs and Save as ISO), Audio CD (burn, and rip with AccurateRip / secure
mode / read-offset detection), DVD-Video, Disc Image (burn/rip/verify `.iso` and `.bin/.cue`), Archive (plan, stage,
burn, verify and repair multi-disc archives), Drive Utilities (erase, scan, media inspector), Virtual Drive manager,
and Settings.
Projects save as `.cdbproj` JSON files.

---

## Building from source

Requirements: Windows 10 1903+ or Windows 11, x64, and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build CdBurner.sln -c Release
dotnet test  CdBurner.sln -c Release
```

The `Core` test project also runs on Linux/macOS (`-p:EnableWindowsTargeting=true`); hardware and WPF tests need Windows.

Build the installer and portable zip:

```powershell
pwsh scripts/Build-Installer.ps1
# optionally bundle ffmpeg (an LGPL build is recommended) together with its license:
pwsh scripts/Build-Installer.ps1 -FfmpegPath C:\tools\ffmpeg\bin\ffmpeg.exe -FfmpegLicensePath C:\tools\ffmpeg\LICENSE.txt
```

### Project layout

| Project | Contents |
| --- | --- |
| `src/CdBurner.Core` | File system builder (ISO 9660 / Joliet / UDF / El Torito), audio, CUE, DVD-Video, persistence, services |
| `src/CdBurner.Hardware` | IMAPIv2 COM interop, SCSI pass-through, physical sector reader, virtual drive simulator |
| `src/CdBurner.Cli` | `ignisdisc-cli` / `cdburner-cli` |
| `src/CdBurner.App` | WPF desktop app (`IgnisDisc.exe`) |
| `tools/CdBurner.Setup` | Per-user installer / uninstaller |
| `tests/*` | xUnit tests (virtual drive, image validation with an independent reader, CLI and view-model tests) |

---

## Roadmap

- Direct drive-command writing tried on real DVD-RAM, dual-layer DVD-R and Blu-ray discs (CD-R, CD-RW, DVD-R, DVD-RW, DVD+R, DVD+RW and dual-layer DVD+R have run on real discs)
- More drive models and stand-alone CD player checks (including CD-Extra and Audio CD PAR2 recovery sessions on real discs)
- Blu-ray burning, tested on real drives
- Picture discs (`disc-art`) and the Rosetta hybrid DVD-R burned and read back on a real disc
- CUETools Database (CTDB) parity repair tried on a physically scratched disc (it has repaired a real rip with damage added afterwards, against the live database)

---

## Code signing policy

Free code signing on Windows is provided by [SignPath.io](https://signpath.io), certificate by the
[SignPath Foundation](https://signpath.org) (application and CI pipeline configured; current release binaries are
unsigned until certificate provisioning completes).

- **Committers, reviewers, and approvers:** [tether-works](https://github.com/tether-works) maintainers. External pull
  requests require maintainer review before merge, and release builds are produced exclusively from tagged commits by
  `.github/workflows/release.yml` on GitHub-hosted runners.
- **Privacy policy:** This program will not transfer any information to other networked systems unless specifically
  requested by the user or the person installing or operating it. See [SECURITY.md](SECURITY.md) for full details on
  optional MusicBrainz, AccurateRip, opt-in CUETools Database (CTDB), and release update checks, and the optional tool downloads (FFmpeg, par2, subtitle tool)
  you start yourself.

---

## License

`GPL-3.0-or-later` — see [LICENSE](LICENSE), [TRADEMARKS.md](TRADEMARKS.md), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The source code in this repository is licensed under the GNU General Public License v3.0 or later. Pursuant to Section 7(e) of the GPLv3, the names **"IgnisDisc"**, **"tetherworks"**, **"Tetherworks"**, and the IgnisDisc application icons/logos are trademarks of Tetherworks. You may not distribute modified or unmodified binaries under the name **"IgnisDisc"** without prior written permission.
