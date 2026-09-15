// StudioSwitchShim.exe: Claude launches this instead of mcp.bat. It runs the real StudioMCP.exe and
// relays stdio both ways, filtering by %LOCALAPPDATA%\StudioSwitch\config.json:
//   - list_roblox_studios only returns Studios the user left enabled (default one marked "default": true)
//   - calls aimed at a disabled Studio are refused without reaching Studio
//   - calls without studio_id go to the pinned Studio, or the only enabled one
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StudioSwitch
{
    static class Shim
    {
        const string InternalPrefix = "studioswitch-";

        static StreamWriter _toClient, _toServer;
        static readonly ConcurrentDictionary<string, string> PendingKinds = new ConcurrentDictionary<string, string>();
        static readonly ConcurrentDictionary<string, TaskCompletionSource<object>> Internal = new ConcurrentDictionary<string, TaskCompletionSource<object>>();
        static readonly object StudiosLock = new object();
        static List<StudioInfo> _studios = new List<StudioInfo>();
        static int _internalSeq;
        static Config _config = new Config();
        static DateTime _configStamp = DateTime.MinValue;

        static int Main(string[] args)
        {
            string exe = Paths.FindStudioMcp();
            if (exe == null)
            {
                Console.Error.WriteLine("StudioSwitch: StudioMCP.exe not found under " + Paths.RobloxDir);
                return 1;
            }

            var psi = new ProcessStartInfo(exe)
            {
                Arguments = string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            var server = Process.Start(psi);
            Job.Adopt(server);
            server.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
            server.BeginErrorReadLine();

            _toServer = new StreamWriter(server.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            _toClient = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var fromServer = server.StandardOutput;
            var fromClient = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

            var serverPump = new Thread(() => Pump(fromServer, HandleServerLine, SendClient)) { IsBackground = true };
            var clientPump = new Thread(() => Pump(fromClient, HandleClientLine, SendServer)) { IsBackground = true };
            serverPump.Start();
            clientPump.Start();

            while (true)
            {
                if (server.WaitForExit(200))
                {
                    serverPump.Join(2000); // deliver whatever it printed last
                    return server.ExitCode;
                }
                if (!clientPump.IsAlive)
                {
                    try { _toServer.Close(); } catch { }
                    if (!server.WaitForExit(3000)) try { server.Kill(); } catch { }
                    return 0;
                }
            }
        }

        static void Pump(StreamReader reader, Func<string, string> transform, Action<string> send)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    string output;
                    try { output = transform(line); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("StudioSwitch: passing message through unchanged after error: " + ex.Message);
                        output = line;
                    }
                    if (output != null) send(output);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("StudioSwitch: pipe closed: " + ex.Message);
            }
        }

        static void SendClient(string line) { lock (_toClient) _toClient.WriteLine(line); }
        static void SendServer(string line) { lock (_toServer) _toServer.WriteLine(line); }

        static string IdKey(object id)
        {
            return id is string ? "s:" + id : "n:" + Convert.ToString(id, CultureInfo.InvariantCulture);
        }

        static Config CurrentConfig()
        {
            try
            {
                var stamp = File.Exists(Paths.ConfigFile) ? File.GetLastWriteTimeUtc(Paths.ConfigFile) : DateTime.MinValue;
                if (stamp != _configStamp)
                {
                    _config = Config.Load();
                    _configStamp = stamp;
                }
            }
            catch { }
            return _config;
        }

        // ---- client (Claude) -> server (StudioMCP) ----

        static string HandleClientLine(string line)
        {
            if (line.IndexOf("\"method\"", StringComparison.Ordinal) < 0) return line;
            var msg = Json.Obj(Json.Parse(line));
            if (msg == null) return line;

            string method = Json.Str(msg, "method");
            object id = Json.Get(msg, "id");
            if (id == null) return line;

            if (method == "tools/list")
            {
                PendingKinds[IdKey(id)] = "tools";
                return line;
            }
            if (method != "tools/call") return line;

            var prms = Json.Obj(Json.Get(msg, "params"));
            if (prms == null) return line;
            string tool = Json.Str(prms, "name");
            if (tool == "list_roblox_studios")
            {
                PendingKinds[IdKey(id)] = "list";
                return line;
            }

            var args = Json.Obj(Json.Get(prms, "arguments"));
            if (args == null)
            {
                args = new Dictionary<string, object>();
                prms["arguments"] = args;
            }
            var cfg = CurrentConfig();
            string sid = Json.Str(args, "studio_id");

            if (string.IsNullOrEmpty(sid))
            {
                var studios = FetchStudios();
                if (studios == null) return line; // hub unreachable: let StudioMCP report it
                var enabled = studios.Where(s => !cfg.IsHidden(s.Key)).ToList();
                var target = enabled.FirstOrDefault(s => s.Key == cfg.Pinned);
                if (target == null && enabled.Count == 1) target = enabled[0];
                if (target == null)
                {
                    ReplyError(id, enabled.Count == 0
                        ? "No Roblox Studio is enabled in StudioSwitch. Ask the user to tick a Studio in the StudioSwitch window."
                        : "No default Studio is set in StudioSwitch and several are enabled. Call `list_roblox_studios` and pass `studio_id`, or ask the user to set a default.");
                    return null;
                }
                args["studio_id"] = target.Id;
                return Json.Stringify(msg);
            }

            var known = FindStudio(sid);
            if (known == null)
            {
                FetchStudios();
                known = FindStudio(sid);
            }
            if (known != null && cfg.IsHidden(known.Key))
            {
                ReplyError(id, "The Studio \"" + known.Name + "\" is turned off in StudioSwitch, so it can't be used right now. " +
                               "Call `list_roblox_studios` for the enabled Studios, or ask the user to enable this one.");
                return null;
            }
            return line;
        }

        static StudioInfo FindStudio(string id)
        {
            lock (StudiosLock) return _studios.FirstOrDefault(s => s.Id == id);
        }

        static void Remember(List<StudioInfo> studios)
        {
            lock (StudiosLock) _studios = studios;
        }

        // Asks StudioMCP for the full (unfiltered) list on our own request id; the reply is swallowed.
        static List<StudioInfo> FetchStudios()
        {
            string id = InternalPrefix + Interlocked.Increment(ref _internalSeq);
            var tcs = new TaskCompletionSource<object>();
            Internal[id] = tcs;
            SendServer(Json.Stringify(Json.O(
                "jsonrpc", "2.0", "id", id, "method", "tools/call",
                "params", Json.O("name", "list_roblox_studios", "arguments", Json.O()))));
            if (!tcs.Task.Wait(10000))
            {
                Internal.TryRemove(id, out tcs);
                return null;
            }
            var list = Studios.FromListResult(Json.Get(tcs.Task.Result, "result"));
            if (list != null) Remember(list);
            return list;
        }

        static void ReplyError(object id, string text)
        {
            SendClient(Json.Stringify(Json.O(
                "jsonrpc", "2.0", "id", id,
                "result", Json.O("content", new object[] { Json.O("type", "text", "text", text) }, "isError", true))));
        }

        // ---- server (StudioMCP) -> client (Claude) ----

        static string HandleServerLine(string line)
        {
            bool maybeInternal = line.IndexOf(InternalPrefix, StringComparison.Ordinal) >= 0;
            if (!maybeInternal && PendingKinds.IsEmpty) return line;
            if (line.IndexOf("\"id\"", StringComparison.Ordinal) < 0) return line;

            var msg = Json.Obj(Json.Parse(line));
            object id = Json.Get(msg, "id");
            if (id == null) return line;

            var sid = id as string;
            if (sid != null && sid.StartsWith(InternalPrefix, StringComparison.Ordinal))
            {
                TaskCompletionSource<object> tcs;
                if (Internal.TryRemove(sid, out tcs)) tcs.TrySetResult(msg);
                return null;
            }

            string kind;
            if (!PendingKinds.TryRemove(IdKey(id), out kind)) return line;
            var result = Json.Obj(Json.Get(msg, "result"));
            if (result == null) return line;

            if (kind == "list") FilterStudioList(result);
            else if (kind == "tools") PatchToolSchemas(result);
            return Json.Stringify(msg);
        }

        static void FilterStudioList(Dictionary<string, object> result)
        {
            var content = Json.Get(result, "content") as object[];
            var block = content != null && content.Length > 0 ? Json.Obj(content[0]) : null;
            string text = block != null ? Json.Str(block, "text") : null;
            if (text == null) return;
            var parsed = Json.Obj(Json.Parse(text));
            var items = Json.Get(parsed, "studios") as object[];
            if (items == null) return;

            Remember(items.Select(Studios.FromJson).Where(s => s != null).ToList());
            var cfg = CurrentConfig();
            var kept = new List<object>();
            int hidden = 0;
            foreach (var item in items)
            {
                var d = Json.Obj(item);
                string key = Studios.KeyOf(Json.Str(item, "name"));
                if (cfg.IsHidden(key)) { hidden++; continue; }
                if (d != null && key == cfg.Pinned) d["default"] = true;
                kept.Add(item);
            }
            parsed["studios"] = kept.ToArray();

            var notes = new List<string>();
            if (hidden > 0)
                notes.Add(hidden + " more Studio(s) are open but turned off by the user in StudioSwitch and cannot be used.");
            if (kept.Count == 1 || kept.Any(k => Json.Get(k, "default") != null))
                notes.Add("studio_id may be omitted; such calls go to " + (kept.Count == 1 ? "the only enabled Studio." : "the Studio marked default."));
            if (notes.Count > 0) parsed["note"] = string.Join(" ", notes);

            block["text"] = Json.Stringify(parsed);
        }

        static void PatchToolSchemas(Dictionary<string, object> result)
        {
            var tools = Json.Get(result, "tools") as object[];
            if (tools == null) return;
            foreach (var tool in tools)
            {
                var t = Json.Obj(tool);
                var schema = Json.Obj(Json.Get(tool, "inputSchema"));
                if (t == null || schema == null) continue;

                if (Json.Str(tool, "name") == "list_roblox_studios")
                {
                    t["description"] = Json.Str(tool, "description") +
                        " Only the Studios the user enabled in StudioSwitch are listed; the one marked `default: true` receives calls that omit `studio_id`.";
                    continue;
                }

                var required = Json.Get(schema, "required") as object[];
                if (required != null) schema["required"] = required.Where(r => (r as string) != "studio_id").ToArray();
                var prop = Json.Obj(Json.Get(Json.Get(schema, "properties"), "studio_id"));
                if (prop != null)
                    prop["description"] = Json.Str(prop, "description") +
                        " Optional: when omitted, StudioSwitch routes the call to the user's default Studio (or the only enabled one).";
            }
        }
    }
}
