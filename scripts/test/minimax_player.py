"""
MiniMax 自主星露谷玩家
通过 StardewMCPBridge 的 JSON 接口控制游戏
"""
import json
import os
import time
import requests
import sys

# 游戏路径不硬编码：设置 STARDW_PATH 环境变量指向 Stardew Valley 安装目录。
_GAME_PATH = os.environ.get("STARDW_PATH", "")
if not _GAME_PATH:
    raise SystemExit(
        "请先设置 STARDW_PATH 环境变量，指向 Stardew Valley 安装目录\n"
        '例如: set STARDW_PATH=X:\\SteamLibrary\\steamapps\\common\\Stardew Valley'
    )
_BRIDGE_DIR = os.path.join(_GAME_PATH, "Mods", "StardewMCPBridge")
BRIDGE_PATH = os.path.join(_BRIDGE_DIR, "bridge_data.json")
ACTION_PATH = os.path.join(_BRIDGE_DIR, "actions.json")
MINIMAX_API_KEY = os.environ.get("MINIMAX_API_KEY", "")
MINIMAX_MODEL = "minimax-m2.7"
MINIMAX_URL = "https://api.minimax.chat/v1/text/chat/completions"

COMPANION_NAME = "Companion1"

# ---------------------------------------------------------------------------
# warp_to 落点白名单（2026-08-03 测试加固）
#
# 背景：LLM 曾自选坐标把玩家/同伴传送到 NPC 物理不可达的位置（如农场狗窝、
# 农田硬隔离区），跟随/挖矿/战斗测试因此失效。TestMod 侧已有 WarpTargetGuard
# 做"从地图入口 BFS 可达"验证，但本脚本在游戏外，无法跑 BFS —— 因此约束为
# 每张地图一组"已在 TestMod 中验证 NPC 可达"的白名单瓦片，禁止 LLM 自选坐标。
# 白名单来源（TestMod 各测试长期使用且 NPC 可跟随到达的落点）：
#   Farm     (70,17) F_FollowCrossMap 实测东侧开阔区、入口连通
#   Farm     (54,15) 全部测试的标准落点
#   Town     (54,68) F6_SceneSwitch / EXP004 / EXP014 使用
#   Town     (30,60) E13_FarmScanLocationCheck 使用
#   Mountain (40,10) E14_MineScanLocationCheck 使用（矿洞入口外湖区）
# 注意：首进矿洞（Mine 地图）会触发 Marlon 剧情事件导致卡死，白名单刻意不含
# Mine 地图 —— 如需进矿洞请先确认 MCP Bridge 支持跳过事件，否则不要 warp 进 Mine。
# ---------------------------------------------------------------------------
WARP_WHITELIST = {
    "Farm": [(70, 17), (54, 15)],
    "Town": [(54, 68), (30, 60)],
    "Mountain": [(40, 10)],
}

def sanitize_action(action):
    """约束 LLM 动作：warp_to 坐标强制吸附到白名单瓦片，禁止自选坐标。"""
    if not isinstance(action, dict):
        return action
    if action.get("actionType") != "warp_to":
        return action

    loc = action.get("location", "")
    tiles = WARP_WHITELIST.get(loc)
    if not tiles:
        # 不在白名单的地图（如 Mine）：拒绝 warp，退化为原地不动并说明原因
        print(f"  [WARP GUARD] location '{loc}' not in whitelist {list(WARP_WHITELIST)} — action rejected")
        return None

    x, y = action.get("x"), action.get("y")
    if (x, y) in tiles:
        return action

    # 吸附到最近的白名单瓦片（曼哈顿距离）
    try:
        px, py = int(x), int(y)
        snapped = min(tiles, key=lambda t: abs(t[0] - px) + abs(t[1] - py))
    except (TypeError, ValueError):
        snapped = tiles[0]
    print(f"  [WARP GUARD] ({x},{y}) not whitelisted on {loc} — snapped to {snapped}")
    action = dict(action)
    action["x"], action["y"] = snapped
    return action

def check_event_state(state):
    """warp 后检测剧情/事件状态。MCP Bridge 命令集没有跳过事件的能力（已查），
    只能检测并等待：若 bridge state 暴露了事件标志则等待其结束。"""
    if not state:
        return False
    for key in ("eventUp", "event", "cutscene", "inEvent"):
        if state.get(key):
            return True
    return False


