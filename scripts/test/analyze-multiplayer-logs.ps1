#requires -Version 5.1
<#
.SYNOPSIS
    分析双端联机 SMAPI 日志，验证 C1/C2/C3 修复是否生效。

.DESCRIPTION
    读取主机和客机 SMAPI 日志，检测关键事件并给出 verdict。
    支持 SMAPI-latest.txt 以及普通 ErrorLogs 目录下的 SMAPI 日志。

.PARAMETER HostLog
    主机 SMAPI 日志路径。默认 %APPDATA%\StardewValley\ErrorLogs\SMAPI-latest.txt。

.PARAMETER FarmhandLog
    客机 SMAPI 日志路径。默认 %APPDATA%\StardewValley\ErrorLogs\SMAPI-latest.txt。

.PARAMETER Verbose
    输出详细匹配行。

.EXAMPLE
    .\analyze-multiplayer-logs.ps1 -HostLog "C:\Logs\host-SMAPI-latest.txt" -FarmhandLog "C:\Logs\farmhand-SMAPI-latest.txt"
#>
[CmdletBinding()]
param(
    [string]$HostLog = (Join-Path $env:AppData "StardewValley\ErrorLogs\SMAPI-latest.txt"),
    [string]$FarmhandLog = (Join-Path $env:AppData "StardewValley\ErrorLogs\SMAPI-latest.txt")
)

$ErrorActionPreference = "Stop"
$isVerbose = $PSBoundParameters['Verbose'] -or $VerbosePreference -eq 'Continue'

# 提示：直接运行会查找默认 SMAPI 日志；建议双端实测后分别指定 -HostLog 和 -FarmhandLog
if (-not $PSBoundParameters.ContainsKey('HostLog') -and -not $PSBoundParameters.ContainsKey('FarmhandLog')) {
    Write-Host "提示: 未指定日志路径，将自动查找默认 SMAPI 日志位置。" -ForegroundColor DarkGray
    Write-Host "       双端验收时请分别提供主机和客机日志以获得准确 verdict。" -ForegroundColor DarkGray
    Write-Host "示例: .\analyze-multiplayer-logs.ps1 -HostLog C:\Logs\host-SMAPI-latest.txt -FarmhandLog C:\Logs\farmhand-SMAPI-latest.txt -Verbose" -ForegroundColor DarkGray
}

# ── Helpers ──
function Write-CheckHeader {
    param([string]$Label)
    Write-Host ""
    Write-Host "=== $Label ===" -ForegroundColor Cyan
}

function Write-Verdict {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("PASS", "FAIL", "WARN")]
        [string]$Verdict,
        [string]$Message
    )
    $colorMap = @{ "PASS" = "Green"; "FAIL" = "Red"; "WARN" = "Yellow" }
    $color = $colorMap[$Verdict]
    Write-Host "  [$Verdict] $Message" -ForegroundColor $color
    return $Verdict
}

function Find-DefaultLogPath {
    param([string]$ExplicitPath)
    if (Test-Path $ExplicitPath -PathType Leaf) { return $ExplicitPath }

    $candidateDirs = @(
        (Join-Path $env:AppData "StardewValley\ErrorLogs"),
        (Join-Path $env:USERPROFILE "AppData\Roaming\StardewValley\ErrorLogs"),
        (Join-Path $env:USERPROFILE "StardewValley\ErrorLogs")
    )
    foreach ($dir in $candidateDirs) {
        if (-not (Test-Path $dir -PathType Container)) { continue }
        $latest = Get-ChildItem -Path $dir -Filter "SMAPI-*.txt" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latest) { return $latest.FullName }
    }
    return $null
}

function Read-LogLines {
    param([string]$Path)
    $lines = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path $Path -PathType Leaf)) { return ,$lines }
    try {
        $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8)
        while ($null -ne ($line = $sr.ReadLine())) {
            $lines.Add($line)
        }
        $sr.Close()
        $fs.Close()
    } catch {
        Write-Host "  读取日志失败: $($_.Exception.Message)" -ForegroundColor Red
    }
    return ,$lines
}

function Search-Lines {
    param(
        [string[]]$Lines,
        [string[]]$Patterns
    )
    $matchResults = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        foreach ($pattern in $Patterns) {
            if ($Lines[$i] -match $pattern) {
                $matchResults.Add([PSCustomObject]@{ LineNumber = $i + 1; Pattern = $pattern; Text = $Lines[$i] })
                break
            }
        }
    }
    Write-Output -NoEnumerate $matchResults
}

