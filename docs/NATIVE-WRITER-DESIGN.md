# Native drive-command writer: design proposal

**Status:** partly built. What exists:

- the read-only capability probe (`MmcWriteCapabilityProbe`, `MmcMediaIdInspector`);
- audio CD Session-at-Once in `src/CdBurner.Hardware/NativeWriting/`: test write and real write
  (`NativeSaoAudioWriter`), the read-back check that follows every real write (`NativeSaoVerifier`), CD-Text in the
  lead-in, ISRC and catalog codes, pre-emphasis and copy bits, audio in pre-gaps (INDEX 00) and in front of track 1.
  Reached through `ignisdisc-cli burn-audio --native` (off by default) and the "Direct drive commands" option on the app's Audio CD
  screen (on by default when the drive offers Session-at-Once, falling back to the Windows writer).
- mixed-mode CDs (track 1 a Mode 1 data track, then audio), from a `.cue` file whose track 1 is `MODE1/2352` or
  `MODE1/2048` in a BINARY file, through the same audio path (`burn-audio --native`, the Audio CD screen). The
  Windows writer refuses them. Disc copy (Disc Image screen, with direct drive commands on) copies such a disc whole:
  track 1 is read as 2,048-byte user data up to the next track's 2-second pre-gap, the audio is ripped as usual,
  and both are written by this writer (on one real drive on 2026-11-11: CD-RW to CD-R twice, and CD-R to CD-R once). An Enhanced CD (data after the audio, in a second
  session) is copied whole too, audio session then data session; that has run only in the virtual drive.
- data discs: one Mode 1 track Track-at-Once on a blank CD-R/CD-RW, and one data track on a blank single-layer
  DVD-R, DVD-RW (sequential) or DVD+R (`NativeTaoDataWriter`, DVD half in `NativeTaoDataWriter.Dvd.cs`), through
  `burn-iso` / `burn-data --native` and the Data Disc and Disc Image screens. The DVD path has run on real discs for
  DVD-R, DVD+R, DVD+RW and DVD-RW (see the results table).
- Exclusive access opens the drive by its device interface path (`\\?\…{53f56308-…}`): the cdrom driver refuses
  the lock (ERROR_INVALID_HANDLE) on a handle opened by drive letter, because that handle belongs to the mounted
  file system.

**What has run where.** Everything above passes against the virtual drive (`VirtualMmcDrive`). On real hardware
(see Test hardware) the test write has run on CD-R and CD-RW, and two real audio writes have run and read back
identical: two tracks with CD-Text on a CD-RW, and four tracks on a CD-R with CD-Text, catalog and ISRC codes,
pre-emphasis and copy bits, audio in pre-gaps and audio in front of track 1. A mixed-mode CD (a data track, then two
audio tracks) has been written on a CD-RW and read back identical, and again on a CD-R with the write-offset
correction on (2026-11-21, results table). Audio writes and DVD writes have run on one drive model only (a second drive, the
Lite-On iHAS124, has written a data CD-R); no disc has
been played in a stand-alone CD player yet.

Data discs have also run on the same drive (results table): Track-at-Once on CD-RW (24 MB and 690 MB, every sector read
back); a DVD-R written in one pass, a DVD-R left open and given a second session by the Windows writer, and a DVD-R test
write; a used and a new DVD+RW; a DVD+R finalized by the drive; three added sessions on a CD-RW, a CD-R and a DVD+R and
four on a DVD-R, every file of every session identical; a 60 s DVD-Video test disc on a DVD+RW, read back and decoded
in full; and a dual-layer DVD+R with the layer break where IgnisDisc was told to put it (the Ubuntu 26.04 image, 6.5 GB,
SHA-256 of the disc equal to the image). Also real: a two-film DVD-9 on a dual-layer DVD+R with the break on the second film's first cell, played on an Xbox Series X across the layer change. Real since 2026-11-15: DVD-RW closed (Disc-At-Once), left open (incremental) and in restricted overwrite mode (expanded with 13h). A second session added to a real DVD-RW the same day. Still simulator only: DVD-RAM, dual-layer DVD-R, Blu-ray, CD-R overburning, DVD+R book type (bitsetting), DVD-RW test write, a Mode 2 / XA data track in a mixed-mode CD, the open audio session with a data
session after it (CD-Extra) and copying an Enhanced CD whole, the Audio CD recovery session, the radial spread of archive recovery data, the archive
`HEALTH.json` certificates, `eject` and `load` on any tray other than the Lite-On iHAS124's motorised one (where `eject` opened the tray when clear and returned `4/53/00` when blocked, and `load` closed it, 2026-11-19 to 21), CD picture tracks added after a base `.cue`, and the picture-disc options not covered by the burns noted below: `disc-art burn` (including
the two-session burn of a picture with the files at the outer edge on DVD-R and DVD+R, the rim header drawn on calibrated CD, DVD and BD-R picture discs, and the experimental audio CD
picture in the pre-gap before track 1), the picture fill on data, DVD-Video and audio CD burns, and the Rosetta hybrid
DVD-R. Picture discs have run on the GP60NB50 on CD-RW (2026-11-18 with `--rim-header` and no calibration, no picture; 2026-11-22 placed from a turn table measured by read timing, first recognisable picture), on CD-R (2026-11-22, read-back passed) and on one DVD-R (2026-11-21, a coarse calibration target; write accepted, read-back check stopped at the first sector); see the results table for what each showed.

Phases 5–6 below are still plans.

**First feature (decided 2026-11-08):** audio CD Session-at-Once with CD-Text.

## Why

IgnisDisc burns through Windows' IMAPIv2 service. IMAPIv2 cannot:

- write CD-Text into the lead-in of an audio CD;
- do a test write (laser off);
- write audio inside pre-gaps (INDEX 00), hidden-track-one audio, the pre-emphasis flag, or ISRC / MCN in the subcode;
- set the layer break of a DVD±R DL disc, overburn, or change the DVD+R book type.

ImgBurn and cdrtools do these by sending SCSI MMC commands to the drive themselves. IgnisDisc already reads discs this
way (`Win32ScsiTransport`, `IOCTL_SCSI_PASS_THROUGH_DIRECT`); this proposal adds writing.

## Rules

1. **Off by default on the command line.** Native writing is chosen per burn (CLI `--native`; in the app it is on by default for audio CDs when Session-at-Once is offered, for dual-layer DVD-Video, and for DVD-RW in the app and `burn-data`/`burn-iso`). Elsewhere IMAPIv2 stays the default.
2. **Probed drives only.** The drive must report the needed feature: CD Mastering (0x002E) with SAO for audio
   Session-at-Once, plus its R-W bit for CD-Text (`supportsNativeCdText` in `drives --json`); Track-at-Once (0x002D)
   for TAO; DVD-R/-RW Write (0x002F) or the DVD+R feature for DVD.
3. **Test write on offer.** A test write (Test Write bit in mode page 0x05) can be run on its own, or first in the
   same burn (`--test-first`, "Test write first"), on drives that report it. It is not forced: see "After a test
   write" for why most drives cannot go straight on to the real write.
4. **Verify after every native burn.** Not optional in native mode: `NativeSaoVerifier` reads the table of contents
   (track starts, lead-out, pre-emphasis and copy bits), the CD-Text packs and all the audio back and compares them.
   A catalog number and ISRC codes are read with `READ SUB-CHANNEL` (formats 2 and 3) and compared; a code that is
   missing or different fails the check, and a drive that refuses the command is reported as "not checked".
   Audio reads back shifted by the drive's write and read offsets, so the check first finds that shift (searching
   up to 2,352 samples either way from a stretch of real signal) and then compares every sample under it.
5. **Stop before the laser starts.** Any error during setup (exclusive access, `MODE SELECT`, `SEND CUE SHEET`) aborts
   and offers the IMAPIv2 burn instead. Errors after writing has begun fail the burn; they are never retried silently.
6. **Log everything.** Every CDB, transfer length, status and sense key/ASC/ASCQ goes into a log the user can save and
   attach to a bug report.
7. **Simulator first.** The virtual drive gets the same write commands, so each phase passes its tests there before
   it runs on a real disc.

## Command sequences

**Common setup:** take exclusive access (`IOCTL_CDROM_EXCLUSIVE_ACCESS`, so Windows and IMAPIv2 keep out), `TEST UNIT
READY`, `GET CONFIGURATION` (current profile), `READ DISC INFORMATION` (blank? appendable?), `MODE SENSE` page 0x05.

**Data CD, Track-at-Once** (built 2026-11-10, `NativeTaoDataWriter`, `NativeDataBurnEngine`): `GET CONFIGURATION`
RT=2 feature 0x002D (profile CD-R/CD-RW, Track at Once current, test write offered) → `READ DISC INFORMATION`
(status must be blank) → `READ TRACK INFORMATION` track 1 (next writable address must be valid and 0; free blocks
must hold the image, padded to at least 300 sectors, plus 2 run-out blocks; without an answer the last possible
lead-out is used) → `MODE SELECT` page 0x05 (write type 1, track mode 4, data block type 8 = 2,048-byte Mode 1,
multi-session 00 to close the disc or 11 to leave it open, test write as chosen), read back with `MODE SENSE` →
`GET PERFORMANCE` type 3 (the drive's write speeds for this disc) and `SET CD SPEED` with the fastest, or the
user's choice rounded down to a listed speed → `WRITE(10)` of 31 sectors (63,488 bytes) at a time from the next
writable address → `SYNCHRONIZE CACHE` (the track closes) → `CLOSE TRACK/SESSION` close function 010b with Immed,
then `TEST UNIT READY` until the drive is ready → `READ TOC` format 0 (one data track from LBA 0). No `CLOSE TRACK`
is sent: on CD the flush closes a Track-at-Once track. The read-back check is `DataBurnService`'s: `READ (10)` with
FUA over every sector, compared with the image. A test write skips the close and must leave sector 0 unreadable. A
disc that is not blank is never erased by the writer.

**Adding a session (CD-R, CD-RW, DVD-R, DVD-RW, DVD+R left open; built 2026-11-12; run on a real CD-RW, CD-R, DVD-R
and DVD+R 2026-11-13; on a real DVD-RW 2026-11-15, once the direct engine read the open disc itself):** On a real open DVD-RW (Ritek, 2026-11-15) Windows' media status says
overwrite-only with no next writable address (state 0x1, profiles 0x0014 and 0x0013 both current), although the drive
reports profile 0x0014 and disc status 1. `QueryMediaStatus` keeps Windows' answer (the Windows writer would write
such a disc over). Code that plans a burn asks the writing engine through `MediaStatusForWriting.Describe`
(`IWriterMediaStatus`): `NativeDataBurnEngine` then opens the drive read-only and, only for a used DVD-RW that Windows
calls overwrite-only, returns it open when the drive reports profile 0014h, status 1, an empty last session and a
valid next writable address (`NativeTaoDataWriter.ReadOpenSequentialDvdRw`); anything else keeps Windows' answer.
Real run the same day: the second session went to 131,120 (first track 102,432 + **28,688**, the DVD-R gap), CLOSE
TRACK / CLOSE SESSION accepted; READ TOC stale afterwards as on DVD-R (track 2 at 0) until a reload.
the image is built by `IsoUdfImageBuilder` from the disc's next writable address, with the last session's tree
imported (`MultiSessionManager`), as for the Windows writer. Before anything is written the writer checks:
`READ DISC INFORMATION` status 1 (appendable) with an empty last session (byte 2 bits 3–2 = 00b); `READ TRACK
INFORMATION` of the empty next track, asked for by the number `READ DISC INFORMATION` gives (bytes 6 and 11; FFh only
as a fallback, because the GP60NB50 answers 5/24/00 to FFh on a DVD-R), with a valid next writable address equal to
the address the image was built for; and the image's own Primary Volume Descriptor (image sector 16) places its root directory and volume
end inside the new session, so an `.iso` built for sector 0 is refused. Then the blank-disc sequence runs from that
address: Track-at-Once on CD; on DVD-R/-RW always incremental (Disc-At-Once needs a blank disc), multi-session 00 in
page 0x05 when the session closes the disc; DVD+R as above. Afterwards `READ TRACK INFORMATION` of the new track by
number must give its start, then `READ TOC` format 0 as a second opinion, then `DataBurnService`'s FUA read-back
from the session's first sector. `READ TOC` is not trusted on DVD-R: on a multi-session DVD-R the GP60NB50 lists only
the first track and the last session's (numbered 2), and straight after an added session not even the newest one; so
the table of contents used to import the last session (and for ripping and copying) is checked against `READ DISC
INFORMATION`'s track count and, when tracks are missing, rebuilt from `READ TRACK INFORMATION`
(`PhysicalSectorReader.ReadTableOfContentsCheckedAgainstTracks`; the status query does not use it). On CD-R, CD-RW and
DVD+R the table was complete straight after every session. Dual-layer DVD+R, BD-R and overwritable discs are not
appended to (the Windows writer adds the session, and says so). The simulator places the next session as measured on
the GP60NB50 (2026-11-10 to 2026-11-13): on CD as the Orange Book does (11,400 sectors after the first session's
lead-out, 6,900 after later ones); on DVD-R the first session at least to LBA 65,264, then 28,688 sectors, and 6,160
after later sessions; on DVD+R 2,048 sectors after every session, with no minimum. On DVD-RW the gap after the first session measured 28,688 sectors (Ritek DVD-RW on the GP60NB50, 2026-11-15, matching DVD-R); later sessions use the DVD-R rule until measured.

