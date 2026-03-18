using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Neo.App
{
    public partial class CrashDialog : Window
    {
        public CrashDialogResult Result { get; private set; }

        public CrashDialog(string title, string message, string button1Text, string button2Text, string button3Text)
        {
            InitializeComponent();
            this.Title = title;
            MessageText.Text = message;
            Button1.Content = button1Text;
            Button2.Content = button2Text;
            Button3.Content = button3Text;
        }

        public CrashDialog() : this("Dialog", "Message", "OK", "Cancel", "Close") { }

        private void Button1_Click(object? sender, RoutedEventArgs e)
        {
            this.Result = CrashDialogResult.Button1;
            Close(CrashDialogResult.Button1);
        }

        private void Button2_Click(object? sender, RoutedEventArgs e)
        {
            this.Result = CrashDialogResult.Button2;
            Close(CrashDialogResult.Button2);
        }

        private void Button3_Click(object? sender, RoutedEventArgs e)
        {
            this.Result = CrashDialogResult.Button3;
            Close(CrashDialogResult.Button3);
        }
    }
}
