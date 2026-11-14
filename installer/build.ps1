param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
[ValidatePattern('^[A-Za-z0-9-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'akifk27/advanced-video-manager',
    [string]$Compiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'avmsoftware'
if (!(Test-Path -LiteralPath $Compiler)) {
    throw 'Pass the path to Inno Setup ISCC.exe with -Compiler.'
}
foreach ($tool in 'ffmpeg.exe','ffprobe.exe','yt-dlp.exe','deno.exe') {
    if (!(Test-Path -LiteralPath (Join-Path $project "Tools/$tool"))) {
        throw "Missing Tools/$tool"
    }
}

dotnet publish (Join-Path $project 'avmsoftware.csproj') -c Release -r win-x64 --self-contained false `
    -o (Join-Path $root 'artifacts/publish') "-p:Version=$Version" "-p:AssemblyVersion=$Version.0" "-p:AvmUpdateRepository=$Repository"
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
& $Compiler "/DAvmVersion=$Version" (Join-Path $PSScriptRoot 'AVM.iss')
if ($LASTEXITCODE) { throw "Inno Setup build failed ($LASTEXITCODE)" }

