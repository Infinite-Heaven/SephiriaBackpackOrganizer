#!/bin/sh
# SephiriaBackpackOrganizer —— macOS 安装脚本
#
#   ./install.sh [游戏目录] [--zip 完整包.zip]
#
# 不带参数时会自动找 Steam 里的游戏目录，并从 GitHub Releases 下载最新的完整包。
# 插件和 BepInEx 都直接用官方发行版里的原文件，macOS 这边只额外补一个注入库。
set -e

REPO="Infinite-Heaven/SephiriaBackpackOrganizer"
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd -P)

GAME_DIR=""
ZIP_PATH=""
while [ $# -gt 0 ]; do
    case "$1" in
        --zip) ZIP_PATH="$2"; shift 2 ;;
        -h|--help)
            echo "用法：./install.sh [游戏目录] [--zip 完整包.zip]"
            exit 0 ;;
        *) GAME_DIR="$1"; shift ;;
    esac
done

# ---------------------------------------------------------------- 找游戏目录
find_game_dir() {
    steam_root="$HOME/Library/Application Support/Steam"

    if [ -d "$steam_root/steamapps/common/Sephiria/Sephiria.app" ]; then
        echo "$steam_root/steamapps/common/Sephiria"
        return
    fi

    # 游戏可能装在别的 Steam 库（移动硬盘等），从 libraryfolders.vdf 里找。
    # while 跑在管道的子 shell 里，return 出不去，所以用 break + head -1 收口。
    vdf="$steam_root/steamapps/libraryfolders.vdf"
    if [ -f "$vdf" ]; then
        sed -n 's/.*"path"[^"]*"\(.*\)".*/\1/p' "$vdf" | while IFS= read -r lib; do
            if [ -d "$lib/steamapps/common/Sephiria/Sephiria.app" ]; then
                echo "$lib/steamapps/common/Sephiria"
                break
            fi
        done | head -1
    fi
}

if [ -z "$GAME_DIR" ]; then
    GAME_DIR=$(find_game_dir)
fi

if [ -z "$GAME_DIR" ] || [ ! -d "$GAME_DIR/Sephiria.app" ]; then
    echo "错误：没找到《赛菲莉娅》的安装目录。" 1>&2
    echo "在 Steam 里右键 Sephiria → 管理 → 浏览本地文件，把那个目录传进来：" 1>&2
    echo "    ./install.sh \"/路径/到/Sephiria\"" 1>&2
    exit 1
fi
echo "游戏目录：$GAME_DIR"

# ---------------------------------------------------------------- 环境检查
if ! command -v clang >/dev/null 2>&1; then
    echo "错误：需要 clang 来编译注入库。请先装 Xcode 命令行工具：" 1>&2
    echo "    xcode-select --install" 1>&2
    exit 1
fi

# 插件用 Harmony 打补丁，而 BepInEx 6 自带的 MonoMod 22.5 只会生成 x86/x64 的
# 跳板代码。所以 Apple 芯片上游戏必须跑在 Rosetta 下（run_bepinex.sh 会自动切）。
if [ "$(uname -m)" = "arm64" ]; then
    if ! /usr/bin/arch -x86_64 /usr/bin/true 2>/dev/null; then
        echo "正在安装 Rosetta 2（插件要求游戏以 x86_64 运行），可能需要输入密码……"
        softwareupdate --install-rosetta --agree-to-license
    fi
    echo "Rosetta 2：已就绪"
fi

# ---------------------------------------------------------------- 取完整包
TMPDIR_WORK=$(mktemp -d)
cleanup() { rm -rf "$TMPDIR_WORK"; }
trap cleanup EXIT INT TERM

if [ -z "$ZIP_PATH" ]; then
    echo "正在查询最新发行版……"
    ZIP_URL=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
        | grep -o '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
        | sed 's/.*"\(https[^"]*\)"$/\1/' \
        | grep -E 'SephiriaBackpackOrganizer-v[0-9.]+\.zip$' \
        | head -1)

    if [ -z "$ZIP_URL" ]; then
        echo "错误：没能从 Releases 找到完整包。" 1>&2
        echo "请手动下载后用 --zip 指定：./install.sh --zip ~/Downloads/xxx.zip" 1>&2
        exit 1
    fi

    echo "下载：$ZIP_URL"
    ZIP_PATH="$TMPDIR_WORK/package.zip"
    curl -fL --progress-bar -o "$ZIP_PATH" "$ZIP_URL"
fi

