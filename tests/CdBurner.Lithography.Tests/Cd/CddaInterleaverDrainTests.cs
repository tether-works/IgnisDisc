using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Tests.Cd;

/// <summary>
/// The CD-DA delay interleaver at both ends of a finite stretch, checked against the drive's CIRC data path written
/// out from ECMA-130 annex C figure C.4 (<see cref="CircEncoderReference"/>). Upstream's audit
/// (docs/PHYSICAL_MODEL_AUDIT.md, finding four) found 1,332 zero placeholders at the start and 1,332 bytes of the
/// stretch never sent at the end; a primed stream that ends with its lead-out has neither.
/// </summary>
public sealed class CddaInterleaverDrainTests
{
    private const int SectorBytes = 2352;

    /// <summary>Bytes are interleaved by position only, so a 16-bit tag per byte is followed through two runs
    /// (low and high byte). Tag 0 is never used, so a 0 in the output is a placeholder.</summary>
    private static int[] Interleave(int[] leadIn, int[] stretch, int[] leadOut)
    {
        byte[] Run(int shift)
        {
            CddaInterleaver interleaver = new(CdAudioByteOrder.BigEndian);
            using MemoryStream output = new();
            interleaver.Prime(leadIn.Select(t => (byte)(t >> shift)).ToArray());
            interleaver.Add(stretch.Select(t => (byte)(t >> shift)).ToArray(), output);
            foreach (int tag in leadOut) interleaver.Add((byte)(tag >> shift), output);
            interleaver.Flush(output);
            return output.ToArray();
        }

        byte[] low = Run(0);
        byte[] high = Run(8);
        Assert.Equal(low.Length, high.Length);
        return Enumerable.Range(0, low.Length).Select(i => low[i] | (high[i] << 8)).ToArray();
    }

    private static int[] Tags(int first, int count) => Enumerable.Range(first, count).ToArray();

    [Fact]
    public void Primed_stream_sends_every_stretch_byte_once_and_no_placeholder()
    {
        int context = CddaInterleaver.ContextBytes;
        Assert.Equal(CircEncoderReference.LongestDelayFrames * CircEncoderReference.BytesPerFrame, context);
        int stretchLength = (10 * SectorBytes) - context; // the whole stream fills exactly ten sectors
        int[] leadIn = Tags(1, context);
        int[] stretch = Tags(1 + context, stretchLength);
        int[] leadOut = Tags(1 + context + stretchLength, context);

        int[] stream = Interleave(leadIn, stretch, leadOut);

        Assert.Equal(10 * SectorBytes, stream.Length);
        Assert.DoesNotContain(0, stream);
        Assert.Equal(stream.Length, stream.Distinct().Count());
        HashSet<int> sent = [.. stream];
        Assert.All(stretch, tag => Assert.Contains(tag, sent));

        // Through the drive's encoder the stretch comes out in order, every data slot of every frame filled, at the
        // reference shift; the lead-in and lead-out bytes fill the frames before and after it.
        int[] encoded = CircEncoderReference.Encode(stream);
        for (int index = 0; index < stretchLength; index++)
        {
            int frame = (index / CircEncoderReference.BytesPerFrame) + CircEncoderReference.ReferenceShiftFrames;
            int slot = index % CircEncoderReference.BytesPerFrame;
            Assert.Equal(stretch[index], encoded[(frame * CircEncoderReference.BytesPerFrame) + slot]);
        }
    }

    [Fact]
    public void The_lead_in_fills_exactly_the_positions_upstream_left_as_placeholders()
    {
        int context = CddaInterleaver.ContextBytes;
        int[] leadIn = Tags(1, context);
        int[] stretch = Tags(1 + context, (10 * SectorBytes) - context);
        int[] leadOut = Tags(1 + context + stretch.Length, context);

        int[] stream = Interleave(leadIn, stretch, leadOut);

        HashSet<int> leadInTags = [.. leadIn];
        int fromLeadIn = 0;
        for (int position = 0; position < stream.Length; position++)
        {
            bool isLeadIn = leadInTags.Contains(stream[position]);
            Assert.Equal(CircEncoderReference.IsBeforeStretch(position), isLeadIn);
            if (isLeadIn) fromLeadIn++;
        }

        Assert.Equal(1_332, fromLeadIn); // the audit's placeholder count
    }

    [Fact]
    public void Ending_without_a_full_lead_out_is_refused()
    {
        CddaInterleaver interleaver = new(CdAudioByteOrder.BigEndian);
        interleaver.Prime(new byte[CddaInterleaver.ContextBytes]);
        interleaver.Add(new byte[CddaInterleaver.ContextBytes - 24], Stream.Null);

        Assert.Throws<InvalidOperationException>(() => interleaver.Flush(Stream.Null));
    }

