using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// AccurateRip's drive read-offset list and the write offset worked out from it (M3.2). The two records below are the
/// bytes of the GP60NB50 and GP57EW40 entries in DriveOffsets.bin as downloaded on 2026-11-11.
/// </summary>
public class DriveOffsetsTests
{
    private static readonly byte[] Gp60nb50 = Convert.FromHexString(
        "0600484C2D44542D5354202D2044564452414D20475036304E42353000000000000000FF000000000000000000000000000000000000000000000000000000000000000000");
    private static readonly byte[] Gp57ew40 = Convert.FromHexString(
        "0600484C2D44542D5354202D2044564452414D20475035374557343000000000000000E7000000000000000000000000000000000000000000000000000000000000000000");

    [Fact]
    public void Records_AreParsedFromTheRealFile()
    {
        var entries = AccurateRipDriveOffsets.Parse([.. Gp57ew40, .. Gp60nb50]);

        Assert.Equal(AccurateRipDriveOffsets.RecordSize, Gp60nb50.Length);
        Assert.Equal(2, entries.Count);
        Assert.Equal(new DriveOffsetEntry("HL-DT-ST - DVDRAM GP60NB50", 6, 255), entries[1]);
        Assert.Equal(231, entries[0].Submissions);
    }

    [Fact]
    public void NegativeOffsetsAndPartialRecords_AreHandled()
    {
        byte[] record = (byte[])Gp60nb50.Clone();
        record[0] = 0x9A; record[1] = 0xFD; // -614, little-endian
        var entries = AccurateRipDriveOffsets.Parse([.. record, .. new byte[40]]); // a cut-off record at the end is ignored

        Assert.Equal(-614, Assert.Single(entries).ReadOffsetSamples);
    }

    [Theory]
    [InlineData("HL-DT-ST", "DVDRAM GP60NB50")]
    [InlineData("HL-DT-ST ", "DVDRAM GP60NB50    ")] // INQUIRY fields are space-padded
    [InlineData("hl-dt-st", "dvdram  gp60nb50")]
    public void TheDriveIsFoundByItsInquiryNames(string vendor, string product)
    {
        var entry = AccurateRipDriveOffsets.Find(AccurateRipDriveOffsets.Parse([.. Gp57ew40, .. Gp60nb50]), vendor, product);

        Assert.Equal(6, entry?.ReadOffsetSamples);
    }

    [Fact]
    public void UnknownDrive_IsNotFound()
    {
        Assert.Null(AccurateRipDriveOffsets.Find(AccurateRipDriveOffsets.Parse(Gp60nb50), "HL-DT-ST", "DVDRAM GP60NB60"));
    }

    [Fact]
    public void WriteOffset_IsCombinedMinusRead()
    {
        // The GP60NB50 as measured: combined 0 after every direct burn, read +6 from the list, so it writes 6 samples early.
        var plan = WriteOffsetPlan.Resolve(readOffset: 6, "list", combinedOffset: 0, explicitWriteOffset: null);

        Assert.Equal(-6, plan.WriteOffset);
        Assert.Equal(6, plan.ShiftSamples);
    }

    [Fact]
    public void WithoutAReadOffsetOrAMeasurement_NothingIsShifted()
    {
        Assert.Equal(0, WriteOffsetPlan.Resolve(null, null, 0, null).ShiftSamples);
        Assert.Equal(0, WriteOffsetPlan.Resolve(6, "list", null, null).ShiftSamples);
        Assert.Contains("not measured yet", WriteOffsetPlan.Resolve(6, "list", null, null).Explanation);
        Assert.Equal(-12, WriteOffsetPlan.Resolve(6, "list", 0, explicitWriteOffset: 12).ShiftSamples); // as given wins
    }

