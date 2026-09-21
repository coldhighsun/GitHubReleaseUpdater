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
          --asset <pattern>  Asset pattern with wildcards and {os} {arch} {rid} {version} {tag}.
                             Default: auto-detect for the current OS/arch.
          --sha256 <hex>     Expected SHA-256 instead of reading it from the release.
          --require-checksum Fail when no checksum can be found.
          --no-verify        Skip checksum verification.
          --out <dir>        Download directory (download only).
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var command = args[0];
        var opts = ParseArgs(args.Skip(1));

        var owner = Require(opts, "owner");
        var repo = Require(opts, "repo");
        var current = Require(opts, "current");
        if (!SemanticVersion.TryParse(current, out var currentVersion))
        {
            Console.Error.WriteLine($"--current '{current}' is not a valid version.");
            return 2;
        }

        var token = opts.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        IChecksumProvider? checksums = null;
        if (opts.ContainsKey("no-verify")) checksums = NoChecksumProvider.Instance;
        else if (opts.TryGetValue("sha256", out var sha) && sha is not null) checksums = new StaticChecksumProvider(sha);

        using var updater = new ReleaseUpdater(new UpdaterOptions
        {
            Owner = owner,
            Repo = repo,
            CurrentVersion = currentVersion,
            Token = token,
            BaseUrl = opts.TryGetValue("base-url", out var b) && b is not null ? new Uri(b) : null,
            IncludePrerelease = opts.ContainsKey("prerelease"),
            TagPrefix = opts.GetValueOrDefault("tag-prefix"),
            AssetSelector = opts.TryGetValue("asset", out var pattern) && pattern is not null ? new PatternAssetSelector(pattern) : null,
            ChecksumProvider = checksums,
            RequireChecksum = opts.ContainsKey("require-checksum"),
            UserAgent = "GitHubReleaseUpdater.Cli",
        });

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            Console.WriteLine($"Checking {owner}/{repo} (current {currentVersion}, runtime {RuntimeInfo.Current.Rid})...");
            var check = await updater.CheckForUpdateAsync(cancellationToken: cts.Token);

            if (!check.Success)
            {
                Console.Error.WriteLine($"Check failed: {check.Error!.Message}");
                return 1;
            }

            if (check.LatestVersion is null)
            {
                Console.WriteLine("No parseable release found.");
                if (check.SkippedTags.Count > 0) Console.WriteLine("Skipped tags: " + string.Join(", ", check.SkippedTags));
                return 0;
            }

            Console.WriteLine($"Latest: {check.LatestVersion} ({check.Release!.TagName}), published {check.Release.PublishedAt:u}");
            if (!check.IsUpdateAvailable)
            {
                Console.WriteLine("You are up to date.");
                return 0;
            }

            Console.WriteLine("Update available!");
            Console.WriteLine("Assets:");
            foreach (var a in check.Release.Assets)
                Console.WriteLine($"  {(a == check.SelectedAsset ? "*" : " ")} {a.Name}  ({FormatBytes(a.Size)})");
            if (!string.IsNullOrWhiteSpace(check.ReleaseNotes))
            {
                Console.WriteLine();
                Console.WriteLine(check.ReleaseNotes.Trim());
            }

            if (command == "check") return 0;
            if (command != "download")
            {
                Console.Error.WriteLine($"Unknown command '{command}'.");
                Console.Error.WriteLine(Usage);
                return 2;
            }

            var outDir = Require(opts, "out");
            Console.WriteLine();
            var download = await updater.DownloadAsync(check, outDir, new ConsoleProgress(), cts.Token);
            Console.WriteLine();
            Console.WriteLine($"Saved to {download.FilePath}");
            Console.WriteLine($"SHA-256  {download.Sha256}  [{(download.Verified ? "verified" : "NOT verified - no checksum published")}]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (GitHubApiException ex)
        {
            Console.Error.WriteLine($"GitHub API error ({(int)ex.StatusCode}): {ex.Message}");
            return 1;
        }
        catch (UpdaterException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
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
    /// Returns the value of a required option, or prints usage and exits the process when it is missing.
    /// </summary>
    private static string Require(Dictionary<string, string?> opts, string key)
    {
        if (opts.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        Console.Error.WriteLine($"Missing required option --{key}.");
        Console.Error.WriteLine(Usage);
        Environment.Exit(2);
        return string.Empty;
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
    /// Renders download progress as a single overwritten console line.
    /// </summary>
    private sealed class ConsoleProgress : IProgress<DownloadProgress>
    {
        /// <summary>
        /// Writes the current percentage, bytes transferred, speed and ETA to the console.
        /// </summary>
        public void Report(DownloadProgress p)
        {
            var pct = p.Percentage is { } x ? string.Create(CultureInfo.InvariantCulture, $"{x,5:0.0}%") : "  ?  ";
            var speed = FormatBytes((long)p.BytesPerSecond) + "/s";
            var eta = p.EstimatedRemaining is { } r ? $" ETA {r:mm\\:ss}" : string.Empty;
            Console.Write($"\r{pct}  {FormatBytes(p.BytesReceived)}/{(p.TotalBytes is { } t ? FormatBytes(t) : "?")}  {speed}{eta}   ");
        }
    }
}