**Data session after an open audio session (CD-Extra; simulator only, 2026-11-13):** the same sequence adds one Mode 1
data track Track-at-Once to a CD whose Session-at-Once audio session was left open. There is no earlier file system
to import: the image is laid out from the next writable address alone. On such a disc the invisible track's number
counts the audio tracks (track 3 after two audio tracks, in session 2), so the writer names the session from `READ
TRACK INFORMATION` byte 3 (session number) rather than from the track number. The simulator numbers tracks and
sessions that way, answers `READ (10)` of the audio with 5/64/00, and places session 2 11,400 sectors after the audio
lead-out starts. The data track is Mode 1 by default; `--blue-book` (burn-audio) and "Blue Book layout" (Audio CD screen) write CD-ROM XA
Mode 2 Form 1 instead, with `CDPLUS/INFO.CDP` and a `PICTURES` folder (simulator only, 2026-11-16). Both layouts are available from the
CLI and the app, and neither has been run on a real disc (to check there: Windows mounts session 2, a CD player plays session 1).

**Audio CD, Session-at-Once with CD-Text:**

1. `MODE SELECT` page 0x05: write type 2 (SAO), Test Write bit as chosen, BUFE on when supported, multi-session 00
   (close the disc), or 11b to leave the disc open for another session (the first session of a CD-Extra disc; the
   writer reads the field back and stops before writing if the drive changed it).
2. `SEND CUE SHEET` (0x5D). One 8-byte entry per index (CTL/ADR, track, index, data form, SCMS, absolute MSF): a
   lead-in entry (track 0), track 1 INDEX 00 at 00:00:00 and INDEX 01 at 00:02:00, then every track's INDEX 00 / 01,
   then the lead-out (track 0xAA). The CTL nibble carries pre-emphasis and copy-permit; ADR 2 / 3 entries carry MCN
   and ISRC. With CD-Text, the lead-in entry's data form says the host sends R-W subcode for the lead-in.
3. With CD-Text, write the lead-in: CD-Text packs are 18 bytes (4 header, 12 text, 2 CRC); four packs fill one
   sector's 96 bytes of raw R-W subcode, 6 bits per byte. The pack set repeats from the lead-in start address in
   `READ DISC INFORMATION` (bytes 17–19, as cdrdao reads it) up to LBA −151. Start MSF 9x:ss:ff converts to LBA as
   `(m × 60 + s) × 75 + f − 450,150`, which is negative. Mode page 0x05 asks for data block type 3 (raw with raw
   P-W) while a CD-Text lead-in is written; if the drive refuses that, block type 0 is tried.
4. `WRITE(10)` of 2,352-byte audio sectors from LBA −150 (the 2-second pre-gap of track 1) to the end of the last
   track.
5. `SYNCHRONIZE CACHE`. The drive writes the lead-out and closes the session itself. With multi-session 11b it also
   writes the B0h pointer (start of the next possible program area, 11,250 sectors after the lead-out starts: 6,750
   lead-out plus 4,500 next lead-in) and C0h, and `READ DISC INFORMATION` then reports status 1 (incomplete); the
   writer checks that status after the read-back. Simulator only so far: `VirtualMmcDrive` answers `READ TOC`
   format 2 with these pointers. Not yet run on a real disc.

The cue sheet data form values (0x00 audio, 0x01 drive-generated lead-in/lead-out, 0x41 lead-in with host R-W),
the pack-to-subcode packing and the lead-in addressing were checked against cdrdao's generic MMC driver. Drives that
reject the standard cue sheet get cdrdao's fallbacks: ISRC entries without control bits, and a CD-Text lead-in entry
without its start time.

**Mixed-mode CD** (built 2026-11-11, same writer and checks as the audio CD): the cue sheet's lead-in entry carries
the first track's control nibble (0x4, data) and the lead-out entry the last track's (audio). Track 1's INDEX 00
and INDEX 01 entries have CTL/ADR 0x41 and data form 0x10 (2,048-byte Mode 1 user data; the drive adds sync,
header, EDC and ECC). Track 1 is written from LBA −150 in 2,048-byte sectors, 32 per `WRITE(10)` (64 KiB): its
2-second pre-gap and a 2-second post-gap after the data are zeros (Yellow Book; libburn's cookbook). The audio
follows in 2,352-byte sectors from the first audio track's pre-gap, at least 2 seconds; no `WRITE` crosses from
one block size to the other. A rip of such a disc keeps the post-gap at the end of track 1, so the `.cue` import
leaves up to 150 trailing zero sectors to the writer, which writes the same zeros: the disc then matches the
`.bin`. The read-back check reads track 1 with `READ CD` sector type Mode 1 (user data only), compares it with
the image and the post-gap with zeros, then finds the audio offset and compares the audio from the first audio
sector. The write-offset correction is applied to the audio tracks only (from the first audio sector to the
lead-out); track 1, its pre-gap and post-gap stay byte-exact. First real mixed-mode disc with a correction:
2026-11-21 on the GP60NB50 (CD-R, results table), audio sent 6 samples later, data track and audio read back
identical.

**Mode 2 / CD-ROM XA track 1 (simulator only, 2026-11-15):** `MODE2/2352` and `MODE2/2336` are accepted as track 1 of a
mixed-mode cue sheet and written with data form 20h (2,336-byte host blocks) and session format 20h, keeping the
Form 1 / Form 2 sub-headers. The read-back check and the rip back were tested byte for byte against the virtual
drive. Not yet run on a real disc.

**Safety checks before the first WRITE** (any failure stops with nothing written): disc profile is CD-R or CD-RW;
the CD Mastering feature is current and offers SAO, test write and (for CD-Text) R-W; the disc is blank; the audio
ends before the disc's last possible lead-out; the drive accepts the write parameters and, read back with MODE
SENSE, reports Session-at-Once and the requested test-write setting; the drive accepts a cue sheet.

**After a test write** the disc must be unwritten. A blank disc status settles it. Some drives (the GP60NB50) report
the simulated session as an open, incomplete session until the disc is reloaded; then `READ CD` of LBA 0 must fail.
A closed disc, or audio readable at LBA 0, means the drive recorded: the run fails and says so.

While it shows that simulated session, the GP60NB50 rejects the real cue sheet with 5/30/06 (measured 2026-11-10).
A spindle stop and start, a load command on the closed tray, a sleep and wake cycle, and a quick blank of a CD-RW
all leave the state unchanged; only reloading the disc clears it. So a burn with a test write first stops after
the test with a message to reload the disc and burn again without the test, unless the drive can close its own
tray. That is read from the Removable Medium feature (0x0003): only a caddy or ordinary tray with both the Load and
Eject bits is cycled (START STOP UNIT eject, then load, then wait until ready and check the disc reads blank), and
the real write follows in the same run (`TrayReload`, since 2026-11-11, simulator only). A pop-up (spring) tray, a
changer, or a tray without the Load bit is never ejected. The GP60NB50 reports tray, Load 0, Eject 1, so it still
stops for a reload by hand.

**Pre-gap audio and flags.** A track's cue sheet entries carry its control bits (0x10 pre-emphasis, 0x20 digital
copy permitted); the lead-in, catalog and lead-out entries carry plain audio control. A pre-gap may end in audio
from the track's own stream (its INDEX 00 area). Track 1's first 150 sectors (before LBA 0) are always silence;
audio after them and before INDEX 01 is hidden audio that players reach by rewinding. From a single-file `.cue`
album the bytes on the disc stay in file order; only the track marks move.

**Data DVD** (built 2026-11-11; run on real discs for DVD-R, DVD+R, DVD+RW and DVD-RW, see the results table). Sequences from libburn's cookbook (libburnia,
`doc/cookbook.txt`); `SET STREAMING` layout from libburn's `mmc.c`. Every case starts with `GET CONFIGURATION`
(current profile), `READ DISC INFORMATION` (status must be blank) and `READ TRACK INFORMATION` track 1 (next writable
address must be 0, free blocks must hold the image). The image is padded with zeros to whole 16-sector (32 KiB) ECC
blocks and sent 32 sectors (two blocks, 64 KiB) per `WRITE (10)`, or 16 where the adapter refuses 64 KiB. Speed: the drive's `GET PERFORMANCE` list for the disc, set with
`SET STREAMING` (one 28-byte performance descriptor: end LBA = last sector of the disc, read and write size = the
speed in kB over 1,000 ms), then `SET CD SPEED` if the drive refuses that.

