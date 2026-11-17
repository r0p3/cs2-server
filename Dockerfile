# syntax=docker/dockerfile:1
ARG CS2_BASE_IMAGE=joedwards32/cs2:latest
FROM ${CS2_BASE_IMAGE} AS upstream

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
RUN apt-get update && apt-get install -y --no-install-recommends unzip && rm -rf /var/lib/apt/lists/*

# Keep the runtime and the API used at compile time on the same release.
ADD --checksum=sha256:f0653332a517c9edbaf8d6bfeb2ffec5aa730638ebd5e568d9753fa1063ba68e https://github.com/roflmuffin/CounterStrikeSharp/releases/download/v1.0.380/counterstrikesharp-with-runtime-linux-1.0.380.zip /tmp/css.zip
ADD https://github.com/alliedmodders/metamod-source/releases/download/2.0.0.1491/mmsource-2.0.0-git1491-linux.tar.gz /tmp/metamod.tar.gz
RUN mkdir /bundle && tar -xzf /tmp/metamod.tar.gz -C /bundle && unzip -oq /tmp/css.zip -d /bundle

COPY src/RepServer/ ./
RUN dotnet build -c Release -o /out -p:CounterStrikeSharpApi=/bundle/addons/counterstrikesharp/api/CounterStrikeSharp.API.dll \
    && mkdir -p /bundle/addons/counterstrikesharp/plugins/RepServer \
    && cp /out/RepServer.dll /bundle/addons/counterstrikesharp/plugins/RepServer/

# Keep the SteamCMD sequence, but load the hook packaged with this image.
COPY --from=upstream /home/steam/entry.sh /build/upstream-entry.sh
RUN grep -Fxq 'source "${STEAMAPPDIR}/pre.sh"' /build/upstream-entry.sh \
    && sed 's|^source "\${STEAMAPPDIR}/pre\.sh"$|source /opt/repserver/pre.sh|' /build/upstream-entry.sh > /bundle/entry.sh \
    && bash -n /bundle/entry.sh

FROM upstream
COPY --from=build /bundle/ /opt/repserver/
COPY config/maps.txt /opt/repserver/maps.txt
COPY config/bot-names.txt /opt/repserver/bot-names.txt
COPY config/bot-priority.txt /opt/repserver/bot-priority.txt
COPY config/modes/*.cfg /opt/repserver/modes/
COPY scripts/pre.sh /opt/repserver/pre.sh
