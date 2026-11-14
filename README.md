# Advanced Video Manager (AVM)

Windows video editing, conversion and media downloading. Developed by M.Akif Kızılcık.
AVM source: GPL-3.0-or-later. See LICENSE and AVM-LICENSE-NOTICE.txt.

## Build

Use the .NET 10 SDK on Windows. Run dotnet build avm.slnx from the repository root.
For all media features, place ffmpeg.exe, ffprobe.exe, yt-dlp.exe and deno.exe in
avmsoftware/Tools. See avmsoftware/THIRD_PARTY_LICENSES for distribution notices.

## Installer

Install Inno Setup, then run:

    powershell -File installer/build.ps1 -Version 1.0.0

Pass -Compiler with the path to ISCC.exe if needed. Output: dist/AVM-Setup-x64.exe.
This installer requires the x64 .NET 10 Desktop Runtime.
A single installer supports Turkish and English. AVM also supports in-app language selection.
The English interface still contains some Turkish dynamic messages.

## GitHub release

Use a tag matching the installer version, for example v1.0.0. Upload AVM-Setup-x64.exe
and provide the corresponding source for the exact build, including GPL tools bundled
in the installer. The bundled Gyan FFmpeg build has been matched to upstream binaries,
but its complete corresponding source is still unverified. The packaged yt-dlp source
and Deno third-party notices also need verification before public binary distribution.
Test clean installation and upgrading from a previous AVM release before publishing.
