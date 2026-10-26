# GitHubReleaseUpdater.AutoUpdate

A UI-agnostic update flow, built on [GitHubReleaseUpdater](https://github.com/coldhighsun/GitHubReleaseUpdater).

`UpdateController` drives the usual sequence — check in the background, tell the user, ask before installing, download, start the installer — and leaves everything the user sees or the application does to an `IUpdatePresenter` you implement (tray balloon, message box, closing the app, …).

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

_ = controller.RunAsync(shutdownToken);            // first check after 30 s, then every 24 h
await controller.CheckAsync(manual: true);         // from a "Check for updates" button
await controller.InstallAsync();                   // from "Update to x.y.z"
```

- **States:** `Idle`, `Checking`, `UpToDate`, `Available`, `Failed`, `Installing`; `Changed` is raised on every change.
- **Automatic checks** announce a version once; **manual checks** bypass throttling and the skipped version, report "up to date" / "failed" and go straight to the confirmation.
- A manual check requested while an automatic one runs is made when that one ends; an installation that is already running makes further calls do nothing.
- **Verification is required by default** (`RequireVerifiedDownload`): an installer whose checksum could not be verified (no sidecar, no sums file, no digest from GitHub) is not started and the failure goes to `ReportInstallFailed`. Set it to false only if your releases cannot be verified.
- Exceptions thrown by your presenter, a `Changed` handler or `IsAutoCheckEnabled` never leave the controller stuck; they go to `UpdateControllerOptions.OnError`, together with a failure to record the check time.
- Methods are meant to be called from one thread (the UI thread); `Changed` and the presenter run on the caller's context.
