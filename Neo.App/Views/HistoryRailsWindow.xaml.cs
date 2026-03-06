using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Neo.App
{
    public partial class HistoryRailsWindow : Window
    {
        public HistoryRailsWindow(UndoRedoManager history)
        {
            InitializeComponent();

            DataContext = new HistoryRailsViewModel(history);
            Closed += (_, _) => (DataContext as System.IDisposable)?.Dispose();

            Loaded += (_, _) =>
            {
                if (DataContext is HistoryRailsViewModel vm)
                    vm.Selected = vm.Current;

                Graph.Focus();
            };

            Graph.NodeActivated += (_, e) =>
            {
                if (DataContext is HistoryRailsViewModel vm)
                    vm.Selected = e.Node;

                TryAcceptAndClose();
            };
        }

        public HistoryNode? SelectedNode
        {
            get
            {
                if (DataContext is not HistoryRailsViewModel vm)
                    return null;

                return vm.Selected;
            }
        }

        private void Checkout_Click(object sender, RoutedEventArgs e) => TryAcceptAndClose();

        private void HistoryRailsWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            DialogResult = false;
            Close();
        }

        private void TryAcceptAndClose()
        {
            if (SelectedNode == null)
                return;

            DialogResult = true;
            Close();
        }
    }
}
