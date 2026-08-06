using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Guance.Windows;

internal sealed record RumPlatformInfo(
    string Os,
    string OsVersion,
    string OsVersionMajor,
    string? Device,
    string? Model,
    string Architecture,
    string? ScreenSize,
    string Locale,
    string ApplicationUuid)
{
    public static RumPlatformInfo Capture()
    {
        var osVersion = Environment.OSVersion.Version.ToString();
        var locale = CultureInfo.CurrentCulture.Name;
        if (string.IsNullOrWhiteSpace(locale))
        {
            locale = CultureInfo.CurrentUICulture.Name;
        }

        return new RumPlatformInfo(
            GetOsName(),
            osVersion,
            Environment.OSVersion.Version.Major.ToString(CultureInfo.InvariantCulture),
            ReadRegistryValue(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer"),
            ReadRegistryValue(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName"),
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            GetScreenSize(),
            string.IsNullOrWhiteSpace(locale) ? "unknown" : locale,
            (Assembly.GetEntryAssembly() ?? typeof(RumPlatformInfo).Assembly).ManifestModule.ModuleVersionId.ToString("N"));
    }

    public static string GetNetworkType()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(item => item.NetworkInterfaceType)
                .ToArray();

            if (interfaces.Contains(NetworkInterfaceType.Wireless80211))
            {
                return "wifi";
            }

            if (interfaces.Any(item => item is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT))
            {
                return "ethernet";
            }

            if (interfaces.Contains(NetworkInterfaceType.Ppp))
            {
                return "mobile";
            }

            return NetworkInterface.GetIsNetworkAvailable() ? "unknown" : "none";
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return "unknown";
        }
    }

    private static string GetOsName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return RuntimeInformation.OSDescription;
        }

        return ReadRegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
    }

    private static string? ReadRegistryValue(string path, string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static string? GetScreenSize()
    {
#if WINDOWS
        try
        {
            var size = System.Windows.Forms.SystemInformation.PrimaryMonitorSize;
            return size.Width > 0 && size.Height > 0 ? $"{size.Width}*{size.Height}" : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
#else
        return null;
#endif
    }
}
