<#
.SYNOPSIS
  Measures a browser's whole process tree at idle: working set, private bytes and CPU.

.DESCRIPTION
  Each run starts the browser with a fresh temporary profile, waits -Settle seconds, optionally minimizes it,
  then samples CPU over -Sample seconds and memory at the end. The root process and every descendant are counted.
  Results are written as JSON plus a markdown summary (median, min, max).
  -Warm launches once first and closes, so first-run work (extension install, profile creation) isn't measured.
  -Cover puts a full-screen window (standing in for a game) in front for -AfterCover seconds before and during the sample.

.EXAMPLE
  ./tools/measure.ps1 -Exe .\out\Lilt\Lilt.exe
  ./tools/measure.ps1 -Exe .\out\Lilt\Lilt.exe -Minimize -Label "minimized"
  ./tools/measure.ps1 -Browser edge -Exe "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
  ./tools/measure.ps1 -Browser firefox -Exe "C:\Program Files\Mozilla Firefox\firefox.exe"
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [ValidateSet('lilt', 'chromium', 'edge', 'helium', 'firefox')][string]$Browser = 'lilt',
    [string]$Url = 'about:blank',
    [int]$Runs = 5,
    [int]$Settle = 30,
    [int]$Sample = 60,
    [switch]$Minimize,
    [int]$AfterMinimize = 10,
    [switch]$Warm,
    [switch]$Cover,
    [int]$AfterCover = 20,
    [string]$Label = '',
    [string]$Out = 'measure-results.json',
    [string[]]$ExtraArgs = @(),
    [string]$Theme = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -Namespace Lilt -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int cmd); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'

function Get-Tree([int]$rootId) {
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine)
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($rootId)
    do {
        $added = $false
        foreach ($p in $all) { if ($ids.Contains([int]$p.ParentProcessId) -and $ids.Add([int]$p.ProcessId)) { $added = $true } }
    } while ($added)
    foreach ($p in $all) {
        if (-not $ids.Contains([int]$p.ProcessId)) { continue }
        $proc = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
        if (-not $proc) { continue }
        $type = if ($p.CommandLine -match '--type=([\w-]+)') { $Matches[1] } elseif ($p.CommandLine -match '-contentproc') { 'content' } else { 'main' }
        [pscustomobject]@{
            Id = [int]$p.ProcessId; Name = $p.Name; Type = $type
            WorkingSet = $proc.WorkingSet64; Private = $proc.PrivateMemorySize64; Cpu = $proc.TotalProcessorTime.TotalMilliseconds
        }
    }
}

function Stop-Tree([int]$rootId) {
    $ids = @(Get-Tree $rootId | ForEach-Object Id)
    $root = Get-Process -Id $rootId -ErrorAction SilentlyContinue
    if ($root) { [void]$root.CloseMainWindow(); [void]$root.WaitForExit(10000) }
    foreach ($id in $ids) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
}

function Get-Median([double[]]$v) {
    $s = $v | Sort-Object
    if ($s.Count % 2) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 }
}

$exePath = (Resolve-Path $Exe).Path
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$wv2 = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue).pv
$conditions = [ordered]@{
    label = $Label; browser = $Browser; exe = $exePath; version = (Get-Item $exePath).VersionInfo.ProductVersion; url = $Url
    os = "$($os.Caption) $($os.Version)"; cpu = $cpu.Name.Trim(); logicalCores = [Environment]::ProcessorCount
    ramGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1); powerPlan = ((powercfg /getactivescheme) -replace '^.*\((.*)\).*$', '$1')
    webView2Runtime = $wv2; runs = $Runs; settleSeconds = $Settle; sampleSeconds = $Sample; minimized = [bool]$Minimize
    date = (Get-Date).ToString('o')
}

