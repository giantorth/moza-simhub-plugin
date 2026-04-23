"""MCP server exposing SimHub process control via sim/simhub.sh.

Thin wrapper: each tool shells out to the bash script and returns its
stdout/stderr plus exit code. Keeps the Proton env-scrub logic in one place
(the shell script) instead of re-implementing it in Python.
"""

import subprocess
from pathlib import Path

from mcp.server.fastmcp import FastMCP

_server = FastMCP("simhub-control")
_SCRIPT = Path(__file__).parent / "simhub.sh"


def _run(subcmd: str, timeout: float = 30.0) -> dict:
    try:
        proc = subprocess.run(
            ["bash", str(_SCRIPT), subcmd],
            capture_output=True, text=True, timeout=timeout,
        )
    except subprocess.TimeoutExpired as e:
        return {"error": f"{subcmd} timed out after {timeout}s",
                "stdout": e.stdout or "", "stderr": e.stderr or ""}
    except FileNotFoundError:
        return {"error": f"Script not found: {_SCRIPT}"}
    return {
        "returncode": proc.returncode,
        "stdout": proc.stdout.strip(),
        "stderr": proc.stderr.strip(),
    }


@_server.tool()
def simhub_start() -> dict:
    """Launch SimHub (SimHubWPF.exe) in its Proton prefix via protontricks-launch.
    No-op if already running."""
    return _run("start")


@_server.tool()
def simhub_stop() -> dict:
    """Terminate SimHub and its protontricks wrapper processes. SIGTERM with
    10s grace, then SIGKILL."""
    return _run("stop", timeout=45.0)


@_server.tool()
def simhub_status() -> dict:
    """Report whether SimHub is running; includes matching process command lines."""
    return _run("status", timeout=10.0)


if __name__ == "__main__":
    _server.run(transport="stdio")
