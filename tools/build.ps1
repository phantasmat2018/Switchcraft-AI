param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$scRoot = Split-Path -Parent $PSScriptRoot
$scProject = Join-Path $scRoot 'src\Switchcraft.csproj'
$scOutput = Join-Path $scRoot 'dist\Switchcraft'
if ($SelfContained) {
    dotnet publish $scProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o $scOutput --nologo
} else {
    dotnet publish $scProject -c Release -r win-x64 --self-contained false -p:DebugType=none -o $scOutput --nologo
}
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $scRoot 'README.md') -Destination $scOutput
Copy-Item -LiteralPath (Join-Path $scRoot 'THIRD-PARTY.md') -Destination $scOutput
Write-Output "Ready: $scOutput\Switchcraft.exe"
