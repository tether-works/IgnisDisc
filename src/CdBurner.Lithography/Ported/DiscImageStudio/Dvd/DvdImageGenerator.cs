// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Dvd/DvdImageGenerator.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Dvd/DvdImageGenerator.cs
// IgnisDisc change: namespace; calibration targets and the files-plus-picture (hybrid) request are not ported. See THIRD-PARTY-NOTICES.md.

using System.Text.Json;
using CdBurner.Lithography.Dvd.Solver;

namespace CdBurner.Lithography.Dvd;

public sealed record DvdImageRequest(
    string SourceImage,
    string OutputPath,
    DvdStreamingOptions Options);

/// <summary>Generates the normal drawing ISO and its compatible solve sidecar.
/// CLI argument parsing and exit-code handling are deliberately outside this API.</summary>
public static class DvdImageGenerator
{
    public static DiscImageResult Generate(DvdImageRequest request,
        IProgress<DiscJobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentNullException.ThrowIfNull(request.Options);
        request.Options.Validate();
        string outputPath = Path.GetFullPath(request.OutputPath);
        progress?.Report(new("dvd", "prepare", "Preparing DVD image…"));
        cancellationToken.ThrowIfCancellationRequested();
        MultiBlockSolveOptions options = DvdStreamingGenerator.CreateSolveOptions(request.Options,
            0, request.Options.OutputSectors);
        // The historical fast CLI records 64 even though this setting is not used
        // by dispersion. Keep the sidecar schema and values identical.
        options = options with
        {
            ImageMapping = options.ImageMapping with { SampleEveryChannelBits = 64 },
        };
        FastDispersionWriteResult result = FastDvdImageFileWriter.Write(request.SourceImage,
            outputPath, options, value => progress?.Report(new(
                "dvd", "generate", $"Generating DVD image: {value.CompletedBlocks}/{value.TotalBlocks} blocks",
                (double)value.CompletedBlocks / value.TotalBlocks,
                (long)value.CompletedBlocks * 32768, (long)value.TotalBlocks * 32768, value.Elapsed)),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string summary = $"DVD image completed: {result.CompletedBlocks} blocks, {result.ControlledWords} controlled words.";
        progress?.Report(new("dvd", "complete", summary, 1,
            (long)result.FilledSectors * 2048, (long)result.FilledSectors * 2048, result.Elapsed));
        return new DiscImageResult(outputPath, summary);
    }
}
