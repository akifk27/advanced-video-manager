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

$publish = Join-Path $root ('artifacts/publish-' + [guid]::NewGuid())
dotnet publish (Join-Path $project 'avmsoftware.csproj') -c Release -r win-x64 --self-contained true `
    -o $publish "-p:Version=$Version" "-p:AssemblyVersion=$Version.0" "-p:AvmUpdateRepository=$Repository" "-p:IncludeMediaToolBinaries=false" "-p:DebugType=None"
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
& $Compiler "/DAvmVersion=$Version" "/DAvmPublishDir=$publish" (Join-Path $PSScriptRoot 'AVM.iss')
if ($LASTEXITCODE) { throw "Inno Setup build failed ($LASTEXITCODE)" }


