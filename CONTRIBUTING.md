# Contributing

Thanks for helping! Bug reports with a diagnostics file (tray icon → **Report a problem…**) are the most useful contribution of all.

## Building and testing

```powershell
dotnet test                                   # unit tests (no display changes, safe to run anywhere)
dotnet run --project src/RemotePlayBlanker    # run the tray app
.\build-installer.ps1                         # tests + installer in dist\
```

Before changing anything display-related, also run the real round trip on your own machine (exit the tray app first):

```powershell
dotnet run --project src/RemotePlayBlanker -- --self-test
```

It must print `PASS`.

## How the code is organised

| File | Responsibility |
|---|---|
| `SessionDetector.cs` | `SessionTracker`: pure state machine turning Steam log lines into "streaming / not streaming" (unit tested with recorded logs). `LogTailer`: reads new lines safely (rotation, partial lines, UTF-8). `SessionDetector`: glues them to a 1 s timer. |
| `DisplayController.cs` | The **only** place that changes displays. Callers set the wanted state; one worker thread applies the latest one, so events can't be applied out of order. |
| `DisplayTopology.cs` | Virtual-display switching with the Windows CCD API (`QueryDisplayConfig`/`SetDisplayConfig`); saves, restores and verifies the layout. |
| `MonitorControl.cs` | Fallback: monitor standby/brightness over DDC/CI. |
| `TrayContext.cs` | Tray icon, menu, hotkey, wiring. |
| `SettingsForm.cs`, `Settings.cs` | Settings window and the JSON settings file (validated on load). |
| `Diagnostics.cs` | Diagnostics zip with privacy redaction, GitHub issue link, crash handler. |
| `DriverInstaller.cs` | Installs/removes the virtual display device via SetupAPI (used by the installer). |
| `installer/` | Inno Setup script and the bundled, signed Virtual Display Driver. |

## Guidelines

- **Never leave the user with black screens.** Any new code path that blanks must have a matching restore path, and failures must fall back to restoring.
- Log what you do and why something failed (include Win32 error codes). Logs are what make bug reports useful.
- Anything that can end up in a diagnostics file must go through `Diagnostics.Redact`.
- Add a test when you fix a detection bug, ideally a redacted snippet of the real Steam log in `tests/RemotePlayBlanker.Tests/Fixtures/`.
- If Steam changes its log format, update the defaults in `Settings.cs` *and* add a fixture for the new format, keeping the old one passing.
