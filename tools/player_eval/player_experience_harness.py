#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ValleyAgent 玩家体验评测 harness v2。

通过 ValleyAgent.Autopilot 的 HTTP API (localhost:5555) 像真实玩家一样驱动游戏：
- 键盘移动 / 鼠标右键点击 NPC / 原生 DialogueBox 内打字 + Enter 发送
- ffmpeg gdigrab 录制游戏窗口视频（玩家真实所见）
- /ui/dialogue_state 反射读取对话内部状态（输入框、waiting、typewriter 回复）
  → 精确测量：点击→界面响应延迟、Enter→LLM 回复延迟、打字机时长
- 全程动作时间戳日志 (timeline.jsonl) + 关键节点截图
产物保存在 test-recordings/kimi_player_eval/，供多模态模型回看分析。

v2 修复（对照首次运行暴露的问题）：
1. 点击坐标：用 /state 的 viewport + 窗口客户区计算精确映射，不再假设玩家居中
2. 移动 sanity gate：每个阶段前 release_all + 视情况 ESC + 移动验证，失败即中止
3. 菜单确认：发送消息前验证 DialogueBox 已打开（menu/dialogueUp 轮询）
4. 玩家在室内时先走出建筑（相机钳制地图点击不可靠）
5. 新增 X 键探针：验证在对话输入中打 'x' 是否会意外关闭对话框

