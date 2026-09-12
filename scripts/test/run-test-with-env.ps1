#requires -Version 5.1
<#
.SYNOPSIS
    Wrapper around test-game.ps1 that injects LLM environment variables
    so the mod-spawned TS Agent Server can reach LM Studio.

.DESCRIPTION
    test-game.ps1 launches SMAPI; SMAPI loads ValleyAgent; ValleyAgent's
    ServerProcessManager spawns valley-ai-server.exe with --llm-* CLI args
    derived from ModConfig (which reads VALLEY_LLM_* env vars at startup if
    the config fields are empty). The TS server (cli.ts) also reads env vars
    LLM_API_KEY / LLM_MODEL / LLM_BASE_URL / LLM_PROVIDER as fallbacks when
    the CLI args are not supplied.

    This wrapper sets the env vars in the current PowerShell session
    (Start-Process inherits them into SMAPI → ServerProcessManager →
    valley-ai-server.exe).
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [int]$TimeoutSeconds = 600,
    [string]$BaseUrl   = "http://127.0.0.1:1234/v1",
    [string]$ApiKey    = "lm-studio",
    [string]$Model     = "qwen-3.6-27b-uncensored-hauhaucs-aggressive-q2-k"
)

$ErrorActionPreference = "Stop"

# ── Inject LLM env (inherited by SMAPI → mod → valley-ai-server.exe) ──
$env:VALLEY_LLM_BASE_URL = $BaseUrl
$env:VALLEY_LLM_API_KEY  = $ApiKey
$env:VALLEY_LLM_MODEL    = $Model
$env:VALLEY_LLM_PROVIDER = "openai_compatible"

Write-Host "[ENV] VALLEY_LLM_BASE_URL = $BaseUrl" -ForegroundColor DarkGray
Write-Host "[ENV] VALLEY_LLM_API_KEY  = $ApiKey"   -ForegroundColor DarkGray
Write-Host "[ENV] VALLEY_LLM_MODEL    = $Model"    -ForegroundColor DarkGray
Write-Host ""

& "$PSScriptRoot\test-game.ps1" -NoBuild -Configuration $Configuration -TimeoutSeconds $TimeoutSeconds
exit $LASTEXITCODE
