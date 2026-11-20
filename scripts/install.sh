#!/bin/bash
# FMO SAS 安装 / 升级脚本（Linux / macOS）
#
# 安装（交互）:
#   curl -fsSL https://bg5esn.com/share/fmo/sas-installer/install.sh -o /tmp/install.sh && sudo bash /tmp/install.sh
#
# 升级（覆盖程序，保留配置）:
#   curl -fsSL https://bg5esn.com/share/fmo/sas-installer/install.sh -o /tmp/install.sh && sudo bash /tmp/install.sh --upgrade
#
# 已安装时直接运行脚本会自动进入升级模式。
#
# 参数:
#   --upgrade    强制升级（要求已安装）
#   --install    强制安装 / 重装（进入配置向导）
#   -y, --yes    自动确认，不提示
#   --force      目标版本不高于当前版本时也覆盖
#   -h, --help   显示帮助

set -e

SAS_JSON_URL="https://bg5esn.com/share/fmo/sas.json"
SAS_INSTALLER_URL="https://bg5esn.com/share/fmo/sas-installer/install.sh"
SCRIPT_VERSION="1.1.0"

SYSTEMD_UNIT="/etc/systemd/system/fmo-sas.service"
LAUNCHD_PLIST="/Library/LaunchDaemons/com.fmo.sas.plist"

MODE="auto"
AUTO_YES=false
FORCE=false

TMPDIR_INSTALL=""
BACKUP_DIR=""
cleanup() {
    if [ -n "$TMPDIR_INSTALL" ]; then
        rm -rf "$TMPDIR_INSTALL"
    fi
    return 0
}
trap cleanup EXIT

# ── Colors ──
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { printf "${CYAN}%s${NC}\n" "$*"; }
ok()    { printf "${GREEN}%s${NC}\n" "$*"; }
warn()  { printf "${YELLOW}%s${NC}\n" "$*"; }
err()   { printf "${RED}%s${NC}\n" "$*"; }

prompt_input() {
    local prompt="$1"
    local var
    printf "%s" "$prompt" > /dev/tty
    read -r var < /dev/tty
    echo "$var"
}

confirm() {
    if [ "$AUTO_YES" = true ]; then
        return 0
    fi
    if [ ! -r /dev/tty ]; then
        err "当前环境无法交互确认，请加 --yes"
        return 1
    fi
    local reply
    printf "  确定继续吗？(y/N) " > /dev/tty
    read -r reply < /dev/tty
    case "$reply" in
        y|Y) return 0 ;;
        *)   return 1 ;;
    esac
}

show_help() {
    echo ""
    echo "FMO SAS 安装 / 升级脚本 v$SCRIPT_VERSION"
    echo ""
    echo "用法: $0 [选项]"
    echo ""
    echo "  (无参数)     已安装时自动升级，未安装时进入安装向导"
    echo "  --upgrade    强制升级（要求已安装）"
    echo "  --install    强制安装 / 重装（进入配置向导）"
    echo "  -y, --yes    自动确认，不提示"
    echo "  --force      目标版本不高于当前版本时也覆盖"
    echo "  -h, --help   显示此帮助"
    echo ""
    echo "典型用法:"
    echo "  # 安装"
    echo "  curl -fsSL $SAS_INSTALLER_URL -o /tmp/install.sh && sudo bash /tmp/install.sh"
    echo ""
    echo "  # 升级（保留配置文件）"
    echo "  curl -fsSL $SAS_INSTALLER_URL -o /tmp/install.sh && sudo bash /tmp/install.sh --upgrade"
    echo ""
}

parse_args() {
    while [ $# -gt 0 ]; do
        case "$1" in
            --upgrade)  MODE="upgrade"; shift ;;
            --install)  MODE="install"; shift ;;
            -y|--yes)   AUTO_YES=true;  shift ;;
            --force)    FORCE=true;     shift ;;
            -h|--help)  show_help; exit 0 ;;
            *)
                err "未知参数: $1"
                show_help
                exit 1
                ;;
        esac
    done
}

check_root() {
    if [ "$(id -u)" -ne 0 ]; then
        err "请以 root 权限运行此脚本"
        err "用法: curl -fsSL $SAS_INSTALLER_URL | sudo bash"
        exit 1
    fi
}

