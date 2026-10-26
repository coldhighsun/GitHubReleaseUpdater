# GitHubReleaseUpdater

[![.NET](https://github.com/coldhighsun/GitHubReleaseUpdater/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/GitHubReleaseUpdater/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/coldhighsun/GitHubReleaseUpdater/branch/main/graph/badge.svg)](https://codecov.io/gh/coldhighsun/GitHubReleaseUpdater)
[![NuGet](https://img.shields.io/nuget/v/GitHubReleaseUpdater.svg)](https://www.nuget.org/packages/GitHubReleaseUpdater)
[![Pre-release](https://img.shields.io/nuget/vpre/GitHubReleaseUpdater.svg?label=pre-release)](https://www.nuget.org/packages/GitHubReleaseUpdater/absoluteLatest)
[![NuGet Downloads](https://img.shields.io/nuget/dt/GitHubReleaseUpdater.svg)](https://www.nuget.org/packages/GitHubReleaseUpdater)
[![.NET Version](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Open Issues](https://img.shields.io/github/issues/coldhighsun/GitHubReleaseUpdater.svg)](https://github.com/coldhighsun/GitHubReleaseUpdater/issues)
[![Open PRs](https://img.shields.io/github/issues-pr/coldhighsun/GitHubReleaseUpdater.svg)](https://github.com/coldhighsun/GitHubReleaseUpdater/pulls)

[English](#english) | [中文](#中文)

---

## English

A dependency-free .NET 10 library that turns GitHub Releases into an update source for your application: **check for a newer version → pick an asset → download with progress → verify SHA‑256 → (optionally) launch the installer**.

Works with github.com and GitHub Enterprise Server, anonymously or with a token (private repositories / higher rate limits). It deliberately does **not** extract archives, replace files or restart the app — that part is up to the caller (or the installer you launch).

### Quick start

```csharp
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;

using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",                                     // or SemanticVersion.FromVersion(assembly.GetName().Version!)
    Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),    // optional
    // BaseUrl = new Uri("https://ghe.example.com/api/v3/"),       // GitHub Enterprise
    AssetSelector = new PatternAssetSelector("gh_{version}_windows_amd64.zip"),
});

var check = await updater.CheckForUpdateAsync();
if (!check.Success)
{
    // CheckForUpdateAsync never throws (except OperationCanceledException) — inspect check.Error instead.
    Console.WriteLine($"Check failed: {check.Error}");
    return;
}

if (check.IsUpdateAvailable)
{
    // check.Update is guaranteed non-null here, with non-null Version/Release — no defensive null checks needed.
    Console.WriteLine($"New version {check.Update!.Version}: {check.ReleaseNotes}");

    var progress = new Progress<DownloadProgress>(p => Console.Write($"\r{p.Percentage:0.0}%"));
    var file = await updater.DownloadAsync(check, downloadDir, progress);

    Console.WriteLine($"{file.FilePath}  sha256={file.Sha256}  verified={file.Verified}");
}
```

### Core concepts

| Type | Purpose |
|---|---|
| `ReleaseUpdater` | Facade: `CheckForUpdateAsync()`, `ListAssets()`, `DownloadAsync()`, `LaunchInstallerAsync()` and `DownloadAndLaunchInstallerAsync()`. Constructing a new instance per check is fine — see [HttpClient reuse](#httpclient-reuse) below. |
| `UpdaterOptions` | `Owner`/`Repo`/`CurrentVersion` are required; `Token`, `BaseUrl`, `IncludePrerelease`, `TagPrefix`, `AssetSelector`, `ChecksumProvider`, `RequireChecksum`, `HttpClient`, `Timeout`, `LastCheckStore`, `MinimumCheckInterval`, `DownloadMaxRetryAttempts`, `DownloadRetryDelay`, `DownloadAllowResume`, `DownloadIdleTimeout`, `InstallerLauncher`, `TimeProvider` are optional. |
| `UpdateCheckResult` | Outcome of `CheckForUpdateAsync()`. `Success`/`Error` report whether the check completed without an exception (see [Exceptions](#exceptions) below). `LatestVersion`/`Release` report the highest release found even when it isn't an update; `Update` (see below) is the null-safe way to get an actionable one. `Throttled` is true when a `LastCheckStore` skipped the API call (see below). |
| `AvailableUpdate` | `UpdateCheckResult.Update`: non-null exactly when `IsUpdateAvailable`, with **non-nullable** `Version`/`Release` (`SelectedAsset` is still nullable — null when no asset matched the selector). |
| `SemanticVersion` | Minimal SemVer 2.0 implementation. Tolerates a `v` prefix, a missing patch (`1.2`) and a custom prefix (`TagPrefix`). |
| `IAssetSelector` | Chooses which release asset to download:<br>`RuntimeAssetSelector` (default — matches the current OS/arch: `win/linux/osx × x64/x86/arm64`, including aliases such as `amd64`, `aarch64`, `darwin`, `x86_64`)<br>`PatternAssetSelector` (wildcards plus `{os}` `{arch}` `{rid}` `{version}` `{tagversion}` `{tag}` placeholders; `{version}` is normalized — tag `v1.2` gives `1.2.0` — while `{tagversion}` keeps the tag's text minus prefix/`v`, giving `1.2`)<br>`DelegateAssetSelector` (custom predicate) |
| `IChecksumProvider` | Supplies the expected SHA‑256:<br>`ReleaseChecksumProvider` (default — looks for a `<asset>.sha256` sidecar → a `SHA256SUMS` / `*checksums.txt` aggregate file → the GitHub API `digest` field)<br>`StaticChecksumProvider` (caller-supplied)<br>`NoChecksumProvider` (disables verification)<br>A custom provider may return hex in any case, optionally prefixed with `sha256:`; any other value fails the download with `UpdaterException` before any bytes are transferred. |
| `IGitHubReleaseClient` | GitHub API abstraction; `GitHubReleaseClient` is the `HttpClient` implementation. Inject your own for tests or custom transports. |
| `ILastCheckStore` | Optional persistence hook (see below) for throttling and skipped-version tracking. `InMemoryLastCheckStore` is a process-lifetime implementation; `JsonFileLastCheckStore(path)` keeps the state in a small JSON file so it survives restarts (or implement it over your own settings storage). |

### Version resolution

The current version usually comes from the running application: `SemanticVersion.TryFromAssembly(typeof(App).Assembly, out var current)` reads the assembly's `AssemblyInformationalVersion` (where MinVer and similar tools put the full SemVer, pre-release and build metadata included) and returns `false` — rather than a made-up `0.0.0` — when there is none. `version.ToString(includeMetadata: false)` formats it without the `+commit` part for display.

- `IncludePrerelease = false` (default): calls `/releases/latest`; GitHub already excludes pre-releases and drafts.
- `IncludePrerelease = true`: fetches the most recent `ReleaseScanCount` releases (default 30) and takes the highest SemVer. Drafts and unparseable tags are skipped; the latter are reported in `UpdateCheckResult.SkippedTags`.
- `IsUpdateAvailable` is true only when `latest > current` (and, if a skipped version is recorded, `latest` isn't it — see below).

### Throttling and skipped versions

By default the library performs no throttling and every call to `CheckForUpdateAsync()` hits the API — persistence policy is left to the caller. To opt in, implement `ILastCheckStore` over your own storage (a settings file, a database, …) and set it on `UpdaterOptions`:

```csharp
using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",
    LastCheckStore = myStore,                        // implements ILastCheckStore
    MinimumCheckInterval = TimeSpan.FromHours(24),    // omit to disable throttling even with a store set
});

var check = await updater.CheckForUpdateAsync();
if (check.Throttled)
{
    // Too soon since the last check — no API call was made.
}
```

- `MinimumCheckInterval` throttles: a call inside the window returns `UpdateCheckResult.Throttled == true` without contacting GitHub. `UpdaterOptions.TimeProvider` supplies the clock (default `TimeProvider.System`), so tests can control it.
- Failing to record the check time (`SetLastCheckedAtAsync` throws) does not discard a successful check; the next call is just not throttled, and the exception is available as `UpdateCheckResult.LastCheckStoreError` so you can log it.
- `JsonFileLastCheckStore` is a ready-made persistent store: `LastCheckStore = new JsonFileLastCheckStore(Path.Combine(dataDir, "update-state.json"))`. Writes are atomic, and a missing or corrupt file counts as "nothing recorded".
- `myStore.SetSkippedVersionAsync(version)` suppresses `IsUpdateAvailable`/`Update` for that version on future checks (it still shows up in `LatestVersion`, so you can still display "a newer version exists but was skipped"). Call `myStore.ClearSkippedVersionAsync()` to undo it.
- For a user-initiated "check now" that should still surface a version the user previously skipped, call `CheckForUpdateAsync(bypassSkippedVersion: true)`. Throttling and recording the check time still happen as usual — only the skipped-version filter is bypassed for that call.
- For a user-initiated "check now" that should ignore `MinimumCheckInterval` too, call `CheckForUpdateAsync(bypassThrottle: true)`. This lets you reuse the same `ReleaseUpdater`/`UpdaterOptions` for both throttled automatic checks and an unthrottled manual check, instead of constructing a second updater just to disable throttling.

> **Breaking change:** `CheckForUpdateAsync` now takes `bypassSkippedVersion` and `bypassThrottle` before `cancellationToken`. A positional call like `CheckForUpdateAsync(cts.Token)` no longer compiles — pass it as `CheckForUpdateAsync(cancellationToken: cts.Token)` instead.
>
> **Breaking change:** `ILastCheckStore.SetSkippedVersionAsync` now takes a non-nullable `SemanticVersion`. A custom `ILastCheckStore` implementation must implement `ClearSkippedVersionAsync` directly instead of relying on the old `SetSkippedVersionAsync(null)` default; callers that passed `null` to clear the skipped version must call `ClearSkippedVersionAsync()` instead.

### Automatic update flow (`GitHubReleaseUpdater.AutoUpdate` namespace)

`UpdateController` is a UI-agnostic state machine for the usual update flow: check in the background (first after 30 s, then every 24 h), announce a found version once, ask before installing, download, start the installer. You implement `IUpdatePresenter` (tray balloon, message box, closing the app, …); every member has a do-nothing default (`ConfirmInstallAsync` answers no, so nothing is installed until you ask the user). It lives in this package, in the `GitHubReleaseUpdater.AutoUpdate` namespace, and works in desktop, tray, console and service applications alike.

```csharp
var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "me", Repo = "app", CurrentVersion = current,
    LastCheckStore = new JsonFileLastCheckStore(statePath),
    MinimumCheckInterval = TimeSpan.FromHours(6),
});
var controller = new UpdateController(updater, new MyPresenter(), new UpdateControllerOptions
{
    IsAutoCheckEnabled = () => settings.CheckForUpdates,
    InstallerLaunchOptions = InstallerLaunchOptions.InnoSetupSilent,
});
_ = controller.RunAsync(shutdownToken);       // background loop
await controller.CheckAsync(manual: true);    // "Check for updates" button: bypasses throttling and skipped version
await controller.InstallAsync();              // "Update to x.y.z"
```

- **States:** `State` is `Idle`, `Checking`, `UpToDate`, `Available`, `Failed` or `Installing`; `Available` holds the version found; `Changed` fires on every change.
- **Automatic checks** announce a version once; **manual checks** bypass throttling and the skipped version, report "up to date" / "failed" and go straight to the confirmation.
- A manual check requested while an automatic one runs is made when that one ends: the call returns at once, several such requests are made as one check, and that check runs under the cancellation token of the call that was running (cancel that, for example at shutdown, and it is dropped). Since it runs inside that call, the token the manual caller passed cannot cancel it or its download, the presenter is called on the running call's context, and `RunAsync` waits for it (including a confirmation left open) before its next round. An installation that is already running makes further calls do nothing.
- `SkipVersionAsync()` records the version found as skipped in the updater's `LastCheckStore` (automatic checks then stay quiet about it; a manual check still offers it; `State` becomes `UpToDate`) and returns `false` if there is nothing to skip or no store.
- The installer is downloaded to a folder under the system temporary directory (set `DownloadDirectory` to change it, for example to a per-user folder where the temporary directory is shared) and is left there after it started. `InitialDelay` and `CheckInterval` must not exceed about 49 days.
- `DownloadAsync()` fetches and verifies the installer ahead of time (`State` becomes `Downloaded`); `InstallAsync()` then starts it without downloading again.
- `BeforeInstallAsync` (with `BeforeInstallTimeout`, default 10 s) runs just before the installer starts, e.g. to save state; its failure never stops the installation.
- `WatchInstaller = true` keeps `InstallAsync()` waiting until the installer process ended (plus `InstallerGracePeriod`, default 30 s). If the app still runs, the state returns to `Downloaded` and `IUpdatePresenter.InstallerEnded` is called, so the user can try again.
- `CleanDownloadDirectory = true` deletes earlier downloads before a new one (keeping a resumable `.partial`) and once the app is up to date. Use it only for a directory dedicated to updates.
- A store that also implements `IPendingUpdateStore` (`JsonFileLastCheckStore` and `InMemoryLastCheckStore` do) remembers that a newer release was found, so the next check is not throttled and the announcement survives a restart.
- `InstallerLaunchOptions.InnoSetupSilent.WithArguments("/CLOSEAPPLICATIONS /RELAUNCH=1")` appends your installer's own switches to a preset.
- **Verification is required by default** (`RequireVerifiedDownload`): an installer whose checksum could not be verified (no sidecar, no sums file, no digest from GitHub) is not started and the failure goes to `ReportInstallFailed`. Set it to false only if your releases cannot be verified.
- Exceptions thrown by your presenter, a `Changed` handler or `IsAutoCheckEnabled` never leave the controller stuck; they go to `UpdateControllerOptions.OnError`, together with a failure to record the check time.
- Call it from one thread (normally the UI thread); `Changed` and the presenter run on the caller's context.

### HttpClient reuse

When `UpdaterOptions.HttpClient` is left null, every `GitHubReleaseClient`/`ReleaseUpdater` created that way shares one process-wide `HttpClient` internally — constructing a new `ReleaseUpdater` per check does not create a new connection pool each time. If you supply your own `HttpClient` (e.g. from `IHttpClientFactory`), the usual guidance applies: reuse it rather than creating one per check.

### Timeout

`UpdaterOptions.Timeout` bounds each individual GitHub API call and asset download; it defaults to **10 seconds**. It is layered on top of (and independent from) the underlying `HttpClient`'s own timeout — whichever is shorter wins for a given request. Set it to `null` to rely solely on the `HttpClient`'s timeout (100 seconds by default) instead.

```csharp
using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",
    Timeout = TimeSpan.FromSeconds(5),   // null to disable and use HttpClient.Timeout instead
});
```

On expiry the library throws `TimeoutException` (not `OperationCanceledException`), so it can be told apart from the caller cancelling `cancellationToken`. For asset downloads this only bounds the time to receive response headers, not the full transfer; a body that stalls mid-transfer is caught by `DownloadIdleTimeout` instead (see below).

The value must be at least 1 millisecond and at most about 49.7 days, or `Timeout.InfiniteTimeSpan`; anything else (zero, negative, sub-millisecond, or longer) makes the `ReleaseUpdater` / `GitHubReleaseClient` constructor throw `ArgumentOutOfRangeException`.

### Download retries and resume

`DownloadAsync()` retries a transient asset-download failure — a network I/O error, a request timeout, a truncated body, or a body that stalls — with exponential backoff instead of failing on the first hiccup:

```csharp
using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",
    DownloadMaxRetryAttempts = 2,                     // default; 0 disables retrying
    DownloadRetryDelay = TimeSpan.FromSeconds(1),      // doubles each retry: 1s, 2s, …
    DownloadIdleTimeout = TimeSpan.FromSeconds(30),    // default; null to wait indefinitely
});
```

`DownloadIdleTimeout` is the longest a download may go without receiving any data; the timer restarts on every chunk, so it bounds idle time, not total transfer time. A stall past it fails the attempt with `TimeoutException`, which is retried and resumed like any other transient failure.

A checksum mismatch, a local disk error (full disk, locked file), an invalid argument, or the caller cancelling is never retried.

When a download is interrupted — whether it's about to be retried, or the process is killed outright and `DownloadAsync()` is called again later — the next attempt resumes via an HTTP range request instead of starting over from byte 0, as long as the server honors it (`GitHubReleaseClient` does; a custom `IGitHubReleaseClient` opts in by overriding the range-aware `OpenAssetStreamAsync` overload). A small `<name>.partial.meta` sidecar records which asset the partial file belongs to, so a stale partial from a different asset/release is never blindly appended to — it's discarded and the download restarts from 0 instead. Set `DownloadAllowResume = false` to always restart from 0 and never leave a partial file behind on failure, matching the library's behavior before resume support was added.

### Download and verification

- Streams to `<name>.partial`, then renames atomically. With `DownloadAllowResume = true` (the default) a failed download leaves the partial file (and its `.meta` sidecar) in place so a later attempt can resume it — only a caller-cancelled download always cleans it up. With `DownloadAllowResume = false`, no partial file is left behind on any failure or cancellation.
- Throws `UpdaterException` if the server reported a Content-Length that does not match the bytes received.
- When an expected hash can be resolved the file is verified before it is moved into place; on mismatch the download is discarded (any existing file at the destination is left untouched) and `ChecksumMismatchException` is thrown, its `FilePath` naming the discarded `.partial`. When no hash is available `DownloadResult.Verified` is `false` (set `RequireChecksum = true` to fail instead — it fails before any bytes are transferred).
- Private repository assets are downloaded via the API endpoint with `Accept: application/octet-stream`; just supply a token.

### Releases with a single, platform-less installer

`RuntimeAssetSelector` never guesses: a file such as `MyApp-Setup-1.2.3.exe` that names neither an OS nor an architecture is not selected, so `SelectedAsset` is null. For a release that publishes one installer for one platform, either opt in with `new RuntimeAssetSelector { AllowUnknownPlatform = true }` (platform-less assets then rank below any asset that matches the runtime, and only files that look installable on the target OS — `.exe`/`.msi`/`.msix`/`.appx`, `.dmg`/`.pkg`, `.deb`/`.rpm`/`.appimage` — or have a preferred extension are eligible, so release notes or symbol archives are never picked; assets naming another OS or architecture and checksum/signature files are still never chosen), or use `new PatternAssetSelector("*Setup*.exe")`.

### Letting the user pick a platform

`RuntimeAssetSelector` picks the asset for the *current* machine. To offer every platform instead, list the release's assets with the platform detected from each file name and let the user choose:

```csharp
var check = await updater.CheckForUpdateAsync();
foreach (var a in updater.ListAssets(check))            // checksum/signature files are left out by default
{
    Console.WriteLine($"{a.Asset.Name,-40} os={a.Platform.Os ?? "?"} arch={a.Platform.Arch ?? "?"} {(a.MatchesCurrent ? "<- this machine" : "")}");
}

var chosen = updater.ListAssets(check).First(a => a.Platform is { Os: "linux", Arch: "arm64" });
await updater.DownloadAsync(check.Release!, chosen.Asset, downloadDir);
```

- `AssetPlatformDetector.Detect(name)` returns an `AssetPlatform` (`Os`, `Arch`, `Rid`) using the same aliases as the selector (`windows`, `darwin`, `amd64`, `aarch64`, …). A part the file name does not mention is `null`; a name with neither (e.g. `app-setup.exe`) has `IsUnknown == true` and is never guessed.
- `DescribedAsset` also carries `IsMetadata` and `MatchesCurrent` (false for unknown-platform and metadata files). `AssetPlatformDetector.Describe(release, runtime)` does the same for any `RuntimeInfo`.
- To auto-pick for a specific platform rather than list, use `new RuntimeAssetSelector(new RuntimeInfo("linux", "arm64"))`.

### Launching the installer

When the release asset is an installer (an `.exe`/`.msi`), the library can start it after the download has been verified. It starts the installer through the OS shell (so an installer that requires elevation shows the normal UAC prompt) and returns immediately without waiting — the installer is expected to close and restart your app itself.

```csharp
using GitHubReleaseUpdater.Installation;

var check = await updater.CheckForUpdateAsync();
if (check.IsUpdateAvailable)
{
    // One step: download + verify + start.
    await updater.DownloadAndLaunchInstallerAsync(check, downloadDir, InstallerLaunchOptions.InnoSetupSilent, progress);

    // Or two steps, to run your own checks between downloading and starting:
    var file = await updater.DownloadAsync(check, downloadDir, progress);
    await updater.LaunchInstallerAsync(file, new InstallerLaunchOptions { Arguments = "/S", RequireVerified = true });
}
```

- `InstallerLaunchOptions`: `Arguments`, `WorkingDirectory` (defaults to the installer's directory) and `RequireVerified` (refuse to start an installer whose checksum could not be verified). `InnoSetupSilent` (`/SILENT /SP- /NORESTART`) and `InnoSetupVerySilent` are ready-made presets.
- The installer is never started when the download fails, is cancelled or fails verification. Right before starting it the file is hashed again and compared with the hash recorded at download time, so a file swapped or modified afterwards (installers usually sit in a user-writable folder and often run elevated) is never run; `ChecksumMismatchException` is thrown instead.
- `InstallerLaunchException` (derived from `UpdaterException`) carries `FilePath` and `IsUserCancelled` — true when the user declined the UAC prompt, so callers can treat that differently from a genuine failure.
- Supply `UpdaterOptions.InstallerLauncher` (an `IInstallerLauncher`) to customize or stub out how the installer is started.

### Exceptions

All derive from `UpdaterException`:

- `GitHubApiException` — carries `StatusCode`, `IsRateLimited`, `RateLimitResetAt`.
- `AssetNotFoundException` — carries `AvailableAssets`.
- `ChecksumMismatchException` — carries `Expected` / `Actual`.
- `InstallerLaunchException` — carries `FilePath` / `IsUserCancelled`.

`CheckForUpdateAsync()` never throws any of these (or any other exception) — it catches everything encountered while checking (API failures, a throwing `ILastCheckStore`, etc.) and reports it via `UpdateCheckResult.Success`/`Error` instead, so callers don't need a try/catch around it. A caller-requested cancellation still throws `OperationCanceledException` as usual. `DownloadAsync()` keeps the original throwing contract above.

### Sample CLI

```bash
dotnet run --project samples/GitHubReleaseUpdater.Cli -- check --owner cli --repo cli --current 1.0.0
```

```bash
dotnet run --project samples/GitHubReleaseUpdater.Cli -- download --owner cli --repo cli --current 1.0.0 --asset "gh_{version}_windows_amd64.zip" --require-checksum --out ./downloads
```

Other options: `--token`, `--base-url`, `--prerelease`, `--tag-prefix`, `--sha256`, `--no-verify`.

### Build and test

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet pack src/GitHubReleaseUpdater -c Release
```

Unit tests use HTTP stubs only and never touch the network.

### Project layout

```
src/GitHubReleaseUpdater/      library (NuGet package)
  Versioning/                  SemanticVersion
  GitHub/                      API client and DTOs
  Assets/                      asset selectors
  Download/                    downloader and progress
  Verification/                checksum handling
  LastCheck/                   ILastCheckStore, InMemoryLastCheckStore and JsonFileLastCheckStore
  Installation/                installer launching
  Exceptions/                  exception types
samples/GitHubReleaseUpdater.Cli/   sample command-line tool
tests/GitHubReleaseUpdater.Tests/   xUnit tests
```

### License

MIT

---

## 中文

一个零依赖的 .NET 10 类库，用于把 GitHub Releases 作为应用的更新源：**检查新版本 → 选择资产 → 带进度下载 → SHA‑256 校验 →（可选）运行安装程序**。

支持 github.com 与 GitHub Enterprise Server，支持匿名与 Token 访问（私有仓库 / 更高限流额度）。不负责解压、替换文件与重启，这些由调用方（或被启动的安装程序）决定。

### 快速开始

```csharp
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;

using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",                                     // 也可用 SemanticVersion.FromVersion(assembly.GetName().Version!)
    Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),    // 可选
    // BaseUrl = new Uri("https://ghe.example.com/api/v3/"),       // GitHub Enterprise 时设置
    AssetSelector = new PatternAssetSelector("gh_{version}_windows_amd64.zip"),
});

var check = await updater.CheckForUpdateAsync();
if (!check.Success)
{
    // CheckForUpdateAsync 不会抛出异常（OperationCanceledException 除外）——请检查 check.Error。
    Console.WriteLine($"Check failed: {check.Error}");
    return;
}

if (check.IsUpdateAvailable)
{
    // 此时 check.Update 保证非空，且 Version/Release 也保证非空——无需再做防御性判空。
    Console.WriteLine($"New version {check.Update!.Version}: {check.ReleaseNotes}");

    var progress = new Progress<DownloadProgress>(p => Console.Write($"\r{p.Percentage:0.0}%"));
    var file = await updater.DownloadAsync(check, downloadDir, progress);

    Console.WriteLine($"{file.FilePath}  sha256={file.Sha256}  verified={file.Verified}");
}
```

### 核心概念

| 类型 | 作用 |
|---|---|
| `ReleaseUpdater` | 门面。`CheckForUpdateAsync()`、`ListAssets()`、`DownloadAsync()`、`LaunchInstallerAsync()` 与 `DownloadAndLaunchInstallerAsync()`。每次检查都新建一个实例也没问题——见下方 [HttpClient 复用](#httpclient-复用)。 |
| `UpdaterOptions` | `Owner`/`Repo`/`CurrentVersion` 必填；`Token`、`BaseUrl`、`IncludePrerelease`、`TagPrefix`、`AssetSelector`、`ChecksumProvider`、`RequireChecksum`、`HttpClient`、`Timeout`、`LastCheckStore`、`MinimumCheckInterval`、`DownloadMaxRetryAttempts`、`DownloadRetryDelay`、`DownloadAllowResume`、`DownloadIdleTimeout`、`InstallerLauncher`、`TimeProvider` 可选。 |
| `UpdateCheckResult` | `CheckForUpdateAsync()` 的结果。`Success`/`Error` 表示本次检查是否在未抛出异常的情况下完成（见下方[异常](#异常)）。`LatestVersion`/`Release` 反映找到的最高版本，即使它不构成更新也会有值；`Update`（见下）是判空安全的、用来获取"可执行更新"的方式。`Throttled` 表示本次因 `LastCheckStore` 节流而跳过了 API 调用（见下）。 |
| `AvailableUpdate` | `UpdateCheckResult.Update`：当且仅当 `IsUpdateAvailable` 时非空，`Version`/`Release` **保证非空**（`SelectedAsset` 仍可能为空——没有资产匹配选择器时）。 |
| `SemanticVersion` | 精简 SemVer 2.0 实现，容忍 `v` 前缀、`1.2` 缺省 patch、自定义前缀（`TagPrefix`）。 |
| `IAssetSelector` | 从 Release 资产中选择要下载的文件：<br>`RuntimeAssetSelector`（默认，按当前 OS/架构自动匹配 `win/linux/osx × x64/x86/arm64` 及常见别名 `amd64`、`aarch64`、`darwin`、`x86_64`…）<br>`PatternAssetSelector`（通配符 + 占位符 `{os}` `{arch}` `{rid}` `{version}` `{tagversion}` `{tag}`；`{version}` 是规范化后的版本，tag `v1.2` 得到 `1.2.0`，`{tagversion}` 保留 tag 原文、只去掉前缀和 `v`，得到 `1.2`）<br>`DelegateAssetSelector`（自定义谓词） |
| `IChecksumProvider` | 提供期望的 SHA‑256：<br>`ReleaseChecksumProvider`（默认，依次查找 `<asset>.sha256` 侧车文件 → `SHA256SUMS` / `*checksums.txt` 聚合文件 → GitHub API 的 `digest` 字段）<br>`StaticChecksumProvider`（调用方指定）<br>`NoChecksumProvider`（关闭校验）<br>自定义 provider 返回的值可以是任意大小写的十六进制，可带 `sha256:` 前缀；其他格式会在传输任何字节之前以 `UpdaterException` 失败。 |
| `IGitHubReleaseClient` | GitHub API 抽象，`GitHubReleaseClient` 为 `HttpClient` 实现；可注入用于测试或自定义传输。 |
| `ILastCheckStore` | 可选的持久化接口（见下），用于节流和"跳过某版本"。`InMemoryLastCheckStore` 是进程生命周期内的实现；`JsonFileLastCheckStore(path)` 把状态存成一个小 JSON 文件，重启后依然有效（也可以基于自己的设置存储来实现）。 |

### 版本判定规则

当前版本通常取自正在运行的应用：`SemanticVersion.TryFromAssembly(typeof(App).Assembly, out var current)` 读取程序集的 `AssemblyInformationalVersion`（MinVer 等工具会把完整的 SemVer，含预发布和构建元数据，写在这里）；没有该属性时返回 `false`，而不是编造一个 `0.0.0`。`version.ToString(includeMetadata: false)` 可得到不带 `+提交号` 的显示文本。

- `IncludePrerelease = false`（默认）：调用 `/releases/latest`，GitHub 已排除 prerelease 与 draft。
- `IncludePrerelease = true`：拉取最近 `ReleaseScanCount`（默认 30）条 release，按 SemVer 排序取最高（跳过 draft 与无法解析的 tag，后者记录在 `UpdateCheckResult.SkippedTags`）。
- 仅当 `latest > current`（且未被记录为"已跳过版本"，见下）时 `IsUpdateAvailable` 为真。

### 节流与跳过版本

默认情况下库不做任何节流，每次调用 `CheckForUpdateAsync()` 都会请求 API——持久化策略完全交给调用方。若要启用，实现 `ILastCheckStore`（基于你自己的存储，比如设置文件、数据库），并设置到 `UpdaterOptions`：

```csharp
using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",
    LastCheckStore = myStore,                        // 实现 ILastCheckStore
    MinimumCheckInterval = TimeSpan.FromHours(24),    // 不设置则即使配了 store 也不节流
});

var check = await updater.CheckForUpdateAsync();
if (check.Throttled)
{
    // 距上次检查时间太短，本次未请求 API。
}
```

- `MinimumCheckInterval` 用于节流：在时间窗口内的调用会直接返回 `UpdateCheckResult.Throttled == true`，不会请求 GitHub。`UpdaterOptions.TimeProvider` 提供时钟（默认 `TimeProvider.System`），方便测试控制时间。
- 记录检查时间失败（`SetLastCheckedAtAsync` 抛异常）不会丢弃已成功的检查结果，只是下一次调用不会被节流；异常可从 `UpdateCheckResult.LastCheckStoreError` 取得，方便记录日志。
- `JsonFileLastCheckStore` 是现成的持久化实现：`LastCheckStore = new JsonFileLastCheckStore(Path.Combine(dataDir, "update-state.json"))`。写入是原子的，文件缺失或损坏时按"没有记录"处理。
- 调用 `myStore.SetSkippedVersionAsync(version)` 可以让该版本在之后的检查中不再触发 `IsUpdateAvailable`/`Update`（但仍会出现在 `LatestVersion` 里，所以你依然可以提示"有新版本但已被跳过"）。调用 `myStore.ClearSkippedVersionAsync()` 可以撤销这个跳过。
- 如果是用户主动点击的"立即检查"，希望仍然能看到之前被跳过的版本，可以调用 `CheckForUpdateAsync(bypassSkippedVersion: true)`：节流判断和检查时间的记录照常进行，只是这一次跳过版本过滤不生效。
- 如果用户主动点击的"立即检查"还希望绕过 `MinimumCheckInterval` 节流，可以调用 `CheckForUpdateAsync(bypassThrottle: true)`。这样自动检查（要节流）和手动检查（不要节流）可以共用同一个 `ReleaseUpdater`/`UpdaterOptions` 实例，不必为了关闭节流单独再构造一个 updater。

> **破坏性变更：** `CheckForUpdateAsync` 现在把 `bypassSkippedVersion` 和 `bypassThrottle` 放在 `cancellationToken` 之前。原来按位置传参的 `CheckForUpdateAsync(cts.Token)` 将无法编译，需要改成 `CheckForUpdateAsync(cancellationToken: cts.Token)`。
>
> **破坏性变更：** `ILastCheckStore.SetSkippedVersionAsync` 现在接受非空的 `SemanticVersion`。自定义 `ILastCheckStore` 实现需要直接实现 `ClearSkippedVersionAsync`，不能再依赖旧的 `SetSkippedVersionAsync(null)` 默认实现；原本通过传 `null` 来清除跳过版本的调用方需要改为调用 `ClearSkippedVersionAsync()`。

### 自动更新流程（`GitHubReleaseUpdater.AutoUpdate` 命名空间）

`UpdateController` 是一个与 UI 无关的状态机，实现常见的更新流程——后台检查（首次 30 秒后，之后每 24 小时）、同一版本只通知一次、安装前询问、下载、启动安装程序。你来实现 `IUpdatePresenter`（托盘气泡、消息框、关闭应用等），每个成员都有什么都不做的默认实现（`ConfirmInstallAsync` 默认回答“否”，所以在你询问用户之前不会安装任何东西）。它就在本包里，命名空间为 `GitHubReleaseUpdater.AutoUpdate`，桌面、托盘、控制台和服务类应用都可以使用。

```csharp
var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "me", Repo = "app", CurrentVersion = current,
    LastCheckStore = new JsonFileLastCheckStore(statePath),
    MinimumCheckInterval = TimeSpan.FromHours(6),
});
var controller = new UpdateController(updater, new MyPresenter(), new UpdateControllerOptions
{
    IsAutoCheckEnabled = () => settings.CheckForUpdates,
    InstallerLaunchOptions = InstallerLaunchOptions.InnoSetupSilent,
});
_ = controller.RunAsync(shutdownToken);       // 后台循环
await controller.CheckAsync(manual: true);    // “检查更新”按钮：绕过节流和已跳过的版本
await controller.InstallAsync();              // “更新到 x.y.z”
```

- **状态：** `State` 为 `Idle`、`Checking`、`UpToDate`、`Available`、`Failed` 或 `Installing`；`Available` 是找到的版本；每次变化都会触发 `Changed`。
- **自动检查**同一版本只通知一次；**手动检查**绕过节流和已跳过的版本，会报告“已是最新”或失败，发现新版本后直接进入确认。
- 自动检查运行期间收到的手动检查，会在自动检查结束后立刻执行：调用会立即返回，多个这样的请求合并成一次检查，这次检查使用当时正在运行的那个调用的取消令牌（取消它，比如在关闭应用时，排队的检查就被放弃）。因为它运行在那个调用之内，手动调用方传入的令牌既取消不了这次检查也取消不了它的下载，展示层在正在运行的那个调用的上下文里被调用，`RunAsync` 会等它结束（包括一直不关的确认对话框）才进入下一轮。已有安装在运行时，再次调用什么都不做。
- `SkipVersionAsync()` 把找到的版本记为“已跳过”，写入更新器的 `LastCheckStore`（之后自动检查不再提示它，手动检查仍会提供；`State` 变为 `UpToDate`）；没有可跳过的版本或没有 store 时返回 `false`。
- 安装程序下载到系统临时目录下的文件夹（用 `DownloadDirectory` 修改，例如临时目录被多个用户共用时改成当前用户专属的目录），启动后不会删除。`InitialDelay` 和 `CheckInterval` 不能超过约 49 天。
- **默认要求校验通过**（`RequireVerifiedDownload`）：校验和无法验证的安装程序（没有 sidecar、没有 sums 文件、GitHub 也没有给出 digest）不会被启动，失败交给 `ReportInstallFailed`。只有发布确实无法校验时才把它设为 false。
- 展示层、`Changed` 处理器、`IsAutoCheckEnabled` 抛出的异常不会让控制器卡住，它们和记录检查时间失败一起交给 `UpdateControllerOptions.OnError`。
- 请在同一线程（通常是 UI 线程）调用；`Changed` 和展示层运行在调用方的上下文里。

### HttpClient 复用

当 `UpdaterOptions.HttpClient` 留空时，所有以这种方式创建的 `GitHubReleaseClient`/`ReleaseUpdater` 会在内部共享同一个进程级 `HttpClient`——每次检查都新建一个 `ReleaseUpdater` 不会重复创建连接池。如果你自己传入了 `HttpClient`（比如来自 `IHttpClientFactory`），则遵循通常的建议：复用它，而不是每次检查都新建一个。

### 超时

`UpdaterOptions.Timeout` 限制每次 GitHub API 调用与资产下载的耗时，默认 **10 秒**。它叠加在底层 `HttpClient` 自身的超时之上（两者相互独立），实际生效的是两者中较短的那个。设为 `null` 则完全依赖 `HttpClient` 自身的超时（默认 100 秒）。

```csharp
using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",
    Timeout = TimeSpan.FromSeconds(5),   // 设为 null 可关闭，改用 HttpClient.Timeout
});
```

超时触发时抛出的是 `TimeoutException`（而非 `OperationCanceledException`），因此可以和调用方主动取消区分开。对于资产下载，它只限制"收到响应头"的时间，不限制整个传输过程；传输中途卡住的响应体由 `DownloadIdleTimeout` 负责（见下文）。

取值必须在 1 毫秒到约 49.7 天之间，或为 `Timeout.InfiniteTimeSpan`；其他值（零、负数、不足 1 毫秒、或超过上限）会让 `ReleaseUpdater` / `GitHubReleaseClient` 的构造函数抛出 `ArgumentOutOfRangeException`。

### 下载重试与断点续传

`DownloadAsync()` 遇到瞬时故障（网络 I/O 错误、请求超时、响应体被截断、响应体卡住不动）时会按指数退避自动重试，而不是一次失败就直接抛出：

```csharp
using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",
    DownloadMaxRetryAttempts = 2,                     // 默认值；设为 0 关闭重试
    DownloadRetryDelay = TimeSpan.FromSeconds(1),      // 每次重试翻倍：1s、2s……
    DownloadIdleTimeout = TimeSpan.FromSeconds(30),    // 默认值；设为 null 则无限等待
});
```

`DownloadIdleTimeout` 是下载在收不到任何数据的情况下最多等待的时长；每收到一块数据计时就重新开始，所以它限制的是空闲时间，而不是总传输时间。超过该时长会让本次尝试以 `TimeoutException` 失败，并像其他瞬时故障一样重试和续传。

校验和不匹配、本地磁盘错误（磁盘满、文件被占用）、参数错误、调用方主动取消——这几种情况都不会重试。

当下载被中断时——无论是即将重试，还是进程被直接杀死、之后重新调用 `DownloadAsync()`——只要服务器支持（`GitHubReleaseClient` 支持；自定义 `IGitHubReleaseClient` 需要重写支持 Range 的 `OpenAssetStreamAsync` 重载才能启用），下一次尝试都会通过 HTTP Range 请求从断点续传，而不是从头开始。一个小的 `<name>.partial.meta` 侧车文件记录着这个 `.partial` 文件属于哪个资产，因此不会把不相关或过期的部分下载盲目地续到新文件后面——遇到这种情况会直接丢弃并从 0 重新下载。将 `DownloadAllowResume` 设为 `false` 可以始终从 0 开始，且失败时不残留任何部分文件，行为与加入续传支持之前一致。

### 下载与校验

- 流式写入 `<name>.partial`，完成后原子重命名。`DownloadAllowResume = true`（默认）时，失败的下载会保留部分文件（及其 `.meta` 侧车文件），以便之后的尝试续传——只有调用方主动取消才会始终清理掉它。`DownloadAllowResume = false` 时，任何失败或取消都不会残留部分文件。
- 服务器报告了 Content-Length 但字节数不符时抛 `UpdaterException`。
- 能解析到期望哈希时进行校验，校验在文件移动到目标位置之前进行，不匹配则丢弃本次下载（目标位置已有的文件保持不变）并抛 `ChecksumMismatchException`，其 `FilePath` 为被丢弃的 `.partial` 路径；无法解析到哈希时 `DownloadResult.Verified = false`（设置 `RequireChecksum = true` 可改为直接失败，且在传输前就会失败）。
- 私有仓库资产通过 API 端点 + `Accept: application/octet-stream` 下载，只要提供 Token 即可。

### 只发布一个不带平台名的安装程序

`RuntimeAssetSelector` 从不"猜"：像 `MyApp-Setup-1.2.3.exe` 这种既不含系统也不含架构的文件名不会被选中，`SelectedAsset` 为 null。如果某个 Release 只为一个平台发布一个安装程序，可以用 `new RuntimeAssetSelector { AllowUnknownPlatform = true }` 主动开启回退（不带平台名的资产排在任何匹配当前运行环境的资产之后，而且只有在目标系统上看起来可安装的文件——`.exe`/`.msi`/`.msix`/`.appx`、`.dmg`/`.pkg`、`.deb`/`.rpm`/`.appimage`——或带有首选扩展名的文件才会被考虑，发布说明、符号包不会被选中；点名了其他系统或架构的资产、校验和/签名文件仍然不会被选中），或者用 `new PatternAssetSelector("*Setup*.exe")`。

### 让用户选择平台

`RuntimeAssetSelector` 只会为*当前机器*挑选资产。如果想列出所有平台让用户自己选，可以列出 Release 的资产及从文件名识别出的平台：

```csharp
var check = await updater.CheckForUpdateAsync();
foreach (var a in updater.ListAssets(check))            // 默认不含校验和/签名文件
{
    Console.WriteLine($"{a.Asset.Name,-40} os={a.Platform.Os ?? "?"} arch={a.Platform.Arch ?? "?"} {(a.MatchesCurrent ? "<- 当前机器" : "")}");
}

var chosen = updater.ListAssets(check).First(a => a.Platform is { Os: "linux", Arch: "arm64" });
await updater.DownloadAsync(check.Release!, chosen.Asset, downloadDir);
```

- `AssetPlatformDetector.Detect(name)` 返回 `AssetPlatform`（`Os`、`Arch`、`Rid`），使用与选择器相同的别名（`windows`、`darwin`、`amd64`、`aarch64`…）。文件名没提到的部分为 `null`；两者都没有的（如 `app-setup.exe`）`IsUnknown == true`，不会被猜测归类。
- `DescribedAsset` 还带有 `IsMetadata` 与 `MatchesCurrent`（未知平台和元数据文件为 false）。`AssetPlatformDetector.Describe(release, runtime)` 可针对任意 `RuntimeInfo`。
- 若只想为指定平台自动挑选而非列出，可用 `new RuntimeAssetSelector(new RuntimeInfo("linux", "arm64"))`。

### 运行安装程序

当 Release 资产是安装程序（`.exe`/`.msi`）时，库可以在下载并校验通过后启动它。通过系统 Shell 启动（需要提权的安装程序会弹出正常的 UAC 提示），启动后立即返回、不等待——由安装程序自己负责关闭并重启你的应用。

```csharp
using GitHubReleaseUpdater.Installation;

var check = await updater.CheckForUpdateAsync();
if (check.IsUpdateAvailable)
{
    // 一步完成：下载 + 校验 + 启动。
    await updater.DownloadAndLaunchInstallerAsync(check, downloadDir, InstallerLaunchOptions.InnoSetupSilent, progress);

    // 或分两步，在下载与启动之间做自己的检查：
    var file = await updater.DownloadAsync(check, downloadDir, progress);
    await updater.LaunchInstallerAsync(file, new InstallerLaunchOptions { Arguments = "/S", RequireVerified = true });
}
```

- `InstallerLaunchOptions`：`Arguments`、`WorkingDirectory`（默认为安装程序所在目录）、`RequireVerified`（拒绝启动无法校验校验和的安装程序）。`InnoSetupSilent`（`/SILENT /SP- /NORESTART`）与 `InnoSetupVerySilent` 为现成预设。
- 下载失败、被取消或校验失败时，永远不会启动安装程序。启动前会重新计算文件哈希并与下载时记录的值比对，下载之后被替换或改动的文件（安装包通常位于用户可写目录，且常以管理员权限运行）不会被运行，会抛 `ChecksumMismatchException`。
- `InstallerLaunchException`（派生自 `UpdaterException`）带 `FilePath` 与 `IsUserCancelled`——用户拒绝 UAC 提示时为 true，便于调用方与真正的失败区别对待。
- 通过 `UpdaterOptions.InstallerLauncher`（`IInstallerLauncher`）可自定义或在测试中替换启动方式。

### 异常

均派生自 `UpdaterException`：

- `GitHubApiException` — 带 `StatusCode`、`IsRateLimited`、`RateLimitResetAt`。
- `AssetNotFoundException` — 带 `AvailableAssets`。
- `ChecksumMismatchException` — 带 `Expected` / `Actual`。
- `InstallerLaunchException` — 带 `FilePath` / `IsUserCancelled`。

`CheckForUpdateAsync()` 不会抛出上述任何异常（也不会抛出其他任何异常）——检查过程中遇到的所有异常（API 失败、抛异常的 `ILastCheckStore` 等）都会被捕获，并通过 `UpdateCheckResult.Success`/`Error` 返回，调用方不需要为它加 try/catch。调用方主动取消时仍会照常抛出 `OperationCanceledException`。`DownloadAsync()` 的抛异常约定保持不变，见上文。

### 示例 CLI

```bash
dotnet run --project samples/GitHubReleaseUpdater.Cli -- check --owner cli --repo cli --current 1.0.0
```

```bash
dotnet run --project samples/GitHubReleaseUpdater.Cli -- download --owner cli --repo cli --current 1.0.0 --asset "gh_{version}_windows_amd64.zip" --require-checksum --out ./downloads
```

其他参数：`--token`、`--base-url`、`--prerelease`、`--tag-prefix`、`--sha256`、`--no-verify`。

### 构建与测试

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet pack src/GitHubReleaseUpdater -c Release
```

单元测试全部使用 HTTP 桩，不访问网络。

### 项目结构

```
src/GitHubReleaseUpdater/      类库（NuGet 包）
  Versioning/                  SemanticVersion
  GitHub/                      API 客户端与 DTO
  Assets/                      资产选择器
  Download/                    下载器与进度
  Verification/                校验
  LastCheck/                   ILastCheckStore、InMemoryLastCheckStore 与 JsonFileLastCheckStore
  Installation/                启动安装程序
  Exceptions/                  异常
samples/GitHubReleaseUpdater.Cli/   示例命令行
tests/GitHubReleaseUpdater.Tests/   xUnit 测试
```

### 许可证

MIT
