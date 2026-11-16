// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CdBurner.Lithography.Calibration;

namespace CdBurner.App.ViewModels;

/// <summary>
/// Reads a photo (PNG, JPEG, BMP, TIFF: whatever Windows Imaging decodes) and returns it as PNG bytes, scaled down
/// so the longest side is at most <c>maxSide</c> pixels. The calibration record embeds the photo as PNG, and the
/// outline points are in the pixels of exactly this picture.
/// </summary>
public static class CalibrationPhotoFile
{
    /// <summary>Largest photo file read.</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    public static byte[] LoadAsPng(string path, int maxSide)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxSide < 16) throw new ArgumentOutOfRangeException(nameof(maxSide));
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists) throw new FileNotFoundException("The photo was not found.", info.FullName);
        if (info.Length > MaxFileBytes) throw new IOException("The photo file is too large.");

        BitmapSource frame;
        using (FileStream stream = new(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new NotSupportedException("The file holds no picture.");
            frame = decoder.Frames[0];
        }

        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 1L << 30)
            throw new NotSupportedException("The picture size is not supported.");
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
        BitmapSource working = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
        if (!CalibrationPhotoLimits.Contains(working.PixelWidth, working.PixelHeight))
            throw new NotSupportedException("The picture is too large.");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(working));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
