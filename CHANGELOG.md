# Changelog

## 1.0.0

First public release.

- Detects Steam Remote Play sessions from Steam's streaming log
- Switches Windows to a virtual display while streaming, so physical monitors get no signal and sleep; restores and verifies the exact layout afterwards
- DDC/CI monitor standby as a fallback when the virtual display driver isn't installed
- Stream resolution matches the connecting device automatically
- Device allow-list (for Remote Play Together)
- Settings window, tray menu, restore hotkey (Ctrl+Alt+Shift+M)
- Diagnostics zip with privacy redaction and pre-filled GitHub issues; crash reports
- Installer with optional virtual display driver
