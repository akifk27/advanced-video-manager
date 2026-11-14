using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace avmsoftware;

internal static class TermsAgreement
{
    internal static string CurrentPath => Path.Combine(AppContext.BaseDirectory, $"TERMS.{AvmLanguage.Current}.txt");

    internal static bool Confirm()
    {
        string title = AvmLanguage.Text("AVM Kullanım Koşulları", "AVM Terms of Use");
        try
        {
            byte[] document = File.ReadAllBytes(CurrentPath);
            if (document.Length == 0)
                throw new InvalidDataException();
            string revision = Convert.ToHexString(SHA256.HashData(document));
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVM");
            string record = Path.Combine(folder, $"terms-{AvmLanguage.Current}.accepted");
            if (File.Exists(record) && File.ReadAllText(record) == revision)
                return true;

            using var stream = new MemoryStream(document);
            using var reader = new StreamReader(stream);
            var text = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Text = reader.ReadToEnd(),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14),
                Background = new SolidColorBrush(Color.FromRgb(17, 22, 44)),
                Foreground = Brushes.White, FontSize = 14
            };
            var accept = new Button
            {
                Content = AvmLanguage.Text("Kabul et ve devam et", "Accept and continue"),
                MinHeight = 36, Padding = new Thickness(14, 5, 14, 5),
                Background = new SolidColorBrush(Color.FromRgb(101, 52, 220)), Foreground = Brushes.White
            };
            var decline = new Button
            {
                Content = AvmLanguage.Text("Reddet ve çık", "Decline and exit"), IsCancel = true,
                Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 10, 0)
            };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            actions.Children.Add(decline);
            actions.Children.Add(accept);
            var layout = new DockPanel { Margin = new Thickness(18) };
            DockPanel.SetDock(actions, Dock.Bottom);
            layout.Children.Add(actions);
            layout.Children.Add(text);
            var dialog = new Window
            {
                Title = title, Content = layout,
                Width = Math.Min(790, SystemParameters.WorkArea.Width),
                Height = Math.Min(660, SystemParameters.WorkArea.Height),
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(24, 27, 58))
            };
            accept.Click += (_, _) => dialog.DialogResult = true;
            if (dialog.ShowDialog() != true)
                return false;

            Directory.CreateDirectory(folder);
            File.WriteAllText(record, revision);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(AvmLanguage.Text(
                "Kullanım koşulları okunamadı veya onay kaydedilemedi.", "The terms could not be read or acceptance could not be saved."),
                title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