function Test-ExplicitMarker {
    param(
        [string[]]$Lines,
        [string]$Case
    )
    $passPattern = "\[$Case\]\s*PASS"
    $failPattern = "\[$Case\]\s*FAIL"
    $passMatches = Search-Lines -Lines $Lines -Patterns @($passPattern)
    $failMatches = Search-Lines -Lines $Lines -Patterns @($failPattern)
    return [PSCustomObject]@{
        PassCount = $passMatches.Count
        FailCount = $failMatches.Count
        PassLines = $passMatches
        FailLines = $failMatches
    }
}

# ── Resolve paths ──
$resolvedHostLog = Find-DefaultLogPath -ExplicitPath $HostLog
$resolvedFarmhandLog = Find-DefaultLogPath -ExplicitPath $FarmhandLog

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  双端联机 SMAPI 日志分析器" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "解析日志:" -ForegroundColor Gray

if ($resolvedHostLog) {
    Write-Host "  主机:  $resolvedHostLog" -ForegroundColor Gray
} else {
    Write-Host "  主机:  未找到日志文件 (输入: $HostLog)" -ForegroundColor Yellow
}

if ($resolvedFarmhandLog) {
    Write-Host "  客机:  $resolvedFarmhandLog" -ForegroundColor Gray
} else {
    Write-Host "  客机:  未找到日志文件 (输入: $FarmhandLog)" -ForegroundColor Yellow
}

if (-not $resolvedHostLog -and -not $resolvedFarmhandLog) {
    Write-Host ""
    Write-Host "未找到任何 SMAPI 日志文件。" -ForegroundColor Red
    Write-Host "请确认:" -ForegroundColor Gray
    Write-Host "  1. SMAPI 已运行并生成日志。" -ForegroundColor Gray
    Write-Host "  2. 路径参数正确，或默认路径存在。" -ForegroundColor Gray
    Write-Host "  3. 日志位于 %APPDATA%\StardewValley\ErrorLogs\SMAPI-latest.txt 或同目录 SMAPI-*.txt。" -ForegroundColor Gray
    exit 1
}

# ── Load logs ──
$hostLines = if ($resolvedHostLog) { Read-LogLines -Path $resolvedHostLog } else { @() }
$farmhandLines = if ($resolvedFarmhandLog) { Read-LogLines -Path $resolvedFarmhandLog } else { @() }

Write-Host ""
Write-Host "日志行数:" -ForegroundColor Gray
Write-Host "  主机:  $($hostLines.Count) 行" -ForegroundColor Gray
Write-Host "  客机:  $($farmhandLines.Count) 行" -ForegroundColor Gray

# ── C1: Agent NPC dialogue path ──
Write-CheckHeader -Label "C1: farmhand 点击 Agent NPC 对话路径"

$c1HostMarker = Test-ExplicitMarker -Lines $hostLines -Case "C1"
$c1FarmhandMarker = Test-ExplicitMarker -Lines $farmhandLines -Case "C1"

if ($isVerbose) {
    Write-Host "  主机显式标记 PASS: $($c1HostMarker.PassCount) FAIL: $($c1HostMarker.FailCount)" -ForegroundColor DarkGray
    foreach ($m in $c1HostMarker.PassLines) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
    Write-Host "  客机显式标记 PASS: $($c1FarmhandMarker.PassCount) FAIL: $($c1FarmhandMarker.FailCount)" -ForegroundColor DarkGray
    foreach ($m in $c1FarmhandMarker.PassLines) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
}

