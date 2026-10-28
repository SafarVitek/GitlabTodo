#!/usr/bin/env bash
# Builds the Linux release files from one self-contained build: an AppImage, which updates itself from the app, and
# a .deb for Debian and Ubuntu. Runs on x86-64 Linux with the .NET 10 SDK, curl and dpkg-deb.
# Usage: linux/packaging/build.sh <output folder>
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
linux=$(dirname "$here")
mkdir -p "$1"
out=$(cd "$1" && pwd)
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$linux/GitLabTodo/GitLabTodo.csproj")
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Pinned, checked against the digests GitHub shows for these release files.
appimagetool_url=https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
appimagetool_sha256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
runtime_url=https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64
runtime_sha256=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d

fetch() {
  curl -fsSL --retry 3 -o "$2" "$1"
  echo "$3  $2" | sha256sum -c --quiet -
}

echo "GitLab Todo $version"
AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet publish "$linux/GitLabTodo/GitLabTodo.csproj" \
  -c Release -r linux-x64 --self-contained -p:DebugType=none -o "$work/app"

# AppImage: the build under usr/lib, started by AppRun.
appdir="$work/AppDir"
mkdir -p "$appdir/usr/lib"
cp -a "$work/app" "$appdir/usr/lib/gitlab-todo"
install -Dm755 "$here/AppRun" "$appdir/AppRun"
install -Dm644 "$here/gitlab-todo.desktop" "$appdir/gitlab-todo.desktop"
install -Dm644 "$here/gitlab-todo.desktop" "$appdir/usr/share/applications/gitlab-todo.desktop"
install -Dm644 "$linux/GitLabTodo/Assets/icon.png" "$appdir/gitlab-todo.png"
install -Dm644 "$linux/GitLabTodo/Assets/icon.png" "$appdir/usr/share/icons/hicolor/512x512/apps/gitlab-todo.png"
ln -s gitlab-todo.png "$appdir/.DirIcon"
fetch "$appimagetool_url" "$work/appimagetool" "$appimagetool_sha256"
fetch "$runtime_url" "$work/runtime" "$runtime_sha256"
chmod +x "$work/appimagetool"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$work/appimagetool" --no-appstream --runtime-file "$work/runtime" \
  "$appdir" "$out/GitLabTodo-x86_64.AppImage"

# .deb: the build in /opt, the command on the PATH, the menu entry and icon where desktops look for them.
deb="$work/deb"
mkdir -p "$deb/DEBIAN" "$deb/opt" "$deb/usr/bin"
cp -a "$work/app" "$deb/opt/gitlab-todo"
ln -s /opt/gitlab-todo/gitlab-todo "$deb/usr/bin/gitlab-todo"
install -Dm644 "$here/gitlab-todo.desktop" "$deb/usr/share/applications/gitlab-todo.desktop"
install -Dm644 "$linux/GitLabTodo/Assets/icon.png" "$deb/usr/share/icons/hicolor/512x512/apps/gitlab-todo.png"
find "$deb" -type d -exec chmod 755 {} +
chmod -R go-w "$deb"
# What .NET needs on Linux (no ICU, the app runs without it), then X11 and fontconfig for Avalonia.
cat > "$deb/DEBIAN/control" <<CONTROL
Package: gitlab-todo
Version: $version
Architecture: amd64
Maintainer: GitLab Todo <https://github.com/SafarVitek/GitlabTodo>
Installed-Size: $(du -sk "$deb/opt" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, libssl3 | libssl3t64 | libssl1.1, ca-certificates, libfontconfig1, libx11-6, libice6, libsm6, libxcursor1, libxext6, libxi6, libxrandr2
Recommends: libgl1
Section: devel
Priority: optional
Homepage: https://github.com/SafarVitek/GitlabTodo
Description: GitLab issues and merge requests in the order you work on them
 A to-do list of the issues and merge requests assigned to you on one or two
 GitLab servers, ordered by hand, with a timeline of what waits on what.
CONTROL
dpkg-deb --root-owner-group -Zxz --build "$deb" "$out/gitlab-todo_amd64.deb"

ls -l "$out"
