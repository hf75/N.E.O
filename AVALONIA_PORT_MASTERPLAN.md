# Masterplan: Neo.App Avalonia-Port

## Architektur-Ziel

```
neo.sln (erweitert)
├── Neo.App.Core          (NEU - net9.0, ClassLib, plattformunabhängige Business-Logik)
├── Neo.App               (WPF, bleibt unangetastet, referenziert Core)
├── Neo.App.Avalonia      (NEU - net9.0, Avalonia 11.3.9 Host)
├── Neo.Agents.*          (shared, bereits cross-platform)
├── Neo.IPC               (shared, bereits cross-platform)
├── Neo.AssemblyForge     (shared, bereits cross-platform)
├── Neo.Shared            (shared, bereits cross-platform)
├── Neo.PluginWindowWPF   (unverändert)
└── Neo.PluginWindowAvalonia (unverändert, dient als Referenz)
```

**Leitprinzip:** Jede Phase lässt Neo.App (WPF) vollständig lauffähig. Erst extrahieren, dann portieren.

---

## Dependency-Graph

```
Phase 1 (Core-Extraktion)
   │
   ▼
Phase 2 (IMainView-Abstraktion)
   │
   ├────► Phase 3 (IChildProcessService-Abstraktion)  ─┐
   │                                                     │  parallel
   ▼                                                     │  möglich
Phase 4 (XAML-Portierung)  ◄─────────────────────────────┘
   │
   ▼
Phase 5 (Avalonia ChildProcessService)
   │
   ▼
Phase 6 (Plattform-Services)
   │
   ▼
Phase 7 (Integration & Testing)
```

---

## Phase 1: Neo.App.Core erstellen — Pure Datentypen extrahieren

**Ziel:** Shared Library mit risikofrei extrahierbaren Typen (null UI-Abhängigkeiten).
**Aufwand:** 2–3 Tage | **Risiko:** Niedrig

### Deliverables
1. Neues Projekt `Neo.App.Core/Neo.App.Core.csproj` (net9.0)
2. Referenzen: Neo.AssemblyForge, Neo.Agents.Core, Neo.IPC, Newtonsoft.Json
3. `<ProjectReference>` von Neo.App → Neo.App.Core

### Dateien — Direktes Verschieben (keine Änderungen nötig)

| Datei | Zeilen | Inhalt |
|-------|--------|--------|
| `EnumTypes.cs` | 64 | BubbleType, CrashReason, CreationMode, CrossPlatformExport |
| `StructuredResponse.cs` | 32 | Pure data, nur Newtonsoft.Json |
| `CrossplatformSettings.cs` | 14 | Pure record |
| `ChatMessage.cs` | 43 | Pure data class |
| `Misc.cs` | 16 | IStatefulControl interface |
| `ResxData.cs` | — | Pure data |
| `PatchOperation.cs` | 39 | Pure data/logic |
| `UndoRedo.cs` | — | HistoryNode, UndoRedoManager, ApplicationState |
| `UnifiedDiffPatcher.cs` | 375 | Pure text processing |
| `UnifiedDiffGenerator.cs` | 266 | Pure text processing |
| `LoopUnroller.cs` | 141 | Pure text processing |
| `HTMLHistoryWrapper.cs` | 234 | Pure string building |
| `JsonSchemata.cs` | 47 | Pure constants |
| `AISystemMessages.cs` | 129 | Pure string templates |
| `FileHelper.cs` | 172 | Pure System.IO |
| `EmbeddedResourceReader.cs` | 111 | Pure reflection |
| `DotNetRuntimeFinder.cs` | 165 | Pure System.IO/Process |
| `RoslynCodeAnalyzer.cs` | 201 | Pure Roslyn |
| `PipeGuard.cs` | — | Pure sync primitive |
| `PipeServer.cs` | 222 | Pure networking |

### Dateien — Verschieben mit Interface-Extraktion

| Datei | Zeilen | Anpassung |
|-------|--------|-----------|
| `SettingsService.cs` + `SettingsModel` | 355 | INotifyPropertyChanged only |
| `AppImportService.cs` | — | IAppImportService + Impl |
| `AppExportService.cs` | 311 | IAppExportService, Roslyn/File I/O |
| `AppInstallerService.cs` | — | Verifizieren, dann verschieben |
| `CompilationService.cs` | 73 | ICompilationService + Impl |
| `NuGetPackageService.cs` | 80 | INuGetPackageService + Impl |
| `NuGetBinaryCopier.cs` | 391 | Pure file I/O |
| `ModelListService.cs` | 191 | Pure HTTP |
| `PatchReviewService.cs` | 507 | Verifizieren auf WPF-Freiheit |

