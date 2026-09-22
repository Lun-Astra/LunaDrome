using System.Windows;
using System.Windows.Input;
using Musicplayer.App.Services;

namespace Musicplayer.App.Views
{
    public partial class InputDialog : Window
    {
        public string? ResultText { get; private set; }

        public InputDialog(string title, string prompt, string okText, string defaultText)
        {
            InitializeComponent();
            DarkTitleBar.Apply(this);
            Title = title;
            PromptText.Text = prompt;
            OkButton.Content = okText;
            InputBox.Text = defaultText;
            Loaded += (_, _) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ResultText = InputBox.Text;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                OkButton_Click(sender, e);
        }

        public static string? Show(Window owner, string title, string prompt, string okText = "OK", string defaultText = "")
        {
            var dialog = new InputDialog(title, prompt, okText, defaultText) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.ResultText : null;
        }
    }
}
