using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
    /// IronPython REPL thread stays blocked in ReadConsoleInput and the game hangs on exit.
    ///
    /// What we do: on ConsolePlugin.OnDisable we inject Ctrl+Z + Enter ourselves (the same
    /// intent as Abort(), but without the buggy code), wait for the REPL thread to end and
    /// skip the original shutdown. The thread is a background thread, so once it exits the
    /// process can quit normally.
    ///
    /// Load order: the VNGE console is a BepInEx plugin. Declaring a soft dependency makes
    /// BepInEx sort us after it (BepInEx 5 sorts plugins topologically by dependencies) while
    /// still allowing this plugin to load when the console is missing. If the console assembly
    /// is nevertheless loaded after us, the AppDomain.AssemblyLoad handler hooks it right away;
    /// no polling thread is involved.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(ConsolePluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class FixPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "org.dsh.vngequitfix";
        private const string PluginName = "VNGE Quit Fix";
        private const string PluginVersion = "1.2.2";

        // GUID of Unity.Console.BepInEx.dll (class Unity.ConsoleK.BepInEx.ConsolePlugin).
        private const string ConsolePluginGuid = "a43c1bf8-9f95-4077-b8c1-fa9ec3d94dcd";
        private const string ConsolePluginTypeName = "Unity.ConsoleK.BepInEx.ConsolePlugin";
        private const string OnDisableMethodName = "OnDisable";
        private const string RunThreadFieldName = "runThread";

        private const int WaitTimeoutMs = 1000;

        private const uint GenericRead = 0x80000000u;
        private const uint GenericWrite = 0x40000000u;
        private const uint FileShareReadWrite = 3;
        private const uint OpenExisting = 3;
        private const ushort KeyEventType = 1;
        private const ushort VirtualKeyReturn = 0x0D;
        private const ushort VirtualKeyZ = 0x5A;
        private const uint LeftCtrlPressed = 0x0008;
        private const char EofChar = '\u001a';

        private static readonly object HookLock = new object();
        private static readonly MethodInfo PrefixMethod =
            typeof(FixPlugin).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);

        internal static ManualLogSource LogSource;
        private static Harmony _harmony;
        private static FieldInfo _runThreadField;
        private static volatile bool _hooked;
        private static volatile bool _gaveUp;
        private static volatile bool _subscribed;
        private static bool _hookFailureLogged;
        private static bool _shutdownHandled;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteConsoleInput(IntPtr h, INPUT_RECORD[] recs, uint len, out uint written);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

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

            LogInfo("=== " + PluginName + " " + PluginVersion + " loaded (" +
                Path.GetFileName(Assembly.GetExecutingAssembly().Location) + ") ===");

            // Subscribe before the first scan so an assembly loaded on another thread cannot
            // slip through the gap between the scan and the subscription.
            Subscribe();
            try { TryHook(null); }
            catch (Exception e) { LogWarning("initial hook failed: " + e.GetType().FullName + ": " + e.Message); }
            if (_hooked || _gaveUp) Unsubscribe();
        }

        private static void Subscribe()
        {
            if (_subscribed) return;
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                _subscribed = true;
            }
            catch (Exception e)
            {
                LogDebug("AssemblyLoad subscribe failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            try { AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad; }
            catch (Exception e) { LogDebug("AssemblyLoad unsubscribe failed: " + e.GetType().Name); }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                if (_hooked || _gaveUp) { Unsubscribe(); return; }

                // Only the assembly that was just loaded needs to be inspected here.
                TryHook(args.LoadedAssembly);

                if (_hooked || _gaveUp) Unsubscribe();
            }
            catch (Exception e)
            {
                // Never let an exception escape into the assembly loader.
                LogDebug("AssemblyLoad handler failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void TryHook(Assembly loadedAssembly)
        {
            if (_hooked || _gaveUp) return;

            // Resolve the target outside the gate: a full scan can take a moment and must never
            // make an AssemblyLoad handler wait, since that handler can run on the loading thread.
            Type pluginType = FindConsolePluginType(loadedAssembly);
            if (ReferenceEquals(pluginType, null)) return;

            MethodInfo onDisable = FindOnDisable(pluginType);
            if (ReferenceEquals(onDisable, null))
            {
                _gaveUp = true;
                LogWarning(OnDisableMethodName + "() not found on " + pluginType.FullName + "; quit fix disabled");
                return;
            }

            // Only patching needs to be serialized. Skip instead of blocking: the attempt that
            // holds the gate will either install the hook or mark the plugin as given up.
            if (!Monitor.TryEnter(HookLock)) return;
            try
            {
                if (_hooked || _gaveUp) return;

                try
                {
                    if (ReferenceEquals(_harmony, null)) _harmony = new Harmony(PluginGuid);
                    _harmony.Patch(
                        onDisable,
                        new HarmonyMethod(PrefixMethod),
                        null,
                        null,
                        null,
                        null);

                    _runThreadField = pluginType.GetField(
                        RunThreadFieldName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    _hooked = true;
                    LogInfo("hooked " + pluginType.FullName + "::" + OnDisableMethodName);
                }
                catch (Exception e)
                {
                    _gaveUp = true;
                    if (!_hookFailureLogged)
                    {
                        _hookFailureLogged = true;
                        LogWarning("hook failed: " + e.GetType().FullName + ": " + e.Message);
                    }
                }
            }
            finally
            {
                Monitor.Exit(HookLock);
            }
        }

        private static Type FindConsolePluginType(Assembly loadedAssembly)
        {
            if (!ReferenceEquals(loadedAssembly, null))
                return GetConsolePluginType(loadedAssembly);

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = GetConsolePluginType(assemblies[i]);
                if (!ReferenceEquals(type, null)) return type;
            }
            return null;
        }

        private static Type GetConsolePluginType(Assembly assembly)
        {
            try { return assembly.GetType(ConsolePluginTypeName, false); }
            catch { return null; }
        }

        private static MethodInfo FindOnDisable(Type type)
        {
            try
            {
                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name == OnDisableMethodName && methods[i].GetParameters().Length == 0)
                        return methods[i];
                }
            }
            catch { }
            return null;
        }

        private static bool Prefix(object __instance)
        {
            try
            {
                if (_shutdownHandled) return false;
                _shutdownHandled = true;

                Thread replThread = GetRunThread(__instance);
                if (!IsAlive(replThread))
                {
                    LogDebug("OnDisable: REPL thread is not running; nothing to stop");
                    return false;
                }

                LogInfo("OnDisable: REPL thread alive, injecting EOF");
                InjectEof();

                int waited = JoinFor(replThread, WaitTimeoutMs);
                if (!IsAlive(replThread))
                {
                    LogInfo("REPL thread exited in " + waited + "ms; skipping original Shutdown");
                    return false;
                }

                LogDebug("REPL still alive after " + waited + "ms; trying FreeConsole fallback");
                bool freed;
                try { freed = FreeConsole(); }
                catch (Exception e) { freed = false; LogDebug("FreeConsole threw " + e.GetType().Name); }
                LogDebug("FreeConsole=" + freed);

                int waited2 = JoinFor(replThread, WaitTimeoutMs);
                int total = waited + waited2;
                if (!IsAlive(replThread))
                {
                    LogInfo("REPL thread exited in " + total + "ms after inject+FreeConsole; skipping original Shutdown");
                }
                else
                {
                    LogWarning("REPL thread still alive after " + total + "ms; exit may be slower than usual");
                }
                return false;
            }
            catch (Exception e)
            {
                LogWarning("OnDisable interception failed: " + e.GetType().FullName + ": " + e.Message);
                return false;
            }
        }

        private static Thread GetRunThread(object instance)
        {
            if (ReferenceEquals(instance, null)) return null;
            try
            {
                FieldInfo field = _runThreadField;
                if (ReferenceEquals(field, null) || !field.DeclaringType.IsInstanceOfType(instance))
                {
                    field = instance.GetType().GetField(
                        RunThreadFieldName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (ReferenceEquals(field, null)) return null;
                return field.GetValue(instance) as Thread;
            }
            catch { return null; }
        }

        private static bool IsAlive(Thread thread)
        {
            return !ReferenceEquals(thread, null) && thread.IsAlive;
        }

        private static int JoinFor(Thread thread, int timeoutMs)
        {
            int start = Environment.TickCount;
            try { thread.Join(timeoutMs); } catch { }
            return unchecked(Environment.TickCount - start);
        }

        /// <summary>
        /// Writes two Ctrl+Z + Enter pairs. The VNGE console treats a whole line equal to
        /// Ctrl+Z as EOF, so the first pair ends the normal empty prompt; if the user left a
        /// half-typed line in the console, the first pair executes it and the second pair then
        /// ends the clean prompt.
        /// </summary>
        private static bool InjectEof()
        {
            IntPtr handle = CreateFile(
                "CONIN$",
                GenericRead | GenericWrite,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle.ToInt64() == -1)
            {
                LogWarning("CONIN$ open failed (error=" + Marshal.GetLastWin32Error() + "); skipping key injection");
                return false;
            }

            try
            {
                INPUT_RECORD[] records = new INPUT_RECORD[4];
                records[0] = MakeKey(VirtualKeyZ, EofChar, LeftCtrlPressed);
                records[1] = MakeKey(VirtualKeyReturn, '\r', 0);
                records[2] = MakeKey(VirtualKeyZ, EofChar, LeftCtrlPressed);
                records[3] = MakeKey(VirtualKeyReturn, '\r', 0);

                uint written;
                bool ok = WriteConsoleInput(handle, records, (uint)records.Length, out written);
                if (!ok)
                {
                    LogWarning("WriteConsoleInput failed (error=" + Marshal.GetLastWin32Error() + ")");
                    return false;
                }

                if (written == (uint)records.Length) LogDebug("WriteConsoleInput wrote " + written + " records");
                else LogDebug("WriteConsoleInput wrote " + written + "/" + records.Length + " records");
                return true;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static INPUT_RECORD MakeKey(ushort virtualKeyCode, char unicodeChar, uint controlKeyState)
        {
            INPUT_RECORD record = new INPUT_RECORD();
            record.EventType = KeyEventType;
            record.bKeyDown = 1;
            record.wRepeatCount = 1;
            record.wVirtualKeyCode = virtualKeyCode;
            record.UnicodeChar = unicodeChar;
            record.dwControlKeyState = controlKeyState;
            return record;
        }

        private static void LogInfo(string message) { Log(LogLevel.Info, message); }
        private static void LogDebug(string message) { Log(LogLevel.Debug, message); }
        private static void LogWarning(string message) { Log(LogLevel.Warning, message); }

        private static void Log(LogLevel level, string message)
        {
            try
            {
                ManualLogSource source = LogSource;
                if (!ReferenceEquals(source, null)) source.Log(level, "[QuitFix] " + message);
            }
            catch { }
        }
    }
}