### Namespace-Strategie
`namespace Neo.App` bleibt in allen verschobenen Dateien — vermeidet massive `using`-Änderungen.

### Testkriterium
Neo.App (WPF) baut und läuft identisch. Core targeting net9.0 (nicht windows) = Compiler fängt WPF-Leaks.

---

## Phase 2: IMainView-Abstraktion — AppController entkoppeln

**Ziel:** Direkte Kopplung AppController↔MainWindow durch Interface brechen.
**Aufwand:** 3–4 Tage | **Risiko:** Mittel

### Deliverables
1. `Neo.App.Core/IMainView.cs` — extrahiert aus 27 `_view.`-Aufrufstellen
2. `Neo.App.Core/IAppLogger.cs` — Interface verschieben
3. AppController.cs nach Core (abhängig nur von Interfaces)

### IMainView Interface-Design

```csharp
public interface IMainView
{
    // UI-Zustand
    Task SetUiBusyState(bool isBusy, string? message = null, bool showCancel = false, bool showOverlay = true);
    void ShowEmptyContent();
    void ShowFrostedSnapshot(object snapshot);
    Task HideFrostedSnapshotAsync();
    void HideFrostedSnapshot();

    // Prompt-Management
    string PromptText { get; set; }
    void PromptToNextLine();
    void ClearPrompt();
    void FocusPrompt();
    void EnablePrompt(bool enable);

    // Fenster-Operationen
    void ActivateWindow();
    void ResetButtonMenu();

    // Repair-Overlay
    void ShowRepairOverlay();
    void HideRepairOverlay();

    // Dialoge
    CrashDialogResult ShowCrashDialog();
    PatchReviewDecision ShowPatchReviewDialog(string originalCode, string patchedCode, string diff);

    // Wait-Indikator
    void SetWaitIndicatorStatus(string text);

    // Threading
    void InvokeOnUIThread(Action action);
    T InvokeOnUIThread<T>(Func<T> func);
    Task InvokeOnUIThreadAsync(Action action);

    // Child-Process-Host (abstrahiert)
    object GetHostContainer();
    object GetParentWindow();
}
```

### Refactoring-Schritte
- `private readonly MainWindow _view;` → `private readonly IMainView _view;`
- `_view.Dispatcher.Invoke(...)` → `_view.InvokeOnUIThread(...)`
- `_view.txtPrompt.Text` → `_view.PromptText`
- `_view.RepairOverlay.Visibility` → `_view.ShowRepairOverlay()` / `HideRepairOverlay()`
- Designer-Window: hinter `IDesignerService` abstrahieren

### Testkriterium
MainWindow implementiert IMainView. AppController bekommt MainWindow als IMainView. Alles funktioniert identisch.

---

## Phase 3: IChildProcessService & ISandboxProvider abstrahieren

**Ziel:** ChildProcessService und Sandboxing plattformunabhängig machen.
**Aufwand:** 5–7 Tage | **Risiko:** Hoch

### Deliverables
1. `IChildProcessService` → Neo.App.Core (existiert bereits, anpassen)
2. `ISandboxProvider` → Neo.App.Core (neu)
3. WPF-Implementierungen bleiben in Neo.App

### Interface-Änderungen
- `WindowState` (WPF) → portables Enum `HostWindowState { Normal, Minimized, Maximized }`
- `BitmapSource` Screenshot → `byte[]?` (PNG bytes)
- `IntPtr GetChildHwnd()` → nur in Windows-spezifischem Sub-Interface

### ChildProcessService aufspalten

```
ChildProcessServiceBase (Core)        Win32ChildProcessHost (Neo.App)
├── Pipe-Kommunikation                ├── HWND-Embedding (SetParent)
├── Prozess-Lifecycle                 ├── MoveWindow/SetWindowPos
├── DLL-Streaming                     ├── Job Objects
└── State Management                  └── AppContainer-Integration
```

### Sandbox-Abstraktion

| Plattform | Implementierung | Status |
|-----------|----------------|--------|
| Windows | AppContainer (bestehend) | Fertig |
| Linux | seccomp/bwrap | Zukunft (NullSandboxProvider initial) |
| macOS | App Sandbox | Zukunft (NullSandboxProvider initial) |

### Testkriterium
Neo.App (WPF) baut und läuft. ChildProcessService instanziiert durch Interface. Sandbox identisch.

---

## Phase 4: XAML-Views nach Avalonia portieren

**Ziel:** Alle 14 XAML-Dateien + Code-Behinds in Avalonia erstellen.
**Aufwand:** 8–12 Tage | **Risiko:** Mittel

