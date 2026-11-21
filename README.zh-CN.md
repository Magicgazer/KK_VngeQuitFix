# KK_VngeQuitFix

[English](README.md) | [简体中文](README.zh-CN.md)

一个很小的 BepInEx 插件，用于修复 **CharaStudio 关闭窗口时卡死**的问题，同时**完整保留 VNGE**。

## 功能

- 修复"关游戏卡住、只能用任务管理器结束进程"的问题
- VNGE / Unity Console 保持启用，游戏内功能不受影响
- 只接管退出流程，不改变游戏内行为
- 在 `BepInEx\VngeQuitFix.log` 留下简短日志，便于排查

## 为什么会卡死？

VNGE 的控制台宿主（`Unity.Console.BepInEx.dll`）通过 `UC.UnityConsole.Abort()` 结束 IronPython REPL 线程。在当前这套 Unity/Mono 上，这个调用会先抛出 `System.DivideByZeroException`，导致退出按键没有注入、REPL 线程一直阻塞在 `ReadConsoleInput`，Mono 退出时便会永远等待它。

本插件会在 `ConsolePlugin.OnDisable` 之前介入：自己注入 `Ctrl+Z` + 回车，等待 REPL 线程退出（约 50ms），然后跳过有 bug 的原始关停代码。

## 安装

1. 从 [Releases](https://github.com/Magicgazer/KK_VngeQuitFix/releases/latest) 下载 `VngeQuitFix.dll`。
2. 放进 `<Game>\BepInEx\plugins\`。
3. 确认 `Unity.Console.BepInEx.dll`（VNGE 控制台）保持**启用**。
4. 启动游戏，正常使用 VNGE，关闭窗口即可正常退出。

## 验证

正常退出后，`BepInEx\VngeQuitFix.log` 里应包含：

```
hooked Unity.ConsoleK.BepInEx.ConsolePlugin::OnDisable
--- OnDisable intercepted ---
WriteConsoleInput ok=True written=2
after inject: waited=50ms alive=False
REPL thread exited -> skip original shutdown
```

## 卸载

删除 `BepInEx\plugins\VngeQuitFix.dll`，卡死会重新出现。

## 兼容性

- Koikatu / CharaStudio（`CharaStudio.exe`，Unity 5.6.2f1，BepInEx 5.4.22）
- VNGE / Unity Console v45.1
- 已在 Windows 11 上测试

## 构建

需要 .NET Framework 4.x 的 `csc.exe`。游戏运行的是 Mono 2.0，因此只使用了 .NET 2.0 时代的 API——不要使用新语法。

```powershell
csc /target:library /langversion:5 `
    /r:"<Game>\CharaStudio_Data\Managed\UnityEngine.dll" `
    /r:"<Game>\BepInEx\core\BepInEx.dll" `
    /r:"<Game>\BepInEx\core\0Harmony.dll" `
    /r:System.dll /r:System.Core.dll `
    /out:dist\VngeQuitFix.dll src\VngeQuitFix.cs
```

## 许可

MIT，详见 [LICENSE](LICENSE)。
