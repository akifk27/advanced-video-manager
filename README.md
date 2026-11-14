# Advanced Video Manager (AVM)

Windows video editing, conversion and media downloading. Developed by M.Akif Kızılcık.
AVM is licensed under GPL-3.0-or-later. See LICENSE and AVM-LICENSE-NOTICE.txt.

## Install and configure

Download AVM-Setup-x64.exe from the matching GitHub Release. Setup includes the x64
.NET Desktop Runtime and offers Turkish and English. Choose an installation folder
and accept the application terms on first launch.

For media features, obtain the tools from their publishers:

- FFmpeg and FFprobe: https://ffmpeg.org/download.html
- yt-dlp: https://github.com/yt-dlp/yt-dlp/releases
- Deno: https://github.com/denoland/deno/releases

Place ffmpeg.exe, ffprobe.exe, yt-dlp.exe and deno.exe together in a folder you
control. In AVM, open Home > Media Tools Folder and select that folder.
When downloading or importing media, use content you have permission to use.

## Build

On Windows with the .NET 10 SDK, run dotnet build avm.slnx.
With Inno Setup 6 installed, run:

    powershell -File installer/build.ps1 -Version 1.0.0

Pass -Compiler with the full path to ISCC.exe when needed. The build creates
dist/AVM-Setup-x64.exe. The installer does not redistribute media tool executables.

## Release

Tag the commit used for the build (for example, v1.0.0). Attach AVM-Setup-x64.exe
to the release and offer the corresponding AVM source next to it. Keep the
installer filename unchanged for the in-app update check. Update checks use the
latest stable release, validate the installer asset size and SHA-256 digest, and
require user approval before installation.

Before publishing, test a clean Turkish and English installation and an upgrade
from an older build. Some dynamic AVM messages are still in Turkish.
