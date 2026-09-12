"""HTTP client for communicating with the ValleyAgent.Autopilot SMAPI mod."""
import asyncio

import httpx

from .config import AUTOPILOT_BASE_URL


async def _request(method: str, path: str, json_data: dict | None = None) -> dict:
    """Send an HTTP request to the game mod and return the JSON response."""
    url = f"{AUTOPILOT_BASE_URL}{path}"
    async with httpx.AsyncClient(timeout=10.0) as client:
        if method == "GET":
            resp = await client.get(url)
        elif method == "POST":
            resp = await client.post(url, json=json_data)
        else:
            raise ValueError(f"Unsupported HTTP method: {method}")
        resp.raise_for_status()
        return resp.json()


async def start_autopilot() -> dict:
    return await _request("POST", "/autopilot/start")


async def stop_autopilot() -> dict:
    return await _request("POST", "/autopilot/stop")


async def get_autopilot_status() -> dict:
    return await _request("GET", "/autopilot/status")


async def set_speed(factor: float) -> dict:
    return await _request("POST", "/speed", {"factor": factor})


async def press_key(key: str, duration_ms: int = 100) -> dict:
    await _request("POST", "/input/key", {"key": key, "pressed": True})
    await asyncio.sleep(duration_ms / 1000.0)
    return await _request("POST", "/input/key", {"key": key, "pressed": False})


async def hold_key(key: str) -> dict:
    return await _request("POST", "/input/key", {"key": key, "pressed": True})


async def release_key(key: str) -> dict:
    return await _request("POST", "/input/key", {"key": key, "pressed": False})


async def move_mouse(x: int, y: int) -> dict:
    return await _request("POST", "/input/mouse", {"x": x, "y": y, "leftButton": False, "rightButton": False})


async def click(x: int, y: int, button: str = "left") -> dict:
    return await _request("POST", "/input/click", {"x": x, "y": y, "button": button})


async def release_all() -> dict:
    return await _request("POST", "/input/release_all")


async def screenshot() -> dict:
    return await _request("GET", "/screenshot")


async def get_game_state() -> dict:
    return await _request("GET", "/state")


async def get_npcs_state() -> dict:
    return await _request("GET", "/state/npcs")
