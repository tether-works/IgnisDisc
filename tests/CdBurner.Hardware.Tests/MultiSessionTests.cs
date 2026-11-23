using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for Multi-Session Data Disc Import & Session Merging.
/// Verifies importing previous session trees, non-finalized appendable disc tracking,
/// LBA pointer reuse without data duplication, and multi-session merged volume reading.
/// </summary>
public class MultiSessionTests
{
    [Fact]
    public async Task MultiSession_TwoSessions_ImportsSession1_AppendsSession2_PreservesPointers_AndReadsBothFiles()
    {
        // 1. Setup virtual CD-R medium and recorder
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var burnEngine = new VirtualBurnEngine(recorder);

        // 2. Prepare Session 1: /Session1/fileA.txt = 10 KB
        var root1 = CompilationDirectory.CreateRoot();
        var session1Dir = root1.AddDirectory("Session1");
        byte[] fileABytes = new byte[10 * 1024]; // 10 KB = 10,240 bytes
        Random.Shared.NextBytes(fileABytes);
        session1Dir.AddFile("fileA.txt", fileABytes);

        var session1BuildOptions = new IsoBuildOptions("SESSION1", IncludeUdf: false);
        using var session1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, session1BuildOptions);

        // 3. Burn Session 1 with CloseDisc = false
        var burnResult1 = await burnEngine.BurnDataStreamAsync(
            recorder,
            session1Stream,
            new DataBurnOptions { CloseDisc = false },
            progress: null,
            CancellationToken.None);

        Assert.True(burnResult1.Success, burnResult1.ErrorMessage);
        Assert.True(burnResult1.SectorsWritten > 0);

        // 4. Verify QueryMediaStatus() after Session 1
        var status1 = recorder.QueryMediaStatus();
        Assert.Equal(1, status1.Sessions);
        Assert.Equal(1, status1.SessionCount);
        Assert.False(status1.IsBlank);
        Assert.True(status1.IsAppendable);
        Assert.True(status1.NextWritableLba > 0);

        int nextWritableLba = status1.NextWritableLba;

        // 5. Import Session 1 via MultiSessionManager
        using var session1DiscStream = medium.CreateReadStream();
        var mergedRoot = MultiSessionManager.ImportSessionTree(session1DiscStream, sessionStartLba: 0);

        // Verify /Session1/fileA.txt imported with its already-recorded LBA
        var importedFileA = mergedRoot.FindFileByVirtualPath("/Session1/fileA.txt");
        Assert.NotNull(importedFileA);
        Assert.Equal(fileABytes.Length, importedFileA.SizeBytes);
        Assert.NotNull(importedFileA.ImportedStartLba);
        int session1FileALba = importedFileA.ImportedStartLba.Value;
        Assert.True(session1FileALba > 0);

        // 6. Add /Session2/fileB.txt = 8 KB
        var session2Dir = mergedRoot.AddDirectory("Session2");
        byte[] fileBBytes = new byte[8 * 1024]; // 8 KB = 8,192 bytes
        Random.Shared.NextBytes(fileBBytes);
        session2Dir.AddFile("fileB.txt", fileBBytes);

        // 7. Build appended session stream at NextWritableLba
        var session2BuildOptions = new IsoBuildOptions("SESSION2", IncludeUdf: false);
        using var session2Stream = MultiSessionManager.BuildAppendedSessionStream(
            mergedRoot,
            nextWritableLba,
            session2BuildOptions);

        Assert.NotNull(session2Stream);
        Assert.True(session2Stream.Length > 0);

        // 8. Burn Session 2 with CloseDisc = true
        var burnResult2 = await burnEngine.BurnDataStreamAsync(
            recorder,
            session2Stream,
            new DataBurnOptions { CloseDisc = true },
            progress: null,
            CancellationToken.None);

        Assert.True(burnResult2.Success, burnResult2.ErrorMessage);

        // 9. Verify media status after Session 2
        var status2 = recorder.QueryMediaStatus();
        Assert.Equal(2, status2.Sessions);
        Assert.Equal(2, status2.SessionCount);
        Assert.False(status2.IsAppendable);

