# build-ai-installer.ps1 — Build NSIS installer for Switchcraft AI
# Usage: .\tools\build-ai-installer.ps1
param(
    [string]$Compiler = 'C:\Program Files (x86)\NSIS\makensis.exe'
)
$ErrorActionPreference = 'Stop'

$scRoot    = Split-Path -Parent $PSScriptRoot
$scPayload = Join-Path $scRoot 'publish'
$scPayloadPrefix = [IO.Path]::GetFullPath($scPayload).TrimEnd('\') + '\'

function Get-PayloadRelative([string]$Path) {
    if (-not $Path.StartsWith($scPayloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the payload: $Path"
    }
    $Path.Substring($scPayloadPrefix.Length)
}

$scExe = Join-Path $scPayload 'Switchcraft.exe'
if (-not (Test-Path -LiteralPath $scExe)) { throw "Switchcraft.exe not found at: $scExe" }

$scVersion = (Get-Item -LiteralPath $scExe).VersionInfo.ProductVersion.Split('+')[0]
if ($scVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Unsupported version: $scVersion" }
Write-Output "Version: $scVersion"

if (-not (Test-Path -LiteralPath $Compiler)) { throw "NSIS compiler was not found: $Compiler" }

# Copy license notices into payload
$scLicenseDir = Join-Path $scPayload 'Licenses'
[IO.Directory]::CreateDirectory($scLicenseDir) | Out-Null
$noticesDir = Join-Path $scRoot 'installer\notices'
if (Test-Path -LiteralPath $noticesDir) {
    foreach ($scNotice in Get-ChildItem -LiteralPath $noticesDir -File) {
        Copy-Item -LiteralPath $scNotice.FullName -Destination (Join-Path $scLicenseDir $scNotice.Name) -Force
    }
}

# Generate install-files.nsh and uninstall-files.nsh
$scGenerated = Join-Path $scRoot 'installer\generated'
[IO.Directory]::CreateDirectory($scGenerated) | Out-Null

$scInstallLines   = [Collections.Generic.List[string]]::new()
$scUninstallLines = [Collections.Generic.List[string]]::new()
$scFiles = @(Get-ChildItem -LiteralPath $scPayload -Recurse -File | Sort-Object FullName)

foreach ($scFile in $scFiles) {
    $scRelative = Get-PayloadRelative $scFile.FullName
    if ($scRelative -match '[$"]' -or $scRelative.StartsWith('..')) { throw 'Unsafe payload filename.' }
    $scParent = Split-Path -Parent $scRelative
    $scInstallLines.Add('SetOutPath "$INSTDIR' + $(if ($scParent) { '\' + $scParent } else { '' }) + '"')
    $scInstallLines.Add('File "' + $scFile.FullName + '"')
    $scUninstallLines.Add('Delete "$INSTDIR\' + $scRelative + '"')
}

foreach ($scDirectory in @(Get-ChildItem -LiteralPath $scPayload -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending)) {
    $scRelative = Get-PayloadRelative $scDirectory.FullName
    $scUninstallLines.Add('RMDir "$INSTDIR\' + $scRelative + '"')
}
$scInstallLines.Add('SetOutPath "$INSTDIR"')

$scInstallFile   = Join-Path $scGenerated 'install-files.nsh'
$scUninstallFile = Join-Path $scGenerated 'uninstall-files.nsh'
$enc = New-Object System.Text.UTF8Encoding $true
[IO.File]::WriteAllLines($scInstallFile,   $scInstallLines,   $enc)
[IO.File]::WriteAllLines($scUninstallFile, $scUninstallLines, $enc)

Write-Output "Generated: $scInstallFile"
Write-Output "Generated: $scUninstallFile"
Write-Output "Payload files: $($scFiles.Count)"

$scOutput = Join-Path $scRoot "Switchcraft-AI-Setup-$scVersion-win-x64.exe"

$scArguments = @(
    '/V3',
    '/INPUTCHARSET', 'UTF8',
    "/DPRODUCT_VERSION=$scVersion",
    "/DPROJECT_ROOT=$scRoot",
    "/DOUTPUT_FILE=$scOutput",
    "/DINSTALL_FILES=$scInstallFile",
    "/DUNINSTALL_FILES=$scUninstallFile",
    ('/DINSTALLED_KB=' + [Math]::Ceiling(($scFiles | Measure-Object Length -Sum).Sum / 1KB))
)
$scArguments += (Join-Path $scRoot 'installer\Switchcraft.nsi')

Write-Output "Running NSIS..."
& $Compiler @scArguments
if ($LASTEXITCODE -ne 0) { throw "NSIS failed: $LASTEXITCODE" }

$setupSize = (Get-Item -LiteralPath $scOutput).Length
Write-Output ""
Write-Output "=========================================="
Write-Output "Installer ready: $scOutput"
Write-Output "Size: $([Math]::Round($setupSize / 1MB, 1)) MB"
Write-Output "=========================================="
