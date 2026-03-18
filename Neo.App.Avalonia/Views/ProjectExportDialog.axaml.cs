using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Neo.App
{
    public partial class ProjectExportDialog : Window
    {
        public string? BaseDirectory { get; private set; }
        public string? ProjectName { get; private set; }
        public string? SelectedCreationMode { get; private set; }
        public string? ExportIcoFullPath { get; private set; }

        public bool InstallDesktop { get; private set; }
        public bool InstallStartMenu { get; private set; }
        public bool RequiresExport { get; private set; }

        public CrossPlatformExport SelectedExportTarget
        {
            get
            {
                if (targetWin.IsChecked == true) return CrossPlatformExport.WINDOWS;
                if (targetLinux.IsChecked == true) return CrossPlatformExport.LINUX;
                if (targetOSX.IsChecked == true) return CrossPlatformExport.OSX;
                return CrossPlatformExport.NONE;
            }
        }

        private bool _isInitializing;
        private SettingsModel? _settings;

        public ProjectExportDialog() : this(null) { }

        public ProjectExportDialog(SettingsModel? settings)
        {
            _isInitializing = true;
            InitializeComponent();

            _settings = settings;
            if (_settings != null && !string.IsNullOrWhiteSpace(_settings.ExportBasePath))
                BasePathTextBox.Text = _settings.ExportBasePath;

            _isInitializing = false;
            UpdateHintAndValidate();
        }

        private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select base directory",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                var path = folders[0].TryGetLocalPath();
                if (path != null)
                    BasePathTextBox.Text = path;
            }
        }

        private void OnInputChanged(object? sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;
            UpdateHintAndValidate();
        }

        private void OnCreationModeChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            UpdateHintAndValidate();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }

        private void ExportProject_Click(object? sender, RoutedEventArgs e)
        {
            PerformSaveOrExport(cbInstallDesktop.IsChecked.GetValueOrDefault(),
                cbInstallStartMenu.IsChecked.GetValueOrDefault(), true);
        }

        private void SaveAsButton_Click(object? sender, RoutedEventArgs e)
        {
            PerformSaveOrExport(false, false, false);
        }

        private void PerformSaveOrExport(bool installDesktop, bool installStartMenu, bool requiresExport)
        {
            if (!UpdateHintAndValidate()) return;

            BaseDirectory = (BasePathTextBox.Text ?? "").Trim();
            ProjectName = (ProjectNameTextBox.Text ?? "").Trim();

            if (_settings != null)
            {
                _settings.ExportBasePath = BaseDirectory;
                SettingsService.Save(_settings);
            }

            SelectedCreationMode = (CreationModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                   ?? CreationModeComboBox.SelectedItem?.ToString();
            ExportIcoFullPath = IconFileNameTextBlock.Text?.Trim();

            InstallDesktop = installDesktop;
            InstallStartMenu = installStartMenu;
            RequiresExport = requiresExport;

            Close(true);
        }

        private bool UpdateHintAndValidate()
        {
            var baseDir = (BasePathTextBox.Text ?? "").Trim();
            var projName = (ProjectNameTextBox.Text ?? "").Trim();

            if (!string.IsNullOrWhiteSpace(baseDir) && !string.IsNullOrWhiteSpace(projName))
                ProjectPathHintTextBlock.Text = $"Project path: {Path.Combine(baseDir, projName)}";
            else
                ProjectPathHintTextBlock.Text = "Project path appears here.";

            bool baseOk = Directory.Exists(baseDir);
            bool nameOk = IsValidFolderName(projName);
            bool targetExists = baseOk && nameOk && Directory.Exists(Path.Combine(baseDir, projName));

            string mode = (CreationModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

            bool canProceed = baseOk && nameOk && (mode switch
            {
                string m when m.StartsWith("Overwrite") => true,
                _ => !targetExists
            });

            ProjectPathHintTextBlock.Foreground =
                !baseOk || !nameOk ? Brushes.OrangeRed :
                targetExists && mode.StartsWith("Cancel") ? Brushes.OrangeRed :
                Brushes.Green;

            if (!baseOk)
                ProjectPathHintTextBlock.Text = "Base directory does not exist.";
            else if (!nameOk)
                ProjectPathHintTextBlock.Text = "Project name is not a valid folder name.";
            else if (targetExists && mode.StartsWith("Cancel"))
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

        private async void LoadIcon_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Choose ICO file",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Icon") { Patterns = new[] { "*.ico" } },
                        new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                    }
                });

                if (files.Count > 0)
                {
                    var path = files[0].TryGetLocalPath();
                    if (path != null)
                    {
                        ExportIcoFullPath = path;
                        IconFileNameTextBlock.Text = ExportIcoFullPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadIcon] Error: {ex.Message}");
            }
        }
    }
}
