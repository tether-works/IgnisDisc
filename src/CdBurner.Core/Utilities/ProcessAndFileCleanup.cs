// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CdBurner.Core.Utilities;

/// <summary>
/// Terminates external process trees deterministically and cleans up temporary files and directories with
/// bounded retries so file handles held by exiting child processes or OS scanners do not leak temp artifacts.
/// </summary>
public static class ProcessAndFileCleanup
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly ConditionalWeakTable<Process, object> ProcessKillLocks = new();

    /// <summary>
    /// Terminates <paramref name="process"/> and its entire descendant tree, waiting until both the root process
    /// and any captured descendant processes have exited (up to <paramref name="timeoutMs"/>). Serialized per
    /// <see cref="Process"/> instance so concurrent cancellation/watchdog callbacks and catch handlers all wait
    /// until the process tree has finished exiting.
    /// </summary>
    public static void KillTreeAndWait(Process? process, int timeoutMs = 5000)
    {
        if (process is null)
        {
            return;
        }

        object gate = ProcessKillLocks.GetOrCreateValue(process);
        lock (gate)
        {
            long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            List<Process> descendants = CaptureDescendantsSafe(process, out int rootPid, out DateTime? rootStartTime);

            try
            {
                try
                {
                    if (!HasExitedSafe(process))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                }

                int remainingRoot = RemainingTimeoutMs(deadline);
                try
                {
                    if (!process.WaitForExit(remainingRoot))
                    {
                        Trace.TraceWarning($"External process (PID {rootPid}) did not exit within {timeoutMs} ms after termination.");
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
                {
                }

                // A child spawned just before Kill() might not have been in the initial snapshot; pick it up if its
                // start time falls inside the root process's lifetime.
                if (OperatingSystem.IsWindows() && rootPid > 0 && rootStartTime.HasValue)
                {
                    DateTime? rootExitTime = TryGetExitTime(process);
                    MergeNewDescendants(descendants, rootPid, rootStartTime.Value, rootExitTime);
                }

                WaitAndDisposeDescendants(descendants, deadline);
            }
            finally
            {
                foreach (Process child in descendants)
                {
                    child.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> if it exists, retrying briefly on sharing or access violations and
    /// logging a warning via <see cref="Trace"/> instead of silently swallowing a persistent leak.
    /// </summary>
    public static bool TryDeleteFileWithRetry(string? path, int maxAttempts = 10, int initialDelayMs = 15)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return true;
                }

                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryClearReadOnly(path);
                if (attempt == maxAttempts - 1)
                {
                    Trace.TraceWarning($"Could not delete temporary file '{path}' after {maxAttempts} attempts: {ex.Message}");
                    return false;
                }

                Thread.Sleep(Math.Min(250, initialDelayMs << Math.Min(attempt, 4)));
            }
        }

        return !File.Exists(path);
    }

    /// <summary>
    /// Deletes <paramref name="path"/> if it exists, retrying briefly on sharing or access violations and
    /// logging a warning via <see cref="Trace"/> instead of silently swallowing a persistent leak.
    /// </summary>
    public static bool TryDeleteDirectoryWithRetry(string? path, bool recursive = true, int maxAttempts = 10, int initialDelayMs = 15)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                Directory.Delete(path, recursive);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (recursive)
                {
                    TryClearReadOnlyInDirectory(path);
                }

                if (attempt == maxAttempts - 1)
                {
                    Trace.TraceWarning($"Could not delete temporary directory '{path}' after {maxAttempts} attempts: {ex.Message}");
                    return false;
                }

                Thread.Sleep(Math.Min(250, initialDelayMs << Math.Min(attempt, 4)));
            }
        }

        return !Directory.Exists(path);
    }

    /// <summary>
    /// Deletes <paramref name="path"/> with bounded retries on transient sharing or access violations,
    /// throwing the final exception if deletion still fails.
    /// </summary>
    public static void DeleteDirectoryWithRetry(string path, bool recursive = true, int maxAttempts = 10, int initialDelayMs = 15)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts - 1)
            {
                if (recursive)
                {
                    TryClearReadOnlyInDirectory(path);
                }

                Thread.Sleep(Math.Min(250, initialDelayMs << Math.Min(attempt, 4)));
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="sourcePath"/> to <paramref name="destinationPath"/>, retrying briefly on transient
    /// Windows sharing or access violations (for example when another scanner briefly opens the target file).
    /// </summary>
    public static void MoveFileWithRetry(string sourcePath, string destinationPath, bool overwrite = true, int maxAttempts = 10, int initialDelayMs = 15)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts - 1)
            {
                Thread.Sleep(Math.Min(250, initialDelayMs << Math.Min(attempt, 4)));
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> with bounded retries on transient sharing or access violations.
    /// </summary>
    public static T RetryOnTransientIo<T>(Func<T> operation, int maxAttempts = 6, int initialDelayMs = 15)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts - 1)
            {
                Thread.Sleep(Math.Min(250, initialDelayMs << Math.Min(attempt, 4)));
            }
        }

        return operation();
    }

    private static List<Process> CaptureDescendantsSafe(Process root, out int rootPid, out DateTime? rootStartTime)
    {
        rootPid = 0;
        rootStartTime = null;
        var result = new List<Process>();

        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        try
        {
            rootPid = root.Id;
            rootStartTime = root.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException)
        {
            return result;
        }

        if (rootPid <= 0 || !rootStartTime.HasValue)
        {
            return result;
        }

        CollectDescendants(rootPid, rootStartTime.Value, upperBoundTime: null, result, seenPids: new HashSet<int> { rootPid });
        return result;
    }

    private static void MergeNewDescendants(List<Process> existing, int rootPid, DateTime rootStartTime, DateTime? rootExitTime)
    {
        var seen = new HashSet<int> { rootPid };
        foreach (Process p in existing)
        {
            try
            {
                seen.Add(p.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        CollectDescendants(rootPid, rootStartTime, rootExitTime, existing, seen);
    }

    private static void CollectDescendants(
        int rootPid,
        DateTime rootStartTime,
        DateTime? upperBoundTime,
        List<Process> result,
        HashSet<int> seenPids)
    {
        List<(int Pid, int ParentPid)> snapshot = SnapshotProcesses();
        if (snapshot.Count == 0)
        {
            return;
        }

        var childrenByParent = new Dictionary<int, List<int>>();
        foreach ((int pid, int ppid) in snapshot)
        {
            if (pid == 0 || pid == ppid)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(ppid, out List<int>? list))
            {
                list = [];
                childrenByParent[ppid] = list;
            }
            list.Add(pid);
        }

        var queue = new Queue<int>();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (!childrenByParent.TryGetValue(current, out List<int>? childPids))
            {
                continue;
            }

            foreach (int childPid in childPids)
            {
                if (!seenPids.Add(childPid))
                {
                    continue;
                }

                Process? child = null;
                try
                {
                    child = Process.GetProcessById(childPid);
                    if (IsConsoleHostProcess(child))
                    {
                        child.Dispose();
                        continue;
                    }

                    DateTime childStart = child.StartTime;
                    if (childStart < rootStartTime)
                    {
                        child.Dispose();
                        continue;
                    }

                    if (upperBoundTime.HasValue && childStart > upperBoundTime.Value)
                    {
                        child.Dispose();
                        continue;
                    }

                    result.Add(child);
                    queue.Enqueue(childPid);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    child?.Dispose();
                }
            }
        }
    }

    private static bool IsConsoleHostProcess(Process process)
    {
        try
        {
            string name = process.ProcessName;
            return string.Equals(name, "conhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "OpenConsole", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static void WaitAndDisposeDescendants(List<Process> descendants, long deadline)
    {
        foreach (Process child in descendants)
        {
            try
            {
                if (!HasExitedSafe(child))
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException)
            {
            }
        }

        foreach (Process child in descendants)
        {
            try
            {
                if (!HasExitedSafe(child))
                {
                    int remaining = RemainingTimeoutMs(deadline);
                    if (!child.WaitForExit(remaining))
                    {
                        int childPid = 0;
                        string childName = "?";
                        try { childPid = child.Id; childName = child.ProcessName; } catch { }
                        Trace.TraceWarning($"Descendant process '{childName}' (PID {childPid}) did not exit within timeout after termination.");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
            {
            }
        }
    }

    private static List<(int Pid, int ParentPid)> SnapshotProcesses()
    {
        var list = new List<(int, int)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return list;
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>(), szExeFile = string.Empty };
            if (!Process32First(snapshot, ref entry))
            {
                return list;
            }

            do
            {
                list.Add((unchecked((int)entry.th32ProcessID), unchecked((int)entry.th32ParentProcessID)));
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return list;
    }

    private static bool HasExitedSafe(Process proc)
    {
        try
        {
            return proc.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
        {
            return true;
        }
    }

    private static DateTime? TryGetExitTime(Process proc)
    {
        try
        {
            return proc.HasExited ? proc.ExitTime : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static int RemainingTimeoutMs(long deadline)
    {
        long remaining = deadline - Environment.TickCount64;
        return remaining > 0 ? (int)Math.Min(int.MaxValue, remaining) : 0;
    }

    private static void TryClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch
        {
        }
    }

    private static void TryClearReadOnlyInDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                TryClearReadOnly(file);
            }
        }
        catch
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
