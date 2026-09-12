#!/bin/bash
set -euo pipefail

# ValleyAgent IT container entrypoint.
# Copies game + installs SMAPI on first run (cached in /data bind mount).
# Copies mods fresh every run. Launches SMAPI headless via Xvfb + PTY.

GAME_SRC="/game"
MODS_SRC="/mods-src"
SAVES_SRC="/saves-src"
DATA_DIR="/data"
GAME_DEST="${DATA_DIR}/game"
MODS_DEST="${DATA_DIR}/Mods"
SMAPI_EXECUTABLE="${GAME_DEST}/StardewModdingAPI"
# SMAPI 4.x 的数据目录 = 游戏目录内 StardewValley/ 子目录（日志在
# ${GAME_DEST}/StardewValley/ErrorLogs 实证）——存档必须在
# ${GAME_DEST}/StardewValley/Saves，不是 ~/.config（2026-08-19 容器实测）。
SAVE_DIR="${GAME_DEST}/StardewValley/Saves"
DEBUG_LOG_DIR="${DATA_DIR}/logs-debug"

echo "=== ValleyAgent IT Container ==="
echo "SMAPI version: ${SMAPI_VERSION}"

# ── 1. Disable X screensaver / DPMS ──
xset s off 2>/dev/null || true
xset -dpms 2>/dev/null || true
xset s noblank 2>/dev/null || true

