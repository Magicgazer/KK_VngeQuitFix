# KK_VngeQuitFix

[English](README.md) | [简体中文](README.zh-CN.md)

A small BepInEx plugin that fixes **CharaStudio hanging when you close the window** — without disabling VNGE.

## Features

- Fixes the "game freezes on exit, must be killed from Task Manager" issue
- Keeps VNGE / Unity Console fully working (no feature loss)
- Only touches the shutdown path; in-game behaviour is unchanged
- Writes a short `BepInEx\VngeQuitFix.log` for troubleshooting

## Why does it hang?

The VNGE console host (`Unity.Console.BepInEx.dll`) stops its IronPython REPL thread through `UC.UnityConsole.Abort()`. On this Unity/Mono build that call throws `System.DivideByZeroException` before it can inject the exit keys, so the REPL thread is never stopped and Mono waits for it forever when the game quits.

This plugin intercepts `ConsolePlugin.OnDisable`, injects `Ctrl+Z` + `Enter` itself, waits for the REPL thread to exit (~50 ms) and then skips the broken shutdown code.

## Install

1. Download `VngeQuitFix.dll` from [Releases](https://github.com/Magicgazer/KK_VngeQuitFix/releases/latest).
2. Copy it into `<Game>\BepInEx\plugins\`.
3. Make sure `Unity.Console.BepInEx.dll` (VNGE console) stays **enabled**.
4. Start the game, use VNGE as usual, and close the window — it exits normally.

## Verify

After a normal exit, `BepInEx\VngeQuitFix.log` should contain:

```
hooked Unity.ConsoleK.BepInEx.ConsolePlugin::OnDisable
--- OnDisable intercepted ---
WriteConsoleInput ok=True written=2
after inject: waited=50ms alive=False
REPL thread exited -> skip original shutdown
```

## Uninstall

Delete `BepInEx\plugins\VngeQuitFix.dll`. The hang will come back.

## Compatibility

- Koikatu / CharaStudio (`CharaStudio.exe`, Unity 5.6.2f1, BepInEx 5.4.22)
- VNGE / Unity Console v45.1
- Tested on Windows 11

## Build

Requires .NET Framework 4.x `csc.exe`. The game runs Mono 2.0, so only .NET 2.0-era APIs are used — do not use modern C# features.

```powershell
csc /target:library /langversion:5 `
    /r:"<Game>\CharaStudio_Data\Managed\UnityEngine.dll" `
    /r:"<Game>\BepInEx\core\BepInEx.dll" `
    /r:"<Game>\BepInEx\core\0Harmony.dll" `
    /r:System.dll /r:System.Core.dll `
    /out:dist\VngeQuitFix.dll src\VngeQuitFix.cs
```

## License

MIT — see [LICENSE](LICENSE).
