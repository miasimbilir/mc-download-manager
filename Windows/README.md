# MC Download Manager for Windows

A Windows port of the macOS app in this repository: the same download engine, browser bridge and
extension, rebuilt on .NET 10 and [Avalonia](https://avaloniaui.net). [Türkçe](README.tr.md)

The Chrome extension in [`../Extension`](../Extension) is used unchanged, and the bridge speaks the same
API on the same port (`127.0.0.1:48213`), so one extension works with either app.

## What it does

- **Segmented downloads** — up to 16 parallel HTTP `Range` connections per file (8 by default), a single
  stream when the server has no range support, fewer connections when it answers `429`/`503`.
- **Pause / resume** — segment state is saved; downloads continue after the app is restarted.
- **Browser integration** — Chrome, Edge, Brave, Opera and Firefox hand their downloads over together with cookies,
  `Referer` and `User-Agent`. A wizard walks through loading the unpacked extension in the Chromium browsers; Firefox installs the Mozilla-signed add-on shipped with the app in one click.
- **Video** — `<video>`, `.m3u8` and `.mpd` streams are captured; "Download video from this page" runs the
  bundled yt-dlp and merges with the bundled ffmpeg. yt-dlp can be updated from Settings, and deno
  (needed for YouTube) is installed on request with its checksum verified.
- **Starts itself when a download arrives** — as in the macOS app from 1.3.0, the extension starts the app
  when it is not running. On Windows the browsers call `MCDownloadManager.exe` itself as the native messaging
  host; the app registers it under `HKCU\Software\…\NativeMessagingHosts` at every start.
- **Quality question** — before a video download starts, the app lists the resolutions that video really
  offers with the size of each (measured, not guessed, for X and Instagram) plus audio only as MP3 or M4A.
  "Always use this" stores the choice. A picked resolution becomes part of the file name
  (`Title [id] 720p.mp4`), so asking for the same video in another quality gives a second file. Not in the macOS app.
- **Clipboard** — copying a link to a captured file type asks whether to download it; `Ctrl+V` in the
  main window accepts any link.
- **Lives in the notification area** — the bridge stays up when the window is closed; optional start at
  sign-in. English and Turkish, following the system language.

## Differences from the macOS app

| macOS | Windows |
|---|---|
| Safari extension | not applicable; Edge is supported in its place |
| Firefox add-on with one-click install (1.2.0) | the same signed add-on (`FirefoxExtension.xpi`, taken from the macOS release) ships next to the exe; "Add to Firefox" in the wizard |
| In-app sign-in window for age-restricted videos | import a `cookies.txt` export in Settings ▸ Video |
| Update check against GitHub Releases | not included yet (yt-dlp/deno updates are) |
| Quarantine attribute on finished files | Mark of the Web (`Zone.Identifier`), so SmartScreen checks them |
| Menu bar item, login item | notification-area icon, `HKCU\…\Run` entry |

Part files are created sparse so that eight connections can write to far-apart offsets without NTFS
zero-filling the gaps first.

## Install

**Installer:** run `MC-Download-Manager-<version>-Setup.exe`. It needs no administrator rights, installs into
`%LOCALAPPDATA%\Programs\MC Download Manager`, adds a Start menu entry and can be removed from Settings ▸ Apps
(settings and the download list are kept unless you say otherwise). It also upgrades a copy unpacked from the zip.
Silent install: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.

**Zip:** unzip `MC-Download-Manager-<version>-win-x64.zip`, move the folder somewhere permanent and run
`MCDownloadManager.exe`. The build is not code-signed, so SmartScreen asks once ("More info" ▸ "Run
anyway"). Nothing else needs to be installed: the .NET runtime is inside the executable.

`MCDownloadManager.exe --selftest` checks the platform-dependent parts (file system, Recycle Bin,
bridge port, registry, bundled tools) and writes `%APPDATA%\MC Download Manager\selftest.txt`.

## Build

Needs the .NET 10 SDK. The app is cross-compiled; macOS, Linux and Windows all work as build hosts.

```bash
Windows/scripts/fetch-tools.sh   # yt-dlp.exe and ffmpeg.exe into Windows/Tools/ (checksums verified)
Windows/scripts/build.sh         # → Windows/build/MC-Download-Manager-<version>-win-x64.zip
# on Windows: powershell -ExecutionPolicy Bypass -File Windows\scripts\build.ps1
#   with Inno Setup 6 installed (winget install JRSoftware.InnoSetup) it also builds the installer: …-Setup.exe
#   (Windows\packaging\installer.iss; winget draft in Windows\packaging\winget\)
dotnet test Windows/tests/MCDM.Tests
```

| Path | Contents |
|---|---|
| `src/MCDM.Core` | engine without UI: segmented downloader, store and queue, yt-dlp wrapper, bridge, tool installer |
| `src/MCDM.App` | Avalonia UI: main window, per-download window, settings, setup wizard, tray icon |
| `tests/MCDM.Tests` | engine tests against a local origin server (ranges, rate limits, cut connections, redirects) |
| `dev/MCDM.Shots` | renders every window off-screen to PNG, for checking layout without a Windows machine |
| `scripts/extract-strings.py` | regenerates `Strings.tr.json` from the macOS string catalog |

Turkish strings come from `App/Resources/Localizable.xcstrings`; strings that only exist on Windows are
in `src/MCDM.Core/Strings.tr.windows.json`. A test fails when a UI string has no translation.

## License

MIT, like the rest of the repository. The bundled binaries keep their own licenses:
[yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense) and [FFmpeg](https://ffmpeg.org)
(yt-dlp/FFmpeg-Builds, GPL). Icons are from Microsoft's Fluent UI System Icons (MIT).
