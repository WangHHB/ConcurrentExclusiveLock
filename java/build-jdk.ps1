$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$BuildRoot = Join-Path $Root ".build-jdk"
$CoreOut = Join-Path $BuildRoot "core"
$TestOut = Join-Path $BuildRoot "test"
$CombinedOut = Join-Path $BuildRoot "combined"
$CoreTarget = Join-Path $Root "ConcurrentExclusiveLock\target"
$TestTarget = Join-Path $Root "TestAndBenchmark\target"

Remove-Item $BuildRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item $CoreOut, $TestOut, $CombinedOut, $CoreTarget, $TestTarget -ItemType Directory -Force | Out-Null

$CoreSources = Get-ChildItem `
    (Join-Path $Root "ConcurrentExclusiveLock\src\main\java") `
    -Recurse -Filter *.java | ForEach-Object FullName

$TestSources = Get-ChildItem `
    (Join-Path $Root "TestAndBenchmark\src\main\java") `
    -Recurse -Filter *.java | ForEach-Object FullName

& javac --release 17 -Xlint:all -d $CoreOut $CoreSources
if ($LASTEXITCODE -ne 0) { throw "Core javac failed with exit code $LASTEXITCODE" }

& javac --release 17 -Xlint:all -cp $CoreOut -d $TestOut $TestSources
if ($LASTEXITCODE -ne 0) { throw "TestAndBenchmark javac failed with exit code $LASTEXITCODE" }

$CoreJar = Join-Path $CoreTarget "concurrent-exclusive-lock-1.0.0-SNAPSHOT.jar"
& jar --create --file $CoreJar -C $CoreOut .
if ($LASTEXITCODE -ne 0) { throw "Core jar creation failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $CoreOut "*") $CombinedOut -Recurse -Force
Copy-Item (Join-Path $TestOut "*") $CombinedOut -Recurse -Force

$TestJar = Join-Path $TestTarget "TestAndBenchmark.jar"
& jar --create `
    --file $TestJar `
    --main-class io.github.wanghhb.concurrentexclusivelock.testandbenchmark.TestAndBenchmark `
    -C $CombinedOut .
if ($LASTEXITCODE -ne 0) { throw "TestAndBenchmark jar creation failed with exit code $LASTEXITCODE" }

& java -jar $TestJar --help
if ($LASTEXITCODE -ne 0) { throw "TestAndBenchmark --help failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Created:"
Write-Host "  $CoreJar"
Write-Host "  $TestJar"
