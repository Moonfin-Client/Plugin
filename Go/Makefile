# cmd/silo/manifest.json holds the version for local builds, and a release
# passes the one from its tag instead. Silo release files are named
# Moonbase.Silo-<version>-<os>-<arch>, which Silo installs as uploaded.
VERSION ?= $(shell sed -n 's/^  "version": "\(.*\)",$$/\1/p' cmd/silo/manifest.json)
LDFLAGS := -s -w -X main.version=$(VERSION)
PLATFORMS := linux/amd64 linux/arm64

.PHONY: build test dist silo-index plex-image clean

build:
	CGO_ENABLED=0 go build -trimpath -ldflags "$(LDFLAGS)" -o dist/moonbase ./cmd/silo
	CGO_ENABLED=0 go build -trimpath -ldflags "$(LDFLAGS)" -o dist/moonbase-plex ./cmd/plex

test:
	go vet ./...
	go test -race ./...

dist:
	rm -f dist/Moonbase.Silo-* dist/SHA256SUMS
	@for p in $(PLATFORMS); do \
		GOOS=$${p%/*} GOARCH=$${p#*/} CGO_ENABLED=0 go build -trimpath -ldflags "$(LDFLAGS)" \
			-o dist/Moonbase.Silo-$(VERSION)-$${p%/*}-$${p#*/} ./cmd/silo || exit 1; \
	done
	cd dist && sha256sum Moonbase.Silo-* > SHA256SUMS

# The catalog Silo reads when an admin adds it as a plugin repository.
# BASE_URL is where this version's release files can be downloaded.
silo-index: dist
	@test -n "$(BASE_URL)" || { echo "Set BASE_URL to the release's download address"; exit 1; }
	jq -n -f tools/silo-index.jq --arg v "$(VERSION)" --arg base "$(BASE_URL)" \
		--arg amd64 "$$(sha256sum dist/Moonbase.Silo-$(VERSION)-linux-amd64 | cut -d' ' -f1)" \
		--arg arm64 "$$(sha256sum dist/Moonbase.Silo-$(VERSION)-linux-arm64 | cut -d' ' -f1)" \
		--slurpfile m cmd/silo/manifest.json > dist/index.json

plex-image:
	$(or $(DOCKER),docker) build --build-arg VERSION=$(VERSION) -f cmd/plex/Dockerfile -t moonbase-plex:$(VERSION) .

clean:
	rm -rf dist
