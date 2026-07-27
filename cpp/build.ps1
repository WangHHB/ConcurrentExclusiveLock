param(
    [string]$BuildDirectory = "build",
    [ValidateSet("Debug", "Release", "RelWithDebInfo", "MinSizeRel")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE: $Command $($Arguments -join ' ')"
    }
}

Push-Location $PSScriptRoot
try {
    Invoke-CheckedNative -Command "cmake" -Arguments @(
        "-S", ".",
        "-B", $BuildDirectory,
        "-DCMAKE_BUILD_TYPE=$Configuration")
    Invoke-CheckedNative -Command "cmake" -Arguments @(
        "--build", $BuildDirectory,
        "--config", $Configuration)
    Invoke-CheckedNative -Command "ctest" -Arguments @(
        "--test-dir", $BuildDirectory,
        "-C", $Configuration,
        "--output-on-failure")
}
finally {
    Pop-Location
}