if [ ! -f "$ZIP_PATH" ]; then
    echo "错误：找不到完整包 $ZIP_PATH" 1>&2
    exit 1
fi

# 官方完整包是在 Windows 上打的，压缩包内部用反斜杠做路径分隔符；
# 两种写法都试一遍，兼容以后换打包方式的情况。
EXTRACT="$TMPDIR_WORK/extract"
mkdir -p "$EXTRACT"
unzip -q -o "$ZIP_PATH" 'BepInEx\\*' -d "$EXTRACT" 2>/dev/null || true
if [ ! -d "$EXTRACT/BepInEx/core" ]; then
    unzip -q -o "$ZIP_PATH" 'BepInEx/*' -d "$EXTRACT" 2>/dev/null || true
fi

if [ ! -f "$EXTRACT/BepInEx/plugins/SephiriaBackpackOrganizer.dll" ]; then
    echo "错误：压缩包里没有 BepInEx/plugins/SephiriaBackpackOrganizer.dll" 1>&2
    echo "请确认用的是完整包（不是 -PluginOnly 或 -bep5）。" 1>&2
    exit 1
fi

# ---------------------------------------------------------------- 编译注入库
# Windows 用 winhttp.dll + doorstop_config.ini 注入，macOS 走 DYLD_INSERT_LIBRARIES。
# 单文件无依赖，直接现编，省得在仓库里塞二进制。
echo "正在编译 libdoorstop.dylib……"
clang -arch arm64 -arch x86_64 \
      -dynamiclib -O2 -Wall -Wextra \
      -mmacosx-version-min=11.0 \
      -install_name @rpath/libdoorstop.dylib \
      -o "$TMPDIR_WORK/libdoorstop.dylib" \
      "$SCRIPT_DIR/doorstop_shim.c"
codesign --force -s - "$TMPDIR_WORK/libdoorstop.dylib"

# ---------------------------------------------------------------- 装进游戏
mkdir -p "$GAME_DIR/BepInEx/core" "$GAME_DIR/BepInEx/plugins" \
         "$GAME_DIR/BepInEx/patchers" "$GAME_DIR/BepInEx/config"

cp -R "$EXTRACT/BepInEx/core/." "$GAME_DIR/BepInEx/core/"
cp "$EXTRACT/BepInEx/plugins/SephiriaBackpackOrganizer.dll" "$GAME_DIR/BepInEx/plugins/"
cp "$TMPDIR_WORK/libdoorstop.dylib" "$GAME_DIR/"
cp "$SCRIPT_DIR/run_bepinex.sh" "$GAME_DIR/"
chmod +x "$GAME_DIR/run_bepinex.sh"

# 脱离 Steam 直接启动时，游戏靠这个文件认出自己的 AppID
if [ ! -f "$GAME_DIR/steam_appid.txt" ]; then
    printf '2436940\n' > "$GAME_DIR/steam_appid.txt"
fi

xattr -dr com.apple.quarantine \
    "$GAME_DIR/libdoorstop.dylib" "$GAME_DIR/BepInEx" "$GAME_DIR/run_bepinex.sh" 2>/dev/null || true

# hardened runtime 会屏蔽 DYLD 注入。当前版本是 ad-hoc 签名用不着处理，
# 这里留个检测，将来游戏改了签名方式也能自动补救。
if codesign -dv "$GAME_DIR/Sephiria.app" 2>&1 | grep -q 'flags=.*runtime'; then
    echo "检测到游戏启用了 hardened runtime，正在重新签名以允许注入……"
    codesign --force --deep -s - "$GAME_DIR/Sephiria.app"
fi

# ---------------------------------------------------------------- 完成
cat <<EOF

✅ 安装完成。

【启动】
  Steam 库 → 右键 Sephiria → 属性 → 通用 → 启动选项，粘贴下面这一行：

    "$GAME_DIR/run_bepinex.sh" %command%

  之后照常点「开始游戏」。

【使用】
  按 fn + F8 整理背包；中键点击神器逐次循环设置优先级 P1→P2→P3→P4→取消。

  ⚠️  macOS 上 F8 默认是「播放/暂停」媒体键，会被系统截走，所以要加 fn。
      想直接按 F8：系统设置 → 键盘 → 键盘快捷键 → 功能键 →
      打开「将 F1、F2 等键用作标准功能键」。
      也可以改键：编辑
        BepInEx/config/com.sephiria.backpack-organizer.cfg
      里的 [General] Hotkey。

日志：$GAME_DIR/BepInEx/LogOutput.log
EOF
