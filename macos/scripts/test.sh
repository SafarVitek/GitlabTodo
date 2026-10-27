#!/usr/bin/env bash
# Compiles the app's logic together with the checks in Tests/main.swift and runs them.
set -euo pipefail
cd "$(dirname "$0")/.."

out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
sources=$(ls Shared/*.swift Widget/WidgetViews.swift Widget/WidgetIntents.swift App/*.swift | grep -v GitLabTodoApp.swift)
# shellcheck disable=SC2086
swiftc -swift-version 6 -o "$out/tests" $sources Tests/main.swift
"$out/tests"
