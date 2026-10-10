$ErrorActionPreference = 'Stop'
$scRoot = Split-Path -Parent $PSScriptRoot
$scManifest = Get-Content -LiteralPath (Join-Path $scRoot 'tests\installer-manifest.json') -Raw | ConvertFrom-Json
$scSetup = Join-Path $scRoot ('dist\' + $scManifest.filename)
$scTarget = [IO.Path]::GetFullPath((Join-Path $scRoot 'tests\installer-release-sandbox'))
if (-not $scTarget.StartsWith([IO.Path]::GetFullPath((Join-Path $scRoot 'tests')) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test target.' }
$scReg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Switchcraft'
$scDesktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Switchcraft.lnk'
$scMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Switchcraft'
$scRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ((Test-Path -LiteralPath $scReg) -or (Test-Path -LiteralPath $scDesktop) -or (Test-Path -LiteralPath $scMenu) -or (Test-Path -LiteralPath $scTarget)) { throw 'Real install resources already exist. Keep them unchanged; use the isolated test build instead.' }
if ((Get-ItemProperty -LiteralPath $scRunKey -ErrorAction SilentlyContinue).'Switchcraft.Keyboard') { throw 'An existing autostart entry must remain unchanged; use the isolated test build.' }
if (Get-Process -Name Switchcraft -ErrorAction SilentlyContinue) { throw 'Switchcraft is running; use the isolated test build.' }
$scChecks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed) { $scChecks.Add([ordered]@{ name = $Name; passed = $Passed }); if (-not $Passed) { throw "Release installer check failed: $Name" } }
function Run([string]$File, [string]$Arguments) { $scProcess = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait; $scProcess.ExitCode }
$scFailure = $null
try {
    Check 'Exact release installer hash' ((Get-FileHash -LiteralPath $scSetup).Hash -eq $scManifest.installer_sha256)
    $scCode = Run $scSetup ('/S /D=' + $scTarget)
    Check 'Release installer succeeds in a path containing spaces' ($scCode -eq 0)
    foreach ($scFile in $scManifest.payload_files) { Check ('Release payload: ' + $scFile.path) ((Get-FileHash -LiteralPath (Join-Path $scTarget $scFile.path)).Hash -eq $scFile.sha256) }
    $scInstalled = Get-ItemProperty -LiteralPath $scReg
    Check 'Real uninstall registration is correct' ($scInstalled.InstallLocation -eq $scTarget -and $scInstalled.DisplayVersion -eq $scManifest.version)
    $scShell = New-Object -ComObject WScript.Shell
    Check 'Real desktop shortcut is correct' ($scShell.CreateShortcut($scDesktop).TargetPath -eq (Join-Path $scTarget 'Switchcraft.exe'))
    Check 'Real Start menu shortcut is correct' ($scShell.CreateShortcut((Join-Path $scMenu 'Switchcraft.lnk')).TargetPath -eq (Join-Path $scTarget 'Switchcraft.exe'))
    Check 'Autostart remains disabled' (-not (Get-ItemProperty -LiteralPath $scRunKey -ErrorAction SilentlyContinue).'Switchcraft.Keyboard')
    $scMarker = Get-Content -LiteralPath (Join-Path $scTarget 'switchcraft-install.ini') -Raw
    Check 'Uninstall target matches the verified sandbox' ($scMarker.Contains('Directory=' + $scTarget))
    $scUninstall = Join-Path $scRoot 'tests\installer-release-uninstall.exe'
    Copy-Item -LiteralPath (Join-Path $scTarget 'Uninstall.exe') -Destination $scUninstall
    $scCode = Run $scUninstall ('/S _?=' + $scTarget)
    Check 'Exact release uninstaller succeeds' ($scCode -eq 0)
    Check 'Release uninstall removes installed files and folder' (-not (Test-Path -LiteralPath $scTarget))
    Check 'Release uninstall removes registration and shortcuts' (-not (Test-Path -LiteralPath $scReg) -and -not (Test-Path -LiteralPath $scDesktop) -and -not (Test-Path -LiteralPath $scMenu))
} catch { $scFailure = $_.ToString() }
$scReport = [ordered]@{ timestamp = (Get-Date).ToString('o'); installer_sha256 = $scManifest.installer_sha256; total = $scChecks.Count; passed = @($scChecks | Where-Object passed).Count; failed = @($scChecks | Where-Object { -not $_.passed }).Count; error = $scFailure; checks = $scChecks }
$scReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scRoot 'tests\installer-release-results.json') -Encoding utf8
[pscustomobject]$scReport | Select-Object total,passed,failed,error | ConvertTo-Json
if ($scFailure) { throw $scFailure }