    [Fact]
    public void Store_KeepsTheLastMeasurement_AndSaysWhenItChanged()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-offsets-").FullName;
        try
        {
            var store = new DriveOffsetStore(Path.Combine(dir, "drive-offsets.json"));
            Assert.Null(store.GetCombinedOffset("A - B"));
            Assert.Null(store.RecordCombinedOffset("A - B", 0));
            Assert.Null(store.RecordCombinedOffset("A - B", 0));
            Assert.Equal(0, store.RecordCombinedOffset("A - B", 4)); // reports the value it replaced
            Assert.Equal(4, new DriveOffsetStore(Path.Combine(dir, "drive-offsets.json")).GetCombinedOffset("A - B"));

            File.WriteAllText(Path.Combine(dir, "drive-offsets.json"), "not json");
            Assert.Null(store.GetCombinedOffset("A - B")); // a damaged file means measuring again, not a crash
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Load_UsesTheCachedCopy_AndWithNoCopyAndNoNetworkIsEmpty()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-offsets-").FullName;
        try
        {
            string cache = Path.Combine(dir, "DriveOffsets.bin");
            Assert.Empty(await AccurateRipDriveOffsets.LoadAsync(null, cache));

            await File.WriteAllBytesAsync(cache, Gp60nb50);
            Assert.Equal(6, Assert.Single(await AccurateRipDriveOffsets.LoadAsync(null, cache)).ReadOffsetSamples);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("HL-DT-ST\0 ", "DVDRAM\tGP60NB50\0")]
    [InlineData("  hl-dt-st ", "dvdram   gp60nb50\r\n")]
    [InlineData("", "HL-DT-ST DVDRAM GP60NB50")]
    public void Find_NormalizesInquiryVendorAndProduct(string vendor, string product)
    {
        var entries = AccurateRipDriveOffsets.Parse([.. Gp57ew40, .. Gp60nb50]);
        var entry = AccurateRipDriveOffsets.Find(entries, vendor, product);

        Assert.NotNull(entry);
        Assert.Equal(6, entry.ReadOffsetSamples);
        Assert.Equal(255, entry.Submissions);
    }

    [Fact]
    public void Store_PreservesBothCombinedAndCalibratedReadOffsets()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-offsets-").FullName;
        try
        {
            var store = new DriveOffsetStore(Path.Combine(dir, "drive-offsets.json"));
            Assert.Null(store.GetCalibratedReadOffset("HL-DT-ST - DVDRAM GP60NB50"));

            Assert.Null(store.RecordCombinedOffset("HL-DT-ST - DVDRAM GP60NB50", 0));
            Assert.Null(store.RecordCalibratedReadOffset("HL-DT-ST - DVDRAM GP60NB50", 6, confidence: 12));
            Assert.Equal(6, store.RecordCalibratedReadOffset("hl-dt-st - dvdram  gp60nb50", 8, confidence: 20));

            Assert.Equal(0, store.GetCombinedOffset("HL-DT-ST - DVDRAM GP60NB50"));
            var cal = store.GetCalibratedReadOffset("HL-DT-ST - DVDRAM GP60NB50");
            Assert.NotNull(cal);
            Assert.Equal(8, cal.ReadOffsetSamples);
            Assert.Equal(20, cal.Confidence);

            // Recording a combined offset update preserves the calibrated read offset.
            Assert.Equal(0, store.RecordCombinedOffset("HL-DT-ST - DVDRAM GP60NB50", 2));
            Assert.Equal(8, store.GetCalibratedReadOffset("HL-DT-ST - DVDRAM GP60NB50")!.ReadOffsetSamples);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LoadDatabase_OfflineWithStaleCache_FallsBackToStaleCacheWithoutThrowing()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-offsets-").FullName;
        try
        {
            string cache = Path.Combine(dir, "DriveOffsets.bin");
            await File.WriteAllBytesAsync(cache, Gp60nb50);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow - TimeSpan.FromDays(45));

            using var offlineHttp = new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("Network unreachable")));
            var result = await AccurateRipDriveOffsets.LoadDatabaseAsync(offlineHttp, cache);

