using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Burning;

/// <summary>
/// Which discs are burned with IgnisDisc's own drive commands unless the user picks the Windows writer. One rule for the
/// app's burn screens and the command line.
/// </summary>
public static class DirectWritingDefault
{
    /// <summary>
    /// True for a DVD-RW. Windows' writer formats every blank DVD-RW for overwriting and cannot add a session to one left
    /// open; direct drive commands keep it sequential, leave it open and add sessions (GP60NB50, Ritek DVD-RW,
    /// 2026-11-15). A quick-erased DVD-RW the drive cannot leave open still goes to the Windows writer, with the reason.
    /// </summary>
    public static bool Prefers(MediaStatusDto? status) =>
        status is { MediaPresent: true, IsLoading: false } && status.MediaType == DiscMediaType.DvdMinusRw;

    /// <summary>Shown when the default chose direct drive commands for the disc.</summary>
    public const string Note =
        "DVD-RW: using direct drive commands, which keep it open for more sessions; Windows' writer would format it for overwriting.";
}
