
## Instalace

**Linux** (x86-64)

- **AppImage**, aktualizuje se sám: stáhni `GitLabTodo-x86_64.AppImage`, ulož ho třeba do `~/Applications`,
  povol spouštění (`chmod +x GitLabTodo-x86_64.AppImage`) a spusť. Při prvním spuštění se přidá do menu aplikací.
  Až vyjde nová verze, objeví se v aplikaci tlačítko „Aktualizovat na …“.
- **Debian a Ubuntu**: `sudo apt install ./gitlab-todo_amd64.deb`. Novější verzi nainstaluješ stejně.
- **Nix**: `nix profile install github:SafarVitek/GitlabTodo`, aktualizace přes `nix profile upgrade --all`.

**macOS** (15 a novější)

Stáhni `GitLabTodo-macOS.zip`, rozbal a přesuň `GitLabTodo.app` do `~/Applications` nebo `/Applications`.
Aplikace není notarizovaná, napoprvé ji otevři pravým tlačítkem → Otevřít, nebo spusť
`xattr -dr com.apple.quarantine ~/Applications/GitLabTodo.app`.

Kontrolní součty souborů jsou v `SHA256SUMS`.