            Assert.Equal(DriveOffsetDatabaseStatus.StaleCacheFallback, result.Status);
            Assert.Single(result.Entries);
            Assert.Equal(6, result.Entries[0].ReadOffsetSamples);
            Assert.Contains("Network unreachable", result.ErrorMessage);
            Assert.Equal(Gp60nb50, await File.ReadAllBytesAsync(cache));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LoadDatabase_ForceRefreshInvalidBody_KeepsExistingCache()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-offsets-").FullName;
        try
        {
            string cache = Path.Combine(dir, "DriveOffsets.bin");
            await File.WriteAllBytesAsync(cache, Gp60nb50);

            using var badHttp = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("<html>error</html>"))
            }));
            var result = await AccurateRipDriveOffsets.LoadDatabaseAsync(badHttp, cache, forceRefresh: true);

            Assert.Equal(DriveOffsetDatabaseStatus.StaleCacheFallback, result.Status);
            Assert.Single(result.Entries);
            Assert.Equal(Gp60nb50, await File.ReadAllBytesAsync(cache));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LoadDatabase_ForceRefreshOnline_DownloadsAndUpdatesCache()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-offsets-").FullName;
        try
        {
            string cache = Path.Combine(dir, "DriveOffsets.bin");
            await File.WriteAllBytesAsync(cache, Gp57ew40);

            byte[] updated = [.. Gp57ew40, .. Gp60nb50];
            using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(updated)
            }));
            var result = await AccurateRipDriveOffsets.LoadDatabaseAsync(http, cache, forceRefresh: true);

            Assert.Equal(DriveOffsetDatabaseStatus.Downloaded, result.Status);
            Assert.Equal(2, result.Entries.Count);
            Assert.Equal(updated, await File.ReadAllBytesAsync(cache));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ReadOffsetResolver_EnforcesStrictPrecedence_AndEmptyCacheWarning()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-resolve-").FullName;
        try
        {
            string storePath = Path.Combine(dir, "drive-offsets.json");
            string cachePath = Path.Combine(dir, "DriveOffsets.bin");
            var recorder = new StubRecorder("HL-DT-ST", "DVDRAM GP60NB50", isVirtual: false);

            // 4. Empty cache + no calibration + no override -> 0 with clear warning
            var emptyRes = await ReadOffsetResolver.ResolveAsync(recorder, null, storePath, cachePath, http: null);
            Assert.Equal(0, emptyRes.OffsetSamples);
            Assert.Equal(ReadOffsetSource.DefaultZero, emptyRes.Source);
            Assert.Equal("0 samples (uncalibrated)", emptyRes.BadgeText);
            Assert.False(string.IsNullOrWhiteSpace(emptyRes.Warning));

            // Populate DB cache (+6)
            await File.WriteAllBytesAsync(cachePath, Gp60nb50);
            var dbRes = await ReadOffsetResolver.ResolveAsync(recorder, null, storePath, cachePath, http: null);
            Assert.Equal(6, dbRes.OffsetSamples);
            Assert.Equal(ReadOffsetSource.AccurateRipDatabase, dbRes.Source);
            Assert.Equal("+6 samples via AccurateRip DB", dbRes.BadgeText);
            Assert.Null(dbRes.Warning);

            // 2. Local key-disc calibration (+18) beats AccurateRip DB (+6)
            new DriveOffsetStore(storePath).RecordCalibratedReadOffset("HL-DT-ST - DVDRAM GP60NB50", 18, confidence: 7);
            var calRes = await ReadOffsetResolver.ResolveAsync(recorder, null, storePath, cachePath, http: null);
            Assert.Equal(18, calRes.OffsetSamples);
            Assert.Equal(ReadOffsetSource.KeyDiscCalibration, calRes.Source);
            Assert.Equal("+18 samples via key-disc calibration", calRes.BadgeText);
            Assert.Null(calRes.Warning);

            // 1. Explicit user override (-12, and even explicit 0) beats key-disc calibration (+18) and DB (+6)
            var manualRes = await ReadOffsetResolver.ResolveAsync(recorder, -12, storePath, cachePath, http: null);
            Assert.Equal(-12, manualRes.OffsetSamples);
            Assert.Equal(ReadOffsetSource.ExplicitOverride, manualRes.Source);
            Assert.Equal("-12 samples (manual override)", manualRes.BadgeText);
            Assert.Null(manualRes.Warning);

            var explicitZeroRes = await ReadOffsetResolver.ResolveAsync(recorder, 0, storePath, cachePath, http: null);
            Assert.Equal(0, explicitZeroRes.OffsetSamples);
            Assert.Equal(ReadOffsetSource.ExplicitOverride, explicitZeroRes.Source);
            Assert.Null(explicitZeroRes.Warning);

            // Unlisted drive with populated DB -> 0 with clear warning
            var unlisted = new StubRecorder("PLEXTOR", "PX-UNKNOWN", isVirtual: false);
            var unlistedRes = await ReadOffsetResolver.ResolveAsync(unlisted, null, storePath, cachePath, http: null);
            Assert.Equal(0, unlistedRes.OffsetSamples);
            Assert.Equal(ReadOffsetSource.DefaultZero, unlistedRes.Source);
            Assert.Contains("not found in the AccurateRip offset database", unlistedRes.Warning);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubRecorder(string vendorId, string productId, bool isVirtual) : IDiscRecorderBackend
    {
        public string UniqueId => "STUB-01";
        public string VendorId => vendorId;
        public string ProductId => productId;
        public string ProductRevision => "1.00";
        public string VolumePath => @"Z:\";
        public bool IsVirtual => isVirtual;
        public DriveCapabilitiesDto GetCapabilities() => new();
        public MediaStatusDto QueryMediaStatus() => new();
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }
}
