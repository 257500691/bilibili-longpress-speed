// BiliLongPress - 哔哩哔哩桌面端「长按鼠标左键倍速」工具
//
// 原理：
//   1. 客户端以 --remote-debugging-port=9222 启动（Electron/Chromium 调试协议）
//   2. 本工具通过 CDP 轮询每个播放器窗口里 <video> 的位置、当前倍速、播放状态
//   3. 全局低级鼠标钩子(WH_MOUSE_LL)检测左键长按：命中视频区域且在按住 holdMs 后
//      仍未松开 → 把 playbackRate 临时设为 speed；松开 → 恢复原倍速
//
// 编译：build.ps1（csc /codepage:65001）
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BiliLongPress
{
    // ---------------------------------------------------------------- 日志
    static class Log
    {
        static readonly object Gate = new object();
        public static string Path;
        public static bool Verbose = false;

        public static void Init(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Path = System.IO.Path.Combine(dir, "log.txt");
                if (File.Exists(Path) && new FileInfo(Path).Length > 512 * 1024)
                    File.Delete(Path);
            }
            catch { Path = null; }
        }

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "  " + msg;
            try { Console.WriteLine(line); } catch { }
            if (Path == null) return;
            lock (Gate)
            {
                try { File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }

        public static void Debug(string msg) { if (Verbose) Write(msg); }
    }

    // ---------------------------------------------------------------- 极简 JSON
    static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = ParseValue(s, ref i);
            return v;
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("json: unexpected end");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || s.Substring(i, word.Length) != word)
                throw new FormatException("json: bad literal at " + i);
            i += word.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("json: expected key at " + i);
                string k = ParseString(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("json: expected ':' at " + i);
                i++;
                d[k] = ParseValue(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length) throw new FormatException("json: unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("json: expected ',' or '}' at " + i);
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++; // [
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                Ws(s, ref i);
                if (i >= s.Length) throw new FormatException("json: unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("json: expected ',' or ']' at " + i);
            }
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // "
            while (true)
            {
                if (i >= s.Length) throw new FormatException("json: unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("json: bad escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("json: bad \\u");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw new FormatException("json: bad escape \\" + e);
                }
            }
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && ("+-0123456789.eE".IndexOf(s[i]) >= 0)) i++;
            string t = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("json: bad number '" + t + "'");
            return d;
        }

        public static Dictionary<string, object> Dict(object o) { return o as Dictionary<string, object>; }

        public static double Num(object o, double def)
        {
            if (o == null) return def;
            if (o is double) return (double)o;
            if (o is bool) return ((bool)o) ? 1 : 0;
            double d;
            if (double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return def;
        }

        public static string Str(object o, string def) { return o == null ? def : Convert.ToString(o, CultureInfo.InvariantCulture); }
        public static bool Bool(object o, bool def) { return o == null ? def : (o is bool ? (bool)o : Num(o, def ? 1 : 0) != 0); }

        public static string EncodeString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }

    // ---------------------------------------------------------------- 配置
    sealed class Config
    {
        public int DebugPort = 9222;
        public string ClientExe = "";
        public int HoldMs = 300;
        public double Speed = 2.0;
        // passthrough: 完全不干预点击 | swallowUp: 长按后吞掉抬起事件(防暂停) | block: 拦住按下并回放短按
        public string ClickMode = "swallowUp";
        public int DragCancelPx = 12;
        public int BottomExcludeCssPx = 100;
        public int TopExcludeCssPx = 0;
        public int PollMs = 400;
        public bool AutoLaunchClient = true;
        public bool AutoRestartClient = false;
        public bool ShowNotifications = true;
        public bool ResumeWhenPaused = false;
        public bool RestorePauseState = true;
        public bool VerboseLog = false;
        public string LogDir = "";

        public static string AppDir
        {
            get { return System.IO.Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static string DefaultLogDir
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliLongPress"); }
        }

        public static string ConfigPath { get { return System.IO.Path.Combine(AppDir, "config.json"); } }

        /// <summary>配置在默认值上的 JSON 序列化（首次运行/文件丢失时生成）</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"debugPort\": " + DebugPort + ",");
            sb.AppendLine("  \"clientExe\": " + Json.EncodeString(ClientExe) + ",");
            sb.AppendLine();
            sb.AppendLine("  \"holdMs\": " + HoldMs + ",");
            sb.AppendLine("  \"speed\": " + Speed.ToString("0.###", CultureInfo.InvariantCulture) + ",");
            sb.AppendLine();
            sb.AppendLine("  \"clickMode\": " + Json.EncodeString(ClickMode) + ",");
            sb.AppendLine("  \"dragCancelPx\": " + DragCancelPx + ",");
            sb.AppendLine();
            sb.AppendLine("  \"bottomExcludeCssPx\": " + BottomExcludeCssPx + ",");
            sb.AppendLine("  \"topExcludeCssPx\": " + TopExcludeCssPx + ",");
            sb.AppendLine();
            sb.AppendLine("  \"pollMs\": " + PollMs + ",");
            sb.AppendLine("  \"autoLaunchClient\": " + (AutoLaunchClient ? "true" : "false") + ",");
            sb.AppendLine("  \"autoRestartClient\": " + (AutoRestartClient ? "true" : "false") + ",");
            sb.AppendLine("  \"showNotifications\": " + (ShowNotifications ? "true" : "false") + ",");
            sb.AppendLine("  \"resumeWhenPaused\": " + (ResumeWhenPaused ? "true" : "false") + ",");
            sb.AppendLine("  \"restorePauseState\": " + (RestorePauseState ? "true" : "false") + ",");
            sb.AppendLine("  \"verboseLog\": " + (VerboseLog ? "true" : "false") + ",");
            sb.AppendLine("  \"logDir\": " + Json.EncodeString(LogDir));
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static Config Load()
        {
            var c = new Config();
            // 常见安装位置里探测客户端（探测不到就由用户在 config.json 里指定）
            string[] roots = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"D:\",
            };
            string[] subs = new string[] { "bilibili", @"Programs\bilibili", @"bilibili\bilibili" };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string sub in subs)
                {
                    string guess = System.IO.Path.Combine(root, sub, "哔哩哔哩.exe");
                    if (File.Exists(guess)) { c.ClientExe = guess; break; }
                }
                if (!string.IsNullOrEmpty(c.ClientExe)) break;
            }

            try
            {
                if (File.Exists(ConfigPath))
                {
                    var root = Json.Dict(Json.Parse(File.ReadAllText(ConfigPath, Encoding.UTF8)));
                    if (root != null)
                    {
                        c.DebugPort = (int)Json.Num(Get(root, "debugPort"), c.DebugPort);
                        c.ClientExe = Json.Str(Get(root, "clientExe"), c.ClientExe);
                        c.HoldMs = (int)Json.Num(Get(root, "holdMs"), c.HoldMs);
                        c.Speed = Json.Num(Get(root, "speed"), c.Speed);
                        c.ClickMode = Json.Str(Get(root, "clickMode"), c.ClickMode);
                        c.DragCancelPx = (int)Json.Num(Get(root, "dragCancelPx"), c.DragCancelPx);
                        c.BottomExcludeCssPx = (int)Json.Num(Get(root, "bottomExcludeCssPx"), c.BottomExcludeCssPx);
                        c.TopExcludeCssPx = (int)Json.Num(Get(root, "topExcludeCssPx"), c.TopExcludeCssPx);
                        c.PollMs = (int)Json.Num(Get(root, "pollMs"), c.PollMs);
                        c.AutoLaunchClient = Json.Bool(Get(root, "autoLaunchClient"), c.AutoLaunchClient);
                        c.AutoRestartClient = Json.Bool(Get(root, "autoRestartClient"), c.AutoRestartClient);
                        c.ShowNotifications = Json.Bool(Get(root, "showNotifications"), c.ShowNotifications);
                        c.ResumeWhenPaused = Json.Bool(Get(root, "resumeWhenPaused"), c.ResumeWhenPaused);
                        c.RestorePauseState = Json.Bool(Get(root, "restorePauseState"), c.RestorePauseState);
                        c.VerboseLog = Json.Bool(Get(root, "verboseLog"), c.VerboseLog);
                        c.LogDir = Json.Str(Get(root, "logDir"), c.LogDir);
                    }
                }
            }
            catch (Exception ex) { Log.Write("读取配置失败，使用默认值: " + ex.Message); }

            if (c.HoldMs < 80) c.HoldMs = 80;
            if (c.Speed <= 0) c.Speed = 2.0;
            if (c.PollMs < 100) c.PollMs = 100;
            if (string.IsNullOrEmpty(c.LogDir)) c.LogDir = DefaultLogDir;

            // 文件不存在（例如只拷贝了 exe）就生成一份，方便用户改
            try
            {
                if (!File.Exists(ConfigPath))
                    File.WriteAllText(ConfigPath, c.ToJson(), new UTF8Encoding(false));
            }
            catch { }
            return c;
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }
    }

    // ---------------------------------------------------------------- Win32
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public const int WH_MOUSE_LL = 14;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_MOUSEMOVE = 0x0200;

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT p);

        // 让本进程具备 DPI 感知，保证屏幕坐标与 Chromium 的物理像素一致
        public static void EnableDpiAwareness()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        /// <summary>回放一次点击：光标本来就在该点，只需补按下+抬起</summary>
        public static void InjectClick(IntPtr extraInfo)
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
            inputs[0].mi.dwExtraInfo = extraInfo;
            inputs[1].type = INPUT_MOUSE;
            inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;
            inputs[1].mi.dwExtraInfo = extraInfo;
            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    // ---------------------------------------------------------------- WebSocket + CDP
    sealed class WsClient : IDisposable
    {
        readonly TcpClient _tcp;
        readonly NetworkStream _ns;
        int _id;
        static readonly Random Rng = new Random();

        public WsClient(string wsUrl, int timeoutMs)
        {
            var uri = new Uri(wsUrl);
            int port = uri.Port <= 0 ? 80 : uri.Port;
            _tcp = new TcpClient();
            var ar = _tcp.BeginConnect(uri.Host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                throw new IOException("连接调试端口超时");
            _tcp.EndConnect(ar);
            _tcp.NoDelay = true;
            _ns = _tcp.GetStream();
            _ns.ReadTimeout = timeoutMs;
            _ns.WriteTimeout = timeoutMs;

            var keyBytes = new byte[16];
            lock (Rng) { Rng.NextBytes(keyBytes); }
            string key = Convert.ToBase64String(keyBytes);
            string req = "GET " + uri.PathAndQuery + " HTTP/1.1\r\n"
                       + "Host: " + uri.Host + ":" + port + "\r\n"
                       + "Upgrade: websocket\r\n"
                       + "Connection: Upgrade\r\n"
                       + "Sec-WebSocket-Key: " + key + "\r\n"
                       + "Sec-WebSocket-Version: 13\r\n\r\n";
            var raw = Encoding.ASCII.GetBytes(req);
            _ns.Write(raw, 0, raw.Length);
            _ns.Flush();

            string head = ReadHeaders();
            if (head.IndexOf(" 101", StringComparison.Ordinal) < 0)
                throw new IOException("WebSocket 握手失败: " + head.Split('\n')[0].Trim());
        }

        string ReadHeaders()
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                int n = _ns.Read(one, 0, 1);
                if (n <= 0) throw new IOException("连接被关闭");
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb[sb.Length - 4] == '\r' && sb[sb.Length - 3] == '\n'
                    && sb[sb.Length - 2] == '\r' && sb[sb.Length - 1] == '\n')
                    return sb.ToString();
                if (sb.Length > 16384) throw new IOException("响应头异常");
            }
        }

        void SendFrame(int opcode, byte[] payload)
        {
            var head = new List<byte>(16);
            head.Add((byte)(0x80 | opcode));
            int len = payload.Length;
            if (len < 126) head.Add((byte)(0x80 | len));
            else if (len < 65536) { head.Add((byte)(0x80 | 126)); head.Add((byte)(len >> 8)); head.Add((byte)(len & 0xFF)); }
            else { head.Add((byte)(0x80 | 127)); for (int i = 7; i >= 0; i--) head.Add((byte)(((long)len >> (8 * i)) & 0xFF)); }
            var mask = new byte[4];
            lock (Rng) { Rng.NextBytes(mask); }
            head.AddRange(mask);
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
            var hb = head.ToArray();
            _ns.Write(hb, 0, hb.Length);
            if (payload.Length > 0) _ns.Write(payload, 0, payload.Length);
            _ns.Flush();
        }

        int ReadByteOrThrow()
        {
            int b = _ns.ReadByte();
            if (b < 0) throw new IOException("连接已关闭");
            return b;
        }

        byte[] ReadExactly(int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = _ns.Read(buf, off, n - off);
                if (r <= 0) throw new IOException("连接已关闭");
                off += r;
            }
            return buf;
        }

        string ReadMessage()
        {
            var ms = new MemoryStream();
            while (true)
            {
                int b0 = ReadByteOrThrow();
                int b1 = ReadByteOrThrow();
                bool fin = (b0 & 0x80) != 0;
                int opcode = b0 & 0x0F;
                long len = b1 & 0x7F;
                bool masked = (b1 & 0x80) != 0;
                if (len == 126) len = (ReadByteOrThrow() << 8) | ReadByteOrThrow();
                else if (len == 127)
                {
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | (uint)ReadByteOrThrow();
                }
                if (len > 32 * 1024 * 1024) throw new IOException("帧过大");
                byte[] mask = masked ? ReadExactly(4) : null;
                byte[] data = ReadExactly((int)len);
                if (masked) for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];

                if (opcode == 0x9) { SendFrame(0xA, data); continue; }   // ping -> pong
                if (opcode == 0xA) continue;                              // pong
                if (opcode == 0x8) throw new IOException("服务端关闭连接");
                ms.Write(data, 0, data.Length);
                if (fin) break;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public void SendText(string text) { SendFrame(0x1, Encoding.UTF8.GetBytes(text)); }

        /// <summary>执行 JS，返回 result.result.value（returnByValue）</summary>
        public object Eval(string expression, int timeoutMs)
        {
            int id = ++_id;
            string req = "{\"id\":" + id + ",\"method\":\"Runtime.evaluate\",\"params\":{"
                       + "\"expression\":" + Json.EncodeString(expression)
                       + ",\"returnByValue\":true,\"awaitPromise\":true,\"userGesture\":true}}";
            SendText(req);
            long deadline = Environment.TickCount + timeoutMs;
            while (unchecked(Environment.TickCount - deadline) < 0)
            {
                string txt = ReadMessage();
                var obj = Json.Dict(Json.Parse(txt));
                if (obj == null) continue;
                object mid;
                if (!obj.TryGetValue("id", out mid)) continue;     // 事件
                if ((int)Json.Num(mid, -1) != id) continue;
                var err = obj.ContainsKey("error") ? obj["error"] : null;
                if (err != null) throw new IOException("CDP 错误: " + Json.Str(err, "?"));
                var res = Json.Dict(obj.ContainsKey("result") ? obj["result"] : null);
                if (res == null) return null;
                var ex = Json.Dict(res.ContainsKey("exceptionDetails") ? res["exceptionDetails"] : null);
                if (ex != null) throw new IOException("JS 异常: " + Json.Str(ex["text"], "?"));
                var r2 = Json.Dict(res.ContainsKey("result") ? res["result"] : null);
                return r2 == null ? null : (r2.ContainsKey("value") ? r2["value"] : null);
            }
            throw new IOException("CDP 调用超时");
        }

        public void Dispose()
        {
            try { _ns.Close(); } catch { }
            try { _tcp.Close(); } catch { }
        }
    }

    sealed class Target
    {
        public string Url, Title, WsUrl, Type;
    }

    // ---------------------------------------------------------------- 播放器跟踪
    sealed class Player
    {
        public string WsUrl;
        public string Title = "";
        public WsClient Conn;
        public Rectangle HotZone;        // 物理像素：可长按的区域（视频画面去掉控制条）
        public bool Valid;
        public double Dpr = 1.0;
        public double Rate = 1.0;
        public bool Paused;
        public int FailCount;
    }

    sealed class Tracker
    {
        readonly Config _cfg;
        readonly object _gate = new object();
        readonly List<Player> _players = new List<Player>();
        readonly ConcurrentQueue<Action> _work = new ConcurrentQueue<Action>();
        Thread _thread, _worker;
        volatile bool _running;
        public volatile bool ClientReachable;
        public string LastError = "";

        public Tracker(Config cfg) { _cfg = cfg; }

        public void Start()
        {
            _running = true;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "cdp-worker" };
            _worker.Start();
            _thread = new Thread(Loop) { IsBackground = true, Name = "cdp-poller" };
            _thread.Start();
        }

        public void Stop() { _running = false; }

        /// <summary>把 CDP 操作排队到单独线程，避免阻塞鼠标钩子</summary>
        public void Post(Action a) { _work.Enqueue(a); }

        void WorkerLoop()
        {
            while (_running)
            {
                Action a;
                if (_work.TryDequeue(out a))
                {
                    try { a(); } catch (Exception ex) { Log.Write("worker 异常: " + ex.Message); }
                }
                else Thread.Sleep(5);
            }
        }

        public List<Player> Snapshot()
        {
            lock (_gate) return new List<Player>(_players);
        }

        /// <summary>命中测试：返回长按应作用于哪个播放器</summary>
        public Player HitTest(Point p)
        {
            Player best = null;
            lock (_gate)
            {
                foreach (var pl in _players)
                {
                    if (!pl.Valid) continue;
                    if (pl.HotZone.Contains(p)) { best = pl; break; }
                }
            }
            return best;
        }

        static List<Target> ListTargets(int port, int timeoutMs)
        {
            var list = new List<Target>();
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json/list");
            req.Timeout = timeoutMs;
            req.Proxy = null;
            req.KeepAlive = false;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var arr = Json.Parse(sr.ReadToEnd()) as List<object>;
                if (arr == null) return list;
                foreach (var item in arr)
                {
                    var d = Json.Dict(item);
                    if (d == null) continue;
                    var t = new Target();
                    t.Url = Json.Str(d.ContainsKey("url") ? d["url"] : null, "");
                    t.Title = Json.Str(d.ContainsKey("title") ? d["title"] : null, "");
                    t.WsUrl = Json.Str(d.ContainsKey("webSocketDebuggerUrl") ? d["webSocketDebuggerUrl"] : null, "");
                    t.Type = Json.Str(d.ContainsKey("type") ? d["type"] : null, "");
                    list.Add(t);
                }
            }
            return list;
        }

        const string StateJs = @"(function(){try{
