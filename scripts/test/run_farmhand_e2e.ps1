$ErrorActionPreference = "Stop"

. "$PSScriptRoot\..\lib\paths.ps1"
$gameDir = Get-GamePath
# 测试存档名不硬编码（存档名含 Steam 账号数字，属个人信息）；用 VALLEY_TEST_SAVE 覆盖。
$SaveName = if ($env:VALLEY_TEST_SAVE) { $env:VALLEY_TEST_SAVE } else { "TestSave_Main" }
$smapi = "$gameDir\StardewModdingAPI.exe"
$logDir = Join-Path $env:APPDATA "StardewValley\ErrorLogs"
$hostLog = Join-Path $logDir "SMAPI-latest.txt"
$farmhandLog = Join-Path $logDir "SMAPI-latest.player-2.txt"
$testModDir = "$gameDir\Mods\ValleyAgent.TestMod"
$hostCmdFile = "$testModDir\test_commands_host.txt"
$farmhandCmdFile = "$testModDir\test_commands_farmhand.txt"
$videoPath = "$gameDir\farmhand_test_$(Get-Date -Format yyyyMMdd_HHmmss).mp4"

function Wait-LogLine {
    param([string]$Path, [string]$Pattern, [int]$TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) {
            try {
                $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8)
                $text = $sr.ReadToEnd()
                $sr.Close()
                $fs.Close()
                if ($text -match $Pattern) { return $true }
            }
            catch { }
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections;
public class Win32 {
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError=true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const int SW_RESTORE = 9;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public static int ScreenWidth { get { return GetSystemMetrics(SM_CXSCREEN); } }
    public static int ScreenHeight { get { return GetSystemMetrics(SM_CYSCREEN); } }

    public static string GetWindowTitle(IntPtr hWnd) {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, 256);
        return sb.ToString();
    }

    public static ArrayList FindWindowsByProcessId(int processId) {
        var result = new ArrayList();
        EnumWindows((hWnd, lParam) => {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if ((int)pid == processId) {
                result.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

function Start-GameInstance {
    param([string]$InstanceName)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $smapi
    $psi.WorkingDirectory = $gameDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.EnvironmentVariables["VALLEY_TEST_INSTANCE"] = $InstanceName
    $proc = [System.Diagnostics.Process]::Start($psi)
    return $proc
}

function Wait-MainWindow {
    param([System.Diagnostics.Process]$proc, [int]$timeoutSeconds = 60)
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $proc.Refresh()
    }
    return $proc.MainWindowHandle
}

function Find-GameWindow {
    param([System.Diagnostics.Process]$proc, [int]$timeoutSeconds = 60)
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        # Prefer EnumWindows matching by PID (SMAPI-launched windows often have Process.MainWindowHandle == 0)
        $handles = [Win32]::FindWindowsByProcessId($proc.Id)
        foreach ($h in $handles) {
            $title = [Win32]::GetWindowTitle($h)
            if ($title -like "*Stardew Valley*" -or $title -like "*SMAPI*") {
                return $h
            }
        }
        # Fallback to .NET MainWindowHandle
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0 -and [Win32]::IsWindow($proc.MainWindowHandle)) {
            return $proc.MainWindowHandle
        }
        Start-Sleep -Milliseconds 500
    }
    # Last resort: any visible window
    $handles = [Win32]::FindWindowsByProcessId($proc.Id)
    if ($handles.Count -gt 0) { return $handles[0] }
    return 0
}

function Set-GameWindowLayout {
    param([System.Diagnostics.Process]$hostProc, [System.Diagnostics.Process]$farmhandProc)
    $hostHwnd = Find-GameWindow -proc $hostProc -timeoutSeconds 60
    $farmhandHwnd = Find-GameWindow -proc $farmhandProc -timeoutSeconds 60
    $width = 640
    $height = 480

    # Shrink both windows and place them in the bottom-right corner to avoid occupying the center of the main screen.
    # Hard-coded for bottom-right of a 2048x1152 primary screen to avoid PowerShell variable scoping issues.
    $rightX = 768
    $rightY = 672

    # Use SW_SHOWNOACTIVATE + HWND_BOTTOM + SWP_NOACTIVATE to keep windows visible but never steal focus,
    # avoiding interference with the user's work on the main screen.
    $layoutFlags = [uint32]0x50
    if ($hostHwnd -ne 0) {
        [Win32]::ShowWindow($hostHwnd, [Win32]::SW_SHOWNOACTIVATE) | Out-Null
        [Win32]::SetWindowPos($hostHwnd, [Win32]::HWND_BOTTOM, $rightX, $rightY, $width, $height, $layoutFlags) | Out-Null
    }
    if ($farmhandHwnd -ne 0) {
        [Win32]::ShowWindow($farmhandHwnd, [Win32]::SW_SHOWNOACTIVATE) | Out-Null
        [Win32]::SetWindowPos($farmhandHwnd, [Win32]::HWND_BOTTOM, $rightX + $width, $rightY, $width, $height, $layoutFlags) | Out-Null
    }

    Write-Host "Window layout: host=$hostHwnd farmhand=$farmhandHwnd (screen $([Win32]::ScreenWidth)x$([Win32]::ScreenHeight))"

    if ($hostHwnd -ne 0) {
        Write-Host "  host title: $([Win32]::GetWindowTitle($hostHwnd))"
    }
    if ($farmhandHwnd -ne 0) {
        Write-Host "  farmhand title: $([Win32]::GetWindowTitle($farmhandHwnd))"
    }
}

Write-Host "Cleaning old SMAPI logs..."
Remove-Item $hostLog, $farmhandLog -ErrorAction SilentlyContinue

# V3TestRunner 自动测试会占住主机实例并在完成后退出游戏（"All tests complete. Exiting."），
# 与 E2E 联机流程冲突。强制 manual——TestMod 构建部署会把 src 的 v3 默认值拷回来，故每次跑前重设。
# 文本替换而非 JSON 解析（PS5.1 ConvertFrom-Json 不支持 // 注释，会破坏文件）。
$testCfg = "$testModDir\test_config.json"
if (Test-Path $testCfg) {
    $cfgText = Get-Content $testCfg -Raw -Encoding UTF8
    if ($cfgText -notmatch '"runner":\s*"manual"') {
        $cfgText = $cfgText -replace '"runner":\s*"[^"]*"', '"runner": "manual"'
        [System.IO.File]::WriteAllText($testCfg, $cfgText, (New-Object System.Text.UTF8Encoding $false))
        Write-Host "test_config.json runner set to manual (E2E)"
    }
}

# 强退的游戏进程可能让 AutoLoadGame 清空 LastFileLoaded → host 无档可载。每次跑前恢复。
$autoLoadCfg = "$gameDir\Mods\AutoLoadGame\config.json"
if (Test-Path $autoLoadCfg) {
    $cfgText = Get-Content $autoLoadCfg -Raw -Encoding UTF8
    $cfgText = $cfgText -replace '"LastFileLoaded"\s*:\s*("[^"]*"|null)', "`"LastFileLoaded`": `"$SaveName`""
    [System.IO.File]::WriteAllText($autoLoadCfg, $cfgText, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "AutoLoadGame LastFileLoaded restored"
}

Write-Host "Starting desktop recording: $videoPath"
# Record the two 640x480 windows side-by-side in the bottom-right corner (768,672)-(2048,1152).
# Use ArgumentList array and set max recording duration to avoid PowerShell string escaping issues.
$ffmpegArgs = @("-y", "-f", "gdigrab", "-framerate", "10", "-offset_x", "768", "-offset_y", "672", "-video_size", "1280x480", "-i", "desktop", "-pix_fmt", "yuv420p", "-t", "420", $videoPath)
Write-Host "ffmpegArgs count = $($ffmpegArgs.Count)"
$ffmpeg = Start-Process -FilePath "ffmpeg" -ArgumentList $ffmpegArgs -WindowStyle Minimized -PassThru

try {
    Write-Host "Starting host instance..."
    $hostProc = Start-GameInstance -InstanceName "host"
    Write-Host "Host PID: $($hostProc.Id)"

    Write-Host "Waiting for host save load..."
    if (-not (Wait-LogLine -Path $hostLog -Pattern "ValleyAgent TestMod\] Save loaded" -TimeoutSeconds 180)) {
        throw "Host did not load save in time."
    }

    # Ensure CommandFileWatcher is ready before writing the command file.
    Start-Sleep -Seconds 3

    Write-Host "Issuing host server command..."
    Set-Content -Path $hostCmdFile -Value "va_mp_host" -Encoding UTF8 -NoNewline

    Write-Host "Waiting for host multiplayer server..."
    if (-not (Wait-LogLine -Path $hostLog -Pattern "MP\] Hosted multiplayer server" -TimeoutSeconds 120)) {
        throw "Host server did not start in time."
    }

    # Give host a moment to finish server setup
    Start-Sleep -Seconds 3

    Write-Host "Starting farmhand instance..."
    $farmhandProc = Start-GameInstance -InstanceName "farmhand"
    Write-Host "Farmhand PID: $($farmhandProc.Id)"

    Write-Host "Waiting for farmhand TestMod load..."
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "ValleyAgent TestMod\] ValleyAgent TestMod loaded" -TimeoutSeconds 180)) {
        throw "Farmhand did not start in time."
    }

    # Ensure farmhand CommandFileWatcher is ready.
    Start-Sleep -Seconds 3

    Write-Host "Issuing farmhand join command..."
    Set-Content -Path $farmhandCmdFile -Value "va_mp_join" -Encoding UTF8 -NoNewline

    Write-Host "Waiting for farmhand join..."
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "MP\] Joined game and activated farmhand" -TimeoutSeconds 180)) {
        throw "Farmhand did not join in time."
    }

    Start-Sleep -Seconds 5

    # Arrange windows so both host and farmhand are visible in the recording.
    Set-GameWindowLayout -hostProc $hostProc -farmhandProc $farmhandProc
    Start-Sleep -Seconds 2

    Write-Host "Allocating Haley as Agent (before C1 — checkAction needs Agent)..."
    Set-Content -Path $hostCmdFile -Value "va_test_alloc Haley" -Encoding UTF8 -NoNewline
    if (-not (Wait-LogLine -Path $hostLog -Pattern "\[Alloc\] Haley allocated" -TimeoutSeconds 60)) {
        throw "Haley allocation failed."
    }
    Start-Sleep -Seconds 3

    Write-Host "Running C1..."
    Set-Content -Path $farmhandCmdFile -Value "va_test_c1 Haley" -Encoding UTF8 -NoNewline
    Start-Sleep -Seconds 15

    Write-Host "Running C2..."
    Set-Content -Path $farmhandCmdFile -Value "va_test_c2 Haley 74" -Encoding UTF8 -NoNewline
    Start-Sleep -Seconds 25

    Write-Host "Running C3..."
    # RunC3 defaults to Chinese message when no message arg is provided, avoiding encoding issues in this script file.
    Set-Content -Path $farmhandCmdFile -Value "va_test_c3 Haley" -Encoding UTF8 -NoNewline
    Start-Sleep -Seconds 60

    Write-Host "Running C4 (M1 multiplayer adjust: farmhand wallet +50 / host unchanged)..."
    # 主机侧注入带房客 playerId 的 execute_adjust（va_test_c4 <npc>），断言钱落在房客钱包。
    Set-Content -Path $hostCmdFile -Value "va_test_c4 Haley" -Encoding UTF8 -NoNewline
    Start-Sleep -Seconds 20

    Write-Host "Running C5 (M2: host player dialogue — two players talk to Haley)..."
    # 房客已通过 C3 对话（player=房客 ID）；主机再对话一次 → agents/Haley_players/ 应有两个 rel 文件。
    Set-Content -Path $hostCmdFile -Value "va_test_c3 Haley 晚上好呀" -Encoding UTF8 -NoNewline
    Start-Sleep -Seconds 30

    Write-Host "Test sequence complete."
}
finally {
    Write-Host "Stopping recording..."
    # ffmpeg max duration is set via -t; terminate directly if still running.
    if ($ffmpeg -and -not $ffmpeg.HasExited) {
        $ffmpeg.Kill()
        $ffmpeg.WaitForExit(5000)
    }

    Write-Host "Stopping game instances..."
    if ($hostProc -and -not $hostProc.HasExited) {
        $hostProc.Kill()
        $hostProc.WaitForExit(10000)
    }
    if ($farmhandProc -and -not $farmhandProc.HasExited) {
        $farmhandProc.Kill()
        $farmhandProc.WaitForExit(10000)
    }

    Write-Host ""
    Write-Host "Artifacts:"
    Write-Host "  Video:    $videoPath"
    Write-Host "  Host log: $hostLog"
    Write-Host "  Farmhand: $farmhandLog"
}
