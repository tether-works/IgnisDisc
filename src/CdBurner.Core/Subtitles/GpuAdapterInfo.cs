using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CdBurner.Core.Subtitles;

/// <summary>
/// Maker of a display adapter, from the PCI vendor ID Windows records for it.
/// </summary>
public enum GpuVendor
{
    Other,
    Nvidia,
    Amd,
    Intel
}

/// <summary>
/// One display adapter as Windows lists it.
/// </summary>
/// <param name="Name">Adapter name (for example "NVIDIA GeForce RTX 3090").</param>
/// <param name="Vendor">Maker, from the PCI vendor ID.</param>
/// <param name="DriverVersion">Windows driver version string (for example "32.0.16.1742"), or null.</param>
public sealed record GpuAdapterInfo(string Name, GpuVendor Vendor, string? DriverVersion)
{
    /// <summary>
    /// NVIDIA's own driver number (for example 617.42) from the Windows driver version: the last five digits of the
    /// last two fields ("32.0.16.1742" gives 617.42). Null for other makers or an unreadable version.
    /// </summary>
    public Version? NvidiaDriverVersion => Vendor == GpuVendor.Nvidia ? ParseNvidiaDriverVersion(DriverVersion) : null;

    /// <summary>
    /// Converts a Windows driver version of an NVIDIA adapter to NVIDIA's numbering ("32.0.16.1742" gives 617.42).
    /// </summary>
    public static Version? ParseNvidiaDriverVersion(string? windowsDriverVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsDriverVersion))
        {
            return null;
        }

        string[] parts = windowsDriverVersion.Trim().Split('.');
        if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        string digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5)
        {
            return null;
        }

        string last5 = digits[^5..];
        return new Version(
            int.Parse(last5[..3], CultureInfo.InvariantCulture),
            int.Parse(last5[3..], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Maps a PCI vendor ID (hex, for example "10DE") to a maker.
    /// </summary>
    public static GpuVendor VendorFromPciId(string? vendorHex) => vendorHex?.ToUpperInvariant() switch
    {
        "10DE" => GpuVendor.Nvidia,
        "1002" or "1022" => GpuVendor.Amd,
        "8086" => GpuVendor.Intel,
        _ => GpuVendor.Other
    };
}

/// <summary>
/// Reads the PC's display adapters from the Windows registry (the display adapter device class), so no extra library
/// or WMI package is needed. Only physical PCI adapters are returned; virtual displays (remote desktop, VR mirror
/// drivers) are skipped.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsGpuDetector
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Physical display adapters listed by Windows. Returns an empty list when the registry cannot be read.
    /// </summary>
    public static IReadOnlyList<GpuAdapterInfo> DetectAdapters()
    {
        var adapters = new List<GpuAdapterInfo>();
        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey == null)
            {
                return adapters;
            }

            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                // Adapter entries are numbered 0000, 0001, ...; "Properties" and "Configuration" are not adapters.
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsAsciiDigit))
                {
                    continue;
                }

                try
                {
                    using RegistryKey? adapterKey = classKey.OpenSubKey(subKeyName);
                    string? matchingId = adapterKey?.GetValue("MatchingDeviceId") as string;
                    string? vendorHex = ParsePciVendor(matchingId);
                    if (adapterKey == null || vendorHex == null)
                    {
                        continue;
                    }

                    string name = adapterKey.GetValue("DriverDesc") as string ?? "Display adapter";
                    string? driverVersion = adapterKey.GetValue("DriverVersion") as string;
                    adapters.Add(new GpuAdapterInfo(name, GpuAdapterInfo.VendorFromPciId(vendorHex), driverVersion));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // One unreadable entry must not hide the others.
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return adapters;
    }

    /// <summary>
    /// True when the NVIDIA driver's CUDA library (<c>nvcuda.dll</c>) is installed in the Windows system folder.
    /// </summary>
    public static bool IsNvidiaCudaDriverInstalled()
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The 4-digit PCI vendor ID in a device ID such as <c>PCI\VEN_10DE&amp;DEV_2204</c>; null for non-PCI devices.
    /// </summary>
    public static string? ParsePciVendor(string? matchingDeviceId)
    {
        if (string.IsNullOrWhiteSpace(matchingDeviceId) ||
            !matchingDeviceId.StartsWith(@"PCI\VEN_", StringComparison.OrdinalIgnoreCase) ||
            matchingDeviceId.Length < 12)
        {
            return null;
        }

        string vendor = matchingDeviceId.Substring(8, 4);
        return vendor.All(Uri.IsHexDigit) ? vendor.ToUpperInvariant() : null;
    }
}