var v=document.querySelector('video');
if(!v) return '{\""ok\"":0,\""reason\"":\""novideo\""}';
var r=v.getBoundingClientRect();
var m=document.querySelector('.bpx-player-control-mask')||document.querySelector('.bpx-player-control-wrap');
var mr=m?m.getBoundingClientRect():null;
return JSON.stringify({ok:1,rate:v.playbackRate,paused:v.paused?1:0,
dpr:window.devicePixelRatio||1,sx:window.screenX,sy:window.screenY,
x:r.left,y:r.top,w:r.width,h:r.height,
ctop:mr?mr.top:null,ch:mr?mr.height:null,title:document.title});
}catch(e){return '{\""ok\"":0,\""reason\"":\""'+String(e).replace(/[\""\\]/g,'')+'\""}';}})()";

        void Loop()
        {
            while (_running)
            {
                int sleep = _cfg.PollMs;
                try
                {
                    List<Target> targets;
                    try
                    {
                        targets = ListTargets(_cfg.DebugPort, 700);
                        ClientReachable = true;
                    }
                    catch
                    {
                        ClientReachable = false;
                        LastError = "无法连接调试端口 " + _cfg.DebugPort;
                        lock (_gate) { foreach (var pl in _players) { pl.Valid = false; } }
                        Thread.Sleep(sleep);
                        continue;
                    }

                    var players = new List<Target>();
                    foreach (var t in targets)
                        if (t.Type == "page" && t.Url != null && t.Url.IndexOf("player.html", StringComparison.OrdinalIgnoreCase) >= 0)
                            players.Add(t);

                    var seen = new HashSet<string>();
                    lock (_gate)
                    {
                        // 清理已消失的窗口
                        for (int i = _players.Count - 1; i >= 0; i--)
                        {
                            bool alive = false;
                            foreach (var t in players) if (t.WsUrl == _players[i].WsUrl) { alive = true; break; }
                            if (!alive)
                            {
                                try { if (_players[i].Conn != null) _players[i].Conn.Dispose(); } catch { }
                                _players.RemoveAt(i);
                            }
                        }

                        foreach (var t in players)
                        {
                            seen.Add(t.WsUrl);                            Player pl = null;
                            foreach (var q in _players) if (q.WsUrl == t.WsUrl) { pl = q; break; }
                            if (pl == null)
                            {
                                pl = new Player { WsUrl = t.WsUrl };
                                _players.Add(pl);
                                Log.Write("发现播放器窗口: " + t.Title);
                            }
                            pl.Title = t.Title;

                            try
                            {
                                bool freshConn = false;
                                if (pl.Conn == null) { pl.Conn = new WsClient(t.WsUrl, 900); freshConn = true; }
                                var val = pl.Conn.Eval(StateJs, 900);
                                var st = Json.Dict(Json.Parse(Json.Str(val, "{}")));
                                if (freshConn)
                                {
                                    // 新连接：清掉上次残留的加速
                                    try
                                    {
                                        var c = Json.Dict(Json.Parse(Json.Str(pl.Conn.Eval(Scripts.ClearBoost(), 900), "{}")));
                                        if (c != null && Json.Num(c.ContainsKey("cleared") ? c["cleared"] : null, 0) != 0)
                                            Log.Write("检测到残留加速，已还原为 " + Json.Num(c["rate"], 1) + "x");
                                    }
                                    catch { }
                                }
                                if (st != null && Json.Num(st.ContainsKey("ok") ? st["ok"] : null, 0) == 1)
                                {
                                    double dpr = Json.Num(st["dpr"], 1.0);
                                    double sx = Json.Num(st["sx"], 0), sy = Json.Num(st["sy"], 0);
                                    double x = Json.Num(st["x"], 0), y = Json.Num(st["y"], 0);
                                    double w = Json.Num(st["w"], 0), h = Json.Num(st["h"], 0);
                                    pl.Dpr = dpr;
                                    pl.Rate = Json.Num(st["rate"], 1.0);
                                    pl.Paused = Json.Num(st["paused"], 0) != 0;

                                    int left = (int)Math.Round((sx + x) * dpr);
                                    int top = (int)Math.Round((sy + y) * dpr);
                                    int right = (int)Math.Round((sx + x + w) * dpr);
                                    int bottom = (int)Math.Round((sy + y + h) * dpr);

                                    double ctop = Json.Num(st.ContainsKey("ctop") ? st["ctop"] : null, double.NaN);
                                    int cut = double.IsNaN(ctop)
                                        ? bottom - (int)Math.Round(_cfg.BottomExcludeCssPx * dpr)
                                        : (int)Math.Round((sy + ctop) * dpr);
                                    if (cut > bottom) cut = bottom;
                                    int topCut = top + (int)Math.Round(_cfg.TopExcludeCssPx * dpr);
                                    if (topCut > cut) topCut = cut;

                                    var zone = Rectangle.FromLTRB(left, topCut, right, cut);
                                    pl.HotZone = zone;
                                    pl.Valid = zone.Width > 40 && zone.Height > 40;
                                    pl.FailCount = 0;
                                }
                                else
                                {
                                    pl.Valid = false;
                                    pl.FailCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                pl.Valid = false;
                                pl.FailCount++;
                                Log.Debug("轮询播放器失败(" + pl.FailCount + "): " + ex.Message);
                                if (pl.FailCount >= 3)
                                {
                                    try { if (pl.Conn != null) pl.Conn.Dispose(); } catch { }
                                    pl.Conn = null;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log.Write("轮询异常: " + ex.Message);
                }
                Thread.Sleep(sleep);
            }
        }
    }

    // ---------------------------------------------------------------- JS 片段
    static class Scripts
    {
        public static string Fire(double speed, bool resumeWhenPaused)
        {
            string resume = resumeWhenPaused
                ? "if(v.paused){try{v.play();}catch(e){}}"
                : "";
            return @"(function(){try{
var v=document.querySelector('video');
if(!v) return '{\""ok\"":0}';
if(window.__bliplpActive!==1){window.__bliplpRate=v.playbackRate;}
window.__bliplpActive=1;
window.__bliplpPaused=v.paused?1:0;
v.playbackRate=" + speed.ToString("0.###", CultureInfo.InvariantCulture) + @";
" + resume + @"
return JSON.stringify({ok:1,rate:v.playbackRate,base:window.__bliplpRate,paused:v.paused?1:0});
}catch(e){return '{\""ok\"":0,\""err\"":\""'+String(e).replace(/[\""\\]/g,'')+'\""}';}})()";
        }

        public static string Release()
        {
            return @"(function(){try{
var v=document.querySelector('video');
if(!v) return '{\""ok\"":0}';
var base=(window.__bliplpRate===undefined)?1:window.__bliplpRate;
v.playbackRate=base;
window.__bliplpActive=0;
return JSON.stringify({ok:1,rate:v.playbackRate,paused:v.paused?1:0,base:base});
}catch(e){return '{\""ok\"":0}';}})()";
        }

        /// <summary>工具启动/重连时调用：清掉上次异常退出可能残留的加速状态</summary>
        public static string ClearBoost()
        {
            return @"(function(){try{
var v=document.querySelector('video');
if(!v) return '{\""ok\"":0}';
if(window.__bliplpActive===1){
  var b=(window.__bliplpRate===undefined)?1:window.__bliplpRate;
  v.playbackRate=b; window.__bliplpActive=0;
  return JSON.stringify({ok:1,cleared:1,rate:b});
}
return JSON.stringify({ok:1,cleared:0});
}catch(e){return '{\""ok\"":0}';}})()";
        }

        public static string CheckPause()
        {
            return @"(function(){try{
var v=document.querySelector('video');
if(!v||window.__bliplpPaused===undefined) return '{\""ok\"":0}';
var want=window.__bliplpPaused, got=v.paused?1:0, fixed=0;
if(want===1&&got===0){v.pause();fixed=1;}
else if(want===0&&got===1){v.play();fixed=1;}
return JSON.stringify({ok:1,want:want,got:got,fixed:fixed});
}catch(e){return '{\""ok\"":0}';}})()";
        }
    }

    // ---------------------------------------------------------------- 长按逻辑
    sealed class LongPress
    {
        readonly Config _cfg;
        readonly Tracker _tracker;

        readonly object _gate = new object();
        bool _armed, _fired, _blockedDown;
        int _pressTick;
        Point _pressPoint;
        Player _pressPlayer;

        public readonly IntPtr Magic = new IntPtr(0x424C5031); // 'BLP1'

        public LongPress(Config cfg, Tracker tracker) { _cfg = cfg; _tracker = tracker; }

        public bool Armed { get { lock (_gate) return _armed; } }

        // 鼠标钩子线程调用
        public void OnDown(Point p)
        {
            var pl = _tracker.HitTest(p);
            if (pl == null) return;
            if (!Client.UnderCursor(_cfg, p)) return;   // 该点上不是哔哩哔哩的窗口（被别的窗口遮挡）
            lock (_gate)
            {
                _armed = true;
                _fired = false;
                _blockedDown = (_cfg.ClickMode == "block");
                _pressTick = Environment.TickCount;
                _pressPoint = p;
                _pressPlayer = pl;
            }
            Log.Debug("按下命中 " + p.X + "," + p.Y + " -> " + pl.HotZone);
        }

        /// <returns>true = 本次长按已触发（调用方应吞掉鼠标抬起事件）</returns>
        public bool OnUp(Point p, out bool needInjectClick)
        {
            needInjectClick = false;
            bool fired, blocked, armed, moved;
            Player pl;
            lock (_gate)
            {
                fired = _fired;
                blocked = _blockedDown;
                armed = _armed;
                moved = Math.Abs(p.X - _pressPoint.X) > _cfg.DragCancelPx || Math.Abs(p.Y - _pressPoint.Y) > _cfg.DragCancelPx;
                pl = _pressPlayer;
                _armed = false;
                _fired = false;
                _blockedDown = false;
                _pressPlayer = null;
            }

            if (fired && pl != null)
            {
                _tracker.Post(() =>
                {
                    try { EvalOn(pl, Scripts.Release()); }
                    catch (Exception ex) { Log.Write("恢复倍速失败: " + ex.Message); }
                    if (_cfg.RestorePauseState)
                    {
                        Thread.Sleep(180);
                        try
                        {
                            var r = Json.Dict(Json.Parse(Json.Str(EvalOn(pl, Scripts.CheckPause()), "{}")));
                            if (r != null && Json.Num(r.ContainsKey("fixed") ? r["fixed"] : null, 0) != 0)
                                Log.Write("点击导致播放状态改变，已还原");
                        }
                        catch (Exception ex) { Log.Debug("校验播放状态失败: " + ex.Message); }
                    }
                });
                return true;
            }

            if (blocked && armed && !moved) needInjectClick = true;   // block 模式下的普通短按：回放点击
            return false;
        }

        public void Cancel() { lock (_gate) { _armed = false; _fired = false; _blockedDown = false; _pressPlayer = null; } }

        // 计时线程调用：判断是否达到长按时长（若中途拖动则取消）
        public void Tick()
        {
            bool shouldFire = false;
            Player pl = null;
            lock (_gate)
            {
                if (!_armed || _fired) return;

                Native.POINT cur;
                if (Native.GetCursorPos(out cur)
                    && (Math.Abs(cur.X - _pressPoint.X) > _cfg.DragCancelPx
                        || Math.Abs(cur.Y - _pressPoint.Y) > _cfg.DragCancelPx))
                {
                    _armed = false;                       // 视为拖动，不触发倍速
                    Log.Debug("检测到拖动，取消长按");
                    return;
                }

                if (unchecked(Environment.TickCount - _pressTick) >= _cfg.HoldMs)
                {
                    shouldFire = true;
                    _fired = true;
                    pl = _pressPlayer;
                }
            }
            if (!shouldFire || pl == null) return;

            double speed = _cfg.Speed;
            _tracker.Post(() =>
            {
                try
                {
                    var res = Json.Dict(Json.Parse(Json.Str(EvalOn(pl, Scripts.Fire(speed, _cfg.ResumeWhenPaused)), "{}")));
                    if (res != null && Json.Num(res.ContainsKey("ok") ? res["ok"] : null, 0) == 1)
                        Log.Write("长按倍速 " + speed.ToString("0.##") + "x 已生效 (" + (pl.Title ?? "") + ")");
                    else
                        Log.Write("设置倍速失败（页面未就绪）");
                }
                catch (Exception ex) { Log.Write("设置倍速异常: " + ex.Message); }
            });
        }

        static string EvalOn(Player pl, string js)
        {
            if (pl.Conn == null) throw new IOException("播放器连接已断开");
            return Json.Str(pl.Conn.Eval(js, 900), "");
        }
    }

    // ---------------------------------------------------------------- 钩子
    sealed class Hook : IDisposable
    {
        readonly LongPress _lp;
        readonly Tracker _tracker;
        Native.LowLevelMouseProc _proc;
        IntPtr _hook = IntPtr.Zero;
        Thread _thread;

        public Hook(LongPress lp, Tracker tracker) { _lp = lp; _tracker = tracker; }

        public void Start()
        {
            _thread = new Thread(() =>
            {
                _proc = Callback;
                _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero)
                {
                    Log.Write("安装鼠标钩子失败，错误码 " + Marshal.GetLastWin32Error());
                    return;
                }
                Log.Write("鼠标钩子已安装");
                Application.Run();
            });
            _thread.IsBackground = true;
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            try
            {
                var data = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                if (data.dwExtraInfo == _lp.Magic) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                var pt = new Point(data.pt.X, data.pt.Y);

                if (msg == Native.WM_LBUTTONDOWN)
                {
                    _lp.OnDown(pt);
                    if (_blockMode && _lp.Armed) return (IntPtr)1;   // 拦住按下，短按时再回放
                }
                else if (msg == Native.WM_LBUTTONUP)
                {
                    bool inject;
                    bool wasLong = _lp.OnUp(pt, out inject);
                    if (wasLong) return (IntPtr)1;                 // 吞掉抬起：避免触发「单击暂停」
                    if (inject)
                    {
                        Native.InjectClick(_lp.Magic);
                        return (IntPtr)1;                          // 短按由我们回放
                    }
                }
            }
            catch (Exception ex) { Log.Debug("钩子异常: " + ex.Message); }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        bool _blockMode;
        public bool BlockMode { set { _blockMode = value; } }

        public void Dispose()
        {
            try { if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook); } catch { }
        }
    }

    // ---------------------------------------------------------------- 进程/客户端
    static class Client
    {
        public static bool PortOpen(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        public static string ProcName(Config cfg)
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(cfg.ClientExe); }
            catch { return "哔哩哔哩"; }
        }

        public static bool Running(Config cfg)
        {
            try { return Process.GetProcessesByName(ProcName(cfg)).Length > 0; }
            catch { return false; }
        }

        public static HashSet<int> Pids(Config cfg)
        {
            var set = new HashSet<int>();
            try
            {
                foreach (var p in Process.GetProcessesByName(ProcName(cfg)))
                {
                    try { set.Add(p.Id); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
            return set;
        }

        public static bool UnderCursor(Config cfg, Point p)
        {
            var h = Native.WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y });
            if (h == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            var pids = _pidCache;
            if (pids == null || pids.Count == 0) return true;   // 尚未刷新出进程表，退化为仅按热区判断
            return pids.Contains((int)pid);
        }

        /// <summary>由后台线程定期刷新（不要在鼠标钩子里做进程枚举，可能超时）</summary>
        public static void RefreshPids(Config cfg)
        {
            try { _pidCache = Pids(cfg); _pidCacheTick = Environment.TickCount; }
            catch { }
        }

        static volatile HashSet<int> _pidCache;
        static int _pidCacheTick;

        public static void Launch(Config cfg)
        {
            var psi = new ProcessStartInfo(cfg.ClientExe);
            psi.Arguments = "--remote-debugging-port=" + cfg.DebugPort + " --remote-allow-origins=*";
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(cfg.ClientExe);
            psi.UseShellExecute = false;
            // 关键：清掉这个环境变量，否则 Electron 会以 Node 模式启动并秒退
            try { psi.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE"); } catch { }
            try { psi.EnvironmentVariables.Remove("NODE_OPTIONS"); } catch { }
            Process.Start(psi);
            Log.Write("已带调试端口启动客户端");
        }

        public static void Kill(Config cfg)
        {
            foreach (var p in Process.GetProcessesByName(ProcName(cfg)))
            {
                try { p.Kill(); Log.Write("结束客户端进程 " + p.Id); } catch (Exception ex) { Log.Write("结束进程失败: " + ex.Message); }
                try { p.Dispose(); } catch { }
            }
        }
    }

    // ---------------------------------------------------------------- 托盘
    static class Tray
    {
        static NotifyIcon _icon;
        static Config _cfg;
        static Tracker _tracker;
        static ToolStripMenuItem _status;
        static ToolStripMenuItem _speedMenu, _holdMenu;
        static readonly List<ToolStripMenuItem> _speedItems = new List<ToolStripMenuItem>();
        static readonly List<ToolStripMenuItem> _holdItems = new List<ToolStripMenuItem>();

        // 右键菜单里可选的倍速与触发时长
        static readonly double[] SpeedPresets = { 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0 };
        static readonly int[] HoldPresets = { 200, 250, 300, 400, 500 };

        public static string FmtSpeed(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture) + "x"; }

        /// <summary>只绑定配置、不建托盘（命令行改设置时用）</summary>
        public static void Bind(Config cfg) { _cfg = cfg; }

        public static void Setup(Config cfg, Tracker tracker)
        {
            _cfg = cfg;
            _tracker = tracker;

            var menu = new ContextMenuStrip();
            _status = new ToolStripMenuItem("状态：启动中…") { Enabled = false };
            menu.Items.Add(_status);
            menu.Items.Add(new ToolStripSeparator());

            // ---- 长按倍速 ----
            _speedMenu = new ToolStripMenuItem("长按倍速");
            foreach (double v in SpeedPresets)
            {
                double val = v;
                var it = new ToolStripMenuItem(FmtSpeed(val)) { Tag = val };
                it.Click += (s, e) => SetSpeed(val);
                _speedItems.Add(it);
                _speedMenu.DropDownItems.Add(it);
            }
            if (!IsPreset(SpeedPresets, cfg.Speed)) AddCustomSpeed(cfg.Speed);
            menu.Items.Add(_speedMenu);

            // ---- 触发时长 ----
            _holdMenu = new ToolStripMenuItem("触发时长");
            foreach (int ms in HoldPresets)
            {
                int val = ms;
                var it = new ToolStripMenuItem(val + " 毫秒") { Tag = (double)val };
                it.Click += (s, e) => SetHold(val);
                _holdItems.Add(it);
                _holdMenu.DropDownItems.Add(it);
            }
            if (!IsPresetInt(HoldPresets, cfg.HoldMs)) AddCustomHold(cfg.HoldMs);
            menu.Items.Add(_holdMenu);
            menu.Items.Add(new ToolStripSeparator());

            var restart = new ToolStripMenuItem("重启客户端并启用长按倍速");
            restart.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                Client.Kill(_cfg);
                Thread.Sleep(1500);
                Client.Launch(_cfg);
            });
            menu.Items.Add(restart);

            var launch = new ToolStripMenuItem("启动哔哩哔哩（带倍速支持）");
            launch.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ => { try { Client.Launch(_cfg); } catch (Exception ex) { Log.Write(ex.Message); } });
            menu.Items.Add(launch);

            var autoRun = new ToolStripMenuItem("开机自动运行") { CheckOnClick = true };
            autoRun.Checked = AutoRunEnabled();
            autoRun.CheckedChanged += (s, e) => SetAutoRun(autoRun.Checked);
            menu.Items.Add(autoRun);

            menu.Items.Add(new ToolStripSeparator());
            var openCfg = new ToolStripMenuItem("打开配置文件");
            openCfg.Click += (s, e) => { try { Process.Start("notepad.exe", Config.ConfigPath); } catch { } };
            menu.Items.Add(openCfg);

            var openLog = new ToolStripMenuItem("查看日志");
            openLog.Click += (s, e) => { try { Process.Start("notepad.exe", Log.Path); } catch { } };
            menu.Items.Add(openLog);

            menu.Items.Add(new ToolStripSeparator());
            var quit = new ToolStripMenuItem("退出");
            quit.Click += (s, e) => { _icon.Visible = false; Application.Exit(); };
            menu.Items.Add(quit);

            _icon = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "哔哩哔哩 长按倍速",
                Visible = true,
                ContextMenuStrip = menu,
            };

            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (s, e) => Refresh();
            timer.Start();
            SyncMenus();
        }

        static bool IsPreset(double[] list, double v)
        {
            foreach (double x in list) if (Math.Abs(x - v) < 0.001) return true;
            return false;
        }

        static bool IsPresetInt(int[] list, int v)
        {
            foreach (int x in list) if (x == v) return true;
            return false;
        }

        static void AddCustomSpeed(double v)
        {
            var it = new ToolStripMenuItem(FmtSpeed(v)) { Tag = v };
            it.Click += (s, e) => SetSpeed(v);
            _speedItems.Insert(0, it);
            _speedMenu.DropDownItems.Insert(0, it);
        }

        static void AddCustomHold(int ms)
        {
            var it = new ToolStripMenuItem(ms + " 毫秒") { Tag = (double)ms };
            it.Click += (s, e) => SetHold(ms);
            _holdItems.Insert(0, it);
            _holdMenu.DropDownItems.Insert(0, it);
        }

        /// <summary>设置长按倍速（菜单点击 / 命令行都走这里）：立即生效并写回 config.json</summary>
        public static void SetSpeed(double v)
        {
            if (v <= 0) return;
            _cfg.Speed = v;
            if (!IsPreset(SpeedPresets, v) && _speedMenu != null)
            {
                bool has = false;
                foreach (var it in _speedItems) if (Math.Abs((double)it.Tag - v) < 0.001) has = true;
                if (!has) AddCustomSpeed(v);
            }
            SaveConfig();
            SyncMenus();
            Log.Write("长按倍速已设为 " + FmtSpeed(v));
        }

        /// <summary>设置长按触发时长（毫秒）</summary>
        public static void SetHold(int ms)
        {
            if (ms < 80) ms = 80;
            _cfg.HoldMs = ms;
            if (!IsPresetInt(HoldPresets, ms) && _holdMenu != null)
            {
                bool has = false;
                foreach (var it in _holdItems) if (Math.Abs((double)it.Tag - ms) < 0.001) has = true;
                if (!has) AddCustomHold(ms);
            }
            SaveConfig();
            SyncMenus();
            Log.Write("触发时长已设为 " + ms + " 毫秒");
        }

        static void SaveConfig()
        {
            try { File.WriteAllText(Config.ConfigPath, _cfg.ToJson(), new UTF8Encoding(false)); }
            catch (Exception ex) { Log.Write("保存配置失败: " + ex.Message); }
        }

        /// <summary>把菜单勾选状态与当前配置同步（没有托盘时也能安全调用）</summary>
        static void SyncMenus()
        {
            try
            {
                if (_cfg == null) return;
                if (_speedMenu != null)
                {
                    _speedMenu.Text = "长按倍速（当前 " + FmtSpeed(_cfg.Speed) + "）";
                    foreach (var it in _speedItems) it.Checked = Math.Abs((double)it.Tag - _cfg.Speed) < 0.001;
                }
                if (_holdMenu != null)
                {
                    _holdMenu.Text = "触发时长（当前 " + _cfg.HoldMs + " 毫秒）";
                    foreach (var it in _holdItems) it.Checked = Math.Abs((double)it.Tag - _cfg.HoldMs) < 0.001;
                }
            }
            catch (Exception ex) { Log.Write("刷新菜单勾选失败: " + ex.Message); }
        }

        static volatile string _balloonTitle, _balloonText;

        /// <summary>可以任意线程调用；真正弹气泡在 UI 线程的定时器里做</summary>
        public static void Balloon(string title, string text)
        {
            if (!_cfg.ShowNotifications) return;
            _balloonTitle = title;
            _balloonText = text;
        }

        static void Refresh()
        {
            string t = _balloonTitle, x = _balloonText;
            if (x != null)
            {
                _balloonTitle = null;
                _balloonText = null;
                try { _icon.ShowBalloonTip(6000, t, x, ToolTipIcon.Info); } catch { }
            }

            string s;
            if (!_tracker.ClientReachable)
            {
                if (Client.PortOpen(_cfg.DebugPort))
                    s = "状态：调试端口异常";
                else if (Client.Running(_cfg))
                    s = "状态：客户端未开启调试端口（右键→重启客户端）";
                else
                    s = "状态：客户端未运行";
            }
            else
            {
                int n = 0;
                foreach (var p in _tracker.Snapshot()) if (p.Valid) n++;
                s = n > 0
                    ? "状态：已就绪（" + n + " 个播放窗口，长按 " + _cfg.HoldMs + "ms → " + _cfg.Speed.ToString("0.##") + "x）"
                    : "状态：已连接，等待播放窗口";
            }
            if (_status != null && _status.Text != s) _status.Text = s;
            if (_icon != null)
            {
                string tip = s.Length > 62 ? s.Substring(0, 62) : s;
                try { _icon.Text = tip; } catch { }
            }
        }

        static Icon MakeIcon()
        {
            try
            {
                var bmp = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Color.FromArgb(255, 251, 114, 153)))
                        g.FillEllipse(b, 1, 1, 30, 30);
                    using (var f = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("2x", f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
            catch { return SystemIcons.Application; }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "BiliLongPress";

        static bool AutoRunEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        static void SetAutoRun(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                Log.Write("开机自启: " + (on ? "已开启" : "已关闭"));
            }
            catch (Exception ex) { Log.Write("设置开机自启失败: " + ex.Message); }
        }
    }

    // ---------------------------------------------------------------- 入口
    static class Program
    {
        static Mutex _single;
        static readonly StringBuilder Rep = new StringBuilder();

        static void Say(string s)
        {
            Rep.AppendLine(s);
            try { Console.WriteLine(s); } catch { }
        }

        static void FlushReport(string name)
        {
            try
            {
                File.WriteAllText(System.IO.Path.Combine(Config.AppDir, name),
                    Rep.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Native.EnableDpiAwareness();
            bool probe = false, speedtest = false, verbose = false;
            double cliSpeed = 0;
            int cliHold = 0;
            foreach (var a in args)
            {
                if (a == "--probe") probe = true;
                if (a == "--speedtest") speedtest = true;
                if (a == "--verbose") verbose = true;
                if (a.StartsWith("--setspeed=", StringComparison.Ordinal))
                {
                    double v;
                    if (double.TryParse(a.Substring(11), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) cliSpeed = v;
                }
                if (a.StartsWith("--sethold=", StringComparison.Ordinal))
                {
                    int v;
                    if (int.TryParse(a.Substring(10), out v)) cliHold = v;
                }
            }
            bool setOnly = cliSpeed > 0 || cliHold > 0;
            if (probe || speedtest || setOnly) { ConsoleHost.Attach(); }

            var cfg = Config.Load();
            Log.Init(cfg.LogDir);
            Log.Verbose = cfg.VerboseLog || verbose;

            // 命令行直接改设置（和托盘菜单走同一套逻辑）
            if (setOnly)
            {
                Tray.Bind(cfg);
                if (cliSpeed > 0) Tray.SetSpeed(cliSpeed);
                if (cliHold > 0) Tray.SetHold(cliHold);
                Say("已保存到 " + Config.ConfigPath);
                Say("  长按倍速 : " + cfg.Speed.ToString("0.###", CultureInfo.InvariantCulture) + "x");
                Say("  触发时长 : " + cfg.HoldMs + " 毫秒");
                FlushReport("setting-report.txt");
                return;
            }

            if (probe) { Probe(cfg); return; }
            if (speedtest) { SpeedTest(cfg); return; }

            bool created;
            _single = new Mutex(true, "BiliLongPress_SingleInstance", out created);
            if (!created) { MessageBox.Show("长按倍速工具已经在运行了（看任务栏托盘图标）。", "哔哩哔哩 长按倍速"); return; }

            Log.Write("启动。客户端=" + cfg.ClientExe + " 端口=" + cfg.DebugPort
                      + " 长按=" + cfg.HoldMs + "ms 倍速=" + cfg.Speed + "x 点击模式=" + cfg.ClickMode);

            var tracker = new Tracker(cfg);
            var longPress = new LongPress(cfg, tracker);
            var hook = new Hook(longPress, tracker);
            hook.BlockMode = (cfg.ClickMode == "block");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Tray.Setup(cfg, tracker);

            tracker.Start();
            hook.Start();

            // 计时线程：判断长按是否达到阈值；顺带定期刷新客户端进程表
            var tick = new Thread(() =>
            {
                int lastPid = 0;
                while (true)
                {
                    longPress.Tick();
                    if (unchecked(Environment.TickCount - lastPid) > 3000)
                    {
                        lastPid = Environment.TickCount;
                        Client.RefreshPids(cfg);
                    }
                    Thread.Sleep(15);
                }
            });
            tick.IsBackground = true;
            tick.Start();

            // 客户端可用性检查
            var watchdog = new Thread(() =>
            {
                bool notified = false;
                while (true)
                {
                    try
                    {
                        if (!Client.PortOpen(cfg.DebugPort))
                        {
                            bool running = Client.Running(cfg);
                            if (!running && cfg.AutoLaunchClient && !notified)
                            {
                                notified = true;
                                Thread.Sleep(1200);
                                if (!Client.PortOpen(cfg.DebugPort) && !Client.Running(cfg)) Client.Launch(cfg);
                            }
                            else if (running && !notified)
                            {
                                notified = true;
                                if (cfg.AutoRestartClient)
                                {
                                    Log.Write("客户端未带调试端口运行，自动重启");
                                    Client.Kill(cfg);
                                    Thread.Sleep(1500);
                                    Client.Launch(cfg);
                                }
                                else
                                {
                                    Tray.Balloon("哔哩哔哩 长按倍速", "客户端正在运行但没有开启调试端口，长按倍速暂时不可用。\n右键托盘图标 →「重启客户端并启用长按倍速」。");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log.Debug("watchdog: " + ex.Message); }
                    Thread.Sleep(3000);
                }
            });
            watchdog.IsBackground = true;
            watchdog.Start();

            Application.Run();
            hook.Dispose();
            tracker.Stop();
        }

        // ---- 诊断模式：输出几何信息，便于校准坐标 ----
        static void Probe(Config cfg)
        {
            Say("客户端路径 : " + cfg.ClientExe);
            Say("客户端存在 : " + File.Exists(cfg.ClientExe));
            Say("调试端口   : " + cfg.DebugPort + "  端口开启=" + Client.PortOpen(cfg.DebugPort));
            Say("进程运行   : " + Client.Running(cfg));
            {
                string pn = Client.ProcName(cfg);
                var codes = new StringBuilder();
                foreach (char ch in pn) codes.Append((int)ch).Append(",");
                Say("进程名     : '" + pn + "' 长度=" + pn.Length + " codes=" + codes
                    + "  直接查询命中=" + Process.GetProcessesByName(pn).Length);
                var all = Process.GetProcesses();
                Say("进程总数   : " + all.Length);
                int shown = 0;
                var hits = new StringBuilder();
                foreach (var pr in all)
                {
                    string pname = "";
                    string ppath = "";
                    try { pname = pr.ProcessName; } catch { }
                    try { ppath = pr.MainModule.FileName; } catch { }
                    if (pname.IndexOf('哔') >= 0 || ppath.IndexOf("bilibili", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (shown < 8) hits.Append("[").Append(pr.Id).Append(" ").Append(pname).Append("] ");
                        shown++;
                    }
                    try { pr.Dispose(); } catch { }
                }
                Say("匹配客户端 : " + shown + " 个  " + hits);
            }
            Say("长按/倍速  : " + cfg.HoldMs + "ms -> " + cfg.Speed + "x   点击模式=" + cfg.ClickMode);
            Say("");

            var tracker = new Tracker(cfg);
            tracker.Start();
            Thread.Sleep(1500);

            var players = tracker.Snapshot();
            Say("播放器窗口数: " + players.Count);
            foreach (var p in players)
            {
                Say("  URL      : " + p.WsUrl);
                Say("  标题     : " + p.Title);
                Say("  有效     : " + p.Valid + "  dpr=" + p.Dpr + "  当前倍速=" + p.Rate + "  暂停=" + p.Paused);
                Say("  长按热区 : " + p.HotZone + "  (物理像素)");

                if (p.Valid)
                {
                    var c = new Point(p.HotZone.Left + p.HotZone.Width / 2, p.HotZone.Top + p.HotZone.Height / 2);
                    var h = Native.WindowFromPoint(new Native.POINT { X = c.X, Y = c.Y });
                    Native.RECT wr; Native.GetWindowRect(h, out wr);
                    uint pid; Native.GetWindowThreadProcessId(h, out pid);
                    Say("  热区中心 : " + c);
                    Say("  该点窗口 : hwnd=0x" + h.ToInt64().ToString("x") + " pid=" + pid
                        + " 窗口矩形=" + wr.Left + "," + wr.Top + "," + wr.Right + "," + wr.Bottom);
                    var pids = new List<int>(Client.Pids(cfg));
                    Say("  客户端PID: " + string.Join(",", pids.ConvertAll(i => i.ToString()).ToArray()));
                    Say("  命中客户端: " + Client.UnderCursor(cfg, c));
                    Say("  屏幕尺寸 : " + Screen.PrimaryScreen.Bounds.Width + "x" + Screen.PrimaryScreen.Bounds.Height);
                }
            }
            tracker.Stop();
            Say("");
            Say("日志: " + Log.Path);
            FlushReport("probe-report.txt");
            Thread.Sleep(300);
        }

        // ---- 倍速通道自测：直接触发 N 倍速，1.5 秒后恢复（不经过鼠标） ----
        static void SpeedTest(Config cfg)
        {
            var tracker = new Tracker(cfg);
            tracker.Start();
            Thread.Sleep(1500);
            var players = tracker.Snapshot();
            if (players.Count == 0)
            {
                Say("没有播放器窗口");
                FlushReport("speedtest-report.txt");
                return;
            }
            var pl = players[0];
            Say("测试窗口: " + pl.Title);
            Say("设置前  : " + Rates(tracker));
            pl.Conn.Eval(Scripts.Fire(cfg.Speed, false), 900);
            Thread.Sleep(400);
            Say("设置后  : " + Rates(tracker) + "   (期望 " + cfg.Speed + ")");
            Thread.Sleep(1500);
            pl.Conn.Eval(Scripts.Release(), 900);
            Thread.Sleep(400);
            Say("恢复后  : " + Rates(tracker) + "   (期望回到 1)");
            tracker.Stop();
            FlushReport("speedtest-report.txt");
            Thread.Sleep(300);
        }

        static string Rates(Tracker t)
        {
            var sb = new StringBuilder();
            foreach (var p in t.Snapshot()) sb.Append(p.Rate.ToString("0.###", CultureInfo.InvariantCulture)).Append(" ");
            return sb.ToString().Trim();
        }

        static string Probe(Func<Player, double> f, Tracker t)
        {
            var sb = new StringBuilder();
            foreach (var p in t.Snapshot()) sb.Append(f(p).ToString("0.###", CultureInfo.InvariantCulture)).Append(" ");
            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// 命令行模式（--probe / --speedtest / --setspeed / --sethold）的输出去向。
    /// 只在当前进程已经有可用的控制台或已被重定向时打印；不去 AttachConsole/AllocConsole，
    /// 避免在"从资源管理器直接运行"等场景下弹出/挂起一个没人看的控制台。
    /// 所有命令行模式同时会把结果写成同目录下的 *-report.txt，输出丢了也能看文件。
    /// </summary>
    static class ConsoleHost
    {
        [DllImport("kernel32.dll")]
        static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll")]
        static extern bool GetConsoleMode(IntPtr h, out uint mode);

        public static void Attach()
        {
            try
            {
                IntPtr h = GetStdHandle(-11);                       // STD_OUTPUT_HANDLE
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return; // 没有输出句柄：只写报告文件
                uint mode;
                if (GetConsoleMode(h, out mode))
                {
                    try { Console.OutputEncoding = Encoding.UTF8; } catch { }
                }
                // 控制台或重定向都直接可用，Console.WriteLine 无需额外处理
            }
            catch { }
        }
    }
}
