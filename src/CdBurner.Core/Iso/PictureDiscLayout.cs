using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>Where the files go on a disc that also carries a picture drawn in its data sectors.</summary>
public enum PictureDiscFilesPlacement
{
    /// <summary>Files from LBA 0 (inner radius), then a zero gap, then the picture to the end of the disc.</summary>
    InnerFiles,

    /// <summary>
    /// Picture from LBA 0, then a zero gap, then the files as a second session near the end of the disc. ISO 9660 and
    /// Joliet only: the builder writes UDF for a session at LBA 0 only.
    /// </summary>
    OuterFiles,
}

/// <summary>Disc family; sets the alignment of the picture zone and of the files session.</summary>
public enum PictureDiscMedium
{
    /// <summary>DVD: one ECC block is 16 sectors (ECMA-267 §18).</summary>
    Dvd,

    /// <summary>BD: one recording cluster is 32 sectors.</summary>
    BluRay,
}

/// <summary>
/// The sector plan of a disc with files and a picture. Every range is a half-open LBA range
/// <c>[start, end)</c> of 2048-byte sectors.
/// </summary>
/// <param name="Placement">Inner or outer files.</param>
/// <param name="Medium">DVD or BD.</param>
/// <param name="AlignmentSectors">16 for DVD, 32 for BD.</param>
/// <param name="FilesSessionStartLba">LBA where the files session starts (0 for inner files).</param>
/// <param name="VolumeEndLba">End of the file system's volume space: the ISO 9660 volume space size, and the end of
/// the UDF partition plus its closing anchor when UDF is written.</param>
/// <param name="PictureStartLba">First picture sector (a multiple of <paramref name="AlignmentSectors"/>).</param>
/// <param name="PictureEndLba">End of the picture zone (a multiple of <paramref name="AlignmentSectors"/>).</param>
/// <param name="TotalSectors">Sectors in the composed image.</param>
public sealed record PictureDiscLayout(
    PictureDiscFilesPlacement Placement,
    PictureDiscMedium Medium,
    int AlignmentSectors,
    int FilesSessionStartLba,
    int VolumeEndLba,
    int PictureStartLba,
    int PictureEndLba,
    int TotalSectors)
{
    /// <summary>Sectors in the picture zone.</summary>
    public int PictureSectorCount => PictureEndLba - PictureStartLba;

    /// <summary>First sector of the zero gap between the file system and the picture.</summary>
    public int GapStartLba => Placement == PictureDiscFilesPlacement.InnerFiles ? VolumeEndLba : PictureEndLba;

    /// <summary>End of the zero gap.</summary>
    public int GapEndLba => Placement == PictureDiscFilesPlacement.InnerFiles ? PictureStartLba : FilesSessionStartLba;

    /// <summary>Sectors in the zero gap (at least <see cref="PictureDiscLayoutPlanner.MinimumGapSectors"/>).</summary>
    public int GapSectorCount => GapEndLba - GapStartLba;
}

/// <summary>
/// Plans a disc that holds files and a picture: the file system is clamped to the files (ISO 9660 volume space size
/// and UDF partition end where the files end), at least 2 MiB of zero sectors separate it from the picture, and the
/// picture zone starts and ends on an ECC block (DVD, 16 sectors) or cluster (BD, 32 sectors) boundary. The
/// operating system therefore never reads a picture sector as part of the volume.
/// </summary>
public static class PictureDiscLayoutPlanner
{
    /// <summary>2 MiB of 2048-byte sectors: the smallest zero gap between files and picture.</summary>
    public const int MinimumGapSectors = 2 * 1024 * 1024 / IsoUdfImageBuilder.SectorSize;

    /// <summary>Picture zone alignment for <paramref name="medium"/>.</summary>
    public static int AlignmentFor(PictureDiscMedium medium) => medium switch
    {
        PictureDiscMedium.Dvd => OpticalConstants.DvdEccBlockSectors,
        PictureDiscMedium.BluRay => OpticalConstants.BdClusterSectors,
        _ => throw new ArgumentOutOfRangeException(nameof(medium), medium, "Unknown disc medium."),
    };

