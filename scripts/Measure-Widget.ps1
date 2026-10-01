param(
    [Parameter(Mandatory=$true)][string]$ExePath,
    [ValidateRange(1,10000)][int]$StartupCount=30,
    [ValidateRange(0,86400)][int]$WarmupSeconds=600,
    [ValidateRange(1,86400)][int]$MeasureSeconds=1800,
    [switch]$SoftwareRendering,
    [switch]$ExerciseUi
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe=[IO.Path]::GetFullPath($ExePath)
if (-not $exe.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Executable must be in workspace.' }
if (-not (Test-Path -LiteralPath $exe)) { throw 'Executable missing.' }
$runDir=Join-Path $root ('artifacts\measure-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runDir | Out-Null
$startupCsv=Join-Path $runDir 'startup.csv'
$cpuCsv=Join-Path $runDir 'cpu.csv'
function Q([string]$s) { '"'+$s.Replace('"','\"')+'"' }
function Stop-Owned($p) {
    if ($null -ne $p) { $p.Refresh(); if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
}
function Start-Widget([string]$name,[int]$exitMs) {
    $settings=Join-Path $runDir "$name.settings.json"
    $ready=Join-Path $runDir "$name.ready.json"
    Set-Content -LiteralPath $settings -Value '{}' -Encoding utf8
    $appArgs=@('--ready-file',(Q $ready),'--settings',(Q $settings),'--exit-after-ms',"$exitMs")
    if ($SoftwareRendering) { $appArgs += '--software-rendering' }
    if ($ExerciseUi -and $name -eq 'cpu') { $appArgs += '--exercise-ui' }
    $arguments=$appArgs -join ' '
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $p=Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Hidden -PassThru
    try {
        while ($watch.Elapsed.TotalSeconds -lt 60) {
            if (Test-Path -LiteralPath $ready) {
                try {
                    $json=Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
                    if ($json.processId -eq $p.Id) { return [pscustomobject]@{Process=$p;ReadyMs=$watch.Elapsed.TotalMilliseconds} }
                } catch { }
            }
            $p.Refresh(); if ($p.HasExited) { throw 'App exited before ready.' }
            Start-Sleep -Milliseconds 50
        }
        throw 'Readiness timeout.'
    } catch { Stop-Owned $p; throw }
}
function Add-Csv([string]$path,$row) { $row | Export-Csv -LiteralPath $path -NoTypeInformation -Append }
function Stats([double[]]$values) {
    $s=@($values | Sort-Object); $n=$s.Count
    if ($n -eq 0) { return $null }
    $median=if ($n%2 -eq 0) { ($s[$n/2-1]+$s[$n/2])/2 } else { $s[[int][Math]::Floor($n/2)] }
    [pscustomobject]@{Count=$n;Median=$median;P95=$s[[Math]::Max(0,[int][Math]::Ceiling(.95*$n)-1)];Max=$s[$n-1]}
}
$startup=[Collections.Generic.List[double]]::new()
Write-Output "Run directory: $runDir"
for ($i=1;$i -le $StartupCount;$i++) {
    $launch=Start-Widget "startup-$i" 1000
    try {
        $p=$launch.Process; $p.Refresh()
        $startup.Add([double]$launch.ReadyMs)
        Add-Csv $startupCsv ([pscustomobject]@{Sample=$i;ReadyElapsedMs=$launch.ReadyMs;ProcessId=$p.Id;WorkingSetBytes=$p.WorkingSet64;PrivateBytes=$p.PrivateMemorySize64})
        if (-not $p.WaitForExit(15000)) { throw 'Startup sample did not exit.' }
        if ($p.ExitCode -ne 0) { throw 'Startup sample exited with error.' }
    } finally { Stop-Owned $launch.Process }
}
$logical=[Environment]::ProcessorCount
$cpuValues=[Collections.Generic.List[double]]::new()
$ws=[Collections.Generic.List[double]]::new()
$private=[Collections.Generic.List[double]]::new()
$cpuLaunch=$null; $machineCounter=$null; $failure=$null; $status='failed'
$measurementStartAt=$null; $measurementStartCpu=$null; $measurementEndAt=$null; $measurementEndCpu=$null
$childStart=$null; $childEnd=$null; $counterNote=$null
$uiExerciseTicks=$null; $uiExerciseCycles=$null
try {
    $cpuLaunch=Start-Widget 'cpu' (($WarmupSeconds+$MeasureSeconds+120)*1000)
    $p=$cpuLaunch.Process
    $childStart=@(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($p.Id)").Count
    if ($childStart -ne 0) { throw 'Owned child processes require tree sampling.' }
    try { $machineCounter=[Diagnostics.PerformanceCounter]::new('Processor','% Processor Time','_Total'); $null=$machineCounter.NextValue() }
    catch { $counterNote=$_.Exception.Message; $machineCounter=$null }
    $clock=[Diagnostics.Stopwatch]::StartNew(); $previousAt=0.0
    $p.Refresh(); $previousCpu=$p.TotalProcessorTime.TotalSeconds
    for ($i=1;$i -le ($WarmupSeconds+$MeasureSeconds);$i++) {
        while ($clock.Elapsed.TotalSeconds -lt $i) { Start-Sleep -Milliseconds 100 }
        $p.Refresh(); if ($p.HasExited) { throw 'App exited during sampling.' }
        $at=$clock.Elapsed.TotalSeconds; $cpuNow=$p.TotalProcessorTime.TotalSeconds
        $wallDelta=$at-$previousAt
        $normalized=100*($cpuNow-$previousCpu)/$wallDelta/$logical
        $totalMachine=$null
        if ($null -ne $machineCounter) { $totalMachine=$machineCounter.NextValue() }
        $phase=if ($i -le $WarmupSeconds) { 'warmup' } else { 'measurement' }
        if ($phase -eq 'measurement') {
            if ($null -eq $measurementStartAt) { $measurementStartAt=$previousAt; $measurementStartCpu=$previousCpu }
            $cpuValues.Add($normalized); $measurementEndAt=$at; $measurementEndCpu=$cpuNow
        }
        $ws.Add([double]$p.WorkingSet64); $private.Add([double]$p.PrivateMemorySize64)
        Add-Csv $cpuCsv ([pscustomobject]@{Sample=$i;Phase=$phase;ElapsedSeconds=$at;ProcessCpuSeconds=$cpuNow;MachineNormalizedProcessPercent=$normalized;TotalMachinePercent=$totalMachine;WorkingSetBytes=$p.WorkingSet64;PrivateBytes=$p.PrivateMemorySize64})
        $previousAt=$at; $previousCpu=$cpuNow
    }
    $childEnd=@(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($p.Id)").Count
    if ($childEnd -ne 0) { throw 'Owned child process detected.' }
    if (-not $p.WaitForExit(150000)) { throw 'Exit timer did not complete.' }
    if ($ExerciseUi) {
        $markerPath=Join-Path $runDir 'cpu.ready.json.exercise-complete'
        if (-not (Test-Path -LiteralPath $markerPath)) { throw 'UI exercise completion marker missing.' }
        $markerTicks=0
        if (-not [int]::TryParse([IO.File]::ReadAllText($markerPath),[ref]$markerTicks) -or $markerTicks -ne 60) { throw 'UI exercise completion marker did not report 60 ticks.' }
        $uiExerciseTicks=$markerTicks; $uiExerciseCycles=[int]($markerTicks/2)
    }
    if ($p.ExitCode -ne 0) { throw 'App exited with error.' }
    $status='completed'
} catch { $failure=$_.Exception.Message }
finally { if ($null -ne $cpuLaunch) { Stop-Owned $cpuLaunch.Process }; if ($null -ne $machineCounter) { $machineCounter.Dispose() } }
$weighted=if ($null -ne $measurementEndAt -and $measurementEndAt -gt $measurementStartAt) { 100*($measurementEndCpu-$measurementStartCpu)/($measurementEndAt-$measurementStartAt)/$logical } else { $null }
$notes=@('Warm file caches; cold launches unmeasured.','Ready marker follows rendered widget, WPF automation invoke, tray registration and programmatic tray menu opening; physical tray click unmeasured.','CPU is exact app PID; child process counts checked at start/end.','No monitoring loop exists in fixture-only Milestone 1.')
if ($ExerciseUi) { $notes += 'Exercise enabled: finite 30 card open/close pairs at 500 ms intervals after readiness.' }
$summary=[pscustomobject]@{
    Executable=$exe;RunDirectory=$runDir;Status=$status;Failure=$failure
    RenderingPreference='SoftwareOnly'
    UiExerciseEnabled=[bool]$ExerciseUi;UiExerciseTicks=$uiExerciseTicks;UiExerciseCycles=$uiExerciseCycles
    Notes=$notes
    Hardware=[pscustomobject]@{LogicalProcessors=$logical;Processor=(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfLogicalProcessors);OS=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,TotalVisibleMemorySize)}
    WarmStartupMilliseconds=(Stats ($startup.ToArray()))
    WarmupSeconds=$WarmupSeconds;RequestedMeasurementSeconds=$MeasureSeconds;CompletedMeasurementSamples=$cpuValues.Count
    ActualMeasurementSeconds=if ($null -ne $measurementEndAt) {$measurementEndAt-$measurementStartAt} else {$null}
    WeightedMachineNormalizedCpuPercent=$weighted;CpuSamplePercent=(Stats ($cpuValues.ToArray()))
    WorkingSetBytes=(Stats ($ws.ToArray()));PrivateBytes=(Stats ($private.ToArray()))
    ChildCountAtStart=$childStart;ChildCountAtEnd=$childEnd;MachineCounterNote=$counterNote
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDir 'summary.json') -Encoding utf8
$summary | Select-Object Status,Failure,WarmStartupMilliseconds,WeightedMachineNormalizedCpuPercent,WorkingSetBytes,PrivateBytes | ConvertTo-Json -Depth 5
if ($status -ne 'completed') { throw "Measurement failed: $failure" }
