{
  description = "GitLab Todo for Linux: issues and merge requests from GitLab in your own work order";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs =
    { self, nixpkgs }:
    let
      linux = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      # The macOS app is built with Xcode, see macos/README.md; here macOS only gets a shell for working on the Linux app.
      everywhere = linux ++ [
        "x86_64-darwin"
        "aarch64-darwin"
      ];
      forSystems = systems: f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      packages = forSystems linux (pkgs: rec {
        gitlab-todo = pkgs.callPackage ./linux/package.nix { };
        default = gitlab-todo;
      });

      apps = forSystems linux (pkgs: {
        default = {
          type = "app";
          program = nixpkgs.lib.getExe self.packages.${pkgs.stdenv.hostPlatform.system}.gitlab-todo;
        };
      });

      overlays.default = final: _prev: {
        gitlab-todo = final.callPackage ./linux/package.nix { };
      };

      devShells = forSystems everywhere (pkgs: {
        default = pkgs.mkShell {
          packages = [
            pkgs.dotnetCorePackages.sdk_10_0
            pkgs.nuget-to-json
          ];
          AVALONIA_TELEMETRY_OPTOUT = "1";
        };
      });

      formatter = forSystems everywhere (pkgs: pkgs.nixfmt);
    };
}