    /// <summary>
    /// Lays out <paramref name="root"/> and a picture zone that fills the rest of a disc of
    /// <paramref name="discCapacitySectors"/> sectors.
    /// </summary>
    /// <param name="outerFilesGapSectors">Outer files only: zero sectors kept between the picture and the files session,
    /// at least <see cref="MinimumGapSectors"/>. A two-session burn needs room for the drive's own gap between sessions
    /// (see <c>PictureDiscTwoSessionBurner.DvdFilesSessionReserveSectors</c>).</param>
    /// <exception cref="ArgumentException">The files contain sectors imported from an earlier session.</exception>
    /// <exception cref="InvalidOperationException">The files, the gap and at least one aligned picture block do not
    /// fit on the disc.</exception>
    public static PictureDiscLayout Plan(
        CompilationDirectory root,
        IsoBuildOptions options,
        PictureDiscFilesPlacement placement,
        PictureDiscMedium medium,
        int discCapacitySectors,
        int outerFilesGapSectors = MinimumGapSectors)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(discCapacitySectors);
        ArgumentOutOfRangeException.ThrowIfLessThan(outerFilesGapSectors, MinimumGapSectors);
        if (HasImportedFiles(root))
        {
            throw new ArgumentException(
                "A picture disc is written in one pass; the compilation must not reference files from an earlier session.",
                nameof(root));
        }

