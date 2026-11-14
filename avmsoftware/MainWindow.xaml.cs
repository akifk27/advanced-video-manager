using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
namespace avmsoftware
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            bool english = AvmLanguage.Current == "en";
            EnglishBanner.Visibility = EnglishQuickPanel.Visibility = english ? Visibility.Visible : Visibility.Collapsed;
            TurkishBanner.Visibility = TurkishQuickPanel.Visibility = english ? Visibility.Collapsed : Visibility.Visible;
            TurkishLanguageButton.IsEnabled = AvmLanguage.Current != "tr";
            EnglishLanguageButton.IsEnabled = AvmLanguage.Current != "en";
            Loaded += async (_, _) => await CheckForUpdateAsync();

            SourceInitialized += (sender, e) =>
            {
                EnableRoundedCorners();
            };
        }
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
    IntPtr hwnd,
    int attribute,
    ref int value,
    int size);

        private void EnableRoundedCorners()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;

            int preference = DWMWCP_ROUND;

            DwmSetWindowAttribute(
                hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference,
                sizeof(int));
        }
        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left &&
                e.ButtonState == MouseButtonState.Pressed &&
                e.ClickCount == 1)
            {
                DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }
        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                var workArea = SystemParameters.WorkArea;

                MaxWidth = workArea.Width;
                MaxHeight = workArea.Height;
            }
            else
            {
                MaxWidth = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;
            }
        }
        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        private void ShowPage(string page)
        {
            HomePage.Visibility = page == "Home"
                ? Visibility.Visible : Visibility.Collapsed;

            MediaPage.Visibility = page == "Media"
                ? Visibility.Visible : Visibility.Collapsed;

            EditorPage.Visibility = page == "Editor"
                ? Visibility.Visible : Visibility.Collapsed;

            var activeBrush = (System.Windows.Media.Brush)FindResource("ActiveBrush");

            HomeButton.Background = page == "Home"
                ? activeBrush : System.Windows.Media.Brushes.Transparent;

            MediaButton.Background = page == "Media"
                ? activeBrush : System.Windows.Media.Brushes.Transparent;

            EditorButton.Background = page == "Editor"
                ? activeBrush : System.Windows.Media.Brushes.Transparent;
        }

        private void Legal_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string section })
                return;

            string title;
            string body;
            switch (section)
            {
                case "terms":
                                        title = AvmLanguage.Text("Kullanım Koşulları", "Terms of Use");
                    body = ReadLegalFile(TermsAgreement.CurrentPath);
                    break;
                case "privacy":
                    title = AvmLanguage.Text("Gizlilik ve Veri Kullanımı", "Privacy and Data Use");
                    body = AvmLanguage.Text(
                        "Video düzenleme ve dönüştürme işlemleri cihazınızda yapılır. İndirme geçmişi ve kullanım koşulları onayı cihazınızda saklanır.\n\n" +
                        "AVM açılışta internet üzerinden GitHub Releases ile güncellemeleri kontrol eder. Güncellemeyi onayladığınızda kurulum dosyası GitHub üzerinden indirilir. Bu istekler medya dosyalarınızı, indirme geçmişinizi, ayarlarınızı veya onay kaydınızı AVM geliştiricisine ya da GitHub’a göndermez. GitHub, bağlantıyı sağlamak için IP adresi gibi ağ bilgilerini alabilir.\n\n" +
                        "Medya İndir, seçtiğiniz bağlantıyı işlemek için ilgili internet hizmetlerine bağlanır. AVM kurulum sayısı veya kullanım istatistiği göndermez.",
                        "Video editing and conversion run on your device. Download history and acceptance of the terms are stored locally.\n\n" +
                        "AVM checks GitHub Releases over the internet at startup. If you approve an update, the installer is downloaded from GitHub. These requests do not send your media files, download history, settings, or acceptance record to the AVM developer or GitHub. GitHub may receive connection information such as your IP address.\n\n" +
                        "Download Media connects to relevant online services to process the link you select. AVM does not send installation counts or usage statistics.");

                    break;
                case "license":
                    title = "AVM Lisansı";
                    string? license = new[] { "LICENSE", "LICENSE.txt" }
                        .Select(name => Path.Combine(AppContext.BaseDirectory, name)).FirstOrDefault(File.Exists);
                    body = license == null ? "Bu sürümün AVM lisans metni kurulum paketine eklenmemiş." : ReadLegalFile(license);
                    string notice = Path.Combine(AppContext.BaseDirectory, "AVM-LICENSE-NOTICE.txt");
                    if (File.Exists(notice))
                        body = ReadLegalFile(notice).TrimEnd() + Environment.NewLine + Environment.NewLine + body;
                    break;
                case "thirdParty":
                    title = "Üçüncü Taraf Lisansları";
                    body = "Lisansını görüntülemek istediğiniz aracı seçin.";
                    break;
                default:
                    return;
            }

            var text = new TextBox
            {
                Text = body, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14),
                Background = new SolidColorBrush(Color.FromRgb(17, 22, 44)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(82, 65, 129)),
                Foreground = Brushes.White, FontSize = 14
            };
            var dialog = new Window
            {
                Owner = this, Title = title, Width = 730, Height = 520, MinWidth = 380, MinHeight = 240,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(24, 27, 58))
            };
            var layout = new DockPanel { Margin = new Thickness(18) };
            var heading = new TextBlock { Text = title, FontSize = 22, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(heading, Dock.Top);
            layout.Children.Add(heading);
            var scrollStyle = new Style(typeof(ScrollBar));
            scrollStyle.Setters.Add(new Setter(Control.TemplateProperty, FindResource("AvmVerticalScrollBar")));
            text.Resources[typeof(ScrollBar)] = scrollStyle;
            if (section == "thirdParty")
            {
                var choices = new System.Windows.Controls.Primitives.UniformGrid
                {
                    Columns = 3, Margin = new Thickness(0, 0, 0, 12)
                };
                string[] toolNames = ["FFmpeg", "Deno", "yt-dlp"];
                foreach (string tool in toolNames)
                {
                    var button = new Button
                    {
                        Content = tool, Style = (Style)FindResource("NavButtonStyle"),
                        Background = new SolidColorBrush(Color.FromRgb(51, 48, 98))
                    };
                    button.Click += (_, _) =>
                    {
                        heading.Text = tool + " Lisansları";
                        text.Text = ReadToolLicenses(tool);
                        text.ScrollToHome();
                    };
                    choices.Children.Add(button);
                }
                DockPanel.SetDock(choices, Dock.Top);
                layout.Children.Add(choices);
            }
            layout.Children.Add(text);
            dialog.Content = layout;
            dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) dialog.Close(); };
            dialog.ShowDialog();
        }

        private static string ReadToolLicenses(string tool)
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_LICENSES");
            if (!Directory.Exists(directory))
                return "Üçüncü taraf lisans dosyaları kurulum klasöründe bulunamadı.";

            var documents = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => (Path: path, Relative: Path.GetRelativePath(directory, path)))
                .Where(file =>
                    (Path.GetExtension(file.Path).ToLowerInvariant() is ".txt" or ".md" || Path.GetFileName(file.Path) == "LICENSE")
                    && file.Relative.Contains(tool, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase)
                .Select(file => Path.GetFileName(file.Path) + Environment.NewLine + Environment.NewLine + ReadLegalFile(file.Path))
                .ToArray();
            return documents.Length == 0
                ? tool + " için lisans dosyası bulunamadı."
                : string.Join(Environment.NewLine + Environment.NewLine, documents);
        }

        private static string ReadLegalFile(string path)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) { return "Bu lisans dosyası okunamadı."; }
            catch (UnauthorizedAccessException) { return "Bu lisans dosyası okunamadı."; }
        }

        private void Language_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string language } || language == AvmLanguage.Current)
                return;
            if (MediaPage.HasActiveDownload || EditorPage.HasActiveExport)
            {
                MessageBox.Show(this, AvmLanguage.Text("Devam eden indirme veya dışa aktarma tamamlandığında dili değiştirebilirsiniz.",
                    "You can change the language after the current download or export finishes."), Title);
                return;
            }

            var choice = MessageBox.Show(this, AvmLanguage.Text(
                "Dil değişikliği için AVM yeniden açılacak. Açık bir düzenlemeniz varsa önce dışa aktarın. Devam edilsin mi?",
                "AVM will restart to change the language. Export any open edits first. Continue?"),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes)
                return;

            string? processPath = Environment.ProcessPath;
            try
            {
                if (processPath == null)
                    throw new InvalidOperationException();
                var launch = new System.Diagnostics.ProcessStartInfo(processPath) { UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    launch.ArgumentList.Add(typeof(MainWindow).Assembly.Location);
                AvmLanguage.Save(language);
                using var restarted = System.Diagnostics.Process.Start(launch) ?? throw new InvalidOperationException();
                Close();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine(error.Message);
                MessageBox.Show(this, AvmLanguage.Text("Dil değişikliği tamamlanamadı.", "The language change could not be completed."), Title);
            }
        }

        private void Home_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("Home");
        }

        private bool copyrightWarningShown;

        private void ShowMediaCopyrightWarning()
        {
            if (copyrightWarningShown)
                return;

            string flag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AVM", $"media-copyright-warning-v2.{AvmLanguage.Current}.seen");
            copyrightWarningShown = true;
            if (File.Exists(flag))
                return;

            ShowAvmNotice(AvmLanguage.Text("Telif Hakkı Uyarısı", "Copyright Notice"), AvmLanguage.Text("Yalnızca indirme ve paylaşma hakkınız olan içerikleri kullanın. Telif korumalı içerikleri izinsiz yayımlamak hak ihlali oluşturabilir ve koşullara göre suç teşkil edebilir.",
                "Only download or share content when you have the right to do so. Publishing copyrighted content without authorization may infringe copyright and, depending on the circumstances, constitute a criminal offense."), AvmLanguage.Text("Anladım", "Got it"));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
                File.WriteAllText(flag, "1");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                System.Diagnostics.Debug.WriteLine(error.Message);
            }
        }

        private void ReportBug_Click(object sender, RoutedEventArgs e)
        {
            const string issuesUrl = "https://github.com/akifk27/advanced-video-manager/issues";
            ShowAvmNotice(AvmLanguage.Text("Hata Bildir", "Report a Bug"), AvmLanguage.Text(
                "Lütfen hatayı GitHub Issues bölümünde açıkça anlatın. Hatayı tekrarlama adımlarını ve mümkünse ekran görüntülerini ekleyin. Paylaşmadan önce kişisel bilgileri gizleyin.",
                "Please describe the bug clearly in GitHub Issues. Include steps to reproduce it and screenshots if possible. Hide personal information before posting."),
                AvmLanguage.Text("Kapat", "Close"), issuesUrl);
        }

        private void ShowAvmNotice(string title, string message, string closeLabel, string? link = null)
        {
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(224, 187, 255)), Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White, FontSize = 14 });
            var dialog = new Window
            {
                Owner = this, Title = title, Width = 500, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
                Background = new LinearGradientBrush(Color.FromRgb(39, 24, 77), Color.FromRgb(14, 22, 46), 45)
            };
            if (link != null)
            {
                var reportLink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(link))
                {
                    NavigateUri = new Uri(link), Foreground = new SolidColorBrush(Color.FromRgb(191, 152, 255))
                };
                reportLink.RequestNavigate += (_, args) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        MessageBox.Show(dialog, AvmLanguage.Text("Tarayıcı açılamadı.", "Could not open the browser."), title);
                    }
                    args.Handled = true;
                };
                var linkText = new TextBlock { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
                linkText.Inlines.Add(reportLink);
                panel.Children.Add(linkText);
            }
            var close = new Button { Content = closeLabel, IsDefault = true, IsCancel = true,
                Style = (Style)FindResource("NavButtonStyle"), Background = (Brush)FindResource("ActiveBrush"),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            close.Click += (_, _) => dialog.DialogResult = true;
            panel.Children.Add(close);
            dialog.ShowDialog();
        }

        private void MediaDownload_Click(object sender, RoutedEventArgs e)
        {
            ShowMediaCopyrightWarning();
            ShowPage("Media");
        }

        private void ChooseTools_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = AvmLanguage.Text("Medya araçlarının bulunduğu klasörü seçin", "Select the folder containing the media tools")
            };
            if (MediaTools.ExternalFolder is { } current)
                dialog.InitialDirectory = current;
            if (dialog.ShowDialog(this) != true)
                return;
            if (!MediaTools.HasTools(dialog.FolderName))
            {
                MessageBox.Show(this, AvmLanguage.Text(
                    "Aynı klasörde ffmpeg.exe, ffprobe.exe, yt-dlp.exe ve deno.exe bulunmalı.",
                    "Place ffmpeg.exe, ffprobe.exe, yt-dlp.exe and deno.exe in the same folder."), Title);
                return;
            }
            try
            {
                MediaTools.Save(dialog.FolderName);
                MessageBox.Show(this, AvmLanguage.Text("Medya araçları klasörü kaydedildi.", "Media tools folder saved."), Title);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, AvmLanguage.Text("Klasör ayarı kaydedilemedi.", "The folder setting could not be saved."), Title);
            }
        }

        private void Editor_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("Editor");
        }
    }
}








