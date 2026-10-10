param([switch]$InputIntegration, [string]$Executable)
$ErrorActionPreference = 'Stop'
$scRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) {
    dotnet build (Join-Path $scRoot 'src\Switchcraft.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $Executable = Join-Path $scRoot 'src\bin\Release\net8.0-windows\Switchcraft.exe'
}
$scMode = if ($InputIntegration) { '--input-self-test' } else { '--self-test' }
$scReport = Join-Path $scRoot $(if ($InputIntegration) { 'tests\input-results.json' } else { 'tests\engine-results.json' })
$scWindow = if ($InputIntegration) { 'Normal' } else { 'Hidden' }
$scProcess = Start-Process -FilePath $Executable -ArgumentList $scMode,('"' + $scReport + '"') -WindowStyle $scWindow -PassThru -Wait
$scResult = Get-Content -LiteralPath $scReport -Raw | ConvertFrom-Json
$scResult | Select-Object total,passed,failed
if ($scProcess.ExitCode -ne 0 -or $scResult.failed -ne 0) { throw "Tests failed. Report: $scReport" }