        int align = AlignmentFor(medium);
        return placement switch
        {
            PictureDiscFilesPlacement.InnerFiles => PlanInner(root, options, medium, align, discCapacitySectors),
            PictureDiscFilesPlacement.OuterFiles => PlanOuter(root, options, medium, align, discCapacitySectors, outerFilesGapSectors),
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown files placement."),
        };
    }

    /// <summary>
    /// Lays out <paramref name="root"/> from LBA 0 with the picture zone starting at a fixed
    /// <paramref name="pictureStartLba"/> (for example the sector at a chosen radius) and running to the end of a disc
    /// of <paramref name="discCapacitySectors"/> sectors. Everything between the end of the file system and the picture
    /// is zero; that gap must be at least <see cref="MinimumGapSectors"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The files contain sectors imported from an earlier session, or the start
    /// is not aligned.</exception>
    /// <exception cref="InvalidOperationException">The files and the gap do not end before
    /// <paramref name="pictureStartLba"/>, or no aligned picture block fits after it.</exception>
    public static PictureDiscLayout PlanInnerBefore(
        CompilationDirectory root,
        IsoBuildOptions options,
        PictureDiscMedium medium,
        int pictureStartLba,
        int discCapacitySectors)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(discCapacitySectors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pictureStartLba);
        if (HasImportedFiles(root))
        {
            throw new ArgumentException(
                "A picture disc is written in one pass; the compilation must not reference files from an earlier session.",
                nameof(root));
        }

        int align = AlignmentFor(medium);
        if (pictureStartLba % align != 0)
        {
            throw new ArgumentException($"The picture must start on a {align}-sector boundary.", nameof(pictureStartLba));
        }

        int volumeEnd = IsoUdfImageBuilder.CalculateTotalSectors(root, options, sessionStartLba: 0);
        if ((long)volumeEnd + MinimumGapSectors > pictureStartLba)
        {
            throw new InvalidOperationException(
                $"The files take {volumeEnd:N0} sectors; with the {MinimumGapSectors:N0}-sector gap they must end by " +
                $"sector {pictureStartLba:N0}, which leaves room for {Math.Max(0, pictureStartLba - MinimumGapSectors):N0}.");
        }

        int pictureEnd = AlignDown(discCapacitySectors, align);
        if (pictureEnd - pictureStartLba < align)
        {
            throw new InvalidOperationException(
                $"No aligned picture block fits between sector {pictureStartLba:N0} and the end of a disc of {discCapacitySectors:N0} sectors.");
        }

        return new PictureDiscLayout(
            PictureDiscFilesPlacement.InnerFiles, medium, align,
            FilesSessionStartLba: 0, VolumeEndLba: volumeEnd,
            PictureStartLba: pictureStartLba, PictureEndLba: pictureEnd, TotalSectors: pictureEnd);
    }

    private static PictureDiscLayout PlanInner(
        CompilationDirectory root, IsoBuildOptions options, PictureDiscMedium medium, int align, int capacity)
    {
        int volumeEnd = IsoUdfImageBuilder.CalculateTotalSectors(root, options, sessionStartLba: 0);
        int pictureStart = AlignUp((long)volumeEnd + MinimumGapSectors, align);
        int pictureEnd = AlignDown(capacity, align);
        if (pictureEnd - pictureStart < align)
        {
            throw new InvalidOperationException(
                $"The files take {volumeEnd:N0} sectors; with the {MinimumGapSectors:N0}-sector gap no aligned picture " +
                $"block fits on a disc of {capacity:N0} sectors.");
        }

        return new PictureDiscLayout(
            PictureDiscFilesPlacement.InnerFiles, medium, align,
            FilesSessionStartLba: 0, VolumeEndLba: volumeEnd,
            PictureStartLba: pictureStart, PictureEndLba: pictureEnd, TotalSectors: pictureEnd);
    }

    private static PictureDiscLayout PlanOuter(
        CompilationDirectory root, IsoBuildOptions options, PictureDiscMedium medium, int align, int capacity, int gap)
    {
        // The session's size does not depend on where it starts, but confirm it at the chosen start: step back one
        // aligned block at a time until the session ends inside the disc.
        int trialStart = AlignDown(capacity, align);
        int sessionSectors = IsoUdfImageBuilder.CalculateTotalSectors(root, options, trialStart) - trialStart;
        int sessionStart = AlignDown(capacity - sessionSectors, align);
        while (sessionStart > 0 && IsoUdfImageBuilder.CalculateTotalSectors(root, options, sessionStart) > capacity)
        {
            sessionStart -= align;
        }

        int pictureEnd = AlignDown(sessionStart - gap, align);
        if (sessionStart <= 0 || pictureEnd < align)
        {
            throw new InvalidOperationException(
                $"The files take {sessionSectors:N0} sectors; with the {gap:N0}-sector gap no aligned " +
                $"picture block fits on a disc of {capacity:N0} sectors.");
        }

        int volumeEnd = IsoUdfImageBuilder.CalculateTotalSectors(root, options, sessionStart);
        return new PictureDiscLayout(
            PictureDiscFilesPlacement.OuterFiles, medium, align,
            FilesSessionStartLba: sessionStart, VolumeEndLba: volumeEnd,
            PictureStartLba: 0, PictureEndLba: pictureEnd, TotalSectors: volumeEnd);
    }

    private static bool HasImportedFiles(CompilationDirectory dir) =>
        dir.Children.Any(child => child switch
        {
            CompilationFile file => file.ImportedStartLba.HasValue && !file.IsPlannedLayoutLba,
            CompilationDirectory sub => HasImportedFiles(sub),
            _ => false,
        });

    private static int AlignUp(long value, int align) => checked((int)((value + align - 1) / align * align));

    private static int AlignDown(int value, int align) => value < 0 ? -1 : value / align * align;
}

/// <summary>
/// Fills one aligned picture block (16 sectors on DVD, 32 on BD) starting at <paramref name="firstLba"/>.
/// <paramref name="block"/> is <c>AlignmentSectors × 2048</c> bytes and arrives zeroed.
/// </summary>
public delegate void PictureBlockWriter(long firstLba, Span<byte> block);

