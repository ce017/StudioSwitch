// Shared by StudioSwitch.exe (UI) and StudioSwitchShim.exe (the MCP filter Claude launches).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StudioSwitch
{
    static class Paths
    {
        public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // STUDIOSWITCH_DIR relocates settings, hook records and the installed shim (used by the tests).
        public static readonly string AppDir = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STUDIOSWITCH_DIR"))
            ? Environment.GetEnvironmentVariable("STUDIOSWITCH_DIR")
            : Path.Combine(LocalAppData, "StudioSwitch");
        public static readonly string ConfigFile = Path.Combine(AppDir, "config.json");
        public static readonly string HooksFile = Path.Combine(AppDir, "hooks.json");
        public static readonly string ShimExe = Path.Combine(AppDir, "StudioSwitchShim.exe");
        public static readonly string RobloxDir = Path.Combine(LocalAppData, "Roblox");

        // Roblox rewrites mcp.bat with the current version folder, so prefer the path it names.
        public static string FindStudioMcp()
        {
            try
            {
                string bat = Path.Combine(RobloxDir, "mcp.bat");
                if (File.Exists(bat))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(bat), "\"([^\"]*StudioMCP\\.exe)\"", RegexOptions.IgnoreCase))
                        if (File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
                }
            }
            catch { }

            string versions = Path.Combine(RobloxDir, "Versions");
            if (!Directory.Exists(versions)) return null;
            return Directory.GetDirectories(versions)
                .Select(d => Path.Combine(d, "StudioMCP.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
    }

    static class Json
    {
        [ThreadStatic] static JavaScriptSerializer _s;

        static JavaScriptSerializer S
        {
            get
            {
                if (_s == null) _s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
                return _s;
            }
        }

        public static object Parse(string text) { return S.DeserializeObject(text); }
        public static string Stringify(object o) { return S.Serialize(o); }
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }

        public static object Get(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(object o, string key) { return Get(o, key) as string; }

        public static Dictionary<string, object> O(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }

    class StudioInfo
    {
        public string Id;
        public string Name;
        public string Key;      // place id when present, otherwise the full name
        public string BaseName; // name without the "(placeId: ...)" suffix
    }

    static class Studios
    {
        static readonly Regex PlaceRx = new Regex(@"\(placeId:\s*(\d+)\)\s*$");

        public static string KeyOf(string name)
        {
            var m = PlaceRx.Match(name ?? "");
            return m.Success ? m.Groups[1].Value : (name ?? "");
        }

        public static string BaseNameOf(string name)
        {
            var m = PlaceRx.Match(name ?? "");
            return m.Success ? name.Substring(0, m.Index).Trim() : (name ?? "");
        }

        public static StudioInfo FromJson(object item)
        {
            string id = Json.Str(item, "id"), name = Json.Str(item, "name");
            if (id == null) return null;
            return new StudioInfo { Id = id, Name = name ?? id, Key = KeyOf(name ?? id), BaseName = BaseNameOf(name ?? id) };
        }

        // list_roblox_studios returns {"content":[{"type":"text","text":"{\"studios\":[...]}"}]}
        public static List<StudioInfo> FromListResult(object result)
        {
            var content = Json.Get(result, "content") as object[];
            if (content == null || content.Length == 0) return null;
            string text = Json.Str(content[0], "text");
            if (text == null) return null;
            var arr = Json.Get(Json.Parse(text), "studios") as object[];
            if (arr == null) return null;
            return arr.Select(FromJson).Where(s => s != null).ToList();
        }
    }

    class Config
    {
        public List<string> Hidden = new List<string>();
        public string Pinned;

        public bool IsHidden(string key) { return Hidden.Contains(key); }

        public static Config Load()
        {
            var c = new Config();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!File.Exists(Paths.ConfigFile)) return c;
                    var d = Json.Parse(File.ReadAllText(Paths.ConfigFile));
                    var hidden = Json.Get(d, "hidden") as object[];
                    if (hidden != null) c.Hidden = hidden.OfType<string>().ToList();
                    c.Pinned = Json.Str(d, "pinned");
                    return c;
                }
                catch (IOException) { Thread.Sleep(30); }
                catch { return c; }
            }
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Paths.AppDir);
            string tmp = Paths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, Json.Stringify(Json.O("hidden", Hidden.ToArray(), "pinned", Pinned)), new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(Paths.ConfigFile)) File.Replace(tmp, Paths.ConfigFile, null);
                    else File.Move(tmp, Paths.ConfigFile);
                    return;
                }
                catch (IOException)
                {
                    if (attempt >= 10) throw;
                    Thread.Sleep(30);
                }
            }
        }
    }

    // Puts child processes in a kill-on-close job so StudioMCP.exe never outlives us.
    static class Job
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static IntPtr _job;
        static readonly object Lock = new object();

        public static void Adopt(Process p)
        {
            try
            {
                lock (Lock)
                {
                    if (_job == IntPtr.Zero)
                    {
                        _job = CreateJobObject(IntPtr.Zero, null);
                        var info = new ExtendedLimits();
                        info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        SetInformationJobObject(_job, 9, ref info, (uint)Marshal.SizeOf(typeof(ExtendedLimits)));
                    }
                    AssignProcessToJobObject(_job, p.Handle);
                }
            }
            catch { }
        }
    }

    class ToolResult
    {
        public string Text;
        public bool IsError;
    }

    // Minimal request/response MCP client over StudioMCP.exe's stdio (used by the UI for live status).
    class McpClient : IDisposable
    {
        Process _p;
        StreamWriter _in;
        int _seq;
        readonly object _lock = new object();
        readonly Dictionary<int, TaskCompletionSource<object>> _pending = new Dictionary<int, TaskCompletionSource<object>>();

        public bool Alive
        {
            get { try { return _p != null && !_p.HasExited; } catch { return false; } }
        }

        public static McpClient Start(string exe)
        {
            var c = new McpClient();
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            c._p = Process.Start(psi);
            Job.Adopt(c._p);
            c._p.ErrorDataReceived += delegate { };
            c._p.BeginErrorReadLine();
            c._in = new StreamWriter(c._p.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var reader = c._p.StandardOutput;
            new Thread(() => c.ReadLoop(reader)) { IsBackground = true }.Start();

            try
            {
                c.Request("initialize", Json.O(
                    "protocolVersion", "2025-06-18",
                    "capabilities", Json.O(),
                    "clientInfo", Json.O("name", "StudioSwitch", "version", "1.0")), 15000);
                c.Send(Json.O("jsonrpc", "2.0", "method", "notifications/initialized"));
            }
            catch
            {
                c.Dispose();
                throw;
            }
            return c;
        }

        void ReadLoop(StreamReader reader)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    object msg;
                    try { msg = Json.Parse(line); } catch { continue; }
                    object id = Json.Get(msg, "id");
                    if (id == null || !(id is int)) continue;
                    TaskCompletionSource<object> tcs;
                    lock (_lock)
                    {
                        if (!_pending.TryGetValue((int)id, out tcs)) continue;
                        _pending.Remove((int)id);
                    }
                    object err = Json.Get(msg, "error");
                    if (err != null) tcs.TrySetException(new Exception(Json.Str(err, "message") ?? "MCP error"));
                    else tcs.TrySetResult(Json.Get(msg, "result"));
                }
            }
            catch { }
            lock (_lock)
            {
                foreach (var t in _pending.Values) t.TrySetException(new Exception("StudioMCP exited"));
                _pending.Clear();
            }
        }

        void Send(object msg)
        {
            string line = Json.Stringify(msg);
            lock (_in) _in.WriteLine(line);
        }

        public object Request(string method, object prms, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<object>();
            int id;
            lock (_lock)
            {
                id = ++_seq;
                _pending[id] = tcs;
            }
            Send(Json.O("jsonrpc", "2.0", "id", id, "method", method, "params", prms));
            if (!tcs.Task.Wait(timeoutMs))
            {
                lock (_lock) _pending.Remove(id);
                throw new TimeoutException(method + " timed out");
            }
            return tcs.Task.Result;
        }

        public ToolResult CallTool(string name, Dictionary<string, object> args, int timeoutMs)
        {
            var result = Request("tools/call", Json.O("name", name, "arguments", args ?? Json.O()), timeoutMs);
            var content = Json.Get(result, "content") as object[];
            return new ToolResult
            {
                Text = content != null && content.Length > 0 ? Json.Str(content[0], "text") : null,
                IsError = Json.Get(result, "isError") is bool && (bool)Json.Get(result, "isError"),
            };
        }

        public void Dispose()
        {
            try { if (_in != null) _in.Close(); } catch { }
            try { if (Alive && !_p.WaitForExit(1500)) _p.Kill(); } catch { }
        }
    }
}