check_deps() {
    MISSING=""
    for cmd in curl tar mktemp; do
        if ! command -v "$cmd" >/dev/null 2>&1; then
            MISSING="$MISSING $cmd"
        fi
    done

    if [ "$(uname -s)" = "Linux" ] && ! command -v systemctl >/dev/null 2>&1; then
        MISSING="$MISSING systemctl(systemd)"
    fi

    if [ -n "$MISSING" ]; then
        err "当前组件不支持您的环境，您缺乏:${MISSING}"
        err "您可以尝试安装组件后重试，或手动部署，参考: https://bg5esn.com/docs/fmo-server-construction/"
        exit 1
    fi
}

detect_platform() {
    OS=$(uname -s)
    ARCH=$(uname -m)

    case "$OS-$ARCH" in
        Linux-x86_64)   RID="linux-x64" ;;
        Linux-aarch64)  RID="linux-arm64" ;;
        Darwin-x86_64)  RID="osx-x64" ;;
        Darwin-arm64)   RID="osx-arm64" ;;
        *)
            err "不支持的平台: $OS $ARCH"
            err "支持的平台: linux-x64, linux-arm64, osx-x64, osx-arm64"
            exit 1
            ;;
    esac
}

setup_paths() {
    if [ "$OS" = "Linux" ]; then
        INSTALL_DIR="/opt/fmo-sas"
        CONFIG_DIR="/etc/fmo-sas"
    else
        INSTALL_DIR="/usr/local/opt/fmo-sas"
        CONFIG_DIR="/usr/local/etc/fmo-sas"
    fi
    CONFIG_FILE="$CONFIG_DIR/config.json"

    # 数据目录统一为运行用户（root）的 ~/.sas：
    # Linux -> /root/.sas，macOS -> /var/root/.sas
    ROOT_HOME=$(getent passwd root 2>/dev/null | cut -d: -f6)
    if [ -z "$ROOT_HOME" ]; then
        ROOT_HOME=~root
    fi
    DATA_DIR="$ROOT_HOME/.sas"
}

# 官方部署以 $CONFIG_FILE 为准；$DATA_DIR/config.json 通常是遗留文件或手动运行生成的，
# 两者同时存在时提示运维，避免误以为数据目录里的配置正在生效。
warn_legacy_config() {
    if [ -f "$DATA_DIR/config.json" ] && [ -f "$CONFIG_FILE" ]; then
        warn "检测到遗留配置: $DATA_DIR/config.json"
        warn "当前服务使用的是: $CONFIG_FILE"
        warn "遗留文件不会生效；如需清理，请先备份后手动删除"
        echo ""
    fi
}

# 返回 0 表示 $1 >= $2
version_ge() {
    awk -v a="$1" -v b="$2" 'BEGIN {
        na = split(a, x, ".")
        nb = split(b, y, ".")
        n = (na > nb ? na : nb)
        for (i = 1; i <= n; i++) {
            xi = (i <= na ? x[i] + 0 : 0)
            yi = (i <= nb ? y[i] + 0 : 0)
            if (xi > yi) exit 0
            if (xi < yi) exit 1
        }
        exit 0
    }'
}

