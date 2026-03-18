using System;
using System.Diagnostics;
using System.Threading;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Neo.App
{
    public partial class PythonSetupWindow : Window
    {
        private CancellationTokenSource? _cts;

        public PythonSetupWindow()
        {
            InitializeComponent();
        }

        private async void DownloadButton_Click(object? sender, RoutedEventArgs e)
        {
            DownloadButton.IsEnabled = false;
            CancelButton.Content = "Cancel Download";
            DownloadProgress.IsVisible = true;
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
                Close(true);
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Download cancelled.";
                DownloadProgress.IsVisible = false;
                DownloadButton.IsEnabled = true;
                CancelButton.Content = "Cancel";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Download failed.";
                DownloadProgress.IsVisible = false;
                DownloadButton.IsEnabled = true;
                CancelButton.Content = "Cancel";
                Debug.WriteLine($"[PythonSetup] Download failed: {ex.Message}");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void CancelButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                return;
            }

            Close(false);
        }
    }
}
