#!/bin/sh
# 安裝到目前使用者（不需要 sudo）：~/.local/lib/islandbar
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
PREFIX="${HOME}/.local"
APP="$PREFIX/lib/islandbar/islandbar"

pkill -x islandbar 2>/dev/null || true
sleep 1
install -Dm755 "$DIR/islandbar" "$APP"
mkdir -p "$PREFIX/bin"
ln -sf "$APP" "$PREFIX/bin/islandbar"
install -Dm644 "$DIR/islandbar.png" "$PREFIX/share/icons/hicolor/256x256/apps/islandbar.png"
mkdir -p "$PREFIX/share/applications"
sed "s#^Exec=.*#Exec=\"$APP\"#" "$DIR/islandbar.desktop" > "$PREFIX/share/applications/islandbar.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$PREFIX/share/applications" >/dev/null 2>&1 || true

echo "IslandBar 已安裝：$APP"
echo "第一次執行會自動設定登入時啟動（右鍵選單可以關閉）。"
nohup "$APP" >/dev/null 2>&1 &