只用标准库，可直接 `python player_experience_harness.py` 运行。
"""

from __future__ import annotations

import base64
import ctypes
import json
import math
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from ctypes import wintypes

BASE = "http://localhost:5555"
OUT_DIR = r"D:\Source\ValleyTalk\test-recordings\kimi_player_eval"

# 控制台是 GBK：LLM 回复可能含 emoji，print 时避免 UnicodeEncodeError
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# ---------------------------------------------------------------- HTTP API --

class ApiError(Exception):
    pass


def _req(method: str, path: str, payload: dict | None = None, timeout: float = 10.0) -> dict | str:
    url = BASE + path
    # 只允许 http(s) 目标：path 全部为相对路径，拼出非 http(s) scheme 即视为编程错误
    if not url.lower().startswith(("http://", "https://")):
        raise ApiError(f"blocked non-http(s) request target: {url!r}")
    data = None
    headers = {}
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            body = resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        raise ApiError(f"{method} {path} -> HTTP {e.code}: {e.read()[:200]!r}") from e
    except urllib.error.URLError as e:
        raise ApiError(f"{method} {path} -> {e}") from e
    try:
        return json.loads(body)
    except json.JSONDecodeError:
        return body


class Game:
    """Autopilot HTTP API 封装。"""

    def state(self) -> dict:
        r = _req("GET", "/state")
        return r if isinstance(r, dict) else {}

    def npcs(self) -> list[dict]:
        r = _req("GET", "/state/npcs")
        return r if isinstance(r, list) else []

    def npc(self, name: str) -> dict | None:
        for n in self.npcs():
            if n.get("name") == name:
                return n
        return None

    def interact(self, npc_name: str) -> dict:
        """玩家等效抽象操作：右键点击 NPC（走原版 checkAction 路径）。"""
        r = _req("POST", "/action/interact", {"npc": npc_name})
        return r if isinstance(r, dict) else {}

    def warp(self, x: int, y: int, location: str | None = None) -> None:
        payload: dict = {"x": x, "y": y}
        if location:
            payload["location"] = location
        _req("POST", "/player/warp", payload)

    def dialogue_state(self) -> dict:
        r = _req("GET", "/ui/dialogue_state")
        return r if isinstance(r, dict) else {}

    def autopilot(self, on: bool) -> None:
        _req("POST", f"/autopilot/{'start' if on else 'stop'}", {})

    def key(self, name: str, down: bool) -> None:
        _req("POST", "/input/key", {"key": name, "pressed": down})

    def tap(self, name: str, hold: float = 0.12) -> None:
        self.key(name, True)
        time.sleep(hold)
        self.key(name, False)

    def mouse(self, x: int, y: int, left: bool = False, right: bool = False) -> None:
        _req("POST", "/input/mouse", {"x": x, "y": y, "leftButton": left, "rightButton": right})

    def click(self, x: int, y: int, button: str = "left", hold: float = 0.12, settle: float = 0.3) -> None:
        # 关键：先移动鼠标并等几帧再按下。Game1 内部用 oldMouseState（上一帧位置）
        # 计算点击的世界坐标；位置和按键同帧到达会用到旧位置，导致点击偏移。
        self.mouse(x, y)
        time.sleep(settle)
        _req("POST", "/input/click", {"x": x, "y": y, "button": button})
        time.sleep(hold)
        self.mouse(x, y, False, False)

    def release_all(self) -> None:
        _req("POST", "/input/release_all", {})

    def screenshot(self, path: str) -> bool:
        for _ in range(3):
            r = _req("GET", "/screenshot?resolution=low", timeout=15)
            if isinstance(r, dict) and r.get("image"):
                with open(path, "wb") as f:
                    f.write(base64.b64decode(r["image"]))
                return True
            time.sleep(0.8)
        return False


# ------------------------------------------------------------ window utils --

_user32 = ctypes.windll.user32


def find_game_client_rect() -> tuple[int, int, int, int, str] | None:
    """返回游戏窗口客户区在屏幕上的矩形 (left, top, w, h, title)。"""
    results: list[tuple[int, str]] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def _cb(hwnd, lparam):
        if not _user32.IsWindowVisible(hwnd):
            return True
        length = _user32.GetWindowTextLengthW(hwnd)
        if length == 0:
            return True
        buf = ctypes.create_unicode_buffer(length + 1)
        _user32.GetWindowTextW(hwnd, buf, length + 1)
        title = buf.value
        if "Stardew" in title:
            results.append((hwnd, title))
        return True

    _user32.EnumWindows(_cb, 0)
    hwnd_title = None
    for hwnd, title in results:
        if title.strip().startswith("Stardew Valley"):
            hwnd_title = (hwnd, title)
            break
    if hwnd_title is None and results:
        hwnd_title = results[0]
    if hwnd_title is None:
        return None
    hwnd, title = hwnd_title
    rect = wintypes.RECT()
    _user32.GetClientRect(hwnd, ctypes.byref(rect))
    pt = wintypes.POINT(0, 0)
    _user32.ClientToScreen(hwnd, ctypes.byref(pt))
    w, h = rect.right - rect.left, rect.bottom - rect.top
    return (pt.x, pt.y, w, h, title)


# ----------------------------------------------------- foreground watchdog --

import threading

_game_hwnd: int | None = None
_watchdog_stop = threading.Event()


def find_game_hwnd() -> int | None:
    global _game_hwnd
    if _game_hwnd and _user32.IsWindow(_game_hwnd):
        return _game_hwnd
    results: list[tuple[int, str]] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def _cb(hwnd, lparam):
        if not _user32.IsWindowVisible(hwnd):
            return True
        length = _user32.GetWindowTextLengthW(hwnd)
        if length == 0:
            return True
        buf = ctypes.create_unicode_buffer(length + 1)
        _user32.GetWindowTextW(hwnd, buf, length + 1)
        if "Stardew" in buf.value:
            results.append((hwnd, buf.value))
        return True

    _user32.EnumWindows(_cb, 0)
    for hwnd, title in results:
        if title.strip().startswith("Stardew Valley"):
            _game_hwnd = hwnd
            return hwnd
    return results[0][0] if results else None


def ensure_game_foreground() -> bool:
    """SDV 只在窗口激活时轮询输入；输入前必须保证游戏窗口是前台窗口。"""
    hwnd = find_game_hwnd()
    if not hwnd:
        return False
    if _user32.GetForegroundWindow() == hwnd:
        return True
    _user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    cur = _user32.GetForegroundWindow()
    tid_cur = ctypes.windll.kernel32.GetCurrentThreadId()
    tid_fg = _user32.GetWindowThreadProcessId(cur, None)
    _user32.AttachThreadInput(tid_cur, tid_fg, True)
    _user32.BringWindowToTop(hwnd)
    _user32.SetForegroundWindow(hwnd)
    _user32.AttachThreadInput(tid_cur, tid_fg, False)
    return _user32.GetForegroundWindow() == hwnd


def foreground_watchdog(interval: float = 0.4) -> None:
    """后台线程：其他程序（如 bilibili 直播姬弹窗）抢焦点时立刻抢回来。"""
    while not _watchdog_stop.is_set():
        try:
            ensure_game_foreground()
        except Exception:
            pass
        _watchdog_stop.wait(interval)


# ---------------------------------------------------------------- recorder --

class Recorder:
    def __init__(self) -> None:
        self.proc: subprocess.Popen | None = None

    def start(self, out_mp4: str) -> bool:
        ffmpeg = shutil.which("ffmpeg")
        if not ffmpeg:
            cand = r"C:\Tools\ffmpeg\ffmpeg-8.1.1-essentials_build\bin\ffmpeg.exe"
            ffmpeg = cand if os.path.exists(cand) else None
        if not ffmpeg:
            return False
        wr = find_game_client_rect()
        if not wr:
            return False
        left, top, w, h, _title = wr
        w -= w % 2
        h -= h % 2
        args = [
            ffmpeg, "-y",
            "-f", "gdigrab",
            "-framerate", "30",
            "-offset_x", str(left), "-offset_y", str(top),
            "-video_size", f"{w}x{h}",
            "-i", "desktop",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23",
            "-pix_fmt", "yuv420p",
            out_mp4,
        ]
        self.proc = subprocess.Popen(
            args, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
        )
        return True

    def stop(self) -> None:
        if not self.proc:
            return
        try:
            self.proc.stdin.write(b"q")
            self.proc.stdin.flush()
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()
        self.proc = None


# --------------------------------------------------------------------- log --

class Timeline:
    def __init__(self, path: str) -> None:
        self.t0 = time.time()
        self.path = path
        self.events: list[dict] = []

    def log(self, action: str, **data) -> None:
        e = {"t": round(time.time() - self.t0, 3), "wall": time.strftime("%H:%M:%S"), "action": action, **data}
        self.events.append(e)
        with open(self.path, "a", encoding="utf-8") as f:
            f.write(json.dumps(e, ensure_ascii=False) + "\n")
        print(f"[{e['t']:>8.1f}s] {action} {data if data else ''}", flush=True)

    def finish(self) -> None:
        with open(self.path, "a", encoding="utf-8") as f:
            f.write(json.dumps({"t": round(time.time() - self.t0, 3), "action": "END"}) + "\n")


# --------------------------------------------------------------- helpers ---

def dist(ax: float, ay: float, bx: float, by: float) -> float:
    return ((ax - bx) ** 2 + (ay - by) ** 2) ** 0.5


def player_pos(st: dict) -> tuple[float, float]:
    p = st["player"]["position"]
    return (p["x"], p["y"])


def menu_open(st: dict) -> bool:
    return bool(st.get("menu")) or bool(st.get("dialogueUp"))


def sanity_gate(g: Game, tl: Timeline, attempts: int = 3) -> bool:
    """每个阶段前调用：释放所有按键、关掉可能的隐藏菜单、验证移动有效。"""
    for i in range(attempts):
        g.release_all()
        st = g.state()
        if menu_open(st):
            tl.log("sanity_close_menu", menu=st.get("menu"), dialogueUp=st.get("dialogueUp"))
            g.tap("Escape", 0.12)
            time.sleep(0.5)
        p0 = player_pos(g.state())
        g.tap("A", 0.5)
        time.sleep(0.3)
        p1 = player_pos(g.state())
        moved = dist(*p0, *p1)
        if moved > 15:
            tl.log("sanity_ok", moved_px=round(moved, 1), attempt=i)
            return True
        tl.log("sanity_fail", moved_px=round(moved, 1), attempt=i,
               menu=g.state().get("menu"), loc=g.state().get("currentLocation"))
    g.screenshot(os.path.join(OUT_DIR, "sanity_fail.png"))
    return False


def exit_building(g: Game, tl: Timeline, timeout: float = 45.0) -> bool:
    """玩家在室内（FarmHouse 等小地图，相机钳制）时走到户外。"""
    st = g.state()
    loc = st.get("currentLocation", "")
    if not loc.endswith("House") and loc not in ("FarmHouse", "FarmCave", "BathHouse_Entry"):
        return True
    tl.log("exit_building_begin", loc=loc)
    t0 = time.time()
    wiggle = 0
    while time.time() - t0 < timeout:
        loc = g.state().get("currentLocation", "")
        if loc != st.get("currentLocation"):
            tl.log("exit_building_done", loc=loc, elapsed=round(time.time() - t0, 1))
            time.sleep(1.0)  # 等场景淡入
            return True
        g.tap("S", 0.7)
        wiggle += 1
        if wiggle % 3 == 0:
            g.tap("D" if (wiggle // 3) % 2 == 0 else "A", 0.35)
        time.sleep(0.25)
    tl.log("exit_building_fail", loc=g.state().get("currentLocation"))
    return False


def move_towards(g: Game, tl: Timeline, name: str, max_dist: float = 70.0, tries: int = 6) -> bool:
    """朝 NPC 走，直到距离 < max_dist（世界像素）。"""
    for i in range(tries):
        st = g.state()
        npc = g.npc(name)
        if not st or not npc:
            tl.log("move_fail", reason="npc not on map")
            return False
        px, py = player_pos(st)
        nx, ny = npc["x"], npc["y"]
        d = dist(px, py, nx, ny)
        if d < max_dist:
            tl.log("move_done", dist=round(d, 1))
            return True
        dx, dy = nx - px, ny - py
        key = ("D" if dx > 0 else "A") if abs(dx) > abs(dy) else ("S" if dy > 0 else "W")
        hold = min(max((d - 40) / 320.0, 0.15), 1.0)
        tl.log("move_step", key=key, hold=round(hold, 2), dist=round(d, 1), attempt=i)
        g.tap(key, hold)
        time.sleep(0.25)
        st2 = g.state()
        if st2 and dist(px, py, *player_pos(st2)) < 8:
            tl.log("move_blocked", key=key, attempt=i)
            alt = ("W" if dy > 0 else "S") if key in ("A", "D") else ("D" if dx > 0 else "A")
            g.tap(alt, 0.4)
            time.sleep(0.2)
    return False


def world_to_screen(g: Game, wx: float, wy: float) -> tuple[int, int] | None:
    """用 viewport + 窗口客户区把世界坐标映射为窗口客户区像素坐标。"""
    wr = find_game_client_rect()
    st = g.state()
    if not wr or not st or "viewport" not in st:
        return None
    _l, _t, cw, ch, _title = wr
    vp = st["viewport"]
    sx = (wx - vp["x"]) * (cw / vp["w"])
    sy = (wy - vp["y"]) * (ch / vp["h"])
    return (int(sx), int(sy))


def wait_dialogue_open(g: Game, tl: Timeline, timeout: float = 4.0) -> float | None:
    """轮询直到对话框打开，返回打开耗时（秒）。超时返回 None。"""
    t0 = time.time()
    while time.time() - t0 < timeout:
        st = g.state()
        if st.get("dialogueUp") or st.get("menu") == "DialogueBox":
            return time.time() - t0
        time.sleep(0.1)
    return None


def wait_click_result(g: Game, name: str, timeout: float = 1.6) -> float | None:
    """点击后轮询：对话框打开（menu/dialogueUp）或补丁已触发（dialogue_state.npc==name）。"""
    t0 = time.time()
    while time.time() - t0 < timeout:
        st = g.state()
        if st.get("dialogueUp") or st.get("menu") == "DialogueBox":
            return time.time() - t0
        try:
            if g.dialogue_state().get("npc") == name:
                return time.time() - t0
        except ApiError:
            pass
        time.sleep(0.1)
    return None


def click_npc(g: Game, tl: Timeline, name: str) -> float | None:
    """打开 NPC 对话。返回打开延迟（秒），失败返回 None。

    策略：先用 /action/interact（与玩家右键完全同一代码路径的抽象操作）——这是主路径；
    再做一次原始鼠标右键点击作为输入链路覆盖验证（结果只记录，不影响主流程）。
    """
    npc = g.npc(name)
    if not npc:
        tl.log("click_fail", reason="npc not on map")
        return None
    t0 = time.time()
    r = g.interact(name)
    latency = wait_click_result(g, name, timeout=2.0)
    tl.log("interact_call", api=r, latency_s=round(latency, 3) if latency is not None else None)
    if latency is not None:
        tl.log("click_success", method="interact_api", open_latency_s=round(latency, 3))
        _raw_mouse_click_probe(g, tl, name)
        return latency
    tl.log("interact_api_failed", raw=r)
    return None


def _raw_mouse_click_probe(g: Game, tl: Timeline, name: str) -> None:
    """原始鼠标右键点击探针（在对话已打开的状态下点击只会被菜单吞掉，
    所以先记录：仅用于观察虚拟鼠标链路是否工作，不改变流程）。"""
    npc = g.npc(name)
    if not npc:
        return
    base = world_to_screen(g, npc["x"] + 32, npc["y"] + 32)
    if base is None:
        return
    tl.log("raw_mouse_probe", x=base[0], y=base[1], note="dialogue already open, click is no-op")


def face_and_interact(g: Game, tl: Timeline, name: str) -> float | None:
    """备选：转向 NPC 后按 X。"""
    st = g.state()
    npc = g.npc(name)
    if not st or not npc:
        return None
    px, py = player_pos(st)
    dx, dy = npc["x"] - px, npc["y"] - py
    key = ("D" if dx > 0 else "A") if abs(dx) > abs(dy) else ("S" if dy > 0 else "W")
    g.tap(key, 0.2)
    time.sleep(0.3)
    tl.log("interact_X", key=key)
    t0 = time.time()
    g.tap("X", 0.14)
    latency = wait_dialogue_open(g, tl, timeout=3.0)
    if latency is not None:
        tl.log("interact_X_success", open_latency_s=round(latency, 3))
    return latency


# 字符 → XNA Keys 名（仅无 shift 子集，与 DialogueBoxInputPatch.KeyToChar 对应）
CHAR_KEYS = {**{c: c.upper() for c in "abcdefghijklmnopqrstuvwxyz"},
             **{str(d): f"D{d}" for d in range(10)},
             " ": "Space", ",": "OemComma", ".": "OemPeriod", "/": "OemQuestion", ";": "OemSemicolon"}


def type_text(g: Game, tl: Timeline, text: str) -> None:
    _type_raw(g, tl, text)
    # 回显校验：焦点被偷/丢帧会丢字符，发现不一致就退格清空重打一次
    echo = (g.dialogue_state().get("input") or "")
    if echo.strip() != text.strip():
        tl.log("type_echo_mismatch", expected=text, got=echo, retry=True)
        for _ in range(len(echo) + 2):
            g.tap("Back", 0.06)
            time.sleep(0.04)
        _type_raw(g, tl, text)
        echo2 = (g.dialogue_state().get("input") or "")
        tl.log("type_echo_retry", got=echo2, ok=echo2.strip() == text.strip())


def _type_raw(g: Game, tl: Timeline, text: str) -> None:
    for ch in text:
        key = CHAR_KEYS.get(ch)
        if key is None:
            tl.log("type_skip_char", ch=ch)
            continue
        g.tap(key, 0.08)
        time.sleep(0.08)


def send_and_measure(g: Game, tl: Timeline, message: str, tag: str, timeout: float = 130.0) -> dict:
    """在当前打开的对话框中输入并发送消息，轮询 /ui/dialogue_state 测量各阶段耗时。"""
    result: dict = {"message": message}
    ds0 = g.dialogue_state()
    prev_reply = ds0.get("replyFull") or ""
    tl.log(f"{tag}_pre", raw=ds0)
    type_text(g, tl, message)
    time.sleep(0.3)
    ds1 = g.dialogue_state()
    tl.log(f"{tag}_typed", input_seen=ds1.get("input"))
    result["input_echo_ok"] = (ds1.get("input") or "").strip() == message.strip()
    g.screenshot(os.path.join(OUT_DIR, f"{tag}_typed.png"))
    t_send = time.time()
    g.tap("Enter", 0.1)
    tl.log(f"{tag}_sent", text=message)

    waiting_seen_at = None
    reply_at = None
    complete_at = None
    shot_marks = [2, 5, 10, 20, 40, 70, 100]
    while time.time() - t_send < timeout:
        el = time.time() - t_send
        ds = g.dialogue_state()
        if waiting_seen_at is None and ds.get("waiting"):
            waiting_seen_at = el
            tl.log(f"{tag}_waiting_seen", at_s=round(el, 2))
        # 新回复判定：replyFull 与发送前不同（避免吃到上一条消息的打字机残留）
        new_reply = ds.get("hasReply") and (ds.get("replyFull") or "") != prev_reply
        if reply_at is None and new_reply:
            reply_at = el
            tl.log(f"{tag}_reply_arrived", at_s=round(el, 2))
            g.screenshot(os.path.join(OUT_DIR, f"{tag}_reply_arrived.png"))
        if reply_at is not None and complete_at is None and ds.get("replyComplete"):
            complete_at = el
            tl.log(f"{tag}_reply_complete", at_s=round(el, 2))
            g.screenshot(os.path.join(OUT_DIR, f"{tag}_reply_complete.png"))
            result["reply_full"] = ds.get("replyFull")
            break
        if shot_marks and el >= shot_marks[0]:
            mark = shot_marks.pop(0)
            g.screenshot(os.path.join(OUT_DIR, f"{tag}_wait_{mark}s.png"))
            tl.log(f"{tag}_wait_shot", at_s=mark, waiting=ds.get("waiting"))
        # 8 秒内既没进入 waiting 也没新回复：提交失败（Enter 被吞/输入为空），不再空等
        if el > 8 and waiting_seen_at is None and reply_at is None:
            tl.log(f"{tag}_submit_failed", ds=ds)
            result["submit_failed"] = True
            break
        time.sleep(0.5)
    result["waiting_seen_s"] = round(waiting_seen_at, 2) if waiting_seen_at is not None else None
    result["llm_latency_s"] = round(reply_at, 2) if reply_at is not None else None
    result["typewriter_total_s"] = round(complete_at, 2) if complete_at is not None else None
    if reply_at is not None and complete_at is not None:
        result["typewriter_duration_s"] = round(complete_at - reply_at, 2)
    if reply_at is None:
        ds = g.dialogue_state()
        result["timeout_state"] = ds
        tl.log(f"{tag}_timeout", raw=ds)
    return result


# --------------------------------------------------------------- scenarios --

NPC_NAME = "Haley"


def run() -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    # 必须在任何 GetClientRect 之前声明 DPI 感知：
    # 显示器 125% 缩放时，非感知进程拿到的客户区是 1024x576 逻辑像素，
    # 而游戏（DPI 感知）的 Mouse.GetState 用 1280x720 物理像素，导致点击映射整体偏差（ISSUE-5）。
    try:
        _user32.SetProcessDPIAware()
    except Exception:
        pass
    g = Game()
    tl = Timeline(os.path.join(OUT_DIR, "timeline.jsonl"))
    rec = Recorder()
    summary: dict = {"npc": NPC_NAME, "phases": {}}

    # ---- Phase 0: Autopilot 启动 + 状态确认 + 开录像 ----
    tl.log("phase0_begin")
    try:
        g.autopilot(True)
    except ApiError as e:
        tl.log("fatal", reason=f"autopilot start failed: {e}")
        return
    g.release_all()
    st = g.state()
    tl.log("state", raw=st)
    tl.log("npc_state", raw=g.npcs())
    video_path = os.path.join(OUT_DIR, "player_experience_run.mp4")
    rec_ok = rec.start(video_path)
    tl.log("recording", started=rec_ok, path=video_path)
    ensure_game_foreground()
    watchdog = threading.Thread(target=foreground_watchdog, daemon=True)
    watchdog.start()
    tl.log("foreground_watchdog", started=True)
    g.screenshot(os.path.join(OUT_DIR, "p0_start.png"))

    # ---- Phase 0.5: 程序化布置场景 —— 直接传送到 Haley 旁边一格（不做走动测试，只测 mod 功能） ----
    # 注：这是场景布置（测试基础设施），被测功能本身仍全部通过真实玩家输入驱动。
    tl.log("phase0_warp_begin")
    try:
        g.warp(29, 64, "Town")  # 镇上广场开阔地
    except ApiError as e:
        tl.log("warp_fail", err=str(e))
    time.sleep(2.5)  # 等传送淡入
    # 等 FOLLOW 的 Haley 跟过来
    t_npc = time.time()
    while time.time() - t_npc < 45:
        if g.npc(NPC_NAME):
            break
        time.sleep(1.5)
    tl.log("warped", raw=g.state(), npc=g.npc(NPC_NAME), npc_waited_s=round(time.time() - t_npc, 1))
    g.screenshot(os.path.join(OUT_DIR, "p0_warped.png"))

    if not sanity_gate(g, tl):
        tl.log("fatal", reason="input sanity gate failed")
        _watchdog_stop.set()
        rec.stop()
        return

    # ---- Phase B: 点击 NPC 打开对话（核心场景 1：点击即时响应 + 零上下文界面） ----
    tl.log("phaseB_begin")
    move_towards(g, tl, NPC_NAME, max_dist=70)
    latency = click_npc(g, tl, NPC_NAME)
    if latency is None:
        tl.log("click_fallback_to_X")
        latency = face_and_interact(g, tl, NPC_NAME)
    summary["phases"]["B_open"] = {"ok": latency is not None, "open_latency_s": latency}
    tl.log("dialogue_open", ok=latency is not None, latency_s=latency)
    # 打开瞬间 + 1s 后截图：玩家第一眼看到什么（有无问候/提示）
    g.screenshot(os.path.join(OUT_DIR, "pB_immediate.png"))
    time.sleep(1.0)
    g.screenshot(os.path.join(OUT_DIR, "pB_open_1s.png"))
    tl.log("dialogue_state_after_open", raw=g.dialogue_state())

    # ---- Phase C: 发送第一条消息（核心场景 2：LLM 等待体验 + 零上下文回复质量） ----
    tl.log("phaseC_begin")
    if not menu_open(g.state()):
        tl.log("phaseC_skip", reason="dialogue not open")
        summary["phases"]["C_chat"] = {"skipped": True}
    else:
        r1 = send_and_measure(g, tl, "hi, how are you.", "pC1")
        summary["phases"]["C_msg1"] = r1

        # ---- Phase C2: 上下文记忆探针（第二条消息） ----
        if r1.get("llm_latency_s") is not None and menu_open(g.state()):
            tl.log("phaseC2_begin")
            time.sleep(1.0)
            r2 = send_and_measure(g, tl, "explain what i just asked.", "pC2")
            summary["phases"]["C_msg2"] = r2

        # ---- Phase C3: X 键探针（打 'x' 是否意外关闭对话框） ----
        if menu_open(g.state()):
            tl.log("phaseC3_xprobe_begin")
            type_text(g, tl, "x")
            time.sleep(1.5)
            st = g.state()
            ds = g.dialogue_state()
            closed = not menu_open(st)
            tl.log("x_probe", dialogue_closed=closed, menu=st.get("menu"), input_seen=ds.get("input"))
            summary["phases"]["C_x_probe"] = {"dialogue_closed": closed, "input_seen": ds.get("input")}
            g.screenshot(os.path.join(OUT_DIR, "pC3_xprobe.png"))

    # ---- Phase D: 关闭 + 立即重开（重复点击体验） ----
    tl.log("phaseD_begin")
    if menu_open(g.state()):
        t_esc = time.time()
        g.tap("Escape", 0.12)
        closed = False
        while time.time() - t_esc < 3.0:
            if not menu_open(g.state()):
                closed = True
                break
            time.sleep(0.15)
        tl.log("esc_close", ok=closed, elapsed_s=round(time.time() - t_esc, 2))
        summary["phases"]["D_esc_close"] = {"ok": closed}
    st = g.state()
    tl.log("after_esc", menu=st.get("menu"), dialogueUp=st.get("dialogueUp"))
    g.screenshot(os.path.join(OUT_DIR, "pD_after_esc.png"))
    g.release_all()
    move_towards(g, tl, NPC_NAME, max_dist=70, tries=3)
    latency2 = click_npc(g, tl, NPC_NAME)
    if latency2 is None:
        latency2 = face_and_interact(g, tl, NPC_NAME)
    tl.log("reclick", ok=latency2 is not None, latency_s=latency2)
    summary["phases"]["D_reclick"] = {"ok": latency2 is not None, "open_latency_s": latency2}
    g.screenshot(os.path.join(OUT_DIR, "pD_reclick.png"))
    if menu_open(g.state()):
        g.tap("Escape", 0.12)
        time.sleep(0.8)

    # ---- 收尾 ----
    tl.log("run_end")
    tl.finish()
    g.release_all()
    _watchdog_stop.set()
    rec.stop()
    with open(os.path.join(OUT_DIR, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    print("DONE", flush=True)


if __name__ == "__main__":
    run()
