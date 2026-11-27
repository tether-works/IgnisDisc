using System.Runtime.CompilerServices;
using CdBurner.Hardware.NativeWriting;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Sends the direct writer's command logs from this test run to a temporary folder instead of the user's own
/// %LOCALAPPDATA%\IgnisDisc\logs, where they buried the logs of real burns. Runs once when the assembly loads.
/// </summary>
public sealed class TestLogDirectoryTests
{
    [Xunit.Fact]
    public void LogsOfThisTestRun_DoNotGoToTheUsersLogFolder()
    {
        string users = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "logs");
        Xunit.Assert.NotEqual(users, NativeLogDirectory.Default);
        Xunit.Assert.StartsWith(Path.GetTempPath(), NativeLogDirectory.Default);
    }
}

internal static class TestLogDirectory
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NativeLogDirectory.EnvironmentVariable)))
        {
            Environment.SetEnvironmentVariable(NativeLogDirectory.EnvironmentVariable,
                Path.Combine(Path.GetTempPath(), "IgnisDisc-test-logs"));
        }
    }
}
