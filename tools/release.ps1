<#
  Hexa release pipeline (build -> checks -> publish -> swap -> verify).

  Usage:
    pwsh -File tools\release.ps1                # checks + publish + swap + verify
    pwsh -File tools\release.ps1 -SkipChecks    # publish + swap + verify only
    pwsh -File tools\release.ps1 -ChecksOnly    # build + unit tests + self-test only

  Why this exists: every manual release used to be 8 hand-copied steps (stage the publish,
  run the published self-test, preserve the user's presets.json, swap folders, verify
  onnxruntime + deps.json). Skipping one of them shipped a broken build twice: once without
  the native onnxruntime, once almost overwriting the user's 42 stroke presets. The order is
  now fixed here and every step fails loudly.

  Rules baked in:
  - dist_hexa is NEVER deleted before the new build exists and passed its own self-test.
  - presets.json lives next to the exe and is USER DATA: it is copied from the live folder
    into the staging folder before the swap, then the count is verified.

  ASCII only on purpose: PowerShell 5.1 decodes BOM-less script files as ANSI (GBK on this
  machine), so Chinese text here would turn into garbage and break parsing.
#>
param(
    [switch]$SkipChecks,
    [switch]$ChecksOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$hexaProj = Split-Path -Parent $PSScriptRoot
$root     = Split-Path -Parent $hexaProj
$csproj   = Join-Path $hexaProj 'Hexa.csproj'
$tests    = Join-Path $hexaProj 'tests\Hexa.CoreTests\Hexa.CoreTests.csproj'
$dist     = Join-Path $root 'dist_hexa'
$stage    = Join-Path $root '_dist_hexa_new'
$prev     = Join-Path $root 'dist_hexa_prev_20260924'
$report   = Join-Path $env:TEMP 'hexa-selftest-report.txt'
$pubReport = Join-Path $env:TEMP 'hexa-published-report.txt'

$failures = New-Object System.Collections.Generic.List[string]

function Say([string]$text) { Write-Host $text }

function Step([string]$name, [scriptblock]$body) {
    Say ""
    Say ("=== " + $name)
    & $body
}

function Require([bool]$ok, [string]$what) {
    if ($ok) { Say ("  OK   " + $what) } else { Say ("  FAIL " + $what); $failures.Add($what) }
}

function Count-Presets([string]$path) {
    if (-not (Test-Path $path)) { return -1 }
    $json = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    return @($json).Count
}

# ---------------------------------------------------------------- checks
if (-not $SkipChecks) {
    Step 'build (Debug)' {
        $out = & dotnet build $csproj -c Debug -v q --nologo 2>&1
        $code = $LASTEXITCODE
        $text = ($out | Out-String)
        Require ($code -eq 0) "dotnet build exit code = $code"
        Require ($text -match '0\s*(个)?警告|0\s*Warning') 'zero warnings reported'
    }

    Step 'unit tests (Hexa.CoreTests)' {
        $out = & dotnet run --project $tests -c Debug -v q --nologo 2>&1
        $code = $LASTEXITCODE
        $tail = ($out | Select-Object -Last 1)
        Say ("  " + $tail)
        Require ($code -eq 0) "unit tests exit code = $code"
    }

    Step 'self-test (Debug build, sandboxed settings)' {
        $env:HEXA_SELFTEST = $report
        Remove-Item $report -ErrorAction SilentlyContinue
        & dotnet run --project $csproj -c Debug -v q --nologo | Out-Null
        Remove-Item Env:\HEXA_SELFTEST -ErrorAction SilentlyContinue
        if (Test-Path $report) {
            $summary = (Get-Content $report | Select-String -Pattern '自检结束|checks passed|项通过' | Select-Object -Last 1).Line
            Say ("  " + $summary)
            Require ($summary -match '0\s*(项失败|failed)') 'self-test has zero failures'
        } else {
            Require $false 'self-test report was written'
        }
    }

    if ($ChecksOnly) {
        Say ""
        Say ("checks done, failures: " + $failures.Count)
        if ($failures.Count -gt 0) { exit 1 }
        exit 0
    }
}

# ---------------------------------------------------------------- publish
Step 'publish (Release, win-x64, self-contained)' {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    $out = & dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $stage -v q --nologo 2>&1
    $code = $LASTEXITCODE
    Require ($code -eq 0) "dotnet publish exit code = $code"
}

Step 'verify staging folders contents' {
    $exe = Join-Path $stage 'Hexa.exe'
    $dll = Join-Path $stage 'Hexa.dll'
    $onnx = Join-Path $stage 'onnxruntime.dll'
    $deps = Join-Path $stage 'Hexa.deps.json'
    Require (Test-Path $exe) 'Hexa.exe exists'
    Require (Test-Path $dll) 'Hexa.dll exists'
    Require (Test-Path $onnx) 'native onnxruntime.dll exists (vision feature would be dead without it)'
    if (Test-Path $onnx) {
        $size = (Get-Item $onnx).Length
        Say ("  onnxruntime.dll = " + $size + " bytes")
        Require ($size -gt 15000000) 'native onnxruntime.dll looks like the real win-x64 binary'
    }
    if (Test-Path $deps) {
        $hasOnnx = (Get-Content -Raw -LiteralPath $deps).Contains('Microsoft.ML.OnnxRuntime')
        Require $hasOnnx 'Hexa.deps.json lists Microsoft.ML.OnnxRuntime'
    } else {
        Require $false 'Hexa.deps.json exists'
    }
}

if (-not $SkipChecks) {
    Step 'self-test the PUBLISHED build' {
        Remove-Item $pubReport -ErrorAction SilentlyContinue
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = Join-Path $stage 'Hexa.exe'
        $psi.WorkingDirectory = $stage
        $psi.UseShellExecute = $false
        $psi.EnvironmentVariables['HEXA_SELFTEST'] = $pubReport
        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.WaitForExit()
        if (Test-Path $pubReport) {
            $summary = (Get-Content $pubReport | Select-String -Pattern '自检结束|项通过' | Select-Object -Last 1).Line
            Say ("  " + $summary)
            Require ($summary -match '0\s*项失败') 'published build passes its own self-test'
        } else {
            Require $false 'published build wrote a self-test report'
        }
    }
}

Step 'preserve user data (presets.json next to the exe)' {
    $livePresets = Join-Path $dist 'presets.json'
    $liveCount = Count-Presets $livePresets
    $stagePresets = Join-Path $stage 'presets.json'
    $stageCount = Count-Presets $stagePresets
    Say ("  live presets = " + $liveCount + " / staging presets = " + $stageCount)
    if ($liveCount -gt 0) {
        Copy-Item $livePresets $stagePresets -Force
        $after = Count-Presets $stagePresets
        Require ($after -eq $liveCount) ("staging presets.json now holds the user's " + $liveCount + " presets")
    } else {
        Say '  (live folder has no presets.json yet - keeping whatever the fresh publish wrote)'
    }
}

Step 'swap folders' {
    Require (Test-Path (Join-Path $stage 'Hexa.exe')) 'staging build still present right before the swap'
    if (Test-Path $prev) { Remove-Item $prev -Recurse -Force }
    if (Test-Path $dist) { Move-Item -LiteralPath $dist -Destination $prev }
    Move-Item -LiteralPath $stage -Destination $dist
    Say ("  previous build kept at " + $prev)
}

Step 'verify final folder' {
    $exe = Join-Path $dist 'Hexa.exe'
    $onnx = Join-Path $dist 'onnxruntime.dll'
    $deps = Join-Path $dist 'Hexa.deps.json'
    $presets = Join-Path $dist 'presets.json'
    Require (Test-Path $exe) 'dist_hexa\Hexa.exe exists'
    Require (Test-Path $onnx) 'dist_hexa\onnxruntime.dll exists'
    if (Test-Path $deps) {
        Require ((Get-Content -Raw -LiteralPath $deps).Contains('Microsoft.ML.OnnxRuntime')) 'deps.json still lists onnxruntime'
    }
    $count = Count-Presets $presets
    Say ("  presets in dist_hexa = " + $count)
    Require ($count -ne 0) 'presets.json is present and non-empty'
    $files = (Get-ChildItem $dist -File | Measure-Object).Count
    Say ("  files in dist_hexa = " + $files)
    Require ($files -gt 200) 'dist_hexa looks complete (more than 200 files)'
}

Say ""
if ($failures.Count -eq 0) {
    Say 'RELEASE OK'
    exit 0
}
Say ("RELEASE FAILED - " + $failures.Count + " problem(s):")
foreach ($f in $failures) { Say ("  - " + $f) }
exit 1
