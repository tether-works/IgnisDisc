// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.BluRay/BluRayIsoGenerator.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.BluRay/BluRayIsoGenerator.cs
// IgnisDisc change: namespace; the UDF file system plan and writer (upstream DiscImageStudio.Iso) are not ported, so a
// plan never holds files and a data directory is refused. See THIRD-PARTY-NOTICES.md.


namespace CdBurner.Lithography.BluRay;

/// <summary>A full-disc drawing plan. The file system properties keep upstream's names and always report no files.</summary>
public sealed class BluRayIsoPlan
{
    internal BluRayIsoPlan(BluRayDiscParameters disc)
    {
        Disc = disc;
        DrawingStartSector = 0;
        DrawingEndSectorExclusive = disc.OutputSectors;
        DrawingStartRadiusMm = disc.InnerRadiusMm;
        DrawingEndRadiusMm = disc.OutputOuterRadiusMm;
    }

    internal BluRayIsoPlan(BluRayDiscParameters disc, uint drawingStartSector, uint drawingEndSectorExclusive)
    {
        disc.Validate();
        if (drawingStartSector >= drawingEndSectorExclusive
            || drawingEndSectorExclusive > disc.OutputSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(drawingEndSectorExclusive), "The drawing range must lie inside the disc.");
        }

        Disc = disc;
        DrawingStartSector = drawingStartSector;
        DrawingEndSectorExclusive = drawingEndSectorExclusive;
        BluRayTrackGeometry geometry = new(disc);
        DrawingStartRadiusMm = geometry.AtSector(drawingStartSector).RadiusMm;
        DrawingEndRadiusMm = geometry.AtSector(drawingEndSectorExclusive).RadiusMm;
    }

    public bool HasFileSystem => false;
    public bool FilesAtOuterRing => false;
    public BluRayDiscParameters Disc { get; }
    public uint TotalSectors => Disc.OutputSectors;
    public long ContentLength => Disc.ContentLength;
    public string? DataDirectory => null;
    public string? VolumeLabel => null;
    public DateTimeOffset? UdfTimestamp => null;
    public int FileCount => 0;
    public int DirectoryCount => 0;
    public long FileBytes => 0;
    public uint DrawingStartSector { get; }
    public double DrawingStartRadiusMm { get; }
    public uint DrawingEndSectorExclusive { get; }
    public double DrawingEndRadiusMm { get; }

    /// <summary>First sector of the UDF partition (metadata plus file data) and the end of
    /// its data area. The partition moves as a whole, so the design guide marks their span.</summary>
    public uint PartitionStartSector => 0;
    public uint DataEndSectorExclusive => DrawingEndSectorExclusive;
}

/// <summary>Writes the image payload into every cluster of the disc. The drive performs physical encoding.</summary>
public static class BluRayIsoGenerator
{
    /// <summary>A saved drawing range without a filesystem, used by calibration records and
    /// sidecar-driven previews.</summary>
    public static BluRayIsoPlan PrepareRaw(BluRayDiscParameters disc,
        uint drawingStartSector, uint drawingEndSectorExclusive)
        => new(disc, drawingStartSector, drawingEndSectorExclusive);

    /// <summary>Draws every user-data sector. A data directory is refused: the file system writer is not ported.</summary>
    public static BluRayIsoPlan PrepareDrawing(BluRayDiscParameters disc, string? dataDirectory = null,
        string volumeLabel = "BD_IMAGE", DateTimeOffset? timestamp = null, bool filesAtOuterRing = false)
    {
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
        if (filesAtOuterRing && string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Outer-ring files require a data directory.", nameof(dataDirectory));
        if (!string.IsNullOrWhiteSpace(dataDirectory))
            throw new NotSupportedException("A Blu-ray picture image that also holds files is not supported.");
        return new BluRayIsoPlan(disc);
    }

    /// <summary>The same sequential byte producer is used by ISO files and live burns.
    /// No seek, Position, Length or SetLength is required on the destination.</summary>
    public static BluRayStreamingSummary GenerateToStream(Stream output, BluRayIsoPlan plan,
        IBluRayPayloadMapping mapping, IProgress<BluRayStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mapping);
        if (!output.CanWrite) throw new ArgumentException("A writable output stream is required.", nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
        return BluRayStreamingGenerator.GenerateToStream(output,
            new(plan.Disc, 0, plan.TotalSectors), mapping, progress, cancellationToken);
    }

    /// <summary>Publishes only a complete ISO; cancellation or failure preserves an existing file.</summary>
    public static BluRayStreamingSummary GenerateFile(string outputPath, BluRayIsoPlan plan,
        IBluRayPayloadMapping mapping, IProgress<BluRayStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mapping);
        ValidateOutputPath(outputPath, plan);
        cancellationToken.ThrowIfCancellationRequested();
        BluRayStreamingSummary? summary = null;
        BluRayOutputPublisher.Write(outputPath, stream =>
            summary = GenerateToStream(stream, plan, mapping, progress, cancellationToken), cancellationToken);
        return summary!;
    }

    public static void ValidateOutputPath(string outputPath, BluRayIsoPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(plan);
    }
}
