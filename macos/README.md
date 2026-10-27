# GitLab Todo pro macOS

SwiftUI aplikace v menu baru s hlavním oknem (pořadí, timeline, detail) a widgetem na plochu (WidgetKit).
Hotovou aplikaci stáhneš z Releases, viz [README v kořeni](../README.md).

## Sestavení ze zdrojáků

Potřeba je macOS 15+, Xcode a [XcodeGen](https://github.com/yonaskolb/XcodeGen). Když XcodeGen nemáš,
skript si ho vezme z nixpkgs.

```sh
./scripts/install.sh
```

Skript vygeneruje Xcode projekt z `project.yml`, sestaví Release, nainstaluje do
`~/Applications/GitLabTodo.app`, ukončí běžící verzi včetně rozšíření widgetu (jinak by macOS dál
ukazoval starý widget) a aplikaci spustí.

Ikona aplikace se generuje skriptem `swift scripts/make-icon.swift`.

Testy logiky (pořadí, čekání, timeline, párování issue a MR, odkazy) spustíš `./scripts/test.sh`.

## Struktura

- `App/`: aplikace (menu bar, hlavní okno, timeline, nastavení, GitLab API).
- `Widget/`: rozšíření widgetu.
- `Shared/`: model a vzhled sdílený aplikací a widgetem.

## Poznámky

- Aplikace je podepsaná ad-hoc, bez Apple Developer účtu. Po každém novém buildu se macOS jednou zeptá
  na přístup ke Keychainu, kde jsou tokeny. Stačí „Vždy povolit“.
- Data (pořadí, vazby, poznámky, poslední stav pro widget, stav synchronizace) leží v
  `~/Library/Application Support/GitLabTodo/`.
- Widget je sandboxovaný a z té složky jen čte, s aplikací se domlouvá přes odkazy `gitlabtodo://`.
