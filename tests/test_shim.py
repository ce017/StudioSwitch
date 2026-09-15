r"""Live shim tests. Needs at least 2 Roblox Studio windows open with the MCP server enabled.
Settings go to a temp STUDIOSWITCH_DIR, so your real StudioSwitch choices are never touched.
The only Studio action is a read-only `return game.PlaceId`.

    py tests/test_shim.py [path\to\StudioSwitchShim.exe]
"""
import json, os, pathlib, subprocess, sys, tempfile, threading, time

ROOT = pathlib.Path(__file__).resolve().parents[1]
SHIM = sys.argv[1] if len(sys.argv) > 1 else str(ROOT / "obj" / "StudioSwitchShim.exe")

tmp = tempfile.mkdtemp(prefix="studioswitch-test-")
CFG = os.path.join(tmp, "config.json")
env = dict(os.environ, STUDIOSWITCH_DIR=tmp)


def set_cfg(hidden, pinned):
    with open(CFG, "w") as f:
        json.dump({"hidden": hidden, "pinned": pinned}, f)
    time.sleep(0.05)


p = subprocess.Popen([SHIM], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
threading.Thread(target=lambda: [None for _ in p.stderr], daemon=True).start()
seq = [0]


def call(method, params=None, notify=False):
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None:
        msg["params"] = params
    if not notify:
        seq[0] += 1
        msg["id"] = seq[0]
    p.stdin.write((json.dumps(msg) + "\n").encode())
    p.stdin.flush()
    if notify:
        return None
    while True:
        line = p.stdout.readline()
        if not line:
            raise SystemExit("shim closed stdout")
        m = json.loads(line)
        if m.get("id") == seq[0]:
            return m


def tool(name, args):
    r = call("tools/call", {"name": name, "arguments": args})["result"]
    return r.get("isError"), r["content"][0]["text"]


ok = True


def check(label, cond, detail=""):
    global ok
    ok &= bool(cond)
    print(("PASS " if cond else "FAIL ") + label + ("" if cond else "  -> " + str(detail)[:400]))


call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "test_shim", "version": "0"}})
call("notifications/initialized", notify=True)

tools = call("tools/list")["result"]["tools"]
schema = [t for t in tools if t["name"] == "execute_luau"][0]["inputSchema"]
check("tools/list: studio_id no longer required", "studio_id" not in schema["required"] and "code" in schema["required"], schema["required"])
check("tools/list: studio_id description explains routing", "StudioSwitch" in schema["properties"]["studio_id"]["description"])

err, text = tool("list_roblox_studios", {})
studios = json.loads(text)["studios"]
print("  studios:", [s["name"] for s in studios])
if len(studios) < 2:
    raise SystemExit("Open at least 2 Studio windows with the MCP server enabled, then rerun.")
check("no settings: every studio listed", len(studios) >= 2, text)
key = lambda s: s["name"].rsplit("placeId: ", 1)[1].rstrip(")")
a, b = studios[0], studios[1]
others = [key(s) for s in studios if s["id"] != a["id"]]

err, text = tool("get_studio_state", {})
check("several enabled + no studio_id: helpful refusal", err and "StudioSwitch" in text, text)

set_cfg(others, None)
err, text = tool("list_roblox_studios", {})
d = json.loads(text)
check("hide all but A: list only has A", [s["id"] for s in d["studios"]] == [a["id"]], text)
check("hide all but A: note gives hidden count", ("%d more Studio" % len(others)) in d.get("note", ""), text)

err, text = tool("get_studio_state", {})
check("one enabled + no studio_id: routed to it", not err and "Studio Mode" in text, text)

err, text = tool("get_studio_state", {"studio_id": b["id"]})
check("explicit call to hidden studio refused", err and "turned off in StudioSwitch" in text, text)

err, text = tool("get_studio_state", {"studio_id": a["id"]})
check("explicit call to enabled studio works", not err and "Studio Mode" in text, text)

set_cfg([], key(b))
err, text = tool("list_roblox_studios", {})
d = json.loads(text)
check("pin B: B marked default", any(s.get("default") and s["id"] == b["id"] for s in d["studios"]), text)
err, text = tool("execute_luau", {"code": "return game.PlaceId", "datamodel_type": "Edit"})
check("pin B + no studio_id: execute_luau runs in B's place", (not err) and key(b) in text, text)

set_cfg([key(s) for s in studios], None)
err, text = tool("get_studio_state", {})
check("everything hidden + no studio_id: clear refusal", err and "No Roblox Studio is enabled" in text, text)

p.stdin.close()
t0 = time.time()
while p.poll() is None and time.time() - t0 < 6:
    time.sleep(0.1)
check("shim exits when the client closes stdin", p.poll() is not None, p.poll())

print("ALL PASS" if ok else "SOME FAILED")
sys.exit(0 if ok else 1)
