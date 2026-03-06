using System;
using System.Threading;
using System.Windows;

namespace Neo.App
{
    public partial class PythonSetupWindow : Window
    {
        private CancellationTokenSource? _cts;

        public PythonSetupWindow()
        {
            InitializeComponent();
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadButton.IsEnabled = false;
            CancelButton.Content = "Cancel Download";
            DownloadProgress.Visibility = Visibility.Visible;
            StatusText.Text = "Starting download...";

            _cts = new CancellationTokenSource();

            var progress = new Progress<(long bytesDownloaded, long? totalBytes)>(p =>
            {
                if (p.totalBytes.HasValue && p.totalBytes.Value > 0)
                {
                    double percent = (double)p.bytesDownloaded / p.totalBytes.Value * 100;
                    DownloadProgress.Value = percent;

                    double downloadedMb = p.bytesDownloaded / (1024.0 * 1024.0);
                    double totalMb = p.totalBytes.Value / (1024.0 * 1024.0);
                    StatusText.Text = $"Downloading... {downloadedMb:F1} / {totalMb:F1} MB";

                    if (p.bytesDownloaded >= p.totalBytes.Value)
                        StatusText.Text = "Extracting...";
                }
                else
                {
                    double downloadedMb = p.bytesDownloaded / (1024.0 * 1024.0);
                    StatusText.Text = $"Downloading... {downloadedMb:F1} MB";
                }
            });

            try
            {
                await PythonDownloadService.DownloadAndExtractAsync(progress, _cts.Token);

                StatusText.Text = "Python 3.11 installed successfully!";
                this.DialogResult = true;
                this.Close();
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Download cancelled.";
                DownloadProgress.Visibility = Visibility.Collapsed;
                DownloadButton.IsEnabled = true;
                CancelButton.Content = "Cancel";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Download failed.";
                DownloadProgress.Visibility = Visibility.Collapsed;
                DownloadButton.IsEnabled = true;
                CancelButton.Content = "Cancel";

                System.Windows.MessageBox.Show(
                    $"Failed to download Python runtime:\n\n{ex.Message}",
                    "Python Setup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                return; // Let the download handler deal with cleanup
            }

            this.DialogResult = false;
            this.Close();
        }
    }
}