if ($c1HostMarker.FailCount -gt 0 -or $c1FarmhandMarker.FailCount -gt 0) {
    $c1Result = Write-Verdict -Verdict "FAIL" -Message "检测到 [C1] FAIL 显式标记。"
} elseif ($c1HostMarker.PassCount -gt 0 -or $c1FarmhandMarker.PassCount -gt 0) {
    $c1Result = Write-Verdict -Verdict "PASS" -Message "检测到 [C1] PASS 显式标记；Agent NPC 对话路径正常。"
} else {
    $c1HostPatterns = @(
        "\[Dialogue\] Agent Haley: opened native DialogueBox"
    )
    $c1FarmhandPositivePatterns = @(
        "\[Dialogue\] Agent Haley: opened native DialogueBox",
        "\[Chat\] LLM response"
    )
    $c1FarmhandNegativePatterns = @(
        "fallen back to vanilla checkAction"
    )
    $c1PatchPattern = "DialogueBoxInputPatch"

    $c1HostMatches = Search-Lines -Lines $hostLines -Patterns $c1HostPatterns
    $c1FarmhandPositiveMatches = Search-Lines -Lines $farmhandLines -Patterns $c1FarmhandPositivePatterns
    $c1FarmhandNegativeMatches = Search-Lines -Lines $farmhandLines -Patterns $c1FarmhandNegativePatterns
    $c1PatchMatches = Search-Lines -Lines $farmhandLines -Patterns @($c1PatchPattern)

    if ($isVerbose) {
        Write-Host "  主机 Agent DialogueBox: $($c1HostMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c1HostMatches) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
        Write-Host "  客机正面证据: $($c1FarmhandPositiveMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c1FarmhandPositiveMatches) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
        Write-Host "  客机 vanilla fallback: $($c1FarmhandNegativeMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c1FarmhandNegativeMatches) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
        Write-Host "  客机 DialogueBoxInputPatch: $($c1PatchMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c1PatchMatches | Select-Object -First 3) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
    }

    $c1Positive = ($c1HostMatches.Count -gt 0 -and $c1FarmhandPositiveMatches.Count -gt 0)
    $c1VanillaFallback = ($c1FarmhandNegativeMatches.Count -gt 0 -and $c1PatchMatches.Count -eq 0)

    if ($c1VanillaFallback) {
        $c1Result = Write-Verdict -Verdict "FAIL" -Message "farmhand 回退到原版 checkAction，且未检测到 DialogueBoxInputPatch 绘制。"
    } elseif ($c1Positive) {
        $c1Result = Write-Verdict -Verdict "PASS" -Message "farmhand 触发 Agent 对话路径。"
    } elseif ($c1FarmhandPositiveMatches.Count -gt 0) {
        $c1Result = Write-Verdict -Verdict "WARN" -Message "仅有 farmhand 正面证据，主机日志未找到对应 DialogueBox。"
    } else {
        $c1Result = Write-Verdict -Verdict "FAIL" -Message "未检测到 C1 相关事件。"
    }
}

# ── C2: farmhand gift transport ──
Write-CheckHeader -Label "C2: farmhand 送礼走 transport 代理"

$c2HostMarker = Test-ExplicitMarker -Lines $hostLines -Case "C2"
$c2FarmhandMarker = Test-ExplicitMarker -Lines $farmhandLines -Case "C2"

if ($isVerbose) {
    Write-Host "  主机显式标记 PASS: $($c2HostMarker.PassCount) FAIL: $($c2HostMarker.FailCount)" -ForegroundColor DarkGray
    foreach ($m in $c2HostMarker.PassLines) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
    Write-Host "  客机显式标记 PASS: $($c2FarmhandMarker.PassCount) FAIL: $($c2FarmhandMarker.FailCount)" -ForegroundColor DarkGray
    foreach ($m in $c2FarmhandMarker.PassLines) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
}

if ($c2HostMarker.FailCount -gt 0 -or $c2FarmhandMarker.FailCount -gt 0) {
    $c2Result = Write-Verdict -Verdict "FAIL" -Message "检测到 [C2] FAIL 显式标记。"
} elseif ($c2HostMarker.PassCount -gt 0 -or $c2FarmhandMarker.PassCount -gt 0) {
    $c2Result = Write-Verdict -Verdict "PASS" -Message "检测到 [C2] PASS 显式标记；farmhand 送礼路径被接管。"
} else {
    $c2FarmhandPatterns = @(
        "\[Gift\] Transport SendAsync",
        "received gift via transport",
        "\[C2\]"
    )
    $c2HostPatterns = @(
        "\[HostRequestHandlers\] HandleGiftRequest",
        "\[Gift\]"
    )

    $c2FarmhandMatches = Search-Lines -Lines $farmhandLines -Patterns $c2FarmhandPatterns
    $c2HostMatches = Search-Lines -Lines $hostLines -Patterns $c2HostPatterns

    if ($isVerbose) {
        Write-Host "  客机 gift transport: $($c2FarmhandMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c2FarmhandMatches) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
        Write-Host "  主机 gift handler: $($c2HostMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c2HostMatches | Select-Object -First 3) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
    }

    if ($c2FarmhandMatches.Count -gt 0 -and $c2HostMatches.Count -gt 0) {
        $c2Result = Write-Verdict -Verdict "PASS" -Message "farmhand 送礼通过 transport 代理，主机已处理 GiftRequest。"
    } elseif ($c2FarmhandMatches.Count -gt 0) {
        $c2Result = Write-Verdict -Verdict "WARN" -Message "farmhand 发出 transport，但主机未找到对应 handler。"
    } elseif ($c2HostMatches.Count -gt 0) {
        $c2Result = Write-Verdict -Verdict "WARN" -Message "主机存在 gift handler，但未在 farmhand 日志找到 transport 证据。"
    } else {
        $c2Result = Write-Verdict -Verdict "FAIL" -Message "未检测到 C2 送礼 transport 事件。"
    }
}

