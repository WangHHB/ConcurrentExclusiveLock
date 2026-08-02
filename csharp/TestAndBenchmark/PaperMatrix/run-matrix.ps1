param(
    [Parameter(Mandatory = $true)][string]$DotNet,
    [Parameter(Mandatory = $true)][string]$BenchmarkDll,
    [Parameter(Mandatory = $true)][string]$Matrix,
    [Parameter(Mandatory = $true)][string]$MachineId,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory, (Join-Path $OutputDirectory 'logs') | Out-Null
$jsonl = Join-Path $OutputDirectory 'results.jsonl'
$commands = Join-Path $OutputDirectory 'commands.txt'
Set-Content -Path $commands -Value ''

function Require-Value([string]$Name, [string]$Value, [string]$Id) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq '-') {
        throw "Matrix row '$Id' requires column '$Name'."
    }
}

$rows = Import-Csv -Path $Matrix -Delimiter "`t"
foreach ($row in $rows) {
    if ($row.enabled -ne '1') { continue }
    $id = $row.id
    $mode = $row.mode
    Require-Value 'id' $id $id
    Require-Value 'mode' $mode $id
    $a = [System.Collections.Generic.List[string]]::new()

    switch ($mode) {
        { $_ -in @('throughput', 'latency') } {
            foreach ($name in @('lock_instances','threads','operations','concurrent_permille','workload','concurrent_work','exclusive_work')) {
                Require-Value $name $row.$name $id
            }
            $a.Add("--$mode"); $a.Add('--lock-instances'); $a.Add($row.lock_instances)
            $a.Add('--threads'); $a.Add($row.threads); $a.Add('--operations'); $a.Add($row.operations)
            $a.Add('--concurrent-permille'); $a.Add($row.concurrent_permille); $a.Add('--workload'); $a.Add($row.workload)
            $a.Add('--concurrent-work'); $a.Add($row.concurrent_work); $a.Add('--exclusive-work'); $a.Add($row.exclusive_work)
            switch ($row.workload) {
                'cpu' { }
                'memory' { Require-Value 'memory_mb' $row.memory_mb $id; $a.Add('--memory-mb'); $a.Add($row.memory_mb) }
                { $_ -in @('dictionary','ledger') } { Require-Value 'dictionary_size' $row.dictionary_size $id; $a.Add('--dictionary-size'); $a.Add($row.dictionary_size); break }
                'payload' { Require-Value 'payload_frames' $row.payload_frames $id; $a.Add('--payload-frames'); $a.Add($row.payload_frames) }
                default { throw "Unknown workload '$($row.workload)' in matrix row '$id'." }
            }
            if ($mode -eq 'latency') { Require-Value 'latency_sample_every' $row.latency_sample_every $id; $a.Add('--latency-sample-every'); $a.Add($row.latency_sample_every) }
            break
        }
        'exclusive-progress' {
            foreach ($name in @('lock_instances','threads','operations','workload','concurrent_work','exclusive_work')) { Require-Value $name $row.$name $id }
            $a.Add('--exclusive-progress'); $a.Add('--lock-instances'); $a.Add($row.lock_instances); $a.Add('--threads'); $a.Add($row.threads)
            $a.Add('--operations'); $a.Add($row.operations); $a.Add('--workload'); $a.Add($row.workload)
            $a.Add('--concurrent-work'); $a.Add($row.concurrent_work); $a.Add('--exclusive-work'); $a.Add($row.exclusive_work)
            switch ($row.workload) {
                'cpu' { }
                'memory' { Require-Value 'memory_mb' $row.memory_mb $id; $a.Add('--memory-mb'); $a.Add($row.memory_mb) }
                { $_ -in @('dictionary','ledger') } { Require-Value 'dictionary_size' $row.dictionary_size $id; $a.Add('--dictionary-size'); $a.Add($row.dictionary_size); break }
                'payload' { Require-Value 'payload_frames' $row.payload_frames $id; $a.Add('--payload-frames'); $a.Add($row.payload_frames) }
                default { throw "Unknown workload '$($row.workload)' in matrix row '$id'." }
            }
            break
        }
        'pipeline-perf' {
            foreach ($name in @('lock_instances','threads','operations','prepare_work','commit_work','post_work')) { Require-Value $name $row.$name $id }
            $a.Add('--pipeline-perf'); $a.Add('--lock-instances'); $a.Add($row.lock_instances); $a.Add('--threads'); $a.Add($row.threads)
            $a.Add('--operations'); $a.Add($row.operations); $a.Add('--prepare-work'); $a.Add($row.prepare_work)
            $a.Add('--commit-work'); $a.Add($row.commit_work); $a.Add('--post-work'); $a.Add($row.post_work)
            break
        }
        'upgrade-contention' {
            foreach ($name in @('lock_instances','upgrade_n','upgrade_m')) { Require-Value $name $row.$name $id }
            $a.Add('--upgrade-contention'); $a.Add($row.upgrade_n); $a.Add($row.upgrade_m)
            $a.Add('--lock-instances'); $a.Add($row.lock_instances)
            break
        }
        'correctness' {
            foreach ($name in @('lock_instances','semantic_workers','semantic_operations','advanced_operations')) { Require-Value $name $row.$name $id }
            $a.Add('--correctness'); $a.Add('--lock-instances'); $a.Add($row.lock_instances)
            $a.Add('--semantic-workers'); $a.Add($row.semantic_workers); $a.Add('--semantic-operations'); $a.Add($row.semantic_operations)
            $a.Add('--advanced-operations'); $a.Add($row.advanced_operations)
            if ($row.semantic_seed -ne '-') { $a.Add('--semantic-seed'); $a.Add($row.semantic_seed) }
            if ($row.advanced_seed -ne '-') { $a.Add('--advanced-seed'); $a.Add($row.advanced_seed) }
            if ($row.pipeline_exception_permille -ne '-') { $a.Add('--pipeline-exception-permille'); $a.Add($row.pipeline_exception_permille) }
            break
        }
        { $_ -in @('pipeline-stress', 'endurance') } {
            foreach ($name in @('duration','lock_instances','semantic_workers','semantic_operations')) { Require-Value $name $row.$name $id }
            $a.Add("--$mode"); $a.Add($row.duration); $a.Add('--lock-instances'); $a.Add($row.lock_instances)
            $a.Add('--semantic-workers'); $a.Add($row.semantic_workers); $a.Add('--semantic-operations'); $a.Add($row.semantic_operations)
            if ($row.semantic_seed -ne '-') { $a.Add('--semantic-seed'); $a.Add($row.semantic_seed) }
            if ($mode -eq 'pipeline-stress') { Require-Value 'pipeline_exception_permille' $row.pipeline_exception_permille $id; $a.Add('--pipeline-exception-permille'); $a.Add($row.pipeline_exception_permille) }
            break
        }
        'contention-diagnostic' {
            foreach ($name in @('duration','threads')) { Require-Value $name $row.$name $id }
            $a.Add('--contention-diagnostic'); $a.Add($row.duration); $a.Add('--threads'); $a.Add($row.threads)
            break
        }
        default { throw "Unknown mode '$mode' in matrix row '$id'." }
    }

    $a.Add('--machine-id'); $a.Add($MachineId); $a.Add('--experiment-id'); $a.Add($id); $a.Add('--output'); $a.Add($jsonl)
    $printable = '& ' + ('"{0}" ' -f $DotNet) + ('"{0}" ' -f $BenchmarkDll) + (($a | ForEach-Object { '"{0}"' -f $_ }) -join ' ')
    Add-Content -Path $commands -Value $printable
    & $DotNet $BenchmarkDll @a 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory "logs/$id.log")
    if ($LASTEXITCODE -ne 0) { throw "Matrix row '$id' failed with exit code $LASTEXITCODE." }
}
