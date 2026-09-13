# Deploy ValleyAgent to Steam-installed Stardew Valley Mods folder
# 与 deploy.ps1 相同的复制逻辑；游戏路径由 scripts/lib/paths.ps1 解析（STARDW_PATH 可覆盖）。
param(
    [switch]$SkipBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\..\lib\paths.ps1"
$RepoRoot = Get-RepoRoot
$GamePath = Get-GamePath
$ModsDir  = Join-Path $GamePath "Mods"
$ValleyAIDir = Get-ValleyAIRoot
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$logFile = Join-Path $RepoRoot "scripts\results\deploy-steam-$timestamp.txt"

function Write-Log([string]$Message, [string]$Level = "Info") {
    $color = switch ($Level) {
        "Success" { "Green" }
        "Warn"    { "Yellow" }
        "Error"   { "Red" }
        default   { "White" }
    }
    $ts = Get-Date -Format "HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    Write-Host $line -ForegroundColor $color
    $line | Out-File -FilePath $logFile -Append -Encoding UTF8
}

# Ensure scripts/results exists
$resultsDir = Join-Path $RepoRoot "scripts\results"
if (-not (Test-Path $resultsDir)) {
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
}

if (-not (Test-Path $GamePath)) {
    Write-Log "Game path not found: $GamePath" "Error"
    exit 1
}

if (-not $SkipBuild) {
    & "$PSScriptRoot\build-all.ps1" -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Write-Log "========================================" "Info"
Write-Log "  Deploying to Steam Mods folder" "Info"
Write-Log "  Target: $ModsDir" "Info"
Write-Log "========================================" "Info"

$BuildDir     = Join-Path $RepoRoot "src\ValleyAgent\bin\$Configuration\net6.0"
$TargetModDir = Join-Path $ModsDir "ValleyAgent"

# Kill running game if needed
$gameProcs = Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue
if ($gameProcs) {
    Write-Log "Game running — killing to unlock files..." "Warn"
    $gameProcs | Stop-Process -Force
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $TargetModDir)) {
    New-Item -ItemType Directory -Path $TargetModDir -Force | Out-Null
}

# Step 1: Clean obsolete python_server/extracted dirs
foreach ($staleDir in @("python_server", "extracted", "extracted_deploy")) {
    $stalePath = Join-Path $TargetModDir $staleDir
    if (Test-Path $stalePath) {
        Write-Log "Removing obsolete $staleDir/ from target mod directory..." "Warn"
        Remove-Item $stalePath -Recurse -Force
        Write-Log "Removed $staleDir/" "Success"
    }
}

# Step 2: Copy ValleyAgent core files
$filesToCopy = @(
    "ValleyAgent.dll",
    "ValleyAgent.pdb",
    "ValleyAgent.xml",
    "ValleyAgent.Abstractions.dll",
    "ValleyAgent.Abstractions.pdb",
    "ValleyAgent.Abstractions.xml",
    "manifest.json"
)
foreach ($f in $filesToCopy) {
    $src = Join-Path $BuildDir $f
    $dst = Join-Path $TargetModDir $f
    if (Test-Path $src) {
        Copy-Item $src $dst -Force
        Write-Log "Copied $f" "Info"
    } else {
        Write-Log "WARN: $f not found in build output" "Warn"
    }
}

# config.json: preserve user settings if exists
$configDst = Join-Path $TargetModDir "config.json"
$configSrc = Join-Path $BuildDir "config.json"
if (-not (Test-Path $configDst)) {
    if (Test-Path $configSrc) {
        Copy-Item $configSrc $configDst -Force
        Write-Log "Initialized config.json (first-time deploy)" "Info"
    }
} else {
    Write-Log "config.json already exists — preserving user settings" "Info"
}

# Step 3: i18n
$I18nSrc = Join-Path $BuildDir "i18n"
$I18nDst = Join-Path $TargetModDir "i18n"
if (Test-Path $I18nSrc) {
    if (-not (Test-Path $I18nDst)) { New-Item -ItemType Directory -Path $I18nDst -Force | Out-Null }
    Copy-Item "$I18nSrc\*" $I18nDst -Force -Recurse
    Write-Log "Copied i18n/" "Info"
}

# Step 4: RAG
$RagSrc = Join-Path $BuildDir "RAG"
$RagDst = Join-Path $TargetModDir "RAG"
if (Test-Path $RagSrc) {
    if (-not (Test-Path $RagDst)) { New-Item -ItemType Directory -Path $RagDst -Force | Out-Null }
    Copy-Item "$RagSrc\*" $RagDst -Force -Recurse
    Write-Log "Copied RAG/" "Info"
}

# Step 5: assets
$AssetsSrc = Join-Path $BuildDir "assets"
$AssetsDst = Join-Path $TargetModDir "assets"
if (Test-Path $AssetsSrc) {
    if (-not (Test-Path $AssetsDst)) { New-Item -ItemType Directory -Path $AssetsDst -Force | Out-Null }
    Copy-Item "$AssetsSrc\*" $AssetsDst -Force -Recurse
    Write-Log "Copied assets/" "Info"
}

# Step 6: valley-ai-server.exe
$ValleyAIExeSrc = Join-Path $ValleyAIDir "packages\stardew\bin\valley-ai-server.exe"
$ValleyAIExeDst = Join-Path $TargetModDir "valley-ai-server.exe"
if (Test-Path $ValleyAIExeSrc) {
    Copy-Item $ValleyAIExeSrc $ValleyAIExeDst -Force
    $sizeMB = [math]::Round((Get-Item $ValleyAIExeDst).Length / 1MB, 1)
    Write-Log "Copied valley-ai-server.exe (${sizeMB} MB)" "Success"
} else {
    Write-Log "ERROR: valley-ai-server.exe not found at $ValleyAIExeSrc" "Error"
    exit 1
}

# data/npc_prompts.json
$ValleyAIDataSrc = Join-Path $ValleyAIDir "packages\stardew\data\npc_prompts.json"
$ValleyAIDataDstDir = Join-Path $TargetModDir "data"
$ValleyAIDataDst = Join-Path $ValleyAIDataDstDir "npc_prompts.json"
if (Test-Path $ValleyAIDataSrc) {
    if (-not (Test-Path $ValleyAIDataDstDir)) {
        New-Item -ItemType Directory -Path $ValleyAIDataDstDir -Force | Out-Null
    }
    Copy-Item $ValleyAIDataSrc $ValleyAIDataDst -Force
    Write-Log "Copied data/npc_prompts.json" "Success"
} else {
    Write-Log "ERROR: npc_prompts.json not found at $ValleyAIDataSrc" "Error"
    exit 1
}

# Step 7: Autopilot mod
$AutopilotBuildDir = Join-Path $RepoRoot "src\ValleyAgent.Autopilot\bin\$Configuration\net6.0"
$AutopilotTargetDir = Join-Path $ModsDir "ValleyAgent.Autopilot"
if (Test-Path $AutopilotBuildDir) {
    if (-not (Test-Path $AutopilotTargetDir)) {
        New-Item -ItemType Directory -Path $AutopilotTargetDir -Force | Out-Null
    }
    $autopilotFiles = @("ValleyAgent.Autopilot.dll", "ValleyAgent.Autopilot.pdb", "manifest.json")
    foreach ($f in $autopilotFiles) {
        $src = Join-Path $AutopilotBuildDir $f
        $dst = Join-Path $AutopilotTargetDir $f
        if (Test-Path $src) {
            Copy-Item $src $dst -Force
            Write-Log "Copied Autopilot/$f" "Info"
        }
    }
}

# Step 8: Remove TestMod if present (release must not ship test mod)
$TestModTargetDir = Join-Path $ModsDir "ValleyAgent.TestMod"
if (Test-Path $TestModTargetDir) {
    Write-Log "Removing ValleyAgent.TestMod/ from Steam Mods..." "Warn"
    Remove-Item $TestModTargetDir -Recurse -Force
    Write-Log "Removed ValleyAgent.TestMod/" "Success"
}
Get-ChildItem -Path $ModsDir -Filter "ValleyAgent.TestMod*.zip" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Log "Removing leftover TestMod zip: $($_.Name)" "Warn"
    Remove-Item $_.FullName -Force
}

Write-Log "========================================" "Info"
Write-Log "  Deployment complete" "Success"
Write-Log "  Steam Mods: $ModsDir\ValleyAgent" "Success"
Write-Log "  Log: $logFile" "Info"
Write-Log "========================================" "Info"