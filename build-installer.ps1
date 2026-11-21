# Runs the tests, then builds dist\RemotePlayBlanker-Setup-<version>.exe
#   .\build-installer.ps1                 # version from the .csproj
#   .\build-installer.ps1 -Version 1.2.3  # override (CI uses the git tag)
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet test -c Release
if ($LASTEXITCODE) { throw "Tests failed" }

$props = @()
if ($Version) { $props += "-p:Version=$Version" }
dotnet publish src\RemotePlayBlanker\RemotePlayBlanker.csproj -c Release -o publish @props
if ($LASTEXITCODE) { throw "dotnet publish failed" }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc /Qp installer\RemotePlayBlanker.iss
if ($LASTEXITCODE) { throw "ISCC failed" }
Get-ChildItem dist\*.exe | Sort-Object LastWriteTime | Select-Object -Last 1 Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
