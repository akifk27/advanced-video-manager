using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;

namespace avmsoftware;

public partial class editwindow : UserControl
{
    private record VideoSegment(double Start, double End, double Gain = 1)
    {
        public double Duration => End - Start;
    }

    private record EditState(VideoSegment[] Clips, double Position);
    private record SourceVideo(string Path, double Duration, double Fps, bool HasAudio, long Bytes, int Width, int Height, double VideoMbps, double TotalMbps, double AudioKbps);
    private record OutputOptions(int Width, int Height, double Fps, string VideoCodec, string AudioCodec,
        int VideoRate, int AudioRate, string ColorFilter, bool AudioOnly, bool Gif);

    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer repeatTimer = new();
    private readonly string tempDirectory = Path.Combine(Path.GetTempPath(), "AVM", Guid.NewGuid().ToString("N"));
    private readonly Stack<EditState> undo = new();
    private VideoSegment[] clips = [];
    private SourceVideo? source;
    private CancellationTokenSource? previewJob;
    private CancellationTokenSource? frameJob;
    private string? previewPath;
    private string? openingPath;
    private Window? fullscreen;
    private double previewFps = 30;
    private long previewFrames;
    private double position;
    private double previousVolume = .7;
    private int repeatDirection;
    private bool initialized;
    private bool changingBitrate;
    private bool initializingSource;
    private bool requireSmallerOutput;
    private static readonly string[] Resolutions = ["4K", "1440p", "1080p", "720p", "480p", "360p", "240p", "144p"];
    private static readonly int[] ResolutionSizes = [2160, 1440, 1080, 720, 480, 360, 240, 144];
    private static readonly string[] Ratios = ["16:9 (Geniş Ekran)", "4:3 (Standart)", "1:1 (Kare)", "21:9 (Sinematik)", "9:16 (Dikey)"];
    private bool applyingQuick;
    private bool openingQuick;
    private double? videoBitrateMbps;
    private double? audioBitrateKbps;
    private System.Media.SoundPlayer? quickSound;

    private static readonly (int Contrast, int Saturation)[] QuickColors =
        [(5, 8), (3, 3), (6, 5), (3, 3), (0, 0), (7, -5)];

    private static readonly double[,] QuickStandardRates =
    {
        {40, 18, 9, 5.5}, {35, 16, 8, 5}, {45, 20, 10, 6},
        {32, 14, 7, 4}, {25, 12, 6, 3.5}
    };

    private static readonly double[,] QuickHighRates =
    {
        {60, 27, 13.5, 8}, {53, 24, 12, 7.5}, {68, 30, 15, 9},
        {48, 21, 10.5, 6}, {38, 18, 9, 5}
    };
    private enum EditTool { Pointer, Cut, Delete }
    private EditTool selectedTool;
    private Cursor? scissorsCursor;

