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
- Synchronizace mezi počítači (i mezi macOS a Linuxem) přes soubor v repozitáři na tvém GitLabu.

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

## Synchronizace mezi počítači

V nastavení v sekci Synchronizace zvol GitLab a repozitář, nebo nech aplikaci založit soukromý
`gitlab-todo-data`. Pořadí, vazby, odklady, cizí položky v timeline a poznámky se pak ukládají do souboru
`gitlab-todo.json` v tom repozitáři. Odesílají se při každém obnovení a chvíli po změně, commit vznikne jen
když se něco změnilo. Na dalším počítači zvol stejný GitLab a repozitář a data se sloučí s tím, co tam už je.
Když se mezitím změní na dvou místech, sloučí se: co změnilo jen jedno zařízení, platí, u poznámky upravené na
obou vyhraje novější. Tokeny ani nastavení se nesynchronizují. Repozitář nech soukromý, jsou v něm i poznámky.

## Přístup ke GitLabu

V nastavení přidáš URL GitLabu a Personal Access Token se scope `api`, který je potřeba na vytváření
větví a merge requestů.

## Licence

[MIT](LICENSE). Ikony linuxové verze jsou z [Lucide](https://lucide.dev) (ISC), viz
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