$results = @()
for ($run = 1; $run -le $Runs; $run++) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("lilt-measure-" + [guid]::NewGuid())
    New-Item -ItemType Directory $dir | Out-Null
    $startup = Join-Path $dir 'startup.json'
    $argList = switch ($Browser) {
        'lilt' { @('--user-data', "`"$dir`"", '--startup-report', "`"$startup`"", '--url', $Url) + $(if ($Theme) { @('--theme', "`"$Theme`"") } else { @() }) + $ExtraArgs }
        'firefox' {
            Set-Content (Join-Path $dir 'user.js') @(
                'user_pref("browser.aboutwelcome.enabled", false);'
                'user_pref("browser.startup.homepage_override.mstone", "ignore");'
                'user_pref("datareporting.policy.dataSubmissionPolicyBypassNotification", true);'
                'user_pref("browser.shell.checkDefaultBrowser", false);')
            @('-no-remote', '-profile', "`"$dir`"", $Url)
        }
        default { @("--user-data-dir=`"$dir`"", '--no-first-run', '--no-default-browser-check', $Url) }
    }
    if ($Warm) {
        $w = Start-Process -FilePath $exePath -ArgumentList $argList -PassThru
        Start-Sleep -Seconds 12
        Stop-Tree $w.Id
        Remove-Item $startup -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    $p = Start-Process -FilePath $exePath -ArgumentList $argList -PassThru
    Start-Sleep -Seconds 3
    $p.Refresh()
    [void][Lilt.Native]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Seconds ([math]::Max(0, $Settle - 3))
    $coverProc = $null
    if ($Cover) {
        $coverFile = Join-Path $dir 'cover.ps1'
        Set-Content $coverFile @'
Add-Type -AssemblyName System.Windows.Forms
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = 'None'; $f.WindowState = 'Maximized'; $f.TopMost = $true; $f.BackColor = 'Black'; $f.Text = 'Game'
[System.Windows.Forms.Application]::Run($f)
'@
        $coverProc = Start-Process pwsh -ArgumentList '-NoProfile', '-File', "`"$coverFile`"" -PassThru
        Start-Sleep -Seconds $AfterCover
    }
    if ($Minimize) {
        $p.Refresh()
        [void][Lilt.Native]::ShowWindow($p.MainWindowHandle, 6)
        Start-Sleep -Seconds $AfterMinimize
    }
    $before = @(Get-Tree $p.Id)
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $Sample
    $after = @(Get-Tree $p.Id)
    $wall = $t0.Elapsed.TotalMilliseconds
    # Per process: new processes count from zero; ones that exited mid-sample drop out instead of making the total negative.
    $start = @{}; foreach ($b in $before) { $start[$b.Id] = $b.Cpu }
    $cpuDelta = 0.0; foreach ($a in $after) { $cpuDelta += $a.Cpu - $(if ($start.ContainsKey($a.Id)) { $start[$a.Id] } else { 0 }) }
    $byType = [ordered]@{}
    foreach ($g in ($after | Group-Object { "$($_.Name)/$($_.Type)" } | Sort-Object Name)) {
        $byType[$g.Name] = [ordered]@{ count = $g.Count; workingSetMB = [math]::Round(($g.Group | Measure-Object WorkingSet -Sum).Sum / 1MB, 1); privateMB = [math]::Round(($g.Group | Measure-Object Private -Sum).Sum / 1MB, 1) }
    }
    $r = [ordered]@{
        run = $run
        processes = $after.Count
        workingSetMB = [math]::Round(($after | Measure-Object WorkingSet -Sum).Sum / 1MB, 1)
        privateMB = [math]::Round(($after | Measure-Object Private -Sum).Sum / 1MB, 1)
        cpuPercentOfOneCore = [math]::Round(100 * $cpuDelta / $wall, 2)
        byProcess = $byType
    }
    if ($coverProc) { Stop-Process -Id $coverProc.Id -Force -ErrorAction SilentlyContinue }
    Stop-Tree $p.Id
    if (Test-Path $startup) { $r.startup = Get-Content $startup -Raw | ConvertFrom-Json }
    if (Test-Path "$startup.keys.txt") { $r.power = @(Get-Content "$startup.keys.txt" | Where-Object { $_ -match ' power ' }) }
    $results += [pscustomobject]$r
    Write-Host ("run {0}: {1} MB working set, {2} MB private, {3} processes, {4}% of one core" -f $run, $r.workingSetMB, $r.privateMB, $r.processes, $r.cpuPercentOfOneCore)
    Start-Sleep -Seconds 2
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
}

function Stat([string]$name, [double[]]$v, [string]$unit) {
    '| {0} | {1:N1} {4} | {2:N1} | {3:N1} |' -f $name, (Get-Median $v), ($v | Measure-Object -Minimum).Minimum, ($v | Measure-Object -Maximum).Maximum, $unit
}
$summary = @(
    "### $Browser $Label".Trim()
    ''
    "$($conditions.os) · $($conditions.cpu) · $($conditions.logicalCores) logical cores · $($conditions.ramGB) GB · power plan $($conditions.powerPlan)"
    "WebView2 runtime $wv2 · $Runs runs · settle $Settle s · CPU sampled over $Sample s · minimized: $([bool]$Minimize) · warm start: $([bool]$Warm) · covered: $([bool]$Cover) · $Url"
    ''
    '| Metric | Median | Min | Max |'
    '|---|---|---|---|'
    (Stat 'Working set (tree)' ($results | ForEach-Object workingSetMB) 'MB')
    (Stat 'Private bytes (tree)' ($results | ForEach-Object privateMB) 'MB')
    (Stat 'Processes' ($results | ForEach-Object processes) '')
    (Stat 'CPU, % of one core' ($results | ForEach-Object cpuPercentOfOneCore) '%')
)
$starts = @($results | Where-Object { $_.startup } | ForEach-Object startup)
if ($starts.Count) {
    $summary += (Stat 'Start to window shown' ($starts | ForEach-Object processToWindowShownMs) 'ms')
    $summary += (Stat 'Start to first page loaded' ($starts | ForEach-Object processToFirstNavigationMs) 'ms')
}
$summary -join "`n" | Tee-Object -FilePath ([IO.Path]::ChangeExtension($Out, '.md'))
[ordered]@{ conditions = $conditions; runs = $results } | ConvertTo-Json -Depth 8 | Set-Content $Out
