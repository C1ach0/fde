FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY src/FoxholeDataExtractor/FoxholeDataExtractor.csproj ./
RUN dotnet restore
COPY src/FoxholeDataExtractor/ ./
RUN dotnet publish -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
USER root
RUN dpkg --add-architecture i386 \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
       ca-certificates curl tar bash lib32gcc-s1 lib32stdc++6 libc6-i386 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /opt/steamcmd /app/config /game /output /state \
    && curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz -o /tmp/steamcmd.tar.gz \
    && tar -xzf /tmp/steamcmd.tar.gz -C /opt/steamcmd \
    && rm /tmp/steamcmd.tar.gz \
    && /opt/steamcmd/steamcmd.sh +quit
WORKDIR /app
COPY --from=build /publish/ /app/
COPY scripts/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh
ENV STEAMCMD=/opt/steamcmd/steamcmd.sh \
    STEAM_APP_ID=505460 \
    FOXHOLE_GAME_DIR=/game \
    OUTPUT_DIR=/output \
    STATE_DIR=/state \
    EXTRACTION_CONFIG=/app/config/extraction.json \
    CHECK_INTERVAL_SECONDS=21600
VOLUME ["/game", "/output", "/state"]
ENTRYPOINT ["/app/entrypoint.sh"]
CMD ["run"]