### Projekt-Setup
- `Neo.App.Avalonia/Neo.App.Avalonia.csproj` (net9.0, Avalonia 11.3.9)
- Referenzen: Neo.App.Core, Neo.IPC, alle Neo.Agents-Projekte
- NuGet: `Markdown.Avalonia` (ersetzt MdXaml), `AvaloniaEdit` (ersetzt AvalonEdit)

### View-Portierung (nach Komplexität geordnet)

| # | WPF-Datei | Avalonia-Datei | Komplexität |
|---|-----------|----------------|-------------|
| 1 | `App.xaml` | `App.axaml` | Niedrig |
| 2 | `EmptyUserControl.xaml` (12 Z.) | `EmptyUserControl.axaml` | Trivial |
| 3 | `CrashDialog.xaml` (44 Z.) | `CrashDialog.axaml` | Niedrig |
| 4 | `SplashScreenWindow.xaml` (28 Z.) | `SplashScreenWindow.axaml` | Niedrig |
| 5 | `ApiKeySetupWindow.xaml` (177 Z.) | `ApiKeySetupWindow.axaml` | Niedrig |
| 6 | `PythonSetupWindow.xaml` (56 Z.) | `PythonSetupWindow.axaml` | Niedrig |
| 7 | `AiWaitIndicator.xaml` (270 Z.) | `AiWaitIndicator.axaml` | Niedrig-Mittel |
| 8 | `SettingsWindow.xaml` (132 Z.) | `SettingsWindow.axaml` | Mittel |
| 9 | `ProjectExportDialog.xaml` (192 Z.) | `ProjectExportDialog.axaml` | Mittel |
| 10 | `PatchReviewWindow.xaml` (68 Z.) | `PatchReviewWindow.axaml` | Mittel |
| 11 | `DesignerPropertiesWindow.xaml` (249 Z.) | `DesignerPropertiesWindow.axaml` | Mittel |
| 12 | `HistoryRailsWindow.xaml` (42 Z.) | `HistoryRailsWindow.axaml` | Mittel |
| 13 | `ChatView.xaml` (277 Z.) | `ChatView.axaml` | Mittel-Hoch |
| 14 | `MainWindow.xaml` (345 Z.) | `MainWindow.axaml` | Hoch |

### XAML-Übersetzungsregeln

| WPF | Avalonia |
|-----|----------|
| `xmlns="...microsoft.com/..."` | `xmlns="https://github.com/avaloniaui"` |
| `Visibility="Collapsed"` | `IsVisible="False"` |
| `ControlTemplate.Triggers` | `:pointerover`, `:pressed` Pseudoklassen |
| `BooleanToVisibilityConverter` | Direkt `IsVisible` binden |
| `Storyboard` in Triggers | `Avalonia.Animation` KeyFrames |
| `InputBindings` | `KeyBindings` |
| `FontFamily="Segoe UI"` | Cross-platform Fallback-Stack |

### RailsGraphControl → Avalonia

| WPF | Avalonia |
|-----|----------|
| `FrameworkElement` | `Control` |
| `OnRender(DrawingContext)` | `Render(DrawingContext)` |
| `DependencyProperty` | `StyledProperty` / `DirectProperty` |
| `MouseLeftButtonDown` | `PointerPressed` |
| `CaptureMouse()` | `Capture(PointerDevice)` |

### Bibliotheks-Ersetzungen

| WPF | Avalonia | Status |
|-----|----------|--------|
| MdXaml 1.27.0 | Markdown.Avalonia | Verfügbar |
| AvalonEdit (ICSharpCode) | AvaloniaEdit | Aktiv gepflegt |
| FlowDocumentScrollViewer | TextBlock + Inlines / Markdown-Control | Kein 1:1, Workaround |

### FullScreenManager (Avalonia-Version)

| WPF | Avalonia |
|-----|----------|
| `WindowStyle = WindowStyle.None` | `SystemDecorations = SystemDecorations.None` |
| `WindowState = Maximized` | `WindowState = Maximized` (identisch) |
| Win32 SetWindowLong | `ExtendClientAreaToDecorationsHint` |

### Testkriterium
Neo.App.Avalonia baut und startet. Jedes Fenster/Dialog rendert korrekt.

---

## Phase 5: Avalonia ChildProcessService implementieren

**Ziel:** Kernfluss funktionsfähig — Prompt → AI → Kompilierung → Child-Process → Live-UI.
**Aufwand:** 5–7 Tage | **Risiko:** Hoch

### Architektur-Entscheidung: Child-Window-Integration

| Ansatz | Pro | Contra |
|--------|-----|--------|
| **A: Synchronisiertes Fenster** (empfohlen) | Cross-platform, kein HWND nötig | Z-Order fragil, kann hinter andere Apps rutschen |
| **B: NativeControlHost** (Windows) | Echtes Embedding | Nur Windows, Avalonia-Interop weniger ausgereift |
| **Hybrid: B auf Windows, A auf Linux/macOS** | Best of both | Mehr Code zu pflegen |