# ── C3: "帮我挖矿" command execution ──
Write-CheckHeader -Label "C3: farmhand 说'帮我挖矿'后主机执行 Actions"

$c3HostMarker = Test-ExplicitMarker -Lines $hostLines -Case "C3"
$c3FarmhandMarker = Test-ExplicitMarker -Lines $farmhandLines -Case "C3"

if ($isVerbose) {
    Write-Host "  主机显式标记 PASS: $($c3HostMarker.PassCount) FAIL: $($c3HostMarker.FailCount)" -ForegroundColor DarkGray
    foreach ($m in $c3HostMarker.PassLines) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
    Write-Host "  客机显式标记 PASS: $($c3FarmhandMarker.PassCount) FAIL: $($c3FarmhandMarker.FailCount)" -ForegroundColor DarkGray
    foreach ($m in $c3FarmhandMarker.PassLines) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
}

if ($c3HostMarker.FailCount -gt 0 -or $c3FarmhandMarker.FailCount -gt 0) {
    $c3Result = Write-Verdict -Verdict "FAIL" -Message "检测到 [C3] FAIL 显式标记。"
} elseif ($c3HostMarker.PassCount -gt 0 -or $c3FarmhandMarker.PassCount -gt 0) {
    $c3Result = Write-Verdict -Verdict "PASS" -Message "检测到 [C3] PASS 显式标记；farmhand 到主机的对话链路通。"
} else {
    $c3FarmhandPatterns = @(
        "\[Chat\] LLM response.*帮我挖矿",
        "帮我挖矿",
        "\[FarmhandDialogueTransport\]"
    )
    $c3HostActionFailPatterns = @(
        "\[HostRequestHandlers\] Action '.*' failed"
    )
    $c3HostActionSuccessPatterns = @(
        "\[HostRequestHandlers\].*move_to",
        "\[HostRequestHandlers\].*set_state",
        "CommandExecutor"
    )
    $c3VisualPatterns = @(
        "\[AgentRemoteRenderer\].*emote",
        "\[AgentRemoteRenderer\].*speak",
        "\[Multiplayer\] NpcAction",
        "action=emote",
        "action=speak"
    )

    $c3FarmhandMatches = Search-Lines -Lines $farmhandLines -Patterns $c3FarmhandPatterns
    $c3HostFailMatches = Search-Lines -Lines $hostLines -Patterns $c3HostActionFailPatterns
    $c3HostSuccessMatches = Search-Lines -Lines $hostLines -Patterns $c3HostActionSuccessPatterns
    $c3VisualMatches = Search-Lines -Lines $farmhandLines -Patterns $c3VisualPatterns

    if ($isVerbose) {
        Write-Host "  客机 '帮我挖矿'/transport: $($c3FarmhandMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c3FarmhandMatches) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
        Write-Host "  主机 action failed: $($c3HostFailMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c3HostFailMatches) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
        Write-Host "  主机 action success: $($c3HostSuccessMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c3HostSuccessMatches | Select-Object -First 3) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
        Write-Host "  客机视觉广播: $($c3VisualMatches.Count) 行" -ForegroundColor DarkGray
        foreach ($m in $c3VisualMatches | Select-Object -First 3) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
    }

    $c3HeardRequest = ($c3FarmhandMatches.Count -gt 0)
    $c3HostExecuted = ($c3HostSuccessMatches.Count -gt 0 -and $c3HostFailMatches.Count -eq 0)
    $c3SawVisuals = ($c3VisualMatches.Count -gt 0)

    if ($c3HeardRequest -and $c3HostExecuted -and $c3SawVisuals) {
        $c3Result = Write-Verdict -Verdict "PASS" -Message "farmhand 请求已送达主机执行，且客机收到视觉广播。"
    } elseif ($c3HostFailMatches.Count -gt 0) {
        $c3Result = Write-Verdict -Verdict "FAIL" -Message "主机执行 Action 出现失败。"
    } elseif ($c3HeardRequest -and $c3HostExecuted) {
        $c3Result = Write-Verdict -Verdict "WARN" -Message "主机执行成功，但客机未收到视觉广播。"
    } elseif ($c3HeardRequest -and $c3SawVisuals -and -not $c3HostExecuted) {
        $c3Result = Write-Verdict -Verdict "WARN" -Message "客机收到视觉广播，但主机未找到明确 action 执行证据。"
    } elseif ($c3HeardRequest) {
        $c3Result = Write-Verdict -Verdict "FAIL" -Message "farmhand 发出请求，但主机未执行且未收到视觉广播。"
    } else {
        $c3Result = Write-Verdict -Verdict "FAIL" -Message "未检测到 '帮我挖矿' 请求或 FarmhandDialogueTransport。"
    }
}

