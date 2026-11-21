# Remote Play Blanker

**Turns your PC's monitors off while you're streaming with Steam Remote Play, and back on when you stop.**

Playing on a Steam Deck, laptop, phone or TV from your gaming PC? Your PC's monitors normally stay on the whole time, lighting up the room and showing everything you do. Remote Play Blanker notices when a stream starts and switches them off, then puts everything back exactly as it was when the stream ends.

- Works with every monitor, because it doesn't depend on monitor features
- Your normal monitor layout (positions, resolutions, refresh rates) is restored exactly
- The stream's resolution automatically matches the device you're playing on
- Lives in the system tray; nothing to do once it's installed
- No network access, no telemetry

## Install

1. Download `RemotePlayBlanker-Setup-x.y.z.exe` from [Releases](../../releases).
2. Run it. Keep **"Install the virtual display"** ticked (see [How it works](#how-it-works)).
3. That's it. Start a Remote Play stream and your monitors switch off about 3 seconds later.

Requires 64-bit Windows 10 (1809 or newer) or Windows 11, and Steam.

> **Windows SmartScreen** may warn about an unrecognised app because the installer isn't code-signed yet. Click **More info → Run anyway**, or build it yourself from source.

## If your screens stay black

This should never happen, but if it does, any of these brings them back:

1. Press **Ctrl + Alt + Shift + M**.
2. Press **Win + P** and choose **Extend** (or **PC screen only**).
3. Restart the PC. Windows restores your normal layout on reboot, and the app tidies up on its next start.

Then please [report it](#reporting-problems) so it can be fixed.

## Using it

Right-click the tray icon (a monitor with a moon; it turns blue while a stream is active):

| Menu item | What it does |
|---|---|
| **Settings…** | Open the settings window (also: double-click the icon, or launch the app again from the Start menu) |
| **Enabled** | Pause or resume it without quitting |
| **Start with Windows** | Run automatically when you sign in |
| **Test: turn monitors off for 5 seconds** | Check it works on your setup without starting a stream |
| **Restore monitors now** | Bring the monitors back immediately |
| **Report a problem…** | Create a diagnostics file and open a GitHub issue |

### Settings

- **Method:** *Virtual display* (recommended) or *Monitor standby over DDC/CI* (no driver needed, but many monitors wake themselves back up).
- **Stream resolution:** *Auto* matches what the connecting device asks for. You can also pick a fixed size such as 1280×800 for a Steam Deck, or leave the resolution unchanged.
- **Delay after stream starts:** seconds to wait before switching off.
- **Only these devices:** device names (as Steam reports them, e.g. `steamdeck`) that trigger it. The settings window shows the last device that connected. Leave it empty for any device. **Set this if you use Remote Play Together**, so friends joining your game don't turn your screens off.

## How it works

1. **Detecting a stream.** Steam writes a log while it's hosting a Remote Play session (`Steam\logs\streaming_log.txt`). The app watches it for `Streaming started to <device>` and `Restoring default audio device` (end of stream). If Steam goes quiet without logging the end, the session is treated as over after 60 seconds.
2. **Switching the monitors off.** Steam has to keep capturing a screen, so Windows' normal "turn off display" can't be used: the stream would freeze. Instead, the app switches Windows to show *only* a virtual display (provided by the open-source [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), included in the installer). Your physical monitors get no signal and go to sleep on their own, and Steam streams the virtual screen.
3. **Switching back.** Your exact layout is saved before switching and re-applied afterwards, then checked against what was saved. If anything goes wrong, Windows' own stored layout is used as a fallback.

All display changes go through a single worker that always applies the *latest* wanted state, so a stream that ends while the screens are still switching off can't leave them dark. If the app ever crashes, its crash handler restores the monitors first.

## Reporting problems

Right-click the tray icon → **Report a problem…**. This:

1. Saves a diagnostics zip to your Desktop (app log, settings, monitor/GPU info, and the relevant part of Steam's streaming log). **IP addresses, window titles, your Windows user name and PC name are removed.** It's all plain text, so have a look inside if you like.
2. Opens a pre-filled GitHub issue. Describe what happened and drag the zip into it.

If the app crashes, it saves a crash report and offers to do this for you the next time it starts.

Useful extras from a terminal (`"C:\Program Files\Remote Play Blanker\RemotePlayBlanker.exe" --help`):

| Command | What it does |
|---|---|
| `--self-test` | Switches to the virtual display for 5 s, restores, and checks your layout came back identical (exit the tray app first) |
| `--displays` | Lists displays and the active layout |
| `--watch` | Prints Steam's streaming log live |
| `--diagnostics` | Saves the diagnostics zip |

## Known limitations

- It relies on lines in Steam's log. If a Steam update changes them, detection stops working until the patterns are updated (they can also be changed in `%AppData%\RemotePlayBlanker\settings.json`).
- Remote Play Together will turn your screens off unless you set **Only these devices**.
- While the PC is locked, Windows doesn't allow display changes. The app tries again when you unlock.
- Not yet tested on PCs with multiple graphics cards or on laptops with built-in screens. Reports are welcome!

## Uninstalling

Use **Settings → Apps → Installed apps → Remote Play Blanker → Uninstall**. The virtual display driver is removed too, unless it was already installed before (e.g. by another app). Your settings and logs stay in `%AppData%\RemotePlayBlanker`; delete that folder if you don't want them.

## Building from source

Requirements: [.NET 9 SDK](https://dotnet.microsoft.com/download), and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer (`winget install JRSoftware.InnoSetup`).

```powershell
dotnet test                # run the tests
dotnet run --project src/RemotePlayBlanker
.\build-installer.ps1      # tests + single-file exe + dist\RemotePlayBlanker-Setup-<version>.exe
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for how the code is organised.

## License

[GPL-3.0-or-later](LICENSE). Copyright © Remote Play Blanker contributors.

The installer includes [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT); see [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt).

Not affiliated with Valve. Steam and Steam Remote Play are trademarks of Valve Corporation.