/// <summary>Writes the image described by a <see cref="PictureDiscLayout"/>, in LBA order, to a stream.</summary>
public static class PictureDiscImageComposer
{
    /// <summary>
    /// Writes the files (built by <see cref="IsoUdfImageBuilder"/>), the zero gap and the picture zone to
    /// <paramref name="output"/>, from LBA 0 to <see cref="PictureDiscLayout.TotalSectors"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The built file system does not end where the plan says (the
    /// compilation changed after planning).</exception>
    public static void Compose(
        CompilationDirectory root,
        IsoBuildOptions options,
        PictureDiscLayout layout,
        PictureBlockWriter writePictureBlock,
        Stream output)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(writePictureBlock);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        if (layout.Placement == PictureDiscFilesPlacement.InnerFiles)
        {
            WriteFiles(root, options, layout, output);
            WriteZeros(output, layout.GapSectorCount);
            WritePicture(layout, writePictureBlock, output);
        }
        else
        {
            WritePicture(layout, writePictureBlock, output);
            WriteZeros(output, layout.GapSectorCount);
            WriteFiles(root, options, layout, output);
        }

        output.Flush();
    }

    /// <summary>
    /// Like <see cref="Compose(CompilationDirectory, IsoBuildOptions, PictureDiscLayout, PictureBlockWriter, Stream)"/>,
    /// but the whole picture zone is written by one call that streams straight into <paramref name="output"/> (no
    /// temporary copy of a zone that can take gigabytes). The zone writer must write exactly
    /// <see cref="PictureDiscLayout.PictureSectorCount"/> sectors.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file system or the picture zone did not take the sectors the
    /// plan says.</exception>
    public static void Compose(
        CompilationDirectory root,
        IsoBuildOptions options,
        PictureDiscLayout layout,
        Action<Stream> writePictureZone,
        Stream output)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(writePictureZone);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        if (layout.Placement == PictureDiscFilesPlacement.InnerFiles)
        {
            WriteFiles(root, options, layout, output);
            WriteZeros(output, layout.GapSectorCount);
            WritePictureZone(layout, writePictureZone, output);
        }
        else
        {
            WritePictureZone(layout, writePictureZone, output);
            WriteZeros(output, layout.GapSectorCount);
            WriteFiles(root, options, layout, output);
        }

        output.Flush();
    }

    private static void WritePictureZone(PictureDiscLayout layout, Action<Stream> writePictureZone, Stream output)
    {
        var counter = new CountingStream(output);
        writePictureZone(counter);
        long expected = (long)layout.PictureSectorCount * IsoUdfImageBuilder.SectorSize;
        if (counter.BytesWritten != expected)
        {
            throw new InvalidOperationException(
                $"The picture zone took {counter.BytesWritten:N0} bytes but the layout planned {expected:N0}.");
        }
    }

    private static void WriteFiles(CompilationDirectory root, IsoBuildOptions options, PictureDiscLayout layout, Stream output)
    {
        // The builder writes the session from its start LBA (16 system-area sectors, then the descriptors). Count
        // what it writes so a compilation changed after planning cannot spill into the gap or the picture.
        var counter = new CountingStream(output);
        IsoUdfImageBuilder.Build(root, counter, options, layout.FilesSessionStartLba);
        long expected = (long)(layout.VolumeEndLba - layout.FilesSessionStartLba) * IsoUdfImageBuilder.SectorSize;
        if (counter.BytesWritten != expected)
        {
            throw new InvalidOperationException(
                $"The file system took {counter.BytesWritten / IsoUdfImageBuilder.SectorSize:N0} sectors but the layout " +
                $"planned {expected / IsoUdfImageBuilder.SectorSize:N0}. Plan the layout again after changing the files.");
        }
    }

    private static void WritePicture(PictureDiscLayout layout, PictureBlockWriter writePictureBlock, Stream output)
    {
        var block = new byte[layout.AlignmentSectors * IsoUdfImageBuilder.SectorSize];
        for (long lba = layout.PictureStartLba; lba < layout.PictureEndLba; lba += layout.AlignmentSectors)
        {
            Array.Clear(block);
            writePictureBlock(lba, block);
            output.Write(block, 0, block.Length);
        }
    }

    private static void WriteZeros(Stream output, int sectors)
    {
        var zero = new byte[IsoUdfImageBuilder.SectorSize];
        for (int i = 0; i < sectors; i++)
        {
            output.Write(zero, 0, zero.Length);
        }
    }

    /// <summary>Pass-through stream that counts the bytes written through it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