fetch_manifest() {
    SAS_META=$(curl -fsSL --proto '=https' --proto-redir '=https' "$SAS_JSON_URL") || { err "无法获取 SAS 版本信息"; return 1; }
    VERSION=$(echo "$SAS_META" | sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' | head -1)
    DOWNLOAD_URL=$(echo "$SAS_META" | sed -n "s/.*\"$RID\": *\"\([^\"]*\)\".*/\1/p" | head -1)
    DOWNLOAD_SHA256=$(echo "$SAS_META" | sed -n "s/.*\"${RID}_sha256\": *\"\([^\"]*\)\".*/\1/p" | head -1)

    if [ -z "$VERSION" ] || [ -z "$DOWNLOAD_URL" ]; then
        err "无法解析版本信息，请检查网络连接"
        return 1
    fi
}

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

prepare_package() {
    TMPDIR_INSTALL=$(mktemp -d)
    local tar_file="$TMPDIR_INSTALL/sas.tar.gz"
    EXTRACT_DIR="$TMPDIR_INSTALL/extract"

    if [ -z "$DOWNLOAD_SHA256" ]; then
        err "升级清单缺少 ${RID}_sha256，拒绝下载未校验的安装包"
        return 1
    fi

    curl -fL --proto '=https' --proto-redir '=https' --progress-bar \
        "$DOWNLOAD_URL" -o "$tar_file" || return 1

    local actual_sha256
    actual_sha256=$(sha256_of "$tar_file")
    if [ "$actual_sha256" != "$DOWNLOAD_SHA256" ]; then
        err "安装包校验失败: expected=$DOWNLOAD_SHA256 actual=$actual_sha256"
        return 1
    fi

    mkdir -p "$EXTRACT_DIR" || return 1
    tar xzf "$tar_file" -C "$EXTRACT_DIR" --no-same-owner || return 1

    if [ ! -f "$EXTRACT_DIR/sas" ]; then
        err "下载文件解压异常，未找到 sas 主程序"
        err "您可以尝试安装组件后重试，或手动部署，参考: https://bg5esn.com/docs/fmo-server-construction/"
        return 1
    fi
    return 0
}

install_payload() {
    mkdir -p "$INSTALL_DIR" || return 1
    cp "$EXTRACT_DIR/sas" "$INSTALL_DIR/sas.new" || return 1
    chmod +x "$INSTALL_DIR/sas.new" || return 1
    mv -f "$INSTALL_DIR/sas.new" "$INSTALL_DIR/sas" || return 1

    if [ -d "$EXTRACT_DIR/builtin" ]; then
        rm -rf "$INSTALL_DIR/builtin" || return 1
        cp -r "$EXTRACT_DIR/builtin" "$INSTALL_DIR/" || return 1
    fi

    rm -rf "$TMPDIR_INSTALL" || true
    TMPDIR_INSTALL=""
    return 0
}

service_stop() {
    if [ "$OS" = "Linux" ]; then
        if systemctl is-active --quiet fmo-sas 2>/dev/null; then
            systemctl stop fmo-sas
            warn "      已停止 fmo-sas 服务"
        fi
    elif [ "$OS" = "Darwin" ]; then
        if launchctl list 2>/dev/null | grep -q "com.fmo.sas"; then
            launchctl unload "$LAUNCHD_PLIST" 2>/dev/null || true
            warn "      已停止 fmo-sas 服务"
        fi
    fi
    pkill -f "$INSTALL_DIR/sas" 2>/dev/null || true
    sleep 1
}

service_start() {
    if [ "$OS" = "Linux" ]; then
        systemctl start fmo-sas
    elif [ "$OS" = "Darwin" ]; then
        launchctl load "$LAUNCHD_PLIST"
    fi
}

service_active() {
    if [ "$OS" = "Linux" ]; then
        systemctl is-active --quiet fmo-sas 2>/dev/null
    else
        launchctl list 2>/dev/null | grep -q "com.fmo.sas"
    fi
}

health_check() {
    local i=1
    while [ "$i" -le 30 ]; do
        if service_active; then
            # 进程刚启动时可能立刻崩溃，短暂等待后复查一次
            sleep 2
            if service_active; then
                return 0
            fi
        fi
        printf "${YELLOW}      等待服务启动... %d/30${NC}\r" "$i"
        sleep 2
        i=$((i + 1))
    done
    echo ""
    return 1
}

write_systemd_unit() {
    cat > "$SYSTEMD_UNIT" << EOF
[Unit]
Description=FMO Server Authorizer Service
After=network.target

[Service]
Type=simple
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/sas --config $CONFIG_FILE
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
}

write_launchd_plist() {
    cat > "$LAUNCHD_PLIST" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.fmo.sas</string>
    <key>ProgramArguments</key>
    <array>
        <string>$INSTALL_DIR/sas</string>
        <string>--config</string>
        <string>$CONFIG_FILE</string>
    </array>
    <key>WorkingDirectory</key>
    <string>$INSTALL_DIR</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/var/log/fmo-sas.log</string>
    <key>StandardErrorPath</key>
    <string>/var/log/fmo-sas.err</string>
</dict>
</plist>
EOF
}

register_service() {
    if [ "$OS" = "Linux" ]; then
        write_systemd_unit
        systemctl daemon-reload
        systemctl enable fmo-sas > /dev/null 2>&1
    else
        write_launchd_plist
    fi
}

# 升级：服务文件已存在时不覆盖，避免覆盖运维自定义的 unit
ensure_service_unit() {
    if [ "$OS" = "Linux" ]; then
        if [ ! -f "$SYSTEMD_UNIT" ]; then
            warn "      systemd 服务文件缺失，正在重建"
            write_systemd_unit || return 1
            systemctl enable fmo-sas > /dev/null 2>&1 || return 1
        fi
        systemctl daemon-reload || return 1
    else
        if [ ! -f "$LAUNCHD_PLIST" ]; then
            warn "      launchd 服务文件缺失，正在重建"
            write_launchd_plist || return 1
        fi
    fi
    return 0
}

backup_current() {
    BACKUP_DIR="$INSTALL_DIR/.backup-$(date +%Y%m%d%H%M%S)"
    mkdir -p "$BACKUP_DIR" || return 1
    cp -a "$INSTALL_DIR/sas" "$BACKUP_DIR/sas" || return 1
    if [ -d "$INSTALL_DIR/builtin" ]; then
        cp -a "$INSTALL_DIR/builtin" "$BACKUP_DIR/builtin" || return 1
    fi
    ok "      已备份当前版本到 $BACKUP_DIR"
    prune_backups
    return 0
}

prune_backups() {
    ls -dt "$INSTALL_DIR"/.backup-* 2>/dev/null | tail -n +2 | while IFS= read -r dir; do
        rm -rf "$dir"
    done
}

rollback() {
    warn "      升级失败，正在回滚..."
    if [ -f "$BACKUP_DIR/sas" ]; then
        cp "$BACKUP_DIR/sas" "$INSTALL_DIR/sas.rollback"
        chmod +x "$INSTALL_DIR/sas.rollback"
        mv -f "$INSTALL_DIR/sas.rollback" "$INSTALL_DIR/sas"
    fi
    if [ -d "$BACKUP_DIR/builtin" ]; then
        rm -rf "$INSTALL_DIR/builtin"
        cp -a "$BACKUP_DIR/builtin" "$INSTALL_DIR/builtin"
    fi
    service_start || true
    err "      已回滚到升级前的版本"
}

show_logs() {
    if [ "$OS" = "Linux" ]; then
        echo ""
        echo "  ── 最近日志 ──"
        journalctl -u fmo-sas -n 20 --no-pager 2>/dev/null || true
        echo "  ──────────────"
        echo ""
        echo "  完整日志: journalctl -u fmo-sas -f"
    else
        echo ""
        echo "  ── 最近日志 ──"
        tail -n 20 /var/log/fmo-sas.err 2>/dev/null || true
        tail -n 20 /var/log/fmo-sas.log 2>/dev/null || true
        echo "  ──────────────"
        echo ""
        echo "  完整日志: tail -f /var/log/fmo-sas.log"
    fi
}

upgrade() {
    info "═══ FMO SAS 升级 ═══"
    echo ""

    if [ ! -x "$INSTALL_DIR/sas" ]; then
        err "未检测到 SAS 安装: $INSTALL_DIR/sas"
        err "如需全新安装，请去掉 --upgrade 重新运行: $0"
        exit 1
    fi
    if [ ! -f "$CONFIG_FILE" ]; then
        err "未检测到配置文件: $CONFIG_FILE"
        err "请先完成安装和配置，或运行: $INSTALL_DIR/sas --config $CONFIG_FILE"
        exit 1
    fi

    info "[1/5] 检测平台... $OS $ARCH -> $RID"

    info "[2/5] 获取最新版本..."
    fetch_manifest
    info "      目标版本: v$VERSION"

    local current=""
    current=$("$INSTALL_DIR/sas" --version 2>/dev/null | head -1 | tr -d '\r' || true)
    current=${current#v}
    if [ -n "$current" ]; then
        info "      当前版本: v$current"
        if version_ge "$current" "$VERSION" && [ "$FORCE" = false ]; then
            ok "已是最新版本 v$current（如需强制覆盖请加 --force）"
            exit 0
        fi
    else
        warn "      无法读取当前版本，继续升级"
    fi

    echo ""
    info "      安装目录: $INSTALL_DIR"
    info "      配置文件: $CONFIG_FILE（保持不变）"
    echo ""
    if ! confirm; then
        warn "操作已取消"
        exit 0
    fi
    echo ""

    info "[3/5] 下载并准备工作包..."
    prepare_package
    ok "      下载完成"

    info "[4/5] 覆盖安装程序..."
    if ! backup_current; then
        err "      备份失败，已中止升级（服务未停止）"
        exit 1
    fi
    service_stop
    if ! install_payload; then
        rollback
        show_logs
        exit 1
    fi
    ok "      程序已更新到 v$VERSION"

    info "[5/5] 重启并检查服务..."
    if ! ensure_service_unit; then
        rollback
        show_logs
        exit 1
    fi
    service_start || true
    if health_check; then
        ok "═══ SAS 已升级到 v$VERSION 并正常运行 ═══"
        echo ""
        echo "  配置文件: $CONFIG_FILE"
        echo "  安装目录: $INSTALL_DIR/"
        echo "  备份目录: $BACKUP_DIR"
        show_logs
    else
        rollback
        show_logs
        echo "  配置文件: $CONFIG_FILE"
        exit 1
    fi
}

install_flow() {
    info "═══ FMO SAS 安装 ═══"
    echo ""

    info "[1/6] 检测平台... $OS $ARCH -> $RID"

    info "[2/6] 获取最新版本..."
    fetch_manifest
    info "      版本: v$VERSION | 下载: $DOWNLOAD_URL"

    info "[3/6] 下载并准备安装包..."
    prepare_package

    info "[4/6] 安装到 $INSTALL_DIR ..."
    service_stop
    mkdir -p "$INSTALL_DIR" "$CONFIG_DIR" "$DATA_DIR/roots" "$DATA_DIR/archive"
    install_payload
    ok "      完成"

    info "[5/6] 配置 SAS..."
    configure_sas

    info "[6/6] 注册系统服务..."
    register_service

    if [ -f "$CONFIG_FILE" ]; then
        service_start || true
        info "      等待服务启动..."
        if health_check; then
            ok "═══ SAS v$VERSION 安装完成并已启动 ═══"
        else
            warn "服务启动失败，请查看日志"
            show_logs
        fi
        print_summary
    else
        echo ""
        ok "═══ SAS v$VERSION 已安装（未启动，需先配置）═══"
        echo ""
        echo "  配置:     $INSTALL_DIR/sas --config $CONFIG_FILE"
        echo "            (运行后进入交互向导，按提示输入参数即可)"
        echo ""
        print_status
    fi
}

configure_sas() {
    SERVER_UID=""
    SERVER_CALL=""
    MQTT_HOST=""
    MQTT_PORT=""
    CERT_FP=""
    HTTP_PORT="8080"
    SKIP_CONFIG=false

    echo ""
    echo "      请在浏览器中打开 FMO 后台获取 SAS 启动命令:"
    echo "      http://fmo.local/config.html -> [SAS 搭建快捷方式] -> [生成命令] -> 复制"
    echo ""
    echo "      [1] 粘贴 FMO 生成的命令 (推荐)"
    echo "      [2] 手动输入各参数"
    echo "      [3] 跳过配置 (稍后运行 $INSTALL_DIR/sas 进入交互向导)"
    CHOICE=$(prompt_input "      请选择 [1/2/3]: ")

    case "$CHOICE" in
        1)
            CMD_LINE=$(prompt_input "      请粘贴命令: ")
            SERVER_UID=$(echo "$CMD_LINE" | sed -n 's/.*--server-uid \([^ ]*\).*/\1/p')
            SERVER_CALL=$(echo "$CMD_LINE" | sed -n 's/.*--server-callsign \([^ ]*\).*/\1/p')
            MQTT_HOST=$(echo "$CMD_LINE" | sed -n 's/.*--mqtt-host \([^ ]*\).*/\1/p')
            MQTT_PORT=$(echo "$CMD_LINE" | sed -n 's/.*--mqtt-port \([^ ]*\).*/\1/p')
            CERT_FP=$(echo "$CMD_LINE" | sed -n 's/.*--cert-fingerprint \([^ ]*\).*/\1/p')
            HTTP_PORT=$(echo "$CMD_LINE" | sed -n 's/.*--http-port \([^ ]*\).*/\1/p')
            [ -z "$MQTT_PORT" ] && MQTT_PORT="1883"
            [ -z "$HTTP_PORT" ] && HTTP_PORT="8080"
            ;;
        2)
            SERVER_UID=$(prompt_input "      server-uid: ")
            SERVER_CALL=$(prompt_input "      server-callsign: ")
            MQTT_HOST=$(prompt_input "      mqtt-host: ")
            MQTT_PORT=$(prompt_input "      mqtt-port [1883]: ")
            [ -z "$MQTT_PORT" ] && MQTT_PORT="1883"
            CERT_FP=$(prompt_input "      cert-fingerprint: ")
            HTTP_PORT=$(prompt_input "      http-port [8080]: ")
            [ -z "$HTTP_PORT" ] && HTTP_PORT="8080"
            ;;
        *)
            SKIP_CONFIG=true
            warn "      跳过配置"
            ;;
    esac

    if [ "$SKIP_CONFIG" = "false" ] && [ -n "$SERVER_UID" ]; then
        info "      生成配置文件..."
        "$INSTALL_DIR/sas" \
            --config "$CONFIG_FILE" \
            --server-uid "$SERVER_UID" \
            --server-callsign "$SERVER_CALL" \
            --mqtt-host "$MQTT_HOST" \
            --mqtt-port "$MQTT_PORT" \
            --cert-fingerprint "$CERT_FP" \
            --http-port "$HTTP_PORT" \
            --roots-dir "$DATA_DIR/roots" \
            --archive-dir "$DATA_DIR/archive" > /dev/null 2>&1 &
        SAS_PID=$!
        sleep 2
        kill "$SAS_PID" 2>/dev/null || true
        wait "$SAS_PID" 2>/dev/null || true

        if [ -f "$CONFIG_FILE" ]; then
            ok "      配置文件已生成: $CONFIG_FILE"
            info "      数据目录: $DATA_DIR"
        else
            warn "      配置文件生成失败，请稍后手动运行: $INSTALL_DIR/sas --config $CONFIG_FILE"
            SKIP_CONFIG=true
        fi
    fi
}

