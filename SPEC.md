# SPEC - VideoSplitter

Application desktop Windows pour découper (splitter) des vidéos, avec lecteur intégré, vignettes de prévisualisation au survol et timeline d'édition.

> Ce document est la source de vérité. En cas de doute, suivre ce document plutôt que d'improviser. Si un point manque ou est contradictoire, le signaler avant de coder.

---

## 1. Objectif

Permettre à l'utilisateur de :
1. Ouvrir une vidéo (n'importe quel codec courant : H.264, HEVC, AV1, ProRes, MKV, MP4, MOV, AVI, WebM).
2. La lire dans un lecteur intégré, avec une barre de lecture qui affiche **une vignette de prévisualisation au survol**.
3. Placer des points de coupe (split) et gérer les segments dans une **timeline d'édition située sous le lecteur**.
4. Exporter chaque segment dans un fichier séparé, soit **sans perte et quasi instantané**, soit **précis à l'image** (ré-encodage).

L'application doit rester fluide (aucun gel de l'interface), même sur des vidéos de 2 h en 4K.

---

## 2. Stack imposée (ne pas en changer)

| Rôle | Choix |
|---|---|
| Runtime | .NET 10, C# (Nullable activé, ImplicitUsings activé) |
| UI | Avalonia UI 11.3.22 (`FluentTheme`), thème sombre, interface en **français** |
| Lecteur | LibVLCSharp.Avalonia 3.10.2 + VideoLAN.LibVLC.Windows 3.0.24 |
| Traitement vidéo | `ffmpeg.exe` et `ffprobe.exe` en processus externes, pilotés par CliWrap 3.10.5 |
| Architecture | MVVM avec CommunityToolkit.Mvvm 8.4.2 |
| Injection de dépendances | Microsoft.Extensions.DependencyInjection |
| Plateforme | Windows x64 uniquement |

Les packages NuGet sont **déjà installés** dans `src/VideoSplitter/VideoSplitter.csproj`.
**Ne pas ajouter de package NuGet sans le signaler explicitement** et justifier pourquoi.

Chemin des binaires FFmpeg à l'exécution : `Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe")` (idem `ffprobe.exe`). Ils sont copiés par le `.csproj` dans le dossier de sortie.

---

## 3. Règles non négociables

