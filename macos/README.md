# macOS 安装说明

🌏 **语言 / Language / 언어**：[中文](README.md) · [English](README.en.md) · [한국어](README.ko.md)

给 macOS 原生版《赛菲莉娅》装背包整理插件。

插件本体和 BepInEx 都直接用 Releases 里的官方原文件，**一个字节都没改**；
macOS 这边只是换了一套加载器——Windows 靠 `winhttp.dll` + `doorstop_config.ini`
注入，macOS 得靠 `DYLD_INSERT_LIBRARIES` 加一个 dylib。

已在 macOS 26.5.2 / Apple M2 Max 及 macOS 27.0 / Apple M1 Pro / Sephiria 1.0.33（Unity 6000.3.21f1）+ 插件 v2.5.4 上实测通过。

主 README：[中文](../docs/README.md) · [English](../docs/README.en.md) · [한국어](../docs/README.ko.md)

## 安装

```sh
cd macos
./install.sh
```

脚本会自动完成：找到 Steam 里的游戏目录（含装在其它 Steam 库的情况）、
按需安装 Rosetta 2、从 Releases 下载最新完整包、现场编译注入库、装进游戏。

已经手动下过完整包的话可以直接指过去，省掉下载：

```sh
./install.sh --zip ~/Downloads/SephiriaBackpackOrganizer-v2.5.2.zip
```

需要 Xcode 命令行工具（`xcode-select --install`）来编译注入库。

## 启动

Steam 库 → 右键 Sephiria → 属性 → 通用 → 启动选项，粘贴（路径换成自己的）：

```
"/Users/你的用户名/Library/Application Support/Steam/steamapps/common/Sephiria/run_bepinex.sh" %command%
```

之后照常点「开始游戏」。安装脚本最后会把这一整行直接打印出来。

## 使用

按 **fn + F8** 整理背包；中键点击神器设置手动优先级（逐次循环 P1→P2→P3→P4，再点一次取消，每件独立）。

> macOS 上 F8 默认是「播放/暂停」媒体键，系统会先截走，游戏收不到，所以要加 fn。
> 想直接按 F8：系统设置 → 键盘 → 键盘快捷键 → 功能键 →
> 打开「将 F1、F2 等键用作标准功能键」。
> 也可以改键：编辑 `BepInEx/config/com.sephiria.backpack-organizer.cfg` 里的
> `[General] Hotkey`，改完重启游戏。

其余配置项和 Windows 版完全一样，见[主 README](../docs/README.md)。

出问题先看 `游戏目录/BepInEx/LogOutput.log`；如果 BepInEx 根本没起来，
看启动日志 `游戏目录/BepInEx/run_bepinex.log`（记录脚本启动和注入过程）
或 `Sephiria.app/Contents/MacOS/preloader_<时间戳>.log`。

## 卸载

```sh
./uninstall.sh
```

## 文件说明

| 文件 | 作用 |
| --- | --- |
| `install.sh` / `uninstall.sh` | 安装 / 卸载 |
| `doorstop_shim.c` | macOS 版注入库源码，装的时候现编 |
| `run_bepinex.sh` | 启动脚本（Rosetta 切换 + DYLD 处理） |

---

## 为什么需要单独写一个 doorstop

官方 UnityDoorstop 的 macOS 版在这个游戏上跑不起来，而且踩了三个连环坑，
都不是换个文件能绕过去的。记在这里省得以后再查一遍。

**1. UnityDoorstop 4.5.0 在 Unity 6 上直接 abort**

它的 plthook 手动解析 Mach-O 的 `LC_DYLD_CHAINED_FIXUPS`，遇到 Unity 6 /
macOS 26 编出来的二进制会报 `unknown imports format 0`。

`doorstop_shim.c` 完全不解析 Mach-O，改用 dyld 原生的 `__interpose` 去劫持
Unity 播放器对 `dlsym("mono_jit_init_version")` 的查询——这是 dyld 自己在绑定期
做的，跟 Mach-O 的具体格式无关。它读同样的 `DOORSTOP_*` 环境变量、导出同样的
托管侧变量，所以 `BepInEx.Unity.Mono.Preloader.dll` 不用改。

**2. `System.Native` 找不到，连游戏本体一起搞崩**

BepInEx 的 `DoorstopEntrypoint.Start()` 第一行就是 `DateTime.Now`。在 Unix 上
这一路会走到 `TimeZoneInfo` → `File.Exists` → `Interop.Sys`，而它的静态构造函数
要 P/Invoke 进 `System.Native`。

问题是 Unity 要等 `mono_jit_init_version` 返回**之后**才调 `mono_config_parse`
装上 dllmap（`System.Native` → `libmono-native.dylib`）。入口点跑得太早，查找必然
失败——而**失败的静态构造函数会被 CLR 永久缓存**。于是之后游戏自己每一次
`File.Exists` 都会重新抛 `TypeInitializationException`，卡在开场动画上连存档都读不出来。
Windows 上永远遇不到，因为那边 `System.IO` 直接走 Win32。

shim 现在会在跑托管入口点之前，自己用绝对路径把这几个 dllmap 注册好。

**3. 没设域配置，`Trace` 初始化就炸**

