<#
  Samples CPU and memory of the BaronDesk processes and writes a CSV plus a summary.

  Usage (PowerShell, from the folder containing this file):
    Set-ExecutionPolicy -Scope Process Bypass
    .\measure-agent.ps1 -Scenario idle-locked -DurationMinutes 30

  - Add -StopAtEnd to close the measured programs when the run ends. Use it when the
    lock overlay covers your screen: the overlay then disappears by itself at the end.

  - Edit -ProcessNames if your exe names differ (Task Manager > Details tab, without ".exe").
  - Run PowerShell as Administrator if Service Core runs as a Windows service (LocalSystem).
  - CpuPercent is the share of the WHOLE machine (all cores), not of one core.
  - PrivateMB is the private memory of the process, the number to report for RAM.
#>
param(
    [string[]]$ProcessNames = @('BaronDeskAgent.ServiceCore', 'BaronDesk.LockUI'),
    [string]$Scenario = 'idle-locked',
    [int]$DurationMinutes = 30,
    [int]$IntervalSeconds = 5,
    [string]$OutDir = '.\bench',
    [switch]$StopAtEnd
)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$cores = [Environment]::ProcessorCount
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csvPath = Join-Path $OutDir "$Scenario-$stamp.csv"
$summaryPath = Join-Path $OutDir "$Scenario-$stamp-summary.csv"
$rows = New-Object System.Collections.Generic.List[object]
$previous = @{}
$endTime = (Get-Date).AddMinutes($DurationMinutes)

function Get-Percentile([double[]]$Values, [double]$Percent) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $index = [math]::Ceiling($Percent / 100 * $sorted.Count) - 1
    return $sorted[[math]::Max(0, $index)]
}

Write-Host "Scenario '$Scenario': $DurationMinutes min, one sample every $IntervalSeconds s, $cores logical cores."
Write-Host "Raw samples go to $csvPath. Ctrl+C stops early and still prints the summary."

try {
    while ((Get-Date) -lt $endTime) {
        $now = Get-Date

        foreach ($name in $ProcessNames) {
            $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
            if ($procs.Count -eq 0) {
                Write-Warning "$name is not running."
                continue
            }

            $cpuSeconds = 0.0
            $privateBytes = 0.0
            $workingSetBytes = 0.0
            $threads = 0
            $handles = 0
            $readable = $true

            foreach ($p in $procs) {
                try {
                    $cpuSeconds += $p.TotalProcessorTime.TotalSeconds
                    $privateBytes += $p.PrivateMemorySize64
                    $workingSetBytes += $p.WorkingSet64
                    $threads += $p.Threads.Count
                    $handles += $p.HandleCount
                }
                catch {
                    $readable = $false
                }
            }

            if (-not $readable) {
                Write-Warning "Cannot read $name. Run PowerShell as Administrator for the service."
                continue
            }

            if ($previous.ContainsKey($name)) {
                $elapsed = ($now - $previous[$name].Time).TotalSeconds
                $cpuDelta = $cpuSeconds - $previous[$name].Cpu

                # A restarted process resets its CPU counter: skip that sample.
                if ($elapsed -gt 0 -and $cpuDelta -ge 0) {
                    $row = [pscustomobject]@{
                        Time         = $now.ToString('s')
                        Scenario     = $Scenario
                        Process      = $name
                        CpuPercent   = [math]::Round(100 * $cpuDelta / ($elapsed * $cores), 3)
                        PrivateMB    = [math]::Round($privateBytes / 1MB, 1)
                        WorkingSetMB = [math]::Round($workingSetBytes / 1MB, 1)
                        Threads      = $threads
                        Handles      = $handles
                    }
                    $rows.Add($row)
                    $row | Export-Csv -Path $csvPath -Append -NoTypeInformation
                }
            }

            $previous[$name] = @{ Cpu = $cpuSeconds; Time = $now }
        }

        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    if ($StopAtEnd) {
        Write-Host 'Stopping the measured processes...'
        foreach ($name in $ProcessNames) {
            Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
        }
    }

    if ($rows.Count -eq 0) {
        Write-Warning 'No samples collected. Check the process names and that the processes are running.'
    }
    else {
        $summary = foreach ($group in ($rows | Group-Object Process)) {
            $cpu = [double[]]@($group.Group | ForEach-Object { $_.CpuPercent })
            $mem = [double[]]@($group.Group | ForEach-Object { $_.PrivateMB })
            $edge = [math]::Max(1, [math]::Floor($mem.Count / 10))
            $startAvg = ($mem | Select-Object -First $edge | Measure-Object -Average).Average
            $endAvg = ($mem | Select-Object -Last $edge | Measure-Object -Average).Average

            [pscustomobject]@{
                Scenario        = $Scenario
                Process         = $group.Name
                Samples         = $cpu.Count
                CpuAvgPercent   = [math]::Round(($cpu | Measure-Object -Average).Average, 3)
                CpuP95Percent   = Get-Percentile $cpu 95
                CpuMaxPercent   = ($cpu | Measure-Object -Maximum).Maximum
                PrivateMBAvg    = [math]::Round(($mem | Measure-Object -Average).Average, 1)
                PrivateMBMax    = ($mem | Measure-Object -Maximum).Maximum
                PrivateMBGrowth = [math]::Round($endAvg - $startAvg, 1)
            }
        }

        $summary | Export-Csv -Path $summaryPath -NoTypeInformation
        $summary | Format-Table -AutoSize
        Write-Host "Summary saved to $summaryPath"
    }
}