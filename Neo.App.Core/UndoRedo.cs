using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Neo.App
{
    public sealed class HistoryNode
    {
        public Guid Id { get; } = Guid.NewGuid();

        public HistoryNode? Parent { get; internal set; }

        public List<HistoryNode> Children { get; } = new();

        public HistoryNode? LastVisitedChild { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.Now;

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public int CommitIndex { get; internal set; }

        public ApplicationStateMemento Snapshot { get; internal set; } = new();
    }

    public class UndoRedoManager : INotifyPropertyChanged
    {
        private readonly ApplicationState _appState;
        private HistoryNode _current;
        private int _nextCommitIndex = 1;

        public UndoRedoManager(ApplicationState appState)
        {
            _appState = appState ?? throw new ArgumentNullException(nameof(appState));

            Root = new HistoryNode
            {
                Parent = null,
                CommitIndex = 0,
                Timestamp = DateTime.Now,
                Title = "Session start",
                Description = string.Empty,
                Snapshot = _appState.CreateMemento(),
            };

            _current = Root;
        }

        public HistoryNode Root { get; private set; }

        public HistoryNode Current
        {
            get => _current;
            private set
            {
                if (ReferenceEquals(_current, value)) return;

                _current = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
                OnPropertyChanged(nameof(IsRedoAmbiguous));

                CurrentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool CanUndo => Current.Parent != null;

        public bool CanRedo => Current.Children.Count > 0;

        public bool IsRedoAmbiguous
        {
            get
            {
                if (Current.Children.Count <= 1) return false;

                var lvc = Current.LastVisitedChild;
                if (lvc != null && Current.Children.Contains(lvc))
                    return false;

                return true;
            }
        }

        public event EventHandler? CurrentChanged;
        public event EventHandler? GraphChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Clear()
        {
            ResetToCurrentState(title: "Session reset", description: string.Empty);
        }

        public void ResetToCurrentState(string title = "Session start", string description = "")
        {
            Root = new HistoryNode
            {
                Parent = null,
                CommitIndex = 0,
                Timestamp = DateTime.Now,
                Title = title ?? string.Empty,
                Description = description ?? string.Empty,
                Snapshot = _appState.CreateMemento(),
            };

            _nextCommitIndex = 1;
            Current = Root;

            OnPropertyChanged(nameof(Root));
            GraphChanged?.Invoke(this, EventArgs.Empty);
        }

        public HistoryNode? CommitChange(string title, string description, bool skipIfUnchanged = true)
        {
            var snapshot = _appState.CreateMemento();

            if (skipIfUnchanged && Current.Snapshot.StructurallyEquals(snapshot))
                return null;

            var newNode = new HistoryNode
            {
                Parent = Current,
                CommitIndex = _nextCommitIndex++,
                Timestamp = DateTime.Now,
                Title = title ?? string.Empty,
                Description = description ?? string.Empty,
                Snapshot = snapshot,
            };

            Current.Children.Add(newNode);

            // Neue Änderung nach Undo erzeugt einen Branch und wird zum deterministischen Redo-Ziel.
            Current.LastVisitedChild = newNode;

            Current = newNode;

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(IsRedoAmbiguous));
            GraphChanged?.Invoke(this, EventArgs.Empty);

            return newNode;
        }

        // Back-compat: früher "vor" einer Mutation aufgerufen; im neuen System bitte "nach" der Mutation verwenden.
        public void SaveState(bool skipIfUnchanged = true)
        {
            CommitChange(title: "Change", description: string.Empty, skipIfUnchanged: skipIfUnchanged);
        }

        public async Task<bool> Undo(Func<string, Task>? compileAndShow = null)
        {
            if (!CanUndo) return false;

            var old = Current;
            var parent = Current.Parent!;

            // Undo ist immer eindeutig: zum Parent; Parent merkt sich, welches Child verlassen wurde.
            parent.LastVisitedChild = old;

            Current = parent;
            ApplyState(Current);

            // Graph-Metadaten haben sich geändert (LastVisitedChild).
            GraphChanged?.Invoke(this, EventArgs.Empty);

            if (compileAndShow != null)
                await SafeCompileAndShowAsync(compileAndShow);

            return true;
        }

        public async Task<bool> Redo(Func<string, Task>? compileAndShow = null)
        {
            if (!CanRedo) return false;

            // Redo ist deterministisch: "mache das Undo rückgängig, das ich gerade getan habe".
            var target = GetDeterministicRedoTarget();
            if (target == null)
                return false;

            if (Current.LastVisitedChild == null && Current.Children.Count == 1)
                Current.LastVisitedChild = target;

            Current = target;
            ApplyState(Current);

            if (compileAndShow != null)
                await SafeCompileAndShowAsync(compileAndShow);

            return true;
        }

        public void Checkout(HistoryNode node)
        {
            if (node is null) throw new ArgumentNullException(nameof(node));

            // Navigations-Intention merken (hilft bei deterministischem Redo nach Checkout)
            if (node.Parent != null)
                node.Parent.LastVisitedChild = node;

            Current = node;
            ApplyState(Current);

            GraphChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task<bool> CheckoutAsync(HistoryNode node, Func<string, Task>? compileAndShow = null)
        {
            Checkout(node);

            if (compileAndShow != null)
                await SafeCompileAndShowAsync(compileAndShow);

            return true;
        }

        public HistoryNode? GetDeterministicRedoTarget()
        {
            var lvc = Current.LastVisitedChild;
            if (lvc != null && Current.Children.Contains(lvc))
                return lvc;

            if (Current.Children.Count == 1)
                return Current.Children[0];

            return null;
        }

        private void ApplyState(HistoryNode node)
        {
            _appState.RestoreFromMemento(node.Snapshot);
        }

        private async Task SafeCompileAndShowAsync(Func<string, Task> compileAndShow)
        {
            try { await compileAndShow(_appState.LastCode); }
            catch
            {
                // Absichtlich geschluckt: Navigation bleibt gültig.
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class ApplicationState
    {
        public string History { get; set; } = string.Empty;
        public string LastCode { get; set; } = string.Empty;

        // Laufender (mutable) Zustand deiner App:
        public List<string> NuGetDlls = new();
        public Dictionary<string, string> PackageVersions = new();

        /// <summary>
        /// Erstellt einen unveränderlichen Snapshot des aktuellen Zustands.
        /// </summary>
        public ApplicationStateMemento CreateMemento()
            => new ApplicationStateMemento
            {
                History = History,
                LastCode = LastCode,
                NuGetDlls = NuGetDlls.ToImmutableArray(),
                PackageVersions = PackageVersions.ToImmutableDictionary(StringComparer.Ordinal),
            };

        /// <summary>
        /// Stellt den Zustand aus einem Snapshot wieder her (kopiert in die mutable Felder).
        /// </summary>
        public void RestoreFromMemento(ApplicationStateMemento memento)
        {
            if (memento is null) throw new ArgumentNullException(nameof(memento));

            History = memento.History;
            LastCode = memento.LastCode;

            // Kopie in mutable Strukturen, damit Memento unverändert bleibt
            NuGetDlls = memento.NuGetDlls.IsDefault
                ? new List<string>()
                : memento.NuGetDlls.ToList();

            PackageVersions = memento.PackageVersions is null
                ? new Dictionary<string, string>()
                : memento.PackageVersions.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Unveränderlicher Snapshot. Verwendet immutable Collections.
    /// Record mit init-Properties erlaubt Objekt-Initialisierung und Value-Equality.
    /// </summary>
    public sealed record ApplicationStateMemento
    {
        public string History { get; init; } = string.Empty;
        public string LastCode { get; init; } = string.Empty;
        public ImmutableArray<string> NuGetDlls { get; init; } = ImmutableArray<string>.Empty;
        public ImmutableDictionary<string, string> PackageVersions { get; init; }
            = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

        /// <summary>
        /// Struktureller Vergleich (Listeninhalt/Dictionary-Paare), robust gegen verschiedene Instanzen.
        /// </summary>
        public bool StructurallyEquals(ApplicationStateMemento other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;

            if (!string.Equals(History, other.History, StringComparison.Ordinal)) return false;
            if (!string.Equals(LastCode, other.LastCode, StringComparison.Ordinal)) return false;

            // ImmutableArray: Elementweiser Vergleich
            if (NuGetDlls.Length != other.NuGetDlls.Length) return false;
            for (int i = 0; i < NuGetDlls.Length; i++)
                if (!string.Equals(NuGetDlls[i], other.NuGetDlls[i], StringComparison.Ordinal))
                    return false;

            // Dictionary: gleicher Umfang + gleiche Key/Value-Paare (Ordinal)
            if (PackageVersions.Count != other.PackageVersions.Count) return false;
            foreach (var kv in PackageVersions)
            {
                if (!other.PackageVersions.TryGetValue(kv.Key, out var v)) return false;
                if (!string.Equals(kv.Value, v, StringComparison.Ordinal)) return false;
            }

            return true;
        }
    }
}

