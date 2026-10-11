# winget manifest (draft)

These three files describe the Windows installer for the Windows Package Manager. They are a **draft**:
nothing has been submitted anywhere.

Before submitting to `microsoft/winget-pkgs` (folder `manifests/m/miasimbilir/MCDownloadManager/<version>/`):

1. Publish `MC-Download-Manager-<version>-Setup.exe` at a permanent URL (a GitHub release).
2. Put that URL in `InstallerUrl` and the SHA-256 of **the published file** in `InstallerSha256`
   (`Get-FileHash`). The hash in the draft is the one of the installer built on the test machine.
3. Decide the `PackageIdentifier` (`Publisher.Package`). `miasimbilir.MCDownloadManager` follows the fork that
   publishes the Windows port; it cannot be changed after the first submission without a new package.
4. `PackageVersion` must equal the version shown in Windows' installed-apps list (`AppVersion` in
   `installer.iss`, taken from `MCDM.App.csproj`).
5. Check with `winget validate --manifest <folder>` and, on a test machine,
   `winget install --manifest <folder>` (needs `winget settings --enable LocalManifestFiles` as administrator).

`ProductCode` is Inno Setup's uninstall key for the fixed `AppId` in `installer.iss`; it is how winget matches an
installed copy (`winget list` shows it as `ARP\User\X64\{B6F3A2C4-...}_is1`) with this package for upgrades.
The installer is per-user (no administrator rights) and is not code-signed, so SmartScreen warns on a manual download;
winget accepts unsigned installers.
