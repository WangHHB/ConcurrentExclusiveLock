$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "bin\x64\Release\TestAndBenchmark.exe"
if (-not (Test-Path $exe)) {
    throw "TestAndBenchmark.exe was not found. Open ConcurrentExclusiveLock.sln, select Release | x64, then Build Solution; or run .\build-vs.ps1."
}
& $exe `
    --lock-instances 8 `
    --threads 8 `
    --workload memory `
    --operations 100000 `
    --memory-mb 64 `
    --read-work 640 `
    --write-work 640
