#!/usr/bin/env bash
# Builds a Release version and installs it to ~/Applications, where macOS picks up its widget.
set -euo pipefail
cd "$(dirname "$0")/.."

if command -v xcodegen >/dev/null; then
  xcodegen generate --quiet
else
  nix run nixpkgs#xcodegen -- generate --quiet
fi

xcodebuild -project GitLabTodo.xcodeproj -scheme GitLabTodo -configuration Release \
  -destination 'generic/platform=macOS' -derivedDataPath build -quiet build

app="$HOME/Applications/GitLabTodo.app"
lsregister=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
# Only the installed copy should be known to macOS, otherwise it may run a stale widget from the build folder.
for built in build/Build/Products/*/GitLabTodo.app; do
  "$lsregister" -u "$built" 2>/dev/null || true
done
pkill -x GitLabTodo || true
# macOS keeps a running widget extension even after the app is replaced; stop it so the new one loads.
pkill -f "GitLabTodoWidget.appex" || true
rm -rf "$app"
cp -R build/Build/Products/Release/GitLabTodo.app "$app"
"$lsregister" -f "$app"
open "$app"
echo "Nainstalováno do $app"
