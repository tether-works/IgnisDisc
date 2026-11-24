using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class BookTypeAllowListTests
{
    private sealed class StubRecorder : IDiscRecorderBackend
    {
        public MediaStatusDto Status { get; init; } = new() { MediaPresent = true, MediaType = DiscMediaType.DvdPlusR };
        public bool Virtual { get; init; }
        public string UniqueId => "STUB-01";
        public string VendorId { get; init; } = "PIONEER";
        public string ProductId { get; init; } = "DVD-RW DVR-221";
        public string ProductRevision => "1.00";
        public string VolumePath => "Z:\\";
        public bool IsVirtual => Virtual;
        public DriveCapabilitiesDto GetCapabilities() => new();
        public MediaStatusDto QueryMediaStatus() => Status;
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    private sealed class StubController(BurnOperationResult setResult) : IBookTypeController
    {
        public int SetCalls { get; private set; }
        public BookTypeQueryResult QueryBookType(IDiscRecorderBackend recorder) =>
            new(true, DvdBookType.DvdPlusR, 1, null, BookTypeDriveFamily.StandardMmc, false, true, "stub");

        public BurnOperationResult SetBookType(IDiscRecorderBackend recorder, DvdBookType targetBookType)
        {
            SetCalls++;
            return setResult;
        }
    }

    [Theory]
    [InlineData("ATAPI", "iHAS124   F", true, BookTypeDriveFamily.LiteOnMediaTek)]
    [InlineData("ATAPI   ", "iHBS212   2", true, BookTypeDriveFamily.LiteOnMediaTek)]
    [InlineData("LITE-ON", "DVDRW LH-20A1P", true, BookTypeDriveFamily.LiteOnMediaTek)]
    [InlineData("LITE-ON", "iHAS324   B", true, BookTypeDriveFamily.LiteOnMediaTek)]
    [InlineData("PLDS", "iHAS124   Y", true, BookTypeDriveFamily.LiteOnMediaTek)]
    [InlineData("ASUS", "DRW-24B1ST", true, BookTypeDriveFamily.LiteOnMediaTek)]
    [InlineData("HL-DT-ST", "DVDRAM GP60NB50", true, BookTypeDriveFamily.LgHlDtSt)]
    [InlineData("HL-DT-ST", "BD-RE BP50NB40", true, BookTypeDriveFamily.LgHlDtSt)]
    [InlineData("HL-DT-ST", "BD-RE WH16NS58", true, BookTypeDriveFamily.LgHlDtSt)]
    [InlineData("LG", "DVDRAM GH24NSC0", true, BookTypeDriveFamily.LgHlDtSt)]
    [InlineData("HL-DT-ST", "UNLISTED-DRIVE", false, BookTypeDriveFamily.Unknown)]
    [InlineData("PIONEER", "DVD-RW DVR-221", false, BookTypeDriveFamily.Unknown)]
    [InlineData("MATSHITA", "DVD-RAM UJ8E2", false, BookTypeDriveFamily.Unknown)]
    [InlineData("TSSTcorp", "CDDVDW SH-224DB", false, BookTypeDriveFamily.Unknown)]
    [InlineData("IGNIS", "VIRTUAL DVD/CD-RW", false, BookTypeDriveFamily.Unknown)]
    [InlineData("", "", false, BookTypeDriveFamily.Unknown)]
    public void AllowList_MatchesOnlyExplicitLiteOnMediaTekAndLgHlDtStEntries(
        string vendor,
        string product,
        bool expectedSupported,
        BookTypeDriveFamily expectedFamily)
    {
        BookTypeAllowListMatch? match = BookTypeDriveAllowList.Evaluate(vendor, product);
        Assert.Equal(expectedSupported, match != null);
        Assert.Equal(expectedSupported, BookTypeDriveAllowList.IsSupported(vendor, product));
        Assert.Equal(expectedFamily, match?.Family ?? BookTypeDriveFamily.Unknown);
    }

    [Theory]
    [InlineData(0x01, DvdBookType.DvdRom, 1)]
    [InlineData(0x10, DvdBookType.DvdRam, 0)]
    [InlineData(0x25, DvdBookType.DvdMinusR, 5)]
    [InlineData(0x32, DvdBookType.DvdMinusRw, 2)]
    [InlineData(0x92, DvdBookType.DvdPlusRw, 2)]
    [InlineData(0xA1, DvdBookType.DvdPlusR, 1)]
    [InlineData(0xE1, DvdBookType.DvdPlusRDualLayer, 1)]
    public void TryParsePhysicalFormatBookType_DecodesMmcFormat00Byte4(
        byte pfiByte0,
        DvdBookType expectedType,
        byte expectedPartVersion)
    {
        byte[] response = [0x00, 0x12, 0x00, 0x00, pfiByte0, 0x02, 0x00, 0x00];

        bool ok = BookTypeService.TryParsePhysicalFormatBookType(response, out DvdBookType actualType, out byte actualVersion);

        Assert.True(ok);
        Assert.Equal(expectedType, actualType);
        Assert.Equal(expectedPartVersion, actualVersion);
    }

    [Fact]
    public void TryParsePhysicalFormatBookType_RejectsShortOrUnknownNibble()
    {
        Assert.False(BookTypeService.TryParsePhysicalFormatBookType([0x00, 0x12, 0x00, 0x00], out _, out _));
        Assert.False(BookTypeService.TryParsePhysicalFormatBookType([0x00, 0x02, 0x00, 0x00, 0x01], out _, out _));
        Assert.False(BookTypeService.TryParsePhysicalFormatBookType([0x00, 0x12, 0x00, 0x00, 0xF1], out _, out _));
    }

    [Fact]
    public void ApplyPreBurnBookType_UnsupportedDriveOrMedia_WarnsWithoutFailingUnlessRequired()
    {
        var controller = new StubController(BurnOperationResult.Failed("Unsupported drive."));
        var service = new BookTypeService(controller);
        var recorder = new StubRecorder();

        PreBurnBookTypeOutcome optInUnsupportedDrive = service.ApplyPreBurnBookType(
            recorder, DiscMediaType.DvdPlusR, DvdBookType.DvdRom, requireBookType: false, isSimulate: false);
        Assert.True(optInUnsupportedDrive.ShouldProceed);
        Assert.False(optInUnsupportedDrive.Applied);
        Assert.True(optInUnsupportedDrive.IsWarning);
        Assert.Contains("Unsupported drive", optInUnsupportedDrive.Note);

        PreBurnBookTypeOutcome requiredUnsupportedDrive = service.ApplyPreBurnBookType(
            recorder, DiscMediaType.DvdPlusR, DvdBookType.DvdRom, requireBookType: true, isSimulate: false);
        Assert.False(requiredUnsupportedDrive.ShouldProceed);
        Assert.False(requiredUnsupportedDrive.Applied);
        Assert.Contains("Unsupported drive", requiredUnsupportedDrive.ErrorMessage);

        PreBurnBookTypeOutcome optInCd = service.ApplyPreBurnBookType(
            recorder, DiscMediaType.CdR, DvdBookType.DvdRom, requireBookType: false, isSimulate: false);
        Assert.True(optInCd.ShouldProceed);
        Assert.True(optInCd.IsWarning);

        PreBurnBookTypeOutcome requiredCd = service.ApplyPreBurnBookType(
            recorder, DiscMediaType.CdR, DvdBookType.DvdRom, requireBookType: true, isSimulate: false);
        Assert.False(requiredCd.ShouldProceed);
    }
}
