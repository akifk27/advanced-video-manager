using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Input;

namespace avmsoftware
{
    public partial class mediawindow : UserControl
    {
        private readonly ObservableCollection<HistoryEntry> _history = new();
        private readonly ObservableCollection<HistoryEntry> _visibleHistory = new();
        private string _historyFilter = "all";
        private static readonly string HistoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVM", "history.json");

        private readonly ObservableCollection<PlaylistVideo> _videos =
            new ObservableCollection<PlaylistVideo>();

        private bool _isAnalyzing = false;
        private CancellationTokenSource? _analysisCancellation;
        private bool _isPlaylist;
        private bool _isDownloading;
    internal bool HasActiveDownload => _isDownloading;
        private CancellationTokenSource? _downloadCancellation;
        private TaskCompletionSource<bool>? _resumeDownload;
        private bool _downloadPauseRequested;
        private bool _resumeRequested;
        private bool _downloadCancelRequested;
        private string _downloadUnique = "";
        private HistoryEntry? _currentDownloadEntry;
        private double _currentProgress;
        private int _completedItems;
        private int _totalItems;

        private SoundPlayer? _downloadPopPlayer;
        private MemoryStream? _downloadPopStream;
        private double _singleDuration;
        private double _singleSize;
        private int _sourceHeight;
        private double _sourceFps;
        private readonly Dictionary<int, double> _playlistSizes = new Dictionary<int, double>();
        private readonly Dictionary<int, bool> _playlistExact = new();
        private CancellationTokenSource? _playlistSizeCancellation;
        private int _playlistSizeVersion;
        private int _playlistSizeCompleted;
        private int _playlistSizeTarget;
        private bool _playlistSelectionVerified;

        private readonly List<AvailableStream> _availableStreams = new();
        private bool _updatingQualityOptions;
        private bool _singleSizeIsExact;
        private CancellationTokenSource? _sizeCancellation;
        private int _sizeRequestVersion;
        private string _verifiedFormatId = "";
        private double _verifiedSize;
        private bool _verifiedExact;
        private string _verifiedKey = "";

        private sealed class AvailableStream
        {
            public string Id { get; set; } = "";
            public int Height { get; set; }
            public double Fps { get; set; }
            public string Extension { get; set; } = "";
            public bool Video { get; set; }
            public bool Audio { get; set; }
            public double Size { get; set; }
            public bool Exact { get; set; }
            public double Bitrate { get; set; }
        }


        public mediawindow()
        {
            InitializeComponent();

            PlaylistList.ItemsSource = _videos;
            HistoryList.ItemsSource = _visibleHistory;
            LoadHistory();
            OutputFolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AVM");
            UpdateDownloadSummary();
            Loaded += (s, e) => DownloadMode_Changed(this, new RoutedEventArgs());

            EmptyPreview.Visibility = Visibility.Visible;
            VideoPreview.Visibility = Visibility.Collapsed;
            PlaylistPanel.Visibility = Visibility.Collapsed;
        }

        // PANODAN BAĞLANTI YAPIŞTIR
        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            if (Clipboard.ContainsText())
            {
                UrlBox.Text = Clipboard.GetText().Trim();
            }
            else
            {
                StatusText.Text = "Panoda metin bulunamadı.";
            }
        }

        // URL KUTUSUNDAKİ ÇARPI: BAĞLANTIYI VE ÖNİZLEMEYİ TEMİZLE
        private void ClearUrlButton_Click(object sender, RoutedEventArgs e)
        {
            // Devam eden yt-dlp analizi varsa iptal et.
            _analysisCancellation?.Cancel();
            _sizeCancellation?.Cancel();

            UrlBox.Clear();
            ResetPreview();
            StatusText.Text = "Bağlantı girerek başlayın.";
            UrlBox.Focus();
        }

