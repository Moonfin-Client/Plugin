# The catalog Silo reads from a plugin repository: this version's manifest
# and a download for each platform.
{
  plugins: [{
    manifest: ($m[0] | .version = $v | del(.checksum)),
    repo_url: $m[0].presentation.source_url,
    binaries: {
      "linux/amd64": {url: "\($base)/Moonbase.Silo-\($v)-linux-amd64", checksum: $amd64},
      "linux/arm64": {url: "\($base)/Moonbase.Silo-\($v)-linux-arm64", checksum: $arm64}
    }
  }]
}
