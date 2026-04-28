FROM mcr.microsoft.com/dotnet/sdk:8.0 AS css-api-provider
WORKDIR /build/css-api

RUN apt-get update && \
    apt-get install -y curl unzip && \
    rm -rf /var/lib/apt/lists/* && \
    curl -L https://github.com/roflmuffin/CounterStrikeSharp/releases/download/v1.0.367/counterstrikesharp-with-runtime-linux-1.0.367.zip --output css-runtime.zip && \
    unzip -j css-runtime.zip 'addons/counterstrikesharp/api/CounterStrikeSharp.API.dll' -d /build/css-api && \
    rm css-runtime.zip

# ── Build local CounterStrikeSharp plugins ───────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS walleye-plugin-builder
WORKDIR /build/WallEyeServer

COPY --from=css-api-provider /build/css-api/CounterStrikeSharp.API.dll /build/css-api/
COPY plugins/WallEyeServer/ ./
RUN dotnet build -c Release

# ── CS2 runtime image with MetaMod + CounterStrikeSharp baseline ─────────────
FROM base-cs2-image
SHELL ["/bin/bash", "-c"]

COPY setup.sh       /home/steam/
COPY autoexec.cfg   /home/steam/
COPY server.cfg     /home/steam/

USER root
RUN apt update && \
    apt install -y unzip jq libicu76 patchelf && \
    ln -sfn "/home/steam/Steam/steamapps/common/Counter-Strike Global Offensive/game/csgo/addons" /addons && \
    chown steam /home/steam/setup.sh && \
    chmod +x /home/steam/setup.sh && \
    chown steam /home/steam/autoexec.cfg && \
    chown steam /home/steam/server.cfg && \
    rm -rf /var/lib/apt/lists/*

USER steam
RUN mkdir -p /home/steam/download && \
    cd /home/steam/download && \
    # MetaMod:Source 2.0 (git1396)
    curl -L https://github.com/alliedmodders/metamod-source/releases/download/2.0.0.1396/mmsource-2.0.0-git1396-linux.tar.gz --output mmsource-2.0.0-git1396-linux.tar.gz && \
    tar -xzvf mmsource-2.0.0-git1396-linux.tar.gz && \
    rm mmsource-2.0.0-git1396-linux.tar.gz && \
    # CounterStrikeSharp v1.0.367 with runtime
    curl -L https://github.com/roflmuffin/CounterStrikeSharp/releases/download/v1.0.367/counterstrikesharp-with-runtime-linux-1.0.367.zip --output counterstrikesharp-with-runtime-linux-1.0.367.zip && \
    unzip counterstrikesharp-with-runtime-linux-1.0.367.zip && \
    rm counterstrikesharp-with-runtime-linux-1.0.367.zip && \
    patchelf --clear-execstack addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so && \
    mkdir -p addons/counterstrikesharp/plugins && \
    mkdir -p addons/counterstrikesharp/plugins/WallEyeServer && \
    true

COPY --from=walleye-plugin-builder --chown=steam:steam /build/WallEyeServer/bin/Release/net8.0/WallEyeServer.dll /home/steam/download/addons/counterstrikesharp/plugins/WallEyeServer/
COPY --from=walleye-plugin-builder --chown=steam:steam /build/WallEyeServer/bin/Release/net8.0/WallEyeServer.deps.json /home/steam/download/addons/counterstrikesharp/plugins/WallEyeServer/

ENTRYPOINT ["bash", "/home/steam/setup.sh"]
