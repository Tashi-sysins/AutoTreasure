#!/bin/bash
# AutoTreasure の中継サーバーを VPS へ配置する。
#
# 【このPC（Shigeya-PC）の Git Bash で実行できます】
#   鍵は C:\Users\Shigeya-PC\.ssh\estelld_vps にあります。
#
# 使い方:
#   bash VPSへ配置する.sh
#
# 何をするか:
#   1. ソースを VPS へ送る
#   2. docker-compose.yml と Caddyfile へ追記（既存は触らない）
#   3. 組み立てて起動
#   4. 応答するか確かめる（新旧3つとも）
#
# 触らないもの:
#   既存の app / plugins.json / plugins/ / .env / data/
#   **稼働中の mogcolle-relay**
#   → プラグイン配布も MogColle も止まりません。

set -euo pipefail

KEY=/c/Users/Shigeya-PC/.ssh/estelld_vps
HOST=root@133.167.127.79
PROJ=/home/ubuntu/estelld-repo

# このスクリプトから見たリポジトリの根。
#
# 2通りの置き方に対応する。
#   ・リポジトリの中  … deploy/ から2つ上
#   ・配置用に切り出し … スクリプトと同じ場所
SRC="$(cd "$(dirname "$0")" && pwd)"

if [ ! -d "$SRC/AutoTreasure.RelayServer" ]; then
    SRC="$(cd "$(dirname "$0")/../.." && pwd)"
fi

if [ ! -d "$SRC/AutoTreasure.RelayServer" ]; then
    echo "中継サーバーのソースが見つかりません（$SRC）"
    exit 1
fi

if [ ! -f "$KEY" ]; then
    echo "SSH鍵が見つかりません（$KEY）"
    echo "別のPCで動かす場合は、このスクリプトの KEY= を書き換えてください。"
    exit 1
fi

echo "=== 1. 送るものを固める ==="
TAR=/tmp/treasure-relay.tar.gz

# ⚠ 封筒の定義（RelayProtocol.cs）はプラグイン側が正本。
#   サーバーの csproj が Compile Include で借りているので、
#   ここにも含めないと VPS 側で組み立てられない。
tar -C "$SRC" \
    --exclude='bin' --exclude='obj' \
    -czf "$TAR" \
    AutoTreasure.RelayServer \
    AutoTreasure/Sync/Relay/RelayProtocol.cs

echo "    $(du -h "$TAR" | cut -f1)"

echo "=== 2. VPS へ送る ==="
ssh -i "$KEY" -o BatchMode=yes "$HOST" "mkdir -p $PROJ/treasure-relay"
scp -i "$KEY" -o BatchMode=yes "$TAR" "$HOST:/tmp/treasure-relay.tar.gz"

ssh -i "$KEY" -o BatchMode=yes "$HOST" "
  rm -rf $PROJ/treasure-relay/*
  tar -xzf /tmp/treasure-relay.tar.gz -C $PROJ/treasure-relay
  chown -R ubuntu:ubuntu $PROJ/treasure-relay
"

echo "=== 3. 設定へ追記（既存は触らない） ==="
ssh -i "$KEY" -o BatchMode=yes "$HOST" "
  set -e
  cd $PROJ

  # 念のため控えを取る。
  #
  # ⚠ 控えの名前は before-treasure にする。
  #   MogColle の控え（before-mogcolle）を上書きすると、
  #   MogColle を入れる前の状態へ戻せなくなる。
  #
  #   cp -n なので2回目以降は上書きされない ＝ 最初の状態が守られる。
  cp -n docker-compose.yml docker-compose.yml.before-treasure || true
  cp -n Caddyfile          Caddyfile.before-treasure          || true

  # --- docker-compose.yml ---
  if grep -q 'treasure-relay:' docker-compose.yml; then
    echo '    compose: 追記済み'
  else
    python3 - <<'PY'
import re, io
p = 'docker-compose.yml'
t = io.open(p, encoding='utf-8').read()

block = '''
  treasure-relay:
    build:
      context: ./treasure-relay
      dockerfile: AutoTreasure.RelayServer/Dockerfile
    container_name: treasure-relay
    restart: unless-stopped
    environment:
      TREASURE_RELAY_PORT: \"8080\"
      Logging__LogLevel__Default: \"Information\"
      Logging__LogLevel__Microsoft.AspNetCore: \"Warning\"
    expose:
      - \"8080\"
'''

m = re.search(r'^services:\\s*$', t, re.M)
if not m:
    raise SystemExit('services: が見つかりません')

i = m.end()
io.open(p, 'w', encoding='utf-8').write(t[:i] + block + t[i:])
print('    compose: 追記しました')
PY
  fi

  # --- Caddyfile ---
  if grep -q '/treasure/' Caddyfile; then
    echo '    Caddy: 追記済み'
  else
    python3 - <<'PY'
import io
p = 'Caddyfile'
t = io.open(p, encoding='utf-8').read()

block = '''\\thandle /treasure/* {
\\t\\treverse_proxy treasure-relay:8080
\\t}

'''

# 既存の reverse_proxy app:8000 の前に入れる。
# ここは catch-all なので、後ろに置くと /treasure/ が届かない。
key = 'reverse_proxy app:8000'
i = t.find(key)
if i < 0:
    raise SystemExit('reverse_proxy app:8000 が見つかりません')

# その行の先頭へ戻す。
start = t.rfind('\\n', 0, i) + 1
io.open(p, 'w', encoding='utf-8').write(t[:start] + block + t[start:])
print('    Caddy: 追記しました')
PY
  fi
"

echo "=== 4. 組み立てて起動 ==="
# ⚠ サービス名を必ず書く。
#   省くと全サービスが再起動し、MogColle が巻き添えになる。
ssh -i "$KEY" -o BatchMode=yes "$HOST" "
  cd $PROJ
  docker compose up -d --build treasure-relay 2>&1 | tail -n 6
  docker compose restart caddy 2>&1 | tail -n 2
"

echo "=== 5. 確かめる ==="
# ⚠ 既存2つの確認を省かない。
#   新機能が動いても、既存が止まっていたら失敗。
sleep 5
ssh -i "$KEY" -o BatchMode=yes "$HOST" "
  echo -n '    コンテナ   : '
  docker ps --filter name=treasure-relay --format '{{.Status}}'

  echo -n '    中から     : '
  curl -s http://127.0.0.1:8080/treasure/health 2>/dev/null || echo '(直接は届きません)'
  echo

  echo -n '    Caddy経由  : '
  curl -s --resolve estelldprereleaserepo.net:443:127.0.0.1 \
       https://estelldprereleaserepo.net/treasure/health
  echo

  echo -n '    MogColle   : '
  curl -s --resolve estelldprereleaserepo.net:443:127.0.0.1 \
       https://estelldprereleaserepo.net/mogcolle/health
  echo

  echo -n '    既存app    : '
  curl -s --resolve estelldprereleaserepo.net:443:127.0.0.1 \
       https://estelldprereleaserepo.net/health
  echo
"

echo
echo "=== 完了 ==="
echo "  wss://estelldprereleaserepo.net/treasure/ws"
echo
echo "  元に戻すには VPS で:"
echo "    cd $PROJ"
echo "    docker compose stop treasure-relay && docker compose rm -f treasure-relay"
echo "    cp docker-compose.yml.before-treasure docker-compose.yml"
echo "    cp Caddyfile.before-treasure Caddyfile"
echo "    docker compose up -d && docker compose restart caddy"