- *DVD-R / DVD-RW (sequential), disc closed: Disc-At-Once.* Feature 0x002F current (its test-write and BUFE bits are
  used) → `MODE SELECT` page 0x05: write type 2, LS_V 0, link size 0, multi-session 00, FP 0, track mode 5, data block
  type 8, packet size 0; read back → `SET STREAMING` → `RESERVE TRACK` (ARSV 0, RMZ 0, size in bytes 5–8 = padded
  sectors) → `WRITE (10)` from LBA 0 → `SYNCHRONIZE CACHE`. No close command: the drive closes the disc. The
  reservation is recorded on the disc, so from `RESERVE TRACK` on a failure is reported as a damaged disc, not as
  untouched.
- *DVD-R / DVD-RW, left open (`--no-close`): incremental.* Features 0x002F and 0x0021 current → page 0x05: write
  type 0, LS_V 1, link size 16, multi-session 11b, FP 1, packet size 16, track mode from `READ TRACK INFORMATION`
  byte 5 (normally 5), data block type 8 → `WRITE (10)` → `SYNCHRONIZE CACHE` → `CLOSE TRACK` (001b, last track in
  last session from `READ DISC INFORMATION` bytes 6 and 11) → `CLOSE SESSION` (010b).
- *DVD+R.* No mode page (the cookbook: none is sent for DVD+R) and no test write → `WRITE (10)` → `SYNCHRONIZE
  CACHE` → `CLOSE TRACK` (001b) → finalize (101b, "minimal radius") for a closed disc, or `CLOSE SESSION` (010b) to
  leave it appendable. The cookbook reports drives that refuse 101b; then the session is closed with 010b and the
  result says the disc is still appendable. The GP60NB50 accepts 101b on a single-layer Verbatim DVD+R
  (2026-11-13): GOOD at once, then 2/04/07 for 79 s while it finalizes.
- A drive that answers 2/04/xx to a close command is waited out and asked again (up to three times), as growisofs
  does for some drives.
