# GitLab Todo

Osobní seznam práce nad GitLabem. Ukazuje issue a merge requesty, které máš přiřazené, a merge requesty,
ve kterých jsi zmíněný, z jednoho nebo více GitLabů (i self-hosted). Pořadí, ve kterém na nich chceš
pracovat, si nastavíš přetahováním. Timeline pak ukáže, co přijde kdy a co na co čeká, i napříč projekty
a GitLaby. Pořadí, vazby a odklady se drží u tebe na počítači a do issue ani MR se nezapisují. Mezi
počítači je můžeš synchronizovat přes soukromý repozitář.

Aplikace je ve dvou verzích se stejnými funkcemi a stejným formátem dat:

| | macOS | Linux |
|---|---|---|
| Složka | [`macos/`](macos) | [`linux/`](linux) |
| Technologie | SwiftUI, WidgetKit | Avalonia (.NET) |
| Widget na ploše | ano | ne |
| Kde žije | ikona v menu baru + okno | ikona v systémové liště + okno |
| Instalace | ke stažení z [Releases](../../releases) | AppImage, .deb nebo Nix flake |
| Aktualizace | novou verzi stáhneš z Releases | AppImage sám z aplikace, jinak jak jsi instaloval |

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

Na [stránce posledního vydání](../../releases/latest) jsou tři možnosti (zatím jen pro x86-64):

- **AppImage**, jeden soubor, který se sám aktualizuje. Stáhni
  [`GitLabTodo-x86_64.AppImage`](../../releases/latest/download/GitLabTodo-x86_64.AppImage), ulož ho třeba
  do `~/Applications`, povol spouštění a spusť:

  ```sh
  chmod +x ~/Applications/GitLabTodo-x86_64.AppImage
  ~/Applications/GitLabTodo-x86_64.AppImage
  ```

  Při prvním spuštění se přidá do menu aplikací. Potřebuje FUSE, které běžné distribuce mají; když chybí,
  spusť ho s `--appimage-extract-and-run`.
- **Debian a Ubuntu**: [`gitlab-todo_amd64.deb`](../../releases/latest/download/gitlab-todo_amd64.deb),
  instalace `sudo apt install ./gitlab-todo_amd64.deb`. Novější verzi nainstaluješ stejně.
- **Nix** s flakes: `nix profile install github:SafarVitek/GitlabTodo`. Jen vyzkoušet bez instalace jde
  `nix run github:SafarVitek/GitlabTodo`, s ukázkovými daty `nix run github:SafarVitek/GitlabTodo -- --preview`.
  Na NixOS nebo v Home Manageru přidej flake do inputs a balíček `packages.${system}.default` (nebo overlay
  `overlays.default`) do svých balíčků.

Aplikace se spouští jako „GitLab Todo“ z menu aplikací, z .deb a Nixu i příkazem `gitlab-todo`. Po zavření
okna běží dál v systémové liště, na GNOME k tomu potřebuješ rozšíření AppIndicator. Víc v
[`linux/README.md`](linux/README.md).

### Aktualizace

AppImage se po spuštění a pak každých 12 hodin podívá na GitHub, jestli je novější verze. Když je, ukáže se
v postranním panelu tlačítko „Aktualizovat na …“ a v nastavení v sekci Aktualizace „Nainstalovat a
restartovat“. Stažený soubor se nainstaluje, jen když nese podpis vydání GitLab Todo (ECDSA P-256, klíč
v [`Updates.cs`](linux/GitLabTodo.Core/Updates.cs)), a nic se neinstaluje bez kliknutí. Kontrolu jde v
nastavení vypnout. Instalace z .deb a Nixu se aktualizují tak, jak byly nainstalované, aplikace jen ukáže
svou verzi. Stejně tak macOS verze, tu stáhneš znovu z Releases.

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

## Vydání nové verze

1. Zvyš verzi na třech místech: `linux/GitLabTodo/GitLabTodo.csproj`, `linux/package.nix` a
   `macos/project.yml`.
2. Commit, push, a tag se zprávou o tom, co je nového (zobrazí se ve vydání i v aplikaci u aktualizace):

   ```sh
   git tag -a v0.3.0 -m "Co je nového…"
   git push origin v0.3.0
   ```

Workflow [`release.yml`](.github/workflows/release.yml) sestaví AppImage, .deb a macOS aplikaci, AppImage
spustí na virtuálním displeji, .deb nainstaluje a spustí, AppImage podepíše a zveřejní vydání s
`latest.json`, ze kterého si aplikace aktualizaci bere. Podpisový klíč je v secretu `UPDATE_SIGNING_KEY`
repozitáře. Ručně spuštěné workflow (Actions → Release → Run workflow) všechno sestaví a otestuje, ale
nic nezveřejní.

## Licence

[MIT](LICENSE). Ikony linuxové verze jsou z [Lucide](https://lucide.dev) (ISC), písmo je
[IBM Plex Sans](https://github.com/IBM/plex) (SIL Open Font License), viz
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
