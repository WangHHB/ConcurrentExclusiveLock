[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& .\build.ps1
Write-Host 'Windows executable copied to Artifacts\windows-x64\cel-test-and-benchmark.exe'
