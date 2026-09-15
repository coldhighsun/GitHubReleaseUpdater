# GitHubReleaseUpdater

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
    Console.WriteLine($"New version {check.LatestVersion}: {check.ReleaseNotes}");

    var progress = new Progress<DownloadProgress>(p => Console.Write($"\r{p.Percentage:0.0}%"));
    var file = await updater.DownloadAsync(check, downloadDir, progress);

    Console.WriteLine($"{file.FilePath}  sha256={file.Sha256}  verified={file.Verified}");
}
```

### Core concepts

| Type | Purpose |
|---|---|
| `ReleaseUpdater` | Facade: `CheckForUpdateAsync()` and `DownloadAsync()`. |
| `UpdaterOptions` | `Owner`/`Repo`/`CurrentVersion` are required; `Token`, `BaseUrl`, `IncludePrerelease`, `TagPrefix`, `AssetSelector`, `ChecksumProvider`, `RequireChecksum`, `HttpClient` are optional. |
| `SemanticVersion` | Minimal SemVer 2.0 implementation. Tolerates a `v` prefix, a missing patch (`1.2`) and a custom prefix (`TagPrefix`). |
| `IAssetSelector` | Chooses which release asset to download:<br>`RuntimeAssetSelector` (default — matches the current OS/arch: `win/linux/osx × x64/x86/arm64`, including aliases such as `amd64`, `aarch64`, `darwin`, `x86_64`)<br>`PatternAssetSelector` (wildcards plus `{os}` `{arch}` `{rid}` `{version}` `{tag}` placeholders)<br>`DelegateAssetSelector` (custom predicate) |
| `IChecksumProvider` | Supplies the expected SHA‑256:<br>`ReleaseChecksumProvider` (default — looks for a `<asset>.sha256` sidecar → a `SHA256SUMS` / `*checksums.txt` aggregate file → the GitHub API `digest` field)<br>`StaticChecksumProvider` (caller-supplied)<br>`NoChecksumProvider` (disables verification) |
| `IGitHubReleaseClient` | GitHub API abstraction; `GitHubReleaseClient` is the `HttpClient` implementation. Inject your own for tests or custom transports. |

### Version resolution

- `IncludePrerelease = false` (default): calls `/releases/latest`; GitHub already excludes pre-releases and drafts.
- `IncludePrerelease = true`: fetches the most recent `ReleaseScanCount` releases (default 30) and takes the highest SemVer. Drafts and unparseable tags are skipped; the latter are reported in `UpdateCheckResult.SkippedTags`.
- `IsUpdateAvailable` is true only when `latest > current`.

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
    Console.WriteLine($"New version {check.LatestVersion}: {check.ReleaseNotes}");

    var progress = new Progress<DownloadProgress>(p => Console.Write($"\r{p.Percentage:0.0}%"));
    var file = await updater.DownloadAsync(check, downloadDir, progress);

    Console.WriteLine($"{file.FilePath}  sha256={file.Sha256}  verified={file.Verified}");
}
```

### 核心概念

| 类型 | 作用 |
|---|---|
| `ReleaseUpdater` | 门面。`CheckForUpdateAsync()` 与 `DownloadAsync()`。 |
| `UpdaterOptions` | `Owner`/`Repo`/`CurrentVersion` 必填；`Token`、`BaseUrl`、`IncludePrerelease`、`TagPrefix`、`AssetSelector`、`ChecksumProvider`、`RequireChecksum`、`HttpClient` 可选。 |
| `SemanticVersion` | 精简 SemVer 2.0 实现，容忍 `v` 前缀、`1.2` 缺省 patch、自定义前缀（`TagPrefix`）。 |
| `IAssetSelector` | 从 Release 资产中选择要下载的文件：<br>`RuntimeAssetSelector`（默认，按当前 OS/架构自动匹配 `win/linux/osx × x64/x86/arm64` 及常见别名 `amd64`、`aarch64`、`darwin`、`x86_64`…）<br>`PatternAssetSelector`（通配符 + 占位符 `{os}` `{arch}` `{rid}` `{version}` `{tag}`）<br>`DelegateAssetSelector`（自定义谓词） |
| `IChecksumProvider` | 提供期望的 SHA‑256：<br>`ReleaseChecksumProvider`（默认，依次查找 `<asset>.sha256` 侧车文件 → `SHA256SUMS` / `*checksums.txt` 聚合文件 → GitHub API 的 `digest` 字段）<br>`StaticChecksumProvider`（调用方指定）<br>`NoChecksumProvider`（关闭校验） |
| `IGitHubReleaseClient` | GitHub API 抽象，`GitHubReleaseClient` 为 `HttpClient` 实现；可注入用于测试或自定义传输。 |

### 版本判定规则

- `IncludePrerelease = false`（默认）：调用 `/releases/latest`，GitHub 已排除 prerelease 与 draft。
- `IncludePrerelease = true`：拉取最近 `ReleaseScanCount`（默认 30）条 release，按 SemVer 排序取最高（跳过 draft 与无法解析的 tag，后者记录在 `UpdateCheckResult.SkippedTags`）。
- 仅当 `latest > current` 时 `IsUpdateAvailable` 为真。

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
  Exceptions/                  异常
samples/GitHubReleaseUpdater.Cli/   示例命令行
tests/GitHubReleaseUpdater.Tests/   xUnit 测试
```

### 许可证

MIT
