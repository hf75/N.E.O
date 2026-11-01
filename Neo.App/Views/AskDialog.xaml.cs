using System.Windows;

namespace Neo.App
{
    /// <summary>A question only the person can answer, with two answers.</summary>
    public partial class AskDialog : Window
    {
        public AskDialog(string title, string question, string yes, string no)
        {
            InitializeComponent();
            Title = title;
            QuestionText.Text = question;
            BtnYes.Content = yes;
            BtnNo.Content = no;
        }

        private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
