// StudioSwitch.exe: pick which open Roblox Studio instances Claude's Studio MCP can use.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace StudioSwitch
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0) return Cli.Run(args);
            StartMenu.Ensure();
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }

    // Keeps a Start menu entry pointing at wherever this exe currently lives (re-pointed if the exe moves).
    static class StartMenu
    {
        public static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StudioSwitch.lnk"); }
        }

        public static void Ensure()
        {
            try
            {
                string exe = Application.ExecutablePath;
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                try
                {
                    object link = Call(shell, "CreateShortcut", BindingFlags.InvokeMethod, ShortcutPath);
                    if (File.Exists(ShortcutPath) &&
                        string.Equals((string)Call(link, "TargetPath", BindingFlags.GetProperty), exe, StringComparison.OrdinalIgnoreCase))
                        return;
                    Call(link, "TargetPath", BindingFlags.SetProperty, exe);
                    Call(link, "WorkingDirectory", BindingFlags.SetProperty, Path.GetDirectoryName(exe));
                    Call(link, "IconLocation", BindingFlags.SetProperty, exe + ",0");
                    Call(link, "Description", BindingFlags.SetProperty, "Choose which Roblox Studio windows your AI assistant can use");
                    Call(link, "Save", BindingFlags.InvokeMethod);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
            catch { } // a missing shortcut must never stop the app from opening
        }

        static object Call(object target, string member, BindingFlags flags, params object[] args)
        {
            return target.GetType().InvokeMember(member, flags, null, target, args);
        }
    }

    // StudioSwitch.exe status|hook|unhook [config.json ...] — scriptable hooking without the window.
    static class Cli
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);

        public static int Run(string[] args)
        {
            if (GetStdHandle(-11) == IntPtr.Zero) AttachConsole(-1); // print into the calling terminal
            string verb = args[0].TrimStart('-', '/').ToLowerInvariant();
            var targets = args.Length > 1 ? args.Skip(1).Select(Hooks.ForFile).ToArray() : Hooks.Targets;

            try
            {
                switch (verb)
                {
                    case "status":
                        if (targets.Length == 0) Console.WriteLine("No MCP client configs found.");
                        foreach (var t in targets)
                            Console.WriteLine(t.Label + " | " + Hooks.Status(t).Describe() + " | " + t.File);
                        return 0;

                    case "hook":
                    case "unhook":
                        if (verb == "hook")
                        {
                            string note;
                            if (!MainForm.EnsureShim(out note))
                            {
                                Console.Error.WriteLine("Couldn't install the shim: " + note);
                                return 1;
                            }
                            if (note != null) Console.WriteLine("Note: " + note);
                        }
                        foreach (var t in targets)
                            Console.WriteLine((verb == "hook" ? Hooks.Hook(t) : Hooks.Unhook(t)) + " | " + t.File);
                        return 0;

                    default:
                        Console.WriteLine("Usage: StudioSwitch.exe [status | hook | unhook] [config.json ...]");
                        Console.WriteLine("No arguments opens the window. Without config paths, all known MCP client configs are used.");
                        return verb == "help" || verb == "?" || verb == "h" ? 0 : 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }

    // Owning PIDs of local TCP sockets, via the IP helper API (netstat's text is localized).
    static class Tcp
    {
        public struct Conn { public int LocalPort, RemotePort, State, Pid; }
        public const int Listen = 2, Established = 5;

        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        static int Port(int raw) { return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); }

        public static List<Conn> Snapshot()
        {
            var list = new List<Conn>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0); // AF_INET, TCP_TABLE_OWNER_PID_ALL
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, 2, 5, 0) != 0) return list;
                int count = Marshal.ReadInt32(buf);
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = buf + 4 + i * 24;
                    list.Add(new Conn
                    {
                        State = Marshal.ReadInt32(row, 0),
                        LocalPort = Port(Marshal.ReadInt32(row, 8)),
                        RemotePort = Port(Marshal.ReadInt32(row, 16)),
                        Pid = Marshal.ReadInt32(row, 20),
                    });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }
    }

    class Row
    {
        public string Key, Name, PlaceId, Mode, Link;
        public bool Connected;
    }

    class Snapshot
    {
        public bool HubUp;
        public string HubError;
        public List<Row> Rows = new List<Row>();
        public List<HookStatus> Hooks = new List<HookStatus>();
    }

    class MainForm : Form
    {
        const int HubPort = 13469;

        readonly ListView _list = new ListView();
        readonly Dictionary<string, ListViewItem> _items = new Dictionary<string, ListViewItem>();
        readonly Label _hub = new Label();
        readonly Label _hookState = new Label();
        readonly Label _footer = new Label();
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        Font _bold;
        McpClient _client;
        Snapshot _last;
        bool _updating;
        int _busy;

        public MainForm()
        {
            Text = "StudioSwitch";
            Font = new Font("Segoe UI", 9f);
            _bold = new Font(Font, FontStyle.Bold);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(760, 470);
            MinimumSize = new Size(560, 380);
            StartPosition = FormStartPosition.CenterScreen;
            try
            {
                using (var ico = Assembly.GetExecutingAssembly().GetManifestResourceStream("StudioSwitchForm.ico"))
                    if (ico != null) Icon = new Icon(ico);
            }
            catch { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14, 12, 14, 10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var title = new Label { Text = "Which Studios can your AI use?", Font = new Font("Segoe UI Semibold", 13f), AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
            var sub = new Label
            {
                Text = "Tick the Studios your MCP client (Claude, Cursor, VS Code…) may see. ★ is the default for calls that don't name a Studio. Changes apply to running sessions immediately.",
                AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 8),
            };
            var header = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            header.Controls.Add(title);
            header.Controls.Add(sub);
            root.Controls.Add(header, 0, 0);

            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.Dock = DockStyle.Fill;
            _list.Columns.Add("Place", 250);
            _list.Columns.Add("Place ID", 120);
            _list.Columns.Add("Mode", 70);
            _list.Columns.Add("AI access", 110);
            _list.Columns.Add("MCP link", 170);
            _list.ItemChecked += OnItemChecked;
            _list.DoubleClick += delegate { SetDefault(false); };
            root.Controls.Add(_list, 0, 1);

            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
            actions.Controls.Add(MakeButton("Use only this one", delegate { SetDefault(true); }));
            actions.Controls.Add(MakeButton("★ Set as default", delegate { SetDefault(false); }));
            actions.Controls.Add(MakeButton("Clear default", delegate { ClearDefault(); }));
            actions.Controls.Add(MakeButton("Refresh", delegate { RefreshNow(); }));
            _hub.AutoSize = true;
            _hub.Margin = new Padding(12, 8, 0, 0);
            actions.Controls.Add(_hub);
            root.Controls.Add(actions, 0, 2);

            var hookBox = new GroupBox { Text = "MCP clients", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 10, 0, 0) };
            var hookFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            hookFlow.Controls.Add(MakeButton("Hook", delegate { DoHook(true); }));
            hookFlow.Controls.Add(MakeButton("Unhook", delegate { DoHook(false); }));
            _hookState.AutoSize = true;
            _hookState.Margin = new Padding(12, 8, 0, 0);
            hookFlow.Controls.Add(_hookState);
            hookBox.Controls.Add(hookFlow);
            root.Controls.Add(hookBox, 0, 3);

            _footer.AutoSize = true;
            _footer.ForeColor = SystemColors.GrayText;
            _footer.Margin = new Padding(0, 6, 0, 0);
            _footer.Text = "Hook points your MCP clients' Roblox Studio entry at StudioSwitch. Restart those apps once after hooking.";
            root.Controls.Add(_footer, 0, 4);

            _timer.Interval = 3000;
            _timer.Tick += delegate { RefreshNow(); };
            Shown += delegate
            {
                string note;
                if (Hooks.Targets.Any(t => Hooks.Status(t).Hooked > 0)) EnsureShim(out note);
                RefreshNow();
                _timer.Start();
            };
            FormClosed += delegate { if (_client != null) _client.Dispose(); };
        }

        Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(0, 0, 8, 0) };
            b.Click += onClick;
            return b;
        }

        // ---- refresh ----

        void RefreshNow()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Snapshot snap;
                try { snap = Gather(); }
                catch (Exception ex) { snap = new Snapshot { HubError = ex.Message }; }
                try { BeginInvoke((Action)(() => { _last = snap; Apply(); })); } catch { }
                Interlocked.Exchange(ref _busy, 0);
            });
        }

        Snapshot Gather()
        {
            var snap = new Snapshot();
            var conns = Tcp.Snapshot();
            snap.HubUp = conns.Any(c => c.State == Tcp.Listen && c.LocalPort == HubPort);
            var linkedPids = new HashSet<int>(conns.Where(c => c.State == Tcp.Established && c.RemotePort == HubPort).Select(c => c.Pid));

            var studios = new List<StudioInfo>();
            // Only join an existing hub: if we started one ourselves, closing this window would cut Studio off.
            if (snap.HubUp)
            {
                try
                {
                    if (_client == null || !_client.Alive)
                    {
                        if (_client != null) _client.Dispose();
                        _client = null;
                        string exe = Paths.FindStudioMcp();
                        if (exe == null) throw new Exception("StudioMCP.exe not found");
                        _client = McpClient.Start(exe);
                    }
                    var list = _client.CallTool("list_roblox_studios", null, 8000);
                    studios = Studios.FromListResult(Json.O("content", new object[] { Json.O("text", list.Text) })) ?? studios;
                }
                catch (Exception ex)
                {
                    snap.HubError = ex.GetBaseException().Message;
                    if (_client != null) _client.Dispose();
                    _client = null;
                }
            }

            foreach (var s in studios)
            {
                string mode;
                try
                {
                    var st = _client.CallTool("get_studio_state", Json.O("studio_id", s.Id), 5000);
                    var m = Regex.Match(st.Text ?? "", @"Current Studio Mode:\s*([^\r\n]+)");
                    mode = m.Success ? m.Groups[1].Value.Trim() : (st.IsError ? "error" : "?");
                }
                catch { mode = "busy"; }
                snap.Rows.Add(new Row
                {
                    Key = s.Key, Name = s.BaseName, PlaceId = s.Key != s.Name ? s.Key : "",
                    Mode = mode, Connected = true, Link = "connected",
                });
            }

            // Studio windows the hub doesn't list.
            foreach (var p in Process.GetProcessesByName("RobloxStudioBeta"))
            {
                string title;
                try { title = p.MainWindowTitle; } catch { continue; }
                const string suffix = " - Roblox Studio";
                int cut = title.LastIndexOf(suffix, StringComparison.Ordinal);
                if (cut <= 0) continue; // start page or no window
                string name = title.Substring(0, cut).TrimEnd('*', ' ');
                if (studios.Any(s => string.Equals(s.BaseName, name, StringComparison.OrdinalIgnoreCase))) continue;
                snap.Rows.Add(new Row
                {
                    Key = "proc:" + p.Id, Name = name, PlaceId = "", Mode = "", Connected = false,
                    Link = !snap.HubUp ? "no hub yet" : linkedPids.Contains(p.Id) ? "socket open, not listed" : "NOT connected",
                });
            }

            snap.Hooks = Hooks.Targets.Select(Hooks.Status).ToList();
            return snap;
        }

        void Apply()
        {
            var snap = _last;
            if (snap == null) return;
            var cfg = Config.Load();
            _updating = true;
            _list.BeginUpdate();
            try
            {
                var seen = new HashSet<string>();
                foreach (var row in snap.Rows)
                {
                    seen.Add(row.Key);
                    ListViewItem item;
                    if (!_items.TryGetValue(row.Key, out item))
                    {
                        item = new ListViewItem(new[] { "", "", "", "", "" }) { Tag = row.Key, UseItemStyleForSubItems = true };
                        _items[row.Key] = item;
                        _list.Items.Add(item);
                    }
                    bool hidden = cfg.IsHidden(row.Key);
                    bool pinned = row.Connected && !hidden && row.Key == cfg.Pinned;
                    Set(item, 0, (pinned ? "★ " : "") + row.Name);
                    Set(item, 1, row.PlaceId);
                    Set(item, 2, row.Mode);
                    Set(item, 3, !row.Connected ? "—" : hidden ? "hidden" : pinned ? "default" : "visible");
                    Set(item, 4, row.Link);
                    item.Checked = row.Connected && !hidden;
                    item.ForeColor = !row.Connected ? Color.Firebrick : hidden ? SystemColors.GrayText : SystemColors.WindowText;
                    item.Font = pinned ? _bold : Font;
                }
                foreach (var key in _items.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    _list.Items.Remove(_items[key]);
                    _items.Remove(key);
                }
            }
            finally
            {
                _list.EndUpdate();
                _updating = false;
            }

            int connected = snap.Rows.Count(r => r.Connected);
            if (snap.HubError != null) SetLabel(_hub, "MCP hub error: " + snap.HubError, Color.Firebrick);
            else if (!snap.HubUp) SetLabel(_hub, "MCP hub not running — start an AI session that uses Roblox Studio", Color.DarkGoldenrod);
            else SetLabel(_hub, "MCP hub running · " + connected + " Studio(s) connected", Color.SeaGreen);

            var withEntries = snap.Hooks.Where(h => h.HasEntries).ToList();
            bool allHooked = withEntries.Count > 0 && withEntries.All(h => h.Unhooked == 0);
            SetLabel(_hookState,
                withEntries.Count == 0
                    ? "No Roblox Studio MCP entry found in Claude, Cursor, VS Code, Windsurf or Gemini configs"
                    : string.Join("   ·   ", withEntries.Select(h => h.Target.Label + ": " + h.Describe())),
                allHooked ? Color.SeaGreen : SystemColors.ControlText);
        }

        static void Set(ListViewItem item, int column, string text)
        {
            if (item.SubItems[column].Text != text) item.SubItems[column].Text = text;
        }

        static void SetLabel(Label label, string text, Color color)
        {
            if (label.Text != text) label.Text = text;
            label.ForeColor = color;
        }

        // ---- actions ----

        string SelectedKey()
        {
            if (_list.SelectedItems.Count == 0) return null;
            return (string)_list.SelectedItems[0].Tag;
        }

        void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_updating) return;
            var key = (string)e.Item.Tag;
            if (key.StartsWith("proc:"))
            {
                _updating = true;
                e.Item.Checked = false;
                _updating = false;
                return;
            }
            var cfg = Config.Load();
            cfg.Hidden.Remove(key);
            if (!e.Item.Checked)
            {
                cfg.Hidden.Add(key);
                if (cfg.Pinned == key) cfg.Pinned = null;
            }
            Save(cfg);
        }

        void SetDefault(bool exclusive)
        {
            string key = SelectedKey();
            if (key == null) { Toast("Select a Studio first."); return; }
            if (key.StartsWith("proc:")) { Toast("That Studio isn't connected to the MCP, so it can't be used yet."); return; }
            var cfg = Config.Load();
            cfg.Pinned = key;
            cfg.Hidden.Remove(key);
            if (exclusive && _last != null)
            {
                foreach (var row in _last.Rows.Where(r => r.Connected && r.Key != key))
                    if (!cfg.Hidden.Contains(row.Key)) cfg.Hidden.Add(row.Key);
            }
            Save(cfg);
        }

        void ClearDefault()
        {
            var cfg = Config.Load();
            cfg.Pinned = null;
            Save(cfg);
        }

        void Save(Config cfg)
        {
            try
            {
                cfg.Save();
                Apply();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save settings: " + ex.Message, "StudioSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void Toast(string text)
        {
            _footer.Text = text;
        }

        // Writes the embedded shim to %LOCALAPPDATA%\StudioSwitch unless an identical copy is already there.
        internal static bool EnsureShim(out string note)
        {
            note = null;
            byte[] bytes;
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("StudioSwitchShim.exe"))
            using (var ms = new MemoryStream())
            {
                if (res == null) { note = "shim missing from this build"; return false; }
                res.CopyTo(ms);
                bytes = ms.ToArray();
            }
            try
            {
                Directory.CreateDirectory(Paths.AppDir);
                foreach (var old in Directory.GetFiles(Paths.AppDir, "StudioSwitchShim.exe.old-*"))
                    try { File.Delete(old); } catch { } // still in use by an older session

                if (File.Exists(Paths.ShimExe) && File.ReadAllBytes(Paths.ShimExe).SequenceEqual(bytes)) return true;
                try
                {
                    File.WriteAllBytes(Paths.ShimExe, bytes);
                }
                catch (IOException)
                {
                    // A running session has the shim open. Windows lets an in-use exe be renamed, so move it
                    // aside: running sessions keep the old copy, new sessions start the new one.
                    File.Move(Paths.ShimExe, Paths.ShimExe + ".old-" + DateTime.Now.Ticks);
                    File.WriteAllBytes(Paths.ShimExe, bytes);
                }
                return true;
            }
            catch (Exception ex)
            {
                note = ex.Message;
                return File.Exists(Paths.ShimExe);
            }
        }

        void DoHook(bool hook)
        {
            string note = null;
            if (hook && !EnsureShim(out note))
            {
                MessageBox.Show(this, "Couldn't install the shim: " + note, "StudioSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var targets = Hooks.Targets.Where(t => Hooks.Status(t).HasEntries).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "No Roblox Studio MCP entry was found in Claude, Cursor, VS Code, Windsurf or Gemini configs.\n\n" +
                    "Add Roblox Studio to your MCP client first (Studio: Assistant settings → MCP), then click Hook.",
                    "StudioSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var lines = targets.Select(t =>
            {
                try { return hook ? Hooks.Hook(t) : Hooks.Unhook(t); }
                catch (Exception ex) { return t.Label + ": failed — " + ex.Message; }
            }).ToList();
            if (note != null) lines.Add("Note: " + note);
            lines.Add("");
            lines.Add("Restart those apps (and any open CLI sessions) so the Studio MCP relaunches " + (hook ? "through StudioSwitch." : "without StudioSwitch."));
            lines.Add("A backup of each edited config was saved next to it (*.studioswitch-*.bak).");
            MessageBox.Show(this, string.Join(Environment.NewLine, lines), "StudioSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshNow();
        }
    }
}