# ── 2. Copy game files to /data (first run only — persists in bind mount) ──
if [ ! -f "${GAME_DEST}/Stardew Valley.dll" ]; then
    echo "First run: copying game files to /data/game (~1.5GB, be patient)..."
    mkdir -p "${GAME_DEST}"
    cp -a "${GAME_SRC}/." "${GAME_DEST}/"
    # Slim the copy: remove host Mods dir (we inject our own via --mods-path),
    # zip archives, and ErrorLogs symlink — cuts copy size roughly in half.
    rm -rf "${GAME_DEST}/Mods" 2>/dev/null || true
    rm -f "${GAME_DEST}"/*.zip 2>/dev/null || true
    rm -f "${GAME_DEST}/ErrorLogs" 2>/dev/null || true
    # StardewValley = macOS/Unix bash launcher script; SMAPI needs to create a
    # *directory* named StardewValley under gamePath for its logs — file/dir
    # name collision crashes SMAPI init (2026-08-19 实测).
    rm -f "${GAME_DEST}/StardewValley" 2>/dev/null || true
    echo "Game files copied and slimmed."
else
    echo "Game files already in /data/game, skipping copy."
fi

# ── 3. Install SMAPI (first run only — cached in /data/game) ──
if [ ! -f "${SMAPI_EXECUTABLE}" ]; then
    echo "Installing SMAPI ${SMAPI_VERSION}..."
    # Use pre-downloaded installer if available (mounted at /smapi-cache),
    # otherwise download from GitHub (may fail in container due to TLS/proxy).
    SMAPI_ZIP="/smapi-cache/SMAPI-${SMAPI_VERSION}-installer.zip"
    if [ -f "${SMAPI_ZIP}" ]; then
        echo "Using pre-downloaded SMAPI installer from /smapi-cache"
        cp "${SMAPI_ZIP}" /tmp/smapi.zip
    else
        echo "Downloading SMAPI from GitHub..."
        curl -fL --retry 5 --retry-delay 2 \
            "https://github.com/Pathoschild/SMAPI/releases/download/${SMAPI_VERSION}/SMAPI-${SMAPI_VERSION}-installer.zip" \
            -o /tmp/smapi.zip
    fi
    unzip -q /tmp/smapi.zip -d /tmp/smapi/
    # "2\n\n" selects: Linux install, default game path
    printf "2\n\n" | "/tmp/smapi/SMAPI ${SMAPI_VERSION} installer/internal/linux/SMAPI.Installer" \
        --install \
        --game-path "${GAME_DEST}"
    rm -rf /tmp/smapi /tmp/smapi.zip
    echo "SMAPI installed to ${GAME_DEST}"
else
    echo "SMAPI already installed, skipping."
fi

# ── 4. Copy mods (fresh every run from read-only /mods-src mount) ──
echo "Setting up Mods..."
rm -rf "${MODS_DEST}"
mkdir -p "${MODS_DEST}"
cp -a "${MODS_SRC}/." "${MODS_DEST}/"
echo "Mods copied to ${MODS_DEST}"

# ── 5. Copy save files ──
# SMAPI installer replaces the game launcher with its own file named
# StardewValley (~6.9KB); SMAPI then needs a *directory* of the same name
# for logs — file/dir conflict crashes (实测 2026-08-19). Remove the file
# BEFORE creating SAVE_DIR (which sits under StardewValley/).
rm -f "${GAME_DEST}/StardewValley" 2>/dev/null || true

if [ -d "${SAVES_SRC}" ] && [ "$(ls -A "${SAVES_SRC}" 2>/dev/null)" ]; then
    mkdir -p "${SAVE_DIR}"
    cp -a "${SAVES_SRC}/." "${SAVE_DIR}/"
    echo "Save files copied to ${SAVE_DIR}"
else
    echo "WARNING: No save files found at ${SAVES_SRC} — AutoLoadGame may fail."
fi

# ── 6. Clear SMAPI crash/update markers (blocks startup on non-interactive stdin) ──
# SMAPI's crash/update marker checks call Console.ReadKey() which hangs without a TTY.
rm -f "${GAME_DEST}/smapi-internal/StardewModdingAPI.crash.marker" \
      "${GAME_DEST}/smapi-internal/StardewModdingAPI.update.marker"

# SMAPI 安装器会把游戏启动脚本替换成自己的 launcher 文件（StardewValley, ~6.9KB），
# 但 SMAPI 初始化又要创建同名 *目录*（日志目录）——文件/目录冲突直接崩（实测 2026-08-19）。
# 启动走 ./StardewModdingAPI，该 launcher 文件无用，删掉让目录可创建。
rm -f "${GAME_DEST}/StardewValley" 2>/dev/null || true

# ── 7. Set permissions ──
chmod -R 755 "${GAME_DEST}" 2>/dev/null || true

# ── 8. Launch SMAPI via PTY (so it prints colored output and doesn't block on ReadKey) ──
LOG_FILE="/tmp/smapi-output.log"
INPUT_FIFO="/tmp/smapi-input"

touch "${LOG_FILE}"
rm -f "${INPUT_FIFO}"
mkfifo "${INPUT_FIFO}"

echo ""
echo "Starting SMAPI..."
echo "  Game dir:  ${GAME_DEST}"
echo "  Mods dir:  ${MODS_DEST}"
echo "  Saves dir: ${SAVE_DIR}"
echo "  VNC:       port 5800 (web) / 5900 (native)"
echo ""

# script creates a PTY so SMAPI sees a terminal (colored output, no ReadKey hang).
# tail -f keeps the FIFO open so SMAPI's stdin doesn't get EOF.
# --mods-path points SMAPI at our patched /data/Mods instead of the game-dir Mods
# (which we deleted during slimming). Absolute path: Path.Combine returns it as-is.
# CRITICAL: cd into the game dir first — SMAPI derives gamePath & SavesPath from its
# working directory; supervisord's default cwd (/etc/services.d/app) made the game
# look for saves in the wrong place (2026-08-19 container实测).
cd "${GAME_DEST}"
script -q -f --return -c "tail -f \"${INPUT_FIFO}\" | \"./StardewModdingAPI\" --mods-path \"${MODS_DEST}\"" "${LOG_FILE}" &
SMAPI_PID=$!

EXIT_CODE=0
wait $SMAPI_PID || EXIT_CODE=$?

echo ""
echo "SMAPI exited with code ${EXIT_CODE}"

# ── 9. Copy debug logs to /data/logs-debug/ for host inspection ──
mkdir -p "${DEBUG_LOG_DIR}"
cp "${LOG_FILE}" "${DEBUG_LOG_DIR}/smapi-output.log" 2>/dev/null || true
if [ -d "${GAME_DEST}/ErrorLogs" ] && [ "$(ls -A "${GAME_DEST}/ErrorLogs" 2>/dev/null)" ]; then
    cp -r "${GAME_DEST}/ErrorLogs/." "${DEBUG_LOG_DIR}/" 2>/dev/null || true
    echo "ErrorLogs copied to ${DEBUG_LOG_DIR}/"
fi

echo "Debug logs at ${DEBUG_LOG_DIR}/"
exit ${EXIT_CODE}
