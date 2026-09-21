# AutoTreasure 中継サーバーの入れ物。
#
# VPS では既に Docker Compose が動いているので、そこへ1つ足す形にする。
# .NET を VPS へ直接入れる必要はない。
#
# 置き場所: /home/ubuntu/estelld-repo/treasure-relay/
# 既存の app / plugins.json / .env / data / mogcolle-relay には一切触らない。

# --- 組み立て ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# 本番プラグインと共有している取り決め（封筒の形）も要る。
COPY AutoTreasure/Sync/Relay/RelayProtocol.cs \
     AutoTreasure/Sync/Relay/
COPY AutoTreasure.RelayServer/ AutoTreasure.RelayServer/

WORKDIR /src/AutoTreasure.RelayServer

RUN dotnet publish -c Release -o /app --nologo

# --- 動かす ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app
COPY --from=build /app .

# Caddy が前に立つので、ここは平文でよい。
ENV TREASURE_RELAY_PORT=8080
EXPOSE 8080

# 生存確認。落ちていたら Docker が気づけるようにする。
# aspnet の image には wget も curl も入っていないので、
# .NET 自身に叩かせる。追加で何も入れなくて済む。
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "TreasureRelay.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "TreasureRelay.dll"]