    [Fact]
    public void Ending_an_unprimed_stream_is_refused()
    {
        CddaInterleaver interleaver = new(CdAudioByteOrder.BigEndian);
        interleaver.Add(new byte[SectorBytes * 2], Stream.Null);

        Assert.Throws<InvalidOperationException>(() => interleaver.Flush(Stream.Null));
        Assert.Throws<InvalidOperationException>(() => interleaver.Prime(new byte[CddaInterleaver.ContextBytes]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_generated_picture_track_holds_only_palette_bytes(bool parallel)
    {
        CdDiscParameters geometry = new(24.5, 56.8, 1200);
        BgraRaster picture = PngRaster.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamGoldens", "quadrants-64.png"));
        using MemoryStream output = new();

        CdTrackGenerator.GenerateToStream(picture, output, geometry, interleave: true,
            audioByteOrder: CdAudioByteOrder.BigEndian, useParallelSampling: parallel);

        byte[] track = output.ToArray();
        Assert.Equal(geometry.TotalBytes, track.Length);
        byte[] palette = CdToneMapping.Palette(2).ToArray();
        int firstOther = Array.FindIndex(track, value => Array.IndexOf(palette, value) < 0);
        Assert.Equal(-1, firstOther);
    }

    /// <summary>The edge zones named for page layouts agree with the figure C.4 data path: logical byte i of a track
    /// leaves the encoder in frame i / 24 + 111, so the last 111 frames of logical positions fall after the track.
    /// </summary>
    [Fact]
    public void Edge_zones_match_the_drive_encoder_timing()
    {
        Assert.Equal(CircEncoderReference.ReferenceShiftFrames, CdCircEdgeZones.ReferenceOffsetFrames);
        int context = CddaInterleaver.ContextBytes;
        int trackBytes = 10 * SectorBytes;
        int[] leadIn = Tags(1, context);
        int[] logical = Tags(1 + context, trackBytes);
        int[] stream = Interleave(leadIn, logical[..^context], logical[^context..]);
        int[] encoded = CircEncoderReference.Encode(stream);
        long end = CdCircEdgeZones.LogicalDrawableEndByte(trackBytes);
        int trackFrames = trackBytes / CircEncoderReference.BytesPerFrame;

        int FrameOf(int logicalIndex) => Array.IndexOf(encoded, logical[logicalIndex]) / CircEncoderReference.BytesPerFrame;

        Assert.Equal(CdCircEdgeZones.InnerEdgeZoneBytes / CircEncoderReference.BytesPerFrame, FrameOf(0));
        Assert.Equal(trackFrames - 1, FrameOf((int)end - 1));
        Assert.Equal(trackFrames, FrameOf((int)end));
        Assert.Throws<ArgumentOutOfRangeException>(() => CdCircEdgeZones.LogicalDrawableEndByte(2 * 2_664));
    }

    /// <summary>
    /// A CIRC F1-frame is 24 bytes = 6 stereo 16-bit samples. Shifting a pre-interleaved CD-DA stream by a multiple
    /// of 6 samples keeps every byte in its frame column so all bytes of a logical frame emerge together in order;
    /// shifting by anything else moves bytes across frame columns with different delays and scatters the picture.
    /// </summary>
    [Theory]
    [InlineData(-6, true)]
    [InlineData(6, true)]
    [InlineData(-7, false)]
    [InlineData(13, false)]
    public void Shifting_interleaved_stream_keeps_circ_frames_intact_only_for_multiples_of_six_samples(
        int shiftSamples, bool sharp)
    {
        int context = CddaInterleaver.ContextBytes;
        int stretchLength = (10 * SectorBytes) - context;
        int[] leadIn = Tags(1, context);
        int[] stretch = Tags(1 + context, stretchLength);
        int[] leadOut = Tags(1 + context + stretchLength, context);
        int[] stream = Interleave(leadIn, stretch, leadOut);

        int shiftBytes = shiftSamples * 4;
        int[] shifted = new int[stream.Length];
        if (shiftBytes >= 0)
        {
            Array.Copy(stream, 0, shifted, shiftBytes, stream.Length - shiftBytes);
        }
        else
        {
            Array.Copy(stream, -shiftBytes, shifted, 0, stream.Length + shiftBytes);
        }

        int[] encoded = CircEncoderReference.Encode(shifted);

        // Compare logical frame 30 (well away from the shifted ends) through the ECMA-130 C.4 encoder.
        int baseIndex = 30 * CircEncoderReference.BytesPerFrame;
        int[] frames = Enumerable.Range(0, CircEncoderReference.BytesPerFrame)
            .Select(slot => Array.IndexOf(encoded, stretch[baseIndex + slot]) / CircEncoderReference.BytesPerFrame)
            .ToArray();

        if (sharp)
        {
            int expectedFrame = 30 + CircEncoderReference.ReferenceShiftFrames + (shiftSamples / 6);
            Assert.All(frames, frame => Assert.Equal(expectedFrame, frame));
            for (int slot = 0; slot < CircEncoderReference.BytesPerFrame; slot++)
            {
                Assert.Equal(stretch[baseIndex + slot], encoded[(expectedFrame * CircEncoderReference.BytesPerFrame) + slot]);
            }
        }
        else
        {
            Assert.True(frames.Distinct().Count() > 1, "A non-multiple-of-6-sample shift must scatter one F1-frame across multiple output frames.");
        }
    }
}
