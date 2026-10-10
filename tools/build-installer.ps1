param([switch]$TestBuild, [string]$Compiler = 'C:\Program Files (x86)\NSIS\makensis.exe')
$ErrorActionPreference = 'Stop'
$scRoot = Split-Path -Parent $PSScriptRoot
$scPayload = Join-Path $scRoot 'dist\Switchcraft'
$scPayloadPrefix = [IO.Path]::GetFullPath($scPayload).TrimEnd('\') + '\'
function Get-PayloadRelative([string]$Path) {
    if (-not $Path.StartsWith($scPayloadPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside the payload: $Path" }
    $Path.Substring($scPayloadPrefix.Length)
}
$scExe = Join-Path $scPayload 'Switchcraft.exe'
$scVersion = (Get-Item -LiteralPath $scExe).VersionInfo.ProductVersion.Split('+')[0]
if ($scVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Unsupported version: $scVersion" }
$scRelease = Get-Content -LiteralPath (Join-Path $scRoot 'tests\release-manifest.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $scExe).Hash -ne $scRelease.executable_sha256) { throw 'Executable differs from the verified release.' }
if ($scRelease.version -ne $scVersion) { throw 'Version differs from verified release.' }
if (-not (Test-Path -LiteralPath $Compiler)) { throw "NSIS compiler was not found: $Compiler" }
$scLicenseDir = Join-Path $scPayload 'Licenses'
[IO.Directory]::CreateDirectory($scLicenseDir) | Out-Null
foreach ($scNotice in Get-ChildItem -LiteralPath (Join-Path $scRoot 'installer\notices') -File) {
    Copy-Item -LiteralPath $scNotice.FullName -Destination (Join-Path $scLicenseDir $scNotice.Name)
}
$scGenerated = Join-Path $scRoot 'installer\generated'
[IO.Directory]::CreateDirectory($scGenerated) | Out-Null
$scInstallLines = [Collections.Generic.List[string]]::new()
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
$scInstallFile = Join-Path $scGenerated 'install-files.nsh'
$scUninstallFile = Join-Path $scGenerated 'uninstall-files.nsh'
[IO.File]::WriteAllLines($scInstallFile, $scInstallLines, [Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllLines($scUninstallFile, $scUninstallLines, [Text.UTF8Encoding]::new($true))
$scSuffix = if ($TestBuild) { '-test' } else { '' }
$scOutputDirectory = if ($TestBuild) { $scGenerated } else { Join-Path $scRoot 'dist' }
$scOutput = Join-Path $scOutputDirectory "Switchcraft-Setup-$scVersion-win-x64$scSuffix.exe"
$scArguments = @('/V3', '/INPUTCHARSET', 'UTF8', "/DPRODUCT_VERSION=$scVersion", "/DPROJECT_ROOT=$scRoot", "/DOUTPUT_FILE=$scOutput", "/DINSTALL_FILES=$scInstallFile", "/DUNINSTALL_FILES=$scUninstallFile", ('/DINSTALLED_KB=' + [Math]::Ceiling(($scFiles | Measure-Object Length -Sum).Sum / 1KB)))
if ($TestBuild) { $scArguments += '/DINSTALLER_TEST' }
$scArguments += (Join-Path $scRoot 'installer\Switchcraft.nsi')
& $Compiler @scArguments
if ($LASTEXITCODE -ne 0) { throw "NSIS failed: $LASTEXITCODE" }
$scFileHashes = @($scFiles | ForEach-Object { [ordered]@{ path = Get-PayloadRelative $_.FullName; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } })
$scManifest = [ordered]@{
    version = $scVersion
    built_at = (Get-Date).ToString('o')
    filename = [IO.Path]::GetFileName($scOutput)
    test_build = [bool]$TestBuild
    installer_sha256 = (Get-FileHash -LiteralPath $scOutput).Hash
    installer_bytes = (Get-Item -LiteralPath $scOutput).Length
    application_sha256 = (Get-FileHash -LiteralPath $scExe).Hash
    signature = (Get-AuthenticodeSignature -LiteralPath $scOutput).Status.ToString()
    payload_files = $scFileHashes
}
$scManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $scRoot "tests\installer-manifest$scSuffix.json") -Encoding utf8
Write-Output "Ready: $scOutput"