1. **Aucune opération longue sur le thread UI.** Tout appel à FFmpeg/FFprobe, tout accès disque lourd, tout calcul important : `async`/`await` avec `CancellationToken`.
2. **FFmpeg tourne toujours dans un processus séparé** (CliWrap). Si FFmpeg plante, l'application ne doit jamais planter.
3. **Aucun décodage de vidéo au survol.** Les vignettes sont pré-générées (sprite sheets) et chargées en mémoire.
4. **Aucun contrôle par-dessus la vidéo** (problème d'airspace avec le handle natif LibVLC dans `VideoView`). Tous les contrôles sont placés **sous** la zone vidéo. Seule exception autorisée : les `Popup` natifs (`ShouldUseOverlayLayer = false`).
5. **Un seul `LibVLC` et un seul `MediaPlayer`** pour toute l'application, créés au démarrage (`Core.Initialize()` dans `App.OnFrameworkInitializationCompleted`), libérés proprement à la fermeture (`desktop.Exit`).
6. **Jamais de `.Result`, `.Wait()` ni `Thread.Sleep`** dans le code applicatif.
7. **Gestion d'erreurs explicite** pour chaque appel FFmpeg : code de sortie non nul, fichier introuvable, annulation, disque plein, fichier de sortie déjà existant. Message clair en français pour l'utilisateur, détails techniques dans le journal.
8. **Jamais plus de 2 exports FFmpeg simultanés.**
9. Pas de code mort, pas de `// TODO`, pas de méthodes vides : chaque fichier livré doit compiler et fonctionner.

---

## 4. Interface (layout)

Fenêtre unique redimensionnable (taille minimale 1000x650), dark theme.

```
+--------------------------------------------------------------+
| [Ouvrir]  [Exporter]  Mode: (o) Rapide  ( ) Précis           |
+--------------------------------------------------------------+
|                                                              |
|                     LECTEUR (LibVLC)                         |
|                                                              |
+--------------------------------------------------------------+
| [<<] [Lecture/Pause] [>>]  00:12:34 / 01:45:00  Vol  Vitesse |
| [========|==============  BARRE DE LECTURE  ================] |
|      (vignette + timecode au survol, dans un Popup)          |
+--------------------------------------------------------------+
| Outils: [Split S] [In I] [Out O] [Supprimer] [Zoom - +]      |
| TIMELINE: filmstrip + marqueurs + segments + playhead        |
+--------------------------------------------------------------+
| Liste des segments | Options d'export | Progression          |
+--------------------------------------------------------------+
```

Disposition suggérée : `Grid` à lignes `*` (lecteur), `Auto` (contrôles), `Auto` (timeline, hauteur environ 120 px), `Auto` (panneau du bas).

---

## 5. Fonctionnalités détaillées

### 5.1 Ouverture
- Bouton « Ouvrir » (`StorageProvider.OpenFilePickerAsync`) et glisser-déposer d'un fichier sur la fenêtre.
- Au chargement : `ffprobe` (durée, résolution, fps, codecs, présence d'audio), lancement de la lecture en pause sur la première image, déclenchement en arrière-plan de la génération des vignettes et de la liste des keyframes.
- Le titre de la fenêtre affiche le nom du fichier.

### 5.2 Lecteur
- Lecture / pause, seek, volume, mute, vitesse (0.5x, 1x, 1.5x, 2x).
- Avance/recul image par image (`MediaPlayer.NextFrame()` en pause, et recul par seek de 1/fps).
- Décodage matériel activé (option LibVLC `--avcodec-hw=any`).
- Pendant le glissement de la barre de lecture, limiter la fréquence des seeks (throttle d'environ 50 ms) pour éviter la saturation.
- Raccourcis : `Espace` lecture/pause, `←`/`→` image par image, `Shift+←`/`Shift+→` saut de 5 s, `Home`/`End`.

### 5.3 Vignettes au survol (fonctionnalité clé)
Sur la **barre de lecture** et sur la **timeline** :
- Au survol, afficher un `Popup` natif (`ShouldUseOverlayLayer = false`, `IsLightDismissEnabled = false`) au-dessus du curseur, contenant la vignette correspondante et le timecode.
- Le popup suit la souris, reste dans les limites de l'écran, et disparaît à la sortie sans capturer les clics.
- Calcul : `t = (x / largeur) * durée` ; `index = floor(t / intervalle)` ; feuille = `index / (cols*lignes)` ; cellule = `index % (cols*lignes)`.
- Affichage via `CroppedBitmap` sur la feuille `Bitmap` déjà chargée en mémoire. Aucun accès disque ni décodage au survol.
- Si la vignette n'est pas encore générée : afficher uniquement le timecode.

### 5.4 Génération des sprite sheets
- Largeur d'une vignette : 160 px, hauteur calculée selon le ratio (valeur paire).
- Grille 10 colonnes x 10 lignes = 100 vignettes par feuille. Fichiers : `sprite_000.jpg`, `sprite_001.jpg`, ...
- Intervalle adaptatif : `intervalle = max(1, ceil(durée / 600))` secondes (maximum environ 600 vignettes par vidéo).
- Commande de base :
  ```
  ffmpeg -hide_banner -y -hwaccel auto -i "<in>" -an -sn -dn
         -vf "fps=1/<intervalle>,scale=160:-2,tile=10x10"
         -q:v 5 -progress pipe:1 -nostats "<cache>/sprite_%03d.jpg"
  ```
- Pour les vidéos longues (> 10 min), ajouter `-skip_frame nokey` avant `-i` (précision approximative sur les keyframes acceptée pour les vignettes).
- Un fichier `meta.json` décrit : `interval`, `thumbWidth`, `thumbHeight`, `cols`, `rows`, `count`, `duration`.
- Les feuilles sont utilisables dès qu'elles sont écrites (génération progressive). Progression via `-progress pipe:1`.
- **Cache disque** : `%LOCALAPPDATA%\VideoSplitter\cache\<clé>\` où la clé est un hash (SHA-1) de `chemin complet + taille + date de dernière modification` (ne **jamais** hasher le contenu entier du fichier). Si le cache existe et que `meta.json` est valide, ne rien régénérer. Prévoir un nettoyage du cache (suppression des dossiers non utilisés depuis plus de 30 jours, au démarrage, en arrière-plan).

### 5.5 Timeline d'édition
Contrôle personnalisé `TimelineControl` (un `Control` Avalonia avec `Render(DrawingContext)`, pas une pile de dizaines de `Border`) :
- **Filmstrip** : vignettes issues des sprite sheets, dessinées uniquement sur la **portion visible** (virtualisation) via `DrawingContext.DrawImage`.
- **Playhead** : ligne verticale suivant la lecture (extrapolation fluide via `TopLevel.RequestAnimationFrame`), déplaçable par clic/glisser.
- **Marqueurs de coupe** : lignes verticales sur les points de split, déplaçables à la souris.
- **Segments** : zones entre deux marqueurs, couleurs alternées, segment sélectionné mis en évidence, segment désactivé grisé (exclu de l'export).
- **Zoom** : `Ctrl + molette` (centré sur le curseur), boutons +/-, bouton « Ajuster à la fenêtre ». **Défilement horizontal** : molette simple ou barre de défilement.
- **Règle de temps** (graduations adaptées au niveau de zoom).
- Option « Aimanter aux keyframes » : le placement des marqueurs se cale sur la keyframe la plus proche (liste obtenue via `ffprobe`, voir 7).
- Rendu optimisé (ressources et `FormattedText` mis en cache, invalidation groupée à priorité `Render`).

### 5.6 Édition (modèle de données)
`Segment { Guid Id; TimeSpan Start; TimeSpan End; bool Enabled; string Name; }`

Opérations :
- **Split** (`S`) : coupe le segment sous le playhead en deux.
- **In / Out** (`I` / `O`) : agissent sur le segment contenant le playhead. Si ce n'est pas le premier/dernier, déplacent la frontière avec le voisin vers le playhead ; si c'est le premier/dernier, coupent au playhead et désactivent la partie extérieure (rognage).
- **Supprimer un marqueur** : fusionne les deux segments adjacents.
- **Activer/désactiver** un segment (`E` ou case à cocher dans la liste).
- **Redimensionner** un segment en glissant un marqueur.
- **Annuler / Rétablir** (`Ctrl+Z` / `Ctrl+Y` / `Ctrl+Shift+Z`) : pile de **snapshots immuables** de la liste de segments (`EditHistory`, limite 200) pour toutes les opérations ci-dessus.
- Invariants : segments contigus, triés, sans chevauchement, durée minimale 0,1 s, le premier commence à 0 et le dernier finit à la durée totale.

### 5.7 Panneau du bas
- **Liste des segments** (`ListBox` en grille) : numéro, nom éditable, début, fin, durée, case « inclus », indicateur « début sur keyframe ». Cliquer un segment le sélectionne et déplace le playhead à son début.
- **Options d'export** : dossier de sortie, modèle de nom (`{nom}_part{NN}`, extension identique à la source par défaut), mode Rapide/Précis, qualité (CRF 18 par défaut) en mode précis, encodeur (Auto / CPU / NVENC / QSV / AMF).
- **Progression** : barre par tâche, barre globale, bouton Annuler, bouton « Ouvrir le dossier » en fin d'export.

---

## 6. Export

Deux modes :

**Rapide (sans perte, défaut)**
```
ffmpeg -hide_banner -nostdin -y -ss <début> -i "<in>" -t <durée>
       -map 0:v:0 -map 0:a? -sn -dn -map_chapters -1
       -c copy -avoid_negative_ts make_zero [-movflags +faststart]
       -progress pipe:1 -nostats "<out.part.ext>"
```
- Quasi instantané (limité par le disque). La coupe tombe sur la keyframe précédente : afficher un avertissement si le point de coupe n'est pas sur une keyframe, avec proposition de basculer en mode Précis (pour ces segments ou pour tout l'export).

**Précis (ré-encodage, précis à l'image)**
```
ffmpeg -hide_banner -nostdin -y -ss <début> -i "<in>" -t <durée>
       -map 0:v:0 -map 0:a? -sn -dn -map_chapters -1
       -pix_fmt yuv420p -c:v <encodeur> <options_qualité>
       -c:a aac -b:a 192k [-movflags +faststart]
       -progress pipe:1 -nostats "<out.part.ext>"
```
- Encodeurs supportés : CPU (`libx264`), NVENC (`h264_nvenc`), QSV (`h264_qsv`), AMF (`h264_amf`). Détection séquentielle au démarrage (en arrière-plan) via `ffmpeg -hide_banner -encoders` puis test réel d'1 s (`lavfi testsrc2`) avec timeout de 10 s. Repli automatique sur CPU (`libx264`) si le mode `Auto` a choisi un encodeur matériel qui échoue au runtime.

Commun aux deux modes :
- Sélection de pistes fixe : première piste vidéo (`0:v:0`) et toutes les pistes audio (`0:a?`) ; sous-titres (`-sn`), pistes de données (`-dn`) et chapitres (`-map_chapters -1`) exclus.
- `-movflags +faststart` ajouté pour les conteneurs `.mp4`, `.m4v` et `.mov`.
- Progression réelle via `-progress pipe:1` (`out_time_us=` et `progress=end`). **Ne pas parser stderr pour la progression.**
- Parallélisme : 2 jobs maximum en mode Rapide et en mode Précis matériel ; 1 seul job en mode Précis CPU (`libx264`).
- Annulation propre (tuer le processus et supprimer le fichier `.part`).
- Ne jamais écraser un fichier existant sans demander (proposer : remplacer tout / renommer automatiquement / ignorer / annuler).
- Écrire d'abord dans `<nom>.part<ext>` puis renommer en nom final à la fin.
- Rapport final : nombre de segments exportés, erreurs, ignorés et annulés.

---

## 7. Services (contrats)

```csharp
public sealed record MediaInfo(
    string Path, TimeSpan Duration, int Width, int Height,
    double Fps, string VideoCodec, string? AudioCodec, bool HasAudio,
    TimeSpan StartTime = default);

public sealed record SpriteSet(
    string Directory, double IntervalSeconds, int ThumbWidth, int ThumbHeight,
    int Columns, int Rows, int Count, int SheetCount);

public enum ExportMode { Fast, Precise }
public enum EncoderKind { Cpu, Nvenc, Qsv, Amf }
public enum EncoderSelection { Auto, Cpu, Nvenc, Qsv, Amf }
public enum ConflictPolicy { Overwrite, AutoRename, Skip }
public enum ExportOutcome { Success, Failed, Cancelled, Skipped }

public sealed record EncoderCapabilities(IReadOnlySet<EncoderKind> Available);

public sealed record ExportJob(
    MediaInfo Media, Segment Segment, int Index, int Total,
    string OutputPath, ExportMode Mode, EncoderKind Encoder, int Crf,
    bool AllowCpuFallback = false, bool IsSkipped = false);

public sealed record ExportProgress(Guid SegmentId, double Fraction, TimeSpan Elapsed);

public sealed record ExportResult(
    ExportOutcome Outcome, string? ErrorMessage, string? TechnicalDetails,
    string? FinalPath, EncoderKind? UsedEncoder, TimeSpan Elapsed);

public interface IFfmpegService
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct);
    // Keyframes via : ffprobe -v error -select_streams v:0
    //   -show_entries packet=pts_time,flags -of csv=p=0 "<in>"   (lignes dont flags contient 'K')
    Task<IReadOnlyList<double>> GetKeyframesAsync(string path, MediaInfo media, CancellationToken ct);
    Task<EncoderCapabilities> DetectEncodersAsync(CancellationToken ct);
}

public interface IThumbnailService
{
    Task<SpriteSet> GetOrCreateAsync(MediaInfo media, IProgress<double>? progress, CancellationToken ct);
}

public interface IExportService
{
    Task<ExportResult> ExportAsync(ExportJob job, IProgress<ExportProgress> progress, CancellationToken ct);
}

public interface ICacheService
{
    string GetCacheDirectory(string mediaPath);
    Task CleanupAsync(TimeSpan maxAge, CancellationToken ct);
}
```

Les services n'ont aucune dépendance vers l'UI. Les ViewModels ne manipulent pas de chemins FFmpeg ni de lignes de commande.

---

## 8. Arborescence cible

```
VideoSplitter/
  README.md
  SPEC.md
  scripts/fetch-ffmpeg.ps1
  src/VideoSplitter/
    VideoSplitter.csproj
    Program.cs                        (point d'entrée Avalonia Desktop)
    App.axaml / App.axaml.cs          (composition root DI, Core.Initialize, thème)
    Assets/ffmpeg/                    (binaires, non versionnés)
    Models/        MediaInfo, Segment, SpriteSet, ExportModels
    Editing/       SegmentEditor, KeyframeSnapper, EditHistory
    Export/        FfmpegArgsBuilder, ExportPlanner, CutAccuracy
    Services/      FfmpegService, ThumbnailService, ThumbnailStore, ExportService, ExportQueue, CacheService, FileLogger
    ViewModels/    MainViewModel, PlayerViewModel, TimelineViewModel, EditViewModel, ExportViewModel, ExportItemViewModel, SegmentRowViewModel
    Controls/      TimelineControl, TimelineView.axaml(.cs), SeekBar.axaml(.cs), ThumbnailHoverPopup
    Converters/    TimeSpanToStringConverter, EnumBooleanConverter, StatusVisualConverters
    Views/         MainWindow.axaml(.cs), ExportConflictDialog.axaml(.cs), FastCutWarningDialog.axaml(.cs), MessageDialog.axaml(.cs)
    Themes/        Dark.axaml
```

Namespace racine : `VideoSplitter`. Les `x:Class` AXAML doivent correspondre exactement aux namespaces C#.

La fenêtre principale est créée et assignée à `desktop.MainWindow` dans `App.OnFrameworkInitializationCompleted` via le conteneur DI.

---

## 9. Étapes de développement

Livrer **une étape à la fois**. Après chaque étape : arrêt, et attente de la validation de l'utilisateur (il compile, teste et renvoie les erreurs éventuelles).

1. **Squelette** : DI, thème sombre, `MainWindow` avec le layout vide, `Core.Initialize()`, journalisation simple dans un fichier (`%LOCALAPPDATA%\VideoSplitter\logs`).
2. **Lecteur** : ouverture, lecture/pause, seek, volume, vitesse, image par image, raccourcis, `FfmpegService.ProbeAsync`.
3. **Vignettes** : `CacheService`, `ThumbnailService`, `SeekBar` avec popup de vignette au survol.
4. **Timeline** : `TimelineControl` (filmstrip, playhead, zoom, défilement, règle), survol avec vignette.
5. **Édition** : modèle `Segment`, split/In/Out, marqueurs déplaçables, liste des segments, annuler/rétablir, keyframes et aimantation.
6. **Export** : `ExportService`, modes Rapide/Précis, file d'attente, progression, annulation, détection d'encodeur matériel.
7. **Finitions** : gestion d'erreurs, préférences (dernier dossier, mode), nettoyage du cache, icône, tests manuels de la liste ci-dessous.

---

## 10. Critères d'acceptation

- Une vidéo 4K HEVC s'ouvre et se lit **sans proxy** ni conversion.
- L'interface ne gèle jamais (ouverture, génération des vignettes, export en cours).
- La vignette au survol apparaît en moins de 50 ms une fois les feuilles chargées, sans à-coups.
- La réouverture d'une vidéo déjà traitée n'attend aucune régénération (cache).
- Sur une vidéo d'1 h, l'export de plusieurs segments en mode Rapide prend quelques secondes.
- Un fichier corrompu ou un FFmpeg qui échoue affiche un message clair et l'application reste utilisable.
- Annuler un export arrête FFmpeg et supprime le fichier partiel.
- Annuler/Rétablir fonctionne pour tout changement des segments.
- Fermer la fenêtre pendant une tâche termine proprement les processus FFmpeg enfants.

---

## 11. Consignes de livraison du code

1. Fournir le code **fichier par fichier**, avec le **chemin exact** (relatif à la racine du dépôt) en en-tête de chaque bloc, par exemple `// src/VideoSplitter/Services/FfmpegService.cs`.
2. Toujours fournir le **fichier complet**, jamais un extrait, un diff ou « le reste est inchangé ».
3. Indiquer à la fin de chaque étape la liste des fichiers créés/modifiés et les commandes à lancer (`dotnet build`, `dotnet run --project src\VideoSplitter`).
4. Le code doit compiler en .NET 10 avec Nullable activé et **zéro avertissement évitable**.
5. Vérifier la cohérence XAML : noms d'espaces de noms (`xmlns:`), `x:Class`, bindings (`{Binding ...}` vers des propriétés existantes avec `[ObservableProperty]` / `[RelayCommand]` de CommunityToolkit.Mvvm).
6. Commentaires concis uniquement là où la logique n'est pas évidente.
