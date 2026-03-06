using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Neo.App
{
    public sealed class HistoryRailsViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly UndoRedoManager _history;
        private HistoryNode? _selected;
        private readonly EventHandler _currentChangedHandler;
        private readonly EventHandler _graphChangedHandler;

        public HistoryRailsViewModel(UndoRedoManager history)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _selected = _history.Current;

            _currentChangedHandler = (_, _) =>
            {
                OnPropertyChanged(nameof(Current));
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
                OnPropertyChanged(nameof(IsRedoAmbiguous));
            };

            _graphChangedHandler = (_, _) =>
            {
                OnPropertyChanged(nameof(Root));
                OnPropertyChanged(nameof(Current));
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
                OnPropertyChanged(nameof(IsRedoAmbiguous));

                Selected = _history.Current;
            };

            _history.CurrentChanged += _currentChangedHandler;
            _history.GraphChanged += _graphChangedHandler;
        }

        public HistoryNode Root => _history.Root;

        public HistoryNode Current => _history.Current;

        public bool CanUndo => _history.CanUndo;

        public bool CanRedo => _history.CanRedo;

        public bool IsRedoAmbiguous => _history.IsRedoAmbiguous;

        public HistoryNode? Selected
        {
            get => _selected;
            set
            {
                if (ReferenceEquals(_selected, value)) return;
                _selected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Dispose()
        {
            _history.CurrentChanged -= _currentChangedHandler;
            _history.GraphChanged -= _graphChangedHandler;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
