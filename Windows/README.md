# MC Download Manager for Windows

A Windows port of the macOS app in this repository: the same download engine, browser bridge and
extension, rebuilt on .NET 10 and [Avalonia](https://avaloniaui.net). [Türkçe](README.tr.md)

The Chrome extension in [`../Extension`](../Extension) is used unchanged, and the bridge speaks the same
API on the same port (`127.0.0.1:48213`), so one extension works with either app.

Up to 1.3.0 the version number followed the macOS app. **From 2.0 on the Windows app has its own**: what is
listed under "New in 2.0" below exists on Windows only. The extension and the bridge stay the same as the
macOS app's; 2.1 brought in the extension and the new features of macOS 1.4.1.

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

### New in 2.1 (from macOS 1.4.0–1.4.1)

- **Sites that refuse download managers** — when a server refuses parallel connections the app continues on a
  single one, the way a browser downloads. If that fails too (WeTransfer's one-time links, for example) it asks
  "Download in the browser instead?" and hands the download back to the browser it came from. Failed downloads
  also have "Download in Browser".
- **Leave a site to the browser** — the extension popup's "Leave downloads on ‹site› to the browser" switch keeps
  that site's downloads in the browser; the list is in Settings ▸ Browser Extensions.
- **The extension keeps itself current** — after an app update the Chrome/Edge/Brave/Opera extension reloads
  itself with the new version (extensions older than 1.4.1 need one click on "↻"; the app says so). Firefox add-on
  1.4.1: no longer pauses new downloads, so downloads that cannot be resumed (ChatGPT's ZIP files) do not break.

### New in 2.0

- **Asks before it downloads** — a file handed over by the browser is shown first, with its name, size and
  where it will be saved: "Download", "Download Later" or "Cancel". Name and folder can be changed for that
  download, and when the name is taken the window says what the file will be called instead. Can be switched
  off in Settings ▸ General.
- **Does not fetch the same thing twice** — something that was already downloaded and is still on disk is
  asked about (Open / Show in Folder / Download Again); a download that is under way is not added a second
  time, and a failed one is retried with the cookies the browser sends now.
- **Speed limit** — one limit in KB/s for all downloads together, switched with one click from the tray icon
  or the status bar.
- **When the queue is done** — puts the computer to sleep or shuts it down after the last download, behind a
  30-second countdown that can be called off. A one-off choice.
- **Playlists and subtitles** — a whole YouTube playlist goes into a folder named after it, numbered in the
  order of the list; a video that is no longer available does not stop the rest. Subtitles are saved next to
  the video as `.srt` files.
- **Updates itself** — checks this repository's releases once a day. A copy installed with the installer
  downloads the new Setup.exe, verifies it against the SHA-256 GitHub publishes and installs it; a copy
  unpacked from the zip opens the download page.
- **Start a waiting download right away** — the button in the window of a queued item now says "Download".
- **Old-extension warning** — when a browser still runs an earlier version of the extension,
  Settings ▸ Browser Extensions says so; after an app update the prepared extension folders are brought up to
  date automatically. A busy bridge port is retried until it is free.

## Differences from the macOS app

| macOS | Windows |
|---|---|
| Safari extension | not applicable; Edge is supported in its place |
| Firefox add-on with one-click install (1.2.0) | the same signed add-on (`FirefoxExtension.xpi`, taken from the macOS release) ships next to the exe; "Add to Firefox" in the wizard |
| In-app sign-in window for age-restricted videos | import a `cookies.txt` export in Settings ▸ Video |
| Update check against GitHub Releases | included (2.0): looks at this repository's `windows-v…` releases and runs the installer itself |
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
| `tests/MCDM.Tests` | engine tests against a local origin server (ranges, server throttling, cut connections, redirects, the download question, the speed limit, playlists, updates) |
| `dev/MCDM.Shots` | renders every window off-screen to PNG, for checking layout without a Windows machine |
| `scripts/extract-strings.py` | regenerates `Strings.tr.json` from the macOS string catalog |

Turkish strings come from `App/Resources/Localizable.xcstrings`; strings that only exist on Windows are
in `src/MCDM.Core/Strings.tr.windows.json`. A test fails when a UI string has no translation.

## License

MIT, like the rest of the repository. The bundled binaries keep their own licenses:
[yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense) and [FFmpeg](https://ffmpeg.org)
(yt-dlp/FFmpeg-Builds, GPL). Icons are from Microsoft's Fluent UI System Icons (MIT).
