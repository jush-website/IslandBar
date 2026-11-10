IslandBar for Linux — 動態島系統監控
=====================================

安裝（目前使用者，不需要 sudo）：
    ./install.sh
移除：
    ./uninstall.sh

也可以不安裝，直接執行 ./islandbar

需求：有桌面的 Linux（X11，或 Wayland 下的 XWayland），x64 或 arm64。
中文字型：建議安裝 fonts-noto-cjk（Ubuntu/Debian：sudo apt install fonts-noto-cjk）。
主機板風扇與溫度：安裝 lm-sensors 後執行 sudo sensors-detect，載入建議的驅動。
NVIDIA GPU：需要 NVIDIA 驅動（附 nvidia-smi）；Jetson 會讀系統的 GPU 負載。

設定：~/.config/IslandBar/settings.json（右鍵選單就能改）
記錄：~/.config/IslandBar/log.txt
