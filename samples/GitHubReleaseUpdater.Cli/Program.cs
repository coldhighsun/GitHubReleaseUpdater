using System.Globalization;
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.Download;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.Verification;
using GitHubReleaseUpdater.Versioning;

return await Cli.RunAsync(args);

internal static class Cli
{
    /// <summary>
    /// CLI usage text shown for <c>--help</c> and on argument errors.
    /// </summary>
    private const string Usage = """
        GitHubReleaseUpdater sample CLI

        Usage:
          gru check    --owner <o> --repo <r> --current <ver> [options]
          gru download --owner <o> --repo <r> --current <ver> --out <dir> [options]

        Options:
          --token <t>        GitHub token (or set GITHUB_TOKEN). Needed for private repos / higher rate limits.
          --base-url <url>   API base for GitHub Enterprise, e.g. https://ghe.example.com/api/v3/
          --prerelease       Consider pre-releases as candidates.
          --tag-prefix <p>   Extra tag prefix to strip, e.g. "release-".
          --asset <pattern>  Asset pattern with wildcards and {os} {arch} {rid} {version} {tagversion} {tag}.
                             Default: auto-detect for the current OS/arch.
          --sha256 <hex>     Expected SHA-256 instead of reading it from the release.
                             Cannot be combined with --no-verify.
          --require-checksum Fail when no checksum can be found.
          --no-verify        Skip checksum verification. Cannot be combined with --sha256.
          --out <dir>        Download directory (download only).
        """;

    /// <summary>
    /// Runs the CLI against the real console and GitHub, falling back to <c>GITHUB_TOKEN</c> when no
    /// <c>--token</c> is given and cancelling on Ctrl+C.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancelKeyPress = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancelKeyPress;
        try
        {
            return await RunAsync(args, Console.Out, Console.Error, httpClient: null, Environment.GetEnvironmentVariable("GITHUB_TOKEN"), cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKeyPress;
        }
    }

