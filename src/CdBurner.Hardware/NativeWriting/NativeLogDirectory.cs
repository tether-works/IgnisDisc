namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Where the direct writer saves its command logs (<c>native-write-*.log</c>) when no folder is given.
/// </summary>
public static class NativeLogDirectory
{
    /// <summary>Environment variable that moves the logs to another folder (a full path); the test projects set it.</summary>
    public const string EnvironmentVariable = "IGNISDISC_LOG_DIR";

    /// <summary>
    /// The folder from <see cref="EnvironmentVariable"/> when it holds a full path, else %LOCALAPPDATA%\IgnisDisc\logs.
    /// Before this, every test run that used the simulated drive wrote its logs into the user's own log folder
    /// (2,780 files by 2026-11-26), which buried the logs of real burns.
    /// </summary>
    public static string Default
    {
        get
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && Path.IsPathFullyQualified(fromEnvironment))
            {
                return fromEnvironment;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "logs");
        }
    }
}
