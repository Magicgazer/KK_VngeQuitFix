# KK_VngeQuitFix

[English](README.md) | [简体中文](README.zh-CN.md)

A small BepInEx plugin that fixes **CharaStudio hanging when you close the window** — without disabling VNGE.

## Features

- Fixes the "game freezes on exit, must be killed from Task Manager" issue
- Keeps VNGE / Unity Console fully working (no feature loss)
- Only touches the shutdown path; in-game behaviour is unchanged
- Still exits cleanly if you leave a half-typed line in the VNGE console

## Why does it hang?

The VNGE console host (`Unity.Console.BepInEx.dll`) stops its IronPython REPL thread through `UC.UnityConsole.Abort()`. On this Unity/Mono build that call throws `System.DivideByZeroException` before it can inject the exit keys, so the REPL thread is never stopped and Mono waits for it forever when the game quits.

This plugin intercepts `ConsolePlugin.OnDisable`, injects `Ctrl+Z` + `Enter` itself, waits for the REPL thread to exit (normally within a few milliseconds) and then skips the broken shutdown code.

The console only treats a line as EOF when the **whole line** equals `Ctrl+Z`, so the plugin writes **two** `Ctrl+Z` + `Enter` pairs: the first ends the normal empty prompt, the second covers a half-typed line left in the console.

The VNGE console is itself a BepInEx plugin. A soft dependency makes BepInEx load it before this plugin (BepInEx sorts plugins by dependencies); if it still shows up later, an `AppDomain.AssemblyLoad` handler hooks it immediately. There is no polling thread.

## Install

1. Download `VngeQuitFix.dll` from [Releases](https://github.com/Magicgazer/KK_VngeQuitFix/releases/latest).
2. Copy it into `<Game>\BepInEx\plugins\`.
3. Make sure `Unity.Console.BepInEx.dll` (VNGE console) stays **enabled**.
4. Start the game, use VNGE as usual, and close the window — it exits normally.

## Verify

After a normal exit, `output_log.txt` in the game folder should contain:

```
[Info   :VNGE Quit Fix] [QuitFix] === VNGE Quit Fix 1.2.2 loaded (VngeQuitFix.dll) ===
[Info   :VNGE Quit Fix] [QuitFix] hooked Unity.ConsoleK.BepInEx.ConsolePlugin::OnDisable
[Info   :VNGE Quit Fix] [QuitFix] OnDisable: REPL thread alive, injecting EOF
[Info   :VNGE Quit Fix] [QuitFix] REPL thread exited in 0ms; skipping original Shutdown
```

## Uninstall

Delete `BepInEx\plugins\VngeQuitFix.dll`. The hang will come back.

## Compatibility

- Koikatu / CharaStudio (Unity 5.6.2f1, BepInEx 5.4.22)
- VNGE / Unity Console v45.1
- Targets .NET Framework 3.5 (Unity Mono 2.0)
- Tested on Windows 11

## Build

Targets **.NET Framework 3.5** (Unity's Mono 2.0 profile) and is compiled with the **.NET Framework 3.5 C# compiler** (`%WINDIR%\Microsoft.NET\Framework\v3.5\csc.exe`, C# 3.0). `build.ps1` references the game's own 3.5-era class libraries (`mscorlib.dll`, `System.dll`, `System.Core.dll` under `CharaStudio_Data\Managed`) and passes `/noconfig /nostdlib+`.

```powershell
.\build.ps1                        # build dist\VngeQuitFix.dll
.\build.ps1 -Deploy                # build and copy to <Game>\BepInEx\plugins
.\build.ps1 -GameDir 'D:\Games\Koikatu' -Deploy
```

Manual equivalent:

```powershell
C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe /nologo /noconfig /nostdlib+ `
    /target:library /optimize+ `
    /r:"<Game>\CharaStudio_Data\Managed\mscorlib.dll" `
    /r:"<Game>\CharaStudio_Data\Managed\System.dll" `
    /r:"<Game>\CharaStudio_Data\Managed\System.Core.dll" `
    /r:"<Game>\CharaStudio_Data\Managed\UnityEngine.dll" `
    /r:"<Game>\BepInEx\core\BepInEx.dll" `
    /r:"<Game>\BepInEx\core\0Harmony.dll" `
    /out:dist\VngeQuitFix.dll src\VngeQuitFix.cs src\AssemblyInfo.cs
```

## License

MIT — see [LICENSE](LICENSE).
