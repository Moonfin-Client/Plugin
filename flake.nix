{
  description = "Moonbase plugin development shell";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { nixpkgs, ... }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
        "x86_64-darwin"
        "aarch64-darwin"
      ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      devShells = forAllSystems (pkgs: {
        default = pkgs.mkShell {
          packages = with pkgs; [
            dotnet-sdk_8
            zip
            jq
            # md5sum for build.sh's checksum
            coreutils
          ];

          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
        };
      });
    };
}