    private static Cursor CreateScissorsCursor()
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            var glyph = new FormattedText("✂", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Symbol"), 25, Brushes.White, 1);
            drawing.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.8), glyph.BuildGeometry(new Point(0, -1)));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write((ushort)0);
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write((byte)size);
        writer.Write((byte)size);
        writer.Write((ushort)0);
        writer.Write((ushort)25);
        writer.Write((ushort)10);
        writer.Write(40 + pixels.Length + size * 4);
        writer.Write(22);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(new byte[24]);
        for (int row = size - 1; row >= 0; row--)
            writer.Write(pixels, row * size * 4, size * 4);
        writer.Write(new byte[size * 4]);
        writer.Flush();
        data.Position = 0;
        return new Cursor(data);
    }
    private Rectangle? playhead;
    private Rectangle? cutGuide;
    private int gainClipIndex = -1;
    private EditState? gainEditStart;
    private bool syncingClipGain;
    private Rectangle? overviewMarker;
    private double overviewDragOffset;
    private float[] peaks = [];
    private CancellationTokenSource? waveformJob;
    private bool audioOnly;
    private bool gif;
    private bool playing;
    private bool seeking;
    private bool syncingSeek;
    private bool resumeAfterPreview;
    private bool loadingSource;
    private bool exporting;
    internal bool HasActiveExport => exporting;
    private bool hasEstimate;
    private bool closing;
    private double Duration => clips.Sum(clip => clip.Duration);

    public editwindow()
    {
        InitializeComponent();
        SetItems(ResolutionBox, Resolutions, 2);
        SetItems(RatioBox, ["16:9 (Geniş Ekran)", "4:3 (Standart)", "1:1 (Kare)", "21:9 (Sinematik)", "9:16 (Dikey)"], 0);
        SetItems(FpsBox, ["Kaynak FPS", "24 FPS", "25 FPS", "30 FPS", "50 FPS", "60 FPS"], 0);
        SetItems(QuickType, ["Doğa / Manzara", "İnsan / Vlog", "Oyun / Aksiyon", "Animasyon / Anime", "Ekran Kaydı", "Sinematik"], 0);
        SetItems(QuickSize, ["Boyutu Küçült", "Dengeli", "Kaliteyi Koru"], 1);
        ConfigureFormats();
        OutputFolder.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        playbackTimer.Tick += (_, _) =>
        {
            if (playing && !seeking)
                SetPosition(Player.Position.TotalSeconds);
        };
        repeatTimer.Tick += (_, _) =>
        {
            repeatTimer.Interval = TimeSpan.FromMilliseconds(85);
            if (frameJob == null) StepFrame(repeatDirection);
        };
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window owner)
            {
                owner.Closed -= OwnerClosed;
                owner.Closed += OwnerClosed;
            }
            DrawTimeline();
        };
        initialized = true;
        RefreshDimensions();
        RefreshBitrate();
    }

    private static void SetItems(ComboBox box, string[] items, int index)
    {
        box.ItemsSource = items;
        box.SelectedIndex = index;
    }

    private void ConfigureFormats()
    {
        SetItems(FormatBox, gif ? ["GIF"] : audioOnly ? ["MP3", "M4A", "WAV", "FLAC", "OGG", "OPUS", "AAC", "AIFF", "AC3", "WMA", "CAF", "AMR", "AU", "MP2"] : ["MP4", "MKV", "MOV", "WebM", "AVI", "MPEG", "TS", "M2TS", "M4V", "FLV", "WMV", "3GP", "OGV", "VOB", "ASF", "MXF", "DV", "F4V"], 0);
        UpdateCodecs();
    }

    private void UpdateCodecs()
    {
        switch (FormatBox.SelectedItem as string)
        {
            case "GIF": break;
            case "FLAC": SetItems(AudioCodecBox, ["FLAC"], 0); break;
            case "OGG": SetItems(AudioCodecBox, ["Vorbis", "Opus"], 0); break;
            case "OPUS": SetItems(AudioCodecBox, ["Opus"], 0); break;
            case "AAC": SetItems(AudioCodecBox, ["AAC"], 0); break;
            case "AIFF": SetItems(AudioCodecBox, ["PCM (BE)"], 0); break;
            case "AC3": SetItems(AudioCodecBox, ["AC-3"], 0); break;
            case "CAF": SetItems(AudioCodecBox, ["PCM", "ALAC"], 0); break;
            case "AMR": SetItems(AudioCodecBox, ["AMR-NB"], 0); break;
            case "AU": SetItems(AudioCodecBox, ["PCM (BE)"], 0); break;
            case "MP2": SetItems(AudioCodecBox, ["MP2"], 0); break;
            case "WMA": SetItems(AudioCodecBox, ["WMA"], 0); break;
            case "AVI":
                SetItems(VideoCodecBox, ["MPEG-4", "MJPEG"], 0);
                SetItems(AudioCodecBox, ["MP3", "PCM"], 0);
                break;
            case "MPEG": case "VOB": case "MXF":
                SetItems(VideoCodecBox, ["MPEG-2"], 0);
                SetItems(AudioCodecBox, [(string)FormatBox.SelectedItem == "MXF" ? "PCM" : "MP2"], 0);
                break;
            case "WMV": case "ASF":
                SetItems(VideoCodecBox, ["WMV"], 0);
                SetItems(AudioCodecBox, ["WMA"], 0);
                break;
            case "OGV":
                SetItems(VideoCodecBox, ["Theora"], 0);
                SetItems(AudioCodecBox, ["Vorbis"], 0);
                break;
            case "DV":
                SetItems(VideoCodecBox, ["DV"], 0);
                SetItems(AudioCodecBox, ["PCM"], 0);
                break;
            case "3GP": case "FLV": case "F4V":
                SetItems(VideoCodecBox, ["H.264 (avc1)"], 0);
                SetItems(AudioCodecBox, ["AAC"], 0);
                break;
            case "MP3": SetItems(AudioCodecBox, ["MP3"], 0); break;
            case "WAV": SetItems(AudioCodecBox, ["PCM"], 0); break;
            case "M4A": SetItems(AudioCodecBox, ["AAC"], 0); break;
            case "MP4": case "M4V":
                SetItems(VideoCodecBox, ["H.264 (avc1)", "H.265 (HEVC)"], 0);
                SetItems(AudioCodecBox, ["AAC", "MP3", "AC-3", "E-AC-3", "ALAC"], 0);
                break;
            case "MOV":
                SetItems(VideoCodecBox, ["H.264 (avc1)", "H.265 (HEVC)"], 0);
                SetItems(AudioCodecBox, ["AAC", "PCM", "PCM (BE)", "ALAC", "AC-3"], 0);
                break;
            case "TS": case "M2TS":
                SetItems(VideoCodecBox, ["H.264 (avc1)", "H.265 (HEVC)"], 0);
                SetItems(AudioCodecBox, ["AAC", "MP2", "AC-3", "E-AC-3"], 0);
                break;
            case "WebM":
                SetItems(VideoCodecBox, ["VP9"], 0);
                SetItems(AudioCodecBox, ["Opus", "Vorbis"], 0);
                break;
            default:
                SetItems(VideoCodecBox, ["H.264 (avc1)", "H.265 (HEVC)"], 0);
                SetItems(AudioCodecBox, FormatBox.SelectedItem as string == "MKV" ? ["AAC", "Opus", "Vorbis", "MP3", "AC-3", "E-AC-3", "FLAC", "ALAC", "PCM"] : ["AAC"], 0);
                break;
        }
    }

    private static string Tool(string name)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null)
        {
            string path = Path.Combine(folder.FullName, "Tools", name + ".exe");
            if (File.Exists(path))
                return path;
            folder = folder.Parent;
        }
        throw new FileNotFoundException(name + ".exe, Tools klasöründe bulunamadı.");
    }

    private static async Task<string> RunTool(string name, IEnumerable<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(Tool(name))
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException(name + " başlatılamadı.");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException((await error).Trim());
        return await output;
    }

    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    private static double ReadNumber(JsonElement element, string key)
    {
        return element.TryGetProperty(key, out var value) &&
               double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? number : 0;
    }

    private static double ReadFps(JsonElement stream)
    {
        foreach (string key in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            if (!stream.TryGetProperty(key, out var value))
                continue;
            string[] parts = value.ToString().Split('/');
            if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out double numerator) &&
                double.TryParse(parts[1], CultureInfo.InvariantCulture, out double denominator) && numerator > 0 && denominator > 0)
                return numerator / denominator;
        }
        return 30;
    }

    private void OpenVideo(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Video dosyaları|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv;*.ts;*.mpeg;*.mpg|Tüm dosyalar|*.*"
        };
        if (dialog.ShowDialog() == true)
            _ = LoadVideo(dialog.FileName);
    }

    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !exporting && !loadingSource
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await LoadVideo(files[0]);
        e.Handled = true;
    }

    private async Task LoadVideo(string path)
    {
        if (loadingSource || exporting || applyingQuick)
            return;

        loadingSource = true;
        Status.Text = "Video bilgileri okunuyor…";
        try
        {
            string result = await RunTool("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], default);
            if (closing)
                return;
            using var metadata = JsonDocument.Parse(result);
            var root = metadata.RootElement;
            var streams = root.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.FirstOrDefault(stream => stream.GetProperty("codec_type").GetString() == "video" &&
                (!stream.TryGetProperty("disposition", out var disposition) || ReadNumber(disposition, "attached_pic") == 0));
            if (video.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("Dosyada video akışı bulunamadı.");
            double duration = ReadNumber(video, "duration");
            if (duration <= 0)
                duration = ReadNumber(root.GetProperty("format"), "duration");
            if (duration <= 0)
                throw new InvalidOperationException("Videonun süresi okunamadı.");

            StopPlayback();
            resumeAfterPreview = false;
            JsonElement container = root.GetProperty("format");
            var audio = streams.FirstOrDefault(stream => stream.GetProperty("codec_type").GetString() == "audio");
            bool hasAudio = audio.ValueKind != JsonValueKind.Undefined;
            long bytes = new FileInfo(path).Length;
            double containerDuration = ReadNumber(container, "duration");
            double totalKbps = bytes * 8d / (containerDuration > 0 ? containerDuration : duration) / 1000;
            double audioKbps = hasAudio ? ReadNumber(audio, "bit_rate") / 1000 : 0;
            if (hasAudio && audioKbps <= 0)
                audioKbps = Math.Min(128, totalKbps * .2);
            double videoKbps = ReadNumber(video, "bit_rate") / 1000;
            if (videoKbps <= 0)
                videoKbps = Math.Max(1, totalKbps * .985 - audioKbps);
            var (width, height) = DisplayDimensions(video);
            source = new SourceVideo(path, duration, ReadFps(video), hasAudio, bytes, width, height,
                videoKbps / 1000, totalKbps / 1000, audioKbps);
            clips = [new VideoSegment(0, duration)];
            undo.Clear();
            UndoButton.IsEnabled = false;
            gainEditStart = null;
            SelectGainClip(-1);
            SetTool(EditTool.Pointer);
            StartWaveform(source);
            ClearPlayer();
            SetPosition(0);
            OpenOverlay.Visibility = Visibility.Collapsed;
            OriginalSize.Text = "Orijinal: " + FormatSize(source.Bytes);
            hasEstimate = false;
            CalculateButton.Content = "Hesapla";
            EstimatedSize.Text = "Mevcut: —";
            SizeChange.Text = "";
            EstimateStatus.Text = "";
            ExportButton.IsEnabled = CalculateButton.IsEnabled = EditButtons.IsEnabled = true;
            if (string.IsNullOrWhiteSpace(OutputFolder.Text))
                OutputFolder.Text = Path.GetDirectoryName(path);
            OutputName.Text = Path.GetFileNameWithoutExtension(path) + " - Düzenleme";
            string sourceExtension = Path.GetExtension(path).TrimStart('.');
            SourceInfo.Text = $"{width} × {height}   •   {sourceExtension.ToUpperInvariant()}" + Environment.NewLine +
                              $"{source.Fps:0.##} FPS   •   {FormatTime(duration)}   •   {FormatSize(source.Bytes)}" + Environment.NewLine +
                              $"Video bitrate: {source.VideoMbps:0.###} Mbps";
            InitializeSourceOptions(source, sourceExtension, container);
            RefreshDimensions();
            RefreshBitrate();
            QuickApply.IsEnabled = !audioOnly && !gif;
            Status.Text = "";
            SchedulePreview();
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
        }
        finally
        {
            loadingSource = false;
        }
    }

    private static (int Width, int Height) DisplayDimensions(JsonElement video)
    {
        double width = ReadNumber(video, "width");
        double height = ReadNumber(video, "height");
        if (video.TryGetProperty("sample_aspect_ratio", out var sar))
        {
            string[] parts = sar.ToString().Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int numerator) &&
                int.TryParse(parts[1], out int denominator) && numerator > 0 && denominator > 0)
                width *= (double)numerator / denominator;
        }
        if (video.TryGetProperty("side_data_list", out var sideData) &&
            sideData.EnumerateArray().Any(item => Math.Abs(ReadNumber(item, "rotation")) % 180 == 90))
            (width, height) = (height, width);
        return (Even(width), Even(height));
    }

    private void InitializeSourceOptions(SourceVideo input, string extension, JsonElement container)
    {
        initializingSource = true;
        try
        {
            requireSmallerOutput = false;
            audioBitrateKbps = null;
            videoBitrateMbps = input.VideoMbps;
            SetMode(false, extension.Equals("gif", StringComparison.OrdinalIgnoreCase));
            string format = extension.ToLowerInvariant() switch
            {
                "mpg" or "mpe" => "MPEG", "mts" => "M2TS", _ => extension
            };
            string? matchingFormat = FormatBox.Items.Cast<string>().FirstOrDefault(item =>
                item.Equals(format, StringComparison.OrdinalIgnoreCase));
            if (matchingFormat == null && container.TryGetProperty("format_name", out var name))
            {
                string[] aliases = name.ToString().Split(',');
                matchingFormat = FormatBox.Items.Cast<string>().FirstOrDefault(item =>
                    aliases.Contains(item, StringComparer.OrdinalIgnoreCase));
            }
            FormatBox.SelectedIndex = 0;
            if (matchingFormat != null)
                FormatBox.SelectedItem = matchingFormat;
            UpdateCodecs();
            FpsBox.SelectedIndex = 0;
            int shortSide = Math.Min(input.Width, input.Height);
            int resolution = Array.IndexOf(ResolutionSizes, shortSide);
            SetItems(ResolutionBox, resolution < 0 ? [.. Resolutions, $"Kaynak ({shortSide}p)"] : Resolutions,
                resolution < 0 ? Resolutions.Length : resolution);
            double ratio = (double)input.Width / input.Height;
            int ratioIndex = Array.FindIndex(new[] { 16d / 9, 4d / 3, 1d, 21d / 9, 9d / 16 },
                known => Math.Abs(known - ratio) < .00001);
            int divisor = GreatestCommonDivisor(input.Width, input.Height);
            SetItems(RatioBox, ratioIndex < 0 ? [.. Ratios, $"Kaynak ({input.Width / divisor}:{input.Height / divisor})"] : Ratios,
                ratioIndex < 0 ? Ratios.Length : ratioIndex);
        }
        finally
        {
            initializingSource = false;
        }
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    private void SetPosition(double time)
    {
        position = Math.Clamp(time, 0, Duration);
        syncingSeek = true;
        SeekBar.Maximum = Duration;
        SeekBar.Value = position;
        syncingSeek = false;
        ClockLabel.Text = $"{FormatTime(position)} / {FormatTime(Duration)}";
        UpdatePlayhead();
    }

    private static string FormatTime(double time)
    {
        var value = TimeSpan.FromSeconds(time);
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
    }

    private static string FormatSize(double bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1_000_000_000:0.##} GB" : $"{bytes / 1_000_000:0.##} MB";

    private void SetVideoMode(object sender, RoutedEventArgs e) => SetMode(false, false);
    private void SetAudioMode(object sender, RoutedEventArgs e) => SetMode(true, false);
    private void SetGifMode(object sender, RoutedEventArgs e) => SetMode(false, true);

    private void SetMode(bool useAudioOnly, bool useGif)
    {
        if (audioOnly == useAudioOnly && gif == useGif)
            return;
        audioOnly = useAudioOnly;
        gif = useGif;
        VideoOptions.Visibility = audioOnly ? Visibility.Collapsed : Visibility.Visible;
        VideoCodecLabel.Visibility = VideoCodecBox.Visibility = AudioOptions.Visibility = BitrateOptions.Visibility =
            gif ? Visibility.Collapsed : Visibility.Visible;
        VideoModeButton.Style = (Style)FindResource(!audioOnly && !gif ? "Accent" : "EditorButton");
        AudioModeButton.Style = (Style)FindResource(audioOnly ? "Accent" : "EditorButton");
        GifModeButton.Style = (Style)FindResource(gif ? "Accent" : "EditorButton");
        ConfigureFormats();
        if (initializingSource)
            return;
        QuickApply.IsEnabled = source != null && !audioOnly && !gif;
        RefreshDimensions();
        RefreshBitrate();
        SchedulePreview();
    }
    private void FormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || initializingSource)
            return;
        UpdateCodecs();
        RefreshDimensions();
        RefreshBitrate();
        SchedulePreview();
    }

    private void EstimateSettingChanged(object sender, RoutedEventArgs e)
    {
        if (initialized && !initializingSource)
        {
            RefreshBitrate();
            InvalidateEstimate();
        }
    }

    private void PreviewSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || applyingQuick || initializingSource)
            return;
        RefreshDimensions();
        RefreshBitrate();
        SchedulePreview();
    }

    private (int Width, int Height) Dimensions()
    {
        int shortSide = ResolutionBox.SelectedIndex >= ResolutionSizes.Length && source != null
            ? Math.Min(source.Width, source.Height) : ResolutionSizes[Math.Clamp(ResolutionBox.SelectedIndex, 0, ResolutionSizes.Length - 1)];
        if (RatioBox.SelectedIndex >= Ratios.Length && source != null)
        {
            double scale = shortSide / (double)Math.Min(source.Width, source.Height);
            return (Even(source.Width * scale), Even(source.Height * scale));
        }
        return RatioBox.SelectedIndex switch
        {
            1 => (Even(shortSide * 4.0 / 3), shortSide),
            2 => (shortSide, shortSide),
            3 => (Even(shortSide * 21.0 / 9), shortSide),
            4 => (shortSide, Even(shortSide * 16.0 / 9)),
            _ => (Even(shortSide * 16.0 / 9), shortSide)
        };
    }

    private static int Even(double value) => Math.Max(2, 2 * (int)Math.Round(value / 2, MidpointRounding.AwayFromZero));

    private void RefreshDimensions()
    {
        var options = Options();
        var (width, height) = (options.Width, options.Height);
        OutputDimensions.Text = $"Çıktı: {width} × {height}  ·  {options.Fps:0.##} FPS";
        bool fixedDimensions = FormatBox.SelectedItem is "DV";
        ResolutionBox.IsEnabled = RatioBox.IsEnabled = !fixedDimensions;
    }

    private OutputOptions Options()
    {
        var (width, height) = Dimensions();
        double fps = FpsBox.SelectedIndex > 0
            ? new[] { 0, 24, 25, 30, 50, 60 }[FpsBox.SelectedIndex] : source?.Fps ?? 30;
        string video = (VideoCodecBox.SelectedItem as string) switch
        {
            "H.265 (HEVC)" => "libx265", "VP9" => "libvpx-vp9", "MPEG-4" => "mpeg4",
            "MJPEG" => "mjpeg", "MPEG-2" => "mpeg2video", "WMV" => "wmv2",
            "Theora" => "libtheora", "DV" => "dvvideo", _ => "libx264"
        };
        string audio = (AudioCodecBox.SelectedItem as string) switch
        {
            "Opus" => "libopus", "PCM" => "pcm_s16le", "PCM (BE)" => "pcm_s16be",
            "MP3" => "libmp3lame", "FLAC" => "flac", "Vorbis" => "libvorbis",
            "MP2" => "mp2", "WMA" => "wmav2", "AC-3" => "ac3", "E-AC-3" => "eac3", "ALAC" => "alac",
            "AMR-NB" => "libopencore_amrnb", _ => "aac"
        };
        int videoRate = VideoRate(width, height, fps, video);
        var (audioMinimum, audioMaximum, audioDefault) = AudioRateRange(audio);
        int audioRate = (int)Math.Clamp(audioOnly ? audioBitrateKbps ?? audioDefault : audioDefault, audioMinimum, audioMaximum);
        if (requireSmallerOutput && !audioOnly && source?.HasAudio == true && audioMaximum > audioMinimum)
            audioRate = (int)Math.Clamp(Math.Min(audioRate, source.AudioKbps), audioMinimum, audioMaximum);
        if (audio == "mp2")
            audioRate = new[] { 64, 96, 128, 160, 192, 224, 256, 320, 384 }.MinBy(rate => Math.Abs(rate - audioRate));
        if (audio == "ac3")
            audioRate = new[] { 96, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640 }.MinBy(rate => Math.Abs(rate - audioRate));
        if (audio == "libmp3lame")
            audioRate = new[] { 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }.MinBy(rate => Math.Abs(rate - audioRate));
        string color = $"exposure=exposure={Number(Exposure.Value / 25)}," +
                       $"eq=brightness={Number(Brightness.Value / 125)}:contrast={Number(1 + Contrast.Value / 50)}:" +
                       $"saturation={Number(1 + Saturation.Value / 50)}";
        if (FormatBox.SelectedItem is "DV")
        {
            (width, height, fps) = fps >= 29 ? (720, 480, 30000d / 1001) : (720, 576, 25);
            videoRate = 25000;
        }
        if (gif)
            fps = Math.Min(fps, 50);
        return new OutputOptions(width, height, fps, video, audio, videoRate, audioRate, color, audioOnly, gif);
    }

    private (double Minimum, double Maximum) VideoRateRange(string codec)
    {
        double[] floors = [.5, .35, .25, .15, .12, .1, .075, .05];
        double[] ceilings = [100, 70, 45, 24, 12, 7, 4, 2];
        int index = RateResolutionIndex();
        double maximum = codec switch
        {
            "wmv2" or "mpeg4" or "libtheora" => Math.Min(20, ceilings[index]),
            "mpeg2video" => Math.Min(50, ceilings[index]),
            _ => ceilings[index]
        };
        double minimum = Math.Min(floors[index], (source?.VideoMbps ?? floors[index]) * (requireSmallerOutput ? .25 : 1));
        if (!requireSmallerOutput && source != null && codec is "libx264" or "libx265" or "libvpx-vp9")
            maximum = Math.Max(maximum, Math.Min(source.VideoMbps, 100));
        return (Math.Max(.001, minimum), maximum);
    }

    private int RateResolutionIndex()
    {
        if (ResolutionBox.SelectedIndex >= 0 && ResolutionBox.SelectedIndex < ResolutionSizes.Length)
            return ResolutionBox.SelectedIndex;
        if (source == null)
            return 2;
        int shortSide = Math.Min(source.Width, source.Height);
        int index = Array.FindIndex(ResolutionSizes, size => size <= shortSide);
        return index < 0 ? ResolutionSizes.Length - 1 : index;
    }

    private int VideoRate(int width, int height, double fps, string codec)
    {
        var (minimum, maximum) = VideoRateRange(codec);
        double autoRate = width * (double)height * fps * .11 * (codec == "libx264" ? 1 : .78) / 1_000_000;
        return (int)Math.Round(Math.Clamp(videoBitrateMbps ?? autoRate, minimum, maximum) * 1000);
    }

    private static (int Minimum, int Maximum, int Default) AudioRateRange(string codec) => codec switch
    {
        "libopencore_amrnb" => (12, 12, 12),
        "pcm_s16le" or "pcm_s16be" => (1536, 1536, 1536),
        "flac" or "alac" => (850, 850, 850),
        "wmav2" => (64, 192, 160),
        "mp2" => (64, 384, 160),
        "ac3" or "eac3" => (96, 640, 192),
        _ => (64, 320, 160)
    };

    private void RefreshBitrate()
    {
        var options = Options();
        bool fixedVideo = FormatBox.SelectedItem is "DV";
        var videoRange = fixedVideo ? (25d, 25d) : VideoRateRange(options.VideoCodec);
        var audioRange = AudioRateRange(options.AudioCodec);
        double minimum = audioOnly ? audioRange.Minimum : videoRange.Item1;
        double maximum = audioOnly ? audioRange.Maximum : videoRange.Item2;
        changingBitrate = true;
        BitrateSlider.Minimum = minimum;
        BitrateSlider.Maximum = maximum;
        BitrateSlider.TickFrequency = audioOnly ? 1 : .05;
        BitrateSlider.SmallChange = audioOnly ? 1 : .05;
        BitrateSlider.LargeChange = audioOnly ? 16 : .5;
        BitrateSlider.Value = audioOnly ? options.AudioRate : options.VideoRate / 1000d;
        BitrateSlider.IsEnabled = maximum > minimum;
        changingBitrate = false;
        BitrateLabel.Text = audioOnly ? "Ses bitrate" : "Video bitrate";
        string unit = audioOnly ? "kbps" : "Mbps";
        bool lossless = audioOnly && options.AudioCodec is "flac" or "alac";
        BitrateValue.Text = lossless ? "Değişken" : $"{BitrateSlider.Value:0.###} {unit}";
        BitrateRange.Text = lossless ? "Kayıpsız kodek" : maximum > minimum
            ? $"{minimum:0.##} – {maximum:0.##} {unit}" : "Kodeğin sabit değeri";
    }

    private void BitrateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized || changingBitrate || initializingSource)
            return;
        if (audioOnly)
            audioBitrateKbps = e.NewValue;
        else
            videoBitrateMbps = e.NewValue;

        var options = Options();
        BitrateValue.Text = audioOnly ? $"{options.AudioRate} kbps" : $"{options.VideoRate / 1000d:0.###} Mbps";
        InvalidateEstimate();
    }

    private void SchedulePreview()
    {
        InvalidateEstimate();
        DrawTimeline();
        if (source == null || closing)
            return;

        previewJob?.Cancel();
        frameJob?.Cancel();
        resumeAfterPreview |= playing;
        StopPlayback();
        CenterPlay.Visibility = Visibility.Collapsed;
        Updating.Visibility = Visibility.Visible;
        Transport.IsEnabled = SeekBar.IsEnabled = false;
        var job = new CancellationTokenSource();
        previewJob = job;
        _ = RenderPreview(source, clips, Options(), job);
    }

    private async Task RenderPreview(SourceVideo input, VideoSegment[] segments, OutputOptions options, CancellationTokenSource job)
    {
        string path = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await Task.Delay(260, job.Token);
            Directory.CreateDirectory(tempDirectory);
            double scale = Math.Min(1, 720.0 / Math.Max(options.Width, options.Height));
            var proxy = options with
            {
                Width = Even(options.Width * scale), Height = Even(options.Height * scale),
                VideoCodec = "libx264", AudioCodec = "aac", AudioOnly = false, Gif = false
            };
            await RunTool("ffmpeg", BuildRenderArguments(input, segments, proxy, path, true), job.Token);
            string count = await RunTool("ffprobe",
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=nb_frames", "-of", "csv=p=0", path], job.Token);
            job.Token.ThrowIfCancellationRequested();

            string? oldPath = openingPath;
            Player.Close();
            openingPath = path;
            previewPath = null;
            previewFps = proxy.Fps;
            previewFrames = long.TryParse(count.Trim(), out long frames)
                ? frames : Math.Max(1, (long)Math.Round(Duration * previewFps));
            previewJob = null;
            Player.Source = new Uri(path);
            Player.Volume = VolumeBar.Value;
            Player.Pause();
            if (oldPath != null)
                DeleteTempFile(oldPath);
        }
        catch (OperationCanceledException)
        {
            DeleteTempFile(path);
        }
        catch (Exception error)
        {
            DeleteTempFile(path);
            if (!job.IsCancellationRequested && !closing)
            {
                Updating.Visibility = Visibility.Collapsed;
                Status.Text = "Önizleme hazırlanamadı. " + error.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(previewJob, job))
                previewJob = null;
            job.Dispose();
        }
    }

    private static List<string> BuildRenderArguments(SourceVideo input, VideoSegment[] segments, OutputOptions options, string path, bool preview)
    {
        bool includeAudio = input.HasAudio && !options.Gif;
        bool includeVideo = !options.AudioOnly;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", input.Path, "-filter_complex_threads", "2" };
        var graph = new List<string>();
        if (segments.Length > 1)
        {
            if (includeVideo)
                graph.Add("[0:V:0]split=" + segments.Length + string.Concat(segments.Select((_, index) => $"[vin{index}]")));
            if (includeAudio)
                graph.Add("[0:a:0]asplit=" + segments.Length + string.Concat(segments.Select((_, index) => $"[ain{index}]")));
        }
        var concatInputs = new List<string>();
        for (int index = 0; index < segments.Length; index++)
        {
            VideoSegment segment = segments[index];
            string range = $"start={Number(segment.Start)}:end={Number(segment.End)}";
            if (includeVideo)
            {
                string inputLabel = segments.Length > 1 ? $"[vin{index}]" : "[0:V:0]";
                graph.Add($"{inputLabel}trim={range},setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={Number(1 / options.Fps)}," +
                          $"fps={Number(options.Fps)},trim=duration={Number(segment.Duration)}[v{index}]");
                concatInputs.Add($"[v{index}]");
            }
            if (includeAudio)
            {
                string inputLabel = segments.Length > 1 ? $"[ain{index}]" : "[0:a:0]";
                graph.Add($"{inputLabel}atrim={range},asetpts=PTS-STARTPTS,aresample=48000,volume={Number(segment.Gain)},apad," +
                          $"atrim=duration={Number(segment.Duration)}[a{index}]");
                concatInputs.Add($"[a{index}]");
            }
        }
        string videoLabel = "[v0]";
        string audioLabel = "[a0]";
        if (segments.Length > 1)
        {
            videoLabel = includeVideo ? "[vjoined]" : "";
            audioLabel = includeAudio ? "[ajoined]" : "";
            graph.Add(string.Concat(concatInputs) + $"concat=n={segments.Length}:v={(includeVideo ? 1 : 0)}:a={(includeAudio ? 1 : 0)}" + videoLabel + audioLabel);
        }
        if (includeVideo)
        {
            graph.Add(videoLabel + options.ColorFilter +
                      $",scale={options.Width}:{options.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1," +
                      $"pad={options.Width}:{options.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={Number(options.Fps)},format={(options.Gif ? "rgb24" : options.VideoCodec == "mjpeg" ? "yuvj420p" : options.VideoCodec == "dvvideo" && options.Fps > 29 ? "yuv411p" : "yuv420p")}[vout]");
        }
        if (options.Gif)
            graph.Add("[vout]split[gifsource][paletteinput];[paletteinput]palettegen[palette];[gifsource][palette]paletteuse[gif]");
        args.AddRange(["-filter_complex", string.Join(';', graph)]);
        if (options.Gif)
        {
            args.AddRange(["-map", "[gif]", "-loop", "0", path]);
            return args;
        }
        if (includeVideo)
        {
            args.AddRange(["-map", "[vout]", "-c:v", options.VideoCodec]);
            if (options.VideoCodec is "libx264" or "libx265")
                args.AddRange(["-preset", preview ? "ultrafast" : "fast"]);
            if (preview)
                args.AddRange(["-crf", "23", "-g", "15", "-bf", "0"]);
            else if (options.VideoCodec != "dvvideo")
                args.AddRange(["-b:v", options.VideoRate + "k"]);
            if (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && options.VideoCodec == "libx265")
                args.AddRange(["-tag:v", "hvc1"]);
        }
        if (includeAudio)
        {
            args.AddRange(["-map", audioLabel, "-c:a", options.AudioCodec, "-ac", options.AudioCodec == "libopencore_amrnb" ? "1" : "2"]);
            if (options.AudioCodec == "libopencore_amrnb") args.AddRange(["-ar", "8000", "-b:a", "12.2k"]);
            if (!options.AudioCodec.StartsWith("pcm_") && options.AudioCodec is not ("flac" or "alac" or "libopencore_amrnb"))
                args.AddRange(["-b:a", options.AudioRate + "k"]);
        }
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!preview)
        {
            string? muxer = extension switch
            {
                ".m4v" => "mp4", ".f4v" => "f4v", ".m2ts" => "mpegts", ".opus" => "opus", ".vob" => "vob",
                ".mxf" => "mxf", _ => null
            };
            if (muxer != null)
                args.AddRange(["-f", muxer]);
            if (extension == ".mxf") args.AddRange(["-ar", "48000"]);
            if (extension == ".m2ts")
                args.AddRange(["-mpegts_m2ts_mode", "1"]);
        }
        args.Add(path);
        return args;
    }

    private void PlayerOpened(object sender, RoutedEventArgs e)
    {
        if (openingPath == null)
            return;
        previewPath = openingPath;
        if (previewJob != null)
            return;
        Updating.Visibility = Visibility.Collapsed;
        Transport.IsEnabled = SeekBar.IsEnabled = true;
        SeekTo(position, !resumeAfterPreview);
        if (resumeAfterPreview && IsVisible)
            StartPlayback();
        resumeAfterPreview = false;
    }

    private void PlayerFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Updating.Visibility = Visibility.Collapsed;
        Status.Text = "Önizleme oynatılamadı. " + e.ErrorException.Message;
    }

    private void PlayerEnded(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SetPosition(LastFrameTime());
    }

    private double LastFrameTime() => Math.Max(0, (previewFrames - 1) / previewFps);

    private void TogglePlay(object sender, RoutedEventArgs e)
    {
        if (playing)
            StopPlayback();
        else if (previewPath != null && Transport.IsEnabled)
        {
            if (position >= LastFrameTime())
                SeekTo(0);
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        frameJob?.Cancel();
        FrameImage.Visibility = Visibility.Collapsed;
        CenterPlay.Visibility = Visibility.Collapsed;
        Player.Position = TimeSpan.FromSeconds(position);
        Player.Play();
        playing = true;
        PlayButton.Content = "Ⅱ";
        playbackTimer.Start();
    }

    private void StopPlayback()
    {
        if (playing)
        {
            Player.Pause();
            SetPosition(Player.Position.TotalSeconds);
        }
        playing = false;
        playbackTimer.Stop();
        PlayButton.Content = "▶";
        CenterPlay.Visibility = previewPath == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearPlayer()
    {
        Player.Close();
        if (openingPath != null)
            DeleteTempFile(openingPath);
        openingPath = previewPath = null;
        FrameImage.Source = null;
        FrameImage.Visibility = Visibility.Collapsed;
    }

    private void SeekTo(double time, bool exactFrame = false)
    {
        frameJob?.Cancel();
        SetPosition(previewPath == null ? time : Math.Clamp(time, 0, LastFrameTime()));
        if (previewPath == null)
            return;
        if (!exactFrame)
            FrameImage.Visibility = Visibility.Collapsed;
        Player.Position = TimeSpan.FromSeconds(position);
        if (exactFrame)
        {
            CenterPlay.Visibility = Visibility.Collapsed;
            var job = new CancellationTokenSource();
            frameJob = job;
            _ = ReadFrame(previewPath, position, job);
        }
        else if (!playing && Updating.Visibility != Visibility.Visible)
            CenterPlay.Visibility = Visibility.Visible;
    }

    private async Task ReadFrame(string video, double time, CancellationTokenSource job)
    {
        string imagePath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await RunTool("ffmpeg", ["-v", "error", "-nostdin", "-ss", Number(Math.Max(0, time - .25 / previewFps)), "-i", video, "-an",
                "-frames:v", "1", "-threads", "1", "-update", "1", imagePath], job.Token);
            job.Token.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(imagePath);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            FrameImage.Source = image;
            FrameImage.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!job.IsCancellationRequested)
                Status.Text = "Kare okunamadı. " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(frameJob, job))
                frameJob = null;
            job.Dispose();
            DeleteTempFile(imagePath);
        }
    }

    private void GoStart(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekTo(0, true);
    }

    private void GoEnd(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekTo(LastFrameTime(), true);
    }

    private void StepFrame(int direction)
    {
        if (previewPath == null || !Transport.IsEnabled)
            return;
        StopPlayback();
        double frame = position * previewFps;
        long current = (long)Math.Floor(frame + .0001);
        long next = Math.Clamp(current + direction, 0, Math.Max(0, previewFrames - 1));
        if (next == current && FrameImage.Visibility == Visibility.Visible)
            return;
        SeekTo(next / previewFps, true);
    }

    private void FrameClick(object sender, RoutedEventArgs e) => StepFrame(int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));

    private void FramePressed(object sender, MouseButtonEventArgs e)
    {
        var button = (Button)sender;
        repeatDirection = int.Parse((string)button.Tag, CultureInfo.InvariantCulture);
        button.Focus();
        button.CaptureMouse();
        StepFrame(repeatDirection);
        repeatTimer.Interval = TimeSpan.FromMilliseconds(320);
        repeatTimer.Start();
        e.Handled = true;
    }

    private void FrameReleased(object sender, MouseButtonEventArgs e)
    {
        repeatTimer.Stop();
        ((Button)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void FrameCaptureLost(object sender, MouseEventArgs e) => repeatTimer.Stop();

    private void SeekStarted(object sender, MouseButtonEventArgs e) => seeking = true;
    private void SeekFinished(object sender, MouseButtonEventArgs e) => seeking = false;

    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (initialized && !syncingSeek && previewPath != null)
            SeekTo(e.NewValue);
    }

    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized)
            return;
        Player.Volume = e.NewValue;
        if (e.NewValue > 0)
            previousVolume = e.NewValue;
        MuteButton.Content = e.NewValue > 0 ? "♪" : "×";
    }

    private void ToggleMute(object sender, RoutedEventArgs e) => VolumeBar.Value = VolumeBar.Value == 0 ? previousVolume : 0;

    private void ToggleFullscreen(object sender, RoutedEventArgs e)
    {
        if (fullscreen != null)
        {
            fullscreen.Close();
            return;
        }

        bool resume = playing;
        StopPlayback();
        PreviewHost.Child = null;
        fullscreen = new Window
        {
            Title = "AVM Önizleme",
            Owner = Window.GetWindow(this),
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(5, 8, 18)),
            Content = PlayerSurface
        };
        fullscreen.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                fullscreen?.Close();
            }
        };
        fullscreen.Closed += (_, _) =>
        {
            var window = fullscreen!;
            bool wasPlaying = playing;
            StopPlayback();
            window.Content = null;
            fullscreen = null;
            PreviewHost.Child = PlayerSurface;
            FullscreenButton.ToolTip = "Tam ekran";
            if (wasPlaying && !closing && IsVisible)
                StartPlayback();
        };
        FullscreenButton.ToolTip = "Tam ekrandan çık (Esc)";
        fullscreen.Show();
        if (resume)
            StartPlayback();
    }

    private void PageVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!initialized || IsVisible)
            return;
        resumeAfterPreview = false;
        repeatTimer.Stop();
        StopPlayback();
    }

    private void OwnerClosed(object? sender, EventArgs e)
    {
        closing = true;
        quickSound?.Dispose();
        waveformJob?.Cancel();
        scissorsCursor?.Dispose();
        previewJob?.Cancel();
        frameJob?.Cancel();
        repeatTimer.Stop();
        StopPlayback();
        ClearPlayer();
        try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteTempFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async void ShowQuickSettings(object sender, RoutedEventArgs e)
    {
        if (openingQuick)
            return;
        openingQuick = true;
        try
        {
            PlayQuickSound();
            var scale = new ScaleTransform(1, 1);
            QuickCover.RenderTransform = scale;
            var pulse = new DoubleAnimationUsingKeyFrames();
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(.93, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.035, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(285))));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            QuickCover.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .25, TimeSpan.FromMilliseconds(285))
            {
                BeginTime = TimeSpan.FromMilliseconds(80), FillBehavior = FillBehavior.Stop
            });
            await Task.Delay(300);
            if (closing)
                return;

            QuickCover.Visibility = Visibility.Collapsed;
            QuickSettings.Visibility = Visibility.Visible;
            QuickSettings.RenderTransform = new TranslateTransform(0, 12);
            QuickSettings.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            QuickSettings.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
        }
        finally
        {
            openingQuick = false;
        }
    }

    private void PlayQuickSound()
    {
        try
        {
            quickSound ??= new System.Media.SoundPlayer(CreateQuickSound());
            quickSound.Play();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TimeoutException) { }
    }

    private static MemoryStream CreateQuickSound()
    {
        const int rate = 24000;
        const int samples = 6200;
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples * 2);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            for (int sample = 0; sample < samples; sample++)
            {
                double t = sample / (double)rate;
                double overtone = Math.Max(0, t - .052);
                double note = Math.Sin(2 * Math.PI * 659.25 * t) * Math.Exp(-19 * t);
                double shimmer = Math.Sin(2 * Math.PI * 987.77 * overtone) * Math.Exp(-15 * overtone);
                double fade = Math.Min(1, t / .012) * Math.Min(1, (samples - sample) / 600d);
                writer.Write((short)(4800 * fade * (note + .55 * shimmer)));
            }
        }
        stream.Position = 0;
        return stream;
    }

    private async void ApplyQuickSettings(object sender, RoutedEventArgs e)
    {
        if (source == null || audioOnly || gif || applyingQuick || loadingSource || exporting)
            return;

        QuickBusy.Visibility = Visibility.Visible;
        QuickSpinner.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(700))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
        applyingQuick = true;
        AnimateQuickOverlay(true);
        try
        {
            await Task.Delay(1050);
            if (closing)
                return;
            int type = QuickType.SelectedIndex;
            int resolution = RateResolutionIndex();
            var options = Options();
            var colors = QuickColors[type];
            Brightness.Value = Exposure.Value = 0;
            Contrast.Value = colors.Contrast;
            Saturation.Value = colors.Saturation;
            int size = QuickSize.SelectedIndex;
            requireSmallerOutput = size == 0;
            if (FormatBox.SelectedItem is not "DV")
            {
                double table = QuickRate(type, resolution, options.Fps);
                double sourceRate = source.VideoMbps;
                double shrinkFactor = sourceRate < table * .5 ? .88 : .8;
                double rate = size switch
                {
                    0 => Math.Min(sourceRate * shrinkFactor, table * .8),
                    1 => Math.Min(sourceRate * .9, table),
                    _ => sourceRate
                };
                if (requireSmallerOutput)
                {
                    double audioRate = source.HasAudio ? Options().AudioRate / 1000d : 0;
                    rate = Math.Min(rate, source.TotalMbps * shrinkFactor * .985 - audioRate);
                }
                videoBitrateMbps = Math.Max(.001, rate);
            }
            RefreshBitrate();
            SchedulePreview();
            UpdateSizeEstimate();
            AnimateQuickOverlay(false);
            await Task.Delay(320);
        }
        finally
        {
            applyingQuick = false;
            QuickSpinner.BeginAnimation(RotateTransform.AngleProperty, null);
            QuickBusy.Visibility = Visibility.Collapsed;
        }
    }

    private void AnimateQuickOverlay(bool entering)
    {
        var duration = TimeSpan.FromMilliseconds(entering ? 450 : 300);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        QuickBusy.BeginAnimation(OpacityProperty,
            new DoubleAnimation(entering ? 0 : 1, entering ? 1 : 0, duration) { EasingFunction = ease });
        QuickCardShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(entering ? 18 : 0, entering ? 0 : -8, duration) { EasingFunction = ease });
    }

    private static double QuickRate(int type, int resolution, double fps)
    {
        bool high = fps >= 48;
        if (resolution >= 4)
            return (high ? new[] { 4, 1.5, 1, .5 } : new[] { 2.5, 1, .7, .3 })[resolution - 4];

        int rateRow = type == 5 ? 0 : type;
        double rate = (high ? QuickHighRates : QuickStandardRates)[rateRow, resolution];
        return rate;
    }
    private void HideQuickSettings(object sender, RoutedEventArgs e)
    {
        QuickSettings.Visibility = Visibility.Collapsed;
        QuickCover.Visibility = Visibility.Visible;
    }

    private static readonly Brush VideoTrackBrush = new SolidColorBrush(Color.FromRgb(118, 64, 214));
    private static readonly Brush AudioTrackBrush = new SolidColorBrush(Color.FromRgb(22, 103, 195));

    private double ZoomFactor => Math.Pow(120, (Zoom.Value - 1) / 119);
    private void ZoomIn(object sender, RoutedEventArgs e) => Zoom.Value += 8;
    private void ZoomOut(object sender, RoutedEventArgs e) => Zoom.Value -= 8;

    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized)
            return;
        double center = Duration > 0 && position / Duration * Timeline.Width >= TimelineScroll.HorizontalOffset &&
            position / Duration * Timeline.Width <= TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth
            ? position / Duration
            : (TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth / 2) / Math.Max(1, Timeline.Width);
        double relative = center * Timeline.Width - TimelineScroll.HorizontalOffset;
        Timeline.Width = Math.Max(1, TimelineScroll.ViewportWidth) * ZoomFactor;
        TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, center * Timeline.Width - relative));
        DrawTimeline();
    }

    private void TimelineSizeChanged(object sender, SizeChangedEventArgs e) => DrawTimeline();
    private void TimelineScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange != 0 || e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0)
            DrawTimeline();
    }

    private void DrawTimeline()
    {
        if (!initialized)
            return;
        double viewport = Math.Max(1, TimelineScroll.ViewportWidth);
        if (!double.IsFinite(viewport))
            return;
        double width = viewport * ZoomFactor;
        Timeline.Width = width;
        Timeline.Children.Clear();
        cutGuide = null;
        double duration = Duration;
        if (duration <= 0)
        {
            playhead = null;
            DrawOverview();
            return;
        }

        double pixelsPerSecond = width / duration;
        double fps = Options().Fps;
        double[] intervals = [1 / fps, 5 / fps, .25, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
        double step = intervals.Order().FirstOrDefault(value => value * pixelsPerSecond >= 69);
        if (step <= 0)
            step = Math.Ceiling(69 / pixelsPerSecond / 3600) * 3600;
        double offset = TimelineScroll.HorizontalOffset;
        double right = Math.Min(width, offset + viewport + 100);
        int divisions = step <= 1 / fps + .000001 ? 1 : 4;
        double tickStep = step / divisions;
        double first = Math.Max(0, Math.Floor((offset - 100) / (tickStep * pixelsPerSecond)) * tickStep);
        for (long i = (long)Math.Round(first / tickStep); i * tickStep * pixelsPerSecond <= right; i++)
        {
            double seconds = i * tickStep;
            double x = seconds * pixelsPerSecond;
            bool major = i % divisions == 0;
            AddBar(Timeline, x, major ? 22 : 27, 1, major ? 10 : 5, Brushes.MediumPurple);
            if (major)
            {
                var label = new TextBlock
                {
                    Text = step < 1 ? TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss\.fff" : @"mm\:ss\.fff") : FormatTime(seconds),
                    FontSize = 11, Foreground = Brushes.White, IsHitTestVisible = false
                };
                Canvas.SetLeft(label, x + 3);
                Canvas.SetTop(label, 2);
                Timeline.Children.Add(label);
            }
        }

        double left = 0;
        foreach (VideoSegment clip in clips)
        {
            double clipWidth = clip.Duration * pixelsPerSecond;
            if (left + clipWidth >= offset && left <= right)
            {
                AddBar(Timeline, left, 37, Math.Max(1, clipWidth - 2), 27, VideoTrackBrush, 4);
                if (source?.HasAudio == true)
                {
                    var audioBar = AddBar(Timeline, left, 69, Math.Max(1, clipWidth - 2), 27, AudioTrackBrush, 4);
                    if (ReferenceEquals(clip, SelectedGainClip))
                    {
                        audioBar.Stroke = Brushes.LightSkyBlue;
                        audioBar.StrokeThickness = 2;
                    }
                    DrawWaveform(clip, left, clipWidth, offset, right);
                }
            }
            left += clipWidth;
        }

        playhead = AddBar(Timeline, 0, 20, 2, 80, Brushes.Gold);
        cutGuide = AddBar(Timeline, 0, 35, 2, 63, Brushes.HotPink);
        UpdateCutGuide(Mouse.GetPosition(Timeline));
        DrawOverview();
        UpdatePlayhead();
    }

    private static Rectangle AddBar(Canvas canvas, double x, double y, double width, double height, Brush fill, double radius = 0)
    {
        var bar = new Rectangle
        {
            Width = Math.Max(0, width), Height = height, Fill = fill,
            RadiusX = radius, RadiusY = radius, IsHitTestVisible = false
        };
        Canvas.SetLeft(bar, x);
        Canvas.SetTop(bar, y);
        canvas.Children.Add(bar);
        return bar;
    }

    private void DrawWaveform(VideoSegment clip, double left, double width, double viewportLeft, double viewportRight)
    {
        if (peaks.Length == 0 || source == null || width <= 0)
            return;
        var waveform = new StreamGeometry();
        using (var geometry = waveform.Open())
        {
            for (double x = Math.Max(left + 2, viewportLeft); x < Math.Min(left + width - 2, viewportRight); x += 3)
            {
                double time = clip.Start + (x - left) / width * clip.Duration;
                int index = Math.Clamp((int)(time / source.Duration * peaks.Length), 0, peaks.Length - 1);
                double height = Math.Clamp(peaks[index] * clip.Gain * 16, 1, 12);
                geometry.BeginFigure(new Point(x, 82.5 - height), false, false);
                geometry.LineTo(new Point(x, 82.5 + height), true, false);
            }
        }
        waveform.Freeze();
        Timeline.Children.Add(new System.Windows.Shapes.Path
        {
            Data = waveform, Stroke = Brushes.LightSkyBlue, StrokeThickness = 1, IsHitTestVisible = false
        });
    }

    private void StartWaveform(SourceVideo input)
    {
        waveformJob?.Cancel();
        peaks = [];
        if (!input.HasAudio)
            return;
        var job = new CancellationTokenSource();
        waveformJob = job;
        _ = LoadWaveform(input, job);
    }

    private async Task LoadWaveform(SourceVideo input, CancellationTokenSource job)
    {
        string path = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".pcm");
        try
        {
            Directory.CreateDirectory(tempDirectory);
            await RunTool("ffmpeg", ["-v", "error", "-nostdin", "-i", input.Path, "-map", "0:a:0",
                "-ac", "1", "-ar", "1000", "-f", "s16le", path], job.Token);
            byte[] samples = await File.ReadAllBytesAsync(path, job.Token);
            int sampleCount = samples.Length / 2;
            var result = await Task.Run(() =>
            {
                int count = Math.Clamp(sampleCount / 20, 1, 150_000);
                var values = new float[count];
                for (int i = 0; i < sampleCount; i++)
                {
                    int bin = (int)((long)i * count / sampleCount);
                    values[bin] = Math.Max(values[bin], Math.Abs((int)BitConverter.ToInt16(samples, i * 2)) / 32768f);
                }
                return values;
            }, job.Token);
            job.Token.ThrowIfCancellationRequested();
            peaks = result;
            DrawTimeline();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Debug.WriteLine(error.Message); }
        finally
        {
            if (ReferenceEquals(waveformJob, job))
                waveformJob = null;
            job.Dispose();
            DeleteTempFile(path);
        }
    }

    private void OverviewSizeChanged(object sender, SizeChangedEventArgs e) => DrawOverview();

    private void DrawOverview()
    {
        if (!initialized)
            return;
        Overview.Children.Clear();
        overviewMarker = null;
        if (Duration <= 0 || Overview.ActualWidth <= 0)
            return;

        double scale = Overview.ActualWidth / Math.Max(1, Timeline.Width);
        double x = TimelineScroll.HorizontalOffset * scale;
        double view = Math.Min(Overview.ActualWidth, TimelineScroll.ViewportWidth * scale);
        AddBar(Overview, 0, 12, Overview.ActualWidth, 3, Brushes.Indigo, 2);
        AddBar(Overview, x, 5, Math.Max(2, view), 17, Brushes.MediumPurple, 4);
        overviewMarker = AddBar(Overview, 0, 2, 2, 23, Brushes.Gold);
        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        double fraction = Duration > 0 ? position / Duration : 0;
        if (playhead != null)
            Canvas.SetLeft(playhead, Math.Max(0, Math.Min(Timeline.Width - 2, fraction * Timeline.Width)));
        if (overviewMarker != null)
            Canvas.SetLeft(overviewMarker, Math.Max(0, Math.Min(Overview.ActualWidth - 2, fraction * Overview.ActualWidth)));
        TimelinePosition.Text = Duration > 0 ? TimeSpan.FromSeconds(position).ToString(position >= 3600 ? @"h\:mm\:ss\.ff" : @"mm\:ss\.ff") : "";
    }

    private void OverviewPressed(object sender, MouseButtonEventArgs e)
    {
        if (Duration <= 0 || Overview.ActualWidth <= 0)
            return;
        double scale = Overview.ActualWidth / Timeline.Width;
        double x = e.GetPosition(Overview).X;
        double left = TimelineScroll.HorizontalOffset * scale;
        double width = TimelineScroll.ViewportWidth * scale;
        overviewDragOffset = x >= left && x <= left + width ? x - left : width / 2;
        Overview.CaptureMouse();
        MoveOverview(x);
    }

    private void MoveOverview(double x)
    {
        TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, (x - overviewDragOffset) / Overview.ActualWidth * Timeline.Width));
    }

    private void OverviewMoved(object sender, MouseEventArgs e)
    {
        if (Overview.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            MoveOverview(e.GetPosition(Overview).X);
    }

    private void OverviewReleased(object sender, MouseButtonEventArgs e) => Overview.ReleaseMouseCapture();

    private double TimelineTime(MouseEventArgs e) => Math.Clamp(e.GetPosition(Timeline).X / Timeline.Width, 0, 1) * Duration;

    private void SelectCut(object sender, RoutedEventArgs e) => SetTool(selectedTool == EditTool.Cut ? EditTool.Pointer : EditTool.Cut);
    private void SelectDelete(object sender, RoutedEventArgs e) => SetTool(selectedTool == EditTool.Delete ? EditTool.Pointer : EditTool.Delete);

    private void SetTool(EditTool tool)
    {
        selectedTool = tool;
        UpdateCutGuide(Mouse.GetPosition(Timeline));
        CutTool.Style = (Style)FindResource(tool == EditTool.Cut ? "Accent" : "EditorButton");
        DeleteTool.Style = (Style)FindResource(tool == EditTool.Delete ? "Accent" : "EditorButton");
        Timeline.Cursor = tool switch
        {
            EditTool.Cut => GetScissorsCursor(),
            EditTool.Delete => Cursors.No,
            _ => Cursors.Arrow
        };
    }

    private Cursor GetScissorsCursor()
    {
        if (scissorsCursor != null)
            return scissorsCursor;
        try { return scissorsCursor = CreateScissorsCursor(); }
        catch (ArgumentException) { return Cursors.Cross; }
        catch (System.ComponentModel.Win32Exception) { return Cursors.Cross; }
    }

    private (int Index, double Start) ClipAt(double time)
    {
        int index = 0;
        double start = 0;
        while (index < clips.Length - 1 && time >= start + clips[index].Duration)
        {
            start += clips[index].Duration;
            index++;
        }
        return (index, start);
    }

    private bool TryGetCut(Point point, out int index, out double split, out double boundary)
    {
        index = 0;
        split = boundary = 0;
        if (source == null || clips.Length == 0 || point.X < 0 || point.X > Timeline.Width ||
            !(point.Y is >= 37 and <= 64 || source.HasAudio && point.Y is >= 69 and <= 96))
            return false;

        double time = point.X / Timeline.Width * Duration;
        var hit = ClipAt(time);
        index = hit.Index;
        double fps = previewPath != null ? previewFps : Options().Fps;
        split = Math.Round(time * fps) / fps;
        double local = split - hit.Start;
        VideoSegment clip = clips[index];
        boundary = clip.Start + local;
        double minimum = Math.Max(1 / fps, 1 / source.Fps) - .00001;
        return local >= minimum && clip.Duration - local >= minimum;
    }

    private void UpdateCutGuide(Point point)
    {
        if (cutGuide == null)
            return;
        cutGuide.Visibility = Visibility.Collapsed;
        if (selectedTool != EditTool.Cut || !Timeline.IsMouseOver || !TryGetCut(point, out _, out double split, out _))
            return;
        Canvas.SetLeft(cutGuide, split / Duration * Timeline.Width - 1);
        cutGuide.Height = source?.HasAudio == true ? 63 : 31;
        cutGuide.Visibility = Visibility.Visible;
    }

    private void TimelineMouseLeave(object sender, MouseEventArgs e)
    {
        if (cutGuide != null)
            cutGuide.Visibility = Visibility.Collapsed;
    }

    private void TimelinePressed(object sender, MouseButtonEventArgs e)
    {
        if (source == null || clips.Length == 0)
            return;
        FinishClipGainEdit();
        StopPlayback();
        Point point = e.GetPosition(Timeline);
        double time = TimelineTime(e);
        var (index, start) = ClipAt(time);
        bool onAudio = source.HasAudio && point.Y is >= 69 and <= 96;
        if (selectedTool != EditTool.Pointer && (point.Y is >= 37 and <= 64 || onAudio))
        {
            if (selectedTool == EditTool.Cut)
            {
                if (TryGetCut(point, out index, out double split, out double boundary))
                {
                    VideoSegment clip = clips[index];
                    ApplyEdit([.. clips.Take(index), clip with { End = boundary }, clip with { Start = boundary }, .. clips.Skip(index + 1)], split);
                }
            }
            else if (clips.Length > 1)
                ApplyEdit([.. clips.Take(index), .. clips.Skip(index + 1)], start);
            else
                Status.Text = "Silmek için önce klibi kesin.";
            return;
        }

        SeekTo(time);
        if (selectedTool == EditTool.Pointer && onAudio)
        {
            SelectGainClip(index);
            DrawTimeline();
            return;
        }
        Timeline.CaptureMouse();
    }

    private void TimelineMoved(object sender, MouseEventArgs e)
    {
        UpdateCutGuide(e.GetPosition(Timeline));
        if (Timeline.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            SeekTo(TimelineTime(e));
    }

    private void TimelineReleased(object sender, MouseButtonEventArgs e) => Timeline.ReleaseMouseCapture();

    private VideoSegment? SelectedGainClip => gainClipIndex >= 0 && gainClipIndex < clips.Length ? clips[gainClipIndex] : null;

    private void SelectGainClip(int index)
    {
        gainClipIndex = index;
        var clip = SelectedGainClip;
        syncingClipGain = true;
        ClipGain.IsEnabled = clip != null;
        ClipGain.Value = clip?.Gain ?? 1;
        syncingClipGain = false;
        ClipGainLabel.Text = clip == null ? "Zaman çizelgesinden bir ses klibi seçin." : $"Klip {index + 1}   ·   %{clip.Gain * 100:0}";
    }

    private void ClipGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized || syncingClipGain || SelectedGainClip is not VideoSegment clip)
            return;
        gainEditStart ??= new EditState(clips, position);
        var updated = (VideoSegment[])clips.Clone();
        updated[gainClipIndex] = clip with { Gain = e.NewValue };
        clips = updated;
        ClipGainLabel.Text = $"Klip {gainClipIndex + 1}   ·   %{e.NewValue * 100:0}";
        UndoButton.IsEnabled = true;
        SchedulePreview();
    }

    private void ClipGainDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => FinishClipGainEdit();
    private void ClipGainLostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishClipGainEdit();

    private void FinishClipGainEdit()
    {
        if (gainEditStart == null)
            return;
        if (!gainEditStart.Clips.SequenceEqual(clips))
            undo.Push(gainEditStart);
        gainEditStart = null;
        UndoButton.IsEnabled = undo.Count > 0;
    }
    private void ApplyEdit(VideoSegment[] updated, double newPosition)
    {
        undo.Push(new EditState(clips, position));
        UndoButton.IsEnabled = true;
        RestoreEdit(new EditState(updated, newPosition));
    }

    private void RestoreEdit(EditState state)
    {
        resumeAfterPreview |= playing;
        StopPlayback();
        var selected = SelectedGainClip;
        int selectedIndex = Array.FindIndex(state.Clips, clip => ReferenceEquals(clip, selected));
        if (selectedIndex < 0 && selected != null && clips.Length == state.Clips.Length &&
            state.Clips[gainClipIndex].Start == selected.Start && state.Clips[gainClipIndex].End == selected.End)
            selectedIndex = gainClipIndex;
        clips = state.Clips;
        SelectGainClip(selectedIndex);
        SetPosition(state.Position);
        SchedulePreview();
    }

    private void UndoEdit(object sender, RoutedEventArgs e)
    {
        FinishClipGainEdit();
        if (!undo.TryPop(out var state))
            return;
        UndoButton.IsEnabled = undo.Count > 0;
        RestoreEdit(state);
    }
    private void InvalidateEstimate()
    {
        if (hasEstimate)
            EstimateStatus.Text = "Güncel değil";
    }

    private void CalculateSize(object sender, RoutedEventArgs e) => UpdateSizeEstimate();

    private void UpdateSizeEstimate()
    {
        if (source == null)
            return;
        var options = Options();
        if (options.AudioOnly && !source.HasAudio)
        {
            EstimateStatus.Text = "Ses akışı yok";
            return;
        }

        double totalRate = (options.AudioOnly ? 0 : options.VideoRate) + (source.HasAudio ? options.AudioRate : 0);
        double bytes = options.Gif ? Duration * options.Width * options.Height * options.Fps * .25 : Duration * totalRate * 1000 / 8 * 1.015;
        double percent = source.Bytes > 0 ? (bytes / source.Bytes - 1) * 100 : 0;
        EstimatedSize.Text = "Mevcut: ≈ " + FormatSize(bytes);
        SizeChange.Text = Math.Abs(percent) < .05 ? "%0 değişim"
            : $"%{Math.Abs(percent):0.#} {(percent < 0 ? "küçülme" : "büyüme")}";
        hasEstimate = true;
        EstimateStatus.Text = "";
        CalculateButton.Content = "Yeniden Hesapla";
    }

    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(OutputFolder.Text))
            dialog.InitialDirectory = OutputFolder.Text;
        if (dialog.ShowDialog() == true)
            OutputFolder.Text = dialog.FolderName;
    }

    private async void ExportVideo(object sender, RoutedEventArgs e)
    {
        if (source == null || exporting || loadingSource || applyingQuick)
            return;
        var options = Options();
        if (options.AudioOnly && !source.HasAudio)
        {
            Status.Text = "Kaynak videoda ses akışı yok.";
            return;
        }

        string? stagingPath = null;
        try
        {
            string folder = OutputFolder.Text.Trim();
            string name = OutputName.Text.Trim();
            if (!Directory.Exists(folder))
                throw new InvalidOperationException("Geçerli bir çıktı klasörü seçin.");
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
                throw new InvalidOperationException("Geçerli bir çıktı adı yazın.");
            string extension = "." + ((string)FormatBox.SelectedItem).ToLowerInvariant();
            string path = Path.GetFullPath(Path.Combine(folder, name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension));
            if (path.Equals(Path.GetFullPath(source.Path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Çıktıya kaynak videodan farklı bir ad verin.");
            bool replace = File.Exists(path);
            if (replace && MessageBox.Show("Bu dosyanın üzerine yazılsın mı?", "Dışa Aktar", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            stagingPath = Path.Combine(folder, ".avm-" + Guid.NewGuid().ToString("N") + extension);
            exporting = true;
            ExportButton.IsEnabled = false;
            Status.Text = "Dışa aktarılıyor…";
            SourceVideo input = source;
            VideoSegment[] segments = clips;
            bool shrink = requireSmallerOutput && !options.AudioOnly && !options.Gif;
            int minimumRate = (int)Math.Ceiling(VideoRateRange(options.VideoCodec).Minimum * 1000);
            for (int attempt = 0; ; attempt++)
            {
                await RunTool("ffmpeg", BuildRenderArguments(input, segments, options, stagingPath, false), default);
                if (!shrink || new FileInfo(stagingPath).Length < input.Bytes)
                    break;
                if (attempt >= 3 || options.VideoCodec == "dvvideo" || options.VideoRate <= minimumRate)
                    throw new InvalidOperationException("Seçili çıktı ayarlarıyla kaynak dosyadan daha küçük bir dosya üretilemedi.");

                double ratio = input.Bytes / (double)new FileInfo(stagingPath).Length;
                int nextRate = Math.Max(minimumRate, (int)(options.VideoRate * Math.Min(.82, ratio * .94)));
                options = options with { VideoRate = nextRate };
                Status.Text = "Daha küçük bir çıktı için bitrate yeniden ayarlanıyor…";
            }
            File.Move(stagingPath, path, replace);
            Status.Text = "Dışa aktarıldı. " + Path.GetFileName(path);
        }
        catch (Exception error)
        {
            Status.Text = "Dışa aktarılamadı. " + error.Message;
        }
        finally
        {
            if (stagingPath != null)
                DeleteTempFile(stagingPath);
            exporting = false;
            ExportButton.IsEnabled = source != null;
        }
    }
}














