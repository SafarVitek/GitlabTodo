# GitLab Todo

Osobní seznam práce nad GitLabem. Ukazuje issue a merge requesty, které máš přiřazené, a merge requesty,
ve kterých jsi zmíněný, z jednoho nebo více GitLabů (i self-hosted). Pořadí, ve kterém na nich chceš
pracovat, si nastavíš přetahováním. Timeline pak ukáže, co přijde kdy a co na co čeká, i napříč projekty
a GitLaby. Pořadí, vazby a odklady se drží jen u tebe na počítači, do GitLabu se nezapisují.

Aplikace je ve dvou verzích se stejnými funkcemi a stejným formátem dat:

| | macOS | Linux |
|---|---|---|
| Složka | [`macos/`](macos) | [`linux/`](linux) |
| Technologie | SwiftUI, WidgetKit | Avalonia (.NET) |
| Widget na ploše | ano | ne |
| Kde žije | ikona v menu baru + okno | ikona v systémové liště + okno |
| Instalace | ke stažení z [Releases](../../releases) | Nix flake |

## Co umí

- Issue a jeho merge request jako jedna položka. Stav MR na první pohled: draft, merge konflikt,
  vyřešená a nevyřešená vlákna z review, štítky.
- Pořadí práce přetahováním, nové položky čekají v „Nezařazených“.
- „Čeká na“ jinou položku, issue kolegy (i z druhého GitLabu) nebo vlastní důvod. Až se to dokončí,
  položka se sama uvolní a ukáže se jako připravená.
- Poznámky k položce, třeba zápis z meetingu, s úkoly psanými jako v GitLabu (`- [ ] …`). Klik na položku
  ve widgetu otevře poznámku rovnou k psaní, widget ukazuje, kolik úkolů je hotových, a ten další na řadě.
- Timeline: řádek pro každý projekt, kroky „Teď, +1, +2…“, šipky vazeb, mezery tam, kde se čeká nebo
  kde je položka odložená. Přetahování, vazby šipkou z tečky na bloku, zoom.
- Jedním kliknutím (s potvrzením) vytvořit k issue větev a draft MR, nebo draft MR označit jako ready
  a přidat výchozího reviewera.

## Instalace

### macOS

Stáhni `GitLabTodo-macOS.zip` z [Releases](../../releases), rozbal a přesuň `GitLabTodo.app`
do `~/Applications` nebo `/Applications`.

Aplikace není notarizovaná od Applu, takže ji macOS napoprvé nespustí. Buď na ni klikni pravým tlačítkem
a vyber Otevřít, nebo jednou spusť:

```sh
xattr -dr com.apple.quarantine ~/Applications/GitLabTodo.app
```

Widget přidáš pravým klikem na ploše → Upravit widgety → GitLab Todo. Ze zdrojáků se aplikace
sestavuje podle [`macos/README.md`](macos/README.md).

### Linux

Přes Nix s flakes:

```sh
nix profile install github:SafarVitek/GitlabTodo
```

Jen vyzkoušet bez instalace jde `nix run github:SafarVitek/GitlabTodo`, s ukázkovými daty
`nix run github:SafarVitek/GitlabTodo -- --preview`. Na NixOS nebo v Home Manageru přidej flake do inputs
a balíček `packages.${system}.default` (nebo overlay `overlays.default`) do svých balíčků.

Aplikace se spouští jako „GitLab Todo“ z menu aplikací nebo příkazem `gitlab-todo`. Po zavření okna
běží dál v systémové liště, na GNOME k tomu potřebuješ rozšíření AppIndicator. Víc v
[`linux/README.md`](linux/README.md).

## Přístup ke GitLabu

V nastavení přidáš URL GitLabu a Personal Access Token se scope `api`, který je potřeba na vytváření
větví a merge requestů.

## Licence

[MIT](LICENSE)