        // BAĞLANTIYI ANALİZ ET
        private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnalyzing)
                return;

            string url = UrlBox.Text.Trim();

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp &&
                 uri.Scheme != Uri.UriSchemeHttps))
            {
                StatusText.Text = "Geçerli bir bağlantı girin.";
                return;
            }

            _sizeCancellation?.Cancel();
            _isAnalyzing = true;
            _analysisCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = _analysisCancellation.Token;
            AnalyzeButton.IsEnabled = false;
            PasteButton.IsEnabled = false;

            StatusText.Text = "Bağlantı analiz ediliyor...";

            ResetPreview();

            try
            {
                // İlk aşama: listenin ilk girdisiyle başlığı/sayısı hızla getir.
                // Normal videolarda tam metadata sorgusu korunur.
                bool likelyPlaylist = url.Contains("list=", StringComparison.OrdinalIgnoreCase)
                    || url.Contains("/playlist", StringComparison.OrdinalIgnoreCase);
                string json = await RunYtDlpAsync(url, cancellationToken, likelyPlaylist, likelyPlaylist);
                cancellationToken.ThrowIfCancellationRequested();

                using JsonDocument document = JsonDocument.Parse(json);

                JsonElement root = document.RootElement;

                string title = GetString(root, "title");
                string author = GetString(root, "uploader");

                if (string.IsNullOrWhiteSpace(author))
                    author = GetString(root, "channel");

                VideoTitle.Text = string.IsNullOrWhiteSpace(title)
                    ? "Başlık bulunamadı"
                    : title;

                VideoAuthor.Text = string.IsNullOrWhiteSpace(author)
                    ? ""
                    : "Kanal: " + author;

                string thumbnail = GetThumbnail(root);

                SetThumbnail(thumbnail);

                bool isPlaylist =
                    root.TryGetProperty("entries", out JsonElement entries) &&
                    entries.ValueKind == JsonValueKind.Array;

                _isPlaylist = isPlaylist;
                _singleDuration = GetNumber(root, "duration");
                _singleSize = GetNumber(root, "filesize");
                _singleSizeIsExact = _singleSize > 0;
                // Kaynak yaklaşık boyutları yanıltıcı olabilir; kesin akış boyutunu tercih et.
                ReadAvailableStreams(root);
                RefreshQualityOptions();
                if (isPlaylist)
                {
                    LoadPlaylist(entries);
                    int playlistCount = (int)GetNumber(root, "playlist_count");
                    if (playlistCount <= 0) playlistCount = (int)GetNumber(root, "n_entries");
                    if (playlistCount <= 0) playlistCount = _videos.Count;

                    VideoDuration.Text =
                        "Video sayısı: " + playlistCount;

                    VideoResolution.Text =
                        "Oynatma listesi";

                    VideoSize.Text =
                        "Boyut indirme formatına göre hesaplanacak.";

                    PlaylistPanel.Visibility = Visibility.Visible;

                    StatusText.Text = "Liste bulundu. Videolar getiriliyor...";
                }
                else
                {
                    double duration = GetNumber(root, "duration");

                    VideoDuration.Text =
                        "Süre: " + FormatDuration(duration);

                    int width = (int)GetNumber(root, "width");
                    int height = (int)GetNumber(root, "height");

                    VideoResolution.Text =
                        width > 0 && height > 0
                        ? "Çözünürlük: " + width + "x" + height
                        : "Çözünürlük: Bilinmiyor";

                    VideoSize.Text = "Seçilen kalite: boyut özette gösterilir.";

                    PlaylistPanel.Visibility = Visibility.Collapsed;

                    StatusText.Text =
                        "Video başarıyla analiz edildi.";
                }

                EmptyPreview.Visibility = Visibility.Collapsed;
                VideoPreview.Visibility = Visibility.Visible;
                UpdateDownloadSummary();
                if (isPlaylist)
                {
                    // Kullanıcı seçenekleri hemen kullanabilir; tam liste arka planda gelir.
                    _ = PopulatePlaylistAsync(url, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Temizleme butonuna basıldığında eski analiz sonucu gösterilmez.
            }
            catch (Exception ex)
            {
                ResetPreview();

                StatusText.Text = "Analiz başarısız.";

                MessageBox.Show(
                    ex.Message,
                    "AVM - Analiz Hatası",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                _isAnalyzing = false;
                if (VideoPreview.Visibility == Visibility.Visible)
                    _ = RefreshVerifiedSizeAsync();
                _analysisCancellation?.Dispose();
                _analysisCancellation = null;
                AnalyzeButton.IsEnabled = true;
                PasteButton.IsEnabled = true;
            }
        }


        // Oynatma listesi başlığı gösterildikten sonra öğeleri ayrı bir hızlı sorguyla doldur.
        private async Task PopulatePlaylistAsync(string url, CancellationToken token)
        {
            try
            {
                string json = await RunYtDlpAsync(url, token, flatPlaylist: true);
                token.ThrowIfCancellationRequested();
                if (UrlBox.Text.Trim() != url || !_isPlaylist) return;
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;
                LoadPlaylist(entries);
                int count = (int)GetNumber(root, "playlist_count");
                if (count <= 0) count = _videos.Count;
                VideoDuration.Text = "Video sayısı: " + count;
                string title = GetString(root, "title");
                if (!string.IsNullOrWhiteSpace(title)) VideoTitle.Text = title;
                StatusText.Text = $"Liste hazır: {_videos.Count} video.";
                UpdateDownloadSummary();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (UrlBox.Text.Trim() == url)
                    StatusText.Text = "Liste ayrıntıları alınamadı: " + ex.Message;
            }
        }

        // YT-DLP ÇALIŞTIR
        private async Task<string> RunYtDlpAsync(string url, CancellationToken cancellationToken, bool flatPlaylist = false, bool firstOnly = false)
        {
            string basePath = FindToolsDirectory();

            string ytDlpPath = Path.Combine(
                basePath, "yt-dlp.exe");

            if (!File.Exists(ytDlpPath))
            {
                ytDlpPath = "yt-dlp";
            }

            string denoPath = Path.Combine(
                basePath, "deno.exe");

            var startInfo = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add("--dump-single-json");
            startInfo.ArgumentList.Add("--skip-download");
            if (flatPlaylist) startInfo.ArgumentList.Add("--flat-playlist");
            if (firstOnly) { startInfo.ArgumentList.Add("--playlist-end"); startInfo.ArgumentList.Add("1"); }
            // Tek videoda tam metadata; listede öğelerin çoğu yine kısmi veri döndürebilir.
            startInfo.ArgumentList.Add("--yes-playlist");
            startInfo.ArgumentList.Add("--no-warnings");

            if (File.Exists(denoPath))
            {
                startInfo.ArgumentList.Add("--js-runtimes");
                startInfo.ArgumentList.Add("deno:" + denoPath);
            }

            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(url);

            using Process process = new Process();
            process.StartInfo = startInfo;

            if (!process.Start())
                throw new Exception("yt-dlp başlatılamadı.");

            Task<string> outputTask =
                process.StandardOutput.ReadToEndAsync();

            Task<string> errorTask =
                process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }

                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();

            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new Exception(
                    string.IsNullOrWhiteSpace(error)
                    ? "yt-dlp işlemi başarısız oldu."
                    : error);
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                throw new Exception(
                    "yt-dlp boş analiz sonucu döndürdü.");
            }

            return output;
        }

        // OYNATMA LİSTESİNİ DOLDUR
        private void LoadPlaylist(JsonElement entries)
        {
            _videos.Clear();

            _playlistSizes.Clear();
            _playlistExact.Clear();
            int index = 1;

            foreach (JsonElement item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }

                string title = GetString(item, "title");

                if (string.IsNullOrWhiteSpace(title))
                    title = "Başlıksız video";

                double duration = GetNumber(item, "duration");

                double size = GetNumber(item, "filesize");
                // Seçilen kaliteye ait boyutlar ayrıca sorgulanır; bu ham boyut kullanılmaz.

                _videos.Add(new PlaylistVideo
                {
                    Index = index,
                    Url = GetString(item, "url"),
                    VideoId = GetString(item, "id"),
                    ThumbnailUrl = GetThumbnail(item),
                    Title = title,
                    Duration = duration,
                    DurationText = duration > 0
                        ? FormatDuration(duration)
                        : "--:--",
                    IsSelected = true
                });

                index++;
            }

            UpdateSelectionSummary();
            InvalidatePlaylistSizes();
        }

        // TÜMÜNÜ SEÇ
        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (PlaylistVideo video in _videos)
                video.IsSelected = true;

            PlaylistList.Items.Refresh();
            UpdateSelectionSummary();
            InvalidatePlaylistSizes();
        }

        // SEÇİMİ KALDIR
        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            foreach (PlaylistVideo video in _videos)
                video.IsSelected = false;

            PlaylistList.Items.Refresh();
            UpdateSelectionSummary();
            InvalidatePlaylistSizes();
        }

        // SEÇİM DEĞİŞTİĞİNDE
        private void PlaylistSelectionChanged(
            object sender,
            RoutedEventArgs e)
        {
            UpdateSelectionSummary();
            InvalidatePlaylistSizes();
        }

        // SEÇİM ÖZETİ
        private void UpdateSelectionSummary()
        {
            if (SelectedCountText == null ||
                PlaylistSummary == null ||
                PlaylistSizeSummary == null)
                return;

            int selectedCount =
                _videos.Count(v => v.IsSelected);

            double totalDuration =
                _videos.Where(v => v.IsSelected)
                       .Sum(v => v.Duration);

            SelectedCountText.Text =
                $"Seçili: {selectedCount} / {_videos.Count}";

            PlaylistSummary.Text =
                "Toplam süre: " + FormatDuration(totalDuration);

            // Format/bitrate seçilmeden güvenilir boyut tahmini yapılamaz.
            double knownSize = _videos.Where(v => v.IsSelected && _playlistSizes.ContainsKey(v.Index))
                                      .Sum(v => _playlistSizes[v.Index]);
            bool allKnown = _videos.Where(v => v.IsSelected).All(v => _playlistSizes.ContainsKey(v.Index));
            PlaylistSizeSummary.Text = selectedCount == 0 ? "Toplam boyut: 0 B"
                : allKnown ? "Toplam boyut: " + (_playlistExact.Values.All(v => v) ? "" : "≈ ") + FormatBytes(knownSize)
                : _playlistSizes.Count == 0 ? "Toplam boyut: Seçimi doğrulayın" : $"Boyut hesaplanıyor: {_playlistSizes.Keys.Count(i => _videos.Any(v => v.Index == i && v.IsSelected))}/{selectedCount} · " + (knownSize > 0 ? "şu ana kadar " + FormatBytes(knownSize) : "bekleniyor");
            UpdateDownloadSummary();
        }

        // ÖNİZLEMEYİ SIFIRLA
        private void ResetPreview()
        {
            _sizeCancellation?.Cancel();
            _playlistSizeCancellation?.Cancel();
            _verifiedKey = ""; _verifiedSize = 0; _verifiedExact = false; _verifiedFormatId = "";
            _isPlaylist = false;
            _singleDuration = 0;
            _singleSize = 0;
            _sourceHeight = 0;
            _sourceFps = 0;
            _playlistSizes.Clear();
            _playlistExact.Clear();
            _playlistSelectionVerified = false;
            _availableStreams.Clear();
            _singleSizeIsExact = false;
            if (BestResolutionItem != null) BestResolutionItem.Content = "En İyi (Otomatik)";
            if (BestFpsItem != null) BestFpsItem.Content = "En İyi (Otomatik)";
            _videos.Clear();

            VideoTitle.Text = "";
            VideoAuthor.Text = "";
            VideoDuration.Text = "";
            VideoResolution.Text = "";
            VideoSize.Text = "";

            ThumbnailImage.Source = null;

            EmptyPreview.Visibility = Visibility.Visible;
            VideoPreview.Visibility = Visibility.Collapsed;
            PlaylistPanel.Visibility = Visibility.Collapsed;

            UpdateSelectionSummary();
        }

        // SAĞ PANEL ÖZETİ: ANALİZ SONUÇLARINI VE SEÇİMLERİ TAKİP EDER
        private void UpdateDownloadSummary()
        {
            if (DownloadSelectedCount == null || DownloadTotalSize == null) return;
            bool ready = VideoPreview != null && VideoPreview.Visibility == Visibility.Visible;
            if (!ready)
            {
                DownloadSelectedCount.Text = "0";
                DownloadTotalDuration.Text = "--:--";
                DownloadTotalSize.Text = "Bilinmiyor";
                SizeNote.Text = "Bağlantı analiz edildiğinde bilgiler gösterilir.";
                return;
            }
            if (_isPlaylist)
            {
                var selected = _videos.Where(v => v.IsSelected).ToList();
                DownloadSelectedCount.Text = selected.Count.ToString();
                DownloadTotalDuration.Text = selected.Count == 0 ? "--:--" : FormatDuration(selected.Sum(v => v.Duration));
                bool allKnown = selected.Count > 0 && selected.All(v => _playlistSizes.ContainsKey(v.Index));
                // Playlist'teki videoların ayrı ayrı akışları henüz bilinmediğinden kaliteye göre sayı uydurmayız.
                double known = selected.Where(v => _playlistSizes.ContainsKey(v.Index)).Sum(v => _playlistSizes[v.Index]);
                int done = selected.Count(v => _playlistSizes.ContainsKey(v.Index));
                DownloadTotalSize.Text = selected.Count == 0 ? "0 B" : allKnown
                    ? (_playlistExact.Values.All(v => v) ? "" : "≈ ") + FormatBytes(known)
                    : _playlistSizes.Count == 0 ? "Doğrulanmadı" : $"Hesaplanıyor ({done}/{selected.Count})";
                SizeNote.Text = selected.Count == 0 ? "Boyut için video seçin."
                    : allKnown ? "Seçilen biçim ve kaliteye göre akış boyutu / dönüştürme tahmini."
                    : _playlistSizes.Count == 0 ? "Boyut için Seçimi Doğrula butonuna basın." : (known > 0 ? "Şu ana kadar: " + FormatBytes(known) + ". " : "") + "Videolar sırayla kontrol ediliyor.";
                return;
            }
            DownloadSelectedCount.Text = "1";
            DownloadTotalDuration.Text = FormatDuration(_singleDuration);
            bool verified = _verifiedKey == GetSizeQueryKey();
            double size = verified ? _verifiedSize : 0;
            bool exact = verified && _verifiedExact;
            bool audioOnly = AudioOnlyMode?.IsChecked == true;
            string audioFormat = ((FormatCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "").ToLowerInvariant();
            bool transcoded = audioOnly && audioFormat != "m4a" && audioFormat != "opus";
            double estimatedOutput = 0;
            if (transcoded && _singleDuration > 0)
            {
                if (audioFormat == "wav")
                {
                    int rate = int.TryParse((AudioPcmCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int pcmRate) ? pcmRate : 44100;
                    estimatedOutput = _singleDuration * rate * 2 * 2; // stereo, 16-bit PCM
                }
                else if (audioFormat != "flac" && audioFormat != "alac")
                    estimatedOutput = _singleDuration * GetAudioBitrate() * 1000 / 8.0;
            }
            DownloadTotalSize.Text = transcoded
                ? estimatedOutput > 0 ? "≈ " + FormatBytes(estimatedOutput) : "Bilinmiyor"
                : size > 0 ? (exact ? "" : "≈ ") + FormatBytes(size) : "Bilinmiyor";
            SizeNote.Text = transcoded
                ? estimatedOutput > 0 ? "Dönüştürme sonrası tahmini çıktı boyutu; kesin değer işlem bitince belli olur."
                    : "Dönüştürme sonrası kesin boyut indirmeden önce bilinemez."
                : !verified ? "Seçilen akışların boyutu kontrol ediliyor..."
                : size <= 0 ? "Seçilen akışların boyutu kaynakta bulunamadı."
                : exact ? "Kaynağın bildirdiği video ve ses akışlarının gerçek boyutu."
                : "Kaynağın bildirdiği yaklaşık akış boyutu (çıktı değişebilir).";
            VideoSize.Text = transcoded
                ? (estimatedOutput > 0 ? "Tahmini çıktı: " + FormatBytes(estimatedOutput) : "Çıktı boyutu bilinmiyor.")
                : size > 0 ? (exact ? "Akış boyutu: " : "Tahmini akış boyutu: ") + FormatBytes(size)
                : "Dosya boyutu bilinmiyor.";
        }

        private void DownloadMode_Changed(object sender, RoutedEventArgs e)
        {
            if (FormatCombo == null || ResolutionCombo == null || FpsCombo == null) return;
            bool audio = AudioOnlyMode?.IsChecked == true;
            if (ResolutionRow != null) ResolutionRow.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
            if (FpsRow != null) FpsRow.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
            _updatingQualityOptions = true;
            FormatCombo.Items.Clear();
            string[] formats = audio ? new[] { "M4A", "MP3", "OPUS", "WAV", "FLAC", "VORBIS", "AAC", "ALAC" }
                                     : new[] { "MP4", "MKV", "WEBM", "MOV" };
            foreach (string format in formats)
                FormatCombo.Items.Add(new ComboBoxItem { Content = format });
            FormatCombo.SelectedIndex = 0;
            RefreshAudioOptions();
            _updatingQualityOptions = false;
            UpdateDownloadSummary();
            _ = RefreshVerifiedSizeAsync();
            if (_isPlaylist && _playlistSelectionVerified)
                SchedulePlaylistSizes();
            else if (_isPlaylist)
                InvalidatePlaylistSizes();
        }

        private void QualitySelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingQualityOptions || DownloadTotalSize == null) return;
            if (ReferenceEquals(sender, FormatCombo))
            {
                _updatingQualityOptions = true;
                RefreshAudioOptions();
                _updatingQualityOptions = false;
            }
            UpdateDownloadSummary();
            if (!ReferenceEquals(sender, AudioBitrateCombo) && !ReferenceEquals(sender, AudioPcmCombo))
                _ = RefreshVerifiedSizeAsync();
            if (_isPlaylist && _playlistSelectionVerified)
                SchedulePlaylistSizes();
            else if (_isPlaylist)
                InvalidatePlaylistSizes();
        }

        // Formatın desteklediği ses kalitesini göster; seçim gerçek indirmeye de uygulanır.
        private void RefreshAudioOptions()
        {
            if (AudioBitrateRow == null || AudioBitrateCombo == null || AudioPcmRow == null) return;
            bool audio = AudioOnlyMode?.IsChecked == true;
            string format = ((FormatCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "").ToLowerInvariant();
            bool wav = audio && format == "wav";
            AudioPcmRow.Visibility = wav ? Visibility.Visible : Visibility.Collapsed;
            AudioBitrateRow.Visibility = audio && !wav ? Visibility.Visible : Visibility.Collapsed;
            AudioBitrateCombo.Items.Clear();
            int[] rates = format switch
            {
                "opus" or "vorbis" => new[] { 64, 96, 128, 160, 192 },
                "flac" or "alac" => Array.Empty<int>(),
                _ => new[] { 128, 192, 256, 320 }
            };
            if (rates.Length == 0)
            {
                AudioBitrateRow.Visibility = Visibility.Collapsed;
                return;
            }
            foreach (int rate in rates)
                AudioBitrateCombo.Items.Add(new ComboBoxItem { Content = $"{rate} kbps", Tag = rate.ToString(CultureInfo.InvariantCulture) });
            AudioBitrateCombo.SelectedIndex = rates.Contains(192) ? Array.IndexOf(rates, 192) : rates.Length - 1;
        }

        private int GetAudioBitrate()
        {
            string? value = (AudioBitrateCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return int.TryParse(value, out int bitrate) ? bitrate : 192;
        }

        private void ReadAvailableStreams(JsonElement root)
        {
            _availableStreams.Clear();
            if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
                return;
            foreach (var f in formats.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) continue;
                string vcodec = GetString(f, "vcodec"), acodec = GetString(f, "acodec");
                bool video = !string.IsNullOrWhiteSpace(vcodec) && vcodec != "none";
                bool audio = !string.IsNullOrWhiteSpace(acodec) && acodec != "none";
                if (!video && !audio) continue;
                double size = GetNumber(f, "filesize");
                bool exact = size > 0;
                // filesize_approx bazen aşırı şişkin; ekranda kesin gibi göstermiyoruz.
                double bitrate = GetNumber(f, "tbr");
                if (bitrate <= 0) bitrate = video ? GetNumber(f, "vbr") : GetNumber(f, "abr");
                // tbr * süre tahmini, özellikle HLS/DASH akışlarında çok yanıltıcı olabilir.
                _availableStreams.Add(new AvailableStream
                {
                    Id = GetString(f, "format_id"),
                    Height = (int)GetNumber(f, "height"),
                    Fps = GetNumber(f, "fps"),
                    Extension = GetString(f, "ext"),
                    Video = video,
                    Audio = audio,
                    Size = size,
                    Exact = exact,
                    Bitrate = bitrate
                });
            }
        }

        private void RefreshQualityOptions()
        {
            if (ResolutionCombo == null || FpsCombo == null) return;
            _updatingQualityOptions = true;
            ResolutionCombo.Items.Clear();
            FpsCombo.Items.Clear();
            var heights = _availableStreams.Where(s => s.Video && s.Height > 0)
                .Select(s => s.Height).Distinct().OrderByDescending(h => h).ToList();
            var fpsValues = _availableStreams.Where(s => s.Video && s.Fps > 0)
                .Select(s => s.Fps).Distinct().OrderByDescending(f => f).ToList();
            // Playlist kökünde kalite bilgisi bulunmuyorsa hiçbir 4K/120 FPS varsayımı yapılmaz.
            BestResolutionItem.Content = heights.Count > 0 ? $"En İyi ({heights[0]}p)" : "En İyi (Otomatik)";
            BestFpsItem.Content = fpsValues.Count > 0 ? $"En İyi ({fpsValues[0]:0.##} FPS)" : "En İyi (Otomatik)";
            ResolutionCombo.Items.Add(BestResolutionItem);
            FpsCombo.Items.Add(BestFpsItem);
            foreach (int h in heights)
                ResolutionCombo.Items.Add(new ComboBoxItem { Content = $"{h}p", Tag = h.ToString(CultureInfo.InvariantCulture) });
            foreach (double f in fpsValues)
                FpsCombo.Items.Add(new ComboBoxItem { Content = $"{f:0.##} FPS", Tag = f.ToString(CultureInfo.InvariantCulture) });
            ResolutionCombo.SelectedIndex = 0;
            FpsCombo.SelectedIndex = 0;
            _updatingQualityOptions = false;
        }

        // Sadece seçilen akışların gerçek "filesize" verileri kullanılır.
        // Kaynak yalnızca yaklaşık bitrate veriyorsa şişirilmiş MB/GB göstermeyiz.
        private (double Size, bool Exact) CalculateSelectedSize()
        {
            if (_availableStreams.Count == 0) return (0, false);
            bool audioOnly = AudioOnlyMode?.IsChecked == true;
            string extension = ((FormatCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "").ToLowerInvariant();
            string? hText = (ResolutionCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            string? fText = (FpsCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            int.TryParse(hText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
            double.TryParse(fText, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps);
            var possible = _availableStreams.Where(s =>
                !s.Video || ((h <= 0 || s.Height <= h) && (fps <= 0 || s.Fps <= fps))).ToList();

            if (audioOnly)
            {
                // MP3/WAV/FLAC gibi yeniden kodlanan seslerin çıktı boyutu akış boyutu değildir.
                if (extension != "m4a" && extension != "opus") return (0, false);
                var audio = possible.Where(s => s.Audio && !s.Video)
                    .OrderByDescending(s => s.Bitrate).FirstOrDefault();
                if (audio == null || audio.Extension != extension || audio.Size <= 0) return (0, false);
                return (audio.Size, audio.Exact);
            }
            // yt-dlp'nin seçtiği video akışını kaliteye göre yaklaşık eşleştir.
            var video = possible.Where(s => s.Video && !s.Audio)
                .OrderByDescending(s => s.Height).ThenByDescending(s => s.Fps)
                .ThenByDescending(s => s.Bitrate).FirstOrDefault();
            var audioStream = _availableStreams.Where(s => s.Audio && !s.Video)
                .OrderByDescending(s => s.Bitrate).FirstOrDefault();
            var combined = possible.Where(s => s.Video && s.Audio)
                .OrderByDescending(s => s.Height).ThenByDescending(s => s.Fps)
                .ThenByDescending(s => s.Bitrate).FirstOrDefault();
            if (video == null)
                return combined != null && combined.Size > 0 ? (combined.Size, combined.Exact) : (0, false);
            if (audioStream == null || video.Size <= 0 || audioStream.Size <= 0)
                return (0, false);
            return (video.Size + audioStream.Size, video.Exact && audioStream.Exact);
        }

        // Kaynak formatları ve seçilen kalite için ortak yt-dlp seçicisi.
        private static string BuildDownloadSelector(string extension, string? height, string? fps)
        {
            string filter = "";
            if (!string.IsNullOrWhiteSpace(height)) filter += "[height<=" + height + "]";
            if (!string.IsNullOrWhiteSpace(fps)) filter += "[fps<=" + fps + "]";
            if (extension == "mp4" || extension == "mov")
                return "bv" + filter + "[ext=mp4]+ba[ext=m4a]/bv" + filter + "+ba/best" + filter + "[vcodec!=none][acodec!=none]";
            if (extension == "webm")
                return "bv" + filter + "[ext=webm]+ba[ext=webm]/bv" + filter + "+ba/best" + filter + "[vcodec!=none][acodec!=none]";
            return "bv" + filter + "+ba/best" + filter + "[vcodec!=none][acodec!=none]";
        }

        // Visual Studio'da Tools dosyaları proje dizininde bulunabilir; çalıştırma
        // sırasında ise uygulama bin/Debug/net... altında başlar.
        private static string FindToolsDirectory()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int depth = 0; dir != null && depth < 7; depth++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Tools");
                if (File.Exists(Path.Combine(candidate, "ffmpeg.exe")) &&
                    File.Exists(Path.Combine(candidate, "ffprobe.exe")) &&
                    File.Exists(Path.Combine(candidate, "yt-dlp.exe")))
                    return candidate;
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools");
        }

        // Aynı -f seçicisi hem indirme hem de boyut sorgusunda kullanılır.
        private static string BuildVideoSelector(string? height, string? fps)
        {
            string filter = "";
            if (!string.IsNullOrWhiteSpace(height)) filter += "[height<=" + height + "]";
            if (!string.IsNullOrWhiteSpace(fps)) filter += "[fps<=" + fps + "]";
            return "bv" + filter + "+ba/b" + filter + "/best";
        }

        private string GetSizeQueryKey()
        {
            bool audio = AudioOnlyMode?.IsChecked == true;
            string format = ((FormatCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "").ToLowerInvariant();
            string h = (ResolutionCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            string fps = (FpsCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            return UrlBox.Text.Trim() + "|" + audio + "|" + format + "|" + h + "|" + fps;
        }

        // Seçim ve kalite değişikliklerinde eski sorguları iptal et; yeniden otomatik başlatma.
        private void InvalidatePlaylistSizes()
        {
            _playlistSizeCancellation?.Cancel();
            ++_playlistSizeVersion;
            _playlistSizes.Clear();
            _playlistExact.Clear();
            _playlistSelectionVerified = false;
            if (VerifyPlaylistSelectionButton != null)
            {
                VerifyPlaylistSelectionButton.IsEnabled = true;
                VerifyPlaylistSelectionButton.Content = "✓ Seçimi Doğrula";
            }
            UpdateSelectionSummary();
        }

        private void VerifyPlaylistSelection_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPlaylist || _videos.All(v => !v.IsSelected)) return;
            _playlistSelectionVerified = true;
            SchedulePlaylistSizes();
        }

        // Seçilen oynatma listesi öğelerini en fazla 3 eşzamanlı sorguyla analiz et.
        // Sorgu başına gerçek yt-dlp format seçicisi kullanılır.
        private void SchedulePlaylistSizes()
        {
            if (!_isPlaylist || _videos.Count == 0 || VideoPreview?.Visibility != Visibility.Visible) return;
            _playlistSizeCancellation?.Cancel();
            var cts = new CancellationTokenSource();
            _playlistSizeCancellation = cts;
            int version = ++_playlistSizeVersion;
            _playlistSizes.Clear();
            _playlistExact.Clear();
            var selected = _videos.Where(v => v.IsSelected).ToList();
            if (VerifyPlaylistSelectionButton != null)
            {
                VerifyPlaylistSelectionButton.IsEnabled = false;
                VerifyPlaylistSelectionButton.Content = "Hesaplanıyor...";
            }
            bool audio = AudioOnlyMode?.IsChecked == true;
            string format = ((FormatCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "mp4").ToLowerInvariant();
            string? height = (ResolutionCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            string? fps = (FpsCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            int bitrate = GetAudioBitrate();
            int pcm = int.TryParse((AudioPcmCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int r) ? r : 44100;
            UpdateSelectionSummary();
            _ = Task.Run(async () =>
            {
                using var limiter = new SemaphoreSlim(3);
                var jobs = selected.Select(async video =>
                {
                    try
                    {
                        await limiter.WaitAsync(cts.Token);
                        try
                        {
                            string videoUrl = video.Url;
                            if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out _))
                            {
                                if (string.IsNullOrWhiteSpace(video.VideoId)) return;
                                videoUrl = "https://www.youtube.com/watch?v=" + video.VideoId;
                            }
                            var result = await QuerySelectedStreamsAsync(videoUrl, audio, format, height, fps, cts.Token);
                            double bytes = result.Size;
                            bool exact = result.Exact;
                            if (audio && video.Duration > 0)
                            {
                                if (format == "wav") { bytes = video.Duration * pcm * 4; exact = false; }
                                else if (format is "mp3" or "aac" or "vorbis")
                                { bytes = video.Duration * bitrate * 1000 / 8d; exact = false; }
                                else if (format is "flac" or "alac")
                                { bytes = 0; exact = false; }
                            }
                            if (cts.IsCancellationRequested) return;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (version != _playlistSizeVersion || cts.IsCancellationRequested) return;
                                _playlistSizes[video.Index] = bytes;
                                _playlistExact[video.Index] = exact;
                                UpdateSelectionSummary();
                            });
                        }
                        finally { limiter.Release(); }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception)
                    {
                        if (cts.IsCancellationRequested) return;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (version != _playlistSizeVersion) return;
                            _playlistSizes[video.Index] = 0;
                            _playlistExact[video.Index] = false;
                            UpdateSelectionSummary();
                        });
                    }
                }).ToArray();
                await Task.WhenAll(jobs);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (version != _playlistSizeVersion) return;
                    if (VerifyPlaylistSelectionButton != null)
                    {
                        VerifyPlaylistSelectionButton.IsEnabled = true;
                        VerifyPlaylistSelectionButton.Content = "✓ Seçimi Doğrula";
                    }
                });
            });
        }

        private async Task RefreshVerifiedSizeAsync()
        {
            _sizeCancellation?.Cancel();
            _sizeCancellation?.Dispose();
            _sizeCancellation = null;
            int version = ++_sizeRequestVersion;
            _verifiedKey = "";
            _verifiedSize = 0;
            _verifiedFormatId = "";
            if (_isPlaylist || VideoPreview?.Visibility != Visibility.Visible || _isAnalyzing)
            {
                UpdateDownloadSummary();
                return;
            }
            string url = UrlBox.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https")) return;
            bool audio = AudioOnlyMode?.IsChecked == true;
            string format = ((FormatCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "").ToLowerInvariant();
            string? h = (ResolutionCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            string? fps = (FpsCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            string key = GetSizeQueryKey();
            var cts = new CancellationTokenSource();
            _sizeCancellation = cts;
            UpdateDownloadSummary();
            try
            {
                // Hızlı menü değişikliklerinde eski sorguyu iptal et.
                await Task.Delay(250, cts.Token);
                var result = await QuerySelectedStreamsAsync(url, audio, format, h, fps, cts.Token);
                if (version != _sizeRequestVersion || cts.IsCancellationRequested || key != GetSizeQueryKey()) return;
                _verifiedSize = result.Size;
                _verifiedExact = result.Exact;
                _verifiedFormatId = result.FormatId;
                _verifiedKey = key;
                UpdateDownloadSummary();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (version != _sizeRequestVersion) return;
                _verifiedKey = key;
                _verifiedSize = 0;
                UpdateDownloadSummary();
                SizeNote.Text = "Akış sorgusu başarısız: " + (ex.Message.Length > 140 ? ex.Message[..140] : ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_sizeCancellation, cts)) _sizeCancellation = null;
                cts.Dispose();
            }
        }

        private static async Task<(double Size, bool Exact, string FormatId)> QuerySelectedStreamsAsync(
            string url, bool audio, string extension, string? height, string? fps, CancellationToken token)
        {
            string tools = FindToolsDirectory();
            string ytDlp = Path.Combine(tools, "yt-dlp.exe");
            if (!File.Exists(ytDlp)) ytDlp = "yt-dlp";
            var psi = new ProcessStartInfo(ytDlp)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            void Add(params string[] args) { foreach (var a in args) psi.ArgumentList.Add(a); }
            Add("--no-playlist", "--simulate", "--dump-single-json", "--no-warnings");
            string deno = Path.Combine(tools, "deno.exe");
            if (File.Exists(deno)) Add("--js-runtimes", "deno:" + deno);
            if (audio) Add("-f", "bestaudio/best");
            else Add("-f", BuildDownloadSelector(extension, height, fps));
            Add("--", url);
            using var process = new Process { StartInfo = psi };
            if (!process.Start()) throw new InvalidOperationException("yt-dlp çalıştırılamadı.");
            using var registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(token);
            string json = await outputTask;
            string stderr = await errorTask;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "yt-dlp hata kodu " + process.ExitCode : stderr.Trim());
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            double total = 0;
            bool exact = true;
            var ids = new List<string>();
            bool found = false;
            if (root.TryGetProperty("requested_formats", out var requested) && requested.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in requested.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    found = true;
                    ids.Add(GetString(item, "format_id"));
                    double size = GetNumber(item, "filesize");
                    if (size <= 0) { size = GetNumber(item, "filesize_approx"); exact = false; }
                    if (size <= 0) return (0, false, string.Join("+", ids));
                    total += size;
                }
            }
            if (!found)
            {
                ids.Add(GetString(root, "format_id"));
                total = GetNumber(root, "filesize");
                if (total <= 0) { total = GetNumber(root, "filesize_approx"); exact = false; }
            }
            // MP3/WAV/FLAC vb. yeniden kodlama sonrasında gerçek çıktı boyutu değişir.
            // Bu yüzden dönüştürülen sesin kaynak boyutunu sonuç diye sunmayız.
            if (audio && extension != "m4a" && extension != "opus") return (0, false, string.Join("+", ids));
            return (total, exact, string.Join("+", ids));
        }

        // .NET 8 WPF OpenFolderDialog, ek Windows Forms bağımlılığı gerektirmez.
        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "AVM - İndirme klasörünü seçin",
                InitialDirectory = Directory.Exists(OutputFolderBox.Text)
                    ? OutputFolderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (dialog.ShowDialog() == true)
                OutputFolderBox.Text = dialog.FolderName;
        }

        // Yumuşak iki tonlu onay sesi; harici dosya gerektirmez.
        private void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryPath))
                {
                    var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(HistoryPath));
                    foreach (var entry in entries ?? new List<HistoryEntry>())
                    {
                        if (entry.Status == "active") entry.Status = "interrupted";
                        _history.Add(entry);
                    }
                }
            }
            catch { /* Bozuk geçmiş dosyası indirmeyi engellemez. */ }
            RefreshHistory();
        }

        private void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history.ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* Geçmiş kaydı yazılamasa da indirme devam eder. */ }
        }

        private void RefreshHistory()
        {
            if (HistoryList == null) return;
            _visibleHistory.Clear();
            foreach (var entry in _history.OrderByDescending(e => e.CreatedAt))
            {
                if (HistoryDatePicker?.SelectedDate is DateTime date && entry.CreatedAt.Date != date.Date) continue;
                if (_historyFilter == "completed" && entry.Status != "completed") continue;
                if (_historyFilter == "active" && entry.Status != "active" && entry.Status != "paused") continue;
                _visibleHistory.Add(entry);
            }
            if (HistoryEmptyText != null)
            {
                HistoryEmptyText.Text = _historyFilter == "completed"
                    ? "Henüz tamamlanan indirme yok."
                    : _historyFilter == "active" ? "Devam eden indirme yok." : "Bu tarihte kayıt yok.";
                HistoryEmptyText.Visibility = _visibleHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (HistoryAllButton != null)
            {
                HistoryAllButton.Background = _historyFilter == "all" ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(101, 52, 220)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 42, 80));
                HistoryCompletedButton.Background = _historyFilter == "completed" ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(101, 52, 220)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 42, 80));
                HistoryActiveButton.Background = _historyFilter == "active" ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(101, 52, 220)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 42, 80));
            }
            UpdateHistorySummary();
        }

        private void UpdateHistorySummary()
        {
            if (HistoryCountText == null) return;
            int count = _visibleHistory.Sum(e => e.Files.Count > 0 ? e.Files.Count : e.ExpectedCount);
            HistoryCountText.Text = (HistoryDatePicker?.SelectedDate is DateTime day
                ? day.ToString("dd MMMM yyyy", new CultureInfo("tr-TR")) + " · Video: "
                : "Toplam video: ") + count;
            long selectedBytes = _history.Where(e => e.IsSelected).Sum(e => e.Files.Sum(f => f.Bytes));
            int selectedCount = _history.Count(e => e.IsSelected);
            HistorySizeText.Text = $"Seçili: {selectedCount} kayıt · {FormatBytes(selectedBytes).Replace("Bilinmiyor", "0 B")}";
        }

        private void HistoryAll_Click(object sender, RoutedEventArgs e) { _historyFilter = "all"; RefreshHistory(); }
        private void HistoryCompleted_Click(object sender, RoutedEventArgs e) { _historyFilter = "completed"; RefreshHistory(); }
        private void HistoryActive_Click(object sender, RoutedEventArgs e) { _historyFilter = "active"; RefreshHistory(); }
        private void HistoryDate_Changed(object sender, SelectionChangedEventArgs e) => RefreshHistory();
        private void HistoryDateClear_Click(object sender, RoutedEventArgs e) => HistoryDatePicker.SelectedDate = null;
        private void HistoryCheck_Click(object sender, RoutedEventArgs e) { SaveHistory(); UpdateHistorySummary(); }
        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryList?.SelectedItem is HistoryEntry entry)
            {
                entry.IsSelected = !entry.IsSelected;
                HistoryList.Items.Refresh();
                SaveHistory(); UpdateHistorySummary();
                HistoryList.SelectedItem = null;
            }
        }
        private void HistoryDelete_Click(object sender, RoutedEventArgs e)
        {
            var chosen = _history.Where(e => e.IsSelected).ToList();
            if (chosen.Count == 0) return;
            if (MessageBox.Show($"{chosen.Count} kayıt geçmişten silinsin mi? İndirilen dosyalar silinmez.",
                "AVM", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var entry in chosen) _history.Remove(entry);
            HistoryDetailsPanel.Visibility = Visibility.Collapsed;
            SaveHistory(); RefreshHistory();
        }
        private void HistoryDetails_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not HistoryEntry entry || !entry.IsPlaylist) return;
            HistoryDetailsHeader.Text = $"{entry.Title}\n{entry.DateLabel} · {entry.StatusLabel}\n{entry.Description}";
            HistoryDetailsFiles.ItemsSource = entry.Files;
            HistoryDetailsPanel.Visibility = Visibility.Visible;
        }
        private void HistoryDetailsClose_Click(object sender, RoutedEventArgs e) => HistoryDetailsPanel.Visibility = Visibility.Collapsed;

        private void PlayDownloadPop()
        {
            try
            {
                string compressedAudio = "H4sIAG5g9moC/xRXY5hdSbcubB52p5OOJrZtZ2LbNia2bTuZWJN8wcQT27ZtNY62q2rfus95uvtHn8Ja61W1qF+nzpMWALSt0aZ2n0Ejs8cBACD/5GwDQK19"
                + "ACAQB3p1H9n9IP/O//9HBvEgCygEKoEGoD3oDQaDEfwzCHQHTUE5kAlY7mN3rzvNbe+WcuPdGHvPHrH77Dn7ySS3qNvVXee+crOCvuAgMEBFOBruhvfgdxiG"
                + "SfAZPADHwTLwM5gDcoEzbnP3J5vNCrG3dCMdStvQ5rQ7nUuv0nRsLPvF+rmp7jiA4Twoo9HoKcqGm+MBuC+uhwP4FGqC7sAKcB0Iu3XcDcymvelnMprkIWHn"
                + "rfPLyUD6kiekC3XpETbKrQpU+AAuRGXxTVxdWCPcEp4LZ4VJQnphCf6B8qEWcAiY5a5nJ+k3UoDMdVTnoD3NnmTvtC17jJOZfCSX6Al2xr0KbsPr6ACeJBQQ"
                + "94uSVEzKJ6WIM8VkobBQF9dBxaAXfGVX6CFyzHll57E3WfWsfFYVa77ltc/aG5wt5CI1WQUwCR5H7/Fv4ZG4SEor95bHy63kqNReWiAuFgbiAugJGOWmZ1fJ"
                + "fGeoPdW6bFYxNSPZyGNuNJtZley2zjpi08HuD9AVXcNpxPJSUTlJ/kvZo2xX2irXZVMKif8JrfBTWAscYDnoLqeJnc3KY/Y3Qvo5/blewfhk3DWjViPnDunP"
                + "0oLLsA8OCW2kBfJspaq6X32g7lALqf2UbvIf0g5Bwo3gHPcS9ZERtmDdNh7p2fWz2l4tSZusdzKmmV+scU4Z6uG3uYTmCkWk7fI75bE6wfPAc98z1nNXfaQs"
                + "kYNSJ2EmWgW2siPknV3c+s+YqC/SwrF9sYux4pqhBYzBpt9+49ynz9wP8B0+L46UQ0pej9+7wXvfu92b2VvBI6hz5Nvie/wI/ueupQudrdZ34y+9ktYt9jF6"
                + "KSrEdsV2aoa+3BxgDyOr2Q2go6AI5bNKWU8Xb2Hf377tvnq++d4hHqDWk1uJpbEJjrDRpKXdydyiF9HiYs2jekSIjou2iM3XEo3fpm2XpDPcFzCn0FiqqwDP"
                + "QO9EX0H/EH8D/ynfFb7PNeWZ9K/QBRF3Na3ppLEyGL00HAPR3pGykVGRHNFS/D4DjFHWMScTWwwA7iwukeeopb3zfBP8MJAxcN5v+m56K3o6KGWkd7gP/MR6"
                + "E2w/MN5qFWKRSEJke3hnOGOERurHolqqUdheS3K6h2F5YaN0Xzntaemb728d+DewPIACyL/O+1g9Jw8Qf6Hm4Bgt4Fw2V+mHY/miONI47A/XCofC/ujy2HB9"
                + "i+lxNtAa4DsaI76RseeNt42/b0AMFg6+CsQHXvjKeauopjReeAHzuRPIL2uu0V/bFC0TqRA+EtoVShe2w82iXi2bMc5SyUk2EuYRjknxah7vR1+5wB/BecHx"
                + "wdRAin+ib5dnouKXBuGDIJVWdv41O+gtY7sifcLrQnVDw0MZw+Uil6OHtagx0s5Ff7oHUU8xSS7jKea77o8E/gm+Da4OPg6s80e8SepqGYv10AT3IAH2FKOG"
                + "1i76JHwxlC/kDQ0JNQlvj/SMTdV/mguc5iw3TMXrpQT1T29G/5zA9CCMg3HTgrMC6fxVvF51mfQJJ8AqbKRzz+yu14hNieQP1w19TgWhDaEj4SrRwtpwg1j/"
                + "kQVuD5RfvCrn9pTyffBnC34NFomLBisF1UBfXw+PVxkhHkDPXZdUs/cbnbUe0dvhf0JO6oPUPCErVDviRjPoU83cTgq9ATbi1tJrJZMX+KcE5gczxuWP+y94"
                + "PdDDv8LbVw1Lfwoj4Dp208ls7dDH8t7UDDcPvUtNSh0XmhEG0S+xHMYOqwMp7IrorjBUfqameHf5o4EbwaxxWrBRsEBglW+Tp4VyRVRwadCNbrV95gntWDRd"
                + "5EuoUCicWjZkh8pE3kW/azXMF/Ya2h+Uw0niMOWwZ5MvT6Bi8HlQC64I/hfo5d/lXawWkBcIN6HDypBFVkYjOZYxup/Pu1SoRGh/aHvYG30dQ8YwKy15yjbB"
                + "DkJMaq3+5c3nHxdoGzwd3Br0Bpl/mm+L5y9FE5viJeAOzeQsNWvqNWObI/3CO0OdQ8tDNcJ9Ijq/TT7zH7sJ9YIbaJD4Q87p8flW+Q8F2gVnBcsGBwdK+Zd6"
                + "l6q15QtCIurs7iZB+x9jirY/WjVSOXwwtCkkh7+Hy0WTY47ezHrlzGQV4Bc8VLqhvPKs9P3yPwzUDFYIHue86uhb6RmnZJYW4c+gNFvhpLPu649jxaOxcP7w"
                + "o5AWWhreFEkfS9HSmeNtLz3idkJRobu8Up3gFf35Ay8D6YLPA7kDwD/Ku0TtLIc5x0+66elCu4jp16vEbkcuhQuFPeHu4cKR3lHENaeUtcMpz16CETgiVlca"
                + "eSRfV3+TwO3Aw0CPwFR/Sd9Mz3SlinQdl4JrmERWWe2MbtrZ6MTI7nDtcLvwh/DLSIWYqUlmS/su6ebqcJLwQfKpqZ7xvm3+zoGdgXGBl/7bvnbe+eowOYu4"
                + "BQXBVIqdA+Zq/UasVbR2ZEd4dPh0eHhkZTSTpusZrZFOhE4GCh4rnpUv86pe+O5yrRgQ8Aeq+RXfcM88pZNE8GSosZEkzn5j/NTqxFA0T+Q436V4JG20S0zU"
                + "HaO4vYLEu6ugX+glzVH6eCLebP5P/nKBzIHF/lW+8t4Z6jS5tvgO9QBf6GiniJXZaKa9il6LZI58CKeNnIhcjZbWTF2w6joHaAGwFQliVbmaqnHlaur/6BcC"
                + "J/zM99bb2bNAGS0VEa7AJu5bMtvuaA7S78RmRrdG8kWyRKZFmkVnxdLqUSPe7kKu84lv5ThWlE/qYO9m3wD/bf9Zf01/F19671h1sdxPzIAPgD/ZZ+cfa73x"
                + "WOscqxVdFqkfGRxxI2a0mUb1iJnNGU5futXQSuGqdE4Z7nnkfeIb4t/s7+k/4zvgrcNvs0jqIEhom1uTGvZTM1lvogmcEasjkyMPI4uj+2N59RRDs4qQ6ewj"
                + "qITHiQvlnmqKJ4cvxqfeza/78vhinoHqWnmu2Jh7zEbWiGSyE82W+pvY+agQPRi5HqkTLRQbrInGF+4x5elC9xMsKDSVaipRtbW3o8/1VfIH/ON807xFPHOV"
                + "LdIM4U8UcbfSXk4jq79xR5saWxrF0XeRnNE70eexSnqS8c5ynRpsCXiJvGI6+bsyzLPLO9Wn+wT/Hl+S976nk7pJ3ilOxlVglB0k8+zF5j29nVY+NiqaGM0W"
                + "XRztFluixRkvzFc2prXc+fAyfi/elseqrzxfvct8L32nfGV9zb3xHMUHpd3CRFQZEHrHOWt9NOroVkyIDY5WiQ6PemJIa64nGzetZ47AqoMJaJuwXRqiGGpx"
                + "b9C32Lfb18V32Lvb00DdJp8V9+FJsJYbR2M2tOoYD7X9sS/RCdHR0WfR1bGDWhbjgXnGfkSgWxb2wMPFtjJUe3qGefP4xvj6+CLe7N6YOl45I90UDqEZoCUr"
                + "RgrZrc2z+hBtVOxFdD3PA/VjxbQBetg4YR1y7lDDzYkqC2UkR56mnvZs8ebzNfEl+MZ4p3rKqtvlx+JTfBIucwfSjs5Q6xSvKrvWPPY+ejeaJXYn9kQrbNw2"
                + "t9s7yUX2DQjYI36XFimpKvCe8qbnuxzwJnueqkOUa9J34RO6BnayJWSlfdUsbnzSvsSqxcxofGxerJM2UzeMPdYSZw097N6Dr/EtcYEcUBt4SnkveWPe697K"
                + "3rae7OpW+avo4DB8yRPXOee1VdA8oS/VTsdqxHLHesewZmlVjRvmQnsCmc3Wg73oH2GilEWZrC73tPCe8F7wDvSe9hxTeyh3JSQGscAn/otYdglri9Fcb67t"
                + "jLWKdYvdjC3QtujA3GwNdfrQoe4kOAX3ErPKm5XX6g1PV565JngBR5+ibpRjYhYhD8oC0rE/SE17jZnXoFpubWtsXGx/rJKWTW/ObzPObk2asJagDaojpJUO"
                + "yXFqPk+Sp4N3sDefd45nhdpSeShlFCvgarCKW4N2cNZZPvOcfkpTtQOxAzFVO62d073mGqu1U4GWdIvDvBiJx6RSyhC1m4d5anmLe694oCdVWS1DqbLQBnUB"
                + "vXhy22m75nKjrz5fA9rdWCw2Qquj9+GT6m+XJJlYHBDRL3xQrCfvVM6qc/k+WbyfPS08Q9WaymOpiNgVj4Yz3JX0hMOsUWZ+I6c+UFM0Gquv/dbe6hnMZVYV"
                + "J8iz+g/wEG0Xmkm3ZVm11U2eFM83zxw+7yfKItkrtRNmoPU8Y98lotPbso37eorWWcuhVdb2aUP0SRzFHe0E8os+dE/A5biZ+EmqqrRV83p2eO56Nnuyehqq"
                + "xZRHUjVxOt4Dr7mfaTzpar8ylxhz9NtaN62BNk/7Q6d6XnOxld/5SS6x7WAqqi+kir3lrcoatYZnrWeDp6Fnl3pcmS6nkYYJ+/nLQWfZaXfnutXVLG00009o"
                + "I7Tp2ldtrb7aeG42sw3nLF3lDoFVcVSYKr2Uo8o1taFnkqeT54eaV82s3Jcai9vwK4hBUdaXnLWrWFHjp15AP66t0W5qTfRsRjlzkZXoXCVLWA9QCkXwMhHL"
                + "VZSy6le1Fu9vwDNO/VsZLSdKc4THyAMrukPoSSePfdFcbRzQ0+hX+S5Z9Uv6QeODWc9+66ygndwCMBn9LeSQxsrLlX5qVC3qyeg5orqKoNyS2ovnsYSqgtHs"
                + "JEl01lutzPrGLN2nf9JUfape06jPeyM5O0lXlhv8hJtwWXGH9FZ+rsxXI6rqeag2VCcofeWM0t+CgSrD8e4p6idT7eyWbviMAXo6Pai305P128Yvs6Z93RlE"
                + "87nfwRZUS7gmZpRLKT51o/pRfaZOUl8oyfJlqaf4EhdHo8EpptBBjmEdNncZ7/VuehG9vn6Yo6+zudyy7LmkFPvtbodN8HuhnjRVnqCUVLeoF9VV6h9qB6Ut"
                + "v80eIT3+i79jEOtKXtgTrJZmX+Oi3ktvpS/TMxo/DMOsah9x6lON7QZteY8HiWel5/IRpZ66Ul2h1lEPKk/kU9IAMYTboL3AYS3pBaeZ7beQWc44rI/R5+lf"
                + "9fnGCM6pFHswUdlRtxtEeIEQEvPIOZQ3SjN1vNpOTVIqKLXl9NIJoQxeC1PdumwfKeI8sfabF4xEvs86/ZZe25BM0apib3MK0OtsEIhD23EGsbc0We6o6Ep9"
                + "tYUap85RTsoHpCGiIIxHb0B5dz2N4+zsY3U0FxnYuKDf0DMYB435XG++2W3IZzrVzQFPoIrC3+Jd6aY8R4FqKd6ZM0p6pYScIF0RmuKLMDeYywwyxSloi1Z6"
                + "s5/h6m91bAw3cpvp+G1WOF66npUBd2FbfE0ISvlkSdmhMCVOfaV0VjbJ26ThYqLwN8ffCPc97UxM+7R1yPxotDAUQzIaGs+MbeZu64Ndg1ygzdwvYDhKwnXF"
                + "MdIIubRyVElRPigLlZicRfZLd4W++DtsC66xavSuM8nuYA00TxkNjOxGZWOzUcvMZ1W3ZzshMpwBsAgG8RjhjPhIOiy3Vo4pD/idSnFmrpRGiHmFE6gM3OPm"
                + "YLtIPSfIqyplbjGaGrWMKQY1TpuHrTd2UbKZ5nT3gmJoGzaE3FJ2+YvcT/mfslPppDyUZVmSngiTsAfNAdCdQTOQe/Y+65ypmiuNHsYo447Ry6xg1bInOi9I"
                + "I3bPbQ5vokLCQHGq1F2WlUHKEmWwoiq95Fm8N6WEJ6gLfOd2ZclkpdPVbmvNMJOMVcZ04zDv8FPzvPXKzkqmUYONAqmwA94jPBHvS6vkLEpvZYBSSNknhyQi"
                + "vhKW4kLoKKjgXqHdSWaHWAGrrfnW2GH8a9jGbLOx1cAe6VwiBdhmNz2cjT7hrGIJKY18Ws6rNFPKK+/kOvJIjpuqQhKaAYNgPStB3zn77X+sR2Y585Vx3vhq"
                + "NDJD5h1+mzjSm95nNcFhmA53FWaL06R68hM5p1JMceQ58gspIr4VtuL66D0Y4mLe4z5OA7uTtc3MbX41vhn5zN1mD6u5Pdj5l7NhhPsB1EXr8WPhs3hVGip/"
                + "kNMoSDki55N7cE7VFTD+B1YBj9lwmocjJ2pltaaaWU3HyGCONH3Wa+u1LZBGdA+LB2PhY5RBqCSWloi0kO+jybflHvJ56Zv4TjiIeyEJbnWrcgf+11ll77KS"
                + "zQFmHjOL2dq8ZY61Otj9nQ3cG+q7+4AHtcXzhbXieI7kDbyuu/JcWZGbSr3EplwprsP+QHUP0gGkhlPF7mfdMLvz/jQ0N5gFrN/WO9t2ytI57ItbA65Ej3FE"
                + "+CUekerIm+Uz8t9yZXmn9JLf5iyejArBh+4YVpBGnXd22CpjHTB78rvMMJPNJVZvu6+ziNzn+BsPHsBEXEtoxmf1QWovr5HXyp3l71IlqbPYSiiAv8LloLKb"
                + "RPeROc5s+yCf+TZzuDnZvGrWsaj12Y44OWhfdtL1wXZoKd4nbBP/kpDcTZ4hD5AT5fnSLfGNcB2vQk0gc/ezvrQsye1U4BoYMbeaC8xDZpy1yxpm93GmkmOU"
                + "soZgLXyBoKCKv8T1Unq5o9xLLilfl/JKrcS2QgUM0UkwyM3NfpNbzh3bsFpYn8195kEzxexlBe2fdhJ38fpsqfsKZEXNcD+hk5hXOiflkBvKFTmGh0inxBfC"
                + "A/w/NBjmAx/ZNjqG9HMm2xes0tY785L5zixtXbAm8d6MJJvoa5YN9IQb0AV8VdgptpWec4YW57zcKcVJ9cX2Ql2cCb0F690OLD/1kTinkr3Kyml9NT+Z6a0Z"
                + "Vi47bP9wGCnCBnCOf4MJOK+QUfwkTpI+SwmyR74ltZT2iI+F5/gMWgibgjTua3qM7HEu2tgeZWWyDI69dtZLayHHzUAyj55mulsC9kTT8Eyhh5hOWiN95Sh8"
                + "Lc2ULLGq2EFoiUsiF1xzF7MetCFpzqt6ZnXlWE6wqlvbrHK2bX/lupWW1XEngUO8x9/xc2GLWEXaJb3hu2yXykprxbvCG3wX7YajQHU3noXJTwc7teyjVmur"
                + "sFXBmmjFrPX2IKcnGU03svsuhIVRbY6cHOITsYO0T7opHZR6SB/FEmI7oTOui7JxjznP/qZzyDLnnJ3O3mC1sWpZ/a1LViNbcD47nwmhedyWYArcjPbgtUI3"
                + "kXHs/MN3mihllGaJ14QP+A26CNeCv9xaHMf5SE1nJkfyEqub1cfaagXsvfZwpzNXimlsj/sYGNCLJeGTsFrMIo3k/ZklVZduirnFNkJv3IGngXQghd2nl8h9"
                + "h9qt7OfWQmuUtcz6aPWwg84X5yX5TkW3IGgK+6MhuIOQU7wolpSGSZOkNlz7hotnhU/4J3oJT4HV7ijWnfYgU51LdhH7hrXSWmFdtQrYZ+yxTjvShvZjc9zd"
                + "4CrXitt4h9COz7yy1FvqKGWVDorpxBbCX/gv1A5WBJl5HrCJh1R0ltrx9iVrp3XKgvZYO7Pz1XlAHtMfTAA5YBlUFmcVvgqzRItPq6lUTHondhGPCJ9xDKXA"
                + "l+Ccu4OtpuvJaYfZ/WzXum6dt35Zte179lSnNalHm7E+7hSwCm5Cy3F/IYu4W5Sk0lJxyRHXiJLYWBiOJ6BhsBOo7ZZmxWktMsq5bTfgzHpifbZy2ivsMg4i"
                + "UeLSP9wqoAPshzrj4sIroa24TTzNfzqKP4WWwhb8DDGYDdbgk9rOkmljepW0JZhzdI+zzTnsPHOCpCM5RQrSPbQSe83muTWBCO/BtXy3DMIloaV4WfRJ+aSg"
                + "dFGsKI4XZuOeKCM84lZml0k7x2P/NLHZ28hg5DLmGQ3MbtYluzcpx/KAP5Ak3BO7yruU1WoOTw1PVK2oZlH2SKkCxDr4xr6RoDPKKmBWNA7ypPVan2ssNt9b"
                + "/Zx09CGbDbKjhfgM16ty0jipn+ST/hLXCBvwQjQGDgbT3TMsL7tOt9ADNJm2YA9ZF/e32xfc5PdOgKlgLUgAfd1N7CpNIQXIbCfR+WJTu5eTheSig1nIXQub"
                + "Y0FcJ5myRz2rAs8tNYOaxBW+mSCiea5JhtmJpqh3iBWNroisiJSKDolV1KeZhZwMLA8sInjlDep5b3t/m8D/Ak0CDfy7vZPV75IgWCCJYqe7mV8fFmsefRgx"
                + "ImeiabWfeg5rvVOO3Qd/8rdzDXm9MlQ9rU7kCWu+zMTiQnXUAAxjd/hkp9qm9YUnGNHJSaZTn7sdFEbz8HahJ5/lVjGtmFm4hLLAum5HOta5YDU3axmH9H/0"
                + "QkY184X1zWnGYuAw7iG9U4B3l++4P3Pghv+sD3h3KlfFGUhxpziJJo11jLQNBVKXp6Sm5AxljzyIpTUvOfvcLXiQ/MnzyJ8+bmV82TR6/PO4H4E/fV51oLgM"
                + "HqGutVN/FN0Vrhralfo1NRI6E8mrBcx6zm5GYWbxhRzn2eVd6PvPl98X8uRSP0hNhBXwLWtBMtmtzaz8fbBMr2ZM44m+J3+zl+Ivr+PiBPmSMkKdqn5X9suf"
                + "xC1YhbXYeOeluUA/HpsYjUUKRfPG3mqVzLzOKEbgQjGiON4JgYZxjeIHxm+NiwaG+yqpo8WWUKF3zZexXuGNKXuT9vze+/tg0r8pi8OFtLbWOzodeeVK3jeB"
                + "8/HnEy6mPZV2b8KO+POBPF5Z3oCyszvWa21y5FNqnpROyYuTD6ccD02IPtVX2jNYPxQn1VU/ey/6bwfMQP3APd8Wz1f5ujAOFmFpHf6O04/F9CiIPYx11Gea"
                + "+Zw/WAlYTnCk5moabw5ff99n7yrPEiVZfI7GuSI5bB7SSkd7hbuFqoRweF8kn9bK9JIMoI7QXkn01QuejW+e8CNhVIIVPyNYwldI+Qe/ZQF7vNYtbCQP/Z30"
                + "c+bPqr8SkpJSdkYCRpIDoCJd9CQFWqW5k7Z04oTEzen2J1yME/z/U5Jw0G1qp2r5IvlT0ycHktIl5U8ulZoY+UfbZ6XSEqicdEs97nsVSIzrGvdfMH/gkfeV"
                + "MlS8DjOzrfYSI15rEu0aaRrJFr0Yy2gguym9CaoII+Syno6+c/56gST/ft96z2N5gnAN/CLZrW3aP5HOoaSUv1KSUvqHHkV8+m+rMruEashjvQWDOdOUTFsp"
                + "Xf50RsLR+HGBIZ5b4nWwxzmmF440Smn0u/XP6T8+/hj0KyH5YChR89gN3WXCOjV74Gf8lbQLEgumX5f4OW32NOMC2T0txOagu3NZPxb5KzUh+crvJb+HJrVN"
                + "yR3+N3bWzEAnw8PiWHWjTw/0jnsV1zLubWCpb6WaQWqJprCn9mLjdexz5Ep4UbhG5FG0gJ7NmkMKgke4llzfc9jXLpA1aAQ++H94C6i3RAINGrNK6D8iDUOz"
                + "Uo4kf0/Omdo+PDRW3VxJSsG3YjNP48D6eCXtsHRn031NG0qjBzP7Vshr0SDaxlwXHZWaK+nBz40/1v+4+TNT0pDU9dG+5gr6EsXkrb6tcRcSnqe7nbg8sWS6"
                + "C2m6BWt6N0sn4ENSyBRiK0Jiytgk9nttUoWUq6G0Mc0oTCaBPcJY5bC3UOBKcEBchrhngTO+FHWNlIwKujOdsuZQrWM0Q+R4uGpke/SBtt0MOTPc3HijdEyt"
                + "7FMDLOANFgz09b1Qt0phRBm1ixm3okJYSTWSvyQ/TbkUWhnNazRwrrtdhMfKV1+HOJBwMG3TdI/SVk7YHJfF/1HJI+R3C9hDtHLh08mFfv/vZ4OfCb+s319T"
                + "/o3kMASSAP3SQc+5AErTMu2KdEfTnUh7IM3xoOM9JKfFvdlH67rWOLIl9WNy7uTBycdTfoTeRvsbbZyB7kCcTW7heelbGugVbBRsHJjg09RP0ij82e1O8lm9"
                + "9dqx75EukaORj9HH2l/mDOcX64/uiR+V4d52/pGBwwFvYJavjKeCfAaHXT9pYH6I4cj71DUpNVN+pswJqdFmegl7LPuFGspdvD8DJ+N3JixNOzBt6QQ77rlf"
                + "U2eJl0GqU9fw8UkdTUr+Vf7Xll9ZkjamsHC8vtUe6lYWbiovfGXijqUpn3Zf2kDagWleBWf4Vip1hKfuRGeMQaMVws1T26d0TGmeWih8IwqM7fY41gB9EBV1"
                + "lbe3v3tgUuCMv4jvt5pJ/o5ngQJUtBsYObR9URDNH80ee8V1LJvTgR2Hspig7PPs9n33twp89v/tm+O5Jk8SXoNE2t3SNSl6PlQn9XBKfGqP0OrIcq2YVYXO"
                + "gvfEN+pof/+4+WlOc53wJZSI/yvw2XNJqo7WU7/1NFY4/GdKtaSqvxv+7p00OWVcuIjW34rSxYhKkndEIGP80zQrEholyGleBj/5GqtNxD9ghKjWSu1y5GRo"
                + "cWrTVCd1Rvhe9LgeZ/+P1ofnhRdyX099X1//IX9+/3PvE7WmXE9IhO/oQzubGdL6xTZE10W7xb5q8eZl+xENwLpCA/mHavJJhfxH/Tt9dz11lVLiWZiejbK9"
                + "hj+2OwxDpfk5rUOVI0mxWmYGUhJ0Ebor2Jcm2CP+UZqGCVfT1I5/G9jnfS1vxAXds/ZS/W3kcerG5DZJcUn3kmalZAmPjHU0NxIEm4gN1PO+acHB8YPTjEoz"
                + "L/548A//e7WoVBXVYRPtBKNSLDFyMVQ/dCiUEk6KztU3WAbpA07hC1JjtYp3jO+Xb4Vvgveo2kleK6yC09ki56t5RM+klYgpsa2xmJZsDLZb0L/AIjxbyq/W"
                + "817zzfFP4jUp3tPKW3ElindnONnNDNq6yM3Q1dQtqV1CdrhbbLihOlFGUaq00nPCXzzuRfyaND3SVIwvFGzhe6S8FDaA1qSSOTPWMwxTJyUnJbVM3p/yPfQp"
                + "OtIY7PztnsM75czeqF+Oqx6/OD4SNyZY2t/I80OqiEe7951x5iatbzQlXCs8JNwvkiU2XK9pdSdb3fvoglhXqe5Z5s3vc72ZvQvUgfJrnl0S3QpkkzXQuKJd"
                + "io2KfY5J+j0ju/2SPHRfoWtif2Wtp7gvvb+yf72vnDeXOl7qhgFYQEpYufQl0fXhPiEhNDn0IJwcPanH2zfoPjhPLKh25gp7P+5+/Nf4uPh2wae+nWpMDMMn"
                + "9KGVTU+J9AsdTHmVHEnWUp6GRkeP6b3t1qwxyiTNV4f4dgT8cevj6sdlDRb1r/FMkdMKM4FIL1sp+v9iCdGqkVKRlEi32DAdWF+dGGPwudBabqWe8Az2DvKe"
                + "9HRWh8ppxH5og/uSNLQzmn30xtr7WB4ts37EeG71JZXdwsgr7pO/qWu8W31R3yxfe57DPHJ24RtYSKvZxY35sTGRtOFxocOhc+EV0Ux6SeswGQSKCzdkwXvc"
                + "fzoYi2sefyuuT7Csv40nWcqPK7gtnb3GsNiD8K/UFym7U9qnfg4VjWbWp1klqcOVOaBYnrr+l4HNwSXBAwG//4pHl+8II+AfLGbnM19rRWNlonZkYvRgbLr+"
                + "xJzqdGV/QiyMlsYov9TTnveeNp6C6mC5mngepQODaJJ91oTGY62e1lcrqW80VlqO8y+bAVsKIam0anvy+Lb6evuGeG+ri+RHwkU4i9VzKpnztA7R+2EQdkKX"
                + "wy2iK7QB5hGnpvsNDZX+VUf5NgW8cbvjBsS1Do72f+PqUk2YBs6SvNYvnkEKhD+lDkl9lhoIy9Ed2kWzJLnltsa7pRVqxLvLvzFwM1Ak8NT31NNE6SXW5Ejy"
                + "koYW1atpeWMXo3Exr7ZNP2kWdz7QTaA2/p+4R86vxnla8UwfVfrIvUQ/Vz6dznLaW6uNNvoBba/WWF9otLNWOXnYB7AJl5OmKe0927x1fBV8Y7yqBypjxaVo"
                + "pNuQ1LLm67ViqyKrwm3D78L5o3m0M8YduxDbD3OJXZXS3on+xGBSMBosGFznb+OdrJQT98M4tthuY6yI9Y/EQnVD/UKtwm6kiZbTbOUcZ1lQe7Ga8j/PON9a"
                + "v+3f4J/ju+dZoHwQNRRzFdre9pk19ThtdmxXbIh2Xz9gRuyRNADWoRThtzRcGaTeU5erp5X28nSxBU4Co1k6kmrlMZ/p6fSQNlCfauSwyjrbaU2QgqaId+Vj"
                + "akbvJy/yjfBW9PRRglJL3BbUofXtBUYprVe0Ak8ggUhi9EEsn0GsgnQ2+Iozyrra2pc+kDPYL/glsNm/y5tejZfOovruR+eI+VPbHfVFCoZ94cPhNNFEbYex"
                + "2r5MAcwhUGmQ2sK70pfH7/oK+A55Dig1pIX4OH9zNHM8VnkjwpFUXXupScZh84qdiS51GfxTqChdlx8rTdSS6nSlvvy3uBQ3gClsI5lkHzRbGuP1svpCfawR"
                + "Mt/Z+egetwa6KWSUA+oCz2jvBW93byfPWWWNJAvFYXlW35lv5tErxX5EakZaRDJHV8T26LWsqmSE+x96I55Rinrj/A0CtwJzA/P9r7zL1DvSIdwPZKfQLmN8"
                + "iuWNJkbOh3NHqkad2J+GbiUTADB+IFZXSnuWexv52vvOeKfz0+ZLqtAdnmCliGZlN2/pov5Sq6vXNi6aO+z7JIc7Eu7Ca8T8cg3lsfJUqaeUkfeL1/E22MXN"
                + "RmWnnHXX+KBP1g/po42r5kL7EEnvroPphN5Sb0VXoXeE90/vUI+gxsn7BQJzuDXJHCujkUHbFr0X2R0pHR0cq63vMAc4I9gauF+YLyseydfP/0cgQ6CTP+pN"
                + "UQfIc4TBsC6r5Iw0E/XqMSU6ObIrMjVqx1x9pFWaZHIpPC7klLOrqz3Dvfu9tbw1PceVM9JQIQoHuYDety1zjXFaH6of1+cbL82l9nL+JjJBBgzE5dJxubMy"
                + "Xgko2eQzooWj8Ka7ni5wTlt1zNrGTf2VPt7Yataw/yQLmA5640PiSbmzOsvzhzeDd7Snsjperi2eQswtRadyjFta71jfaDDaI9o1Zml/mPvseXQ+mI+7Sd+4"
                + "ik32dfev8mf0R7zFPb/lmmJ71NbtSXZaNYw2Go3WiVaIPo0Gtae631rjNGVBeAQnShmULerfHuK57NHUrcpr6YIwFPnASbrauWy1NwcYgpHfeGxY5my7OxnE"
                + "5oE1aJQgSaXlJDmn8lRW5OtiPqEaKgLimZdUss+Z+4x0hmzMMGaa1HrjiKwdOIEYf1tfUKDntOeDZ5Cnt/pYvisOxPdAZjbCYeZbvZjmi82Kbo/2jl3W9hqS"
                + "fZQsc8eimuJNOVld4t3gw/6Lvpfedp7myiexIG4KxtBrdg+znx6NpYs9j1aKVdAu6efNBGcTrQCuoMxiVvmIck1t4Knk2ahOUH5KTHiB1oKurBGZYgetNOYK"
                + "Y62RzcxqzbQrkjwsL8iBYnimeFKaKt+VN8ip0gOxmTAbLQQz2Fxyyi5jJZi9jfLGMmOQedGa6MyiF90AaiJ0lrIqo9Vmno2etp7JahaltuQXlkOTdSXJ1mXD"
                + "1U7H1FgsOiW2XWthjLE0Zy8bBYsJx6TPyibPPW8vXzvfXm9fzyalr/QJF4Oj2DNntDXC+KH9iE2OHYiN1q7q88z/2SKd6P6GJYWS0n1ZV9aqB9QqagPlu1RQ"
                + "LIrjYJRFSGHnlHXIzGLK5iCznjXP/oN8p/fdY3AiVsVaUkZ5gtxLfio9FUcJN9B3EGaQlnX2WRPME8YQY5fRxVxk5XYCtJg7AG7BB8Tx8jflrdrUU8ozT22r"
                + "7Jc2C/XRLfdP+tzeZT7Sh2nrYq1j+2JrtaDBzIbOSzoTFOK+8U7ey73tpPe9d4C3m+eR8liaKliwq/uUTLRHmS/1M1phrZh2SXut9zdb2zPIE5YXdsStREMq"
                + "ovxQcqjPOD9eSHXE4XgsHO3OpXecDnZda5s5wjxjjrJW2vzFTae6LaAfrxGeiv9JheTC8knppbhGkHBV2NEdQw86Oe2vpt/cY1wy2pjdraf2JrKD3QYaQuIT"
                + "qbnSQ9VV5JmpTlNEOacYRktAFnbamWftMorqpbSrsV+xrdpHfb15ws5M/3HLoN3Ce+mUkssjedt703tre1KV/LIs7kElwQU60vnLumIs1d9pJ3hugUYnM72d"
                + "gdRkozk7ZgsFpYFyZWW5MkC5LZ+T2oi78QP40w2yziTFfmrlsN6YqrXD2m7HnJm0gmuAf1BB4S+xrfRW0qSNvKZjQnW8DF50f9L8ZI3dzOpj/jK+8JqqWIvt"
                + "siQHKwkaozZCEemE/EFZoV5Vh6sblMbyKnE+rgeT2RrSz55opurPuGuU085oN/QmZkV7OHnJmsKj+Af3tgZqY88Nz3+eDB5T6S2PFmvjMFjHOpF29m6zu7FC"
                + "r6tP0csa3cwk64Jzhb52v8IbuL94RFopY4XIU+X1UnvxIc6IGoO57DMZ47S311k1rTbWU+uY/cPpRE22A9RBV7AsOuIy6Zw0RXou3hdG4/cwJ2jPNhO/c9a6"
                + "bBYzfWYXM6fVzH7h7KRb3L1wH54lZpbbKfnUSWoLdbcySzbFtFxhD7kDaQ2nmXXYmKJf11ZoPzjb05kPrbdOdjYe3EW28IU7/FK1mKee55WarCyS74u38HrY"
                + "zS1NyzszraJmPeO1nsT9cKT5iPO3A23u/gkz4GMClqLSGHmhXFIeKrUUk3EbtAPEWCeqOU9tr73d2mpJ9i37o1OBnmNtQRLshXcJ28U60hypm/RY/C7sxgXQ"
                + "VHCNJdDpTkE7pzXOrMQ5GGdltqc7JWk6NwAl/E6YJN2WjyoF1bzqTuVfuaG0WFiC+oIyLIHksMeYRXmuIlpu/Z4eMiZanZxx9D83BtMLSNogP1CWqPfVxepD"
                + "ZZucVqooFEdpAKEe0sbWTccYbnQzrhirzRtWfQfRl+wIGIv8QhexnZQq/SF/kKpJtUQgzEEh0Mq9RQeQ+s4YG9i/rXL2R/ut8wedz4JgDVRwQ6GxSMVmUnXp"
                + "jmgLz/BQ9BoUd2dTw/nbnmldN4eas0zJCvF8dIMsY2NBL1RBeCtWlCso95RkZZVyXV4sWUIWnAbG2DuSbFey3htJ+hB9sP5Lf2tUtEL2N4LdgrA6LiDeluKV"
                + "X0pjtSzvxU65lrRaOIwOgyPsHsni/GftM73mSyODedm8a+V1TpHhrBT4Dkfgy8IVcaD0r7RQUqWiokf4FxWH/3OLsKfkqPPGbm+XsPvZlv2Gd6E9u+fWhQdR"
                + "BFvCUTGDlEW6JHp4Fw6jknCNa9CBxOP8tDJZe83tvKZ7VtTuSxLZT/caXI2rijulk3J/ZY8yRnkkX5JaiRvwAbjb3UqPOtRaYc41futX9ETjjeGzVtu9SXc2"
                + "BIxG7QQmtpD/VK7y5PKXslbuKb0XsuPqsL07g75wBtvtuIq1NkealvnWinemk8zsitsTfkUlhQpiklhTKs/dJkH0CXdQXxhz57DyNCOp5Oy3R9tr7QTnrfOb"
                + "FGFLXAd0Qhs4+gaJH0RLPCamF/MKITQL2m5v3vPxTjN7uJVk3jL91n/WaTuO7KDd3WLQQJuFIPdqIndSGiv35e/SFn5SVdSU83AUOcMVNr85xMhjNDV+G6lm"
                + "E9twHtJr7km4HFcQ10jr5fJKXyWfMkkeLiWKk/AZmOoW5I6Y1WFWeeux+cQsb5mW6rQjj/hpKaAn2sc5NVx8IX4RV4lRQRE+o9WwLHjJltPRZJVD7eP2dbuw"
                + "88F5QUTW2j0MvKgebiPkEXeJj8WdYjaxrlAAP4btwUPWlP52Ttl3rVKWwX16l7XQvuxUoh/ZJt4HJIwT90nzZFHJoJyTqfRWHCScR7+A7Bahox3LumWaxlJj"
                + "laGYn8wM9t9OC1rIVeFzNEH4IEal3bImv5BbycOkKuINnB+NAJdZIXrF2WTfsppb5axJ1h+216lIVlO/OxukwOK4ouAKI8RlYnvxgeDiKDoLh4Gc7ld6h/x0"
                + "6juGrdt1nK/OdfKeZnEHgbNQR7Lwjk9qj7hczC32EjrjLOgYqOiep51IHqeQPdUqzM/ZavW1pzivyQCWFlyD/fAnIbsUL//D77ZVFmWvdF4owb3wf+5bWpD8"
                + "zx5kzTaR+dUoaX41o1YT5wvZwvqB/OgaLio2kRLlyfI0OQfHXisRCzPQT9DYvUS7knJOO97DjdZ1q56d3slH+tIrrACYAc+hG3iZEC9WF7OIe4UIZugd3AF6"
                + "uyVYFlqeLHdKObmc7s5v5wQ5S3+yQmAY3IVO4tVCCXGqOIHXNFFYiHsgH9zMOXiHLHMW2Q+tvlY7a6fVmPdiNnHoCrccfIgaCyvFlVINeYk8ToZyIQmJq7EJ"
                + "q/Fc9Zy0doJ2emu8WdXsb2qcGwnOHJ7FnruzYGY8U9grzpSwnEv+ItWXuojFhPuoGbzhNmJhcsP5aNezsZ3WHm8XcrKSP+lc9srNDdugzriQcFQICx+EGcJH"
                + "LGALPgJb3TGsH51BnjuDnabOBCfi7CUb6QmW7OaH7dFA3ESICS3E7mJ2cYlwHG9DXSHgXliZMids57V3W1Otw1ZlO4NTiayhGd1/QXV0ivtaOumpVEWuI4ek"
                + "KlI5MQkPRLdAVncSJc5B+3+Wbm4x/2cmWM+s73YFcpK2dFPBBPQRB8SYOFu6IG2W8kidxCaCD+/m7LjFxtBmpJdznmtYL/sg718Wkp+2Zivc+8CADrqNOwo7"
                + "+aeTcA9LOIhs8MI9wf6lt0hGsssZ76x2bGcLmcLV/JD7CfhQViwJ/wk5xVpcIzYKb/AbtB92BtDdw7WvnTPFTrUOcjyUsaO27VSm/2OFwRFYEI8XVoi9pS+S"
                + "R34s1ZS6iqWFh6gePOBm4m/Kxk5le7QlW1GzkvXaumU73OPfsj7gK6yDRwt9RFVqL7WUTLGJ2E2ozN83q0FlN0ofkI9OSeeufcz+Zfd1cpNEWpi1dWeArXAL"
                + "GoYVXs9AoZRwEos4J8oKFe67Ieqn7chbZ6dzyBHIGtKLdmFj3I3gEnyEzvGTQrwqj/iP4OB4rMP/QB83HXtBznMHqGR/t75yX3vBVeI3qcGOuEXgJpSK48Qw"
                + "7/hVaZ9UWRonDubqcgqWBvtZOfrNuWOb1girljXYSrZO2bd4Ah7Jfrkd4VH0Bb8WFvGVWLoslhP7Ct1wCfSbI6kXq0PbkK1OMUdy8joLnRLES70sl1sdtIKN"
                + "URZ8EqfnFXmF7RjxFVX4bAu7RVkjupIkkHvOXSdIlpGGtAyr5nYEY+FsNAqXEU4LVDCF/wlphRq4AvLC6+5k9ifPfZWchXZu22PXsG/Ya5wd5DOtydGXHvXF"
                + "i4UJYk5pnDRRKijNEdcLI3E29C8o596kE0hHZ7z9ydpg/c9Sudbucu6RTGyC+xoURh1wayGNOEf8V5wrZuCaNA73RKUgcW+zo/Qq8ZDFThunp3PMqUVkGqEG"
                + "E0EAQvQADcFPMcFf8EJMURXUHnYD3dx+bB69T+oQ0/npZCYLSBkaYB43EygBK6PCOIJnCE+Fj8IuIb8wAI9GHWAu8JXtp0vJBueD3ccuZ7e2L9gjnb5kMc8s"
                + "ZcEq+An5BCyeEvNJf0oBaZ54RjiMx6As8D+3FVP5pBy7FZ9VklXMPmcvc/4md2l6tx/4lyv6U/y3kEVsJ9YTw0J7YQ5egIbBBiC/m5EVpoPJL46kbdxtOhCF"
                + "fqCP2WPOqNNwLiqAV+Mb+CwehXVUFw2Hs8Eydwu7TGU6juQgIslP5pEc9Bd9wd66v0CE3+9fXJ/P6LqwRSgrLML/ou1wAqjL0WcS1ynn/GsPsIfal+yOThmu"
                + "RuPYdTcT7IEW4QVCU/E+R9EPcZr4UPiML6OpsCB4ypbSQWSCc91uZ5exu9gP7FnOCLKQXmAAVICd+KTycBUDoiheFWrz03ajHXAJmOCOYgu549QgYV6Rj4wg"
                + "AfqIHmX/uGvARFgH/UAdeVVrcBecgpqgeXAPOO8+ZQ6tSveSBiQXKUfmkjh6k+5m29x/wP/gOtQf+4VxfEorharCHvwefYf3wD/uBNaV9uCKbtnb7RX2XbsR"
                + "PwvRXKyruxN8g3E4rfCZe+EZ8bQ4WHwtqALGz+FK7jZxLJXjoQL37Ol83W9eU1vSlo5i23nPf8Fv6BhuzM86z08rJCzAZ9FdeBtcdW+wLzQPXUUqkyykIllC"
                + "MtBbdD2b7PYGNaGC9qFcuA8ejuvgr6g12shX/HJltzgbSd+QkbwT9ficIN1DJ7D+bi/QHTbj37+H6wgzhelCdeEi9uDc6A/oum/ZFY7xZO7VH+2z9nuusEnO"
                + "bfKMArc8GArXoE1cW9KKQ8VZYkteU26hPM6FYuCMO5cNpMPJXier88S+bwecxU4dUoTzt5k7GEyD41AD/As3FcYI3QSfMB3fRCmQAhEkuGW4on8hM0h70o/s"
                + "J3noNTqHdeZn+eF9OAIlo/K4CS6GX6P26CD8CdJw7vZmu7n2bSTdSAeyiGhkCW3OSrq5QSbehbdoMY7navmXUEV4iIvijqgnbAUquflYPtqArHdyO8k8+9Zz"
                + "njsbyXKu549dAHOhglgVjgjZxQZiMY690kJ33AvVh9mByT7Q7yQTme7kd4Icu5udqsRPAVPc9CA7TEBf0Dxs4WJCAeEHHoHvIxnlh1VAW3cK50dRep2sIKvI"
                + "HVKaXqVjuY4FwVuwHlZFp5EPF8YZ8EPUGV2AEqzAs8Qa9ppW4SumkSF8VRIZTQsyxr67z8AFuBo1xi9xCaEZv9kb3AgvRDvhdrCKp9+5dBdJdQby/JHJaenc"
                + "csaRdrQjG81RfgSeQhtxc+GOoIqCeE6oKEzDf6OVnInd3KasHZ1NvjmT+Zq+znGnOjHIE3qL3Xavg6NwJiqCd+BvOIQvcn7cQxlRAzgYrHAvMQ8bQRXOqsv8"
                + "frXpdTqQ5XWT3ENgAPShhegT8mCIr6Ou6AHMAbuA5RyvATaIRsg2MousJ99Idyqze2yvuwKMg035tzdhJBQX8ggfcC/8H/oIUzhiP7PvVKC1yTGns1PT6e1c"
                + "dbqQfDSRZXfLgPrcN0riFI6/K8JjYYdQXliNr6En8C5n73/sEk3lGH/irHP+dp459cgvcpiuYrPdMaAzLIieoJZ4M/6Pc74OvoHyoD5wHbjhuqwhV/TaFPG1"
                + "PtqW3qE9WJx7x50HqsC3sCs6hX7xzxnUDb2FFeE0cNY1WTW2kWbhDNlI/iGfSVP6jW5hI9wWoBRU0TXUHT/APiFeeMf18hVK5O+oitzfa3GdWE8Q1/QhvOs3"
                + "nYbEJa+5xr5xv/JccBstxNmECcJGzsQiwjb8A3lROpgWJHLUtuITLkg+OY94bmlHvpJ1dABr6Bbn7HgOpyEBt8cT8V+4ENeIQmgiPAMMt6w7m4XpZFqGZqSF"
                + "6UCuex05kg5wzifAQ7A0Wolu8W4c5DX9hC04jr67+dyh7C6tR3+S4+Qor6kGV77RrKqbFqSCy3A614n13NMs/BxPxSaqjYbCWWCpu5Gd4rNtTt5x19jkPHFq"
                + "kPdkF13M5rlzOd/7cG+7josLPYQOQiZhJ0dGedQK9gBD+O2O8glPIYWIjyvtYPKdzKF1WAY37F4F82ApdBKlwzVxNezDB1BhtAi+AtlAX/cMy8/X9aE1aUNe"
                + "20vahiWzVW5NkATmQi8ajPais2gHrykG+8FLwM/f8FsYpSNpkD4mVzj2ytKDtBnzui/d/WAy5+Bn1AufwK/xLTwLB/AgtBveAh/cGEvD6tEt/F4vnMvON872"
                + "G2QibcGquCVALiihu9zhP+PcQlEBCltxGtwJzYe7OPresiDrTB+RUaQ+aca9I5VM4vxNZid5gqsJf8Lh6DFSeQ5LQptQQbQORkFNzqhk1ol9oyvpANqfLqef"
                + "aEv2nk1yc4EboBv8AhugRWgXWoO6IAYnwo+gNJjmPmBF2FY+2TB5zjlYlK6nRdgbtpUrbEVow+2oEJ6HT/G6ZuJseAl6BT3cQevwBLKRhsgAkoakOMz5k5wk"
                + "HWgWZrFP7m3ubcNQBryKV0XxJ/43Ex6LTsDPQAbFeII7QnPRc2Q2mUJ2EUrGcR7eZHPd+gDCnbAoR9JDroC30XyUG22BMuwOTrmZ3AUsHbtAl9I59B/6m/fu"
                + "MevnSmAnqATPw3xoCFrGv98RSWgJ1//2YB/nYCd2j3am8TSZ6KQgncfz0SE2kGPcBMd5Mgihdngl3oon4QJ4DxJRLTgErHRP8fxbnR4mTbkX/sF/HybVaJie"
                + "ZevccaAFzIguo7p4C88t53gv/uCK+RZmgPXAOPcoE9hwKtIr5CC5RYJ0Atekfdyv04FroC9Mhe3QBnQc/Q+NRJl5kkjP9fKt+6d7nNVgP+lRuoOeohp3m5us"
                + "FVe+WSA9/BuKqDkajyaitsjHu5ABTgTP3MK8C4TOpiX5SYgW5KdE+Yujgmu4J8AomAMdQbnxQI68YdyhznNEjIEH+DmqW4MtoQ539qakGulODnH/vEvnsXZu"
                + "EQDgFTgE2agTXsqTy184Ea9DDqwNp3PnpawZO8t5gegnjrw8dDp1ufJVcn+4S0EReBRmQ/3QAjSHu00cd45ccDkgbl/3PRvA4tgzeoHe499vwS6wP91Hbh+g"
                + "gTEcfaVRTzQItUDx6AAsATcB5rbnPS/IDtI2NDtNQ4vQ4fQN7cMZdcGdAErDj3A0SkHVeALpxr3tHqrPT/oJsnMeLmFvaC3+vplF+pKxXGFycCZ2Ylncn+4R"
                + "MILfbT9Kj9viAbgZ9uJN/LyB8Ciw3BrcEQXejao0HU3kvxdRh87kqy643TguJsNfsCxne2+uRpjny8JwJ0gEc13sLmWlmUFf0Y/Uw1qyEzwXnHbrgSegNbwM"
                + "06NGnE0NUTp0FtaD50BuMMv9wdpx553AvTc3LU3/4spXl31gC/krReNvrwroPxTH1agBzoufox7oDswKu4L17iuuRot4F86S5WQl98M/6N+0LPvF9rh/gXzw"
                + "EezJ+f4HroyLY5szPgdPmB9AUTCFr6vLHtDxtD6vqCNnL6FjmOhudMtyzjeEJ3h+KYf+5KqnwS2wFDwGivCMmcc9wtqyRBajMZqOtebcyOXucAuDY7zr2znr"
                + "S6PGfE16dBN2gW9AfXDQTXRnMZnrRCdakZ/Tn2O2MDvJOroKOAF6QAInoff8dsU511+hUTyH/V/HZR6mc9nF8XNuZSlLI0uFZGxRRIUwSni9NSnbKJUlRZRG"
                + "M0KWbDOIyZKyFiE72ZOKMGrSIMmawVhmRC9NgxD5nfN+6pp/nrmu537u+5zz3U4rnQH6Yj3Z9kcJ0ZUbW2+sv7H/RoUoLbrDvrFkryknUOZy4f1wMtxaoFiB"
                + "X8NMMDtPb8blN3hZckhxtHlI1DMaFK2I1JLsoo3wGJktFXScHtESITaUCqfhSgO0uZmkewvfb2/bQ1bMClpFqltupX0SJ6ZJKU3FecuHuNCImvZqsv4liXIY"
                + "Dq7j+5nR0KgdSO8draIX06y27/dhUhVlaQGOLoWSBYoU+CWkhCKhP6ngTnLVl3aXTYvqopiHSQSV4YbYdLJYvn8izfWgPhMWhaxwBmWfGGqHdVpFx5JCWpN2"
                + "nrRzVDM5mhGlR0XsDcuxnnjUALkkXfDR3zTAvwP6oTbkpg6S5d38ok211lbV7oAnL9giUuIQv+YD5U95RbdqAXpWB+xlaYqW1Iki0sdPWFe7GM0Bd52ipGhJ"
                + "FGwgeJjvLeV/koqvDQrbw4VwOewN40LFMEP/lnay0P+2LnYoSgSxipM+EA2P8sDR7f61d5PAi4qH3jjH5rAmDA+1SJhN9DMpLSl+zUZaJTsGHr6MDkWlqOkX"
                + "a08HE2SX1Nc0zdAc/jJ0PFnnoPSQfN5fwteiE3FWw+px6zy7gZqfYCM/IC30U1hYBu2PCSd0stbQlVJNZnoxtK88O8S46E3wsBDl6wyj+ntJWUOC28ukZtLt"
                + "o2Sl98IDYb1WJU0c98b4dVlyfUfUMhZlH4kfdrE8S/MakiEJukfroslTwweo0r1hu7bBr5uA9ftQ9PZW1HLIBH9Esfam/WzNfBNpcanE6Ksw5HvdpetBVSPN"
                + "ln5SAN+t6T/aKGtnja2pvWyf2AV71n+A8V+ApNHcpOS2oiFHP9HHdJfEyzZvCI5a4gBL0PTR+NpZVGKLxftJHyy36UwQ/jod34pajA0N2dae0HV0fIBn27O8"
                + "bDya2RxmjY2ORPEk4K74wAS5U6fqNX2ck8l4R8WwQ7tqrnQFSc/7aUu1hnarXced6lo/+9Hq+SK/S9JIZC2oZSkVLdTBYC9XRqJ8y+DHGZtlPa0VifZV+9jO"
                + "WEtf61VlphTUnnz7vBbCO0/rEjLfGXlDfvPuZO0kHCAz+oQMsgzFrGfzrYqvA32H0YksbRSGhXlhfhgZHgvH+ZWj8l9ZicN/YBUsPRoF3xM5mRXF2ee4zQ5/"
                + "Xk5JJ3S2BN9vHZqGkmjSa/B3KO6R6jG+2rrbg1bO7sFNh9hOu8+neoDde6SK9tCJOkenwPcG+MZ4qU6qetGF6Q61zihEb5tp2VbfZ/kt8o6clXjU5IBeQc1y"
                + "dbW+oqLjpbhMAK8fWwOY+xVTWhtlR9UtzYKP9TKyQHAJMnpCGAHyBvC+33UE596SU97OdzGpK5yYwJTmR8eih2yOlfNP0b6VKPpEPa6l0OVqIQR8WAvrNLlb"
                + "lngjFH0Cr2tsjUDhKGqq7GM8D0Z9LoX0Se2P/o0mkT6sv6Nt9WWfJ6OXO22yJYK8JJTooFXndRe9q+zgfcN1s57WC7jpF3SijC6TB2U96MtAJ4rj1RujzdFx"
                + "slt/1KgbDtoXL0zWw3pv6ASKeqKal+lhRZ0lMfIubjgeJTpJblkYfRHlRrVtkqkPc5HhuFtnXYWKFQ63hHzdxG2luK2R7PCXXNHaIShYe+vBid12tw/yo95E"
                + "PiJt19bOVNUfr6mr+TJH/iO/+rte20+id8Osjw2gpt12l/fzLG8un0lxZrNQf2ZKR/UrHao19XtJkKP+sv9hY1H0y+wnu6KcqCx3ZbJzbOFUhjwGTm9og9CB"
                + "adUL13QBSN8icfKlN/B08FCCTJHBuT+oKdXO2UvcliDbSTspKG2u5nHbWk3SCroRxTzrI3HRQ/BjIJr3tn1ke3nhm+x5NSRV9kkZband0IoEraUXZQknrvrH"
                + "eNRV+5r6B4PAGbytGG/+1u+VSZIvLdHIb3CPA/q1jtE4PS5voedpfgcY70j+PQeCzqGwHW2tVfQZoG8qG9tAtOW2UD88ihNco/utUaRX5LT38ks20ZqQd35l"
                + "ShbVtXcsC/5+hcvPIms/q9Opao9up5OJGquZ0lOCzPWWbKPf4DmjwPgiO2R3kuo2c1eibBKXOvx+R30KXf5TVqErhWQVylIU7Ztr79po+PQdzGjrK7w4r98n"
                + "NbWvLv5XLb/SCZy7LO9LLArb1H9hsg1IFPlk39I43Fy7yfuD8l6SjaIvAEWlYUY5sLdeX9JrMob9a6pX9s04Ry0rbEaif8QGoWJ14G8R6S+/MKkkna3rcNJp"
                + "KEsV3ScD8euNqFJZP0LGmWWzbZ0dYy/sho4VlBdksfwm5fGyeG2GZ1xBrV+XsmSJPl4J9K2xKTBxFin2L3ucjl/x5/hGMX1O3ycBp/OyyXwuosvJHwfok9sC"
                + "e94qkz8KkBDiYeR564ALNMGlyjGpdL2kReHG76SyN7SELoCJW7yNnwffHew+zsRaM7CRATtS/Jy35VxB3jaEemaCiS7UlEUnasl+H+4Pg6Wd1LOK9+Uyp06+"
                + "xP8il36IjxYhvcbpI1qJmjbRm2qcSGGLv2zb7FMyzhxmnG8P+SjP9sboeb7E8b55VLUKr++md+m3Qib2CV7NM0FOM7vbbsfnn7Ax+HtDX8huMwbfaMW79mg+"
                + "mpmjG2DwPSTg9rB3MB1PB+PPWH2m3JpPWyzGE/0nNrCJclKq6Qvcl8KJBN54XCbLoySkGf4Mip4LL9KZ63m0sqPPoQ8N8KNtcoVJPcBfeb3KfyPYXU+j9fFe"
                + "mNy3GvwssE121qqhhtu9Ev6wTyqBnAnge7F+gF7W0MOofFlZ463QiTnWjZdVpevxIPEHq4TGXvTO6EQsurWCTHECJq7FD2vrfkmSwjIP1P6PW5ItwZ4mt4yi"
                + "hwXxkw1+O+zYIjeRBjrp69odja7Abv2RtJLIV4KLmqSrbPQhyy6xbXTw6X4czifJajktReFeNXbCqyjNOHB0hSl28fK49RZbZkup6RS68pKvIWl3kw1yM7ry"
                + "Ni4/WyeBoXp6HhX4xwOSvBT5ciQJpBG8eAYUbbSi/rrvoXuz5BreNo7p/kASWw6eGuLXafhhhr9Kx3eA875oZR/QvtncnsajrnsbmSu54Laptte2cKSsnpJ5"
                + "0lGKkl2G0osYsmmO/Wp/2z3e3if7AS8HZuawF0VSmg7cTk075QNpLTfDwWR/ALzuti+Y1Td04zZUYjbZt6V8DAfvB3OpsClN++ijegONbyeX/EN/hD7M411t"
                + "2HE6wPY13NaWXpSSAbJXKuPwU3QljJ+rg8hVl3lxC9CXBtLzuGkSkx0Bt9I59bi/7zkkuFT6bOAvju/X0duY0yJ5WcrBj8lMpprfhG5eQouq4N/v+U50rC3q"
                + "nCF5cKqUFoNPPzHZF0liP/pob+a3kMe2sRNutSNWiP/TcI378cLdUhIcJFFVKvraQm/RrSSkGJz3BRC7zd4jVz2PAw9hT/nd4nya/+ntZAVvawYrpvybW/pr"
                + "c1VdK51Q5uXobIwftBWwdwoo3G7X4GEK860gvWS55ICkqrA+lhyRTU7tzXRzSf2v4fRlPKBNRf1eej4GvwnSHP6sZwOL5Fa+f5lefor+V5FsOPgs6Muzn6jo"
                + "OztsQi8H+ndgvIesJRnUBeP96HZvqiute+nnP2r0Dhg/ZUvo9hukqhF8OmU1Qckhrwuqs6SiduDMP7klkZqKsLH1lQqSSQZ40K/YDjCxGFfbbdfRpAH4dWFp"
                + "A3Iy2XJKoEWl1eWILJNk9PIiWXKwP+XV6catYP5+akphH/+LfTcZPd8rF3C3oJdQwaXc8qD84cvoQm06cIybMqnpOimxl6/yyJ+S6XKM1NySRNEHt45nFz8p"
                + "M8ikF3GVp+jb9zj7CNj0LjVloUi92ANK0vEN9K4eueUtptRdH9fiultG/atJ03lRaZJzJtxNx0EjHCrRV+MejWQwin5MrkthLQSSDjK3AdKYJLsNrL1IDqnI"
                + "rMp7HX5hGHXmkSVe4TXfoRP/B9/YSXLcUgAA";
                using var input = new MemoryStream(Convert.FromBase64String(compressedAudio));
                using var decompressor = new GZipStream(input, CompressionMode.Decompress);
                var wav = new MemoryStream();
                decompressor.CopyTo(wav);
                wav.Position = 0;
                _downloadPopPlayer?.Stop();
                _downloadPopPlayer?.Dispose();
                _downloadPopStream?.Dispose();
                _downloadPopStream = wav;
                _downloadPopPlayer = new SoundPlayer(wav);
                _downloadPopPlayer.Load();
                _downloadPopPlayer.Play();
            }
            catch { /* Ses oynatılamasa da indirme devam eder. */ }
        }

        private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading) return;
            string url = UrlBox.Text.Trim();
            if (VideoPreview.Visibility != Visibility.Visible ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUrl) ||
                (parsedUrl.Scheme != "https" && parsedUrl.Scheme != "http"))
            {
                ShowDownloadNotice("Önce geçerli bir bağlantıyı analiz edin.");
                return;
            }
            int[] selected = _videos.Where(v => v.IsSelected).Select(v => v.Index).ToArray();
            if (_isPlaylist && selected.Length == 0)
            {
                ShowDownloadNotice("İndirmek için en az bir video seçin.");
                return;
            }
            string folder = OutputFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                ShowDownloadNotice("Bir çıktı klasörü seçin.");
                return;
            }
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { ShowDownloadNotice("Klasör oluşturulamadı: " + ex.Message); return; }

            bool audioOnly = AudioOnlyMode.IsChecked == true;
            string format = ((FormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "MP4").ToLowerInvariant();
            string? height = (ResolutionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            string? fps = (FpsCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            int audioBitrate = GetAudioBitrate();
            string pcmRate = (AudioPcmCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "44100";
            PlayDownloadPop();
            var historyEntry = new HistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = VideoTitle.Text,
                CreatedAt = DateTime.Now,
                Status = "active",
                IsPlaylist = _isPlaylist,
                ExpectedCount = _isPlaylist ? selected.Length : 1,
                ThumbnailUrl = _isPlaylist ? (_videos.FirstOrDefault()?.ThumbnailUrl ?? "") : (ThumbnailImage.Source as BitmapImage)?.UriSource?.ToString() ?? "",
                ThumbnailUrl2 = _isPlaylist ? (_videos.Skip(1).FirstOrDefault()?.ThumbnailUrl ?? "") : "",
                ThumbnailUrl3 = _isPlaylist ? (_videos.Skip(2).FirstOrDefault()?.ThumbnailUrl ?? "") : "",
                Files = new List<HistoryFile>()
            };
            _history.Insert(0, historyEntry);
            SaveHistory(); RefreshHistory();
            _currentDownloadEntry = historyEntry;
            _isDownloading = true;
            _downloadPauseRequested = false;
            _resumeRequested = false;
            _downloadCancelRequested = false;
            _currentProgress = 0;
            _completedItems = 0;
            _totalItems = historyEntry.ExpectedCount;
            _downloadUnique = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..6];
            StartDownloadButton.IsEnabled = false;
            DownloadProgressPanel.Visibility = Visibility.Visible;
            PauseDownloadButton.IsEnabled = true;
            PauseDownloadButton.Content = "⏸  Duraklat";
            CancelDownloadButton.IsEnabled = true;
            SetDownloadProgress(0, "İndirme başlatılıyor...");
            DownloadNotice.Visibility = Visibility.Collapsed;
            DownloadItemCount.Visibility = _isPlaylist ? Visibility.Visible : Visibility.Collapsed;
            DownloadItemCount.Text = $"0/{_totalItems} içerik";
            try
            {
                string[] downloadedFiles;
                while (true)
                {
                    _downloadCancellation = new CancellationTokenSource();
                    try
                    {
                        downloadedFiles = await RunDownloadAsync(url, folder, selected, audioOnly, format,
                            height, fps, audioBitrate, pcmRate, _downloadUnique,
                            _downloadCancellation.Token, (completed, streamPercent, message) =>
                            {
                                Dispatcher.InvokeAsync(() =>
                                {
                                    if (_downloadPauseRequested || _downloadCancelRequested) return;
                                    _completedItems = Math.Max(_completedItems, Math.Clamp(completed, 0, _totalItems));
                                    // Sağ panel: yalnızca mevcut akışın yt-dlp yüzdesi.
                                    // Birleştirme sırasında yüzde yerine aşama bilgisi.
                                    SetDownloadProgress(streamPercent, message);
                                    DownloadItemCount.Text = $"{_completedItems}/{_totalItems} içerik";
                                    // Geçmiş: yalnızca tamamen tamamlanmış içeriklerin oranı.
                                    if (_currentDownloadEntry != null)
                                    {
                                        _currentDownloadEntry.ProgressPercent =
                                            100d * _completedItems / Math.Max(1, _totalItems);
                                        HistoryList?.Items.Refresh();
                                    }
                                });
                            });
                        break;
                    }
                    catch (OperationCanceledException) when (_downloadPauseRequested && !_downloadCancelRequested)
                    {
                        _downloadCancellation.Dispose();
                        _downloadCancellation = null;
                        _resumeDownload ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        historyEntry.Status = "paused";
                        SaveHistory(); RefreshHistory();
                        DownloadProgressInfo.Text = "Duraklatıldı. Devam Et ile sürdürebilirsiniz.";
                        await _resumeDownload.Task;
                        _downloadPauseRequested = false;
                        _resumeRequested = false;
                        if (_downloadCancelRequested) throw new OperationCanceledException();
                        historyEntry.Status = "active";
                        SaveHistory(); RefreshHistory();
                        SetDownloadProgress(_currentProgress, "İndirme devam ediyor...");
                    }
                    finally
                    {
                        _downloadCancellation?.Dispose();
                        _downloadCancellation = null;
                    }
                }
                historyEntry.Files = downloadedFiles.Select(path => new HistoryFile
                {
                    Name = Path.GetFileName(path),
                    Path = path,
                    Bytes = File.Exists(path) ? new FileInfo(path).Length : 0,
                    ThumbnailUrl = _isPlaylist
                        ? (_videos.FirstOrDefault(v => Path.GetFileName(path).Contains(v.VideoId, StringComparison.OrdinalIgnoreCase))?.ThumbnailUrl
                            ?? _videos.FirstOrDefault()?.ThumbnailUrl ?? "")
                        : historyEntry.ThumbnailUrl
                }).ToList();
                historyEntry.Status = "completed";
                historyEntry.ProgressPercent = 100;
                SetDownloadProgress(100, "İndirme tamamlandı.");
                DownloadItemCount.Text = $"{historyEntry.Files.Count}/{_totalItems} içerik";
                SaveHistory(); RefreshHistory();
                DownloadNotice.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                historyEntry.Status = "cancelled";
                SaveHistory(); RefreshHistory();
                DownloadNotice.Visibility = Visibility.Collapsed;
                CleanupPartialDownloads(folder, _downloadUnique);
            }
            catch (Exception ex)
            {
                historyEntry.Status = "failed";
                SaveHistory(); RefreshHistory();
                ShowDownloadNotice("İndirme kontrolü: " + ex.Message);
            }
            finally
            {
                _downloadCancellation?.Dispose();
                _downloadCancellation = null;
                _resumeDownload = null;
                _currentDownloadEntry = null;
                _isDownloading = false;
                _downloadPauseRequested = false;
                _downloadCancelRequested = false;
                StartDownloadButton.IsEnabled = true;
                PauseDownloadButton.IsEnabled = false;
                CancelDownloadButton.IsEnabled = false;
                DownloadProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void PauseDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isDownloading || _downloadCancelRequested) return;
            if (_downloadPauseRequested)
            {
                if (_resumeRequested) return;
                _resumeRequested = true;
                PauseDownloadButton.Content = "⏸  Duraklat";
                PauseDownloadButton.IsEnabled = true;
                _resumeDownload?.TrySetResult(true);
            }
            else
            {
                _downloadPauseRequested = true;
                _resumeDownload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                PauseDownloadButton.Content = "▶  Devam Et";
                _downloadCancellation?.Cancel();
                DownloadProgressInfo.Text = "İndirme duraklatılıyor...";
            }
        }

        private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isDownloading) return;
            _downloadCancelRequested = true;
            _downloadPauseRequested = false;
            _downloadCancellation?.Cancel();
            _resumeDownload?.TrySetResult(true);
            PauseDownloadButton.IsEnabled = false;
            CancelDownloadButton.IsEnabled = false;
            DownloadProgressInfo.Text = "İndirme iptal ediliyor...";
        }

        // Fare tekerleğiyle yumuşak, ivmeli kaydırma. Üç listedeki scrollbar aynı mor temayı kullanır.
        private readonly Dictionary<ScrollViewer, (DispatcherTimer Timer, double Target)> _scrollAnimations = new();

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void SmoothScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not DependencyObject owner) return;
            var viewer = FindVisualChild<ScrollViewer>(owner);
            if (viewer == null || viewer.ScrollableHeight <= 0) return;
            e.Handled = true;
            if (!_scrollAnimations.TryGetValue(viewer, out var state))
            {
                var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
                state = (timer, viewer.VerticalOffset);
                _scrollAnimations[viewer] = state;
                timer.Tick += (_, _) =>
                {
                    if (!_scrollAnimations.TryGetValue(viewer, out var current)) { timer.Stop(); return; }
                    double difference = current.Target - viewer.VerticalOffset;
                    if (Math.Abs(difference) < 0.5)
                    {
                        viewer.ScrollToVerticalOffset(current.Target);
                        timer.Stop();
                        return;
                    }
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset + difference * 0.25);
                };
            }
            // Playlist: her tekerlek kademesinde yaklaşık bir satır; geçmişte 60 piksel.
            double pixelsPerNotch = ReferenceEquals(sender, PlaylistList) ? 39d : 60d;
            // Touchpad / hızlı tekerlek büyük delta gönderebilir; her olayda en fazla bir satır.
            double notches = Math.Clamp(e.Delta / 120d, -1d, 1d);
            double target = Math.Clamp(state.Target - notches * pixelsPerNotch, 0, viewer.ScrollableHeight);
            _scrollAnimations[viewer] = (state.Timer, target);
            if (!state.Timer.IsEnabled) state.Timer.Start();
        }

        private void SetDownloadProgress(double percent, string message)
        {
            _currentProgress = Math.Clamp(percent, 0, 100);
            DownloadProgressBar.Value = _currentProgress;
            DownloadProgressPercent.Text = _currentProgress.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            DownloadProgressInfo.Text = message;
        }

        private static void CleanupPartialDownloads(string folder, string unique)
        {
            if (string.IsNullOrWhiteSpace(unique) || !Directory.Exists(folder)) return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (!Path.GetFileName(file).Contains(unique, StringComparison.OrdinalIgnoreCase)) continue;
                    if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        private void ShowDownloadNotice(string message)
        {
            DownloadNoticeText.Text = message;
            DownloadNotice.Visibility = Visibility.Visible;
        }

        private static async Task<string[]> RunDownloadAsync(string url, string folder, int[] indices,
            bool audioOnly, string extension, string? height, string? fps, int audioBitrate,
            string pcmRate, string unique, CancellationToken cancellationToken,
            Action<int, double, string> progressCallback)
        {
            string tools = FindToolsDirectory();
            string ytDlp = Path.Combine(tools, "yt-dlp.exe");
            if (!File.Exists(ytDlp)) ytDlp = "yt-dlp";
            string ffmpeg = Path.Combine(tools, "ffmpeg.exe");
            string ffprobe = Path.Combine(tools, "ffprobe.exe");
            string deno = Path.Combine(tools, "deno.exe");
            if (!audioOnly && (!File.Exists(ffmpeg) || !File.Exists(ffprobe)))
                throw new FileNotFoundException("Video araçları çalıştırma klasöründe bulunamadı. Tools dosyalarının derleme çıktısına kopyalandığını kontrol edin.");
            if (audioOnly && !File.Exists(ffmpeg))
                throw new FileNotFoundException("Ses dönüştürme aracı bulunamadı.");

            string template = "%(title).150B [%(id)s] " + unique + ".%(ext)s";
            var info = new ProcessStartInfo(ytDlp)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            void Add(params string[] args) { foreach (string arg in args) info.ArgumentList.Add(arg); }
            Add("--newline", "--progress", "--progress-template", "download:AVM_PROGRESS:%(progress._percent_str)s", "--socket-timeout", "20", "--retries", "3", "--fragment-retries", "3", "--continue", "--windows-filenames", "-P", folder,
                "-o", template, "--print", "after_move:AVM_FILE:%(filepath)s", "--no-playlist");
            if (indices.Length > 0)
            {
                info.ArgumentList.Remove("--no-playlist");
                Add("--yes-playlist", "--playlist-items", string.Join(",", indices));
            }
            if (File.Exists(deno)) Add("--js-runtimes", "deno:" + deno);
            Add("--ffmpeg-location", tools);
            if (audioOnly)
            {
                Add("-f", "bestaudio/best", "-x", "--audio-format", extension);
                if (extension is "mp3" or "aac" or "vorbis" or "opus" or "m4a")
                    Add("--audio-quality", audioBitrate.ToString(CultureInfo.InvariantCulture) + "K");
                if (extension == "wav")
                    Add("--postprocessor-args", "ExtractAudio+ffmpeg_o:-ar " + pcmRate + " -ac 2 -sample_fmt s16");
            }
            else
            {
                Add("-f", BuildDownloadSelector(extension, height, fps), "--merge-output-format", extension, "--no-keep-video");
            }
            Add("--", url);
            using var process = new Process { StartInfo = info };
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new Exception("İndirme işlemi başlatılamadı.");
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            });
            var filePaths = new List<string>();
            var errors = new Queue<string>();
            // Tekli indirmede %0'da kalma nedenini teşhis etmek için gerçek yt-dlp çıktısı.
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVM", "logs");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "download-" + unique + ".log");
            object logLock = new();
            void LogLine(string channel, string line)
            {
                try
                {
                    lock (logLock)
                        File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss") + " [" + channel + "] " + line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
            LogLine("AVM", "yt-dlp started; playlist=" + (indices.Length > 0) + "; format=" + extension);
            int finished = 0;
            int count = Math.Max(1, indices.Length);
            double currentStreamPercent = 0;
            bool merging = false;
            object progressLock = new();
            // yt-dlp ayrı video/ses akışlarını sırayla indirir.
            // Her akışın yüzdesi yeniden 0'dan başlayabilir.
            void HandleProgressLine(string line)
            {
                // Hata ve aşama mesajlarını sakla; böylece %0'da beklemenin nedeni görülebilir.
                var match = Regex.Match(line,
                    @"(?:AVM_PROGRESS:|\[download\]\s+)(?:\x1B\[[0-9;]*m)*(?<p>\d+(?:[.,]\d+)?)\s*%",
                    RegexOptions.IgnoreCase);
                if (match.Success && double.TryParse(match.Groups["p"].Value.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                {
                    int done;
                    lock (progressLock)
                    {
                        done = finished;
                        currentStreamPercent = Math.Clamp(p, 0, 100);
                        merging = false;
                    }
                    progressCallback(done, currentStreamPercent,
                        $"{(indices.Length > 0 ? $"{Math.Min(done + 1, count)}. içerik · " : "")}{(currentStreamPercent >= 99.95 ? "Akış tamamlandı, sonraki aşama bekleniyor..." : "Akış indiriliyor")}");
                }
                else if (line.Contains("[youtube]", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("[info]", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("[download] Destination:", StringComparison.OrdinalIgnoreCase))
                {
                    int done;
                    double percent;
                    lock (progressLock) { done = finished; percent = currentStreamPercent; }
                    string stage = line.Contains("Destination:", StringComparison.OrdinalIgnoreCase)
                        ? "Dosya hazırlanıyor / indiriliyor..."
                        : "Bağlantı ve akışlar hazırlanıyor...";
                    progressCallback(done, percent, stage);
                }
                else if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("[ExtractAudio]", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("[VideoRemuxer]", StringComparison.OrdinalIgnoreCase))
                {
                    int done;
                    lock (progressLock) { done = finished; merging = true; }
                    progressCallback(done, currentStreamPercent, "Birleştiriliyor / dönüştürülüyor...");
                }
            }
            async Task ReadStdout()
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    string path = line.Trim();
                    if (path.StartsWith("AVM_FILE:", StringComparison.Ordinal))
                        path = path.Substring("AVM_FILE:".Length).Trim().Trim('"');
                    else
                    {
                        LogLine("OUT", line);
                        HandleProgressLine(line);
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        int done;
                        lock (progressLock)
                        {
                            if (filePaths.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                            filePaths.Add(path);
                            finished++;
                            currentStreamPercent = 0;
                            merging = false;
                            done = finished;
                        }
                        progressCallback(done, 0, done < count ? "Sonraki içerik hazırlanıyor..." : "İndirme tamamlanıyor...");
                    }
                    else HandleProgressLine(line);
                }
            }
            async Task ReadStderr()
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                {
                    LogLine("ERR", line);
                    HandleProgressLine(line);
                    if (errors.Count >= 20) errors.Dequeue();
                    errors.Enqueue(line);
                }
            }
            Task stdoutTask = ReadStdout();
            Task stderrTask = ReadStderr();
            var startedAt = DateTime.UtcNow;
            using var heartbeatStop = new CancellationTokenSource();
            Task heartbeat = Task.Run(async () =>
            {
                while (!heartbeatStop.IsCancellationRequested)
                {
                    try { await Task.Delay(12000, heartbeatStop.Token); }
                    catch (OperationCanceledException) { break; }
                    if (process.HasExited) break;
                    double percent;
                    int done;
                    lock (progressLock) { percent = currentStreamPercent; done = finished; }
                    if (percent <= 0.001)
                    {
                        int seconds = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
                        progressCallback(done, 0, $"Akış bekleniyor ({seconds} sn). Sorun sürerse AVM günlük dosyasını kontrol edin.");
                    }
                }
            });
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(stdoutTask, stderrTask);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(); } catch { }
                throw;
            }
            heartbeatStop.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            if (process.ExitCode != 0)
                throw new Exception("İndirme veya birleştirme başarısız: " + string.Join(" ", errors) + " | Günlük: " + logPath);

            var files = filePaths.Where(File.Exists).Concat(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path).Contains(unique, StringComparison.OrdinalIgnoreCase))
                    .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        && !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                        && !path.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
                    .Where(path => new FileInfo(path).Length > 0))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            if (files.Length == 0)
                throw new Exception("İndirme tamamlandı ancak oluşan dosyanın yolu belirlenemedi.");
            if (indices.Length > 0 && files.Length < indices.Length)
                throw new Exception($"Seçilen {indices.Length} içerikten yalnızca {files.Length} dosya bulundu. Eksik içerikler nedeniyle işlem tamamlandı sayılmadı. yt-dlp çıktısını kontrol edin.");
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(file).Length == 0)
                    throw new Exception("İndirilen dosya boş: " + Path.GetFileName(file));
                if (!audioOnly) await VerifyAudioInVideoAsync(ffprobe, file);
            }
            return files;
        }

        private static async Task VerifyAudioInVideoAsync(string ffprobe, string file)
        {
            var info = new ProcessStartInfo(ffprobe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string arg in new[] { "-v", "error", "-select_streams", "a",
                "-show_entries", "stream=codec_type", "-of", "default=noprint_wrappers=1:nokey=1", file })
                info.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = info };
            if (!process.Start()) throw new Exception("Ses doğrulaması başlatılamadı.");
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string output = await outputTask;
            string error = await errorTask;
            if (process.ExitCode != 0 || !output.Split('\n').Any(x => x.Trim() == "audio"))
                throw new Exception("Video oluşturuldu ancak ses akışı doğrulanamadı: " +
                    Path.GetFileName(file) + (string.IsNullOrWhiteSpace(error) ? "" : " — " + error.Trim()));
        }

        // Akış verilerinden boyut tahmini. Bitrate biliniyorsa yaklaşık sonuçtur.
        private static double EstimateStreamSize(JsonElement item, double duration)
        {
            if (item.ValueKind != JsonValueKind.Object) return 0;
            if (!item.TryGetProperty("formats", out JsonElement formats) || formats.ValueKind != JsonValueKind.Array) return 0;
            double bestVideo = 0, bestAudio = 0, bestCombined = 0;
            foreach (JsonElement format in formats.EnumerateArray())
            {
                if (format.ValueKind != JsonValueKind.Object) continue;
                double size = GetNumber(format, "filesize");
                if (size <= 0) size = GetNumber(format, "filesize_approx");
                if (size <= 0 && duration > 0)
                {
                    double bitrate = GetNumber(format, "tbr");
                    if (bitrate > 0) size = bitrate * 1000 * duration / 8;
                }
                string vcodec = GetString(format, "vcodec");
                string acodec = GetString(format, "acodec");
                bool hasVideo = !string.IsNullOrEmpty(vcodec) && vcodec != "none";
                bool hasAudio = !string.IsNullOrEmpty(acodec) && acodec != "none";
                if (hasVideo && hasAudio) bestCombined = Math.Max(bestCombined, size);
                else if (hasVideo) bestVideo = Math.Max(bestVideo, size);
                else if (hasAudio) bestAudio = Math.Max(bestAudio, size);
            }
            return Math.Max(bestCombined, bestVideo + bestAudio);
        }

        // VİDEO KÜÇÜK RESMİ
        private void SetThumbnail(string url)
        {
            ThumbnailImage.Source = null;

            if (string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                BitmapImage bitmap = new BitmapImage();

                bitmap.BeginInit();
                bitmap.UriSource = new Uri(url);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                ThumbnailImage.Source = bitmap;
            }
            catch
            {
                ThumbnailImage.Source = null;
            }
        }

        // JSON'DAN KÜÇÜK RESİM AL
        private static string GetThumbnail(JsonElement root)
        {
            string thumbnail = GetString(root, "thumbnail");

            if (!string.IsNullOrWhiteSpace(thumbnail))
                return thumbnail;

            if (root.TryGetProperty(
                "thumbnails", out JsonElement thumbnails) &&
                thumbnails.ValueKind == JsonValueKind.Array)
            {
                string result = "";

                foreach (JsonElement item in thumbnails.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        string current = GetString(item, "url");

                        if (!string.IsNullOrWhiteSpace(current))
                            result = current;
                    }
                }

                return result;
            }

            return "";
        }

        // JSON STRING OKUMA
        private static string GetString(
            JsonElement element,
            string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return "";

            if (!element.TryGetProperty(
                property, out JsonElement value))
                return "";

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";

            return "";
        }

        // JSON SAYI OKUMA
        private static double GetNumber(
            JsonElement element,
            string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return 0;

            if (!element.TryGetProperty(
                property, out JsonElement value))
                return 0;

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsed))
            {
                return parsed;
            }

            return 0;
        }

        // SÜRE BİÇİMLENDİRME
        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0 ||
                double.IsNaN(seconds) ||
                double.IsInfinity(seconds))
                return "--:--";

            TimeSpan time = TimeSpan.FromSeconds(seconds);

            if (time.TotalHours >= 1)
                return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

            return time.ToString(@"mm\:ss");
        }

        // DOSYA BOYUTU BİÇİMLENDİRME
        private static string FormatBytes(double bytes)
        {
            if (bytes <= 0)
                return "Bilinmiyor";

            string[] units = { "B", "KB", "MB", "GB", "TB" };

            int unit = 0;

            while (bytes >= 1024 && unit < units.Length - 1)
            {
                bytes /= 1024;
                unit++;
            }

            return bytes.ToString(
                "0.##", CultureInfo.InvariantCulture)
                + " " + units[unit];
        }
    }

    public class HistoryFile
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public long Bytes { get; set; }
        public string ThumbnailUrl { get; set; } = "";
        public string SizeText => Bytes < 1024 ? Bytes + " B" :
            Bytes < 1048576 ? (Bytes / 1024d).ToString("0.##") + " KB" :
            Bytes < 1073741824 ? (Bytes / 1048576d).ToString("0.##") + " MB" :
            (Bytes / 1073741824d).ToString("0.##") + " GB";
    }

    public class HistoryEntry
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string Status { get; set; } = "active";
        public double ProgressPercent { get; set; }
        public bool IsPlaylist { get; set; }
        public string ThumbnailUrl { get; set; } = "";
        public Visibility ActiveProgressVisibility => Status == "active" || Status == "paused" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlaylistBadgeVisibility => IsPlaylist ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SingleThumbVisibility => IsPlaylist ? Visibility.Collapsed : Visibility.Visible;
        public string ThumbnailUrl2 { get; set; } = "";
        public string ThumbnailUrl3 { get; set; } = "";
        public int ExpectedCount { get; set; }
        public bool IsSelected { get; set; }
        public List<HistoryFile> Files { get; set; } = new();
        public string DisplayTitle => (IsPlaylist ? "▤  " : "▶  ") + Title;
        public string DateLabel => CreatedAt.ToString("dd MMM yyyy HH:mm", new CultureInfo("tr-TR"));
        public string StatusLabel => Status switch
        {
            "completed" => "✓ Tamamlandı",
            "active" => $"◌ %{ProgressPercent:0} İndiriliyor",
            "paused" => $"⏸ %{ProgressPercent:0} Duraklatıldı",
            "cancelled" => "✕ İptal edildi",
            "interrupted" => "! Yarım kaldı",
            _ => "! Başarısız"
        };
        public string Description => $"{(IsPlaylist ? "Oynatma listesi · " : "")}{(Files.Count > 0 ? Files.Count : ExpectedCount)} video · " +
            (Files.Count == 0 ? "Boyut bekleniyor" :
            (Files.Sum(f => f.Bytes) / 1048576d).ToString("0.##") + " MB");
    }

    // OYNATMA LİSTESİ VİDEO MODELİ
    public class PlaylistVideo : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public int Index { get; set; }
        public string Url { get; set; } = "";
        public string VideoId { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";

        public string Title { get; set; } = "";

        public double Duration { get; set; }

        public string DurationText { get; set; } = "";

        public bool IsSelected
        {
            get => _isSelected;

            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;

                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(
                        nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler?
            PropertyChanged;
    }
}

