using System.Runtime.InteropServices;

namespace GitHubReleaseUpdater.Assets;

/// <summary>Describes the OS / architecture an asset should target. Defaults to the current process.</summary>
public sealed record RuntimeInfo(string Os, string Arch)
{
    /// <summary>Runtime identifier in the <c>os-arch</c> form, e.g. <c>win-x64</c>.</summary>
    public string Rid => $"{Os}-{Arch}";

    /// <summary>Detects the current OS and architecture.</summary>
    public static RuntimeInfo Current { get; } = Detect();

    private static RuntimeInfo Detect()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
               : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
               : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
               : RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) ? "freebsd"
               : "unknown";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };
        return new RuntimeInfo(os, arch);
    }

    /// <summary>Aliases commonly used in asset file names for each canonical OS name.</summary>
    public static IReadOnlyDictionary<string, string[]> OsAliases { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["win"] = ["win", "windows", "win32", "win64"],
        ["osx"] = ["osx", "macos", "darwin", "mac", "apple"],
        ["linux"] = ["linux"],
        ["freebsd"] = ["freebsd"],
    };

    /// <summary>Aliases commonly used in asset file names for each canonical architecture name.</summary>
    public static IReadOnlyDictionary<string, string[]> ArchAliases { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["x64"] = ["x64", "amd64", "x86_64", "x86-64", "64bit", "64-bit"],
        ["x86"] = ["x86", "i386", "i686", "386", "32bit", "32-bit", "ia32"],
        ["arm64"] = ["arm64", "aarch64"],
        ["arm"] = ["arm", "armv7", "armhf", "armv7l"],
    };
}
