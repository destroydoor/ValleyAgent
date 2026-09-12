"""MCP Server entry point — registers tools and starts the server."""
from mcp.server.fastmcp import FastMCP

from . import game_client

mcp = FastMCP("stardew-autopilot")


@mcp.tool()
async def start_autopilot() -> str:
    """Start AI autopilot mode — blocks player input and enables AI control."""
    result = await game_client.start_autopilot()
    return f"Autopilot started: {result}"


@mcp.tool()
async def stop_autopilot() -> str:
    """Stop AI autopilot mode — restores player input control."""
    result = await game_client.stop_autopilot()
    return f"Autopilot stopped: {result}"


@mcp.tool()
async def set_speed(factor: float) -> str:
    """Set game speed factor (0.1=10x slow motion, 1.0=normal speed).

    Args:
        factor: Speed factor between 0.1 and 1.0
    """
    result = await game_client.set_speed(factor)
    return f"Speed set to {factor}: {result}"


@mcp.tool()
async def press_key(key: str, duration_ms: int = 100) -> str:
    """Press and release a keyboard key.

    Args:
        key: Key name (e.g. 'W', 'A', 'S', 'D', 'Space', 'Enter', 'Escape')
        duration_ms: How long to hold the key in milliseconds
    """
    result = await game_client.press_key(key, duration_ms)
    return f"Pressed {key} for {duration_ms}ms: {result}"


@mcp.tool()
async def hold_key(key: str) -> str:
    """Hold a keyboard key down (until release_key is called).

    Args:
        key: Key name (e.g. 'W', 'A', 'S', 'D')
    """
    result = await game_client.hold_key(key)
    return f"Holding {key}: {result}"


@mcp.tool()
async def release_key(key: str) -> str:
    """Release a held keyboard key.

    Args:
        key: Key name to release
    """
    result = await game_client.release_key(key)
    return f"Released {key}: {result}"


@mcp.tool()
async def move_mouse(x: int, y: int) -> str:
    """Move the mouse cursor to a specific position.

    Args:
        x: X coordinate in pixels
        y: Y coordinate in pixels
    """
    result = await game_client.move_mouse(x, y)
    return f"Mouse moved to ({x}, {y}): {result}"


@mcp.tool()
async def click(x: int, y: int, button: str = "left") -> str:
    """Click at a specific position.

    Args:
        x: X coordinate in pixels
        y: Y coordinate in pixels
        button: 'left' or 'right'
    """
    result = await game_client.click(x, y, button)
    return f"Clicked {button} at ({x}, {y}): {result}"


@mcp.tool()
async def release_all() -> str:
    """Release all held keys and mouse buttons."""
    result = await game_client.release_all()
    return f"All inputs released: {result}"


@mcp.tool()
async def screenshot() -> str:
    """Take a screenshot of the current game screen. Returns base64-encoded PNG image."""
    result = await game_client.screenshot()
    if "image" in result:
        return f"Screenshot captured: {result['width']}x{result['height']}, base64 image data available"
    return f"Screenshot failed: {result}"


@mcp.tool()
async def get_game_state() -> str:
    """Get current game state including player position, time, season, money, etc."""
    result = await game_client.get_game_state()
    return f"Game state: {result}"


@mcp.tool()
async def get_npcs_state() -> str:
    """Get list of visible NPCs in current location with their positions."""
    result = await game_client.get_npcs_state()
    return f"NPCs: {result}"


if __name__ == "__main__":
    mcp.run()
