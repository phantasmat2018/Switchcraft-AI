$ErrorActionPreference = 'Stop'
$scRoot = Split-Path -Parent $PSScriptRoot
$scVersion = (Get-Item -LiteralPath (Join-Path $scRoot 'dist\Switchcraft\Switchcraft.exe')).VersionInfo.ProductVersion
$scSetup = Join-Path $scRoot "installer\generated\Switchcraft-Setup-$scVersion-win-x64-test.exe"
$scSandbox = [IO.Path]::GetFullPath((Join-Path $scRoot 'tests\installer-sandbox'))
if (-not $scSandbox.StartsWith([IO.Path]::GetFullPath((Join-Path $scRoot 'tests')) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test installation target.' }
if (Test-Path -LiteralPath $scSandbox) { throw 'Test installation directory already exists; inspect it before rerunning.' }
$scTestKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Switchcraft.InstallerTest'
if (Test-Path -LiteralPath $scTestKey) { throw 'Installer-test registry key already exists; inspect it before rerunning.' }
$scRealKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Switchcraft'
function Get-RealRegistration {
    # Compare only registry values: serialized PS* provider metadata is not stable in Windows PowerShell 5.1.
    if (-not (Test-Path -LiteralPath $scRealKey)) { return '' }
    $scKey = Get-Item -LiteralPath $scRealKey
    @($scKey.GetValueNames() | Sort-Object | ForEach-Object { $_ + '=' + $scKey.GetValue($_) }) -join '|'
}
$scRealBefore = Get-RealRegistration
$scDesktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Switchcraft.lnk'
$scDesktopHash = if (Test-Path -LiteralPath $scDesktopLink) { (Get-FileHash -LiteralPath $scDesktopLink).Hash } else { '' }
$scPrefs = Join-Path $env:LOCALAPPDATA 'Switchcraft'
function Get-SettingsSnapshot {
    if (Test-Path -LiteralPath $scPrefs) {
        @(Get-ChildItem -LiteralPath $scPrefs -File | Sort-Object Name | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|'
    } else { '' }
}
$scSettingsBefore = Get-SettingsSnapshot
$scChecks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed, [string]$Details = '') {
    $scChecks.Add([ordered]@{ name = $Name; passed = $Passed; details = $Details })
    if (-not $Passed) { throw "Installer test failed: $Name. $Details" }
}
function Run-Installer([string]$Path, [string]$Arguments) {
    $scChild = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait
    return $scChild.ExitCode
}
$scProduction = Join-Path $scRoot 'dist\Switchcraft\Switchcraft.exe'
$scRunning = @(Get-Process -Name Switchcraft -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $scProduction })
$scError = $null
try {
    # Actual live-app guard first, before stopping the portable application.
    $scMutex = [Threading.Mutex]::new($false, 'Local\Switchcraft.Desktop')
    try {
        $scCode = Run-Installer $scSetup ('/S /D=' + $scSandbox)
        Check 'Installation refuses an active app' ($scCode -eq 1618) "Exit code $scCode"
        Check 'Blocked installation creates no payload' (-not (Test-Path -LiteralPath $scSandbox))
    } finally { $scMutex.Dispose() }
    $scRunning | Stop-Process
    Start-Sleep -Milliseconds 300
    $scCode = Run-Installer $scSetup ('/S /D=' + $scSandbox)
    Check 'Silent per-user installation succeeds' ($scCode -eq 0) "Exit code $scCode"
    $scIni = Join-Path $scSandbox 'switchcraft-install.ini'
    Check 'Silent install without /LAYA does not request Laya' (-not (Get-Content -LiteralPath $scIni -Raw).Contains('LayaRequested'))
    $scManifest = Get-Content -LiteralPath (Join-Path $scRoot 'tests\installer-manifest-test.json') -Raw | ConvertFrom-Json
    foreach ($scFile in $scManifest.payload_files) {
        $scInstalledFile = Join-Path $scSandbox $scFile.path
        Check ('Payload SHA256: ' + $scFile.path) ((Test-Path -LiteralPath $scInstalledFile) -and (Get-FileHash -LiteralPath $scInstalledFile).Hash -eq $scFile.sha256)
    }
    $scRegistration = Get-ItemProperty -LiteralPath $scTestKey
    Check 'Registered version and installation path are correct' ($scRegistration.DisplayVersion -eq $scVersion -and $scRegistration.InstallLocation -eq $scSandbox)
    Check 'Uninstall commands quote paths containing spaces' ($scRegistration.UninstallString -eq ('"' + $scSandbox + '\Uninstall.exe"') -and $scRegistration.QuietUninstallString -eq ('"' + $scSandbox + '\Uninstall.exe" /S'))
    $scShell = New-Object -ComObject WScript.Shell
    foreach ($scLink in @('_test-shortcuts\Switchcraft.lnk', '_test-desktop\Switchcraft.lnk')) {
        $scShortcut = $scShell.CreateShortcut((Join-Path $scSandbox $scLink))
        Check ('Shortcut target: ' + $scLink) ($scShortcut.TargetPath -eq (Join-Path $scSandbox 'Switchcraft.exe'))
    }
    $scEngineReport = Join-Path $scRoot 'tests\installer-engine-results.json'
    $scCode = Run-Installer (Join-Path $scSandbox 'Switchcraft.exe') ('--self-test "' + $scEngineReport + '"')
    $scEngine = Get-Content -LiteralPath $scEngineReport -Raw | ConvertFrom-Json
    Check 'Installed application passes engine self-tests' ($scCode -eq 0 -and $scEngine.failed -eq 0) "$($scEngine.passed)/$($scEngine.total)"
    $scSentinel = Join-Path $scSandbox 'user-created-note.txt'
    [IO.File]::WriteAllText($scSentinel, 'Installer must preserve unrelated user files.')
    $scCode = Run-Installer $scSetup ('/S /LAYA /D=' + $scSandbox)
    Check 'Reinstall succeeds' ($scCode -eq 0)
    $scLaya = Join-Path $env:LOCALAPPDATA 'Switchcraft\Laya'
    $scLayaReady = (Test-Path -LiteralPath (Join-Path $scLaya 'model\rl_agent_config.json')) -and (Test-Path -LiteralPath (Join-Path $scLaya 'venv\Scripts\python.exe'))
    Check 'Silent /LAYA requests Laya only when it is not prepared yet' ((Get-Content -LiteralPath $scIni -Raw).Contains('LayaRequested=1') -eq (-not $scLayaReady)) "Laya prepared: $scLayaReady"
    Start-Sleep -Milliseconds 500
    Check 'Installer test build never starts Switchcraft' (@(Get-Process -Name Switchcraft -ErrorAction SilentlyContinue | Where-Object { $_.Path -like ($scSandbox + '\*') }).Count -eq 0)
    Check 'Reinstall preserves unrelated files' ((Get-Content -LiteralPath $scSentinel -Raw) -eq 'Installer must preserve unrelated user files.')
    Set-ItemProperty -LiteralPath $scTestKey -Name DisplayVersion -Value '99.0.0'
    $scCode = Run-Installer $scSetup ('/S /D=' + $scSandbox)
    Check 'Installer blocks version downgrade' ($scCode -eq 1638) "Exit code $scCode"
    Set-ItemProperty -LiteralPath $scTestKey -Name DisplayVersion -Value $scVersion
    # Validate the exact deletion target before invoking the generated uninstaller.
    $scInstallMarker = Get-Content -LiteralPath (Join-Path $scSandbox 'switchcraft-install.ini') -Raw
    Check 'Uninstaller marker is tied to the verified test directory' ($scInstallMarker.Contains('Directory=' + $scSandbox))
    # Like NSIS's normal temporary copy, run the identical uninstaller from outside
    # the installation directory so it can delete its installed executable.
    $scUninstaller = Join-Path $scRoot 'tests\installer-test-uninstall.exe'
    Copy-Item -LiteralPath (Join-Path $scSandbox 'Uninstall.exe') -Destination $scUninstaller
    $scMutex = [Threading.Mutex]::new($false, 'Local\Switchcraft.Desktop')
    try {
        $scCode = Run-Installer $scUninstaller ('/S _?=' + $scSandbox)
        Check 'Uninstall refuses an active app' ($scCode -eq 1618) "Exit code $scCode"
    } finally { $scMutex.Dispose() }
    $scCode = Run-Installer $scUninstaller ('/S _?=' + $scSandbox)
    Check 'Silent uninstall succeeds' ($scCode -eq 0) "Exit code $scCode"
    Check 'Uninstall removes application payload' (-not (Test-Path -LiteralPath (Join-Path $scSandbox 'Switchcraft.exe')))
    Check 'Uninstall removes registration' (-not (Test-Path -LiteralPath $scTestKey))
    Check 'Uninstall removes its shortcuts' (-not (Test-Path -LiteralPath (Join-Path $scSandbox '_test-shortcuts\Switchcraft.lnk')) -and -not (Test-Path -LiteralPath (Join-Path $scSandbox '_test-desktop\Switchcraft.lnk')))
    Check 'Uninstall preserves unrelated user files' ((Get-Content -LiteralPath $scSentinel -Raw) -eq 'Installer must preserve unrelated user files.')
    Check 'Personal settings and user rules are unchanged' ((Get-SettingsSnapshot) -eq $scSettingsBefore)
    $scRealAfter = Get-RealRegistration
    Check 'Existing real installation registration is unchanged' ($scRealAfter -eq $scRealBefore)
    $scDesktopAfter = if (Test-Path -LiteralPath $scDesktopLink) { (Get-FileHash -LiteralPath $scDesktopLink).Hash } else { '' }
    Check 'Existing real desktop shortcut is unchanged' ($scDesktopAfter -eq $scDesktopHash)
} catch { $scError = $_.ToString() }
finally {
    if ($scRunning.Count -gt 0) { Start-Process -FilePath $scProduction -ArgumentList '--minimized' -WindowStyle Hidden }
    $scReport = [ordered]@{ timestamp = (Get-Date).ToString('o'); version = $scVersion; total = $scChecks.Count; passed = @($scChecks | Where-Object passed).Count; failed = @($scChecks | Where-Object { -not $_.passed }).Count; error = $scError; checks = $scChecks }
    $scReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $scRoot 'tests\installer-results.json') -Encoding utf8
    [pscustomobject]$scReport | Select-Object total,passed,failed,error | ConvertTo-Json
    if (-not $scError -and (Test-Path -LiteralPath $scSandbox)) {
        # The only survivor is the sentinel this test created; never recurse.
        $scOwnSentinel = Join-Path $scSandbox 'user-created-note.txt'
        if ((Test-Path -LiteralPath $scOwnSentinel) -and (Get-Content -LiteralPath $scOwnSentinel -Raw) -eq 'Installer must preserve unrelated user files.') { Remove-Item -LiteralPath $scOwnSentinel }
        if (@(Get-ChildItem -LiteralPath $scSandbox -Force).Count -eq 0) { Remove-Item -LiteralPath $scSandbox }
    }
}
if ($scError) { throw $scError }
