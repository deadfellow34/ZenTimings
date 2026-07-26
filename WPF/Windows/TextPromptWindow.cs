using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ZenTimings.Windows
{
    /// <summary>
    /// A one-line input dialog, built in code so it needs no XAML page of its own.
    /// Returns DialogResult true and <see cref="Value"/> when the user confirms.
    /// </summary>
    public class TextPromptWindow : ThemedAdonisWindow
    {
        private readonly TextBox _input;

        public string Value { get; private set; }

        public TextPromptWindow(string title, string prompt, string initialValue)
        {
            Title = title;
            Width = 360;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            BorderThickness = new Thickness(1);
            SetResourceReference(StyleProperty, "WindowStyles");

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });

            _input = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Margin = new Thickness(0, 0, 0, 12),
            };
            _input.KeyDown += Input_KeyDown;
            root.Children.Add(_input);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var ok = new Button
            {
                Content = "OK",
                Width = 70,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
            };
            ok.Click += Ok_Click;

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 70,
                IsCancel = true,
            };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;

            Loaded += (s, e) =>
            {
                _input.Focus();
                _input.SelectAll();
            };
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                Confirm();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Confirm();
        }

        private void Confirm()
        {
            string text = (_input.Text ?? string.Empty).Trim();
            if (text.Length == 0)
                return;

            Value = text;
            DialogResult = true;
            Close();
        }
    }
}
