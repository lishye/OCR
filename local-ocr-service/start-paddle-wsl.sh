#!/usr/bin/env bash
# 在 WSL Ubuntu 内安装并启动 PaddleOCR 服务（端口 8001）
# 用法：wsl -e bash ./start-paddle-wsl.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# 使用 Linux 主目录保存 venv，避免 /mnt/d 挂载目录上的删除/权限问题
VENV="$HOME/.venvs/ocr-paddle"

# 创建虚拟环境（如不存在）
if [ ! -d "$VENV" ]; then
    echo "[setup] 创建虚拟环境 $VENV"
    mkdir -p "$(dirname "$VENV")"
    python3 -m venv "$VENV"
fi

source "$VENV/bin/activate"

# 安装/升级依赖
echo "[setup] 安装依赖..."
pip install --upgrade pip -q
pip install -r "$SCRIPT_DIR/requirements-paddle.txt" -q

echo "[start] 启动 PaddleOCR 服务 on 0.0.0.0:8001"
cd "$SCRIPT_DIR"
uvicorn app_paddle:app --host 0.0.0.0 --port 8001
