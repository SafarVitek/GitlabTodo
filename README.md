# GitLab Todo

Osobní seznam práce nad GitLabem. Ukazuje issue a merge requesty, které máš přiřazené, a merge requesty,
ve kterých jsi zmíněný, z jednoho nebo více GitLabů (i self-hosted). Pořadí, ve kterém na nich chceš
pracovat, si nastavíš přetahováním. Timeline pak ukáže, co přijde kdy a co na co čeká, i napříč projekty
a GitLaby. Pořadí, vazby a odklady se drží jen u tebe na počítači, do GitLabu se nezapisují.

Aplikace je ve dvou verzích se stejnými funkcemi a stejným formátem dat:

| | macOS | Linux (a další systémy) |
|---|---|---|
| Složka | [`macos/`](macos) | `linux/` (připravuje se) |
| Technologie | SwiftUI, WidgetKit | Avalonia (.NET) |
| Widget na ploše | ano | ne |
| Kde žije | ikona v menu baru + okno | ikona v systémové liště + okno |
| Instalace | ke stažení z [Releases](../../releases) | `nix profile install` |

## Co umí

- Issue a jeho merge request jako jedna položka. Stav MR na první pohled: draft, merge konflikt,
  vyřešená a nevyřešená vlákna z review, štítky.
- Pořadí práce přetahováním, nové položky čekají v „Nezařazených“.
- „Čeká na“ jinou položku, issue kolegy (i z druhého GitLabu) nebo vlastní důvod. Až se to dokončí,
  položka se sama uvolní a ukáže se jako připravená.
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

Linuxová verze se připravuje, bude se instalovat přes Nix.

## Přístup ke GitLabu

V nastavení přidáš URL GitLabu a Personal Access Token se scope `api`. `read_api` stačí na čtení,
`api` je potřeba na vytváření větví a merge requestů. Token jde vytvořit i přes SSH
(`ssh git@<host> personal_access_token gitlab-todo api 365`), na to má nastavení tlačítko.

## Licence

[MIT](LICENSE)
