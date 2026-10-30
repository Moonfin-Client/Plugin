# Build from the Go directory: docker build -f cmd/plex/Dockerfile .
# The build stage runs on the builder's own platform and cross-compiles, so a
# multi-platform build doesn't run Go under emulation.
FROM --platform=$BUILDPLATFORM golang:1.26-alpine AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
ARG VERSION=0.0.0-dev
ARG TARGETOS
ARG TARGETARCH
RUN CGO_ENABLED=0 GOOS=$TARGETOS GOARCH=$TARGETARCH go build -trimpath -ldflags "-s -w -X main.version=${VERSION}" -o /out/moonbase-plex ./cmd/plex \
	&& mkdir -p /out/config

FROM gcr.io/distroless/static-debian12:nonroot
LABEL org.opencontainers.image.source=https://github.com/Moonfin-Client/Plugin
LABEL org.opencontainers.image.description="Moonbase for Plex, the Moonfin server companion that runs next to Plex Media Server"
LABEL org.opencontainers.image.licenses=GPL-3.0-only
COPY --from=build /out/moonbase-plex /moonbase-plex
# A named volume takes this folder's owner, so the nonroot user can write it.
COPY --from=build --chown=65532:65532 /out/config /config
VOLUME /config
EXPOSE 32500
ENTRYPOINT ["/moonbase-plex"]
