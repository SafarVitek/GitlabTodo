{
  lib,
  buildDotnetModule,
  dotnetCorePackages,
  copyDesktopItems,
  makeDesktopItem,
  fontconfig,
  libGL,
  libice,
  libsm,
  libx11,
  libxcursor,
  libxext,
  libxi,
  libxrandr,
}:

buildDotnetModule {
  pname = "gitlab-todo";
  version = "0.1.0";

  src = lib.fileset.toSource {
    root = ./.;
    fileset = lib.fileset.unions [
      ./GitLabTodo
      ./GitLabTodo.Core
    ];
  };

  projectFile = "GitLabTodo/GitLabTodo.csproj";
  # Regenerate after changing packages, see linux/README.md.
  nugetDeps = ./deps.json;

  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.runtime_10_0;

  executables = [ "gitlab-todo" ];

  # Avalonia loads these by name at runtime: X11 for windows, fontconfig for Skia's text.
  runtimeDeps = [
    fontconfig
    libGL
    libice
    libsm
    libx11
    libxcursor
    libxext
    libxi
    libxrandr
  ];

  # No build telemetry from Avalonia's build tasks.
  env.AVALONIA_TELEMETRY_OPTOUT = "1";

  nativeBuildInputs = [ copyDesktopItems ];

  desktopItems = [
    (makeDesktopItem {
      name = "gitlab-todo";
      desktopName = "GitLab Todo";
      comment = "Issues a merge requesty z GitLabu v pořadí, ve kterém na nich chceš pracovat";
      exec = "gitlab-todo";
      icon = "gitlab-todo";
      categories = [
        "Development"
        "ProjectManagement"
      ];
      keywords = [
        "gitlab"
        "todo"
        "merge request"
      ];
    })
  ];

  postInstall = ''
    install -Dm644 GitLabTodo/Assets/icon.png $out/share/icons/hicolor/512x512/apps/gitlab-todo.png
  '';

  meta = {
    description = "Issues and merge requests from GitLab in the order you want to work on them, with a timeline of what waits on what";
    homepage = "https://github.com/SafarVitek/GitlabTodo";
    license = lib.licenses.mit;
    mainProgram = "gitlab-todo";
    platforms = lib.platforms.linux;
  };
}