- *DVD+RW* (built 2026-11-11; real runs 2026-11-11 on a used and a new Verbatim disc, see the results table; `NativeTaoDataWriter.DvdPlusRw.cs`). No mode page, no test
  write, nothing to reserve. `READ DISC INFORMATION` byte 7 bits 0–1 give the background format status. A disc never
  formatted (0) gets `FORMAT UNIT` (CDB byte 1 0x11; list: byte 1 FOV | Immed = 0x82, byte 3 = 8, blocks
  0xFFFFFFFF, byte 8 = 26h << 2; libburn's `mmc_format_unit`) and is written while the drive formats in the
  background. Size from `READ FORMAT CAPACITIES`. A disc with a volume descriptor at sector 16 (`CD001`, `BEA01`,
  `NSR02/03`) is left alone unless the user allowed replacing its data. `WRITE (10)` from LBA 0, 64 KiB at a time
  (2 KiB alignment is enough here) → `SYNCHRONIZE CACHE` → `CLOSE SESSION` (010b), which stops a background format
  and makes the disc readable elsewhere; a refusal is logged, not fatal. Sectors past a shorter new image keep their
  old contents, as with any overwrite; the new file system ends before them. `DataBurnService` now verifies from
  sector 0 whenever an overwrite-only disc's data is replaced.
- *DVD-RW in restricted overwrite mode and DVD-RAM* (built 2026-11-11; DVD-RW real 2026-11-15, DVD-RAM simulator only and, since 2026-11-21, not to be tried on a real disc; shared with DVD+RW in
  `NativeTaoDataWriter.Overwritable.cs`). DVD-RW: 32 KiB alignment, so the image is padded to whole 16-sector
  blocks. `READ FORMAT CAPACITIES` decides the state (cookbook): formatted with format 13h or 11h still offering
  blocks = partly formatted, expanded first with `FORMAT UNIT` type 13h (list byte 1 0x82, byte 3 = 8, blocks 0,
  byte 8 = 13h << 2, byte 11 = 16, the packet size libburn sets for types 10h–15h), which leaves the intermediate
  state; no current capacity = intermediate already. `CLOSE SESSION` (010b) after writing ends the intermediate
  state. DVD-RAM: 2 KiB alignment; no close command. A never-formatted one (2026-11-13, simulator only; current
  descriptor type 01b in `READ FORMAT CAPACITIES`) gets `FORMAT UNIT` type 00h, the full format of MMC-5 6.5: list
  byte 1 0xA2 (FOV, DCRT so the drive skips the hour-long certification, Immed), byte 3 = 8, the blocks offered for
  type 00h, byte 8 = 00h, bytes 9–11 = the block length 0x000800 (refused if the drive offers another). The writer
  then polls `TEST UNIT READY` through 2/04/04 for up to 90 minutes, showing the progress indication (sense byte 15
  bit 7, bytes 16–17 of 65,536) as "Formatting n %". Real-disc testing of DVD-RAM will not be done (2026-11-21: the medium is out of production;
  any fix comes from a user's bug report and command log). Unchecked on a real disc: whether the drive offers 00h with
  2,048 in its type parameter, whether it accepts DCRT, how long it stays "format in progress", and whether its
  2/04/04 answers carry the progress bytes. Both: no test write, same leave-to-replace rule. The size is the format
  capacity; the track size is used only when the drive reports no format information.
- *Blu-ray* (built 2026-11-11, simulator only; `NativeTaoDataWriter.BluRay.cs`). Both need the BD Write feature
  (0x0041) current: a drive that only reads Blu-ray still reports the disc's profile. BD-R in sequential recording
  mode (profile 41h) goes the DVD+R way (cookbook: "very similar to DVD+R"): no mode page, no test write (MMC has
  none for Blu-ray), the image padded to whole 64 KiB clusters, `WRITE (10)` from the next writable address,
  `SYNCHRONIZE CACHE`, `CLOSE TRACK` (001b), then `CLOSE SESSION` (010b) or finalize with close function 110b
  (DVD+R uses 101b), falling back to 010b if refused. BD-RE (43h) is in the overwritable path: an unformatted disc
  gets `FORMAT UNIT` type 00h with the block count the drive offered for it in `READ FORMAT CAPACITIES` and the
  block length 2,048 in bytes 9–11 (libburn's `mmc_format_unit`); 2 KiB alignment; no close command. Random
  recording BD-R (42h) and BD-ROM are refused.
- *Multi-layer Blu-ray* (built 2026-11-24, simulator only, never tried on a real disc). BD-R DL (50 GB), BDXL triple
  and quad layer (100 and 128 GB) and multi-layer BD-RE report the same profiles as single-layer discs (41h and 43h), so
  the writer path is the same; what is new: (1) `READ DISC STRUCTURE` media type 1, format 00h (BD Disc Information,
  MMC-6 Tables 441 to 443: 4-byte header, 112-byte DI units, byte 3 = layers the unit applies to, disc type "BDR" or
  "BDW" at bytes 8 to 10) gives the layer count; it is advisory, and a drive that does not answer leaves the count
  unknown. Per-layer capacities are the READ FORMAT CAPACITIES / READ TRACK INFORMATION capacity split evenly in whole
  64 KiB clusters (the BD specifications give equal layers: 2 x 12,219,392, 3 x 16,292,864, 4 x 15,625,216 sectors).
  The byte-3 reading is an assumption from the table's wording, to be checked on a real disc. (2) Formatting on request
  (`NativeDataWriteRequest.BdFormat`, `NativeDataBurnEngine.BdFormat`): BD-R type 32h (full format with spare areas) or
  00h (the drive's default), sub-type 01b (SRM without POW), sent as `READ FORMAT CAPACITIES` offered it, only to a blank
  unformatted disc; BD-RE type 00h (default, as before), 30h (spare areas, sub-type 00b) or 31h (no spare areas, only
  where offered; MMC defines no BD-R format without spares). Left on automatic, a BD-R is not formatted by the host.
  (3) Sector counts and capacities that can pass 2^31 bytes (a 128 GB disc is 128,001,769,472 bytes) are 64-bit;
  addresses stay 32-bit, as the 62.5 million sectors of the largest disc fit. The simulator refuses a BD-R `WRITE`
  that does not start and end on a 32-sector cluster.
- *First Blu-ray writer, read-only* (LG BP50NB40, 2026-11-26, "Test hardware" below). Open questions for the paths
  above: with a new, unformatted BD-RE loaded the drive reports 0x0041 **not current**, so the gate stops before
  `FORMAT UNIT`; and it offers format 00h with 0x003000 in the type-dependent field, not the 2,048 the writer sends.
  Neither has been tried on a disc. The byte-3 reading of the DI units does not hold on this drive: two single-layer
  discs gave 0x10 (BD-RE, 2 units returned) and 0x28 (BD-R, 5 units returned), which fits bits 7 to 3 being the number
  of DI units in the block, so the layer count comes out unknown. Byte 12 read 0x14 (BD-RE) and 0x12 (BD-R): a high
  nibble of 1 on both, which may be the layer count; not confirmed without a multi-layer disc.
- *DVD+R DL layer break* (built 2026-11-11; first real disc 2026-11-14, results table). The first WRITE after the break
  is set waits for the drive's calibration of both layers: 61.3 s on the GP60NB50 with a Verbatim DVD+R DL (23 s on a
  DVD+R), so the first WRITE of every run of writes gets a 4-minute timeout and the others 60 s. Written like DVD+R, with the layer break set first, as
  growisofs' `plus_r_dl_split` does (`growisofs_mmc.cpp`, Debian source archive): `READ DISC STRUCTURE` format 20h,
  12 bytes (bytes 0-1 length >= 10, byte 4 bit 7 = break fixed already, bytes 8-11 = layer 0 data zone capacity).
  An image (rounded to 16 sectors) that fits on layer 0 leaves the break alone. Otherwise layer 0 gets
  (blocks / 16 + 1) / 2 * 16 sectors, half rounded up so layer 0 is never smaller than layer 1, or the size given in
  `DataBurnOptions.LayerBreakSectors` (a multiple of 16, at least half the image, within layer 0). It is sent with
  `SEND DISC STRUCTURE` (0xBF) format 20h (byte 1 = 10, bytes 8-11 = layer 0 size) and read back; the read-back
  decides, because some drives answer 5/26/00 yet set the break (cdwrite list). A break fixed elsewhere that would
  have to move, or one the drive will not take, stops the burn before the first `WRITE`.
- *DVD-Video layer break on a cell boundary* (built 2026-11-11; first real DVD-9 2026-11-14 on the GP60NB50 with a Verbatim DVD+R DL, played across the layer change on an Xbox Series X, results table). `DvdCellLayerBreak` (Core) reads
  each title set's cell address table: the IFO's byte 0xE0 holds the VTS_C_ADT sector; 8-byte header (number of VOBs,
  reserved, end address), 12-byte entries (VOB id, cell id, reserved, start sector, last sector) counted within the
  title set's VOB files (mpucoder's IFO reference; libdvdread `c_adt_t`, `cell_adr_t`). The DVD-Video screen, with
  "Direct drive commands" on a dual-layer disc, builds the image, reads where the VTS_nn_k.VOB files landed, places
  each cell start there, and keeps those on a 16-sector block with layer 0 at least half the disc and within its
  capacity; the one at or nearest the cell picked in the picker (else nearest an even split) becomes
  `LayerBreakSectors`. None usable: the burn stops with nothing written.
- *DVD-R DL layer jump* (built 2026-11-13, simulator only). Only when `GET CONFIGURATION` reports feature 0033h
  (Layer Jump Recording) current and the disc is to be closed; otherwise the burn goes to the Windows writer with the
  reason, before anything is sent to the drive. Mode page 05h with write type 4 (layer jump; Pioneer BDR-203/R11
  command references), otherwise set as for incremental writing (LS_V, link size 16, FP, packet size 16, track mode 5,
  data block type 8) and read back. `READ DISC STRUCTURE` 20h gives the layer 0 capacity C of the disc's fixed address
  space; the layer 0 size N is chosen as for DVD+R DL. `SEND DISC STRUCTURE` 23h (Manual Layer Jump Address: byte 1 =
  10, bytes 8-11 = the address, as Windows' `DVD_DUAL_LAYER_MANUAL_LAYER_JUMP`) carries N - 1, the last layer 0 LBA,
  and is read back. Layer 0 is written up to it; the drive then reports its next writable address on layer 1 at the
  same radius, 2C - N (layer 1 runs from the outer edge in), and the rest is written there. CLOSE TRACK and CLOSE
  SESSION (multi-session 00b) close the disc, which then reads in one run from LBA 0 with layer 0 reported as N
  sectors. The address convention (N - 1) and the landing address come from a Plextor PX-760A's report in ImgBurn
  (Doom9 forum thread 113868, 2006); no IgnisDisc run on a real DVD-R DL yet. To check on the first real disc: whether
  the drive takes write type 4 with these page fields, the 23h read-back on a blank disc, the next writable address
  after layer 0, and how long CLOSE SESSION takes. The simulator refuses a write that goes on past the jump on layer
  0 (5/21/02) and `SEND DISC STRUCTURE` 20h on a DVD-R DL (the R11 reference: format 20h does not work there).

## Default write speed

Used by both writers, the app and the command line when no speed is chosen (`WriteSpeedPolicy.Recommended`). It
works from the drive's own `GET PERFORMANCE` list for the inserted disc, whose write speed descriptors carry a Write
Rotation Control field (byte 0, bits 3–4: 0 = constant linear velocity, 1 = CAV; layout as in Windows'
`CDROM_WRITE_SPEED_DESCRIPTOR`). When the drive marks some speeds CLV and others CAV, only the CLV speeds count; from
those, the fastest at or below 16x for CD and 8x for DVD (no ceiling for Blu-ray); with nothing at or below the ceiling,
the slowest. A list marked all one way, or Windows' list without the drive's, is treated as saying nothing about
rotation, and the ceiling alone decides.

Why not simply the slowest speed: CAV and partial CAV change the linear speed, and so the laser power, across the
disc, and reviews of such burns found errors rising late in the disc; but a drive and disc are tuned for a range of
speeds, and nothing published shows the lowest one is the best on current media. No controlled study ranks the
speeds; the measured rule of practice is to scan a drive and disc pair for C1/C2 errors at two or three speeds.

Measured on the GP60NB50 with CD-R (2026-11-11): the drive reports 10x CLV, 16x CAV and 24x CAV, matching the write
modes LG publishes for its GP60NS50 (10x CLV, 16x P-CAV, 24x CAV). Program-area timings agree: asked for 8x it wrote
at 10.1x on average (it rounds up to its lowest CD-R speed); asked for 10x, 10.1x; asked for 16x, 12.2x over the inner
33 minutes, as partial CAV does. So the default on this drive is 10x; on a desktop drive that reports CLV up to 16x it
is 16x. `media-info` prints the default and why. The direct audio writer logs the speed it asked for and the average
speed the drive actually took the audio at.

## Write offset (audio)

Every drive lands audio a fixed number of samples early or late (its write offset W) and reads it back with
another (its read offset R, the figure rippers correct for). The read-back check after a direct audio burn
measures their sum, the combined offset C = W + R: how many samples later the audio reads back than it was sent.
R comes from AccurateRip's drive list (`DriveOffsets.bin`, downloaded when needed and cached for a month in
`%LOCALAPPDATA%\IgnisDisc\cache`; never bundled), matched on the drive's INQUIRY vendor and product. C is kept per
drive in `%LOCALAPPDATA%\IgnisDisc\drive-offsets.json` after each passed check. Then W = C − R, and the writer sends
all the audio −W samples later (across track boundaries; samples pushed past the end of the program area are lost
and the start gains silence), so a rip with the drive's AccurateRip read offset gives the source back sample for
sample. The read-back check compares with exactly what was sent, so it still measures C. The first burn on a drive
measures C and is not corrected; a drive missing from the list is never corrected. `burn-audio --native
--write-offset <samples|auto>` overrides it; 0 turns it off.

List layout, read from the file (339,204 bytes on 2026-11-11, 4,916 records of 69 bytes): a little-endian 16-bit
read offset, the name "VENDOR - PRODUCT" in 33 bytes padded with zeros, a 32-bit submission count (capped at 255),
30 bytes not used here. The GP60NB50 is listed at +6 (255 submissions); C measured 0 on every burn, so W = −6.

## Overburning (CD-R, Session-at-Once only)

Off unless asked for: `AudioBurnOptions.Overburn` (CLI `--allow-overburn [--max-overburn-minutes <n>]`, with `--native --dao`;
the Audio CD screen's "Allow overburn (CD-R)" switch). The lead-out may then start at most 13,500 sectors (3:00, or the
`--max-overburn-minutes` value, at most 20) past the ATIP lead-out start that READ DISC INFORMATION reports, and never past
99:59:74 (LBA 449,849). Refused outright: any DVD or Blu-ray disc, a CD-RW, Track-at-Once (direct data writing records
Track-at-Once, so a data image that does not fit is refused with that reason) and the Windows writer.

Before the first WRITE the writer logs the ATIP capacity and the lead-out the program needs; after the last it logs the exact
number of sectors sent. The Audio CD screen shows the rated capacity against the image size, the excess in MB and mm:ss, and
the risk, and writes only after the user presses Overburn. Many drives refuse a cue sheet past the ATIP (the simulated drive
does unless `VirtualMmcDrive.OverburnLimitSectors` allows it); that refusal is reported before anything is written. Tested in the drive simulator only so far; not yet run on a real CD-R.

## Buffer and underrun handling

Poll `READ BUFFER CAPACITY` (0x5C) while writing to show the drive buffer and keep the host-side queue full. Turn on
BURN-Free / buffer-underrun protection (BUFE bit in page 0x05) when the drive supports it.

## Errors

| Sense | Meaning | Action |
|---|---|---|
| 2/04/xx not ready | still spinning up | wait and retry `TEST UNIT READY`, then give up |
| 5/xx/xx illegal request during setup | drive rejects the parameters | abort before writing; offer IMAPIv2 |
| 3/0C/xx write error | media or drive fault | fail the burn, keep the log |
| 6/28/00 media changed | disc swapped | abort |
| anything else after writing started | unknown | fail the burn, keep the log |

## Testing

- Virtual drive: each command is implemented in `VirtualDiscBackend`, including failure injection (sense codes at
  chosen LBAs), so the error paths are tested.
- Real hardware, in order: test write on CD-RW (done), real write on CD-RW (done), then CD-R (done); DVD after CD:
  a DVD-R test write, then a real DVD-R (done 2026-11-11), DVD+RW (done 2026-11-11), DVD+R (done 2026-11-13), DVD+R DL (done 2026-11-14) and DVD-RW (done 2026-11-15).

## Test hardware

| Drive | Firmware | Connection | Probe result (`drives --json`, 2026-11-08) |
|---|---|---|---|
| HL-DT-ST DVDRAM GP60NB50 | RF02 | USB, slim tray | TAO, SAO, raw DAO, CD-Text (R-W), test write, BURN-Free, layer jump |

Results:

| Date | Drive | Disc | Run | Result |
|---|---|---|---|---|
| 2026-11-08 | GP60NB50 | CD-R, FORNET (ATIP 97:26:07) | Test write, 2 tracks, CD-Text | Write parameters, cue sheet and all 64 WRITEs accepted. Drive showed the simulated session until reload; after reload the disc read as blank with all 359,844 sectors free. |
| 2026-11-10 | GP60NB50 | CD-RW, Mitsubishi (ATIP 97:34:23), quick-erased | Test write, 2 tracks of 20 s, CD-Text | All WRITEs accepted, 14,077 sectors in 46 s. LBA 0 unreadable afterwards: nothing recorded. Drive showed the simulated session (status 1, lead-out 00:42:00). |
| 2026-11-10 | GP60NB50 | same CD-RW, not reloaded | Attempts to clear the simulated session | Stop/start, load, sleep/wake and BLANK (minimal) all left status 1. |
| 2026-11-10 | GP60NB50 | same CD-RW, not reloaded | Real write attempted over the simulated session | Mode page accepted (SAO, test write off, block type 3); SEND CUE SHEET rejected twice with 5/30/06. Stopped before any WRITE; disc unchanged. |
| 2026-11-10 | GP60NB50 | same CD-RW, reloaded (blank again, status 0) | Real write, 2 tracks of 20 s, CD-Text, no test write | Cue sheet accepted, 135 WRITEs, 14,077 sectors with the CD-Text lead-in; closing the session took 20 s. Read-back check passed: table of contents, 9 CD-Text packs and 1,764,000 audio samples identical, combined drive offset 0 samples. 67 s in all. A separate `rip-audio` of the disc gave files identical to the sources and took its names from CD-Text. Disc reports one closed session of 3,000 sectors. |
| 2026-11-10 | GP60NB50 | same CD-RW, quick-erased | Data, Track-at-Once, 24 MB ISO (12,237 sectors), no test write | Feature 0x002D current, track 1 NWA 0 with 359,847 free; mode page TAO / track mode 4 / block type 8 confirmed; GET PERFORMANCE listed 1,764 kB/s and SET CD SPEED 10x was accepted. WRITEs 24 s, SYNCHRONIZE CACHE 5 s, CLOSE SESSION with Immed about 30 s of 2/04/07. TOC: one data track, lead-out at 12,239 (2 run-out blocks). FUA read-back of every sector passed. 61 s in all. |
| 2026-11-10 | GP60NB50 | same CD-RW, quick-erased | Data, Track-at-Once, 690 MB ISO (352,787 sectors) | Data written in 477 s (9.9x average), with close 8 min 52 s; FUA read-back of every sector passed (7 min 15 s). The same image through IMAPI on the same disc and drive the same day took 12 min 42 s to write. |
| 2026-11-11 | GP60NB50 | blank Verbatim DVD-R (MCC 03RG20) | Data, Disc-At-Once, test write only, 372 MB (181,648 sectors) | Features 0x002D/0x002F current; mode page write type 2 / track mode 5 / block type 8 with the test-write bit confirmed; SET STREAMING 8x (11,080 kB/s) and RESERVE TRACK accepted. The first WRITEs answered 2/04/08 for about 30 s, then 11,353 WRITEs of 16 sectors at about 6.3 ms each (2.5x overall with the start-up). SYNCHRONIZE CACHE 122 s. Afterwards the drive reported a closed disc (status 2) with one 181,648-sector track, but sectors 0, 16 and 100,000 all failed with 3/11/00: nothing was recorded. The writer then judged the disc recorded, because it expected the simulated open session (status 1) this drive shows after a CD test write; fixed to read the sectors in both cases. WRITEs are now 32 sectors (64 KiB). |
| 2026-11-11 | GP60NB50 | same DVD-R, after a reload | Same test write, 32 sectors (64 KiB) per WRITE | After the reload the disc read blank again: status 0, track 1 blank with next writable address 0, reads 5/21/00. Free blocks reported 2,298,496 against 2,297,888 before the first test write (608 more; not explained). The test write passed and the writer now reports "the disc was not written" with the reload message. 5,677 WRITEs at about 12.7 ms each: twice the data per command took twice as long, so the drive paces the transfer to the disc, not the bus. Throughput rose with the radius from 3.46x (LBA 9,312) to 3.92x (LBA 148,960), the shape of a constant-angular-velocity write that reaches 8x only near the outer edge; one 3.4 s pause in the middle. 2/04/08 for 33 s at the start, SYNCHRONIZE CACHE 137 s, 249 s in all. |
| 2026-11-11 | GP60NB50 | same DVD-R, after a reload | Data, Disc-At-Once, **real write**, 372 MB (181,648 sectors, 1,005 files), 8x setting, disc closed | Disc blank before (status 0, NWA 0). Same sequence as the test writes without the test-write bit: RESERVE TRACK, 5,677 WRITEs of 64 KiB, SYNCHRONIZE CACHE 122 s; the drive closed the disc itself. Data sent in 117 s with the drive's start-up; 239 s for the writer in all. Afterwards: status 2, one track of 181,648 sectors, TOC one data track; `READ (10)` of LBA 16 gives the ISO 9660 descriptor. The FUA read-back of every sector passed (75 s). Independent check: Windows mounted the disc as UDF, label IGNISDVDR, and all 1,005 files hashed (SHA-256) identical to the source, none missing or extra. `media-info` then named the disc "CD-ROM": its fallback for discs IMAPI will not describe guessed the type from the size. Fixed the same day: it now takes the drive's current profile, which for this closed DVD-R is DVD-ROM (0x0010, the only current profile, read with GET CONFIGURATION), so it shows "DVD-ROM". |
| 2026-11-11 | GP60NB50 | used Verbatim DVD+RW (MKM-A02-00), formatted, holding a 31,055-sector test volume | Data, overwrite, real write, 372 MB (181,648 sectors, 1,005 files), 4x setting | First a burn without `--overwrite`: refused before any drive command that writes, the old volume intact. Then with `--overwrite`: background format status 3, so no FORMAT UNIT; SET STREAMING 4x accepted; 5,677 WRITEs of 64 KiB at about 19.6 ms each with no busy answers, a flat 2.1–2.4x (the drive's other listed speed is 2.4x; the Windows writer ran this disc at about 2.3x the day before); SYNCHRONIZE CACHE 0.2 s; CLOSE SESSION accepted in 12 ms. 122 s in all. FUA read-back of every sector passed (79 s). Windows remounted the disc by itself as UDF "IGNISDVDRW", and all 1,005 files hashed identical, no old files visible. |
| 2026-11-11 | GP60NB50 | blank Verbatim DVD-R (MCC 03RG20) | Data, incremental, disc left open (`--no-close`), 74 MB (36,030 sectors, 184 files); then a second session by the Windows writer | Features 0x002F and 0x0021 current; mode page write type 0, FP, packet 16, multi-session 3 confirmed; SET STREAMING 8x; WRITEs without a busy answer; SYNCHRONIZE CACHE 1.8 s, CLOSE TRACK about 2 s, CLOSE SESSION 53 s. TOC: one data track, lead-out 65,264 (the border after the session); the disc then reported status 1 with the next session at 93,952, the same 28,688-sector gap the Windows writer left on this disc type on 2026-11-10. FUA read-back passed; Windows mounted it as CDFS (no UDF on an open disc) and all 184 files were identical. The Windows writer then imported this session, added 189 files (39,188 sectors, without rewriting the first session) and closed the disc; its read-back passed. Windows showed only session 1 until a reload, as recorded for this drive before; after it both folders, all 373 files identical to their sources. |
| 2026-11-11 | GP60NB50 | new Verbatim DVD+RW (MKM-A02-00), never formatted | Data, real write with FORMAT UNIT, 372 MB (181,648 sectors, 1,005 files), 4x setting | Before: background format status 0, READ FORMAT CAPACITIES "unformatted", status 0. FORMAT UNIT type 26h (0xFFFFFFFF blocks, FOV and Immed) accepted at once; the drive then answered 2/04/04 (format in progress) for 28.5 s before taking WRITEs. Data written in 117 s with no busy answer; SYNCHRONIZE CACHE 4 s; CLOSE SESSION accepted, then 2/04/07 for about 32 s; 183 s in all. FUA read-back passed. Afterwards background format status 1 (stopped by the close, as libburn's cookbook describes), status 2; Windows mounted it as UDF "NEWDVDRW" and all 1,005 files hashed identical. |
| 2026-11-11 | GP60NB50 | blank CD-R (ATIP lead-in 97:26:07) | Real audio write, Session-at-Once, 8 MP3 tracks (32:44, 147,266 sectors), 16x; no CD-Text (the MP3 tags were not read then, fixed in aa6c340) | SET CD SPEED 16x accepted; 5,460 WRITEs, all GOOD with no busy answer; table of contents matched; offset 0. **Read-back check failed:** 166,424 of 86,592,408 samples differed, the first in track 7 near LBA 125,394, while every READ CD returned GOOD. A secure `rip-audio` (C2 pointers, re-reads) then read all 8 tracks with 0 unreadable and 0 suspect sectors, but needed re-reads for 123 sectors in track 8; each ripped track was identical to the decoded source, with silence after the audio. So the data was written correctly, but the outer part of this disc reads marginally at the check's single fast pass, and the drive returned interpolated audio there without reporting an error. Since fc74218 a mismatch is re-read with C2 pointers and reported as hard to read, written wrong or unreadable. |
| 2026-11-11 | GP60NB50 | blank CD-R of the same old batch (ATIP lead-in 97:26:07) | Same 8 tracks at 8x, with album and track CD-Text from the MP3 tags (57 packs) | SET CD SPEED 1,411 kB/s (8x) accepted, but the drive wrote at its lowest CD-R speed, 10x: the program area took 194 s, 10.1x on average (the 16x run before it averaged 12.2x over the same album, as partial CAV does on the inner half of a disc); table of contents and all 57 CD-Text packs matched; offset 0. **Read-back check failed again:** 46,628 samples differed, the first in track 8 near LBA 133,469. A secure `rip-audio` read every track identical to the decoded source (0 suspect sectors) and found the CD-Text, but needed re-reads for 85 sectors in track 7 and 124 in track 8. Against the 16x disc: fewer differences in the fast check, more re-reads in the secure rip, so this pair (10x CLV against 16x partial CAV) shows no clear benefit from the slower speed; the weak outer area is common to both discs of this old batch. |
| 2026-11-11 | GP60NB50 | blank CMC Magnetics CD-R (ATIP 97:26:66, phthalocyanine), newer stock | Same 8 tracks, no `--speed` and no `--title`/`--artist`: the default speed (the run used the slowest-speed default of that hour, 10x, which is also what the drive-aware default picks on this drive) and album, artist and titles from the MP3 tags | SET CD SPEED 1,764 kB/s accepted; program area 194 s (10.1x); write 246 s with the lead-out; 57 CD-Text packs and the table of contents matched. **Read-back check passed at the first pass: all 86,592,408 samples identical**, where both discs of the old batch failed it. A secure `rip-audio` (C2 pointers, secure by default now) then needed **0 re-reads and found 0 suspect sectors** in all 8 tracks (the old batch: 123 at 16x, 209 at 10x), every track identical to the decoded source, CD-Text found. So on this drive the media made the difference, not the speed. |
| 2026-11-11 (evening) | GP60NB50 | one of the two old-batch CD-Rs above, burned that afternoon | Read-only: the new read-back check (fc74218) run against the disc on its own, with the same 8 tracks as the expected audio | Two runs, 146 s and 140 s: table of contents matched, offset 0, **all 86,592,408 samples identical in the fast pass both times**, so the careful re-read was not needed. The same fast pass had failed on this disc straight after it was burned. This drive and disc read worse just after a long write than hours later; the cause (drive temperature, the fresh dye) is not known. |
| 2026-11-11 (evening) | GP60NB50 | blank CD-R of the old batch (FORNET, ATIP 97:26:07) | Real audio write, the same 8 tracks, default speed and tags; the new check (fc74218) | Asked for 10x; audio sent at 10.1x on average (147,416 sectors in 194 s); 243 s with the lead-out. TOC and 57 CD-Text packs matched; **fast pass: all 86,592,408 samples identical**, so no re-read was needed this time. Then read-only, the same disc checked against the same audio with one byte of the *expected* audio changed on purpose (track 3, LBA 41,240), to run the careful re-read on a real drive. Run 1: the fast pass found 36 differing samples in 3 sectors: the changed sample plus 35 genuine misreads in 2 other sectors; the re-read (READ CD with C2 pointers, 1,024-sector cache flush per round) read the 2 genuine ones right (**hard to read**) and found LBA 41,240 different on every read with clean C2 pointers (**written wrong**), 5.6 s. Run 2: only the changed sample differed; LBA 41,240 written wrong, 5.4 s. Both verdicts and the C2 read path work on this drive; misreads on this disc come and go between passes. |
| 2026-11-11 (evening) | GP60NB50 | the used Verbatim DVD+RW (MKM-A02-00), holding a 372 MB test volume | DVD-Video, **direct writer** (`burn-dvd --native --overwrite`, new in d86f8d0): a 60 s 720×480 test card authored by `burn-dvd` (MPEG-2 + AC-3), 21,361 sectors (43.7 MB), default speed 4x | Background format status 1 (left so by the earlier close), so no FORMAT UNIT; SET STREAMING 4x accepted; data written in 24.1 s, 29 s in all; read-back of every sector passed. Windows mounted UDF "TESTCARD" with VIDEO_TS and AUDIO_TS; both IFO files byte-identical to their BUP; the title VOB decodes in full (1,799 frames, 60.0 s, MPEG-2 720×480, AC-3 48 kHz). Small images write slowly here (1.3x on average over 24 s; the 372 MB burns reached 2.1–2.4x). Not played in a set-top player. Found on the way: authoring stretched the picture to fill the frame; fixed the same evening (the source is fitted to the display shape with bars before the squeeze to 720 pixels), and the disc was burned again the same way (TESTCARD2, 19,232 sectors, read-back passed): a frame read from it has 55- and 57-pixel side bars, a 608-pixel picture that shows as 721×480 on a 16:9 screen, the 3:2 of the source. |
| 2026-11-11 (evening) | GP60NB50 | blank CD-R of the old batch (FORNET, ATIP 97:26:07) | The same 8 tracks, everything automatic, with **write-offset correction** (6c8763c): read offset +6 from AccurateRip's list (255 submissions, downloaded), combined offset 0 on record from the day's four logged burns, so write offset −6 and the audio sent 6 samples later | 10.1x; TOC, 57 CD-Text packs and the drive offset (0, as expected: the check compares with what was sent) matched. **Fast pass: 2,524,041 differing samples from track 6 on**, more than the 4,500-sector limit for careful re-reads, so the check reported failure. A secure `rip-audio --read-offset 6` minutes later: **all 8 tracks identical to the decoded source, sample for sample**, 198 sectors re-read in track 5, 0 suspect. So the disc holds the audio where an offset-corrected rip expects it (the earlier, uncorrected discs matched only with read offset 0), and the check's limit was too low for this batch read straight after a burn. |
| 2026-11-11 (evening) | GP60NB50 | the same disc, about an hour later | Read-only `check-disc`: every sector read once with C2 pointers | 147,266 sectors in 146 s. 6,242 flagged (uncorrectable), 0 unreadable: track 6 4, track 7 1,121, track 8 5,117, all in minutes 25–32, rising towards the edge (14, 89, 403, 1,033, 1,167, 1,310, 1,289, 937 per minute). The secure rip an hour before had flagged only 198 sectors on first reads, in track 5: on this disc single-pass results vary a lot from read to read. |
| 2026-11-10 | GP60NB50 | CD-R, FORNET (ATIP 97:26:07) | Real audio write from a single-file cue sheet: 4 tracks (85 s), CD-Text, catalog number, 4 ISRC codes, pre-emphasis on track 2, copy permitted on track 3, 2 s of gap audio before tracks 2 and 3, 6 s of audio in front of track 1; no test write | Cue sheet (19 entries) accepted in the standard layout, 18,068 sectors with the CD-Text lead-in, session closed in 10 s, 73 s in all. Read-back check passed: table of contents, 21 CD-Text packs, 3,748,500 samples identical, offset 0. Read separately from the disc: control bits 0/1/2/0 in the table of contents, catalog number and all four ISRC codes as written, and the Q sub-channel shows INDEX 00 from the exact frame of each gap, counting down to INDEX 01. A `rip-audio` of the disc gave four files identical to the source. |
| 2026-11-11 (night) | GP60NB50 | the Mitsubishi CD-RW (ATIP 97:34:23), quick-erased (39 s) | **Mixed-mode CD** from a `.cue` (a239555): one BINARY file, track 1 `MODE1/2352` (an ISO 9660 + Joliet image, 1,493 sectors, holding a 3 MB file of known SHA-256) with its 150-sector post-gap of zeros, then two audio tracks (3:25 and 4:25) after a 2-second INDEX 00; `burn-audio --native`, recommended speed, no test write | Cue sheet accepted (lead-in 0x41, track 1 data form 10h); the data track went in 2,048-byte sectors from LBA −150 to 1643, then the audio; 143 s in all, 9.5x on average. Read-back check passed: the data track's sectors and the post-gap, then 20,825,196 audio samples identical (offset 0; no write-offset correction on a mixed disc). Read separately: Windows mounted CDFS "MIXEDTEST" and the file's SHA-256 matched; the TOC gives track 1 control 4 (data) at LBA 0, track 2 at 1,793, track 3 at 17,192, lead-out 37,060, exactly the `.bin`'s layout; the Q sub-channel shows track 1 through the post-gap and track 2 INDEX 00 from LBA 1,643, counting down. A secure `rip-audio` of tracks 2 and 3 about 15 minutes later took 14 min and flagged 20 and 5,226 sectors as still suspect; exactly those sectors differ from the source, all others are identical. A one-pass `check-disc` right after: 8,073 of 37,060 sectors with uncorrectable errors, rising from minute 3 (78, 115, 125, 776, 1,081, 1,780, 1,851, 1,841, 426 per minute), 7 of them in the data track (its own error correction covered them). So the disc was written as sent, and on later reads this CD-RW reads weakly towards the outside. Found on the way and fixed: the JSON reported a +6-sample shift the writer had not applied, and `sectorsWritten` left out the data track. |
| 2026-11-11 (night) | GP60NB50 | source: the mixed-mode CD-RW above; target: blank CD-R | **Disc copy of the mixed-mode CD**, one drive, `DiscCopyService` with the direct writer (before 1e92c96) | Data track read as user data, identical to the source image. The audio was read without C2 pointers or re-reads: 41,663 and 1,808,433 samples wrong in tracks 2 and 3, no error reported. Stopped while it waited for the blank, nothing written. Fixed in 1e92c96: the copy reads audio in secure mode and stops when sectors still read wrong. |
| 2026-11-11 (night) | GP60NB50 | the same CD-RW, about an hour after its burn | Disc copy again, secure reading (1e92c96) | Stopped before the target was touched: 5,005 sectors still read with errors (track 2: 17, track 3: 4,988). Compared with the source files, exactly 17 and 4,988 sectors of what was read differ: every misread was flagged and nothing else. |
| 2026-11-11 (night) | GP60NB50 | the CD-RW quick-erased and burned again (read-back passed), then a blank CD-R (FORNET batch) | Disc copy straight after the burn, secure reading, one drive with a disc swap | Source read in 48 s: data track and every audio sample identical to the source files. The copy was burned with the direct writer and its read-back passed: the data track's 1,493 sectors and 20,825,196 audio samples identical; 4 min 12 s for the copy in all, swap included. On the CD-R: Windows mounted "MIXEDTEST", the file's SHA-256 matched; TOC and Q marks the same as the source (track 1 control 4, tracks at 0, 1,793 and 17,192, lead-out 37,060, track 2 INDEX 00 from LBA 1,643). |
| 2026-11-11 (night) | GP60NB50 | source: the mixed-mode CD-R copied above; target: blank CD-R (FORNET batch) | `copy-disc --source H: --native --keep-image` (01d10b8), one drive | The tray opened after the read and the command waited for the blank (prompt on stderr), then burned: 233 s in all, swap included. Kept image identical to the source files (data track and every audio sample). Read-back passed: the data track's 1,493 sectors and 20,825,196 audio samples identical. On the new CD-R: Windows mounted "MIXEDTEST", the file's SHA-256 matched; TOC and Q marks as the source. |
| 2026-11-11 (night) | GP60NB50 | the copy just made by `copy-disc` | `verify --drive H: --cue mixed.cue` (new: the direct writer's read-back check against a `.cue`, nothing written) | Failed in 3 s on CD-Text pack 4: the copy had given track 1's CD-Text the performer "Various Artists" where the source has "IgnisDisc" (the data track was added before the album text was read). With `--no-cd-text`: passed in 46 s, the data track's 1,493 sectors and 20,825,196 audio samples identical. Fixed the same night: a copy takes track 1's CD-Text from the source disc. |
| 2026-11-11 (night) | GP60NB50 | the mixed-mode CD-RW (burned about an hour before), then a blank CD-R (FORNET batch) | `verify --cue` of the CD-RW; then erase, burn from `mixed.cue`, `copy-disc --native` to the CD-R and `verify --cue` of the copy, CD-Text on (4704267) | CD-RW an hour after its burn: track table and CD-Text matched, then 96 sectors needed re-reads and 22 never read right (track 3), 0 written wrong; check failed in 65 s. Burned again (read-back passed), copied at once (236 s, read-back passed). The copy checked against the original `.cue`: passed in 48 s with CD-Text, the data track's 1,493 sectors and 20,825,196 audio samples identical. The copy's CD-Text now matches the source's. |
| 2026-11-11 (night) | GP60NB50 | the mixed-mode CD-R copy (CD-Text checked) | `rip --raw` of the disc, then `verify --cue` of the disc against the ripped cue (read-only) | First rip: 37,058 sectors in 60 s, all identical to the source `.bin` (Mode 1 sectors with the drive's EDC/ECC, gaps, audio), but 2 sectors short: it stopped at Windows' used size, not the lead-out at 37,060, and cut the end of track 3. Its cue had no album TITLE/PERFORMER and no INDEX 00 for track 2. Fixed: a raw rip runs to the lead-out in the table of contents, the cue gets the album text, and the cue import gives track 2's pre-gap (audio sectors at the end of track 1's range) back to track 2. Second rip: 37,060 sectors, byte-identical to the source `.bin`; `verify --cue` against it passed (CD-Text, the data track's 1,493 sectors, 20,825,196 samples), so `burn-audio --native` takes the rip back as the same disc. |
| 2026-11-13 | GP60NB50 | blank Verbatim DVD+R (2,295,104 sectors free) | Data, real write, closed disc, 198 MB (102,523 sectors + 5 padding, 479 files), 8x setting | No mode page, no test write. GET PERFORMANCE offered 8x and 4x; SET STREAMING 8x accepted. The first WRITE took 23 s (the drive's start-up), then 3,204 WRITEs of 64 KiB, about 13.7 ms each (2.2x average over the data). SYNCHRONIZE CACHE 2.1 s; CLOSE TRACK (001b) accepted; **finalize 101b (`5B 01 05`) accepted**, then 2/04/07 for 79 s; no 010b fallback was sent. TOC: one data track, lead-out at 102,528; READ DISC INFORMATION status 2 (complete). Read-back of every sector 45 s: passed. Independent check: Windows mounted it as UDF and all 479 files were SHA-256 identical to the source. 153 s for the writer. |
| 2026-11-13 | GP60NB50 | test CD-RW (Mitsubishi), quick-erased | Data, three sessions with the direct writer (`burn-data --native`, sessions 1 and 2 left open, session 3 closing the disc), about 30 MB each (15,415 / 15,396 / 15,348 sectors; 148, 13 and 31 files in separate folders) | Track-at-Once each time. Sessions at LBA 0, 26,817 and 49,115: 11,400 sectors after the first lead-out (15,417) and 6,900 after the second (42,215), as the Orange Book and the simulator say. Each session imported the files of the one before. `READ TOC` was right straight after every session and Windows showed the newest session without a reload. Every file of every session identical, read straight from the drive (ISO 9660 of each session by `READ TRACK INFORMATION`) and through Windows, before and after a reload. Before that, a direct session 3 added on top of a session the Windows writer had written (same addresses, all files identical). |
| 2026-11-13 | GP60NB50 | blank CD-R | Same three sessions | Same addresses as the CD-RW (0, 26,817, 49,115), 104 s / 117 s / 107 s; `READ TOC` right at once; every file of every session identical straight from the drive and through Windows, before and after a reload. |
| 2026-11-13 | GP60NB50 | blank Verbatim DVD-R | Same sessions, plus a 5 MB fourth (sessions 1-3 left open, 4 closing the disc) | Session 1 incremental: track padded to 65,264 sectors, next session at 93,952. **Session 2 first refused, nothing written:** `READ TRACK INFORMATION` of track FFh answers 5/24/00 on a DVD-R (it is accepted on CD). Fixed (b52b46f): the track is asked for by its number from `READ DISC INFORMATION`. Then sessions at 93,952, 115,520 and 137,040: 28,688 sectors after the first session, **6,160 after later ones** (the simulator assumed 28,688 each time; corrected). **`READ TOC` misses sessions:** after the third it listed track 1 and "track 2" at 115,520 (session 2 left out); after the fourth still the same, so Windows showed session 3 until a reload; after the reload track 1 and "track 2" at 137,040. Taken at its word, the import of the last session could read an older one; the table is now rebuilt from `READ TRACK INFORMATION` when tracks are missing (b52b46f). Each session imported the right files; every file of all four sessions identical straight from the drive, and through Windows after the reload. |
| 2026-11-13 | GP60NB50 | blank Verbatim DVD+R | Same three sessions (1 and 2 open, 3 finalizing) | No mode page; track 1 is 15,424 sectors (no minimum), sessions at 0, 17,472 and 34,928: **2,048 sectors after every session** (simulator corrected). CLOSE TRACK and CLOSE SESSION (010b) for sessions 1-2; session 3 finalized with **101b**, accepted. `READ TOC` complete straight after every session; Windows showed session 3 without a reload; every file of every session identical straight from the drive and through Windows, before and after a reload. |
| 2026-11-13 | GP60NB50 | used Verbatim DVD+RW (MKM-A02-00) | Data with `--overwrite` (30 MB, 148 files), then a second burn of other files without `--overwrite`, direct and through the Windows writer | First burn: written over from sector 0, every file identical. Second burn: both writers refused before writing ("already has files on it ... add --overwrite, or erase the disc first"); the first burn's files still identical and none of the new ones on the disc. A DVD+RW gets no added session; it is replaced whole or left alone. |
| 2026-11-14 | GP60NB50 | blank Verbatim DVD+R DL (MKM-003-00, 4,173,824 sectors, layer 0 2,086,912) | Data, real write: the Ubuntu 26.04 desktop ISO (6,518,974,464 bytes, 3,183,093 sectors), `burn-iso --native --layer-break 1800000` | **First try stopped at the first WRITE, nothing recorded:** READ DISC STRUCTURE 20h, SEND DISC STRUCTURE 20h with 1,800,000 and the read-back all accepted, SET STREAMING 6x (the drive offers 6x, 4x and 3x for this disc), then the first WRITE was failed by Windows after the writer's 60 s timeout (error 121). Afterwards the disc read blank (next writable address 0) with layer 0 at 1,800,000, not yet fixed, and a capacity of 3,600,000 sectors (twice layer 0). Fixed: the first WRITE of a run now has 4 minutes, and the simulator holds the first WRITE for a set calibration time (`FirstWriteCalibration`). **Second try on the same disc passed:** "layer break already at 1,800,000"; the first WRITE took **61.3 s**, then 64 KiB WRITEs of at most 3.4 s; 1,008 s of writing (4.7x average); SYNCHRONIZE CACHE 40.7 s; CLOSE TRACK, then finalize 101b accepted; 1,275 s for the writer; read-back passed. READ DISC STRUCTURE afterwards: 00h book type **0 (DVD-ROM, set by the drive itself)**, two layers, opposite track path, layer 0 ends after 1,800,000 sectors; 20h layer 0 1,800,000, fixed. Independent check: `verify --iso` read all 3,183,093 sectors in 15 min, SHA-256 `487F87FA…BCEE9DD2`, equal to the image and to Ubuntu's published checksum. |
| 2026-11-14 | GP60NB50 | blank Verbatim DVD+R DL (MKM-003-00) | DVD-9 video: two films authored by IgnisDisc (103 and 86 min, 4,463 kbps video so the first fits on layer 0), `burn-dvd --native`; the break placed on the cell where the second film starts | Image 3,122,832 sectors built first; layer break on title set 2, cell 1 at LBA **1,793,216** (57.4% on layer 0), sent with SEND DISC STRUCTURE 20h and read back. First WRITE **85.4 s** (calibration), 1,016 s of writing, SYNCHRONIZE CACHE 48.3 s, finalize 101b accepted; 1,382 s with the read-back of every sector: passed. READ DISC STRUCTURE afterwards: book type 0 (DVD-ROM), layer 0 1,793,216, fixed. Independent checks: libdvdnav (mpv) read the disc in the drive from 1:42:42 in the first film to its end and on into the second film on layer 1; an Xbox Series X played both films, fast-forwarding through the first into the second (the layer change) without a stop. |
| 2026-11-15 | GP60NB50 | Ritek DVD-RW (RITEKW01), quick-erased | Data, `burn-data --native --no-close` (200 MB) | **Stopped before writing**: feature 0021h listed but not current after a quick erase (BLANK minimal). Nothing written; the message now says to erase fully. |
| 2026-11-15 | GP60NB50 | same disc after a full erase (1,762 s) | Data, `burn-data --native --no-close` (200 MB, 10 files) | Incremental: 102,427 sectors + 5 padding, 77 s writing (1.97x), session closed, read-back passed. Drive afterwards: profile 0014h, disc status 1 (open). 10/10 files identical through Windows and in a rip (102,427 sectors, 0 bad). Windows' media status: overwrite-only, so a second session was refused, nothing written (fixed the same day, next row). |
| 2026-11-15 | GP60NB50 | same disc, full erase again, session 1 as above | Data, `burn-data --native` second session (30 MB, 3 files) after the engine read the open disc itself | Session 2 imported session 1 and was laid out from **131,120** (102,432 + 28,688); 15.9 s writing, CLOSE TRACK + CLOSE SESSION accepted, read-back passed. Disc afterwards status 2, 146,512 sectors. Rip 146,512 sectors, 0 bad: the volume at 131,120 holds all 13 files identical, the one at 0 the first 10. Windows showed session 1 until the disc was reloaded, then 13/13 identical. |
| 2026-11-15 | GP60NB50 | same disc, quick-erased | Data, `burn-data --native` closed (63 MB, 6 files incl. a 100-character name) | Disc-At-Once: 31.9 s writing, then **SYNCHRONIZE CACHE 376 s** (lead-out; 122 s for 372 MB on DVD-R), so the flush timeout went from 10 to 20 min. Status 2, one track of 31,024 sectors. UDF mount and rip: 6/6 identical. Any burn without an erase refused: "must be erased". |
| 2026-11-15 | GP60NB50 | same disc, Windows' writer had formatted it to 65,264 blocks (restricted overwrite) | Data, `burn-data --native --overwrite` (230 MB, 13 files) | FORMAT UNIT 13h accepted, drive ready after 12.4 s; 118,071 sectors (+9 to a 32 KiB block) from LBA 0, 88.5 s; CLOSE SESSION accepted. Formatted size afterwards 118,080. 13/13 identical through Windows (UDF) and in a rip. |
| 2026-11-18 | GP60NB50 | the Mitsubishi CD-RW, quick-erased (37 s) | First picture disc: one audio track made by `disc-art generate --rim-header` (12 alternating 30° wedges from r = 24.8 mm, rim header at 54–58 mm, 359,673 sectors) with the uncalibrated `cd-80` geometry, burned with `disc-art burn` | Written: 370,750 sectors with the lead-in in 11:13 (10.0x). The read-back stopped at LBA 128,547 (3/11/00, about r = 40 mm); a careful rip was stopped at 56 % to look at the disc, so whether that sector was written wrong is not known. **No picture:** neither the wedges nor the header text are visible; the whole surface shows fine concentric stripes, like record grooves. That is what an angle drift looks like: a few tenths of a percent error in the assumed track pitch or speed turns the picture through tens of full turns between the hub and the rim, so the wedges wind into thin spirals. Pictures on CD need a calibration of this drive and disc first (`disc-art calibrate`). |
| 2026-11-20 | Lite-On iHAS124 (first write on this drive) | blank FORNET CD-R (ATIP 97m26s07f) | Data, `burn-data --native --udf --joliet --speed max` (206 MB, 497 files, 106,358 sectors, closed) | Track-at-Once, mode 1; `GET PERFORMANCE` offered one speed, 16x (2,823 kB/s), `SET CD SPEED` accepted. First WRITE held 12.0 s (drive start-up), then 3,439 WRITEs in 88.9 s (15.95x); `SYNCHRONIZE CACHE` 3.2 s, `CLOSE TRACK/SESSION` accepted, drive busy (2/04/08) 22 s while it wrote the lead-out; writer 126.3 s in all. Read-back 61.9 s passed. TOC one data track, lead-out 106,360. Windows mounted it as UDF and 497 of 497 files hashed identical to the source (SHA-256). |
| 2026-11-20 | Lite-On iHAS124 | second blank FORNET CD-R, same batch | Same folder and options through Windows' writer (`--windows-writer`) | Phases: power calibration 9.3 s, writing 301.2 s (4.7x over the data phase), finalization 25 s, read-back 60.4 s; 337.5 s for the burn. The writer's own average (7.06x, from progress reports) does not agree with the phase times; the phase times are the ones used here. 497 of 497 files identical through Windows. The direct writer was **2.7x faster** over the whole burn (126 s against 338 s) and 3.4x faster over the data phase. Why Windows' writer ran well under the 16x the drive offers is not known. |
| 2026-11-21 | GP60NB50 | blank Verbatim DVD-R (MCC 03RG20, 2,297,888 sectors) | `disc-art burn --native` of a coarse calibration target (angle-sweep bands, preset `dvd-5-120mm`, 24 to 58 mm, 2,295,104 sectors) | Disc-At-Once at the 8x setting (`GET PERFORMANCE` offered 8x and 4x): every WRITE accepted, writing 659.8 s, `SYNCHRONIZE CACHE` 10.6 s, the drive closed the disc itself, table of contents one data track of 2,295,104 sectors. The read-back check then stopped at LBA 1 (unrecovered read error) and the CLI exited with an error although the write had finished: picture sectors are not ordinary readable data, and a native burn always runs that check (`--no-verify` does not turn it off). Photographed under direct light: thin dark arcs inside the rainbow sectors; the six band edges could not be placed consistently (a fit needs them within 0.4 mm of one another), so no calibration was saved. Photographed under soft room light: only reflections, no pattern. |
| 2026-11-21 (evening) | GP60NB50 | blank FORNET CD-R (ATIP 97m26s07f), the first CD burn after the DVD-R Disc-At-Once burn above | Mixed-mode CD from a `.cue` (track 1 `MODE1/2352`, 2,001 sectors; track 2 audio, 78 minutes), `burn-audio --dao --native --speed 10` | **Refused before writing, disc untouched.** `MODE SENSE` showed the write parameters page still as the DVD burn left it (`05 32 42 05 08`: write type 2, track mode 5, data block type 8); the Session-at-Once page was built from it with track mode 5 kept, and `MODE SELECT` answered 5/26/00 for data block type 3 and for 0. The drive keeps the page across a disc change. Fixed the same evening: the Session-at-Once page now sets track mode 4, fixed packets off, link size 0 and packet size 0 itself (the page this drive accepted on 2026-11-08 and 2026-11-10, `05 32 42 04 03`); the virtual drive refuses the left-over page the same way. |
| 2026-11-21 (evening) | GP60NB50 | the same FORNET CD-R, still blank | The same image again with the fixed writer | Page `05 32 42 04 03` accepted, cue sheet layout Standard, write-offset correction on (audio sent 6 samples later from the first audio sector). Data track 2,001 sectors plus its post-gap, then 351,623 audio sectors in 467 s (10.0x), session close 30 s, 530 s of writing in all. Read-back (451 s): table of contents, 10 CD-Text packs, the data track and 206,754,324 audio samples identical; 2 sectors (LBA 310,860 and 325,386, near the outer edge) read wrong in the fast pass and right on the re-read with C2 pointers. Windows mounted UDF "RINGTEST1" and all 154 files hashed identical to their sources. |
| 2026-11-22 | GP60NB50 | the Mitsubishi CD-RW, quick-erased (40 s) | Picture disc placed from a measured turn table instead of a photo calibration: the old 359,673-sector track was mapped by read timing (117 stations, 8 minutes, read-only), then `disc-art generate --media cd --turn-map <map> --turn-map-offset -1.5` (12 spokes, three rings, a word) and `disc-art burn --native --speed 10` | Written: 370,750 sectors with the lead-in at 10.0x, 796 s including the check. The check failed at LBA 213,516 (3/11/00, one sector unreadable in the fast pass) although every WRITE was accepted. Mapped again after the burn: 20,016.5097 turns from address 17,000 to 350,000 against 20,016.5090 before the erase; fitted inner radius 24.84851 against 24.84845 mm. **First recognisable picture** on a phone photo under a point light: three round rings, twelve spokes and both words in place. The spokes waved by about 2 degrees: the table had been taken from a disc holding a calibration pattern, and reads of patterned audio sectors time a few degrees differently along the pattern. The turn count over the disc differs by 11 turns from the photo calibration saved on 2026-11-19 (inner 24.8447, outer 57.9922 mm). |
| 2026-11-22 | GP60NB50 | the same CD-RW, quick-erased before each burn (39 to 41 s) | Three more picture burns from the measured turn table, each `disc-art burn --native --speed 10`: a clock face with the offset at -1.5 and then at -0.9 sectors (table taken from the disc while it held the first test picture, no correction needed), then a ladder of twelve spokes pre-bent by 0 to 1.1 degrees per mm | All three written in full (370,750 sectors, 10.0x); the check failed each time on picture sectors (one unreadable sector, or 2.6 to 3 % of samples different in the fast pass). Clock faces: numerals and words sharp, in clock order and not mirrored, seen from the recorded side; the waves of the first burn gone. Spokes still bent: the middle of each lies 1.6 to 3.4 mm clockwise of the line through its ends, the same at both offsets (an offset error only moves a spoke sideways, 16 mm per sector). Mapped at 2,090 and at 1,045 rpm (inner half): the two maps agree to 1 degree, so the bend is not a read delay. Ladder: the spoke pre-bent by 0.6 to 0.7 degrees per mm came out straight to 0.2 mm over 37 mm. Cause of the bend not found. |
| 2026-11-22 | GP60NB50 | blank FORNET CD-R (ATIP 97m26s07f) | The ladder (0.35 to 0.90 degrees per mm) placed from the turn table of another FORNET CD-R with the same ATIP code, `disc-art burn --native --speed 10` | Written: 371,366 sectors with the lead-in, 10.1x, 936 s with the check; **read-back passed** (all 211,487,724 samples identical after re-reading; 2,579 sectors needed the careful re-read). Mapped afterwards (read-only, 3,239 rpm): 21,269.27 turns from address 2,000 to 344,000, against 21,167.32 on the disc the table came from: **102 turns apart** (fitted pitch 1.488 against 1.520 um; this disc within 11 degrees of constant pitch, the other up to 300 degrees off). The table did not fit this disc, so its spokes are wound up; two discs with one ATIP code are not interchangeable. `SEEK (10)` alone returns after 2 to 80 ms with no relation to the rotation, so a blank disc cannot be timed that way. |
| 2026-11-22 | GP60NB50 | third blank FORNET CD-R from the same spindle (ATIP 97m26s07f) | One 79:46 track of noise, `burn-audio --dao --native --speed 10 --no-cd-text`, to compare three discs of one spindle by read timing | Written: 359,150 sectors, 9.5x average, 1,097 s with the check; one sector near LBA 358,868 (outer edge) never read back in 5 careful re-reads. Mapped (read-only, 2,090 rpm): 21,167.3460 turns from address 2,000 to 344,000. The disc measured in the morning: 21,167.3234; the ladder disc above: 21,269.2697. So the spindle holds at least two kinds of disc: this one and the morning disc agree to 0.02 turn over 21,167 (a steady drift of 8 degrees end to end, 0.9 degrees sd around it, the same 300-degree departure from constant pitch), the ladder disc is 102 turns away from both. |
| 2026-11-22 | GP60NB50 | fourth blank FORNET CD-R from the same spindle | The 0.35 to 0.90 ladder placed from the third disc's turn table, `disc-art burn --native --speed 10` | Written: 371,366 sectors with the lead-in, 10.1x, 899 s with the check; read-back passed (1,979 sectors needed the careful re-read). Mapped afterwards (3,239 rpm): 21,269.2587 turns from address 2,000 to 344,000, the same spiral as the second disc (21,269.2697; 0.6 degrees sd apart) and 102 turns from the first and third. The four discs, in the order they came off the spindle, alternate between the two spirals, as discs from a two-cavity mould stacked in turn would. The table used was of the other kind, so this disc's spokes are wound up too. Fitted pitch differs by 2.1 % and start by 0.3 % between the kinds, which one groove written at two densities cannot give, so these are two grooves, not two ways of writing. |
| 2026-11-22 | GP60NB50 | fifth blank FORNET CD-R, the next one off the same spindle | The same ladder image again (placed from the third disc's turn table), `disc-art burn --native --speed 10`, to test the prediction that the discs alternate | Written: 371,366 sectors with the lead-in, 10.1x, 889 s with the check; read-back passed (806 sectors needed the careful re-read). Mapped afterwards (3,239 rpm): 21,167.3434 turns from address 2,000 to 344,000. **Prediction held:** this is the spiral of the first and third discs (third: 21,167.3460, so 0.0026 turn or 0.9 degrees apart at the end, 0.9 degrees sd along the disc), 102 turns from the second and fourth. Five discs in spindle order: kind 1, 2, 1, 2, 1. First CD-R whose picture was placed from a table that fits it; it was a picture burn on kind 1, so the kind does not follow what is burned. |

Media on hand: CD-RW, CD-R, DVD-R, DVD+RW, DVD+R, DVD-RW (Ritek), DVD+R DL (Verbatim, used 2026-11-14), BD-R (Verbatim) and BD-RE (CMC Magnetics). M-DISC BD-R not probed yet. No DVD-RAM, BD-R DL or BDXL yet.

Second DVD writer, Lite-On iHAS124 (internal SATA, firmware CL9E), connected 2026-11-19, probed read-only the same
day, and used for its first CD-R data burns on 2026-11-20 (rows above). On 2026-11-21 it was probed read-only with a blank DVD-R, DVD+R, DVD-RW and CD-RW (write speeds 16x, 12x, 8x, 6x on DVD-R and DVD+R, 2x on DVD-RW, 10x on CD-RW; ready 10 to 18 s after `load`; each blank reads as one sector). No DVD or audio disc has been written on it. It names itself `ATAPI` / `iHAS124   F` in INQUIRY, lists CD-R/RW, DVD±R, DVD±RW
and DVD±R DL profiles (no DVD-RAM, no Blu-ray), and reports a motorised tray with the Load bit set: `load`
closed the open tray by itself and the drive came ready. For a FORNET CD-R its write speed list holds a single
entry, 16x CLV, where the GP60NB50 offers 24x, 16x and 10x.

Blu-ray writer, LG BP50NB40 (external slim USB 2.0, bus powered), connected 2026-11-26 and probed read-only: nothing
has been written with it. INQUIRY `HL-DT-ST` / `BP50NB40` / `1.02`; its USB bridge reports vendor ID 5917h, product
13FDh. Profiles BD-R (41h, 42h), BD-RE (43h), BD-ROM, DVD-RAM, DVD±R, DVD±RW, DVD±R DL, CD-R and CD-RW. Removable
Medium: tray, Load 0, Eject 1, a spring tray like the GP60NB50's. The BD Read and BD Write features list BD-RE class 0
versions 2 and 3 and BD-R class 0 versions 1 and 2. Real Time Streaming (0107h) has Stream Writing set, so the drive
says it takes `WRITE (12)` with the Streaming bit; the feature is current with a BD-R loaded and not current with the
unformatted BD-RE. Buffer 2,048 KB.

| Disc | Profile | 0041h current | `GET PERFORMANCE` 03h | `READ FORMAT CAPACITIES` (format type: blocks, type-dependent parameter) |
|---|---|---|---|---|
| new BD-RE, CMC Magnetics CMCMAG-CN2 (BD-RE version 2, one layer) | 43h | no (0040h neither) | 2x, 8,991 kB/s | unformatted; 00h: 11,826,176, 0x003000; 01h: 11,564,032, 0x000800; 30h: 11,826,176 / 11,564,032 / 12,088,320 with 0x003000 / 0x005000 / 0x001000; 31h: 12,219,392, 0x000800 |
| blank BD-R, Verbatim VERBAT-IMe (BD-R version 1, one layer) | 41h | yes | 6x CAV, 26,974 kB/s; 2x CLV, 8,991 kB/s | unformatted; 00h: 11,826,176, 0x003000; 32h: 11,826,176 / 5,796,864 / 12,088,320, all 0x000000 |

- The current/maximum capacity descriptor holds 20,480 (BD-RE) and 200,704 (BD-R) where MMC puts the block length,
  so a size worked out from that field is 10 and 98 times too large; `media-info` showed a formatted capacity of
  250 GB for the 25 GB BD-RE.
- `READ CAPACITY`: 3/31/00 on the unformatted BD-RE; last LBA 0 on the blank BD-R (the one-sector answer the iHAS124
  gives for blank DVDs). `READ TRACK INFORMATION` on the BD-R: next writable address 0, 12,219,392 blocks free,
  packet size 32.
- `READ DISC STRUCTURE` with media type 1 lists formats 00h, 08h, 09h, 0Ah, 12h, 30h, 80h to 82h and C0h as readable.
  00h returns 112-byte Disc Information units (2 on the BD-RE, 5 on the BD-R) with the manufacturer ID at bytes
  100–105 and the media type ID at 106–108 of each unit. 08h answers 3/31/00 on the unformatted BD-RE and 5/24/00 on
  the BD-R.
- USB power: on a passive extension cable the drive dropped off the bus and came back by itself while a BD-RE spun up;
  plugged straight into a rear port it stayed connected through a BD-R spin-up (one try). With both of its leads
  (USB-C and USB-A) plugged into the PC at once, Windows reports "USB device not recognized".

The GP60NB50 has a spring-loaded slim tray that cannot close itself: automated runs never eject it, and the owner
swaps discs by hand. Drives added later get a row here with their probe result.
