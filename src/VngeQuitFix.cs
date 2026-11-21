using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace VngeQuitFix
{
    /// <summary>
    /// Keeps the VNGE / Unity.Console plugin enabled but avoids its broken shutdown path.
    ///
    /// Why: Unity.Console.Program.Shutdown() -> Close() -> UC.UnityConsole.Abort() throws
    /// DivideByZeroException on this Unity/Mono build (console metrics are zero), so the
    /// Ctrl+Z injection, ScriptRuntime.Shutdown() and runThread.Abort() never run. The
    /// IronPython REPL thread stays blocked in ReadConsoleInput and Mono's shutdown waits
    /// for it forever -> the game window hangs on exit.
    ///
    /// What we do: on ConsolePlugin.OnDisable we inject Ctrl+Z + Enter ourselves (the same
    /// intent as Abort(), but without the buggy code), wait for the REPL thread to end and
    /// skip the original shutdown. The thread is a background thread, so once it exits the
    /// process can quit normally.
    /// </summary>
    [BepInPlugin("org.dsh.vngequitfix", "VNGE Quit Fix", "1.1.0")]
    public class FixPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource LogSource;
        private static string _logPath;
        private static bool _hooked;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteConsoleInput(IntPtr h, INPUT_RECORD[] recs, uint len, out uint written);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Explicit, Size = 20)]
        private struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public int bKeyDown;
            [FieldOffset(8)] public ushort wRepeatCount;
            [FieldOffset(10)] public ushort wVirtualKeyCode;
            [FieldOffset(12)] public ushort wVirtualScanCode;
            [FieldOffset(14)] public char UnicodeChar;
            [FieldOffset(16)] public uint dwControlKeyState;
        }

        private void Awake()
        {
            LogSource = Logger;
            try { _logPath = Path.Combine(Paths.BepInExRootPath, "VngeQuitFix.log"); }
            catch { _logPath = "VngeQuitFix.log"; }
            try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { }

            Log("=== VNGE Quit Fix 1.1 loaded (" + Path.GetFileName(Assembly.GetExecutingAssembly().Location) + ") ===");
            TryHook();

            Thread t = new Thread(delegate ()
            {
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(1000);
                    if (_hooked) return;
                    TryHook();
                }
            });
            t.IsBackground = true;
            t.Name = "VNGEQuitFixHook";
            t.Start();
        }

        private static void TryHook()
        {
            Type type = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { type = a.GetType("Unity.ConsoleK.BepInEx.ConsolePlugin", false); } catch { }
                if (!ReferenceEquals(type, null)) break;
            }
            if (ReferenceEquals(type, null)) return;

            MethodInfo m = null;
            try
            {
                MethodInfo[] all = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (MethodInfo x in all)
                {
                    if (x.Name == "OnDisable" && x.GetParameters().Length == 0) { m = x; break; }
                }
            }
            catch { }
            if (ReferenceEquals(m, null)) { Log("OnDisable not found on " + type.FullName); _hooked = true; return; }

            try
            {
                new Harmony("org.dsh.vngequitfix").Patch(
                    original: m,
                    prefix: new HarmonyMethod(typeof(FixPlugin).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)));
                _hooked = true;
                Log("hooked " + type.FullName + "::OnDisable");
            }
            catch (Exception e)
            {
                Log("hook failed: " + e.GetType().FullName + ": " + e.Message);
                _hooked = true;
            }
        }

        private static void Log(string s)
        {
            try
            {
                if (!ReferenceEquals(LogSource, null)) LogSource.LogInfo("[QuitFix] " + s);
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(_logPath))
                    File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        private static Thread GetRunThread(object instance)
        {
            try
            {
                FieldInfo fi = instance.GetType().GetField("runThread", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(fi, null)) return null;
                return fi.GetValue(instance) as Thread;
            }
            catch { return null; }
        }

        private static bool InjectExitKeys()
        {
            IntPtr h = CreateFile("CONIN$", 0x80000000u | 0x40000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h.ToInt64() == -1)
            {
                Log("CONIN$ open failed err=" + Marshal.GetLastWin32Error());
                return false;
            }
            try
            {
                INPUT_RECORD[] recs = new INPUT_RECORD[2];
                recs[0].EventType = 1; recs[0].bKeyDown = 1; recs[0].wRepeatCount = 1;
                recs[0].wVirtualKeyCode = 0x5A; recs[0].UnicodeChar = '\u001A'; recs[0].dwControlKeyState = 0x00000008;
                recs[1].EventType = 1; recs[1].bKeyDown = 1; recs[1].wRepeatCount = 1;
                recs[1].wVirtualKeyCode = 0x0D; recs[1].UnicodeChar = '\r';
                uint written;
                bool ok = WriteConsoleInput(h, recs, 2, out written);
                Log("WriteConsoleInput ok=" + ok + " written=" + written + " err=" + Marshal.GetLastWin32Error());
                return ok;
            }
            finally { CloseHandle(h); }
        }

        private static bool Prefix(object __instance)
        {
            Log("--- OnDisable intercepted ---");
            Thread th = GetRunThread(__instance);
            Log("runThread alive=" + AliveNow(th));

            InjectExitKeys();
            WaitDead(th, 2000, "after inject");
            if (!AliveNow(th))
            {
                Log("REPL thread exited -> skip original shutdown");
                return false;
            }

            try { Log("FreeConsole=" + FreeConsole()); }
            catch (Exception e) { Log("FreeConsole threw " + e.GetType().Name); }
            WaitDead(th, 2000, "after FreeConsole");
            Log(AliveNow(th) ? "REPL thread still alive; exit may hang" : "REPL thread exited after FreeConsole");
            return false;
        }

        private static bool AliveNow(Thread th)
        {
            return !ReferenceEquals(th, null) && th.IsAlive;
        }

        private static void WaitDead(Thread th, int ms, string tag)
        {
            int waited = 0;
            while (waited < ms && AliveNow(th)) { Thread.Sleep(50); waited += 50; }
            Log(tag + ": waited=" + waited + "ms alive=" + AliveNow(th));
        }
    }
}
