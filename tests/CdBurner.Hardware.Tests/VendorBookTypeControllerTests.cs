using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public sealed class VendorBookTypeControllerTests
{
    private sealed class RecordingTransport : IScsiTransport
    {
        private readonly IScsiTransport _inner;
        public List<byte[]> CdbLog { get; } = [];

        public RecordingTransport(IScsiTransport inner) => _inner = inner;

        public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> dataBuffer, byte dataDirection, TimeSpan timeout)
        {
            CdbLog.Add(cdb.ToArray());
            return _inner.ExecuteCommand(cdb, dataBuffer, dataDirection, timeout);
        }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class FakePhysicalRecorder : IDiscRecorderBackend
    {
        public MediaStatusDto Status { get; init; } = new() { MediaPresent = true, MediaType = DiscMediaType.DvdPlusR, IsBlank = true, FreeSectors = 2_295_104 };
        public string UniqueId => "PHYS-FAKE";
        public string VendorId { get; init; } = "PIONEER";
        public string ProductId { get; init; } = "DVD-RW DVR-221";
        public string ProductRevision => "1.00";
        public string VolumePath => "Z:\\";
        public bool IsVirtual => false;
        public DriveCapabilitiesDto GetCapabilities() => new();
        public MediaStatusDto QueryMediaStatus() => Status;
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    private sealed class NoOpBurnEngine : IBurnEngineBackend
    {
        public int BurnCalls { get; private set; }

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            BurnCalls++;
            int sectors = (int)(imageStream.Length / 2048);
            return Task.FromResult(BurnOperationResult.Succeeded(sectors, TimeSpan.Zero));
        }

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public void ReadCurrentBookType_ReadsFormat00PhysicalFormatInformationFromVirtualMmcDrive()
    {
        using var drive = new VirtualMmcDrive { Profile = NativeTaoDataWriter.ProfileDvdPlusRDualLayer };
        var controller = new VendorBookTypeController();

        var initial = controller.ReadCurrentBookType(drive);
        Assert.True(initial.Success, initial.Diagnostic);
        Assert.Equal(DvdBookType.DvdPlusRDualLayer, initial.BookType);
        Assert.Equal((byte)1, initial.PartVersion);

        drive.DvdBookType = DvdBookType.DvdRom;
        var updated = controller.ReadCurrentBookType(drive);
        Assert.True(updated.Success, updated.Diagnostic);
        Assert.Equal(DvdBookType.DvdRom, updated.BookType);
    }

    [Fact]
    public void SetBookType_StandardMmc0xBf_UpdatesAndVerifiesVia0xAdOnVirtualMmcDrive()
    {
        using var drive = new VirtualMmcDrive
        {
            InquiryVendorId = "IGNIS",
            InquiryProductId = "VIRTUAL DVD/CD-RW",
            Profile = NativeTaoDataWriter.ProfileDvdPlusR
        };
        using var rec = new RecordingTransport(drive);
        var controller = new VendorBookTypeController();

        var res = controller.SetBookType(rec, DiscMediaType.DvdPlusR, DvdBookType.DvdRom);

        Assert.True(res.Success, res.ErrorMessage);
        Assert.Equal(DvdBookType.DvdRom, drive.DvdBookType);
        Assert.Contains(rec.CdbLog, c => c[0] == MmcCommandBuilder.OpCodeInquiry);
        Assert.Contains(rec.CdbLog, c => c[0] == BookTypeService.OpCodeSendDiscStructure && c[7] == 0x00);
        Assert.Contains(rec.CdbLog, c => c[0] == BookTypeService.OpCodeReadDiscStructure && c[7] == 0x00);
        Assert.DoesNotContain(rec.CdbLog, c => c[0] is VendorBookTypeController.OpCodeLiteOnBookType
            or VendorBookTypeController.OpCodeLgBookTypeQuery or VendorBookTypeController.OpCodeLgBookTypeSet);
    }

    [Fact]
    public void SetBookType_UnlistedDrive_Rejecting0xBf_NeverReceivesVendorCdbs()
    {
        using var drive = new VirtualMmcDrive
        {
            InquiryVendorId = "PIONEER",
            InquiryProductId = "DVD-RW DVR-221",
            Profile = NativeTaoDataWriter.ProfileDvdPlusR,
            RejectsMmcBookType = true
        };
        using var rec = new RecordingTransport(drive);
        var controller = new VendorBookTypeController();

        var query = controller.QueryBookType(rec, DiscMediaType.DvdPlusR);
        Assert.True(query.Success);
        Assert.False(query.IsAllowListed);
        Assert.Equal(DvdBookType.DvdPlusR, query.MediaBookType);
        Assert.Null(query.DriveSettingBookType);

        var setRes = controller.SetBookType(rec, DiscMediaType.DvdPlusR, DvdBookType.DvdRom);
        Assert.False(setRes.Success);
        Assert.Contains("allow-list", setRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(rec.CdbLog, c => c[0] is VendorBookTypeController.OpCodeLiteOnBookType
            or VendorBookTypeController.OpCodeLgBookTypeQuery or VendorBookTypeController.OpCodeLgBookTypeSet);
    }

    [Fact]
    public void SetAndQueryBookType_LiteOnMediaTekAllowListedDrive_Uses0xDf0x0F()
    {
        using var drive = new VirtualMmcDrive
        {
            InquiryVendorId = "ATAPI",
            InquiryProductId = "iHAS124   F",
            Profile = NativeTaoDataWriter.ProfileDvdPlusR,
            RejectsMmcBookType = true
        };
        using var rec = new RecordingTransport(drive);
        var controller = new VendorBookTypeController();

        var setRes = controller.SetBookType(rec, DiscMediaType.DvdPlusR, DvdBookType.DvdRom);
        Assert.True(setRes.Success, setRes.ErrorMessage);
        Assert.Equal(DvdBookType.DvdRom, drive.DvdBookType);

        // Verify exact 12-byte 0xDF 0x0F set CDB per dvd+rw-booktype.cpp (cdb[2]=0x0F, cdb[3]=1, cdb[4]=1 for DVD-ROM).
        byte[] setCdb = Assert.Single(rec.CdbLog, c => c[0] == 0xDF && c[2] == 0x0F && c[3] == 0x01);
        Assert.Equal(12, setCdb.Length);
        Assert.Equal(0x01, setCdb[4]);

        var query = controller.QueryBookType(rec, DiscMediaType.DvdPlusR);
        Assert.True(query.Success);
        Assert.True(query.IsAllowListed);
        Assert.Equal(BookTypeDriveFamily.LiteOnMediaTek, query.DriveFamily);
        Assert.Equal(DvdBookType.DvdRom, query.DriveSettingBookType);
        Assert.DoesNotContain(rec.CdbLog, c => c[0] is VendorBookTypeController.OpCodeLgBookTypeQuery or VendorBookTypeController.OpCodeLgBookTypeSet);
    }

    [Fact]
    public void SetAndQueryBookType_LgHlDtStAllowListedDrive_Uses0xFcAnd0xFaForDvdPlusRAndDl_RejectsDvdPlusRw()
    {
        using var drive = new VirtualMmcDrive
        {
            InquiryVendorId = "HL-DT-ST",
            InquiryProductId = "DVDRAM GP60NB50",
            Profile = NativeTaoDataWriter.ProfileDvdPlusRDualLayer,
            RejectsMmcBookType = true
        };
        using var rec = new RecordingTransport(drive);
        var controller = new VendorBookTypeController();

        var setDl = controller.SetBookType(rec, DiscMediaType.DvdPlusRDualLayer, DvdBookType.DvdRom);
        Assert.True(setDl.Success, setDl.ErrorMessage);
        Assert.Equal(DvdBookType.DvdRom, drive.DvdBookType);

        // Verify exact 10-byte 0xFC +RTB CDB per dvd+rw-booktype.cpp.
        byte[] fcCdb = Assert.Single(rec.CdbLog, c => c[0] == VendorBookTypeController.OpCodeLgBookTypeSet);
        Assert.Equal(10, fcCdb.Length);
        Assert.Equal((byte)'+', fcCdb[2]);
        Assert.Equal((byte)'R', fcCdb[3]);
        Assert.Equal((byte)'T', fcCdb[4]);
        Assert.Equal((byte)'B', fcCdb[5]);
        Assert.Equal(4, fcCdb[8]);

        var query = controller.QueryBookType(rec, DiscMediaType.DvdPlusRDualLayer);
        Assert.True(query.Success);
        Assert.True(query.IsAllowListed);
        Assert.Equal(BookTypeDriveFamily.LgHlDtSt, query.DriveFamily);
        Assert.Equal(DvdBookType.DvdRom, query.DriveSettingBookType);
        Assert.DoesNotContain(rec.CdbLog, c => c[0] == VendorBookTypeController.OpCodeLiteOnBookType);

        // LG +RTB does not support DVD+RW bitsetting; must report unsupported without sending 0xFC.
        int fcCountBeforeRw = rec.CdbLog.Count(c => c[0] == VendorBookTypeController.OpCodeLgBookTypeSet);
        drive.Profile = NativeTaoDataWriter.ProfileDvdPlusRw;
        var setRw = controller.SetBookType(rec, DiscMediaType.DvdPlusRw, DvdBookType.DvdRom);
        Assert.False(setRw.Success);
        Assert.Equal(fcCountBeforeRw, rec.CdbLog.Count(c => c[0] == VendorBookTypeController.OpCodeLgBookTypeSet));
    }

    [Fact]
    public async Task DataBurnService_PreBurnBookType_UnlistedDriveContinuesUnlessRequired()
    {
        using var drive = new VirtualMmcDrive
        {
            InquiryVendorId = "PIONEER",
            InquiryProductId = "DVD-RW DVR-221",
            Profile = NativeTaoDataWriter.ProfileDvdPlusR,
            RejectsMmcBookType = true
        };
        using var rec = new RecordingTransport(drive);
        var controller = new VendorBookTypeController(_ => rec);
        var engine = new NoOpBurnEngine();
        var service = new DataBurnService(engine, new BookTypeService(controller));
        var recorder = new FakePhysicalRecorder();

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("TEST.TXT", Encoding.ASCII.GetBytes("hello"));

        var optInResult = await service.BurnCompilationAsync(recorder, root, new DataBurnOptions
        {
            VerifyAfterBurn = false,
            RequestedBookType = DvdBookType.DvdRom,
            RequireBookType = false
        });
        Assert.True(optInResult.Success, optInResult.ErrorMessage);
        Assert.Equal(1, engine.BurnCalls);
        Assert.Contains("Book type was not set", optInResult.BookTypeNote);
        Assert.DoesNotContain(rec.CdbLog, c => c[0] is 0xDF or 0xFA or 0xFC);

        using var drive2 = new VirtualMmcDrive
        {
            InquiryVendorId = "PIONEER",
            InquiryProductId = "DVD-RW DVR-221",
            Profile = NativeTaoDataWriter.ProfileDvdPlusR,
            RejectsMmcBookType = true
        };
        var serviceRequired = new DataBurnService(engine, new BookTypeService(new VendorBookTypeController(_ => drive2)));
        var requiredResult = await serviceRequired.BurnCompilationAsync(recorder, root, new DataBurnOptions
        {
            VerifyAfterBurn = false,
            RequestedBookType = DvdBookType.DvdRom,
            RequireBookType = true
        });
        Assert.False(requiredResult.Success);
        Assert.Equal(1, engine.BurnCalls); // burn did not start
    }
}
