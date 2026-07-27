$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "ConcurrentExclusiveLock.sln"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found. Install Visual Studio 2026 with Desktop development with C++."
}
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild was not found. Install the Visual Studio C++ workload."
}
& $msbuild $solution /m /restore /p:Configuration=Release /p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $root\bin\x64\Release\TestAndBenchmark.exe"