Unity 从来不设 `AppDomain.CurrentDomain.SetupInformation.ConfigurationFile`，
于是 BepInEx 在 `TraceLogSource.CreateSource()` 里碰 `System.Diagnostics.Trace`
时，`ConfigurationManager` 会抛
`ConfigurationErrorsException: The 'ExeConfigFilename' argument cannot be null.`。
shim 照官方 doorstop 的做法补上了 `mono_domain_set_config`。

## 为什么必须跑在 Rosetta 下

BepInEx 6.0.0-be.697 自带 MonoMod 22.5.1.1，它的 detour 引擎只会生成 x86/x64 的
跳板代码。原生 arm64 下 `DetourHelper.Runtime` 是 null，第一个 Harmony 补丁就炸成
`IL Compile Error → NullReferenceException`，插件一个都加载不了。

游戏是 universal 二进制，所以 `run_bepinex.sh` 会在 Apple 芯片上通过
`arch -x86_64` 把自己重新 exec 一遍，让游戏跑在 Rosetta 2 下。等上游 BepInEx 换到
支持 arm64 的 MonoMod（v25+）之后，这一步就可以去掉。

（调试用：设 `DOORSTOP_NO_ROSETTA=1` 可以强制原生 arm64，但用 Harmony 的插件会失效。）

## 关了 SIP 的机器上还有一个坑

正常情况下 dyld 会对 Apple 自家的 arm64e 系统二进制忽略 `DYLD_INSERT_LIBRARIES`。
但如果用户关掉了 SIP，dyld 就不再忽略——而 Steam 的悬浮窗
`gameoverlayrenderer.dylib` 只有 x86_64 和 arm64，没有 arm64e。结果是一旦通过
Steam 启动选项启动，脚本里 fork 出来的每一个 `uname` / `sysctl` / `arch` 都会在
`missing compatible architecture ... need 'arm64e'` 上 abort，脚本一行都跑不出来。

`run_bepinex.sh` 因此在**最开头**就把 `DYLD_INSERT_LIBRARIES` 和 `DYLD_LIBRARY_PATH`
存进 `DOORSTOP_SAVED_*` 并清空，一直到最后 exec 游戏之前才还原，中间不再有任何
子进程。这样 Steam 悬浮窗照样能注入进游戏，脚本也不会被自己人误伤。

（存的时候有个 `if [ -z "${VAR+x}" ]` 判断不能省：脚本会通过 `arch` 把自己重新
exec 一遍，没有这个判断的话第二遍会用空值把第一遍存的覆盖掉，Steam 悬浮窗就悄悄丢了。）

## macOS 27+ 适配与失效原因剖析

在 macOS 27 环境下，曾出现 Mod 无法正常运行且没有任何运行日志输出的问题，排查发现是由于系统沙盒与执行机制收紧导致的连环问题：

### 1. `defaults read` 跨目录沙盒拒绝导致脚本静默闪退（核心原因）

原启动脚本在解析 `.app` 内部主二进制文件名时调用了系统命令：
```sh
inner_executable_name=$(defaults read "${real_executable_name}/Contents/Info" CFBundleExecutable)
```
macOS 27 对 `defaults` 背后的 `cfprefsd` 守护进程实施了更严格的文件沙盒隔离。当跨目录读取 Steam 库中的 `Info.plist` 时，`cfprefsd` 会直接抛出权限异常：
```text
sandbox_extension_issue_file failed ... 1 (Operation not permitted)
Error: Domain '.../Contents/Info' not found.
```
由于脚本在开头启用了 `set -e`（遇到任何错误立即终止退出），**`run_bepinex.sh` 在执行游戏二进制之前就以 Exit Code 1 静默夭折**。游戏根本没有机会进入 BepInEx 注入流程。

**解决方案**：改用系统内置、直接就地解析 XML/二进制 plist 且不受 `cfprefsd` 沙盒限制的 `plutil -extract CFBundleExecutable raw` 与 `/usr/libexec/PlistBuddy`，并保留多层安全回退。

### 2. Steam 启动环境下的“日志黑洞”

当通过 Steam 的“启动选项”运行游戏时，子进程不挂载可见的终端控制台。`run_bepinex.sh` 和 `doorstop_shim.c` 原先的报错与状态追踪全部打印在标准错误流 `stderr` 上。
- 一旦脚本在启动早期出错（如上述 `defaults read` 闪退），所有错误输出都被系统默默丢弃；
- 此时 BepInEx 引擎尚未初始化，`BepInEx/LogOutput.log` 完全没有被创建；
- 最终在用户侧表现为：游戏无法加载 Mod，且整个游戏目录里“看不到任何运行日志”。

**解决方案**：在 `run_bepinex.sh` 检测到非终端启动环境（如 Steam 启动）时，自动将 `stdout` 和 `stderr` 重定向并追加记录到：
```text
游戏目录/BepInEx/run_bepinex.log
```
使启动脚本、`doorstop_shim` 注入垫片及引擎初期的所有诊断信息全程透明可查。

### 3. `DYLD_INSERT_LIBRARIES` 注入路径绝对化

原脚本使用 `export DYLD_INSERT_LIBRARIES="libdoorstop.dylib"` 配合 `DYLD_LIBRARY_PATH` 进行相对寻址。新系统在非预期工作目录下可能出现动态库寻址失败或被限制规则过滤。

**解决方案**：统一将注入路径改为绝对路径 `${doorstop_directory}${doorstop_name}`。
