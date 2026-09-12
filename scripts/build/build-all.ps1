# Build ValleyAgent + ValleyAgent.TestMod
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$IncludeTests
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$logFile = Join-Path $RepoRoot "scripts\results\build-$timestamp.txt"

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

Write-Log "========================================" "Info"
Write-Log "  ValleyTalk Build ($Configuration)" "Info"
Write-Log "========================================" "Info"

$ValleyAgentDir     = Join-Path $RepoRoot "src\ValleyAgent"
$ValleyAgentTestDir = Join-Path $RepoRoot "src\ValleyAgent.TestMod"

# Step 1: Kill running game if DLLs are locked
$gameProcs = Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue
if ($gameProcs) {
    $procNames = ($gameProcs | Select-Object -ExpandProperty ProcessName) -join ", "
    Write-Log "Game running ($procNames) — killing to unlock DLLs..." "Warn"
    $gameProcs | Stop-Process -Force
    Start-Sleep -Seconds 2
    Write-Log "Game processes terminated." "Success"
}

# Step 2: Build ValleyAgent
Write-Log "Building ValleyAgent..." "Info"
dotnet build "$ValleyAgentDir\ValleyAgent.csproj" -c $Configuration --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Log "ValleyAgent build FAILED (exit code $LASTEXITCODE)" "Error"
    exit 1
}
Write-Log "ValleyAgent build succeeded." "Success"

# Step 3: Build TestMod (only if -IncludeTests)
if ($IncludeTests) {
    Write-Log "Building ValleyAgent.TestMod..." "Info"
    dotnet build "$ValleyAgentTestDir\ValleyAgent.TestMod.csproj" -c $Configuration --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Log "ValleyAgent.TestMod build FAILED (exit code $LASTEXITCODE)" "Error"
        exit 1
    }
    Write-Log "ValleyAgent.TestMod build succeeded." "Success"
} else {
    Write-Log "Skipping ValleyAgent.TestMod build (use -IncludeTests to enable)" "Info"
}

# Step 4: Build Autopilot
$AutopilotDir = Join-Path $RepoRoot "src\ValleyAgent.Autopilot"
Write-Log "Building ValleyAgent.Autopilot..." "Info"
dotnet build "$AutopilotDir\ValleyAgent.Autopilot.csproj" -c $Configuration --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Log "ValleyAgent.Autopilot build FAILED (exit code $LASTEXITCODE)" "Error"
    exit 1
}
Write-Log "ValleyAgent.Autopilot build succeeded." "Success"

# Step 5: Build ValleyAI TypeScript server (valley-ai-server.exe) if ValleyAI repo is available
$ValleyAIDir = "D:\Source\ValleyAI"
$ValleyAIEntry = Join-Path $ValleyAIDir "packages\stardew\src\cli.ts"
if (Test-Path $ValleyAIEntry) {
    Write-Log "Building valley-ai-server.exe (Bun compile)..." "Info"
    $bunExe = Get-Command bun -ErrorAction SilentlyContinue
    if ($null -eq $bunExe) {
        Write-Log "bun not found in PATH — skip building valley-ai-server.exe (deploy will fail if exe is missing)" "Warn"
    } else {
        $binDir = Join-Path $ValleyAIDir "packages\stardew\bin"
        if (-not (Test-Path $binDir)) {
            New-Item -ItemType Directory -Path $binDir -Force | Out-Null
        }
        Push-Location (Join-Path $ValleyAIDir "packages\stardew")
        try {
            & bun build --compile --target=bun-windows-x64 "src\cli.ts" --outfile "bin\valley-ai-server.exe"
            if ($LASTEXITCODE -ne 0) {
                Write-Log "valley-ai-server.exe build FAILED (exit code $LASTEXITCODE)" "Error"
                exit 1
            }
            Write-Log "valley-ai-server.exe build succeeded." "Success"
        }
        finally {
            Pop-Location
        }
    }
} else {
    Write-Log "ValleyAI repo not found at $ValleyAIDir — skipping TS server build" "Warn"
}

Write-Log "Build log saved to: $logFile" "Info"
Write-Log "Done." "Success"
exit 0