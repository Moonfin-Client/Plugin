# Build from the repository root: docker build -f Go/cmd/plex/Dockerfile .
# The build stages run on the builder's own platform and cross-compile, so a
# multi-platform build doesn't run Go or .NET under emulation.
FROM --platform=$BUILDPLATFORM golang:1.26-alpine AS build
WORKDIR /src
COPY Go/go.mod Go/go.sum ./
RUN go mod download
COPY Go/ .
COPY Jellyfin/frontend web/moonfin/
ARG VERSION=0.0.0-dev
ARG TARGETOS
ARG TARGETARCH
# The push relay key comes in as the optional build secret relay_key, so it
# stays out of the image's build history. Without it, push needs the Push
# Relay Key setting.
RUN --mount=type=secret,id=relay_key CGO_ENABLED=0 GOOS=$TARGETOS GOARCH=$TARGETARCH go build -trimpath \
	-ldflags "-s -w -X main.version=${VERSION} -X github.com/Moonfin-Client/Plugin/Go/internal/api.builtInRelayKey=$(cat /run/secrets/relay_key 2>/dev/null)" -o /out/moonbase-plex ./cmd/plex \
	&& mkdir -p /out/config

# Smart search runs the same C# engine as the Jellyfin and Emby plugins.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS smartsearch
WORKDIR /src
COPY shared/SmartSearch shared/SmartSearch
COPY Go/smartsearch Go/smartsearch
ARG TARGETARCH
RUN dotnet publish Go/smartsearch -c Release -r linux-$(echo "$TARGETARCH" | sed 's/amd64/x64/') -o /out/smartsearch

# The smart search helper needs glibc and ICU, which this image has and distroless static doesn't.
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-noble-chiseled-extra
LABEL org.opencontainers.image.source=https://github.com/Moonfin-Client/Plugin
LABEL org.opencontainers.image.description="Moonbase for Plex, the Moonfin server companion that runs next to Plex Media Server"
LABEL org.opencontainers.image.licenses=GPL-3.0-only
COPY --from=build /out/moonbase-plex /moonbase-plex
COPY --from=smartsearch /out/smartsearch /smartsearch
COPY shared/PosterBadges/flags /flags
COPY shared/WatchParty/invite.html shared/WatchParty/preview.png /watchparty/
COPY shared/EmulatorJS/player.html shared/EmulatorJS/moonfin-bridge.js /emulatorjs/
COPY shared/Seasonal/seasonal-holidays.json /seasonal-holidays.json
# A named volume takes this folder's owner, so the nonroot user can write it.
COPY --from=build --chown=65532:65532 /out/config /config
USER 65532:65532
VOLUME /config
EXPOSE 32500
ENTRYPOINT ["/moonbase-plex"]
