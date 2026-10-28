# GitLab Todo pro Linux

Avalonia (.NET 10) aplikace se stejnými funkcemi jako macOS verze, jen bez widgetu: seznam „Pořadí“
s přetahováním, timeline s vazbami a odklady, detail položky, akce „+ MR“ a „Ready“ a ikona v systémové
liště s tím, co je na řadě. Instalace (AppImage, .deb, Nix) a aktualizace jsou popsané v
[README v kořeni](../README.md#linux).

## Ovládání

- Pořadí měníš přetažením řádku, dvojklik otevře issue nebo MR v prohlížeči, pravé tlačítko nabídne
  všechny akce.
- V timeline přetáhneš blok v řádku projektu jinam, na prázdný krok (odloží se) nebo na blok jiného
  projektu (bude na něj čekat). Vazbu nakreslíš tažením z oranžové tečky na pravém okraji bloku, odebereš
  ji pravým tlačítkem na šipce. „+“ u projektu přidá cizí issue nebo MR.
- Zoom timeline: Ctrl + kolečko, Ctrl + / Ctrl − / Ctrl 0.
- Poznámky k položce (třeba zápis z meetingu) jsou v detailu, úkoly se píšou jako `- [ ] text` a odškrtávají
  se rovnou v náhledu. Klik na položku v nabídce v liště otevře poznámku k psaní.
- F5 nebo Ctrl R obnoví data, Ctrl , otevře nastavení, Ctrl Q aplikaci ukončí i z lišty.

## Data

- `~/.config/gitlab-todo/settings.json`: připojené GitLaby a nastavení.
- `~/.config/gitlab-todo/tokens.json`: tokeny, čitelné jen pro tebe (práva 600). Nejsou v klíčence
  systému, aby aplikace nepotřebovala žádnou další službu.
- `~/.local/share/gitlab-todo/plan.json`: pořadí, vazby, odklady, poznámky a cizí položky v timeline. Formát je
  stejný jako u macOS verze.
- `~/.local/share/gitlab-todo/snapshot.json`: poslední stažený stav, aby seznam nebyl po startu prázdný.
- `~/.local/share/gitlab-todo/sync.json`: nastavení synchronizace a stav posledního sloučení, když je zapnutá
  (viz [README v kořeni](../README.md#synchronizace-mezi-počítači)).

Spouštění po přihlášení zapíná nastavení, zapíše `~/.config/autostart/gitlab-todo.desktop` a aplikace
pak startuje rovnou do lišty.

AppImage si při spuštění zapíše položku do menu aplikací `~/.local/share/applications/gitlab-todo-appimage.desktop`
a ikonu do `~/.local/share/icons`. Položka ukazuje na soubor AppImage, takže po jeho přesunutí stačí spustit ho
jednou z nového místa. Aktualizace se stahuje vedle něj a pak ho nahradí, složka s AppImage proto musí být
zapisovatelná (třeba `~/Applications`, ne `/opt`).

## Systémová lišta

Ikona v liště používá StatusNotifierItem. KDE, Xfce, Cinnamon a většina panelů ho umí, GNOME potřebuje
rozšíření [AppIndicator](https://extensions.gnome.org/extension/615/appindicator-support/). Bez lišty
vypni v nastavení „Po zavření okna běžet dál v systémové liště“, jinak zavřené okno otevřeš jen novým
spuštěním aplikace.

## Vývoj

```sh
nix develop                                   # .NET 10 SDK
dotnet run --project linux/GitLabTodo         # aplikace
dotnet run --project linux/GitLabTodo -- --preview   # s ukázkovými daty, nic neukládá
dotnet run --project linux/Tests              # testy logiky
dotnet run --project linux/UiTests            # UI proklikané bez displeje
nix build .#gitlab-todo                       # balíček
linux/packaging/build.sh dist                 # AppImage a .deb (na x86-64 Linuxu s .NET 10 SDK a dpkg-deb)
```

Po změně NuGet balíčků je potřeba obnovit `deps.json` pro Nix:

```sh
dotnet restore linux/GitLabTodo -r linux-x64 --packages /tmp/gitlab-todo-packages
dotnet restore linux/GitLabTodo -r linux-arm64 --packages /tmp/gitlab-todo-packages
nuget-to-json /tmp/gitlab-todo-packages \
  | jq '[.[] | select(.pname | test("^Microsoft\\.(NETCore|AspNetCore)\\.App\\.") | not)]' > linux/deps.json
```

Runtime balíčky .NET se vynechávají, ty dodává SDK z nixpkgs.

## Struktura

- `GitLabTodo.Core/`: model, GitLab API, pořadí a vazby (`AppState`), výpočet timeline. Bez UI, takže
  se dá testovat bez displeje.
- `GitLabTodo/`: Avalonia aplikace. Timeline je kreslená vektorově při každém zoomu, takže zůstává ostrá.
- `Tests/`: testy logiky jako konzolový program, stejné scénáře jako testy macOS verze.
- `UiTests/`: aplikace na ukázkových datech proklikaná simulovanou myší a klávesnicí bez displeje.
- `package.nix`: balíček pro nixpkgs, použitý z `flake.nix` v kořeni.
- `packaging/`: sestavení AppImage a .deb pro vydání, spouští ho `.github/workflows/release.yml`.
