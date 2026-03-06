using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace Neo.App
{
    public partial class ProjectExportDialog : Window
    {
        // Ausgabe-Eigenschaften für den Aufrufer
        public string? BaseDirectory { get; private set; }
        public string? ProjectName { get; private set; }
        public string? SelectedCreationMode { get; private set; }
        public string? ExportIcoFullPath { get; private set; }

        public bool InstallDesktop { get; private set; }
        public bool InstallStartMenu { get; private set; }
        public bool RequiresExport { get; private set; }

        public string ProjectFullPath =>
            string.IsNullOrWhiteSpace(BasePathTextBox.Text) || string.IsNullOrWhiteSpace(ProjectNameTextBox.Text)
                ? string.Empty
                : System.IO.Path.Combine(BasePathTextBox.Text.Trim(), ProjectNameTextBox.Text.Trim());

        private bool _isInitializing;
        private MainWindow _mainWindow;

        public ProjectExportDialog(MainWindow mainWindow)
        {
            _isInitializing = true;
            InitializeComponent();
            _isInitializing = false;

            _mainWindow = mainWindow;
            UpdateHintAndValidate();
        }

        // -- Events --

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = "Select base directory",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    BasePathTextBox.Text = dlg.SelectedPath;
                }
            }
        }

        private void OnInputChanged(object? sender, EventArgs e)
        {
            if (_isInitializing) return;

            UpdateHintAndValidate();
        }

        private void OnInputChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            UpdateHintAndValidate();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ExportProject_Click(object sender, RoutedEventArgs e)
        {
            PerformSaveOrExport(cbInstallDesktop.IsChecked.GetValueOrDefault(), 
                cbInstallStartMenu.IsChecked.GetValueOrDefault(), true);
        }

        private void SaveAsButton_Click(object sender, RoutedEventArgs e)
        {
            PerformSaveOrExport(false, false, false);
        }

        private void PerformSaveOrExport(bool installDesktop, 
            bool installStartMenu,
            bool requiresExport)
        {
            // Finales Validieren vor OK
            if (!UpdateHintAndValidate())
                return;

            BaseDirectory = BasePathTextBox.Text.Trim();
            ProjectName = ProjectNameTextBox.Text.Trim();
            SelectedCreationMode = (CreationModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                   ?? CreationModeComboBox.SelectedItem?.ToString();
            ExportIcoFullPath = ((string)(IconFileNameTextBlock.Content))?.Trim();

            InstallDesktop = installDesktop;
            InstallStartMenu = installStartMenu;

            RequiresExport = requiresExport;

            DialogResult = true;
            Close();
        }

        // -- Hilfslogik --

        private bool UpdateHintAndValidate()
        {
            var baseDir = BasePathTextBox.Text?.Trim() ?? string.Empty;
            var projName = ProjectNameTextBox.Text?.Trim() ?? string.Empty;

            // Pfadhinweis
            if (!string.IsNullOrWhiteSpace(baseDir) && !string.IsNullOrWhiteSpace(projName))
            {
                ProjectPathHintTextBlock.Text = $"Project path: {System.IO.Path.Combine(baseDir, projName)}";
            }
            else
            {
                ProjectPathHintTextBlock.Text = "Project path appears here.";
            }

            // Basale Validierungen
            bool baseOk = Directory.Exists(baseDir);
            bool nameOk = IsValidFolderName(projName);

            // Existenzprüfung
            bool targetExists = baseOk && nameOk && Directory.Exists(System.IO.Path.Combine(baseDir, projName));

            // Modus auswerten (nur Beispiel-Logik; passe an deine Modi an)
            string mode = (CreationModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                          ?? string.Empty;

            bool canProceed = baseOk && nameOk && (mode switch
            {
                string m when m.StartsWith("Error") => !targetExists,
                string m when m.StartsWith("Overwrite") => true,
                string m when m.StartsWith("Create new Folder") => true,
                _ => !targetExists
            });

            // Visuelles Feedback
            ProjectPathHintTextBlock.Foreground =
                !baseOk || !nameOk ? System.Windows.Media.Brushes.OrangeRed :
                targetExists && mode.StartsWith("Error") ? System.Windows.Media.Brushes.OrangeRed :
                System.Windows.Media.Brushes.Green;

            if (!baseOk)
                ProjectPathHintTextBlock.Text = "Base directory does not exist.";
            else if (!nameOk)
                ProjectPathHintTextBlock.Text = "Project name is not a valid folder name.";
            else if (targetExists && mode.StartsWith("Error"))
                ProjectPathHintTextBlock.Text = "Target folder already exists (mode: cancel).";

            CreateButton.IsEnabled = canProceed;
            SaveAsButton.IsEnabled = canProceed;

            return canProceed;
        }

        private static bool IsValidFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var c in Path.GetInvalidFileNameChars())
                if (name.Contains(c)) return false;
            return true;
        }

        private void LoadIcon_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Choose ICO file",
                    Filter = "Icon (*.ico)|*.ico|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dlg.ShowDialog(_mainWindow) == true)
                {
                    ExportIcoFullPath = dlg.FileName;
                    IconFileNameTextBlock.Content = ExportIcoFullPath;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error while loading/encoding the file:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
