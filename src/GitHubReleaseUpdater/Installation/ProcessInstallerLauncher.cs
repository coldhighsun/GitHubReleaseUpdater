using System.ComponentModel;
using System.Diagnostics;
using GitHubReleaseUpdater.Exceptions;

namespace GitHubReleaseUpdater.Installation;

/// <summary>
/// Default <see cref="IInstallerLauncher"/>: starts the installer through the operating system shell, so an
/// installer whose manifest requires elevation triggers the normal UAC prompt, and returns without waiting.
/// </summary>
public sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    /// <summary>
    /// Win32 <c>ERROR_CANCELLED</c>, reported when the user declines the elevation prompt.
    /// </summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Starts a process from the given start info and returns its id, or null when no process was started.
    /// </summary>
    private readonly Func<ProcessStartInfo, int?> _start;

    /// <summary>
    /// Creates a launcher that starts real processes.
    /// </summary>
    public ProcessInstallerLauncher() : this(StartProcess) { }

    /// <summary>
    /// Creates a launcher over a custom process starter (used by tests to avoid running anything).
    /// </summary>
    internal ProcessInstallerLauncher(Func<ProcessStartInfo, int?> start)
    {
        _start = start;
    }

    /// <inheritdoc />
    public InstallerLaunchResult Launch(string installerPath, InstallerLaunchOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        ArgumentNullException.ThrowIfNull(options);

        var fullPath = Path.GetFullPath(installerPath);
        if (!File.Exists(fullPath))
        {
            throw new InstallerLaunchException($"The installer '{fullPath}' does not exist.", fullPath);
        }

        var startInfo = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = true,
            WorkingDirectory = options.WorkingDirectory ?? Path.GetDirectoryName(fullPath) ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(options.Arguments))
        {
            startInfo.Arguments = options.Arguments;
        }

        try
        {
            return new InstallerLaunchResult(fullPath, _start(startInfo));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new InstallerLaunchException($"Starting the installer '{fullPath}' was cancelled by the user.", fullPath, isUserCancelled: true, ex);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new InstallerLaunchException($"The installer '{fullPath}' could not be started: {ex.Message}", fullPath, isUserCancelled: false, ex);
        }
    }

    /// <summary>
    /// Starts <paramref name="startInfo"/> as a real process and returns its id.
    /// </summary>
    private static int? StartProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process?.Id;
    }
}
