using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Confirms that real write errors, a wrong table of contents, and a failed close still fail picture-only
/// CD and DVD burns while sector read-back defects on the picture area do not.
/// </summary>
public sealed class DiscArtNativeFailureVerificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-picfail-" + Guid.NewGuid().ToString("N"));

    public DiscArtNativeFailureVerificationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] DvdPictureBytes(int sectors = 2048)
    {
        byte[] data = new byte[sectors * OpticalConstants.DataSectorSize];
        new Random(19).NextBytes(data);
        return data;
    }

    private static List<AudioTrackSource> PictureCdTracks(int sectors = 600)
    {
        byte[] pcm = new byte[sectors * OpticalConstants.RawSectorSize];
        new Random(23).NextBytes(pcm);
        return
        [
            new AudioTrackSource
            {
                TrackNumber = 1,
                Title = "Picture",
                Duration = TimeSpan.FromSeconds(sectors / 75.0),
                TotalPcmBytes = pcm.LongLength,
                PcmStreamFactory = () => new MemoryStream(pcm, writable: false),
                IsPictureTrack = true
            }
        ];
    }

    [Fact]
    public async Task PictureDvd_RejectsSynchronizeCache_WrongToc_AndFailedClose_AllFail()
    {
        foreach (Action<VirtualMmcDrive> fault in new Action<VirtualMmcDrive>[]
        {
            d => d.RejectsSynchronizeCache = true,
            d => d.WrongDataToc = true,
            d => d.ClosesDiscAfterWrite = false,
        })
        {
            using var recorder = new VirtualDiscRecorder("V-DVD", "IGNIS", "VIRTUAL", @"V:\");
            using var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusR, 4096);
            recorder.InsertMedia(medium);

            var engine = new NativeDataBurnEngine(new VirtualBurnEngine(recorder), _dir)
            {
                ConfigureVirtualDrive = fault
            };
            var service = new DataBurnService(engine);
            byte[] image = DvdPictureBytes();

            DataBurnResult result = await service.BurnStreamAsync(recorder, new MemoryStream(image, writable: false), new DataBurnOptions
            {
                VerifyAfterBurn = true,
                CloseDisc = true,
                PictureZone = new SectorRange(0, image.Length / OpticalConstants.DataSectorSize)
            });

            Assert.False(result.Success);
        }
    }

    [Fact]
    public async Task PictureDvd_WithPictureZoneReadErrorsAndDifferences_SucceedsWithExplicitFlags()
    {
        using var recorder = new VirtualDiscRecorder("V-DVD-OK", "IGNIS", "VIRTUAL", @"V:\");
        using var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusR, 4096);
        recorder.InsertMedia(medium);

        var engine = new NativeDataBurnEngine(new VirtualBurnEngine(recorder), _dir)
        {
            ConfigureVirtualDrive = d =>
            {
                d.ReadErrorLbas.Add(0);
                d.ReadErrorLbas.Add(500);
                d.ReadBackDifferenceLbas.Add(10);
                d.ReadBackDifferenceLbas.Add(250);
            }
        };
        var service = new DataBurnService(engine);
        byte[] image = DvdPictureBytes();

        DataBurnResult result = await service.BurnStreamAsync(recorder, new MemoryStream(image, writable: false), new DataBurnOptions
        {
            VerifyAfterBurn = true,
            CloseDisc = true,
            PictureZone = new SectorRange(0, image.Length / OpticalConstants.DataSectorSize)
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.WritesAccepted);
        Assert.True(result.SessionClosed);
        Assert.True(result.TocChecked);
        Assert.False(result.Verified);
        Assert.False(result.PictureAreaCompared);
        Assert.False(result.FilesCompared);
        Assert.Contains("picture area was not compared", result.VerificationNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PictureCd_RejectsSynchronizeCache_WrongToc_AndFailedClose_AllFail()
    {
        foreach (Action<VirtualMmcDrive> fault in new Action<VirtualMmcDrive>[]
        {
            d => d.RejectsSynchronizeCache = true,
            d => d.WrongDataToc = true,
            d => d.ClosesDiscAfterWrite = false,
        })
        {
            using var recorder = new VirtualDiscRecorder("V-CD", "IGNIS", "VIRTUAL", @"V:\");
            using var medium = VirtualDiscMedium.CreateBlankCdR700();
            recorder.InsertMedia(medium);

            var engine = new NativeAudioBurnEngine(_dir)
            {
                ConfigureVirtualDrive = fault
            };
            BurnOperationResult result = await engine.BurnAudioTracksAsync(recorder, PictureCdTracks(), new AudioBurnOptions
            {
                UseDiscAtOnce = true
            }, null, CancellationToken.None);

            Assert.False(result.Success);
        }
    }

    [Fact]
    public async Task PictureCd_WithPictureTrackReadErrorsAndDifferences_SucceedsWithExplicitFlags()
    {
        using var recorder = new VirtualDiscRecorder("V-CD-OK", "IGNIS", "VIRTUAL", @"V:\");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new NativeAudioBurnEngine(_dir)
        {
            ConfigureVirtualDrive = d =>
            {
                d.ReadErrorLbas.Add(0);
                d.ReadErrorLbas.Add(400);
                d.ReadBackDifferenceLbas.Add(12);
                d.ReadBackDifferenceLbas.Add(220);
            }
        };
        BurnOperationResult result = await engine.BurnAudioTracksAsync(recorder, PictureCdTracks(), new AudioBurnOptions
        {
            UseDiscAtOnce = true
        }, null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.WritesAccepted);
        Assert.True(result.SessionClosed);
        Assert.True(result.TocChecked);
        Assert.NotNull(engine.LastResult?.Verification);
        Assert.False(engine.LastResult!.Verification!.AudioCompared);
        Assert.False(engine.LastResult.Verification.PictureAreaCompared);
        Assert.True(engine.LastResult.Verification.HasPictureArea);
        Assert.Contains("picture area was not compared", engine.LastResult.Verification.Message, StringComparison.OrdinalIgnoreCase);
    }
}
