# Changelog

All notable changes to this package are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/),
and versions follow [Semantic Versioning](https://semver.org/) (before 1.0, a minor version may contain breaking changes;
they are listed under **Changed**). Releases are tagged `vX.Y.Z`; the notes of each are also on the GitHub releases page.

## Unreleased

### Added

- `UpdaterOptions.AssetUrlRewriter` (and `GitHubReleaseClient.AssetUrlRewriter`): download the package from another
  address, such as a mirror, for networks where GitHub's download servers are slow or blocked. The token is not sent
  there (only when the checksum is known), checksums still come from GitHub, and a mirror that cannot be opened or delivers something unusable
  (wrong size, failed checksum) makes the package be downloaded from GitHub itself once.
- `UpdateControllerOptions.AskToInstallAfterManualCheck` (default `true`): turn it off so that a manual check only finds
  out and the application decides when to install.
- `UpdateControllerOptions.SkipChecksWhileUpdateKnown` (default `false`): no background check while a newer version is known.
- `UpdateController.LastError`, `UpdateController.Progress` and `UpdateController.ProgressChanged`.
- Package API compatibility validation against the last release when packing, and a Windows job in CI.
- `AssetDownloader.CheckpointInterval` (default 2 seconds): how often a resumable download records its checkpoint,
  apart from `ProgressInterval`.

### Changed

- `UpdateController.RunAsync` also tries to delete leftover downloads in every round (with
  `CleanDownloadDirectory`), also when the checks are throttled or switched off, so that a setup program that was still
  running at the first check is cleaned up later.
- When `UpdaterOptions.MinimumCheckInterval` is not longer than `UpdateControllerOptions.CheckInterval`, only the first
  round of `RunAsync` is throttled by the updater. Before, a check interval equal to the minimum interval skipped every
  other round, because the last check is recorded when it ends.
- `UpdateController` is split into partial files (no change of behavior).
- Resume checkpoints use SHA-256 instead of MD5 (MD5 throws where FIPS mode is enforced); partial files left by an
  earlier version are simply downloaded again. Checkpoints are written at most every `CheckpointInterval` instead of
  every `ProgressInterval`.
- File names of assets are made safer on Windows only: reserved device names (`CON`, `NUL`, ...) get a `_` prefix and
  trailing dots and spaces are dropped. Other systems keep such names.
- `ReleaseUpdater.DownloadAsync(UpdateCheckResult, ...)` reports "no update" and "no asset" through the returned task
  instead of throwing before returning it. Invalid arguments (a null or empty argument) of both `DownloadAsync` overloads
  are thrown at once, where the overload taking a release and an asset used to report them through the returned task.

### Fixed

- `RuntimeAssetSelector` no longer picks an installer of another OS that names only the architecture (for example
  `App-x64.msi` on Linux).
- `ReleaseChecksumProvider` falls back to the digest GitHub reports when a checksum file cannot be read, instead of failing
  the download; without such a digest the failure still stops the download, so it is never left unverified by accident.
  Checksum files larger than 16 MiB are not loaded (that also fails the download when there is no digest). All checksum
  files are tried before that, so one unusable file does not hide the hash in another.
- A failed download records what had arrived as a last resume checkpoint, so a retry does not repeat the data received since
  the previous one.
- `UpdateController` with `CleanDownloadDirectory` keeps the files of the package under the name the downloader saves it as.

### Fixed

- Progress reports of an earlier download can no longer show up in the next one, and `Progress` is read as a whole.
- A check that is throttled or cancelled keeps `LastError` of the state it restores.

## 0.5.0 - 2026-10-26

### Added

- The `GitHubReleaseUpdater.AutoUpdate` namespace with `UpdateController`, `IUpdatePresenter`, `UpdateControllerOptions`
  and `UpdateState`: check in the background, announce a version once, ask before installing, download, verify and start
  the installer.
- `UpdateController.DownloadAsync` (download ahead, states `Downloading` and `Downloaded`), `BeforeInstallAsync`,
  `WatchInstaller` / `InstallerGracePeriod` (offer the package again when the installer ended and the app still runs),
  `CleanDownloadDirectory`, `LaunchInstaller` (download only, for archives) and `IUpdatePresenter.InstallerEnded` /
  `PackageDownloaded`.
- `JsonFileLastCheckStore`, `IPendingUpdateStore` (a pending update is not throttled and survives a restart) and
  retries of the store when an antivirus scanner briefly holds the file.
- `SemanticVersion.TryFromAssembly` and `ToString(includeMetadata)`, `UpdaterOptions.TimeProvider`,
  `RuntimeAssetSelector.AllowUnknownPlatform`, `InstallerLaunchOptions.WithArguments` and `GitHubAsset.IsSameUpload`.

### Changed

- **A release newer than the running version but without a package the selector accepts** leaves the controller in
  `Available` with `HasInstaller == false` instead of `Failed`; a manual check still reports `AssetNotFoundException`
  to `ReportCheckFailed`, and `DownloadAsync` / `InstallAsync` report it to `ReportInstallFailed`.
- `UpdateState` has two more values, `Downloading` and `Downloaded`; code that switches over all values needs two more cases.
- The separate `GitHubReleaseUpdater.AutoUpdate` package was merged into this one.

## 0.4.0 - 2026-10-02 and earlier

See the [GitHub releases](https://github.com/coldhighsun/GitHubReleaseUpdater/releases).
