#!/usr/bin/env bash
#
# Musician 环境恢复脚本
#
# 刚克隆仓库的人跑一次这个，就能从零恢复到可作曲的状态。
#
# 用法：
#     bash tools/setup.sh
#
# 国内网络需要代理时先设置环境变量（脚本会自动读取）：
#     export HTTPS_PROXY=http://127.0.0.1:10808
#     bash tools/setup.sh
#
# 做四件事：
#   1. 检查 .NET SDK 版本
#   2. 还原 NuGet 包
#   3. 下载音色库 FluidR3_GM.sf2（约 140MB，不在仓库里）
#   4. 构建 + 测试 + 校验一首现成曲子
#
# 可重复执行：已就绪的步骤会自动跳过。
#
# 为什么用 bash 而不是 .cmd：批处理在中文 Windows 上有代码页问题（UTF-8 中文
# 会被解析成乱码），且必须用 CRLF 行尾；换成 .ps1 则受执行策略限制，
# Windows 默认禁止运行脚本。三者权衡下来 bash 最省事 —— Git for Windows 自带它。

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

SLNX="$ROOT/Musician.slnx"
CLI="$ROOT/src/Musician.Cli"
SF2="$ROOT/assets/soundfonts/FluidR3_GM.sf2"
STAGING="$ROOT/.staging"
TARBALL="$STAGING/fluid-soundfont.tar.gz"
SF2_URL="https://ftp.osuosl.org/pub/musescore/soundfont/fluid-soundfont.tar.gz"

if [ -t 1 ]; then
    C_OK=$'\033[32m'; C_SKIP=$'\033[90m'; C_WARN=$'\033[33m'; C_ERR=$'\033[31m'; C_OFF=$'\033[0m'
else
    C_OK=; C_SKIP=; C_WARN=; C_ERR=; C_OFF=
fi

ok()   { echo "  ${C_OK}[完成]${C_OFF} $1"; }
skip() { echo "  ${C_SKIP}[跳过]${C_OFF} $1"; }
warn() { echo "  ${C_WARN}[注意]${C_OFF} $1"; }
fail() { echo "  ${C_ERR}[失败]${C_OFF} $1"; }
step() { echo "$1"; }

echo
echo "========================================="
echo " Musician 环境恢复"
echo "========================================="
echo " 仓库位置  $ROOT"
echo

# ---------- 1. .NET SDK ----------

step "[1/4] 检查 .NET SDK"

if ! command -v dotnet >/dev/null 2>&1; then
    fail "找不到 dotnet 命令。"
    echo "         请先安装 .NET SDK 10：https://dotnet.microsoft.com/download"
    exit 1
fi

if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    fail "没有找到 .NET 10 SDK。当前已安装："
    dotnet --list-sdks 2>/dev/null | sed 's/^/         /'
    echo "         请安装 .NET 10：https://dotnet.microsoft.com/download"
    exit 1
fi

ok "已找到 .NET 10 SDK"
echo

# ---------- 2. NuGet 包 ----------

step "[2/4] 还原 NuGet 包（DryWetMidi / MeltySynth / NAudio）"

if ! dotnet restore "$SLNX" --nologo -v quiet; then
    fail "dotnet restore 失败。若是网络问题，请设置代理后重试。"
    exit 1
fi
ok "依赖已还原"
echo

# ---------- 3. 音色库 ----------

step "[3/4] 检查音色库"

if [ -f "$SF2" ]; then
    skip "音色库已存在（$(du -h "$SF2" | cut -f1)）"
