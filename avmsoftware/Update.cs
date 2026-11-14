using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace avmsoftware;

public partial class MainWindow
{
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string InstallerName = "AVM-Setup-x64.exe";
    private (Uri Uri, string Hash, long Size, Version Version)? pendingUpdate;

    private async Task CheckForUpdateAsync(bool manual = false)
    {
        if (!UpdateButton.IsEnabled || pendingUpdate != null) return;
        string? repository = typeof(MainWindow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "AvmUpdateRepository")?.Value;
        if (repository == null || !Regex.IsMatch(repository, @"\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z"))
            return;

        string result = AvmLanguage.Text("Güncelleme denetlenemedi. Daha sonra tekrar deneyin.",
            "Could not check for updates. Please try again later.");
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = AvmLanguage.Text("Güncelleme aranıyor…", "Checking for updates…");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("Advanced-Video-Manager");
            using var response = await UpdateHttp.SendAsync(request, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result = AvmLanguage.Text("İndirilebilir bir güncelleme yayını bulunamadı.", "No downloadable update release was found.");
                return;
            }
            if (!response.IsSuccessStatusCode)
                return;
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = release.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            var installed = typeof(MainWindow).Assembly.GetName().Version;
            if (!Regex.IsMatch(tag, @"\Av?\d+\.\d+\.\d+\z") ||
                !Version.TryParse(tag.TrimStart('v') + ".0", out var version))
                return;
            if (version <= installed)
            {
                result = AvmLanguage.Text("AVM güncel.", "AVM is up to date.");
                return;
            }
            result = AvmLanguage.Text("Yeni sürümün doğrulanabilir kurulum dosyası bulunamadı.",
                "No verifiable installer was found for the new release.");
            foreach (var file in root.GetProperty("assets").EnumerateArray())
            {
                if (file.GetProperty("name").GetString() != InstallerName ||
                    !file.TryGetProperty("digest", out var digest) ||
                    digest.GetString() is not string hash || !Regex.IsMatch(hash, @"\Asha256:[a-fA-F0-9]{64}\z") ||
                    !Uri.TryCreate(file.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                    !uri.AbsolutePath.Equals($"/{repository}/releases/download/{tag}/{InstallerName}", StringComparison.OrdinalIgnoreCase))
                    continue;
                long size = file.GetProperty("size").GetInt64();
                if (size <= 0 || size > 1_000_000_000)
                    continue;
                pendingUpdate = (uri, hash[7..], size, version);
                UpdateButton.Content = AvmLanguage.Text($"Güncelleme var · {version.ToString(3)}", $"Update available · {version.ToString(3)}");
                UpdateButton.ToolTip = null;
                break;
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Debug.WriteLine(error.Message);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            if (pendingUpdate == null)
            {
                UpdateButton.Content = AvmLanguage.Text("Güncellemeleri Kontrol Et", "Check for Updates");
                UpdateButton.ToolTip = result;
                if (manual && IsLoaded)
                    MessageBox.Show(this, result, Title);
            }
        }

    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEnabled)
            return;
        if (pendingUpdate is not { } update)
        {
            await CheckForUpdateAsync(manual: true);
            return;
        }
        var (uri, hash, size, version) = update;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }
        if (MediaPage.HasActiveDownload || EditorPage.HasActiveExport)
        {
            MessageBox.Show(this, AvmLanguage.Text("Önce devam eden indirme veya dışa aktarmayı bitirin.",
                "Wait for the current download or export to finish."), Title);
            return;
        }
        if (MessageBox.Show(this, AvmLanguage.Text(
                "Güncelleme için AVM yeniden açılacak. Açık düzenlemenizi önce dışa aktarın. Devam edilsin mi?",
                "AVM will restart for the update. Export any open edits first. Continue?"),
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        string folder = Path.Combine(Path.GetTempPath(), "AVM-Update-" + Guid.NewGuid().ToString("N"));
        bool handedOff = false;
        var label = new TextBlock { Text = AvmLanguage.Text("Güncelleme indiriliyor…", "Downloading update…"),
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) };
        var bar = new ProgressBar { Height = 8, Maximum = size, Foreground = Brushes.MediumPurple };
        var layout = new StackPanel { Margin = new Thickness(22) };
        layout.Children.Add(label);
        layout.Children.Add(bar);
        var progress = new Window { Owner = this, Width = 360, SizeToContent = SizeToContent.Height, Content = layout,
            Background = new SolidColorBrush(Color.FromRgb(23, 19, 48)), WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        progress.Closing += (_, args) => args.Cancel = !handedOff && !IsEnabled;
        System.ComponentModel.CancelEventHandler preventClose = (_, args) => args.Cancel = !handedOff;
        Closing += preventClose;
        IsEnabled = false;
        progress.Show();
        try
        {
            Directory.CreateDirectory(folder);
            string installer = Path.Combine(folder, InstallerName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            using (var response = await UpdateHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = File.Create(installer);
                using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    received += count;
                    if (received > size) throw new InvalidDataException();
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    checksum.AppendData(buffer.AsSpan(0, count));
                    bar.Value = received;
                }
                if (received != size || !Convert.ToHexString(checksum.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
            }
            var setup = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = folder };
            foreach (string argument in new[] { "/SILENT", "/NORESTART", "/SUPPRESSMSGBOXES", "/SP-",
                         "/LANG=" + AvmLanguage.Current, "/DIR=" + Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                         "/AVMUPDATEPID=" + Environment.ProcessId, "/AVMUPDATEVERSION=" + version.ToString(3) })
                setup.ArgumentList.Add(argument);
            using var process = Process.Start(setup) ?? throw new IOException("Installer could not start.");
            handedOff = true;
            progress.Close();
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            Debug.WriteLine(error.Message);
            IsEnabled = true;
            progress.Close();
            MessageBox.Show(this, AvmLanguage.Text("Güncelleme indirilemedi veya doğrulanamadı. Daha sonra tekrar deneyin.",
                "The update could not be downloaded or verified. Please try again later."), Title);
        }
        finally
        {
            IsEnabled = true;
            if (!handedOff)
            {
                try { Directory.Delete(folder, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}


