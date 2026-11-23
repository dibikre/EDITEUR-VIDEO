# VideoSplitter

Application desktop Windows (Avalonia UI) pour découper des vidéos : lecteur intégré, vignettes de prévisualisation au survol de la barre de lecture, et timeline d'édition avec marqueurs de coupe. Export sans perte (rapide) ou précis à l'image (ré-encodage).

> Le détail fonctionnel et technique est dans [SPEC.md](SPEC.md).

## Stack

| Rôle | Technologie |
|---|---|
| Runtime | .NET 10 (C#) |
| Interface | Avalonia UI 11.3.22 (`FluentTheme`, thème sombre) |
| Lecteur | LibVLCSharp.Avalonia + VideoLAN.LibVLC.Windows |
| Traitement vidéo | FFmpeg / FFprobe (processus externes) via CliWrap |
| Architecture | MVVM (CommunityToolkit.Mvvm) + Microsoft.Extensions.DependencyInjection |

Plateforme : **Windows x64 uniquement** (LibVLC exige du 64 bits).

## Prérequis

- Windows 10/11 x64
- [.NET SDK 10](https://dotnet.microsoft.com/download) (`dotnet --version` doit afficher 10.x)
- Git
- (Optionnel) Visual Studio 2022 avec la charge de travail « Développement .NET Desktop », ou VS Code avec C# Dev Kit

Il n'est **pas nécessaire** d'installer VLC : les DLL natives sont fournies par le package NuGet `VideoLAN.LibVLC.Windows`.

## Installation

```powershell
git clone https://github.com/dibikre/EDITEUR-VIDEO.git
cd EDITEUR-VIDEO

# Télécharge ffmpeg.exe et ffprobe.exe dans src/VideoSplitter/Assets/ffmpeg
powershell -ExecutionPolicy Bypass -File scripts\fetch-ffmpeg.ps1

dotnet build
```

Les binaires FFmpeg ne sont **pas versionnés** dans Git (trop lourds). Le script les télécharge depuis les builds GPL de [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) (inclut `libx264` pour le mode précis). Le téléchargement (environ 150-200 Mo) peut être repris en relançant le script.

## Lancer l'application

```powershell
dotnet run --project src\VideoSplitter
```

## Packages NuGet

| Package | Version |
|---|---|
| Avalonia | 11.3.22 |
| Avalonia.Desktop | 11.3.22 |
| Avalonia.Themes.Fluent | 11.3.22 |
| LibVLCSharp.Avalonia | 3.10.2 |
| VideoLAN.LibVLC.Windows | 3.0.24 |
| CliWrap | 3.10.5 |
| CommunityToolkit.Mvvm | 8.4.2 |
| Microsoft.Extensions.DependencyInjection | 11.0.0 |

## Structure du projet

```
VideoSplitter/
  README.md
  SPEC.md
  scripts/fetch-ffmpeg.ps1
  src/VideoSplitter/
    Program.cs            point d'entrée Avalonia Desktop
    App.axaml(.cs)        composition root DI, Core.Initialize, thème
    Assets/ffmpeg/        binaires FFmpeg (non versionnés)
    Models/               MediaInfo, Segment, SpriteSet, ExportModels
    Editing/              SegmentEditor, KeyframeSnapper, EditHistory
    Export/               FfmpegArgsBuilder, ExportPlanner, CutAccuracy
    Services/             FFmpeg, vignettes, export, cache, logs
    ViewModels/           Main, Player, Timeline, Edit, Export
    Controls/             TimelineControl, TimelineView, SeekBar, ThumbnailHoverPopup
    Converters/           TimeSpan, EnumBoolean, StatusVisualConverters
    Views/                MainWindow, ExportConflictDialog, FastCutWarningDialog, MessageDialog
    Themes/               Dark.axaml
```

## Dépannage

- **`dotnet` introuvable ou erreur sur `#`** : utiliser PowerShell (invite `PS`), pas `cmd.exe`, et ne pas copier les commentaires `# ...` des commandes.
- **Le lecteur plante au démarrage** : vérifier que le projet est bien compilé en x64 (`PlatformTarget` = `x64` et `RuntimeIdentifier` = `win-x64` dans le `.csproj`).
- **Erreurs de fichiers verrouillés pendant le build** : ne pas placer le projet dans un dossier synchronisé par OneDrive (par exemple `Pictures` ou `Documents`). Préférer un dossier comme `C:\Dev`.
- **`ffmpeg.exe` introuvable à l'exécution** : relancer `scripts\fetch-ffmpeg.ps1`, puis `dotnet build` pour recopier les binaires dans le dossier de sortie.
- **Téléchargement FFmpeg très lent** : relancer la même commande, `curl` reprend là où il s'est arrêté.

## Licence

À définir. Note : FFmpeg (build GPL) est soumis à sa propre licence si l'application est redistribuée avec ses binaires.