    /// <summary>
    /// Runs the CLI, writing to <paramref name="stdout"/>/<paramref name="stderr"/> and sending requests through
    /// <paramref name="httpClient"/> (the library's shared client when null), and returns the process exit code:
    /// 0 on success, 1 on a runtime failure, 2 on invalid arguments, 130 when <paramref name="cancellationToken"/>
    /// is cancelled. <paramref name="defaultToken"/> is used when no <c>--token</c> is given. Touches no
    /// process-wide state, so it can be tested in isolation.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, HttpClient? httpClient, string? defaultToken, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return 0;
        }

        var command = args[0];
        if (command is not ("check" or "download"))
        {
            stderr.WriteLine($"Unknown command '{command}'.");
            stderr.WriteLine(Usage);
            return 2;
        }

        var opts = ParseArgs(args.Skip(1));

        // Validate everything up front so a bad invocation fails before any network call.
        string[] required = command == "download" ? ["owner", "repo", "current", "out"] : ["owner", "repo", "current"];
        var missing = required.Where(key => string.IsNullOrWhiteSpace(opts.GetValueOrDefault(key))).ToList();
        if (missing.Count > 0)
        {
            foreach (var key in missing)
            {
                stderr.WriteLine($"Missing required option --{key}.");
            }
            stderr.WriteLine(Usage);
            return 2;
        }
        var owner = opts["owner"]!;
        var repo = opts["repo"]!;
        var current = opts["current"]!;
        string? outDir = null;
        if (command == "download")
        {
            outDir = opts["out"]!;
            if (!IsValidPath(outDir))
            {
                stderr.WriteLine($"--out '{outDir}' is not a valid directory path.");
                return 2;
            }
        }
        if (!SemanticVersion.TryParse(current, out var currentVersion))
        {
            stderr.WriteLine($"--current '{current}' is not a valid version.");
            return 2;
        }

        Uri? baseUrl = null;
        if (opts.TryGetValue("base-url", out var b) && b is not null && !Uri.TryCreate(b, UriKind.Absolute, out baseUrl))
        {
            stderr.WriteLine($"--base-url '{b}' is not a valid absolute URL.");
            return 2;
        }

        var tagPrefix = opts.GetValueOrDefault("tag-prefix");
        var token = opts.GetValueOrDefault("token") ?? defaultToken;
        IChecksumProvider? checksums = null;
        if (opts.TryGetValue("sha256", out var sha))
        {
            if (opts.ContainsKey("no-verify"))
            {
                stderr.WriteLine("--sha256 and --no-verify cannot be used together.");
                return 2;
            }
            if (ChecksumParser.NormalizeDigest(sha) is not { } digest)
            {
                stderr.WriteLine($"--sha256 '{sha}' is not a valid SHA-256 hex digest.");
                return 2;
            }
            checksums = new StaticChecksumProvider(digest);
        }
        else if (opts.ContainsKey("no-verify"))
        {
            checksums = NoChecksumProvider.Instance;
        }

        IAssetSelector? assetSelector = null;
        if (opts.TryGetValue("asset", out var pattern))
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                stderr.WriteLine("--asset requires a non-empty pattern.");
                return 2;
            }
            assetSelector = new PatternAssetSelector(pattern, tagPrefix: tagPrefix);
        }

        using var updater = new ReleaseUpdater(new UpdaterOptions
        {
            Owner = owner,
            Repo = repo,
            CurrentVersion = currentVersion,
            Token = token,
            BaseUrl = baseUrl,
            HttpClient = httpClient,
            IncludePrerelease = opts.ContainsKey("prerelease"),
            TagPrefix = tagPrefix,
            AssetSelector = assetSelector,
            ChecksumProvider = checksums,
            RequireChecksum = opts.ContainsKey("require-checksum"),
            UserAgent = "GitHubReleaseUpdater.Cli",
        });

        try
        {
            stdout.WriteLine($"Checking {owner}/{repo} (current {currentVersion}, runtime {RuntimeInfo.Current.Rid})...");
            var check = await updater.CheckForUpdateAsync(cancellationToken: cancellationToken);

            if (!check.Success)
            {
                stderr.WriteLine($"Check failed: {check.Error!.Message}");
                return 1;
            }

            if (check.LatestVersion is null)
            {
                stdout.WriteLine("No parseable release found.");
                if (check.SkippedTags.Count > 0)
                {
                    stdout.WriteLine("Skipped tags: " + string.Join(", ", check.SkippedTags));
                }
                return 0;
            }

            stdout.WriteLine($"Latest: {check.LatestVersion} ({check.Release!.TagName}), published {check.Release.PublishedAt:u}");
            if (!check.IsUpdateAvailable)
            {
                stdout.WriteLine("You are up to date.");
                return 0;
            }

            stdout.WriteLine("Update available!");
            stdout.WriteLine("Assets:");
            foreach (var a in check.Release.Assets)
                stdout.WriteLine($"  {(a == check.SelectedAsset ? "*" : " ")} {a.Name}  ({FormatBytes(a.Size)})");
            if (!string.IsNullOrWhiteSpace(check.ReleaseNotes))
            {
                stdout.WriteLine();
                stdout.WriteLine(check.ReleaseNotes.Trim());
            }

            if (outDir is null)
            {
                return 0;
            }

            stdout.WriteLine();
            var download = await updater.DownloadAsync(check, outDir, new ConsoleProgress(stdout), cancellationToken);
            stdout.WriteLine();
            stdout.WriteLine($"Saved to {download.FilePath}");
            stdout.WriteLine($"SHA-256  {download.Sha256}  [{(download.Verified ? "verified" : "NOT verified - no checksum published")}]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("Cancelled.");
            return 130;
        }
        catch (GitHubApiException ex)
        {
            stderr.WriteLine($"GitHub API error ({(int)ex.StatusCode}): {ex.Message}");
            return 1;
        }
        catch (UpdaterException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (TimeoutException ex)
        {
            stderr.WriteLine($"Timed out: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"File error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Parses <c>--key value</c>/<c>--key=value</c>/<c>--flag</c> style arguments into a case-insensitive map.
    /// </summary>
    private static Dictionary<string, string?> ParseArgs(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = a[2..];
            var eq = key.IndexOf('=');
            if (eq >= 0)
            {
                result[key[..eq]] = key[(eq + 1)..];
            }
            else if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = list[++i];
            }
            else
            {
                result[key] = null; // flag
            }
        }
        return result;
    }

    /// <summary>
    /// True when <paramref name="path"/> contains no invalid path characters and can be resolved to a full path, so
    /// a malformed <c>--out</c> is rejected up front instead of throwing from the download.
    /// </summary>
    private static bool IsValidPath(string path)
    {
        if (path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }
        try
        {
            Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Formats a byte count using the largest unit (B/KB/MB/GB) that keeps the value under 1024.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return string.Create(CultureInfo.InvariantCulture, $"{v:0.#} {units[u]}");
    }

    /// <summary>
    /// Renders download progress as a single overwritten line on <paramref name="output"/>.
    /// </summary>
    private sealed class ConsoleProgress(TextWriter output) : IProgress<DownloadProgress>
    {
        /// <summary>
        /// Writes the current percentage, bytes transferred, speed and ETA to the output writer.
        /// </summary>
        public void Report(DownloadProgress p)
        {
            var pct = p.Percentage is { } x ? string.Create(CultureInfo.InvariantCulture, $"{x,5:0.0}%") : "  ?  ";
            var speed = FormatBytes((long)p.BytesPerSecond) + "/s";
            var eta = p.EstimatedRemaining is { } r ? $" ETA {r:mm\\:ss}" : string.Empty;
            output.Write($"\r{pct}  {FormatBytes(p.BytesReceived)}/{(p.TotalBytes is { } t ? FormatBytes(t) : "?")}  {speed}{eta}   ");
        }
    }
}
