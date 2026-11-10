#!/bin/sh
# 移除 install.sh 安裝的 IslandBar（設定檔 ~/.config/IslandBar 會保留）
PREFIX="${HOME}/.local"
pkill -x islandbar 2>/dev/null || true
rm -f "$PREFIX/bin/islandbar" "$PREFIX/share/applications/islandbar.desktop" \
      "$PREFIX/share/icons/hicolor/256x256/apps/islandbar.png" \
      "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/islandbar.desktop"
rm -rf "$PREFIX/lib/islandbar"
echo "IslandBar 已移除（設定保留在 ${XDG_CONFIG_HOME:-$HOME/.config}/IslandBar）"
