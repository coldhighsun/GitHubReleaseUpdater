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

A dependency-free .NET 10 library that turns GitHub Releases into an update source for your application: **check for a newer version → pick an asset → download with progress → verify SHA‑256**.

Works with github.com and GitHub Enterprise Server, anonymously or with a token (private repositories / higher rate limits). It deliberately does **not** extract, replace files or restart the app — that part is up to the caller.

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
| `ReleaseUpdater` | Facade: `CheckForUpdateAsync()` and `DownloadAsync()`. Constructing a new instance per check is fine — see [HttpClient reuse](#httpclient-reuse) below. |
| `UpdaterOptions` | `Owner`/`Repo`/`CurrentVersion` are required; `Token`, `BaseUrl`, `IncludePrerelease`, `TagPrefix`, `AssetSelector`, `ChecksumProvider`, `RequireChecksum`, `HttpClient`, `LastCheckStore`, `MinimumCheckInterval` are optional. |
| `UpdateCheckResult` | Outcome of `CheckForUpdateAsync()`. `LatestVersion`/`Release` report the highest release found even when it isn't an update; `Update` (see below) is the null-safe way to get an actionable one. `Throttled` is true when a `LastCheckStore` skipped the API call (see below). |
| `AvailableUpdate` | `UpdateCheckResult.Update`: non-null exactly when `IsUpdateAvailable`, with **non-nullable** `Version`/`Release` (`SelectedAsset` is still nullable — null when no asset matched the selector). |
| `SemanticVersion` | Minimal SemVer 2.0 implementation. Tolerates a `v` prefix, a missing patch (`1.2`) and a custom prefix (`TagPrefix`). |
| `IAssetSelector` | Chooses which release asset to download:<br>`RuntimeAssetSelector` (default — matches the current OS/arch: `win/linux/osx × x64/x86/arm64`, including aliases such as `amd64`, `aarch64`, `darwin`, `x86_64`)<br>`PatternAssetSelector` (wildcards plus `{os}` `{arch}` `{rid}` `{version}` `{tag}` placeholders)<br>`DelegateAssetSelector` (custom predicate) |
| `IChecksumProvider` | Supplies the expected SHA‑256:<br>`ReleaseChecksumProvider` (default — looks for a `<asset>.sha256` sidecar → a `SHA256SUMS` / `*checksums.txt` aggregate file → the GitHub API `digest` field)<br>`StaticChecksumProvider` (caller-supplied)<br>`NoChecksumProvider` (disables verification) |
| `IGitHubReleaseClient` | GitHub API abstraction; `GitHubReleaseClient` is the `HttpClient` implementation. Inject your own for tests or custom transports. |
| `ILastCheckStore` | Optional persistence hook (see below) for throttling and skipped-version tracking. `InMemoryLastCheckStore` is a process-lifetime implementation; production apps implement it over their own settings storage. |

### Version resolution

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

- `MinimumCheckInterval` throttles: a call inside the window returns `UpdateCheckResult.Throttled == true` without contacting GitHub.
- `myStore.SetSkippedVersionAsync(version)` suppresses `IsUpdateAvailable`/`Update` for that version on future checks (it still shows up in `LatestVersion`, so you can still display "a newer version exists but was skipped").

### HttpClient reuse

When `UpdaterOptions.HttpClient` is left null, every `GitHubReleaseClient`/`ReleaseUpdater` created that way shares one process-wide `HttpClient` internally — constructing a new `ReleaseUpdater` per check does not create a new connection pool each time. If you supply your own `HttpClient` (e.g. from `IHttpClientFactory`), the usual guidance applies: reuse it rather than creating one per check.

### Download and verification

- Streams to `<name>.partial`, then renames atomically. No partial file is left behind on failure or cancellation.
- Throws `UpdaterException` if the server reported a Content-Length that does not match the bytes received.
- When an expected hash can be resolved the file is verified; on mismatch it is deleted and `ChecksumMismatchException` is thrown. When no hash is available `DownloadResult.Verified` is `false` (set `RequireChecksum = true` to fail instead — it fails before any bytes are transferred).
- Private repository assets are downloaded via the API endpoint with `Accept: application/octet-stream`; just supply a token.

### Exceptions

All derive from `UpdaterException`:

- `GitHubApiException` — carries `StatusCode`, `IsRateLimited`, `RateLimitResetAt`.
- `AssetNotFoundException` — carries `AvailableAssets`.
- `ChecksumMismatchException` — carries `Expected` / `Actual`.

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
  LastCheck/                   ILastCheckStore and InMemoryLastCheckStore
  Exceptions/                  exception types
samples/GitHubReleaseUpdater.Cli/   sample command-line tool
tests/GitHubReleaseUpdater.Tests/   xUnit tests
```

### License

MIT

---

## 中文

一个零依赖的 .NET 10 类库，用于把 GitHub Releases 作为应用的更新源：**检查新版本 → 选择资产 → 带进度下载 → SHA‑256 校验**。

支持 github.com 与 GitHub Enterprise Server，支持匿名与 Token 访问（私有仓库 / 更高限流额度）。不负责解压、替换文件与重启，这些由调用方决定。

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
| `ReleaseUpdater` | 门面。`CheckForUpdateAsync()` 与 `DownloadAsync()`。每次检查都新建一个实例也没问题——见下方 [HttpClient 复用](#httpclient-复用)。 |
| `UpdaterOptions` | `Owner`/`Repo`/`CurrentVersion` 必填；`Token`、`BaseUrl`、`IncludePrerelease`、`TagPrefix`、`AssetSelector`、`ChecksumProvider`、`RequireChecksum`、`HttpClient`、`LastCheckStore`、`MinimumCheckInterval` 可选。 |
| `UpdateCheckResult` | `CheckForUpdateAsync()` 的结果。`LatestVersion`/`Release` 反映找到的最高版本，即使它不构成更新也会有值；`Update`（见下）是判空安全的、用来获取"可执行更新"的方式。`Throttled` 表示本次因 `LastCheckStore` 节流而跳过了 API 调用（见下）。 |
| `AvailableUpdate` | `UpdateCheckResult.Update`：当且仅当 `IsUpdateAvailable` 时非空，`Version`/`Release` **保证非空**（`SelectedAsset` 仍可能为空——没有资产匹配选择器时）。 |
| `SemanticVersion` | 精简 SemVer 2.0 实现，容忍 `v` 前缀、`1.2` 缺省 patch、自定义前缀（`TagPrefix`）。 |
| `IAssetSelector` | 从 Release 资产中选择要下载的文件：<br>`RuntimeAssetSelector`（默认，按当前 OS/架构自动匹配 `win/linux/osx × x64/x86/arm64` 及常见别名 `amd64`、`aarch64`、`darwin`、`x86_64`…）<br>`PatternAssetSelector`（通配符 + 占位符 `{os}` `{arch}` `{rid}` `{version}` `{tag}`）<br>`DelegateAssetSelector`（自定义谓词） |
| `IChecksumProvider` | 提供期望的 SHA‑256：<br>`ReleaseChecksumProvider`（默认，依次查找 `<asset>.sha256` 侧车文件 → `SHA256SUMS` / `*checksums.txt` 聚合文件 → GitHub API 的 `digest` 字段）<br>`StaticChecksumProvider`（调用方指定）<br>`NoChecksumProvider`（关闭校验） |
| `IGitHubReleaseClient` | GitHub API 抽象，`GitHubReleaseClient` 为 `HttpClient` 实现；可注入用于测试或自定义传输。 |
| `ILastCheckStore` | 可选的持久化接口（见下），用于节流和"跳过某版本"。`InMemoryLastCheckStore` 是进程生命周期内的实现；生产环境应基于自己的设置存储来实现它。 |

### 版本判定规则

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

- `MinimumCheckInterval` 用于节流：在时间窗口内的调用会直接返回 `UpdateCheckResult.Throttled == true`，不会请求 GitHub。
- 调用 `myStore.SetSkippedVersionAsync(version)` 可以让该版本在之后的检查中不再触发 `IsUpdateAvailable`/`Update`（但仍会出现在 `LatestVersion` 里，所以你依然可以提示"有新版本但已被跳过"）。

### HttpClient 复用

当 `UpdaterOptions.HttpClient` 留空时，所有以这种方式创建的 `GitHubReleaseClient`/`ReleaseUpdater` 会在内部共享同一个进程级 `HttpClient`——每次检查都新建一个 `ReleaseUpdater` 不会重复创建连接池。如果你自己传入了 `HttpClient`（比如来自 `IHttpClientFactory`），则遵循通常的建议：复用它，而不是每次检查都新建一个。

### 下载与校验

- 流式写入 `<name>.partial`，完成后原子重命名；失败或取消时不残留部分文件。
- 服务器报告了 Content-Length 但字节数不符时抛 `UpdaterException`。
- 能解析到期望哈希时进行校验，不匹配则删除文件并抛 `ChecksumMismatchException`；无法解析到哈希时 `DownloadResult.Verified = false`（设置 `RequireChecksum = true` 可改为直接失败，且在传输前就会失败）。
- 私有仓库资产通过 API 端点 + `Accept: application/octet-stream` 下载，只要提供 Token 即可。

### 异常

均派生自 `UpdaterException`：

- `GitHubApiException` — 带 `StatusCode`、`IsRateLimited`、`RateLimitResetAt`。
- `AssetNotFoundException` — 带 `AvailableAssets`。
- `ChecksumMismatchException` — 带 `Expected` / `Actual`。

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
  LastCheck/                   ILastCheckStore 与 InMemoryLastCheckStore
  Exceptions/                  异常
samples/GitHubReleaseUpdater.Cli/   示例命令行
tests/GitHubReleaseUpdater.Tests/   xUnit 测试
```

### 许可证

MIT
