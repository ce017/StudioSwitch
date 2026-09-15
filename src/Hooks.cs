// Points Roblox Studio MCP server entries in MCP client configs (Claude, Cursor, VS Code, ...) at
// StudioSwitchShim.exe, and back. An entry is recognised by what it runs (StudioMCP.exe or Roblox's
// mcp.bat), not by its name. Edits are text splices of just that JSON object, so the rest of each file
// is untouched, and every original object is kept in hooks.json for an exact restore.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace StudioSwitch
{
    class HookTarget
    {
        public string Label;
        public string File;
    }

    class HookStatus
    {
        public HookTarget Target;
        public int Hooked, Unhooked;
        public string Problem;

        public bool HasEntries { get { return Problem == null && Hooked + Unhooked > 0; } }

        public string Describe()
        {
            if (Problem != null) return Problem;
            if (Hooked + Unhooked == 0) return "no Studio MCP entry";
            if (Unhooked == 0) return "hooked";
            if (Hooked == 0) return "not hooked";
            return Hooked + " hooked, " + Unhooked + " not";
        }
    }

    static class Hooks
    {
        const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

        // Known MCP client config files that exist on this machine.
        public static HookTarget[] Targets
        {
            get
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var targets = new List<HookTarget>();
                var seen = new HashSet<string>();
                Action<string, string> add = (label, file) =>
                {
                    // Under MSIX redirection two paths can be the same physical file; compare file ids.
                    if (File.Exists(file) && seen.Add(FileId(file)))
                        targets.Add(new HookTarget { Label = label, File = file });
                };

                add("Claude app", Path.Combine(roaming, "Claude", "claude_desktop_config.json"));
                // The Store (MSIX) build of the Claude app reads a redirected AppData inside its package folder.
                try
                {
                    foreach (var pkg in Directory.GetDirectories(Path.Combine(Paths.LocalAppData, "Packages"), "Claude_*"))
                        add("Claude app", Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
                }
                catch { }
                add("Claude Code", Path.Combine(home, ".claude.json"));
                add("Cursor", Path.Combine(home, ".cursor", "mcp.json"));
                add("VS Code", Path.Combine(roaming, "Code", "User", "mcp.json"));
                add("VS Code Insiders", Path.Combine(roaming, "Code - Insiders", "User", "mcp.json"));
                add("Windsurf", Path.Combine(home, ".codeium", "windsurf", "mcp_config.json"));
                add("Gemini CLI", Path.Combine(home, ".gemini", "settings.json"));
                return targets.ToArray();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FileInfoByHandle
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out FileInfoByHandle info);

        static string FileId(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    FileInfoByHandle info;
                    if (GetFileInformationByHandle(fs.SafeFileHandle, out info))
                        return info.VolumeSerial + ":" + info.IndexHigh + ":" + info.IndexLow;
                }
            }
            catch { }
            return Path.GetFullPath(file).ToLowerInvariant();
        }

        public static HookTarget ForFile(string file)
        {
            string full = Path.GetFullPath(file);
            return Targets.FirstOrDefault(t => string.Equals(t.File, full, IgnoreCase))
                   ?? new HookTarget { Label = Path.GetFileName(full), File = full };
        }

        struct Span
        {
            public int Start, End; // End is the index of the closing brace
            public bool Hooked;
        }

        // Single pass over the text, tracking object starts; every closed object that looks like a
        // Studio MCP server entry becomes a span. Tolerates // and /* */ comments (VS Code JSONC).
        static List<Span> FindEntries(string text)
        {
            var spans = new List<Span>();
            var starts = new Stack<int>();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    for (i++; i < text.Length && text[i] != '"'; i++)
                        if (text[i] == '\\') i++;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? text.Length : close + 1;
                }
                else if (c == '{')
                {
                    starts.Push(i);
                }
                else if (c == '}' && starts.Count > 0)
                {
                    int start = starts.Pop();
                    bool hooked;
                    if (IsStudioServer(text, start, i, out hooked))
                        spans.Add(new Span { Start = start, End = i, Hooked = hooked });
                }
            }
            return spans.OrderBy(s => s.Start).ToList();
        }

        static bool Mentions(string s, int start, int length)
        {
            return s.IndexOf("StudioSwitchShim", start, length, IgnoreCase) >= 0
                || s.IndexOf("StudioMCP", start, length, IgnoreCase) >= 0
                || (s.IndexOf("mcp.bat", start, length, IgnoreCase) >= 0 && s.IndexOf("Roblox", start, length, IgnoreCase) >= 0);
        }

        static bool IsStudioServer(string text, int start, int end, out bool hooked)
        {
            hooked = false;
            int length = end - start + 1;
            if (length > 20000 || text.IndexOf("\"command\"", start, length, StringComparison.Ordinal) < 0) return false;
            if (!Mentions(text, start, length)) return false;

            Dictionary<string, object> obj;
            try { obj = Json.Obj(Json.Parse(text.Substring(start, length))); }
            catch { return false; }
            string command = Json.Str(obj, "command");
            if (command == null) return false;

            var args = Json.Get(obj, "args") as object[];
            string line = command + " " + (args == null ? "" : string.Join(" ", args.Select(a => Convert.ToString(a))));
            hooked = line.IndexOf("StudioSwitchShim", IgnoreCase) >= 0;
            return hooked || Mentions(line, 0, line.Length);
        }

        static string Slice(string text, Span s) { return text.Substring(s.Start, s.End - s.Start + 1); }

        public static HookStatus Status(HookTarget target)
        {
            var st = new HookStatus { Target = target };
            try
            {
                if (!File.Exists(target.File)) { st.Problem = "config file not found"; return st; }
                foreach (var s in FindEntries(File.ReadAllText(target.File)))
                {
                    if (s.Hooked) st.Hooked++;
                    else st.Unhooked++;
                }
            }
            catch (Exception ex) { st.Problem = "can't read: " + ex.Message; }
            return st;
        }

        static Dictionary<string, List<string>> LoadRecords()
        {
            var records = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(Paths.HooksFile)) return records;
                var d = Json.Obj(Json.Parse(File.ReadAllText(Paths.HooksFile)));
                foreach (var kv in d)
                {
                    var arr = kv.Value as object[];
                    if (arr != null) records[kv.Key] = arr.OfType<string>().ToList();
                }
            }
            catch { }
            return records;
        }

        static void SaveRecords(Dictionary<string, List<string>> records)
        {
            Directory.CreateDirectory(Paths.AppDir);
            var d = records.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToArray());
            File.WriteAllText(Paths.HooksFile, Json.Stringify(d), new UTF8Encoding(false));
        }

        static string LineIndent(string text, int index)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            int i = lineStart;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(lineStart, i - lineStart);
        }

        static string Pretty(Dictionary<string, object> obj, string indent, string newline)
        {
            var lines = obj.Select(kv => indent + "  " + Json.Stringify(kv.Key) + ": " + Json.Stringify(kv.Value));
            return "{" + newline + string.Join("," + newline, lines) + newline + indent + "}";
        }

        static void WriteWithBackup(string file, string text)
        {
            byte[] head = new byte[3];
            int read;
            using (var fs = File.OpenRead(file)) read = fs.Read(head, 0, 3);
            bool bom = read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;

            string backup = file + ".studioswitch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            File.Copy(file, backup, true);
            File.WriteAllText(file, text, new UTF8Encoding(bom));
        }

        // Returns a human-readable result line.
        public static string Hook(HookTarget target)
        {
            if (!File.Exists(target.File)) return target.Label + ": config file not found";
            string text = File.ReadAllText(target.File);
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var all = FindEntries(text);
            var spans = all.Where(s => !s.Hooked).ToList();
            if (spans.Count == 0) return target.Label + ": " + (all.Count > 0 ? "already hooked" : "no Studio MCP entry");

            var records = LoadRecords();
            List<string> originals;
            if (!records.TryGetValue(target.File, out originals)) records[target.File] = originals = new List<string>();

            // Splice back-to-front so earlier offsets stay valid.
            var added = new List<string>();
            for (int n = spans.Count - 1; n >= 0; n--)
            {
                var s = spans[n];
                string original = Slice(text, s);
                var obj = Json.Obj(Json.Parse(original));
                string command = Json.Str(obj, "command") ?? "";
                bool directExe = command.EndsWith("StudioMCP.exe", IgnoreCase);

                var replacement = new Dictionary<string, object>();
                if (obj.ContainsKey("type")) replacement["type"] = obj["type"];
                replacement["command"] = Paths.ShimExe;
                // Arguments meant for StudioMCP.exe itself are forwarded by the shim; cmd.exe ones are not.
                replacement["args"] = directExe && obj.ContainsKey("args") ? obj["args"] : new object[0];
                foreach (var kv in obj)
                    if (kv.Key != "type" && kv.Key != "command" && kv.Key != "args") replacement[kv.Key] = kv.Value;

                text = text.Substring(0, s.Start) + Pretty(replacement, LineIndent(text, s.Start), newline) + text.Substring(s.End + 1);
                added.Insert(0, original);
            }
            originals.AddRange(added);
            SaveRecords(records);
            WriteWithBackup(target.File, text);
            return target.Label + ": hooked";
        }

        public static string Unhook(HookTarget target)
        {
            if (!File.Exists(target.File)) return target.Label + ": config file not found";
            string text = File.ReadAllText(target.File);
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var spans = FindEntries(text).Where(s => s.Hooked).ToList();
            if (spans.Count == 0) return target.Label + ": not hooked";

            var records = LoadRecords();
            List<string> originals;
            if (!records.TryGetValue(target.File, out originals)) originals = new List<string>();
            bool exact = originals.Count == spans.Count;

            for (int n = spans.Count - 1; n >= 0; n--)
            {
                var s = spans[n];
                string restored;
                if (exact)
                {
                    restored = originals[n];
                }
                else
                {
                    // No matching record: fall back to Roblox's standard launcher.
                    var obj = Json.Obj(Json.Parse(Slice(text, s))) ?? new Dictionary<string, object>();
                    obj["command"] = "cmd.exe";
                    obj["args"] = new object[] { "/c", "%LOCALAPPDATA%\\Roblox\\mcp.bat" };
                    restored = Pretty(obj, LineIndent(text, s.Start), newline);
                }
                text = text.Substring(0, s.Start) + restored + text.Substring(s.End + 1);
            }
            records.Remove(target.File);
            SaveRecords(records);
            WriteWithBackup(target.File, text);
            return target.Label + ": unhooked";
        }
    }
}