### Fenster-Synchronisation (Ansatz A)

```
Host-Fenster Position/Größe ändert sich → IPC-Message → Child repositioniert
Host minimiert → IPC-Message → Child versteckt sich
Host aktiviert → IPC-Message → Child kommt nach vorne
```

### Prozess-Lifecycle

| Plattform | Mechanismus |
|-----------|-------------|
| Windows | Job Objects (bestehend) |
| Linux | `prctl(PR_SET_PDEATHSIG)` |
| macOS | `Process.Exited` Event + Kill |

### Testkriterium
Prompt eingeben → AI antwortet → Code kompiliert → Child-Prozess startet → UI erscheint → Fenster folgt dem Host.

---

## Phase 6: Plattform-Services und verbleibende Windows-Features

**Ziel:** Alle Windows-only Features mit Plattform-Abstraktion versorgen.
**Aufwand:** 4–5 Tage | **Risiko:** Mittel

| Feature | Windows | Linux/macOS | Ansatz |
|---------|---------|-------------|--------|
| Sandboxing | AppContainer | NullSandboxProvider | Graceful degradation |
| Win32IconInjector | Vestris.ResourceLib | Skip | Plattform-Check |
| Globale Hotkeys | RegisterHotKey | Avalonia KeyBindings | Plattform-spezifisch |
| Cursor-Manipulation | SetSystemCursor | `Cursor = None` | Avalonia API |
| Datei-Dialoge | Win32 OpenFileDialog | Avalonia StorageProvider | Cross-platform API |
| Registry-Zugriff | Microsoft.Win32 | Nicht benötigt | Feature-Flag |

### Testkriterium
Windows: alle bestehenden Features funktionieren. Linux/macOS: App startet, Sandbox graceful übersprungen, Export ohne Icon-Injection.

---

## Phase 7: Integration & Testing

**Ziel:** End-to-End-Verifikation, Performance, Feature-Parität.
**Aufwand:** 3–5 Tage | **Risiko:** Niedrig

### Test-Matrix

| Feature | Testfall |
|---------|----------|
| Prompt-Execution | Prompt → AI → Code → Child-UI |
| Undo/Redo | Mehrere Generierungen, Undo, Redo, Branch-Navigation |
| Import/Export | Export → .resx → Re-Import → Roundtrip |
| Settings | Provider/Modell wechseln, speichern/laden |
| Designer-Modus | Click-to-Edit, Property-Editing |
| Sandbox-Toggle | Ein/Aus, Child-Prozess-Verhalten |
| Fullscreen | F11, Content-Only-Mode |
| Crash-Recovery | Child-Prozess killen → Repair-Flow |
| Chat-History | Markdown-Rendering in Avalonia ChatView |
| History-Rails | Rails-Fenster, Graph-Navigation |
| Multi-Provider | Claude, GPT, Gemini, Ollama, LM Studio |

### Build-Konfiguration
- Solution-Konfigurationen: WPF-only, Avalonia-only, Both
- Optionaler Launch-Flag: `--avalonia` / `--wpf`

---

## Gesamtübersicht

| Phase | Beschreibung | Tage | Risiko |
|-------|-------------|------|--------|
| **1** | Neo.App.Core — Pure Typen extrahieren | 2–3 | Niedrig |
| **2** | IMainView — AppController entkoppeln | 3–4 | Mittel |
| **3** | IChildProcessService abstrahieren | 5–7 | Hoch |
| **4** | 14 XAML-Views nach Avalonia portieren | 8–12 | Mittel |
| **5** | Avalonia ChildProcessService | 5–7 | Hoch |
| **6** | Plattform-Services | 4–5 | Mittel |
| **7** | Integration & Testing | 3–5 | Niedrig |
| | **Gesamt** | **30–43** | |

---

## Dateien die NICHT nach Core wandern

Diese bleiben in Neo.App (WPF-only):
- `NativeMethods.cs` — 53 P/Invoke-Deklarationen
- `Sandboxing.cs` + `AppContainerProfile.cs` — Windows AppContainer
- `ChildProcessService.cs` — HWND-Embedding-Implementierung
- `FullScreenManager.cs` — WPF WindowStyle-Manipulation
- `Win32IconInjector.cs` — PE-Resource-Manipulation
- `InteropThread.cs` — WPF-Threading
- `RailsGraphControl.cs` — WPF FrameworkElement (Avalonia bekommt eigene)
- Alle 14 `.xaml` + `.xaml.cs` Dateien
- `App.xaml` + `App.xaml.cs`
- `IconLoader.cs` — System.Drawing/WPF-Imaging