        // 10. Verify merged volume reading
        using var mergedDiscStream = medium.CreateReadStream();
        using var mergedReader = IsoImageReader.FromStream(mergedDiscStream, leaveOpen: true, sessionStartLba: nextWritableLba);

        // Verify /Session1/fileA.txt still points to its Session 1 LBA and is readable
        var fileAEntry = mergedReader.FindFile("/Session1/fileA.txt");
        Assert.NotNull(fileAEntry);
        Assert.Equal(session1FileALba, fileAEntry.StartLba);
        byte[] readBackA = mergedReader.ReadFileBytes(fileAEntry);
        Assert.Equal(fileABytes, readBackA);

        // Verify /Session2/fileB.txt points to Session 2 LBA and is readable
        var fileBEntry = mergedReader.FindFile("/Session2/fileB.txt");
        Assert.NotNull(fileBEntry);
        Assert.True(fileBEntry.StartLba >= nextWritableLba);
        byte[] readBackB = mergedReader.ReadFileBytes(fileBEntry);
        Assert.Equal(fileBBytes, readBackB);
    }

    [Fact]
    public async Task MultiSession_ModifiedFile_AllocatesNewLba_AndUnchangedFilePreservesLba()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        // Session 1: two files
        var root1 = CompilationDirectory.CreateRoot();
        byte[] keepBytes = new byte[4096];
        Random.Shared.NextBytes(keepBytes);
        root1.AddFile("keep.dat", keepBytes);

        byte[] originalModBytes = new byte[6144];
        Random.Shared.NextBytes(originalModBytes);
        root1.AddFile("modified.dat", originalModBytes);

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        var status1 = recorder.QueryMediaStatus();
        int nextWritable = status1.NextWritableLba;

        // Import Session 1
        using var discStream1 = medium.CreateReadStream();
        var mergedRoot = MultiSessionManager.ImportSessionTree(discStream1, sessionStartLba: 0);

        var keepFile = mergedRoot.FindFileByVirtualPath("/keep.dat");
        Assert.NotNull(keepFile);
        int keepLba = keepFile.ImportedStartLba!.Value;

        // Modify "modified.dat": replace with new content and clear ImportedStartLba
        byte[] newModBytes = new byte[8192];
        Random.Shared.NextBytes(newModBytes);
        mergedRoot.RemoveChild("modified.dat");
        mergedRoot.AddFile("modified.dat", newModBytes); // Newly added file has ImportedStartLba == null

        using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(mergedRoot, nextWritable, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s2Stream, new DataBurnOptions { CloseDisc = true }, null, CancellationToken.None);

        // Inspect Session 2
        using var mergedDiscStream = medium.CreateReadStream();
        using var reader = IsoImageReader.FromStream(mergedDiscStream, leaveOpen: true, sessionStartLba: nextWritable);

        var readKeep = reader.FindFile("/keep.dat");
        Assert.NotNull(readKeep);
        Assert.Equal(keepLba, readKeep.StartLba);
        Assert.Equal(keepBytes, reader.ReadFileBytes(readKeep));

        var readModified = reader.FindFile("/modified.dat");
        Assert.NotNull(readModified);
        Assert.True(readModified.StartLba >= nextWritable);
        Assert.Equal(newModBytes, reader.ReadFileBytes(readModified));
    }

    [Fact]
    public async Task MultiSession_DeletedFile_IsNotPresentInAppendedSession()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        // Session 1: keep.txt and delete.txt
        var root1 = CompilationDirectory.CreateRoot();
        root1.AddFile("keep.txt", new byte[] { 1, 2, 3, 4 });
        root1.AddFile("delete.txt", new byte[] { 5, 6, 7, 8 });

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        int nextWritable = recorder.QueryMediaStatus().NextWritableLba;

        // Import and delete
        using var discStream = medium.CreateReadStream();
        var mergedRoot = MultiSessionManager.ImportSessionTree(discStream, 0);
        bool removed = mergedRoot.RemoveChild("delete.txt");
        Assert.True(removed);

        using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(mergedRoot, nextWritable, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s2Stream, new DataBurnOptions { CloseDisc = true }, null, CancellationToken.None);

        // Inspect Session 2
        using var finalDiscStream = medium.CreateReadStream();
        using var reader = IsoImageReader.FromStream(finalDiscStream, leaveOpen: true, sessionStartLba: nextWritable);

        Assert.NotNull(reader.FindFile("/keep.txt"));
        Assert.Null(reader.FindFile("/delete.txt"));
    }

    [Fact]
    public async Task MultiSession_ThreeSessions_ProgressivelyAppendsAndMaintainsAllPointers()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        // Session 1
        var r1 = CompilationDirectory.CreateRoot();
        byte[] doc1 = new byte[2048];
        Random.Shared.NextBytes(doc1);
        r1.AddDirectory("S1").AddFile("doc1.txt", doc1);

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(r1, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        var st1 = recorder.QueryMediaStatus();
        Assert.Equal(1, st1.Sessions);
        Assert.True(st1.IsAppendable);
        int lbaS2 = st1.NextWritableLba;

        // Session 2
        using var discStream1 = medium.CreateReadStream();
        var r2 = MultiSessionManager.ImportSessionTree(discStream1, 0);
        byte[] doc2 = new byte[4096];
        Random.Shared.NextBytes(doc2);
        r2.AddDirectory("S2").AddFile("doc2.txt", doc2);

        using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(r2, lbaS2, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s2Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        var st2 = recorder.QueryMediaStatus();
        Assert.Equal(2, st2.Sessions);
        Assert.True(st2.IsAppendable);
        int lbaS3 = st2.NextWritableLba;

        // Session 3
        using var discStream2 = medium.CreateReadStream();
        var r3 = MultiSessionManager.ImportSessionTree(discStream2, sessionStartLba: lbaS2);
        byte[] doc3 = new byte[1024];
        Random.Shared.NextBytes(doc3);
        r3.AddDirectory("S3").AddFile("doc3.txt", doc3);

        using var s3Stream = MultiSessionManager.BuildAppendedSessionStream(r3, lbaS3, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s3Stream, new DataBurnOptions { CloseDisc = true }, null, CancellationToken.None);

        var st3 = recorder.QueryMediaStatus();
        Assert.Equal(3, st3.Sessions);
        Assert.False(st3.IsAppendable);

        // Verify all 3 files from Session 3
        using var finalDiscStream = medium.CreateReadStream();
        using var reader = IsoImageReader.FromStream(finalDiscStream, leaveOpen: true, sessionStartLba: lbaS3);

        var f1 = reader.FindFile("/S1/doc1.txt");
        Assert.NotNull(f1);
        Assert.Equal(doc1, reader.ReadFileBytes(f1));

        var f2 = reader.FindFile("/S2/doc2.txt");
        Assert.NotNull(f2);
        Assert.Equal(doc2, reader.ReadFileBytes(f2));

        var f3 = reader.FindFile("/S3/doc3.txt");
        Assert.NotNull(f3);
        Assert.Equal(doc3, reader.ReadFileBytes(f3));
    }

    [Fact]
    public async Task MultiSession_ImportFromRecorderAsync_ImportsActiveSessionDirectly()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory("Sub").AddFile("test.txt", new byte[] { 10, 20, 30 });

        using var sStream = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, sStream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        var imported = await MultiSessionManager.ImportFromRecorderAsync(recorder, engine);
        Assert.NotNull(imported);
        var file = imported.FindFileByVirtualPath("/Sub/test.txt");
        Assert.NotNull(file);
        Assert.NotNull(file.ImportedStartLba);
        Assert.Equal(3, file.SizeBytes);
    }

    [Fact]
    public async Task MultiSession_AppendedSessionStream_DoesNotDuplicateUnchangedFileBytes()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        // Session 1: 500 KB file (250 sectors)
        var root1 = CompilationDirectory.CreateRoot();
        byte[] bigFile = new byte[500 * 1024];
        root1.AddFile("big.bin", bigFile);

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        int nextWritable = recorder.QueryMediaStatus().NextWritableLba;
        Assert.True(nextWritable >= 250);

        // Import Session 1 and add tiny 50-byte file
        using var discStream = medium.CreateReadStream();
        var mergedRoot = MultiSessionManager.ImportSessionTree(discStream, 0);
        mergedRoot.AddFile("small.txt", new byte[50]);

        using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(mergedRoot, nextWritable, new IsoBuildOptions(IncludeUdf: false));

        // The appended session stream should only contain descriptors and the small file,
        // which is around 25-30 sectors (< 70 KB), definitely NOT 500 KB!
        Assert.True(s2Stream.Length < 100 * 1024, $"Appended session stream was {s2Stream.Length} bytes, expected < 100 KB.");
    }

    [Fact]
    public async Task MultiSession_Beyond4096Sectors_ImportsOpensStreamsAfterReturn_AndBurnsSubsequentSession()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankDvdR47();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        // Session 1: > 4,096 sectors (9 MiB = 4,608 sectors) + a second file in a subdirectory
        byte[] largePayload = new byte[9 * 1024 * 1024 + 321];
        for (int i = 0; i < largePayload.Length; i += 2048)
        {
            largePayload[i] = (byte)((i / 2048) % 251);
        }
        largePayload[^1] = 0x5A;

        byte[] nestedPayload = "after-4096-sector-boundary"u8.ToArray();

        var root1 = CompilationDirectory.CreateRoot();
        root1.AddFile("large_s1.bin", largePayload);
        root1.AddDirectory("Deep").AddFile("nested.txt", nestedPayload);

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, new IsoBuildOptions("S1_LARGE", IncludeUdf: false));
        Assert.True(s1Stream.Length > 4096 * OpticalConstants.DataSectorSize);

        var burn1 = await engine.BurnDataStreamAsync(recorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);
        Assert.True(burn1.Success, burn1.ErrorMessage);

        // Import via fast-path; returned CompilationFile streams must remain readable after ImportFromRecorderAsync returns.
        var imported = await MultiSessionManager.ImportFromRecorderAsync(recorder, engine);
        var importedLarge = imported.FindFileByVirtualPath("/large_s1.bin");
        var importedNested = imported.FindFileByVirtualPath("/Deep/nested.txt");
        Assert.NotNull(importedLarge);
        Assert.NotNull(importedNested);

        using (var largeStream = importedLarge.OpenRead())
        {
            Assert.Equal(largePayload.Length, largeStream.Length);
            Assert.True(largeStream.CanSeek);

            // Seek across the 4,096-sector boundary (8 MiB) and read tail byte
            largeStream.Position = 4096L * OpticalConstants.DataSectorSize;
            Assert.Equal(4096 % 251, largeStream.ReadByte());
            largeStream.Seek(-1, SeekOrigin.End);
            Assert.Equal(0x5A, largeStream.ReadByte());
        }

        using (var nestedStream = importedNested.OpenRead())
        {
            byte[] actualNested = new byte[nestedPayload.Length];
            nestedStream.ReadExactly(actualNested);
            Assert.Equal(nestedPayload, actualNested);
        }

        // Burn Session 2 using the imported tree + new file
        byte[] s2Bytes = "session-2-file"u8.ToArray();
        imported.AddDirectory("S2").AddFile("added.txt", s2Bytes);
        int nextWritable = recorder.QueryMediaStatus().NextWritableLba;
        using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(imported, nextWritable, new IsoBuildOptions("S2_LARGE", IncludeUdf: false));

        var burn2 = await engine.BurnDataStreamAsync(recorder, s2Stream, new DataBurnOptions { CloseDisc = true }, null, CancellationToken.None);
        Assert.True(burn2.Success, burn2.ErrorMessage);

        using var discStream = medium.CreateReadStream();
        using var reader = MultiSessionManager.OpenSessionReader(discStream, sessionStartLba: nextWritable);
        var readBackNested = reader.FindFile("/Deep/nested.txt");
        var readBackAdded = reader.FindFile("/S2/added.txt");
        Assert.NotNull(readBackNested);
        Assert.NotNull(readBackAdded);
        Assert.Equal(nestedPayload, reader.ReadFileBytes(readBackNested));
        Assert.Equal(s2Bytes, reader.ReadFileBytes(readBackAdded));
    }

    [Fact]
    public async Task MultiSession_FallbackRecorder_Beyond4096Sectors_ExercisesSessionOffsetStreamAndOpenRead()
    {
        using var innerRecorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankDvdR47();
        innerRecorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(innerRecorder);

        // Session 1 (> 4,096 sectors so Session 2 starts past LBA 4,096)
        byte[] s1Data = new byte[4200 * OpticalConstants.DataSectorSize];
        s1Data[0] = 0x11;
        s1Data[^1] = 0x22;
        var root1 = CompilationDirectory.CreateRoot();
        root1.AddFile("s1.bin", s1Data);

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, new IsoBuildOptions("S1", IncludeUdf: false));
        Assert.True((await engine.BurnDataStreamAsync(innerRecorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None)).Success);

        // Session 2 starting at NextWritableLba (> 4,096), also > 4,096 sectors itself
        int session2Start = innerRecorder.QueryMediaStatus().NextWritableLba;
        Assert.True(session2Start > 4096);

        byte[] s2Data = new byte[4300 * OpticalConstants.DataSectorSize + 77];
        s2Data[0] = 0x33;
        s2Data[4100 * OpticalConstants.DataSectorSize] = 0x44;
        s2Data[^1] = 0x55;

        using (var s1Disc = medium.CreateReadStream())
        {
            var merged = MultiSessionManager.ImportSessionTree(s1Disc, 0);
            merged.AddFile("s2_large.bin", s2Data);
            using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(merged, session2Start, new IsoBuildOptions("S2", IncludeUdf: false));
            Assert.True((await engine.BurnDataStreamAsync(innerRecorder, s2Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None)).Success);
        }

        // Wrap recorder without CreateReadStream() to exercise the fallback ReadSectorsAsync + SessionOffsetStream path
        var fallbackRecorder = new NoFastStreamRecorderAdapter(innerRecorder);
        var imported = await MultiSessionManager.ImportFromRecorderAsync(fallbackRecorder, engine);

        var importedS1 = imported.FindFileByVirtualPath("/s1.bin");
        var importedS2 = imported.FindFileByVirtualPath("/s2_large.bin");
        Assert.NotNull(importedS1);
        Assert.NotNull(importedS2);
        Assert.Equal(s1Data.Length, importedS1.SizeBytes);
        Assert.Equal(s2Data.Length, importedS2.SizeBytes);

        // OpenRead works after ImportFromRecorderAsync returns on the fallback path too
        using (var s2Read = importedS2.OpenRead())
        {
            Assert.Equal(s2Data.Length, s2Read.Length);
            Assert.Equal(0x33, s2Read.ReadByte());
            s2Read.Position = 4100L * OpticalConstants.DataSectorSize;
            Assert.Equal(0x44, s2Read.ReadByte());
            s2Read.Seek(-1, SeekOrigin.End);
            Assert.Equal(0x55, s2Read.ReadByte());
        }
    }

    private sealed class NoFastStreamRecorderAdapter : IDiscRecorderBackend, IDiscTocProvider
    {
        private readonly VirtualDiscRecorder _inner;
        public NoFastStreamRecorderAdapter(VirtualDiscRecorder inner) => _inner = inner;
        public string UniqueId => _inner.UniqueId;
        public string VendorId => _inner.VendorId;
        public string ProductId => _inner.ProductId;
        public string ProductRevision => _inner.ProductRevision;
        public string VolumePath => _inner.VolumePath;
        public bool IsVirtual => _inner.IsVirtual;
        public DriveCapabilitiesDto GetCapabilities() => _inner.GetCapabilities();
        public MediaStatusDto QueryMediaStatus() => _inner.QueryMediaStatus();
        public IReadOnlyList<DiscTocTrack> ReadTableOfContents()
        {
            var medium = _inner.CurrentMedium;
            if (medium == null) return Array.Empty<DiscTocTrack>();
            return medium.Sessions
                .SelectMany(s => s.Tracks)
                .Select(t => new DiscTocTrack(t.TrackNumber, t.StartLba, t.SectorCount, t.IsAudio))
                .ToList();
        }
        public void EjectMedia() => _inner.EjectMedia();
        public void CloseTray() => _inner.CloseTray();
        public bool AcquireExclusiveAccess(string clientName) => _inner.AcquireExclusiveAccess(clientName);
        public void ReleaseExclusiveAccess() => _inner.ReleaseExclusiveAccess();
        public void Dispose() { }
    }
}
