param([string]$OutputName = 'stress-3000-final')
$ErrorActionPreference = 'Stop'
if ($OutputName -notmatch '^[a-zA-Z0-9-]+$') { throw 'Use a simple report directory name.' }
$scRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scProduction = Join-Path $scRoot 'dist\Switchcraft\Switchcraft.exe'
$scTest = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\Switchcraft.StressTest.exe'
$scCorpus = Join-Path $scRoot 'tests\stress-corpus-3000.txt'
$scOutput = Join-Path $scRoot ('tests\' + $OutputName)
$scRunning = @(Get-Process -Name Switchcraft -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $scProduction })
$scRunning | Stop-Process
try {
    $scProcess = Start-Process -FilePath $scTest -ArgumentList ('"' + $scCorpus + '"'),('"' + $scOutput + '"') -WindowStyle Normal -PassThru
    Write-Output ('Stress process: ' + $scProcess.Id)
    $scProcess.WaitForExit()
    Write-Output ('Exit code: ' + $scProcess.ExitCode)
    Get-Content -LiteralPath (Join-Path $scOutput 'progress.json')
} finally {
    if ($scRunning.Count -gt 0) {
        Start-Process -FilePath $scProduction -ArgumentList '--minimized' -WindowStyle Hidden
        Write-Output 'Production Switchcraft restarted.'
    }
}
