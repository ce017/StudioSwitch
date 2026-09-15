r"""Hook/unhook tests against synthetic MCP client configs. Touches nothing outside a temp folder.

    py tests/test_hooks.py [path\to\StudioSwitch.exe]
"""
import json, os, pathlib, subprocess, sys, tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "StudioSwitch.exe"

CLAUDE_APP = """{
  "mcpServers": {
    "Roblox_Studio": {
      "command": "cmd.exe",
      "args": [
        "/c",
        "cd /d %LOCALAPPDATA%\\\\Roblox && .\\\\mcp.bat"
      ]
    },
    "blender": {
      "command": "uv",
      "args": ["run", "blender-mcp"]
    }
  },
  "preferences": { "theme": "dark" }
}
"""

CLAUDE_CODE = """{
  "numStartups": 12,
  "mcpServers": {
    "roblox": { "type": "stdio", "command": "cmd.exe", "args": ["/c", "%LOCALAPPDATA%\\\\Roblox\\\\mcp.bat"], "env": {} }
  },
  "projects": {
    "C:/work/game": {
      "mcpServers": {
        "Roblox Studio": {
          "type": "stdio",
          "command": "cmd.exe",
          "args": ["/c", "%LOCALAPPDATA%\\\\Roblox\\\\mcp.bat"],
          "env": { "FOO": "bar" }
        }
      }
    }
  }
}
"""

VSCODE_JSONC = """// VS Code user MCP config
{
  "servers": {
    /* Roblox */
    "roblox-studio": {
      "type": "stdio",
      "command": "C:\\\\Users\\\\someone\\\\AppData\\\\Local\\\\Roblox\\\\Versions\\\\version-1\\\\StudioMCP.exe",
      "args": ["--stdio"]
    },
    "github": { "type": "http", "url": "https://api.githubcopilot.com/mcp/" }
  }
}
"""

UNRELATED = """{ "mcpServers": { "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem"] } } }
"""

CRLF_BOM = "\ufeff" + CLAUDE_APP.replace("\n", "\r\n")

ok = True


def check(label, cond, detail=""):
    global ok
    ok &= bool(cond)
    print(("PASS " if cond else "FAIL ") + label + ("" if cond else "  -> " + str(detail)[:500]))


def run(env, *args):
    r = subprocess.run([str(EXE), *args], env=env, capture_output=True, text=True, timeout=60)
    return r.returncode, r.stdout + r.stderr


with tempfile.TemporaryDirectory() as tmp:
    tmp = pathlib.Path(tmp)
    env = dict(os.environ, STUDIOSWITCH_DIR=str(tmp / "appdir"))
    shim = str(tmp / "appdir" / "StudioSwitchShim.exe")
    files = {
        "claude_desktop_config.json": CLAUDE_APP,
        "claude.json": CLAUDE_CODE,
        "vscode_mcp.json": VSCODE_JSONC,
        "unrelated.json": UNRELATED,
        "crlf_bom.json": CRLF_BOM,
    }
    paths = {}
    for name, text in files.items():
        p = tmp / name
        p.write_bytes(text.encode("utf-8"))
        paths[name] = str(p)
    originals = {n: pathlib.Path(p).read_bytes() for n, p in paths.items()}

    code, out = run(env, "status", *paths.values())
    check("status: exit 0", code == 0, out)
    check("status: claude app entry not hooked", "| not hooked | " + paths["claude_desktop_config.json"] in out, out)
    check("status: claude code entries not hooked", "| not hooked | " + paths["claude.json"] in out, out)
    check("status: unrelated config has no entry", "| no Studio MCP entry | " + paths["unrelated.json"] in out, out)

    code, out = run(env, "hook", *paths.values())
    check("hook: exit 0", code == 0, out)
    check("hook: shim installed into STUDIOSWITCH_DIR", os.path.exists(shim), out)

    app = json.loads(pathlib.Path(paths["claude_desktop_config.json"]).read_text("utf-8"))
    rs = app["mcpServers"]["Roblox_Studio"]
    check("claude app: command -> shim, args emptied", rs["command"] == shim and rs["args"] == [], rs)
    check("claude app: other servers and keys untouched",
          app["mcpServers"]["blender"] == {"command": "uv", "args": ["run", "blender-mcp"]} and app["preferences"] == {"theme": "dark"}, app)

    cc = json.loads(pathlib.Path(paths["claude.json"]).read_text("utf-8"))
    top = cc["mcpServers"]["roblox"]
    proj = cc["projects"]["C:/work/game"]["mcpServers"]["Roblox Studio"]
    check("claude code: top-level entry hooked, type/env kept", top["command"] == shim and top["type"] == "stdio" and top["env"] == {}, top)
    check("claude code: project entry hooked, env kept", proj["command"] == shim and proj["env"] == {"FOO": "bar"}, proj)
    check("claude code: unrelated keys untouched", cc["numStartups"] == 12, cc)

    vs = pathlib.Path(paths["vscode_mcp.json"]).read_text("utf-8")
    check("vscode jsonc: comments survive", vs.startswith("// VS Code user MCP config") and "/* Roblox */" in vs, vs)
    check("vscode jsonc: direct StudioMCP.exe args forwarded", '"args": ["--stdio"]' in vs and json.dumps(shim)[1:-1] in vs, vs)
    check("vscode jsonc: http server untouched", '"github": { "type": "http", "url": "https://api.githubcopilot.com/mcp/" }' in vs, vs)

    check("unrelated: file byte-identical", pathlib.Path(paths["unrelated.json"]).read_bytes() == originals["unrelated.json"])
    bom_after = pathlib.Path(paths["crlf_bom.json"]).read_bytes()
    check("crlf+bom: BOM kept", bom_after.startswith(b"\xef\xbb\xbf"), bom_after[:20])
    check("crlf+bom: no bare LF introduced", b"\n" not in bom_after.replace(b"\r\n", b""), bom_after)

    code, out = run(env, "status", *paths.values())
    check("status after hook: the 4 configs with entries are hooked", out.count("| hooked |") == 4 and "not hooked" not in out, out)

    code, out = run(env, "hook", *paths.values())
    check("hook twice: reports already hooked", out.count("already hooked") == 4, out)

    code, out = run(env, "unhook", *paths.values())
    check("unhook: exit 0", code == 0, out)
    for name in files:
        check("unhook: %s restored byte-for-byte" % name, pathlib.Path(paths[name]).read_bytes() == originals[name],
              pathlib.Path(paths[name]).read_bytes()[:300])

    backups = [p for p in os.listdir(tmp) if ".studioswitch-" in p and p.endswith(".bak")]
    check("backups written next to edited configs", len(backups) >= 4, backups)

print("ALL PASS" if ok else "SOME FAILED")
sys.exit(0 if ok else 1)
