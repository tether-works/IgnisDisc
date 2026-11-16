namespace CdBurner.Core.Abstractions;

/// <summary>
/// A burn engine that reads the disc from the drive itself and can find it in a different state than Windows' media
/// status reports. Windows calls a DVD-RW left open for another session "overwrite-only", with no next writable address
/// (GP60NB50, Ritek DVD-RW, 2026-11-15); the direct writer can add that session, so it says where. Code that plans a
/// burn asks through <see cref="MediaStatusForWriting.Describe"/>, so the Windows writer keeps Windows' answer: through
/// it, the same "append" would write the disc over from sector 0.
/// </summary>
public interface IWriterMediaStatus
{
    /// <summary>The disc as this engine will write it; <paramref name="windowsStatus"/> unchanged when it agrees.</summary>
    MediaStatusDto DescribeForWriting(IDiscRecorderBackend recorder, MediaStatusDto windowsStatus);
}

/// <summary>Asks the engine that will write how it sees the disc.</summary>
public static class MediaStatusForWriting
{
    /// <summary>
    /// The disc status a burn with <paramref name="engine"/> plans from: <paramref name="status"/> (or a fresh query),
    /// corrected by the engine when it reads the disc itself.
    /// </summary>
    public static MediaStatusDto Describe(IBurnEngineBackend engine, IDiscRecorderBackend recorder, MediaStatusDto? status = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(recorder);
        var windows = status ?? recorder.QueryMediaStatus();
        return engine is IWriterMediaStatus writer ? writer.DescribeForWriting(recorder, windows) : windows;
    }
}
