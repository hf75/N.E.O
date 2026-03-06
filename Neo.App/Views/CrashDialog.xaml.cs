using System.Windows;

namespace Neo.App
{
    public enum CrashDialogResult
    {
        Button1, // Corresponds to "Restore"
        Button2, // Corresponds to "Reset"
        Button3  // Corresponds to "Cancel"
    }

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

        private void Button1_Click(object sender, RoutedEventArgs e)
        {
            this.Result = CrashDialogResult.Button1;
            this.DialogResult = true;
        }

        private void Button2_Click(object sender, RoutedEventArgs e)
        {
            this.Result = CrashDialogResult.Button2;
            this.DialogResult = true;
        }

        private void Button3_Click(object sender, RoutedEventArgs e)
        {
            this.Result = CrashDialogResult.Button3;
            this.DialogResult = false;
        }
    }
}