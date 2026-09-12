# Deploy ValleyAgent to Stardew Valley Mods folder
param(
    [switch]$SkipBuild,
    [switch]$IncludeTests,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$GamePath = "D:\Source\ValleyTalk\Stardew Valley"
$ModsDir  = Join-Path $GamePath "Mods"
$ValleyAIDir = "D:\Source\ValleyAI"
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$logFile = Join-Path $RepoRoot "scripts\results\deploy-$timestamp.txt"

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

if (-not $SkipBuild) {
    & "$PSScriptRoot\build-all.ps1" -Configuration $Configuration -IncludeTests:$IncludeTests
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Write-Log "========================================" "Info"
Write-Log "  Deploying to Mods folder" "Info"
Write-Log "========================================" "Info"

$BuildDir       = Join-Path $RepoRoot "src\ValleyAgent\bin\$Configuration\net6.0"
$TargetModDir   = Join-Path $ModsDir "ValleyAgent"

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

# Step 1: Clean obsolete python_server directory from previous deployments
$PythonServerDst = Join-Path $TargetModDir "python_server"
if (Test-Path $PythonServerDst) {
    Write-Log "Removing obsolete python_server/ from target mod directory..." "Warn"
    Remove-Item $PythonServerDst -Recurse -Force
    Write-Log "Removed python_server/" "Success"
}

# Clean obsolete extracted/extracted_deploy directories (leftover from manual zip extraction)
foreach ($staleDir in @("extracted", "extracted_deploy")) {
    $stalePath = Join-Path $TargetModDir $staleDir
    if (Test-Path $stalePath) {
        Write-Log "Removing obsolete $staleDir/ from target mod directory..." "Warn"
        Remove-Item $stalePath -Recurse -Force
        Write-Log "Removed $staleDir/" "Success"
    }
}

# Step 2: Copy ValleyAgent mod files (config.json only if missing to preserve user settings)
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

# config.json: only deploy default if user does not have one (preserve GMCM-edited settings)
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

# Step 3: Copy i18n folder
$I18nSrc = Join-Path $BuildDir "i18n"
$I18nDst = Join-Path $TargetModDir "i18n"
if (Test-Path $I18nSrc) {
    if (-not (Test-Path $I18nDst)) { New-Item -ItemType Directory -Path $I18nDst -Force | Out-Null }
    Copy-Item "$I18nSrc\*" $I18nDst -Force -Recurse
    Write-Log "Copied i18n/" "Info"
}

# Step 4: Copy RAG folder
$RagSrc = Join-Path $BuildDir "RAG"
$RagDst = Join-Path $TargetModDir "RAG"
if (Test-Path $RagSrc) {
    if (-not (Test-Path $RagDst)) { New-Item -ItemType Directory -Path $RagDst -Force | Out-Null }
    Copy-Item "$RagSrc\*" $RagDst -Force -Recurse
    Write-Log "Copied RAG/" "Info"
}

# Step 4.5: Copy Data/ (npc_economy.json) and npc-configs/ (Phase 3 人设配置) — 此前一直漏拷
foreach ($contentDir in @("Data", "npc-configs")) {
    $cSrc = Join-Path $BuildDir $contentDir
    if (Test-Path $cSrc) {
        $cDst = Join-Path $TargetModDir $contentDir
        if (-not (Test-Path $cDst)) { New-Item -ItemType Directory -Path $cDst -Force | Out-Null }
        Get-ChildItem -Path $cSrc -Filter "*.json" -File | ForEach-Object { Copy-Item $_.FullName $cDst -Force }
        Write-Log "Copied $contentDir/" "Info"
    }
}

# Step 5: Copy assets folder (memory_rules.json etc.)
$AssetsSrc = Join-Path $BuildDir "assets"
$AssetsDst = Join-Path $TargetModDir "assets"
if (Test-Path $AssetsSrc) {
    if (-not (Test-Path $AssetsDst)) { New-Item -ItemType Directory -Path $AssetsDst -Force | Out-Null }
    Copy-Item "$AssetsSrc\*" $AssetsDst -Force -Recurse
    Write-Log "Copied assets/" "Info"
}

# Step 6: Copy ValleyAI TypeScript server (valley-ai-server.exe) and data/npc_prompts.json
$ValleyAIExeSrc = Join-Path $ValleyAIDir "packages\stardew\bin\valley-ai-server.exe"
$ValleyAIExeDst = Join-Path $TargetModDir "valley-ai-server.exe"
if (Test-Path $ValleyAIExeSrc) {
    Copy-Item $ValleyAIExeSrc $ValleyAIExeDst -Force
    $sizeMB = [math]::Round((Get-Item $ValleyAIExeDst).Length / 1MB, 1)
    Write-Log "Copied valley-ai-server.exe (${sizeMB} MB)" "Success"
} else {
    Write-Log "ERROR: valley-ai-server.exe not found at $ValleyAIExeSrc" "Error"
    Write-Log "       Run bun build --compile in D:\Source\ValleyAI\packages\stardew first." "Error"
    exit 1
}

# cli.ts defaultDataPath() looks for ../data/npc_prompts.json relative to the exe
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

# Step 7: Copy Autopilot mod
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

# Step 8: Clean up obsolete TestMod deployment (release builds must not ship test mods)
$TestModTargetDir = Join-Path $ModsDir "ValleyAgent.TestMod"
if (Test-Path $TestModTargetDir) {
    Write-Log "Removing obsolete ValleyAgent.TestMod/ from Mods directory..." "Warn"
    Remove-Item $TestModTargetDir -Recurse -Force
    Write-Log "Removed ValleyAgent.TestMod/" "Success"
}

# Remove leftover TestMod zip backups
Get-ChildItem -Path $ModsDir -Filter "ValleyAgent.TestMod*.zip" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Log "Removing leftover TestMod zip: $($_.Name)" "Warn"
    Remove-Item $_.FullName -Force
}

# Step 9: Verify deployment
$deployedDll = Join-Path $TargetModDir "ValleyAgent.dll"
if (Test-Path $deployedDll) {
    $ts = (Get-Item $deployedDll).LastWriteTime
    Write-Log "ValleyAgent.dll deployed at $ts" "Success"
} else {
    Write-Log "ValleyAgent.dll not found after deployment!" "Error"
    exit 1
}

# Sanity: assert no TestMod DLL in Mods directory
$testModDlls = Get-ChildItem -Path $ModsDir -Filter "ValleyAgent.TestMod.dll" -Recurse -ErrorAction SilentlyContinue
if ($testModDlls) {
    foreach ($dll in $testModDlls) {
        Write-Log "WARN: TestMod DLL still present at $($dll.FullName)" "Warn"
    }
}

# Step 10: Launch game (optional, only when -StartGame passed — keeps deploy non-destructive)
# Note: original script auto-started; behavior preserved but moved under a switch for CI safety.
if ($PSBoundParameters.ContainsKey('StartGame')) {
    $gameExe = Join-Path $GamePath "StardewModdingAPI.exe"
    if (Test-Path $gameExe) {
        Write-Log "Starting game..." "Info"
        Start-Process -FilePath $gameExe -WorkingDirectory $GamePath
        Write-Log "Game started." "Success"
    } else {
        Write-Log "WARN: Game executable not found at $gameExe" "Warn"
    }
}

Write-Log "Deployment log saved to: $logFile" "Info"
Write-Log "Done. Mods deployed to: $TargetModDir" "Success"
