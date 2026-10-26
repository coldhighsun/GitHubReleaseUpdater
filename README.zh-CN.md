# GitHubReleaseUpdater

[![.NET](https://github.com/coldhighsun/GitHubReleaseUpdater/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/GitHubReleaseUpdater/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/coldhighsun/GitHubReleaseUpdater/branch/main/graph/badge.svg)](https://codecov.io/gh/coldhighsun/GitHubReleaseUpdater)
[![NuGet](https://img.shields.io/nuget/v/GitHubReleaseUpdater.svg)](https://www.nuget.org/packages/GitHubReleaseUpdater)
[![Pre-release](https://img.shields.io/nuget/vpre/GitHubReleaseUpdater.svg?label=pre-release)](https://www.nuget.org/packages/GitHubReleaseUpdater/absoluteLatest)
[![NuGet Downloads](https://img.shields.io/nuget/dt/GitHubReleaseUpdater.svg)](https://www.nuget.org/packages/GitHubReleaseUpdater)
[![.NET Version](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

[English](README.md) | 中文

一个零依赖的 .NET 10 类库，把 GitHub Releases 作为应用的更新源：**检查新版本 → 选择资产 → 带进度下载 → SHA‑256 校验 →（可选）运行安装程序**。

支持 github.com 与 GitHub Enterprise Server，支持匿名与 Token 访问（私有仓库 / 更高限流额度）。不负责解压、替换文件与重启，这些由调用方（或被启动的安装程序）决定。

发布说明与破坏性变更见 [CHANGELOG.md](CHANGELOG.md)。

## 快速开始

```csharp
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;

using var updater = new ReleaseUpdater(new UpdaterOptions
{
    Owner = "cli",
    Repo = "cli",
    CurrentVersion = "2.90.0",
    Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),    // 可选
    // BaseUrl = new Uri("https://ghe.example.com/api/v3/"),       // GitHub Enterprise 时设置
    AssetSelector = new PatternAssetSelector("gh_{version}_windows_amd64.zip"),
});

var check = await updater.CheckForUpdateAsync();   // 不会抛出异常（取消除外）
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

当前版本通常取自正在运行的应用：`SemanticVersion.TryFromAssembly(typeof(App).Assembly, out var current)` 读取 `AssemblyInformationalVersion`。

## 核心概念

| 类型 | 作用 |
|---|---|
| `ReleaseUpdater` | 门面：`CheckForUpdateAsync()`、`ListAssets()`、`DownloadAsync()`、`LaunchInstallerAsync()`、`DownloadAndLaunchInstallerAsync()`。 |
| `UpdaterOptions` | `Owner`/`Repo`/`CurrentVersion` 必填。可选：`Token`、`BaseUrl`、`IncludePrerelease`、`TagPrefix`、`AssetSelector`、`ChecksumProvider`、`RequireChecksum`、`HttpClient`、`Timeout`、`LastCheckStore`、`MinimumCheckInterval`、`Download*` 重试/续传选项、`AssetUrlRewriter`、`InstallerLauncher`、`TimeProvider`。 |
| `UpdateCheckResult` | 检查结果：`Success`/`Error`、`LatestVersion`/`Release`、`Throttled`，以及 `Update`（当且仅当有可用更新时非空）。 |
| `SemanticVersion` | 精简 SemVer 2.0，容忍 `v` 前缀、缺省 patch（`1.2`）和自定义 `TagPrefix`。 |
| `IAssetSelector` | `RuntimeAssetSelector`（默认，按当前 OS/架构匹配）、`PatternAssetSelector`（通配符 + 占位符 `{os}` `{arch}` `{rid}` `{version}` `{tagversion}` `{tag}`）、`DelegateAssetSelector`（自定义谓词）。 |
| `IChecksumProvider` | `ReleaseChecksumProvider`（默认：`<asset>.sha256` → `SHA256SUMS`/`*checksums.txt` → GitHub `digest`）、`StaticChecksumProvider`、`NoChecksumProvider`。 |
| `IGitHubReleaseClient` | GitHub API 抽象，可注入用于测试或自定义传输。 |
| `ILastCheckStore` | 可选的持久化接口，用于节流和跳过版本：`InMemoryLastCheckStore`、`JsonFileLastCheckStore(path)`，或自行实现。 |

## 版本判定

- `IncludePrerelease = false`（默认）：调用 `/releases/latest`（不含 prerelease 与 draft）。
- `IncludePrerelease = true`：拉取最近 `ReleaseScanCount`（默认 30）条 release，取 SemVer 最高者；跳过 draft 与无法解析的 tag（后者记录在 `SkippedTags`）。
- 仅当 `latest > current` 且该版本未被记录为已跳过时，`IsUpdateAvailable` 才为真。

## 节流与跳过版本

默认不节流。设置 `LastCheckStore` 与 `MinimumCheckInterval` 即可启用：

```csharp
var options = new UpdaterOptions
{
    Owner = "cli", Repo = "cli", CurrentVersion = "2.90.0",
    LastCheckStore = new JsonFileLastCheckStore(Path.Combine(dataDir, "update-state.json")),
    MinimumCheckInterval = TimeSpan.FromHours(24),
};
```

- 时间窗口内的检查直接返回 `Throttled == true`，不请求 GitHub。
- 记录检查时间失败不会丢弃检查结果，异常见 `LastCheckStoreError`。
- `SetSkippedVersionAsync(version)` / `ClearSkippedVersionAsync()` 用于跳过或恢复某个版本（它仍会出现在 `LatestVersion` 中）。
- 用户主动点击“立即检查”时，可用 `CheckForUpdateAsync(bypassSkippedVersion: true, bypassThrottle: true)`。

## 自动更新流程（`GitHubReleaseUpdater.AutoUpdate`）

`UpdateController` 是与 UI 无关的状态机：后台检查（首次 30 秒后，之后每 24 小时）、同一版本只通知一次、安装前询问、下载、启动安装程序。你来实现 `IUpdatePresenter`（托盘气泡、消息框、关闭应用等）；每个成员都有什么都不做的默认实现，所以在你询问用户之前不会安装任何东西。

```csharp
var controller = new UpdateController(updater, new MyPresenter(), new UpdateControllerOptions
{
    IsAutoCheckEnabled = () => settings.CheckForUpdates,
    InstallerLaunchOptions = InstallerLaunchOptions.InnoSetupSilent,
});
_ = controller.RunAsync(shutdownToken);       // 后台循环
await controller.CheckAsync(manual: true);    // “检查更新”按钮
await controller.InstallAsync();              // “更新到 x.y.z”
```

- **状态：** `Idle`、`Checking`、`UpToDate`、`Available`、`Failed`、`Installing`、`Downloading`、`Downloaded`；每次变化触发 `Changed`。`LastError` 与 `Progress`（及 `ProgressChanged`）给出最近的失败原因和下载进度。
- **手动检查**绕过节流和已跳过的版本；自动检查期间收到的手动检查会在其结束后立即执行。`SkipVersionAsync()` 把找到的版本记为已跳过。
- `DownloadAsync()` 提前下载并校验；之后 `InstallAsync()` 直接启动安装程序，不会再下载。
- 比当前版本新、但没有匹配安装包的发布会停在 `Available`，`HasInstaller == false`——只在 `HasInstaller` 为 true 时才显示安装入口。
- `LaunchInstaller = false` 用于非安装程序的包（如压缩包）：下载并校验后交给 `IUpdatePresenter.PackageDownloaded`。
- `BeforeInstallAsync` 在安装程序启动前执行（如保存状态）。`WatchInstaller = true` 会等待安装程序进程结束。
- `CleanDownloadDirectory = true` 会删除之前的下载（只对专用目录启用）；`DownloadDirectory` 默认为系统临时目录下的文件夹。
- **默认要求校验通过**（`RequireVerifiedDownload`）：无法校验的安装程序不会被启动。
- `AskToInstallAfterManualCheck = false` 让手动检查只查找不询问；`SkipChecksWhileUpdateKnown = true` 让后台轮次在已知有新版本时不再重复检查。
- **Inno Setup 静默更新并重启应用：** 使用 `InstallerLaunchOptions.InnoSetupSilent`，在 `IUpdatePresenter.InstallerStarted` 里关闭应用，并且不要给启动应用的 `[Run]` 条目加 `skipifsilent`。追加参数：`InnoSetupSilent.WithArguments("/CLOSEAPPLICATIONS")`。
- 展示层、`Changed` 处理器、`IsAutoCheckEnabled` 抛出的异常交给 `UpdateControllerOptions.OnError`。请在同一线程（通常是 UI 线程）调用控制器。

## 下载

- 流式写入 `<name>.partial` 后原子重命名；`Content-Length` 与实际字节数不符时抛 `UpdaterException`。
- 已知哈希时，在移动到目标位置前校验，不匹配则丢弃并抛 `ChecksumMismatchException`；无哈希时 `DownloadResult.Verified = false`（`RequireChecksum = true` 则在传输前直接失败）。
- 私有仓库资产通过 API + Token 下载。
- **重试：** 瞬时故障（I/O 错误、超时、响应体被截断或卡住）按指数退避重试——`DownloadMaxRetryAttempts`（默认 2）、`DownloadRetryDelay`（默认 1 秒）、`DownloadIdleTimeout`（默认 30 秒，`null` 为无限等待）。校验和不匹配、磁盘错误、取消不会重试。
- **续传：** 中断的下载通过 HTTP Range 续传，由 `<name>.partial.meta` 侧车文件记录归属；属于其他资产的部分文件会被丢弃。`DownloadAllowResume = false` 时始终从头开始，且不残留部分文件。
- **超时：** `UpdaterOptions.Timeout`（默认 10 秒，`null` 则依赖 `HttpClient.Timeout`）限制每次 API 调用及等待资产响应头的时间，超时抛 `TimeoutException`。取值范围 1 毫秒到约 49.7 天。
- **HttpClient：** `HttpClient` 留空时使用进程级共享实例，所以每次检查新建一个 `ReleaseUpdater` 没有问题。

### 镜像

GitHub 下载服务器很慢或被屏蔽时，可用 `AssetUrlRewriter` 把安装包的下载地址映射到另一个地址：

```csharp
AssetUrlRewriter = url => new Uri("https://mirror.example.com/" + url),   // 返回原地址则照常下载
```

只有安装包从镜像下载，且仅在已知校验和时；令牌不会发给镜像，校验和与发布信息仍来自 GitHub。镜像失败或内容不可用时，会改从 GitHub 下载一次。传入自己的 `IGitHubReleaseClient` 时该选项被忽略（请改设 `GitHubReleaseClient.AssetUrlRewriter`）。

## 选择资产

`RuntimeAssetSelector` 从不“猜”：像 `MyApp-Setup-1.2.3.exe` 这种既不含系统也不含架构的文件名不会被选中。只发布一个不带平台名的安装程序时，用 `new RuntimeAssetSelector { AllowUnknownPlatform = true }` 或 `new PatternAssetSelector("*Setup*.exe")`。

让用户自己选平台，可列出资产：

```csharp
foreach (var a in updater.ListAssets(check))   // 默认不含校验和/签名文件
    Console.WriteLine($"{a.Asset.Name,-40} os={a.Platform.Os ?? "?"} arch={a.Platform.Arch ?? "?"} {(a.MatchesCurrent ? "<- 当前机器" : "")}");

var chosen = updater.ListAssets(check).First(a => a.Platform is { Os: "linux", Arch: "arm64" });
await updater.DownloadAsync(check.Release!, chosen.Asset, downloadDir);
```

`AssetPlatformDetector.Detect(name)` 返回 `AssetPlatform`（`Os`、`Arch`、`Rid`），未识别的部分为 `null`。为其他平台自动挑选可用 `new RuntimeAssetSelector(new RuntimeInfo("linux", "arm64"))`。

## 运行安装程序

下载并校验通过后，库可以通过系统 Shell 启动 `.exe`/`.msi`（照常弹出 UAC）并立即返回，由安装程序自己关闭并重启你的应用。

```csharp
using GitHubReleaseUpdater.Installation;

// 一步完成：下载 + 校验 + 启动。
await updater.DownloadAndLaunchInstallerAsync(check, downloadDir, InstallerLaunchOptions.InnoSetupSilent, progress);

// 或分两步：
var file = await updater.DownloadAsync(check, downloadDir, progress);
await updater.LaunchInstallerAsync(file, new InstallerLaunchOptions { Arguments = "/S", RequireVerified = true });
```

- `InstallerLaunchOptions`：`Arguments`、`WorkingDirectory`、`RequireVerified`；预设 `InnoSetupSilent`、`InnoSetupVerySilent`。
- 下载失败、被取消或未通过校验时不会启动安装程序。启动前会重新计算哈希，下载后被改动的文件不会被运行（抛 `ChecksumMismatchException`）。
- 通过 `UpdaterOptions.InstallerLauncher` 可自定义或替换启动方式。

## 异常

均派生自 `UpdaterException`：`GitHubApiException`（`StatusCode`、`IsRateLimited`、`RateLimitResetAt`）、`AssetNotFoundException`（`AvailableAssets`）、`ChecksumMismatchException`（`Expected`/`Actual`）、`InstallerLaunchException`（`FilePath`、`IsUserCancelled`）。

`CheckForUpdateAsync()` 捕获所有异常并通过 `Success`/`Error` 返回；只有调用方主动取消才抛 `OperationCanceledException`。`DownloadAsync()` 会抛异常。

## 示例 CLI

```bash
dotnet run --project samples/GitHubReleaseUpdater.Cli -- check --owner cli --repo cli --current 1.0.0
```

```bash
dotnet run --project samples/GitHubReleaseUpdater.Cli -- download --owner cli --repo cli --current 1.0.0 --asset "gh_{version}_windows_amd64.zip" --require-checksum --out ./downloads
```

其他参数：`--token`、`--base-url`、`--prerelease`、`--tag-prefix`、`--sha256`、`--no-verify`。

## 构建与测试

```bash
dotnet build
dotnet test      # 仅使用 HTTP 桩，不访问网络
dotnet pack src/GitHubReleaseUpdater -c Release
```

## 许可证

MIT
