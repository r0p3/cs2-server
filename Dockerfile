# syntax=docker/dockerfile:1
ARG CS2_BASE_IMAGE=joedwards32/cs2:latest
FROM ${CS2_BASE_IMAGE} AS upstream

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
RUN apt-get update && apt-get install -y --no-install-recommends python3 unzip && rm -rf /var/lib/apt/lists/*

# Keep the runtime and the API used at compile time on the same release.
ADD --checksum=sha256:f0653332a517c9edbaf8d6bfeb2ffec5aa730638ebd5e568d9753fa1063ba68e https://github.com/roflmuffin/CounterStrikeSharp/releases/download/v1.0.380/counterstrikesharp-with-runtime-linux-1.0.380.zip /tmp/css.zip
ADD https://github.com/alliedmodders/metamod-source/releases/download/2.0.0.1491/mmsource-2.0.0-git1491-linux.tar.gz /tmp/metamod.tar.gz
RUN mkdir /bundle && tar -xzf /tmp/metamod.tar.gz -C /bundle && unzip -oq /tmp/css.zip -d /bundle
# Temporary Linux hotfix for CounterStrikeSharp 1.0.380 on CS2 1.42.0.1 (upstream issue #1500).
ADD --checksum=sha256:f20c4c4f2d965364bee0295ed08d09738ac61aa894c5f267e24858d90453d94e https://github.com/karolhel/cs2-css-hotfix-2000950/releases/download/v1.0.380-hotfix-2000950/counterstrikesharp.so /tmp/counterstrikesharp-hotfix.so
RUN install -m 0755 /tmp/counterstrikesharp-hotfix.so /bundle/addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so
ADD --checksum=sha256:1e4cb517308f8a6bd33c8fcb6905bf9d5c864258904374e4859f0bdd94fa2086 https://github.com/Source2ZE/MultiAddonManager/releases/download/v1.7.4/MultiAddonManager-v1.7.4-steamrt3.tar.gz /tmp/multiaddonmanager.tar.gz
RUN mkdir /bundle/multiaddonmanager && tar -xzf /tmp/multiaddonmanager.tar.gz -C /bundle/multiaddonmanager

COPY src/RepServer/ ./
RUN dotnet build -c Release -o /out -p:CounterStrikeSharpApi=/bundle/addons/counterstrikesharp/api/CounterStrikeSharp.API.dll \
    && mkdir -p /bundle/addons/counterstrikesharp/plugins/RepServer \
    && cp /out/RepServer.dll /bundle/addons/counterstrikesharp/plugins/RepServer/

# Keep the SteamCMD sequence, but load the hook packaged with this image.
COPY --from=upstream /home/steam/entry.sh /build/upstream-entry.sh
RUN grep -Fxq 'source "${STEAMAPPDIR}/pre.sh"' /build/upstream-entry.sh \
    && sed 's|^source "\${STEAMAPPDIR}/pre\.sh"$|source /opt/repserver/pre.sh|' /build/upstream-entry.sh > /bundle/entry.sh \
    && bash -n /bundle/entry.sh

COPY scripts/build-bot-profile.py config/bot-names.txt /build/
RUN python3 /build/build-bot-profile.py /build/bot-names.txt /bundle/botprofile.vpk

FROM upstream
COPY --from=build /bundle/ /opt/repserver/
COPY config/maps.txt config/autobuy.json /opt/repserver/
COPY config/bot-names.txt /opt/repserver/bot-names.txt
COPY config/bot-priority.txt /opt/repserver/bot-priority.txt
COPY config/modes/*.cfg /opt/repserver/modes/
COPY scripts/pre.sh /opt/repserver/pre.sh
