using System;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Neo.App
{
    public partial class HistoryRailsWindow : Window
    {
        private readonly HistoryRailsViewModel? _viewModel;

        public HistoryRailsWindow(UndoRedoManager history)
        {
            InitializeComponent();

            _viewModel = new HistoryRailsViewModel(history);
            DataContext = _viewModel;

            Closed += (_, _) => (_viewModel as IDisposable)?.Dispose();

            Opened += (_, _) =>
            {
                if (_viewModel != null)
                    _viewModel.Selected = _viewModel.Current;
            };
        }

        // Parameterless constructor for XAML designer
        public HistoryRailsWindow() : this(null!)
        {
        }

        public HistoryNode? SelectedNode => _viewModel?.Selected;

        private void Checkout_Click(object? sender, RoutedEventArgs e) => TryAcceptAndClose();

        private void HistoryRailsWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;

            Close(false);
        }

        private void TryAcceptAndClose()
        {
            if (SelectedNode == null) return;

            Close(true);
        }
    }
}
