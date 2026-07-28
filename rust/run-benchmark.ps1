[CmdletBinding()]
param(
    [int]$LockInstances = 8,
    [int]$ThreadsPerLock = 8,
    [int]$Operations = 100000,
    [int]$MemoryMB = 64,
    [int]$ReadWork = 640,
    [int]$WriteWork = 640,
    [string]$OutputFile = ''
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
$cargo = if ($cargoCommand) { $cargoCommand.Source } else { Join-Path $HOME '.cargo\bin\cargo.exe' }
if (-not (Test-Path $cargo) -and -not $cargoCommand) {
    throw "Cargo was not found. Install Rust with rustup and reopen PowerShell."
}

$arguments = @(
    'run', '--release', '--offline', '-p', 'cel-test-and-benchmark', '--',
    '--lock-instances', $LockInstances,
    '--threads', $ThreadsPerLock,
    '--operations', $Operations,
    '--workload', 'memory',
    '--memory-mb', $MemoryMB,
    '--read-work', $ReadWork,
    '--write-work', $WriteWork
)

if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    & $cargo @arguments
} else {
    $parent = Split-Path -Parent $OutputFile
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item $parent -ItemType Directory -Force | Out-Null
    }
    & $cargo @arguments 2>&1 | Tee-Object $OutputFile
}
