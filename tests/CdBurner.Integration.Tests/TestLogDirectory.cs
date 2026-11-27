using System;
using System.IO;
using System.Runtime.CompilerServices;
using CdBurner.Hardware.NativeWriting;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Sends the direct writer's command logs from this test run (including CLI runs it starts) to a temporary folder
/// instead of the user's own %LOCALAPPDATA%\IgnisDisc\logs, where they buried the logs of real burns.
/// </summary>
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
