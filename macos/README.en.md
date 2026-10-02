# macOS Installation Guide

🌏 **Language / 语言 / 언어**: [English](README.en.md) · [中文](README.md) · [한국어](README.ko.md)

Install the Backpack Organizer plugin for the native macOS build of *Sephiria*.

Both the plugin itself and BepInEx are the official unmodified files from Releases — **not a single byte changed**.
Only the loader is different on macOS: Windows injects via `winhttp.dll` + `doorstop_config.ini`,
while macOS relies on `DYLD_INSERT_LIBRARIES` plus a dylib.

Tested on macOS 26.5.2 / Apple M2 Max and macOS 27.0 / Apple M1 Pro / Sephiria 1.0.33 (Unity 6000.3.21f1) + plugin v2.5.4.

Main README: [English](../docs/README.en.md) · [中文](../docs/README.md) · [한국어](../docs/README.ko.md)

## Installation

```sh
cd macos
./install.sh
```

The script automatically: locates the game directory in Steam (including games installed in other Steam libraries),
installs Rosetta 2 if needed, downloads the latest full package from Releases, compiles the injection library on the spot, and installs everything into the game.

If you already downloaded the full package manually, pass it directly to skip the download:

```sh
./install.sh --zip ~/Downloads/SephiriaBackpackOrganizer-v2.5.2.zip
```

The Xcode command line tools (`xcode-select --install`) are required to compile the injection library.

## Launch

In your Steam library → right-click Sephiria → Properties → General → Launch Options, paste this (replace the path with your own):

```
"/Users/YourUsername/Library/Application Support/Steam/steamapps/common/Sephiria/run_bepinex.sh" %command%
```

After that, just press "Play" as usual. The install script prints this full line at the end.

## Usage

Press **fn + F8** to sort your backpack; middle-click an artifact to set a manual priority (cycles P1 → P2 → P3 → P4, then one more click clears it; each item is independent).

> On macOS, F8 defaults to the "Play/Pause" media key, which the system intercepts before the game ever sees it, so fn is required.
> To press F8 directly: System Settings → Keyboard → Keyboard Shortcuts → Function Keys →
> turn on "Use F1, F2, etc. keys as standard function keys".
> You can also rebind it: edit `[General] Hotkey` in `BepInEx/config/com.sephiria.backpack-organizer.cfg`,
> then restart the game.

All other options are exactly the same as the Windows build, see the [main README](../docs/README.en.md).

If something goes wrong, check `GameDirectory/BepInEx/LogOutput.log` first; if BepInEx never started at all,
check the launcher log `GameDirectory/BepInEx/run_bepinex.log` (records script startup and injection)
or `Sephiria.app/Contents/MacOS/preloader_<timestamp>.log`.

## Uninstall

```sh
./uninstall.sh
```

## Files

| File | Purpose |
| --- | --- |
| `install.sh` / `uninstall.sh` | Install / uninstall |
| `doorstop_shim.c` | macOS injection library source, compiled at install time |
| `run_bepinex.sh` | Launcher script (Rosetta switching + DYLD handling) |

---

## Why a custom doorstop was needed

The official UnityDoorstop macOS build does not run on this game, and it hits three chained pitfalls
that no file swap can work around. Documented here so nobody has to investigate twice.

**1. UnityDoorstop 4.5.0 aborts on Unity 6**

Its plthook manually parses the Mach-O `LC_DYLD_CHAINED_FIXUPS`, and fails with `unknown imports format 0`
on binaries built with Unity 6 / macOS 26.

`doorstop_shim.c` never parses Mach-O at all. Instead it uses dyld's native `__interpose` to hijack
the Unity player's lookup of `dlsym("mono_jit_init_version")` — this happens during dyld's own binding phase
and is independent of the exact Mach-O format. It reads the same `DOORSTOP_*` environment variables and exports
the same managed-side variables, so `BepInEx.Unity.Mono.Preloader.dll` needs no changes.

**2. `System.Native` not found, crashing the game itself**

The first line of BepInEx's `DoorstopEntrypoint.Start()` is `DateTime.Now`. On Unix this path goes through
`TimeZoneInfo` → `File.Exists` → `Interop.Sys`, whose static constructor needs to P/Invoke into `System.Native`.

The problem is that Unity only installs the dllmap (`System.Native` → `libmono-native.dylib`) in `mono_config_parse`,
which runs **after** `mono_jit_init_version` returns. The entry point runs too early, so the lookup inevitably
fails — and **a failed static constructor is permanently cached by the CLR**. After that, every `File.Exists`
call made by the game itself rethrows `TypeInitializationException`, stuck on the intro animation without even loading a save.
This never happens on Windows, where `System.IO` goes straight through Win32.

The shim now registers these dllmaps itself with absolute paths before running the managed entry point.

