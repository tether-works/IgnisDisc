using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Interop;
using Xunit;

namespace CdBurner.Hardware.Tests;

public sealed class MediaFoundationFloatTests : IDisposable
{
    private readonly string _tempDir;

    public MediaFoundationFloatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_MfFloat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void OpenRedBookPcmStreamViaMediaFoundation_32BitFloatWav_DecodesAccurateRedBookPcm()
    {
        const int sampleRate = 44100;
        const int frameCount = 4410;
        byte[] floatData = new byte[frameCount * 8];
        for (int i = 0; i < frameCount; i++)
        {
            float sample = 0.5f * MathF.Sin(2.0f * MathF.PI * 440.0f * i / sampleRate);
            BinaryPrimitives.WriteSingleLittleEndian(floatData.AsSpan(i * 8, 4), sample);
            BinaryPrimitives.WriteSingleLittleEndian(floatData.AsSpan(i * 8 + 4, 4), sample);
        }

        string wavPath = Path.Combine(_tempDir, "float32.wav");
        byte[] header = WavFileCodec.CreateWavHeader(floatData.Length, sampleRate, 2, 32, isFloat: true);
        using (var fs = File.Create(wavPath))
        {
            fs.Write(header);
            fs.Write(floatData);
        }

        using var decoder = new MediaFoundationAudioDecoder();
        using Stream pcmStream = decoder.OpenRedBookPcmStreamViaMediaFoundation(wavPath);
        Assert.Equal(frameCount * 4, pcmStream.Length);

        byte[] decoded = new byte[pcmStream.Length];
        pcmStream.ReadExactly(decoded);

        // Peak at 0.5 full scale should be around 16384 in signed 16-bit PCM
        short maxSample = 0;
        for (int i = 0; i < decoded.Length; i += 2)
        {
            short s = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(i, 2)));
            if (s > maxSample) maxSample = s;
        }
        Assert.InRange(maxSample, 16300, 16450);
    }

    [Fact]
    public void DecodeFromSourceReader_ThrowsComException_WhenFallbackSetCurrentMediaTypeFails()
    {
        MediaFoundationAudioDecoder.EnsureMediaFoundationInitialized();
        var reader = new FakeSourceReader(acceptPcm: false, acceptFloat: false, negotiatedType: null, floatPayload: Array.Empty<byte>());

        Assert.Throws<COMException>(() => MediaFoundationAudioDecoder.DecodeFromSourceReader(reader, "broken.wma"));
    }

    [Fact]
    public void DecodeFromSourceReader_FallsBackToFloatSubtype_AndConvertsToIntegerRedBookPcm()
    {
        MediaFoundationAudioDecoder.EnsureMediaFoundationInitialized();

        const int sampleRate = 48000;
        const short channels = 2;
        const int frames = 480;
        byte[] floatPayload = new byte[frames * channels * 4];
        for (int i = 0; i < frames; i++)
        {
            float sample = 0.5f * MathF.Sin(2.0f * MathF.PI * 1000.0f * i / sampleRate);
            BinaryPrimitives.WriteSingleLittleEndian(floatPayload.AsSpan(i * 8, 4), sample);
            BinaryPrimitives.WriteSingleLittleEndian(floatPayload.AsSpan(i * 8 + 4, 4), sample);
        }

        var negotiated = new FakeMediaType(MediaFoundationGuids.MFAudioFormat_Float, (uint)channels, sampleRate, 32);
        var reader = new FakeSourceReader(acceptPcm: false, acceptFloat: true, negotiated, floatPayload);

        using Stream decodedStream = MediaFoundationAudioDecoder.DecodeFromSourceReader(reader, "float_only.m4a");
        byte[] actual = new byte[decodedStream.Length];
        decodedStream.ReadExactly(actual);

        // Compare against WavFileCodec decoding of the exact same float payload
        byte[] wavHeader = WavFileCodec.CreateWavHeader(floatPayload.Length, sampleRate, channels, 32, isFloat: true);
        using var refStream = new MemoryStream();
        refStream.Write(wavHeader);
        refStream.Write(floatPayload);
        refStream.Position = 0;
        byte[] expected = WavFileCodec.DecodeToRedBookPcm(refStream);

        Assert.Equal(expected, actual);
    }

    private sealed class FakeMediaType(Guid subtype, uint channels, uint sampleRate, uint bitsPerSample) : IMFMediaType
    {
        public int GetGUID(Guid guidKey, out Guid pguidValue)
        {
            if (guidKey == MediaFoundationGuids.MF_MT_SUBTYPE)
            {
                pguidValue = subtype;
                return 0;
            }
            pguidValue = Guid.Empty;
            return unchecked((int)0xC00D36E6);
        }

        public int GetUINT32(Guid guidKey, out uint punValue)
        {
            if (guidKey == MediaFoundationGuids.MF_MT_AUDIO_NUM_CHANNELS) { punValue = channels; return 0; }
            if (guidKey == MediaFoundationGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND) { punValue = sampleRate; return 0; }
            if (guidKey == MediaFoundationGuids.MF_MT_AUDIO_BITS_PER_SAMPLE) { punValue = bitsPerSample; return 0; }
            punValue = 0;
            return unchecked((int)0xC00D36E6);
        }

        public int GetItem(Guid guidKey, IntPtr pValue) => 0;
        public int GetItemType(Guid guidKey, out int pType) { pType = 0; return 0; }
        public int CompareItem(Guid guidKey, IntPtr Value, out bool pbResult) { pbResult = false; return 0; }
        public int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult) { pbResult = false; return 0; }
        public int GetUINT64(Guid guidKey, out ulong punValue) { punValue = 0; return 0; }
        public int GetDouble(Guid guidKey, out double pfValue) { pfValue = 0; return 0; }
        public int GetStringLength(Guid guidKey, out uint pcchLength) { pcchLength = 0; return 0; }
        public int GetString(Guid guidKey, StringBuilder pwszValue, uint cchBufSize, out uint pcchLength) { pcchLength = 0; return 0; }
        public int GetAllocatedString(Guid guidKey, out IntPtr ppwszValue, out uint pcchLength) { ppwszValue = IntPtr.Zero; pcchLength = 0; return 0; }
        public int GetBlobSize(Guid guidKey, out uint pcbBlobSize) { pcbBlobSize = 0; return 0; }
        public int GetBlob(Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize) { pcbBlobSize = 0; return 0; }
        public int GetAllocatedBlob(Guid guidKey, out IntPtr ppBuf, out uint pcbSize) { ppBuf = IntPtr.Zero; pcbSize = 0; return 0; }
        public int GetUnknown(Guid guidKey, Guid riid, out IntPtr ppv) { ppv = IntPtr.Zero; return 0; }
        public int SetItem(Guid guidKey, IntPtr Value) => 0;
        public int DeleteItem(Guid guidKey) => 0;
        public int DeleteAllItems() => 0;
        public int SetUINT32(Guid guidKey, uint unValue) => 0;
        public int SetUINT64(Guid guidKey, ulong unValue) => 0;
        public int SetDouble(Guid guidKey, double fValue) => 0;
        public int SetGUID(Guid guidKey, Guid guidValue) => 0;
        public int SetString(Guid guidKey, string wszValue) => 0;
        public int SetBlob(Guid guidKey, IntPtr pBuf, uint cbBufSize) => 0;
        public int SetUnknown(Guid guidKey, object pUnknown) => 0;
        public int LockStore() => 0;
        public int UnlockStore() => 0;
        public int GetCount(out uint pcItems) { pcItems = 0; return 0; }
        public int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue) { pguidKey = Guid.Empty; return 0; }
        public int CopyAllItems(IMFAttributes pDest) => 0;
        public int GetMajorType(out Guid pguidMajorType) { pguidMajorType = MediaFoundationGuids.MFMediaType_Audio; return 0; }
        public int IsCompressedFormat(out bool pfCompressed) { pfCompressed = false; return 0; }
        public int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags) { pdwFlags = 0; return 0; }
        public int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation) { ppvRepresentation = IntPtr.Zero; return 0; }
        public int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation) => 0;
    }

    private sealed class FakeMediaBuffer(byte[] bytes) : IMFMediaBuffer
    {
        private GCHandle _pin;

        public int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength)
        {
            _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            ppbBuffer = _pin.AddrOfPinnedObject();
            pcbMaxLength = (uint)bytes.Length;
            pcbCurrentLength = (uint)bytes.Length;
            return 0;
        }

        public int Unlock()
        {
            if (_pin.IsAllocated)
            {
                _pin.Free();
            }
            return 0;
        }

        public int GetCurrentLength(out uint pcbCurrentLength) { pcbCurrentLength = (uint)bytes.Length; return 0; }
        public int SetCurrentLength(uint cbCurrentLength) => 0;
        public int GetMaxLength(out uint pcbMaxLength) { pcbMaxLength = (uint)bytes.Length; return 0; }
    }

    private sealed class FakeSample(byte[] bytes) : IMFSample
    {
        public int GetBufferCount(out uint pdwBufferCount) { pdwBufferCount = 1; return 0; }
        public int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer) { ppBuffer = new FakeMediaBuffer(bytes); return 0; }
        public int GetItem(Guid guidKey, IntPtr pValue) => 0;
        public int GetItemType(Guid guidKey, out int pType) { pType = 0; return 0; }
        public int CompareItem(Guid guidKey, IntPtr Value, out bool pbResult) { pbResult = false; return 0; }
        public int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult) { pbResult = false; return 0; }
        public int GetUINT32(Guid guidKey, out uint punValue) { punValue = 0; return 0; }
        public int GetUINT64(Guid guidKey, out ulong punValue) { punValue = 0; return 0; }
        public int GetDouble(Guid guidKey, out double pfValue) { pfValue = 0; return 0; }
        public int GetGUID(Guid guidKey, out Guid pguidValue) { pguidValue = Guid.Empty; return 0; }
        public int GetStringLength(Guid guidKey, out uint pcchLength) { pcchLength = 0; return 0; }
        public int GetString(Guid guidKey, StringBuilder pwszValue, uint cchBufSize, out uint pcchLength) { pcchLength = 0; return 0; }
        public int GetAllocatedString(Guid guidKey, out IntPtr ppwszValue, out uint pcchLength) { ppwszValue = IntPtr.Zero; pcchLength = 0; return 0; }
        public int GetBlobSize(Guid guidKey, out uint pcbBlobSize) { pcbBlobSize = 0; return 0; }
        public int GetBlob(Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize) { pcbBlobSize = 0; return 0; }
        public int GetAllocatedBlob(Guid guidKey, out IntPtr ppBuf, out uint pcbSize) { ppBuf = IntPtr.Zero; pcbSize = 0; return 0; }
        public int GetUnknown(Guid guidKey, Guid riid, out IntPtr ppv) { ppv = IntPtr.Zero; return 0; }
        public int SetItem(Guid guidKey, IntPtr Value) => 0;
        public int DeleteItem(Guid guidKey) => 0;
        public int DeleteAllItems() => 0;
        public int SetUINT32(Guid guidKey, uint unValue) => 0;
        public int SetUINT64(Guid guidKey, ulong unValue) => 0;
        public int SetDouble(Guid guidKey, double fValue) => 0;
        public int SetGUID(Guid guidKey, Guid guidValue) => 0;
        public int SetString(Guid guidKey, string wszValue) => 0;
        public int SetBlob(Guid guidKey, IntPtr pBuf, uint cbBufSize) => 0;
        public int SetUnknown(Guid guidKey, object pUnknown) => 0;
        public int LockStore() => 0;
        public int UnlockStore() => 0;
        public int GetCount(out uint pcItems) { pcItems = 0; return 0; }
        public int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue) { pguidKey = Guid.Empty; return 0; }
        public int CopyAllItems(IMFAttributes pDest) => 0;
        public int GetSampleFlags(out uint pdwSampleFlags) { pdwSampleFlags = 0; return 0; }
        public int SetSampleFlags(uint dwSampleFlags) => 0;
        public int GetSampleTime(out long pllSampleTime) { pllSampleTime = 0; return 0; }
        public int SetSampleTime(long llSampleTime) => 0;
        public int GetSampleDuration(out long pllSampleDuration) { pllSampleDuration = 0; return 0; }
        public int SetSampleDuration(long llSampleDuration) => 0;
        public int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer) { ppBuffer = new FakeMediaBuffer(bytes); return 0; }
        public int AddBuffer(IMFMediaBuffer pBuffer) => 0;
        public int RemoveBufferByIndex(uint dwIndex) => 0;
        public int RemoveAllBuffers() => 0;
        public int GetTotalLength(out uint pcbTotalLength) { pcbTotalLength = (uint)bytes.Length; return 0; }
        public int CopyToBuffer(IMFMediaBuffer pBuffer) => 0;
    }

    private sealed class FakeSourceReader(bool acceptPcm, bool acceptFloat, IMFMediaType? negotiatedType, byte[] floatPayload) : IMFSourceReader
    {
        private bool _delivered;

        public int GetStreamSelection(uint dwStreamIndex, out bool pfSelected) { pfSelected = true; return 0; }
        public int SetStreamSelection(uint dwStreamIndex, bool fSelected) => 0;
        public int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType? ppMediaType) { ppMediaType = negotiatedType; return 0; }
        public int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType? ppMediaType)
        {
            ppMediaType = negotiatedType;
            return negotiatedType != null ? 0 : unchecked((int)0x80004005);
        }

        public int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType)
        {
            if (pMediaType.GetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, out Guid subtype) == 0)
            {
                if (subtype == MediaFoundationGuids.MFAudioFormat_PCM && acceptPcm) return 0;
                if (subtype == MediaFoundationGuids.MFAudioFormat_Float && acceptFloat) return 0;
            }
            return unchecked((int)0xC00D36B4); // MF_E_INVALIDMEDIATYPE
        }

        public int SetCurrentPosition(Guid guidTimeFormat, ref PropVariant varPosition) => 0;

        public int ReadSample(uint dwStreamIndex, uint dwControlFlags, out uint pdwActualStreamIndex, out uint pdwStreamFlags, out long pllTimestamp, out IMFSample? ppSample)
        {
            pdwActualStreamIndex = 0;
            pllTimestamp = 0;
            if (!_delivered)
            {
                _delivered = true;
                pdwStreamFlags = 0;
                ppSample = new FakeSample(floatPayload);
                return 0;
            }
            pdwStreamFlags = MediaFoundationConstants.MF_SOURCE_READERF_ENDOFSTREAM;
            ppSample = null;
            return 0;
        }

        public int Flush(uint dwStreamIndex) => 0;
        public int GetServiceForStream(uint dwStreamIndex, Guid guidService, Guid riid, out IntPtr ppvObject) { ppvObject = IntPtr.Zero; return 0; }
        public int GetPresentationAttribute(uint dwStreamIndex, Guid guidKey, out PropVariant pValue) { pValue = default; return 0; }
    }
}