def read_bridge():
    """Read game state from bridge_data.json"""
    try:
        if os.path.exists(BRIDGE_PATH):
            with open(BRIDGE_PATH, 'r') as f:
                return json.load(f)
    except Exception as e:
        print(f"  [BRIDGE READ ERROR] {e}")
    return None

def write_action(action_data):
    """Write action to actions.json"""
    try:
        tmp = ACTION_PATH + ".tmp"
        with open(tmp, 'w') as f:
            json.dump(action_data, f)
        os.replace(tmp, ACTION_PATH)
        return True
    except Exception as e:
        print(f"  [ACTION WRITE ERROR] {e}")
        return False

def call_minimax(prompt):
    """Call MiniMax API with a prompt"""
    headers = {
        "Authorization": f"Bearer {MINIMAX_API_KEY}",
        "Content-Type": "application/json"
    }
    payload = {
        "model": MINIMAX_MODEL,
        "messages": [
            {"role": "system", "content": "You are playing Stardew Valley. Look at the game state and decide what action to take. Respond with ONLY a JSON action object."},
            {"role": "user", "content": prompt}
        ],
        "temperature": 0.7,
        "max_tokens": 500
    }
    try:
        resp = requests.post(
            MINIMAX_URL,
            headers=headers,
            json=payload,
            timeout=30
        )
        if resp.status_code == 200:
            return resp.json()['choices'][0]['message']['content']
        else:
            print(f"  [API ERROR] {resp.status_code}: {resp.text[:200]}")
            return None
    except Exception as e:
        print(f"  [API CALL ERROR] {e}")
        return None

def simplify_state(state):
    """Extract key info for MiniMax prompt"""
    if not state:
        return "Game not loaded yet"
    p = state.get('player', {})
    npcs = state.get('npcs', [])
    npc_list = '\n'.join([f"  - {n['name']} at tile ({int(n['position']['x']/64)},{int(n['position']['y']/64)})"
                         for n in npcs if 'position' in n and n['position']['x'] > -1000])
    return (
        f"Time: {state.get('time','?')} | Day: {state.get('day','?')} | Season: {state.get('season','?')} | Weather: {state.get('weather','?')}\n"
        f"Location: {state.get('location','?')}\n"
        f"Player: health={p.get('health','?')} stamina={p.get('stamina','?')} money={p.get('money','?')}\n"
        f"Player position: tile ({int(p.get('position',{}).get('x',0)/64)},{int(p.get('position',{}).get('y',0)/64)})\n"
        f"Nearby NPCs:\n{npc_list if npc_list else '  (none visible)'}\n"
        f"Companions: {len(state.get('companions',[]))}"
    )

def wait_for_ticks(seconds=2):
    """Wait for game ticks to process"""
    time.sleep(seconds)