**3. No domain configuration, `Trace` initialization explodes**

Unity never sets `AppDomain.CurrentDomain.SetupInformation.ConfigurationFile`,
so when BepInEx touches `System.Diagnostics.Trace` in `TraceLogSource.CreateSource()`,
`ConfigurationManager` throws
`ConfigurationErrorsException: The 'ExeConfigFilename' argument cannot be null.`.
The shim adds back `mono_domain_set_config`, following what the official doorstop does.

## Why it must run under Rosetta

The MonoMod 22.5.1.1 bundled with BepInEx 6.0.0-be.697 only generates x86/x64 trampoline code for its detour engine.
On native arm64, `DetourHelper.Runtime` is null, and the first Harmony patch blows up with
`IL Compile Error → NullReferenceException`, so no plugin loads at all.

The game is a universal binary, so `run_bepinex.sh` re-execs itself via `arch -x86_64` on Apple Silicon,
letting the game run under Rosetta 2. Once upstream BepInEx switches to an arm64-capable MonoMod (v25+),
this step can be removed.

(For debugging: set `DOORSTOP_NO_ROSETTA=1` to force native arm64, but Harmony-based plugins will break.)

## One more pitfall on machines with SIP disabled

Normally dyld ignores `DYLD_INSERT_LIBRARIES` for Apple's own arm64e system binaries.
But if the user has disabled SIP, dyld no longer ignores it — and Steam's overlay
`gameoverlayrenderer.dylib` only ships x86_64 and arm64 slices, with no arm64e slice. As a result, once launched
through Steam launch options, every `uname` / `sysctl` / `arch` forked by the script aborts with
`missing compatible architecture ... need 'arm64e'`, and the script never gets past its first line.

`run_bepinex.sh` therefore saves `DYLD_INSERT_LIBRARIES` and `DYLD_LIBRARY_PATH` into `DOORSTOP_SAVED_*`
and clears them **at the very top**, only restoring them right before the final exec of the game, with no child
processes in between. That way the Steam overlay still injects into the game, and the script is not harmed by its own variables.

(One `if [ -z "${VAR+x}" ]` check during saving must not be removed: the script re-execs itself through `arch`,
and without this check the second pass would overwrite what the first pass saved with empty values, silently dropping the Steam overlay.)

## macOS 27+ adaptation and failure analysis

On macOS 27, the mod once failed to run with no log output at all. Investigation showed a chain of issues caused by tightened sandboxing and execution rules:

### 1. `defaults read` cross-directory sandbox denial silently kills the script (root cause)

The original launcher script resolved the inner main binary name inside the `.app` with:

```sh
inner_executable_name=$(defaults read "${real_executable_name}/Contents/Info" CFBundleExecutable)
```

On macOS 27, the `cfprefsd` daemon behind `defaults` enforces stricter file sandbox isolation. When reading an `Info.plist` across directories in a Steam library, `cfprefsd` throws a permission error directly:

```text
sandbox_extension_issue_file failed ... 1 (Operation not permitted)
Error: Domain '.../Contents/Info' not found.
```

Since the script enables `set -e` at the top (exit immediately on any error), **`run_bepinex.sh` died with exit code 1 before ever executing the game binary**. The game never even entered the BepInEx injection flow.

**Fix**: use `plutil -extract CFBundleExecutable raw` and `/usr/libexec/PlistBuddy` instead — both parse the XML/binary plist in place and are not restricted by the `cfprefsd` sandbox — with multiple safe fallbacks kept.

### 2. The "log black hole" under a Steam launch environment

When the game is launched via Steam's "Launch Options", child processes have no visible terminal console. All error and status output from `run_bepinex.sh` and `doorstop_shim.c` previously went to `stderr`.

- Once the script failed early (e.g. the `defaults read` crash above), all error output was silently discarded by the system;
- At that point the BepInEx engine had not initialized yet, so `BepInEx/LogOutput.log` was never created;
- From the user's perspective: the mod did not load, and there was "no log anywhere" in the game directory.

**Fix**: when `run_bepinex.sh` detects a non-terminal launch environment (such as Steam), it automatically redirects and appends `stdout` and `stderr` to:

```text
GameDirectory/BepInEx/run_bepinex.log
```

This keeps the launcher script, the `doorstop_shim` injection shim, and all early engine diagnostics fully traceable.

### 3. Absolute injection path for `DYLD_INSERT_LIBRARIES`

The original script used `export DYLD_INSERT_LIBRARIES="libdoorstop.dylib"` with `DYLD_LIBRARY_PATH` for relative lookup. On newer systems this can fail to resolve or be filtered out by restriction rules when the working directory is unexpected.

**Fix**: always use the absolute injection path `${doorstop_directory}${doorstop_name}`.
