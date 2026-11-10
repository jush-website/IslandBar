#!/bin/bash
# usage: package.sh <rid: linux-x64|linux-arm64> <version> <publish dir> <out dir>
# 產生 IslandBar-<ver>-<rid>.tar.gz 與 islandbar_<ver>_<arch>.deb
set -euo pipefail
RID="$1"; VER="$2"; PUB="$3"; OUT="$4"
HERE="$(cd "$(dirname "$0")" && pwd)"
ICON="$HERE/../../src/IslandBar.Linux/Assets/islandbar.png"
case "$RID" in
  linux-x64) ARCH=amd64 ;;
  linux-arm64) ARCH=arm64 ;;
  *) echo "unknown rid $RID"; exit 1 ;;
esac
mkdir -p "$OUT"
WORK="$(mktemp -d)"

# ---- tar.gz ----
NAME="IslandBar-$VER-$RID"
T="$WORK/$NAME"
mkdir -p "$T"
install -m755 "$PUB/islandbar" "$T/islandbar"
install -m644 "$ICON" "$T/islandbar.png"
install -m644 "$HERE/islandbar.desktop" "$T/islandbar.desktop"
install -m755 "$HERE/install.sh" "$T/install.sh"
install -m755 "$HERE/uninstall.sh" "$T/uninstall.sh"
install -m644 "$HERE/README.txt" "$T/README.txt"
tar -C "$WORK" -czf "$OUT/$NAME.tar.gz" "$NAME"

# ---- .deb ----
D="$WORK/deb"
mkdir -p "$D/DEBIAN" "$D/usr/lib/islandbar" "$D/usr/bin" "$D/usr/share/applications" "$D/usr/share/icons/hicolor/256x256/apps"
install -m755 "$PUB/islandbar" "$D/usr/lib/islandbar/islandbar"
ln -s ../lib/islandbar/islandbar "$D/usr/bin/islandbar"
install -m644 "$HERE/islandbar.desktop" "$D/usr/share/applications/islandbar.desktop"
install -m644 "$ICON" "$D/usr/share/icons/hicolor/256x256/apps/islandbar.png"
SIZE=$(du -sk "$D/usr" | cut -f1)
cat > "$D/DEBIAN/control" <<CTL
Package: islandbar
Version: $VER
Architecture: $ARCH
Maintainer: jush-website <noreply@github.com>
Installed-Size: $SIZE
Depends: libx11-6, libice6, libsm6, libfontconfig1
Recommends: lm-sensors, fonts-noto-cjk
Section: utils
Priority: optional
Homepage: https://github.com/jush-website/IslandBar
Description: Dynamic Island system monitor for Linux desktops
 A small capsule at the top of the screen showing CPU, RAM, GPU and
 temperatures; hover to see disks, network, every temperature sensor
 and fan, NVIDIA GPU details and NVIDIA PAIR cluster status.
CTL
dpkg-deb --build --root-owner-group "$D" "$OUT/islandbar_${VER}_${ARCH}.deb" >/dev/null
rm -rf "$WORK"
ls -la "$OUT"
