# GitHubReleaseUpdater

[![.NET](https://github.com/coldhighsun/GitHubReleaseUpdater/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/GitHubReleaseUpdater/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/coldhighsun/GitHubReleaseUpdater/branch/main/graph/badge.svg)](https://codecov.io/gh/coldhighsun/GitHubReleaseUpdater)
[![NuGet](https://img.shields.io/nuget/v/GitHubReleaseUpdater.svg)](https://www.nuget.org/packages/GitHubReleaseUpdater)
[![Pre-release](https://img.shields.io/nuget/vpre/GitHubReleaseUpdater.svg?label=pre-release)](https://www.nuget.org/packages/GitHubReleaseUpdater/absoluteLatest)
[![NuGet Downloads](https://img.shields.io/nuget/dt/GitHubReleaseUpdater.svg)](https://www.nuget.org/packages/GitHubReleaseUpdater)
[![.NET Version](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

English | [中文](README.zh-CN.md)

A dependency-free .NET 10 library that turns GitHub Releases into an update source: **check for a newer version → pick an asset → download with progress → verify SHA‑256 → (optionally) launch the installer**.

Works with github.com and GitHub Enterprise Server, anonymously or with a token (private repositories / higher rate limits). It deliberately does **not** extract archives, replace files or restart the app — that is up to the caller (or the installer you launch).

See [CHANGELOG.md](CHANGELOG.md) for release notes and breaking changes.

## Quick start

```csharp
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;

using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",
    Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),    // optional
    // BaseUrl = new Uri("https://ghe.example.com/api/v3/"),       // GitHub Enterprise
    AssetSelector = new PatternAssetSelector("gh_{version}_windows_amd64.zip"),
});

var check = await updater.CheckForUpdateAsync();   // never throws (except cancellation)
if (!check.Success)
{
    Console.WriteLine($"Check failed: {check.Error}");
    return;
}

if (check.IsUpdateAvailable)
{
    Console.WriteLine($"New version {check.Update!.Version}: {check.ReleaseNotes}");

    var progress = new Progress<DownloadProgress>(p => Console.Write($"\r{p.Percentage:0.0}%"));
    var file = await updater.DownloadAsync(check, downloadDir, progress);

    Console.WriteLine($"{file.FilePath}  sha256={file.Sha256}  verified={file.Verified}");
}
```

The current version usually comes from the running app: `SemanticVersion.TryFromAssembly(typeof(App).Assembly, out var current)` reads `AssemblyInformationalVersion`.

## Core concepts

| Type | Purpose |
|---|---|
| `ReleaseUpdater` | Facade: `CheckForUpdateAsync()`, `ListAssets()`, `DownloadAsync()`, `LaunchInstallerAsync()`, `DownloadAndLaunchInstallerAsync()`. |
| `UpdaterOptions` | `Owner`/`Repo`/`CurrentVersion` are required. Optional: `Token`, `BaseUrl`, `IncludePrerelease`, `TagPrefix`, `AssetSelector`, `ChecksumProvider`, `RequireChecksum`, `HttpClient`, `Timeout`, `LastCheckStore`, `MinimumCheckInterval`, `Download*` retry/resume options, `AssetUrlRewriter`, `InstallerLauncher`, `TimeProvider`. |
| `UpdateCheckResult` | Outcome of a check: `Success`/`Error`, `LatestVersion`/`Release`, `Throttled`, and `Update` (non-null exactly when an update is available). |
| `SemanticVersion` | Minimal SemVer 2.0. Tolerates a `v` prefix, a missing patch (`1.2`) and a custom `TagPrefix`. |
| `IAssetSelector` | `RuntimeAssetSelector` (default, matches current OS/arch), `PatternAssetSelector` (wildcards plus `{os}` `{arch}` `{rid}` `{version}` `{tagversion}` `{tag}`), `DelegateAssetSelector` (custom predicate). |
| `IChecksumProvider` | `ReleaseChecksumProvider` (default: `<asset>.sha256` → `SHA256SUMS`/`*checksums.txt` → GitHub `digest`), `StaticChecksumProvider`, `NoChecksumProvider`. |
| `IGitHubReleaseClient` | GitHub API abstraction; inject your own for tests or custom transports. |
| `ILastCheckStore` | Optional persistence for throttling and skipped versions: `InMemoryLastCheckStore`, `JsonFileLastCheckStore(path)`, or your own. |

## Version resolution

- `IncludePrerelease = false` (default): uses `/releases/latest` (no pre-releases or drafts).
- `IncludePrerelease = true`: scans the latest `ReleaseScanCount` releases (default 30) and takes the highest SemVer; drafts and unparseable tags are skipped (the latter listed in `SkippedTags`).
- `IsUpdateAvailable` is true only when `latest > current` and the version is not recorded as skipped.

## Throttling and skipped versions

No throttling by default. Set `LastCheckStore` and `MinimumCheckInterval` to opt in:

```csharp
var options = new UpdaterOptions
{
    Owner = "cli", Repo = "cli", CurrentVersion = "2.90.0",
    LastCheckStore = new JsonFileLastCheckStore(Path.Combine(dataDir, "update-state.json")),
    MinimumCheckInterval = TimeSpan.FromHours(24),
};
```

- A check inside the window returns `Throttled == true` without calling GitHub.
- A failure to record the check time keeps the check result; the error is in `LastCheckStoreError`.
- `SetSkippedVersionAsync(version)` / `ClearSkippedVersionAsync()` suppress or restore a version (it still appears in `LatestVersion`).
- `CheckForUpdateAsync(bypassSkippedVersion: true, bypassThrottle: true)` suits a user-initiated "check now".

## Automatic update flow (`GitHubReleaseUpdater.AutoUpdate`)

`UpdateController` is a UI-agnostic state machine: check in the background (first after 30 s, then every 24 h), announce a version once, ask before installing, download, start the installer. Implement `IUpdatePresenter` (tray balloon, message box, closing the app, …); every member has a do-nothing default, so nothing is installed until you ask the user.

```csharp
var controller = new UpdateController(updater, new MyPresenter(), new UpdateControllerOptions
{
    IsAutoCheckEnabled = () => settings.CheckForUpdates,
    InstallerLaunchOptions = InstallerLaunchOptions.InnoSetupSilent,
});
_ = controller.RunAsync(shutdownToken);       // background loop
await controller.CheckAsync(manual: true);    // "Check for updates" button
await controller.InstallAsync();              // "Update to x.y.z"
```

- **States:** `Idle`, `Checking`, `UpToDate`, `Available`, `Failed`, `Installing`, `Downloading`, `Downloaded`; `Changed` fires on every change. `LastFailure` (`Stage`: check, download or install; `Error`; `UserInitiated`) and `Progress` (with `ProgressChanged`) expose the latest failure and download progress; `LastError` is its exception. `UserInitiated` is false for a failed automatic check, which an application usually does not show.
- `CanDownload`, `CanInstall` and `IsBusy` say what `DownloadAsync()` / `InstallAsync()` would do now, so a UI can bind to them instead of mapping `State` itself.
- **Manual checks** bypass throttling and the skipped version; one requested during an automatic check runs right after it. `SkipVersionAsync()` records the found version as skipped.
- `DownloadAsync()` pre-downloads and verifies; `InstallAsync()` then starts the installer without downloading again.
- A newer release without a matching package stays `Available` with `HasInstaller == false` — show the install entry only when `HasInstaller` is true.
- `LaunchInstaller = false` is for non-installer packages (e.g. an archive): the file is downloaded, verified and handed to `IUpdatePresenter.PackageDownloaded`.
- `BeforeInstallAsync` runs just before the installer starts (e.g. to save state). `WatchInstaller = true` waits for the installer process to end.
- `CleanDownloadDirectory = true` deletes earlier downloads (use a dedicated directory only); `DownloadDirectory` defaults to a folder under the system temp directory.
- **Verification is required by default** (`RequireVerifiedDownload`): an installer whose checksum cannot be verified is not started.
- `AskToInstallAfterManualCheck = false` makes a manual check only look; `SkipChecksWhileUpdateKnown = true` stops background rounds from asking again while an update is known.
- **Inno Setup silent update that restarts the app:** use `InstallerLaunchOptions.InnoSetupSilent`, close the app from `IUpdatePresenter.InstallerStarted`, and leave `skipifsilent` off the `[Run]` entry that starts the app. Extra switches: `InnoSetupSilent.WithArguments("/CLOSEAPPLICATIONS")`.
- Exceptions from your presenter, `Changed` handlers or `IsAutoCheckEnabled` go to `UpdateControllerOptions.OnError`. Call the controller from one thread (normally the UI thread).

## Downloading

- Streams to `<name>.partial`, then renames atomically. Throws `UpdaterException` when `Content-Length` does not match the received bytes.
- With a known hash the file is verified before it is moved into place; a mismatch discards it and throws `ChecksumMismatchException`. Without one, `DownloadResult.Verified` is `false` (`RequireChecksum = true` fails instead, before any transfer).
- Private repository assets are downloaded through the API with a token.
- **Retries:** transient failures (I/O error, timeout, truncated or stalled body) are retried with exponential backoff — `DownloadMaxRetryAttempts` (default 2), `DownloadRetryDelay` (default 1 s), `DownloadIdleTimeout` (default 30 s, `null` to wait forever). Checksum mismatches, disk errors and cancellation are never retried.
- **Resume:** an interrupted download resumes via HTTP Range, tracked by a `<name>.partial.meta` sidecar; a partial from a different asset is discarded. `DownloadAllowResume = false` always restarts and leaves no partial file.
- **Timeout:** `UpdaterOptions.Timeout` (default 10 s, `null` to rely on `HttpClient.Timeout`) bounds each API call and the wait for asset response headers; expiry throws `TimeoutException`. Valid range: 1 ms to ~49.7 days.
- **HttpClient:** when `HttpClient` is null, a process-wide shared one is used, so creating a `ReleaseUpdater` per check is fine.

### Mirrors

Where GitHub's download servers are slow or blocked, `AssetUrlRewriter` maps a package's download address to another one:

```csharp
AssetUrlRewriter = url => new Uri("https://mirror.example.com/" + url),   // return the same address to download as usual
```

Only the package is fetched from the mirror, and only when its checksum is known. The token is never sent there; checksums and release data still come from GitHub. If the mirror fails or delivers something unusable, the package is downloaded from GitHub once. The option is ignored when you pass your own `IGitHubReleaseClient` (set `GitHubReleaseClient.AssetUrlRewriter` instead).

## Choosing assets

`RuntimeAssetSelector` never guesses: a name like `MyApp-Setup-1.2.3.exe` with no OS or architecture is not selected. For a single platform-less installer use `new RuntimeAssetSelector { AllowUnknownPlatform = true }` or `new PatternAssetSelector("*Setup*.exe")`.

To let the user pick a platform, list the assets:

```csharp
foreach (var a in updater.ListAssets(check))   // checksum/signature files left out by default
    Console.WriteLine($"{a.Asset.Name,-40} os={a.Platform.Os ?? "?"} arch={a.Platform.Arch ?? "?"} {(a.MatchesCurrent ? "<- this machine" : "")}");

var chosen = updater.ListAssets(check).First(a => a.Platform is { Os: "linux", Arch: "arm64" });
await updater.DownloadAsync(check.Release!, chosen.Asset, downloadDir);
```

`AssetPlatformDetector.Detect(name)` returns an `AssetPlatform` (`Os`, `Arch`, `Rid`); unknown parts are `null`. To auto-pick for another platform use `new RuntimeAssetSelector(new RuntimeInfo("linux", "arm64"))`.

## Launching the installer

After the download is verified, the library can start an `.exe`/`.msi` through the OS shell (UAC prompts as usual) and return immediately; the installer is expected to close and restart your app.

```csharp
using GitHubReleaseUpdater.Installation;

// One step: download + verify + start.
await updater.DownloadAndLaunchInstallerAsync(check, downloadDir, InstallerLaunchOptions.InnoSetupSilent, progress);

// Or two steps:
var file = await updater.DownloadAsync(check, downloadDir, progress);
await updater.LaunchInstallerAsync(file, new InstallerLaunchOptions { Arguments = "/S", RequireVerified = true });
```

- `InstallerLaunchOptions`: `Arguments`, `WorkingDirectory`, `RequireVerified`; presets `InnoSetupSilent` and `InnoSetupVerySilent`.
- The installer never starts after a failed, cancelled or unverified download. The file is re-hashed right before launch, so a file modified after download is not run (`ChecksumMismatchException`).
- Provide `UpdaterOptions.InstallerLauncher` to customize or stub out the launch.
- **Signature check:** the checksum comes from the same release, so whoever can change the release can change both. Set `UpdaterOptions.InstallerVerifier` to check the installer just before it starts; one that is refused is not started (`InstallerVerificationException` with `Reason`). `UpdateController` asks right after the download (also when it only downloads, with `LaunchInstaller` off), so a refused package is never offered, and asks again just before the start. `AuthenticodeInstallerVerifier` checks the Authenticode signature of a Windows installer with the operating system and, with `ExpectedSubjects` (the full subject name of the signing certificate, such as `CN=Contoso Ltd, O=Contoso Ltd, L=Redmond, C=US` — the usual way to pin a publisher, since a common name alone is not unique and a thumbprint changes at every renewal), `ExpectedPublishers` (the common name only) or `ExpectedThumbprints` (SHA-1 or SHA-256), who signed it; each kind that is given must match, and without any a valid signature of anyone passes. On Windows the file is held open for reading from its hash check until the installer has started, so it cannot be replaced in between (elsewhere .NET file sharing is only advisory, so keep the download directory private). An installer the verifier refused is deleted and not downloaded again for the same asset (`IsInstallerRefused`); a manual check allows another try. Revocation is not checked online. On other systems it refuses everything (`AuthenticodeInstallerVerifier.IsSupported`), so set it only on Windows. Implement `IInstallerVerifier` for other schemes.

## Exceptions

All derive from `UpdaterException`: `GitHubApiException` (`StatusCode`, `IsRateLimited`, `RateLimitResetAt`), `AssetNotFoundException` (`AvailableAssets`), `ChecksumMismatchException` (`Expected`/`Actual`), `InstallerLaunchException` (`FilePath`, `IsUserCancelled`), `InstallerVerificationException` (`FilePath`, `Reason`).

`CheckForUpdateAsync()` catches everything and reports it via `Success`/`Error`; only caller cancellation throws `OperationCanceledException`. `DownloadAsync()` throws.

## Sample CLI

```bash
dotnet run --project samples/GitHubReleaseUpdater.Cli -- check --owner cli --repo cli --current 1.0.0
```

```bash
dotnet run --project samples/GitHubReleaseUpdater.Cli -- download --owner cli --repo cli --current 1.0.0 --asset "gh_{version}_windows_amd64.zip" --require-checksum --out ./downloads
```

Other options: `--token`, `--base-url`, `--prerelease`, `--tag-prefix`, `--sha256`, `--no-verify`.

## Build and test

```bash
dotnet build
dotnet test      # HTTP stubs only, no network
dotnet pack src/GitHubReleaseUpdater -c Release
```

## License

MIT
