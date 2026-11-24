# 热更：盯住 .cs / .xaml 改动 → 存盘后自动编译 → 自动重启 Hexa。
# 用途：改界面/逻辑代码时不用手动走「关掉 Hexa → 编译 → 重新打开」这套流程，
#       脚本替你做完「编译 + 杀旧进程 + 起新进程」，界面上就是闪一下。
#
# 为什么不用每次先手动关 Hexa：
#   .NET 10 运行中的进程不会锁死自己的 exe / DLL 文件，dotnet build 能在窗口还开着时
#   直接覆盖 bin\Release 里的产物；只是正在跑的那份还是旧代码。所以脚本在编译通过后
#   再 Stop-Process + Start-Process，用新产物拉起全新进程。手动关窗口省不掉这一步，
#   只会多一次点鼠标。
#
# 用法：
#   pwsh -File tools\hot.ps1              # 一直盯着：存盘 → 编译 → 重启
#   pwsh -File tools\hot.ps1 -Once        # 编译（并按需重启）一次就退出
#   pwsh -File tools\hot.ps1 -NoRun       # 只编译，不杀也不启动正在跑的窗口
#   pwsh -File tools\hot.ps1 -Debounce 1200   # 存盘后等 1200ms 再编译
param(
    # -Once：编译（并按需重启）一次后退出，不进盯文件循环。
    [switch]$Once,
    # -NoRun：只编译、不杀进程也不启动，用来单独验证「能不能编译过」。
    [switch]$NoRun,
    # -Debounce：检测到文件改动后先等这么久（毫秒），把连续保存收成一次编译。默认 800。
    [int]$Debounce = 800
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'Hexa.csproj'
$exe  = Join-Path $root 'bin\Release\net10.0-windows10.0.26100.0\Hexa.exe'

function Write-Step([string]$text) {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $text) -ForegroundColor Cyan
}

function Get-NewestStamp {
    $newest = [datetime]::MinValue
    Get-ChildItem -Path $root -Recurse -File -Include *.cs, *.xaml -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object { if ($_.LastWriteTimeUtc -gt $newest) { $newest = $_.LastWriteTimeUtc } }
    return $newest
}

function Invoke-Build {
    Write-Step '编译中…'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $out = & dotnet build $proj -c Release --nologo -v minimal 2>&1
    $sw.Stop()
    $text = ($out | Out-String)
    $errors = ($out | Select-String -Pattern ': error ' | Measure-Object).Count
    $warns  = ($out | Select-String -Pattern ': warning ' | Measure-Object).Count
    if ($errors -gt 0 -or -not (Test-Path $exe)) {
        # 完整输出先落一份到 %TEMP%\hexa-hot-build.log（控制台只给前 8 条，方便事后翻完整错误）。
        $log = Join-Path $env:TEMP 'hexa-hot-build.log'
        try { $text | Set-Content -LiteralPath $log -Encoding UTF8 } catch { }
        Write-Host ("编译失败（{0:N1}s，{1} 个错）——正在跑的那份不动，完整输出已写 {2}：" -f $sw.Elapsed.TotalSeconds, $errors, $log) -ForegroundColor Red
        ($out | Select-String -Pattern ': error ' | Select-Object -First 8) | ForEach-Object { Write-Host ("  " + $_.Line) -ForegroundColor DarkRed }
        return $false
    }
    Write-Step ("编译通过 {0:N1}s · 警告 {1}" -f $sw.Elapsed.TotalSeconds, $warns)
    return $true
}

function Restart-Hexa {
    if ($NoRun) { Write-Step '（-NoRun）不启动程序'; return }
    $running = Get-Process Hexa -ErrorAction SilentlyContinue
    if ($running) {
        Write-Step ("关掉正在跑的 Hexa（PID {0}）" -f ($running.Id -join ','))
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 600
    }
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) | Out-Null
    Write-Step '新版本已启动'
}

Write-Step ("项目：{0}" -f $proj)
if (-not (Invoke-Build)) { exit 1 }
Restart-Hexa
if ($Once) { exit 0 }

$stamp = Get-NewestStamp
Write-Step '盯着源码，存盘就重来（Ctrl+C 退出）'
while ($true) {
    Start-Sleep -Milliseconds 400
    $now = Get-NewestStamp
    if ($now -le $stamp) { continue }
    Start-Sleep -Milliseconds $Debounce          # 收拢连续保存
    $stamp = Get-NewestStamp
    if (Invoke-Build) { Restart-Hexa }
}