print_status() {
    if [ "$OS" = "Linux" ]; then
        echo "  状态:     systemctl status fmo-sas"
        echo "  启动:     systemctl start fmo-sas"
        echo "  停止:     systemctl stop fmo-sas"
        echo "  日志:     journalctl -u fmo-sas -f"
    else
        echo "  状态:     sudo launchctl list | grep com.fmo.sas"
        echo "  启动:     sudo launchctl load $LAUNCHD_PLIST"
        echo "  停止:     sudo launchctl unload $LAUNCHD_PLIST"
        echo "  日志:     tail -f /var/log/fmo-sas.log"
    fi
    echo "  升级:     curl -fsSL $SAS_INSTALLER_URL -o /tmp/install.sh && sudo bash /tmp/install.sh --upgrade"
    echo "  卸载:     curl -fsSL https://bg5esn.com/share/fmo/sas-installer/uninstall.sh | sudo bash"
    echo ""
    echo "  配置文件: $CONFIG_FILE"
    echo "  安装目录: $INSTALL_DIR/"
    echo "  数据目录: $DATA_DIR/"
}

print_summary() {
    echo ""
    if [ "$OS" = "Linux" ]; then
        echo "  日志:     journalctl -u fmo-sas -f"
    else
        echo "  日志:     tail -f /var/log/fmo-sas.log"
    fi
    echo ""
    print_status
}

main() {
    parse_args "$@"
    check_root
    check_deps
    detect_platform
    setup_paths
    warn_legacy_config

    case "$MODE" in
        upgrade)
            upgrade
            ;;
        install)
            install_flow
            ;;
        auto)
            if [ -x "$INSTALL_DIR/sas" ] && [ -f "$CONFIG_FILE" ]; then
                upgrade
            else
                install_flow
            fi
            ;;
    esac

    echo ""
}

main "$@"
