# prep-mods.ps1 -- Builds docker/mods-cache*/ and docker/saves/ from live game files.
# Patches ValleyAgent config.json COPIES for headless container mode.
# Does NOT modify the live Stardew Valley/Mods/ directory.
#
# Usage:
#   .\scripts\docker\prep-mods.ps1                     # IT mode (default, existing behavior)
#   .\scripts\docker\prep-mods.ps1 -Role host           # E2E host container
#   .\scripts\docker\prep-mods.ps1 -Role farmhand        # E2E farmhand container

param(
    [ValidateSet("it","host","farmhand")]
    [string]$Role = "it"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
if ((Test-Path (Join-Path $repoRoot "AGENTS.md")) -eq $false) {
    # Try resolving from script location
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$modsSrc = Join-Path $repoRoot "Stardew Valley\Mods"
$savesSrc = Join-Path $env:APPDATA "StardewValley\Saves"
$savesDest = Join-Path $repoRoot "docker\saves"

# Role-dependent output directory:
#   it       -> docker/mods-cache      (existing behavior)
#   host     -> docker/mods-cache-host (E2E host container mods)
#   farmhand -> docker/mods-cache-farmhand (E2E farmhand container mods)
if ($Role -eq "it") {
    $modsDest = Join-Path $repoRoot "docker\mods-cache"
} else {
    $modsDest = Join-Path $repoRoot "docker\mods-cache-$Role"
}

Write-Host "prep-mods.ps1 -Role $Role"
Write-Host "  Output: $modsDest"

# -- Validate --
if (-not (Test-Path $modsSrc)) {
    Write-Error "Live Mods directory not found: $modsSrc"
    exit 1
}

# -- Clean and rebuild mods-cache --
if (Test-Path $modsDest) {
    Remove-Item -Recurse -Force $modsDest
}
New-Item -ItemType Directory -Path $modsDest | Out-Null

$requiredMods = @(
    "ValleyAgent",
    "ValleyAgent.Abstractions",
    "ValleyAgent.TestMod",
    "AutoLoadGame"
)

foreach ($mod in $requiredMods) {
    $src = Join-Path $modsSrc $mod
    if (-not (Test-Path $src)) {
        Write-Error "Required mod not found: $src"
        exit 1
    }
    Write-Host "Copying $mod..."
    Copy-Item -Recurse -Force $src (Join-Path $modsDest $mod)
}

# ValleyAgent.Abstractions deploys as manifest-only sometimes (host Mods dir is
# like that; container SMAPI errors "ValleyAgent.Abstractions.dll doesn't exist"
# -> whole Integration group SKIPs). Patch the dll in from build output.
# NOTE: keep comments ASCII - PS 5.1 reads UTF-8-no-BOM as GBK and multi-byte
# chars can swallow newlines, nulling variables.
$absDll = Join-Path $repoRoot "src\ValleyAgent.Abstractions\bin\Debug\net6.0\ValleyAgent.Abstractions.dll"
if ((Test-Path (Join-Path $modsDest "ValleyAgent.Abstractions")) -and (Test-Path $absDll)) {
    Copy-Item -Force $absDll (Join-Path $modsDest "ValleyAgent.Abstractions\ValleyAgent.Abstractions.dll")
    Write-Host "Patched ValleyAgent.Abstractions.dll from build output."
} else {
    Write-Warning "Abstractions dll patch skipped: dll=$(-not (Test-Path $absDll))"
}

# -- Patch ValleyAgent config.json --
# Exact key names from src/ValleyAgent/Config/ModConfig.cs:
#   UseAgentServer  (line 63) -- enable/disable TS server connection
#   AutoStartServer (line 88) -- prevent ServerProcessManager launch
#   ServerConsoleWindow (line 100) -- prevent cmd.exe /c start (Linux has no cmd.exe)
#   AgentServerHost -- NEW field (D3): WS target host, default "127.0.0.1"
$vaConfig = Join-Path $modsDest "ValleyAgent\config.json"
if (Test-Path $vaConfig) {
    Write-Host "Patching ValleyAgent config.json for $Role mode..."
    $json = Get-Content $vaConfig -Raw | ConvertFrom-Json

    # Common: always disable ServerConsoleWindow (no cmd.exe in Linux containers)
    $json.ServerConsoleWindow = $false

    # Also disable the legacy Python auto-start (prevents any fallback path)
    if ($null -ne $json.PSObject.Properties["AutoStartPythonServer"]) {
        $json.AutoStartPythonServer = $false
    }

    if ($Role -eq "it") {
        # IT mode: no TS server connection, MaxAgentNpcs>0 for TryAllocateAgent(Haley)
        $json.UseAgentServer = $false
        $json.AutoStartServer = $false
        if ($null -ne $json.PSObject.Properties["MinAgentNpcs"]) {
            $json.MinAgentNpcs = 0
        }
        if ($null -ne $json.PSObject.Properties["NormalAgentNpcs"]) {
            $json.NormalAgentNpcs = 0
        }
        if ($null -ne $json.PSObject.Properties["MaxAgentNpcs"]) {
            $json.MaxAgentNpcs = 2
        }
        Write-Host "  UseAgentServer=false, AutoStartServer=false, MaxAgentNpcs=2"
    }
    elseif ($Role -eq "host") {
        # E2E host: connect to external TS server, do NOT auto-start server process
        $json.UseAgentServer = $true
        $json.AutoStartServer = $false
        # AgentServerHost: compose service DNS name for the TS container (D3)
        if ($null -ne $json.PSObject.Properties["AgentServerHost"]) {
            $json.AgentServerHost = "valley-ts"
        } else {
            # Add the field if not present (C# side will add it in batch A;
            # for now, inject raw JSON property via PSCustomObject)
            $json | Add-Member -NotePropertyName "AgentServerHost" -NotePropertyValue "valley-ts" -Force
        }
        if ($null -ne $json.PSObject.Properties["MinAgentNpcs"]) {
            $json.MinAgentNpcs = 0
        }
        if ($null -ne $json.PSObject.Properties["NormalAgentNpcs"]) {
            $json.NormalAgentNpcs = 0
        }
        if ($null -ne $json.PSObject.Properties["MaxAgentNpcs"]) {
            $json.MaxAgentNpcs = 2
        }
        Write-Host "  UseAgentServer=true, AutoStartServer=false, AgentServerHost=valley-ts"
        Write-Host "  MinAgentNpcs=0, NormalAgentNpcs=0, MaxAgentNpcs=2"

        # C5 assertion hygiene: stale *_rel.json from prior host E2E runs would
        # make the C5 ">= 2 rel files" check pass spuriously. Clear them.
        $haleyPlayers = Join-Path $modsDest "ValleyAgent\agents\Haley_players"
        if (Test-Path $haleyPlayers) {
            Get-ChildItem $haleyPlayers -Filter "*_rel.json" -ErrorAction SilentlyContinue |
                Remove-Item -Force -ErrorAction SilentlyContinue
            Write-Host "  Cleared stale Haley_players rel files (C5 baseline)."
        }
    }
    elseif ($Role -eq "farmhand") {
        # E2E farmhand: ThinClient mode, no agent allocation
        $json.UseAgentServer = $false
        $json.AutoStartServer = $false
        if ($null -ne $json.PSObject.Properties["MaxAgentNpcs"]) {
            $json.MaxAgentNpcs = 0
        }
        Write-Host "  UseAgentServer=false, AutoStartServer=false, MaxAgentNpcs=0 (ThinClient)"
    }

    $json | ConvertTo-Json -Depth 10 | Set-Content $vaConfig -Encoding UTF8
} else {
    Write-Warning "ValleyAgent config.json not found at $vaConfig -- skipping patch"
}

# -- Copy test_config.json for TestMod --
$testModDest = Join-Path $modsDest "ValleyAgent.TestMod"
if ($Role -eq "it") {
    # IT mode: use container test config (runner=v3, exitOnComplete=true)
    $containerTestConfig = Join-Path $repoRoot "docker\test_config.container.json"
    if (Test-Path $containerTestConfig) {
        Write-Host "Copying test_config.container.json to TestMod..."
        Copy-Item -Force $containerTestConfig (Join-Path $testModDest "test_config.json")
    } else {
        Write-Warning "Container test config not found: $containerTestConfig"
        Write-Warning "TestMod will use deployed test_config.json (may have runner=manual)"
    }
} else {
    # E2E mode: use e2e test config (runner=manual, exitOnComplete=false)
    $e2eTestConfig = Join-Path $repoRoot "docker\test_config.e2e.json"
    if (Test-Path $e2eTestConfig) {
        Write-Host "Copying test_config.e2e.json to TestMod ($Role)..."
        Copy-Item -Force $e2eTestConfig (Join-Path $testModDest "test_config.json")
    } else {
        Write-Warning "E2E test config not found: $e2eTestConfig"
        Write-Warning "TestMod will use deployed test_config.json (may have runner=v3)"
    }
}

# -- Copy save files (same for all roles) --
if (Test-Path $savesSrc) {
    if (Test-Path $savesDest) {
        Remove-Item -Recurse -Force $savesDest
    }
    New-Item -ItemType Directory -Path $savesDest | Out-Null
    Write-Host "Copying save files from $savesSrc..."
    Copy-Item -Recurse -Force "$savesSrc\*" $savesDest
    $saveCount = (Get-ChildItem $savesDest -Directory).Count
    Write-Host "Copied $saveCount save(s) to $savesDest"
} else {
    Write-Warning "Saves directory not found: $savesSrc"
    Write-Warning "Create docker/saves/ manually and copy your save there."
    New-Item -ItemType Directory -Path $savesDest -Force | Out-Null
}

Write-Host ""
Write-Host "Preparation complete."
Write-Host "  Role:      $Role"
Write-Host "  Mods cache:  $modsDest"
Write-Host "  Saves cache: $savesDest"
Write-Host ""
if ($Role -eq "it") {
    Write-Host "Next: docker build -f docker/Dockerfile.gameit -t valleyagent-gameit ."
    Write-Host "Then: .\scripts\docker\run-it.ps1"
} else {
    Write-Host "Next: .\scripts\docker\run-e2e.ps1"
}