else
    echo "      音色库约 140MB，不在仓库里，需要单独下载。"
    echo "      来源：$SF2_URL"
    echo "      视网络情况可能要几分钟到几小时。**中断了不要紧，重跑本脚本会从断点继续。**"

    mkdir -p "$STAGING" "$(dirname "$SF2")"

    PROXY="${HTTPS_PROXY:-${https_proxy:-${HTTP_PROXY:-${http_proxy:-}}}}"
    if [ -n "$PROXY" ]; then
        echo "      使用代理 $PROXY"
    else
        echo "      直连。若卡住或失败，请先设置代理："
        echo "        export HTTPS_PROXY=http://127.0.0.1:10808"
    fi

    echo "      正在下载，约 140MB，请耐心等待 ..."

    # --continue-at - 断点续传。这个下载在国内网络下实测可能只有 10–30 KB/s，
    # 也就是要跑一到三小时；中断后从头再来是不可接受的，所以续传是必需品。
    curl_opts=(-L --fail --retry 3 --retry-delay 5 --continue-at -)
    [ -n "$PROXY" ] && curl_opts+=(-x "$PROXY")

    if ! curl "${curl_opts[@]}" -o "$TARBALL" "$SF2_URL"; then
        # Windows 自带的 curl 走 schannel，代理环境下常因无法联网做证书吊销检查
        # 而失败（CRYPT_E_REVOCATION_OFFLINE）。这时再退一步跳过吊销检查重试 ——
        # 只在标准路径失败后才降级，不默认放宽。
        echo
        warn "标准下载失败，改用跳过证书吊销检查的方式重试 ..."
        if ! curl "${curl_opts[@]}" --ssl-no-revoke -o "$TARBALL" "$SF2_URL"; then
            echo
            fail "音色库下载失败。"
            echo
            echo "         可以手动下载后放到这个位置："
            echo "           $SF2"
            echo
            echo "         手动步骤："
            echo "           1. 下载 $SF2_URL"
            echo "           2. 解压，得到 \"FluidR3 GM2-2.SF2\""
            echo "           3. 重命名为 FluidR3_GM.sf2，放到上面的位置"
            echo
            echo "         或者指定其它位置："
            echo "           dotnet run --project src/Musician.Cli -- render <乐谱> --sf2 <你的sf2路径>"
            echo
            echo "         提示：重新跑一次本脚本会从断点继续下载，不必从头再来。"
            echo "               若续传一直失败（服务器不支持 Range），可先删掉："
            echo "               rm -f \"$TARBALL\""
            echo
            exit 1
        fi
    fi

    ok "下载完成（$(du -h "$TARBALL" | cut -f1)）"

    if ! command -v tar >/dev/null 2>&1; then
        fail "找不到 tar 命令。Windows 10 1803 以上自带，请手动解压："
        echo "           $TARBALL"
        echo "         并把 .SF2 放到：$SF2"
        exit 1
    fi

    echo "      正在解压 ..."
    if ! tar -xzf "$TARBALL" -C "$STAGING"; then
        fail "解压失败。"
        exit 1
    fi

    EXTRACTED="$(find "$STAGING" -maxdepth 2 -iname '*.sf2' -print -quit)"
    if [ -z "$EXTRACTED" ]; then
        fail "解压后没有找到 .SF2 文件。"
        exit 1
    fi

    cp "$EXTRACTED" "$SF2"
    rm -f "$TARBALL"

    ok "音色库已就位（$(du -h "$SF2" | cut -f1)）"
fi
echo

# ---------- 4. 构建与自检 ----------

step "[4/4] 构建并自检"

BUILD_OUT="$(dotnet build "$SLNX" --nologo -v quiet 2>&1)"
if [ $? -ne 0 ]; then
    fail "构建失败。"
    echo "$BUILD_OUT" | tail -15 | sed 's/^/         /'
    exit 1
fi
ok "构建通过"

TEST_OUT="$(dotnet test "$SLNX" --nologo -v quiet 2>&1)"
if [ $? -ne 0 ]; then
    fail "测试未全部通过。"
    echo "$TEST_OUT" | tail -15 | sed 's/^/         /'
    exit 1
fi
ok "测试通过（$(echo "$TEST_OUT" | grep -oE '通过:[[:space:]]*[0-9]+' | tail -1)）"

VALIDATE_OUT="$(dotnet run --project "$CLI" --no-build -- validate "$ROOT/songs/morning-light.json" 2>&1)"
if [ $? -ne 0 ]; then
    fail "校验失败，环境可能不完整。"
    echo "$VALIDATE_OUT" | sed 's/^/         /'
    exit 1
fi
ok "校验通过"

# ---------- 完成 ----------

echo
echo "========================================="
echo " 环境就绪"
echo "========================================="
echo
echo " 试试渲染一首现成的曲子："
echo
echo "   dotnet run --project src/Musician.Cli -- render songs/morning-light.json --play"
echo
echo " 想作曲的话，让 Claude Code 读仓库根目录的 CLAUDE.md ——"
echo " 那里写明了乐谱格式、可用织体、乐器音域和作曲注意事项。"
echo
