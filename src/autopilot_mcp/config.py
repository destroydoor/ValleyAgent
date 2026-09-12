"""Configuration for the autopilot MCP server."""
import os

AUTOPILOT_HOST = os.environ.get("AUTOPILOT_HOST", "localhost")
AUTOPILOT_PORT = int(os.environ.get("AUTOPILOT_PORT", "5555"))
AUTOPILOT_BASE_URL = f"http://{AUTOPILOT_HOST}:{AUTOPILOT_PORT}"
