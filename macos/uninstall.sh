#!/bin/sh
# 卸载 SephiriaBackpackOrganizer（macOS）
#   ./uninstall.sh [游戏目录]
set -e

find_game_dir() {
    if [ -n "$1" ]; then echo "$1"; return; fi
    steam_root="$HOME/Library/Application Support/Steam"
    if [ -d "$steam_root/steamapps/common/Sephiria/Sephiria.app" ]; then
        echo "$steam_root/steamapps/common/Sephiria"
        return
    fi
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

GAME_DIR=$(find_game_dir "$1")

if [ -z "$GAME_DIR" ] || [ ! -d "$GAME_DIR/Sephiria.app" ]; then
    echo "错误：没找到《赛菲莉娅》的安装目录。用法：./uninstall.sh [游戏目录]" 1>&2
    exit 1
fi

echo "游戏目录：$GAME_DIR"
echo "只删插件（保留 BepInEx 框架）输入 1，连框架一起删干净输入 2："
read -r choice
case "$choice" in
    1)
        rm -f "$GAME_DIR/BepInEx/plugins/SephiriaBackpackOrganizer.dll"
        echo "已删除插件。"
        ;;
    2)
        rm -rf "$GAME_DIR/BepInEx"
        rm -f "$GAME_DIR/libdoorstop.dylib" "$GAME_DIR/run_bepinex.sh"
        rm -f "$GAME_DIR/Sephiria.app/Contents/MacOS/preloader_"*.log 2>/dev/null || true
        echo "已删除插件和 BepInEx 框架。"
        echo "记得去 Steam 里把 Sephiria 的启动选项清空。"
        ;;
    *)
        echo "已取消。"
        ;;
esac
