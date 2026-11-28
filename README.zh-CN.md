# KK_VngeQuitFix

[English](README.md) | [简体中文](README.zh-CN.md)

一个很小的 BepInEx 插件，用于修复 **CharaStudio 关闭窗口时卡死**的问题，同时**完整保留 VNGE**。

## 功能

- 修复"关游戏卡住、只能用任务管理器结束进程"的问题
- VNGE / Unity Console 保持启用，游戏内功能不受影响
- 只接管退出流程，不改变游戏内行为
- 即使 VNGE 控制台里留着没输完的半行文字，也能正常退出

## 为什么会卡死？

VNGE 的控制台宿主（`Unity.Console.BepInEx.dll`）通过 `UC.UnityConsole.Abort()` 结束 IronPython REPL 线程。在当前这套 Unity/Mono 上，这个调用会先抛出 `System.DivideByZeroException`，导致退出按键没有注入、REPL 线程一直阻塞在 `ReadConsoleInput`，Mono 退出时便会永远等待它。

本插件会在 `ConsolePlugin.OnDisable` 之前介入：自己注入 `Ctrl+Z` + 回车，等待 REPL 线程退出（通常几毫秒内），然后跳过有 bug 的原始关停代码。

VNGE 控制台只有在**整行**等于 `Ctrl+Z` 时才把它当作 EOF，所以插件会写入**两组** `Ctrl+Z` + 回车：第一组结束正常的空提示行，第二组处理控制台里残留的半行输入。

VNGE 控制台本身是 BepInEx 插件。本插件用软依赖让 BepInEx 先加载它（BepInEx 按依赖做拓扑排序）；如果它仍然晚于本插件加载，`AppDomain.AssemblyLoad` 事件会在加载瞬间完成 hook。整个插件没有轮询线程。

## 安装

1. 从 [Releases](https://github.com/Magicgazer/KK_VngeQuitFix/releases/latest) 下载 `VngeQuitFix.dll`。
2. 放进 `<Game>\BepInEx\plugins\`。
3. 确认 `Unity.Console.BepInEx.dll`（VNGE 控制台）保持**启用**。
4. 启动游戏，正常使用 VNGE，关闭窗口即可正常退出。

## 验证

正常退出后，游戏目录下的 `output_log.txt` 里应包含：

```
[Info   :VNGE Quit Fix] [QuitFix] === VNGE Quit Fix 1.2.2 loaded (VngeQuitFix.dll) ===
[Info   :VNGE Quit Fix] [QuitFix] hooked Unity.ConsoleK.BepInEx.ConsolePlugin::OnDisable
[Info   :VNGE Quit Fix] [QuitFix] OnDisable: REPL thread alive, injecting EOF
[Info   :VNGE Quit Fix] [QuitFix] REPL thread exited in 0ms; skipping original Shutdown
```

## 卸载

删除 `BepInEx\plugins\VngeQuitFix.dll`，卡死会重新出现。

## 兼容性

- Koikatu / CharaStudio（Unity 5.6.2f1，BepInEx 5.4.22）
- VNGE / Unity Console v45.1
- 目标框架：.NET Framework 3.5（Unity Mono 2.0）
- 已在 Windows 11 上测试

## 构建

目标框架为 **.NET Framework 3.5**（Unity 的 Mono 2.0 配置），使用 **.NET Framework 3.5 的 C# 编译器**（`%WINDIR%\Microsoft.NET\Framework\v3.5\csc.exe`，C# 3.0）编译。`build.ps1` 引用游戏自带的 3.5 时代类库（`CharaStudio_Data\Managed` 下的 `mscorlib.dll`、`System.dll`、`System.Core.dll`）并传入 `/noconfig /nostdlib+`。

```powershell
.\build.ps1                        # 编译到 dist\VngeQuitFix.dll
.\build.ps1 -Deploy                # 编译并复制到 <Game>\BepInEx\plugins
.\build.ps1 -GameDir 'D:\Games\Koikatu' -Deploy
```

等价的命令行：

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

## 许可

MIT，详见 [LICENSE](LICENSE)。
