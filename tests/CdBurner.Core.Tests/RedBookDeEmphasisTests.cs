using System.Buffers.Binary;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class RedBookDeEmphasisTests
{
    private const int SampleRate = OpticalConstants.CdAudioSampleRate;

    // Exact Red Book 50/15 us analog transfer function |H(jw)| = |(1 + j*w*15us) / (1 + j*w*50us)|.
    private static double RedBookMagnitudeDb(double freqHz)
    {
        double w = 2.0 * Math.PI * freqHz;
        double numSq = 1.0 + Math.Pow(w * 15e-6, 2);
        double denSq = 1.0 + Math.Pow(w * 50e-6, 2);
        return 20.0 * Math.Log10(Math.Sqrt(numSq / denSq));
    }

    private static byte[] GenerateStereoSine(double freqHz, int frames, double amplitude = 0.7)
    {
        byte[] pcm = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)Math.Round(amplitude * short.MaxValue * Math.Sin(2.0 * Math.PI * freqHz * i / SampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), sample);
        }
        return pcm;
    }

    private static double MeasureRmsDb(ReadOnlySpan<byte> pcm, int channelOffsetBytes, int skipFrames)
    {
        int totalFrames = pcm.Length / 4;
        double sumSq = 0.0;
        int count = totalFrames - skipFrames;
        for (int i = skipFrames; i < totalFrames; i++)
        {
            double s = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 4 + channelOffsetBytes, 2));
            sumSq += s * s;
        }
        return 20.0 * Math.Log10(Math.Sqrt(sumSq / count));
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(1000.0)]
    [InlineData(3183.1)]
    [InlineData(5000.0)]
    [InlineData(10610.3)]
    [InlineData(15000.0)]
    [InlineData(20000.0)]
    public void ApplyDeEmphasis_MatchesRedBook50_15UsCurveWithinHalfDb(double freqHz)
    {
        const int totalFrames = 4410; // 100 ms
        const int settleFrames = 882; // skip first 20 ms transient
        byte[] input = GenerateStereoSine(freqHz, totalFrames);
        byte[] output = RedBookPcmDspProcessor.ApplyDeEmphasis(input, SampleRate);

        double inRmsDb = MeasureRmsDb(input, 0, settleFrames);
        double outRmsDb = MeasureRmsDb(output, 0, settleFrames);
        double measuredGainDb = outRmsDb - inRmsDb;
        double expectedGainDb = RedBookMagnitudeDb(freqHz);

        Assert.InRange(Math.Abs(measuredGainDb - expectedGainDb), 0.0, 0.5);
    }

    [Fact]
    public void ApplyDeEmphasis_KeepsLeftAndRightChannelStatesIndependentAndSilenceZero()
    {
        const int frames = 1000;
        byte[] pcm = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            short tone = (short)Math.Round(0.5 * short.MaxValue * Math.Sin(2.0 * Math.PI * 12000.0 * i / SampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), tone);
            // Right channel stays silent (0)
        }

        byte[] output = RedBookPcmDspProcessor.ApplyDeEmphasis(pcm, SampleRate);
        for (int i = 0; i < frames; i++)
        {
            short r = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 4 + 2, 2));
            Assert.Equal(0, r);
        }

        byte[] silence = new byte[frames * 4];
        Assert.Equal(silence, RedBookPcmDspProcessor.ApplyDeEmphasis(silence, SampleRate));
    }

    [Fact]
    public void ApplyDeEmphasisToWavFile_MatchesInMemoryAcrossStreamChunkBoundary()
    {
        // > 64 KiB so the streaming WAV helper spans multiple buffer reads while keeping IIR filter state continuous.
        byte[] pcm = GenerateStereoSine(10000.0, frames: 30_000); // 120,000 bytes
        byte[] expectedPcm = RedBookPcmDspProcessor.ApplyDeEmphasis(pcm, SampleRate);

        string tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_DeEmph_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string srcWav = Path.Combine(tempDir, "src.wav");
            string dstWav = Path.Combine(tempDir, "dst.wav");
            WavFileCodec.WriteWavFile(srcWav, pcm);

            RedBookPcmDspProcessor.ApplyDeEmphasisToWavFile(srcWav, dstWav);
            byte[] dstBytes = File.ReadAllBytes(dstWav);
            Assert.Equal(expectedPcm, dstBytes[44..]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