def main():
    print("=" * 50)
    print("MiniMax Autonomous Stardew Valley Player")
    print("=" * 50)
    
    # Phase 1: Spawn companion
    print("\n[PHASE 1] Spawning companion...")
    write_action({
        "actionType": "spawn"
    })
    wait_for_ticks(5)
    
    # Phase 2: Set companion to player mode
    print("\n[PHASE 2] Setting companion to player mode...")
    write_action({
        "actionType": "set_mode",
        "companion": COMPANION_NAME,
        "mode": "player"
    })
    wait_for_ticks(3)
    
    state = read_bridge()
    if state:
        companions = state.get('companions', [])
        print(f"Companions after spawn: {len(companions)}")
        for c in companions:
            print(f"  {c.get('name')}: mode={c.get('mode')}")
    
    # Phase 3: Autonomous loop
    print("\n[PHASE 3] Starting MiniMax decision loop...")
    print("Press Ctrl+C to stop\n")
    
    action_counts = {}
    max_iterations = 20
    for i in range(max_iterations):
        print(f"\n--- Iteration {i+1}/{max_iterations} ---")
        
        # Read game state
        state = read_bridge()
        state_text = simplify_state(state)
        print(f"Current state:\n{state_text}")

        # 事件检测：warp 后若游戏处于剧情/事件中，LLM 决策无意义——等待并报告，
        # 不消耗决策次数（MCP Bridge 无跳过事件能力，只能等事件自然结束）
        if check_event_state(state):
            print("  [EVENT] Game is in a cutscene/event — waiting for it to finish...")
            wait_for_ticks(5)
            continue
        
        # Build prompt for MiniMax
        companions = state.get('companions', []) if state else []
        companion_info = "Not spawned yet" if not companions else (
            f"{companions[0].get('name')} at {companions[0].get('tile', {}).get('x')},{companions[0].get('tile', {}).get('y')} "
            f"mode={companions[0].get('mode')} health={companions[0].get('health')} stamina={companions[0].get('stamina')}"
        )
        
        fmt = "You can send these actions:\n"
        fmt += "1. {\"actionType\":\"move_to\",\"companion\":\"Companion1\",\"x\":TILE_X,\"y\":TILE_Y} - Walk to tile\n"
        fmt += "2. {\"actionType\":\"use_tool\",\"companion\":\"Companion1\",\"tool\":\"pickaxe|axe|hoe|watering_can|sword\",\"x\":TILE_X,\"y\":TILE_Y} - Use tool at tile\n"
        fmt += "3. {\"actionType\":\"attack\",\"companion\":\"Companion1\"} - Attack nearby monsters\n"
        fmt += "4. {\"actionType\":\"interact\",\"companion\":\"Companion1\",\"x\":TILE_X,\"y\":TILE_Y} - Interact with object/crop\n"
        fmt += "5. {\"actionType\":\"cast_fishing_rod\",\"companion\":\"Companion1\"} - Cast fishing rod\n"
        fmt += "6. {\"actionType\":\"face_direction\",\"companion\":\"Companion1\",\"direction\":0|1|2|3} - 0=up,1=right,2=down,3=left\n"
        fmt += "7. {\"actionType\":\"warp_to\",\"companion\":\"Companion1\",\"location\":\"Farm|Town|Mountain\",\"x\":X,\"y\":Y} - Teleport to location. IMPORTANT: x,y MUST be one of these whitelisted tiles (verified NPC-reachable): " + json.dumps({k: list(v) for k, v in WARP_WHITELIST.items()}) + ". Any other coordinates will be snapped to the nearest whitelisted tile. Do NOT warp to Mine (first entry triggers an unskippable cutscene).\n"
        fmt += "8. {\"actionType\":\"eat_item\",\"companion\":\"Companion1\"} - Eat food from inventory\n"
        fmt += "9. {\"actionType\":\"set_mode\",\"companion\":\"Companion1\",\"mode\":\"follow|farm|mine|fish|idle|player\"} - Set companion mode\n"
        
        prompt = (
            f"Game State:\n{state_text}\n\n"
            f"Companion Status:\n{companion_info}\n\n"
            f"Actions taken so far: {json.dumps(action_counts)}\n\n"
            f"{fmt}\n"
            "Look at the game state. What should the companion do next? "
            "Respond with ONLY a JSON action object, no explanation. "
            "If you see monsters nearby, attack them. If there are crops, water/harvest them. "
            "If nothing special, explore the area or follow the player."
        )
        
        # Call MiniMax
        response = call_minimax(prompt)
        if not response:
            print("MiniMax returned nothing. Waiting...")
            wait_for_ticks(5)
            continue
        
        # Extract JSON from response
        response_clean = response.strip()
        if '```json' in response_clean:
            response_clean = response_clean.split('```json')[1].split('```')[0].strip()
        elif '```' in response_clean:
            response_clean = response_clean.split('```')[1].split('```')[0].strip()
        
        try:
            action = json.loads(response_clean)
            if not isinstance(action, dict) or 'actionType' not in action:
                print(f"Invalid action (no actionType): {response_clean[:200]}")
                continue
                
            # Log action
            action_type = action['actionType']
            action_counts[action_type] = action_counts.get(action_type, 0) + 1
            print(f"MiniMax decided: {json.dumps(action, ensure_ascii=False)}")

            # 约束 warp_to 落点到白名单（LLM 自选坐标可能 NPC 不可达）
            action = sanitize_action(action)
            if action is None:
                print("  [WARP GUARD] action rejected, skipping execution")
                continue

            # Execute action
            write_action(action)
            wait_for_ticks(3)

            # warp 后检查是否触发剧情事件（如误入 Mine 触发 Marlon 剧情）
            if action.get("actionType") == "warp_to":
                post_state = read_bridge()
                if check_event_state(post_state):
                    print("  [EVENT] Cutscene triggered after warp — waiting for it to finish...")
                    wait_for_ticks(10)
            
        except json.JSONDecodeError:
            print(f"MiniMax returned non-JSON:\n{response_clean[:200]}")
    
    print(f"\n\nDone! Action summary: {json.dumps(action_counts, ensure_ascii=False)}")

if __name__ == "__main__":
    main()
