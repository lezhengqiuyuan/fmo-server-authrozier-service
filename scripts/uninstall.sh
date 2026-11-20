#!/bin/bash
# FMO SAS 卸载脚本（Linux / macOS）
# 用法: curl -fsSL https://bg5esn.com/share/fmo/sas-installer/uninstall.sh | sudo bash
set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { printf "${CYAN}%s${NC}\n" "$*"; }
ok()    { printf "${GREEN}%s${NC}\n" "$*"; }
warn()  { printf "${YELLOW}%s${NC}\n" "$*"; }
err()   { printf "${RED}%s${NC}\n" "$*"; }

echo ""
info "═══ FMO SAS Uninstaller ═══"
echo ""

if [ "$(id -u)" -ne 0 ]; then
    err "请以 root 权限运行此脚本"
    err "用法: curl -fsSL https://bg5esn.com/share/fmo/sas-installer/uninstall.sh | sudo bash"
    exit 1
fi

OS=$(uname -s)

if [ "$OS" = "Linux" ]; then
    INSTALL_DIR="/opt/fmo-sas"
    CONFIG_DIR="/etc/fmo-sas"
elif [ "$OS" = "Darwin" ]; then
    INSTALL_DIR="/usr/local/opt/fmo-sas"
    CONFIG_DIR="/usr/local/etc/fmo-sas"
else
    err "不支持的平台: $OS"
    exit 1
fi

# 数据目录统一为运行用户（root）的 ~/.sas
ROOT_HOME=$(getent passwd root 2>/dev/null | cut -d: -f6)
if [ -z "$ROOT_HOME" ]; then
    ROOT_HOME=~root
fi
DATA_DIR="$ROOT_HOME/.sas"

if [ "$OS" = "Linux" ]; then
    info "[1/4] 停止服务..."
    systemctl stop fmo-sas 2>/dev/null || true
    systemctl disable fmo-sas 2>/dev/null || true
    ok "      完成"

    info "[2/4] 移除服务文件..."
    rm -f /etc/systemd/system/fmo-sas.service
    systemctl daemon-reload 2>/dev/null || true
    ok "      完成"

    info "[3/4] 删除程序和配置..."
    rm -rf "$INSTALL_DIR"
    rm -rf "$CONFIG_DIR"
    ok "      已删除程序: $INSTALL_DIR"
    ok "      已删除配置: $CONFIG_DIR"

    info "[4/4] 检查数据目录..."
    ok "      见下方未删除内容"

elif [ "$OS" = "Darwin" ]; then
    info "[1/4] 停止服务..."
    launchctl unload /Library/LaunchDaemons/com.fmo.sas.plist 2>/dev/null || true
    ok "      完成"

    info "[2/4] 移除服务文件..."
    rm -f /Library/LaunchDaemons/com.fmo.sas.plist
    ok "      完成"

    info "[3/4] 删除程序和配置..."
    rm -rf "$INSTALL_DIR"
    rm -rf "$CONFIG_DIR"
    rm -f /var/log/fmo-sas.log /var/log/fmo-sas.err
    ok "      完成"

    info "[4/4] 检查数据目录..."
    ok "      见下方未删除内容"
fi

echo ""
ok "═══ FMO SAS 已卸载 ═══"
echo ""
info "以下内容未删除："

if [ -d "$DATA_DIR" ]; then
    warn "  数据目录: $DATA_DIR"
    warn "    含 roots/roles/crl/archive"
    warn "  彻底删除: rm -rf $DATA_DIR"
else
    ok "  数据目录: 无"
fi

if [ "$OS" = "Linux" ] && [ -d /etc/systemd/system/fmo-sas.service.d ]; then
    warn "  systemd 覆盖配置: /etc/systemd/system/fmo-sas.service.d"
    warn "  彻底删除: rm -rf /etc/systemd/system/fmo-sas.service.d"
fi

if [ "$OS" = "Linux" ]; then
    warn "  系统日志: journalctl -u fmo-sas"
    warn "    日志由 journald 管理，通常无需手动删除"
fi

echo ""