# ── Global error scan ──
Write-CheckHeader -Label "通用错误扫描"

$errorPatterns = @(
    "NullReferenceException",
    "Object reference not set",
    "ArgumentNullException",
    "KeyNotFoundException",
    "IndexOutOfRangeException"
)
$hostErrorMatches = Search-Lines -Lines $hostLines -Patterns $errorPatterns
$farmhandErrorMatches = Search-Lines -Lines $farmhandLines -Patterns $errorPatterns

if ($isVerbose) {
    Write-Host "  主机异常: $($hostErrorMatches.Count) 行" -ForegroundColor DarkGray
    foreach ($m in $hostErrorMatches | Select-Object -First 5) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
    Write-Host "  客机异常: $($farmhandErrorMatches.Count) 行" -ForegroundColor DarkGray
    foreach ($m in $farmhandErrorMatches | Select-Object -First 5) { Write-Host "    L$($m.LineNumber): $($m.Text)" -ForegroundColor DarkGray }
}

if ($hostErrorMatches.Count -gt 0 -or $farmhandErrorMatches.Count -gt 0) {
    Write-Verdict -Verdict "WARN" -Message "检测到 $($hostErrorMatches.Count + $farmhandErrorMatches.Count) 处异常/空引用，建议人工复核相关堆栈。"
} else {
    Write-Verdict -Verdict "PASS" -Message "未检测到常见的空引用/索引越界异常。"
}

# ── Summary ──
$results = @($c1Result, $c2Result, $c3Result)
$passCount = ($results | Where-Object { $_ -eq "PASS" }).Count
$failCount = ($results | Where-Object { $_ -eq "FAIL" }).Count
$warnCount = ($results | Where-Object { $_ -eq "WARN" }).Count

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  汇总 verdict" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$overallColor = "Green"
if ($failCount -gt 0) { $overallColor = "Red" }
elseif ($warnCount -gt 0) { $overallColor = "Yellow" }

$verdictText = if ($failCount -gt 0) { "FAIL" } elseif ($warnCount -gt 0) { "WARN" } else { "PASS" }
Write-Host "  C1 对话路径: $c1Result" -ForegroundColor $(@{ "PASS"="Green"; "FAIL"="Red"; "WARN"="Yellow" }[$c1Result])
Write-Host "  C2 送礼代理: $c2Result" -ForegroundColor $(@{ "PASS"="Green"; "FAIL"="Red"; "WARN"="Yellow" }[$c2Result])
Write-Host "  C3 挖矿指令: $c3Result" -ForegroundColor $(@{ "PASS"="Green"; "FAIL"="Red"; "WARN"="Yellow" }[$c3Result])
Write-Host ""
Write-Host "  总计: PASS=$passCount  WARN=$warnCount  FAIL=$failCount" -ForegroundColor $overallColor
Write-Host "  Verdict: $verdictText" -ForegroundColor $overallColor
Write-Host "========================================" -ForegroundColor Cyan

exit $(if ($verdictText -eq "PASS") { 0 } elseif ($verdictText -eq "WARN") { 2 } else { 1 })
