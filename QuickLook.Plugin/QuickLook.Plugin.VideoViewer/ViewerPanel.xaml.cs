// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

extern alias MediaInfoWrapper;

using MediaInfoWrapper::MediaInfo;
using QuickLook.Common.Annotations;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.VideoViewer.AudioTrack;
using QuickLook.Plugin.VideoViewer.LyricTrack;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UtfUnknown;
using WPFMediaKit.DirectShow.Controls;
using WPFMediaKit.DirectShow.MediaPlayers;

namespace QuickLook.Plugin.VideoViewer;

public partial class ViewerPanel : UserControl, IDisposable, INotifyPropertyChanged
{
    private readonly ContextObject _context;
    private BitmapSource _coverArt;
    private DispatcherTimer _lyricTimer;
    private LrcLine[] _lyricLines;
    private MidiPlayer _midiPlayer;
    private Point _mouseDownPosition;
    private bool _isMouseDown;
    private System.Threading.CancellationTokenSource _volumeSliderCancelTokenSource;
    private double _fps = 25.0;

    private bool _hasVideo;
    private bool _isPlaying;
    private bool _wasPlaying;
    private bool _shouldLoop;
    private bool _useHardwareAcceleration;

    private string _currentPath;
    private System.Collections.Generic.List<string> _playlist = new System.Collections.Generic.List<string>();
    private int _playlistIndex = -1;
    private MediaInfoLib _mediaInfo;

    public ViewerPanel(ContextObject context)
    {
        InitializeComponent();
        LoadAndInsertGlassLayer();

        // apply global theme
        Resources.MergedDictionaries[0].MergedDictionaries.Clear();

        _context = context;

        mediaElement.MediaUriPlayer.LAVFilterDirectory =
            IntPtr.Size == 8 ? @"LAVFilters-x64\" : @"LAVFilters-x86\";

        //ShowViedoControlContainer(null, null);
        viewerPanel.PreviewMouseMove += ShowViedoControlContainer;

        mediaElement.MediaUriPlayer.PlayerStateChanged += PlayerStateChanged;
        mediaElement.MediaOpened += MediaOpened;
        mediaElement.MediaEnded += MediaEnded;
        mediaElement.MediaFailed += MediaFailed;

        ShouldLoop = SettingHelper.Get("ShouldLoop", false, "QuickLook.Plugin.VideoViewer");
        UseHardwareAcceleration = SettingHelper.Get("UseHardwareAcceleration", false, "QuickLook.Plugin.VideoViewer");

        // Apply persisted HW/SW mode to the underlying player if supported.
        HardwareAccelerationModeChanged(UseHardwareAcceleration);

        string translationFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Translations.config");
        buttonPlayPause.ToolTip = TranslationHelper.Get("BTN_PlayPause", translationFile, failsafe: "Play/Pause");
        buttonLoop.ToolTip = TranslationHelper.Get("BTN_Loop", translationFile, failsafe: "Loop");
        buttonHardwareAcceleration.ToolTip = TranslationHelper.Get("BTN_HardwareAcceleration", translationFile, failsafe: "Hardware/Software Decoding");
        buttonMute.ToolTip = TranslationHelper.Get("BTN_Volume", translationFile, failsafe: "Volume");
        buttonTime.ToolTip = TranslationHelper.Get("BTN_Time", translationFile, failsafe: "Time Elapsed/Remaining");

        buttonPlayPause.Click += TogglePlayPause;
        buttonLoop.Click += ToggleShouldLoop;
        buttonHardwareAcceleration.Click += ToggleHardwareAcceleration;
        buttonTime.Click += (_, _) => buttonTime.Tag = (string)buttonTime.Tag == "Time" ? "Length" : "Time";
        buttonMute.Click += (_, _) => volumeSliderLayer.Visibility = Visibility.Visible;
        volumeSliderLayer.MouseDown += (_, _) => volumeSliderLayer.Visibility = Visibility.Collapsed;
        buttonCopyFrame.Click += (_, _) => CopyCurrentFrame();

        sliderProgress.PreviewMouseDown += (_, e) =>
        {
            _wasPlaying = IsPlaying;
            if (_wasPlaying)
            {
                mediaElement.Pause();
            }
        };
        sliderProgress.PreviewMouseUp += (_, _) =>
        {
            if (_wasPlaying)
            {
                _wasPlaying = false;
                mediaElement.Play();
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Focus();
                System.Windows.Input.Keyboard.Focus(this);
            }), DispatcherPriority.Input);
        };
        sliderProgress.LostMouseCapture += (_, _) =>
        {
            if (_wasPlaying)
            {
                _wasPlaying = false;
                mediaElement.Play();
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Focus();
                System.Windows.Input.Keyboard.Focus(this);
            }), DispatcherPriority.Input);
        };

        PreviewMouseWheel += (_, e) => ChangeVolume(e.Delta / 120d * 0.04d);

        PreviewKeyDown += ViewerPanel_PreviewKeyDown;

        Focusable = true;
        Loaded += (s, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Focus();
                System.Windows.Input.Keyboard.Focus(this);
            }), DispatcherPriority.Input);
        };
        InputMethod.SetIsInputMethodEnabled(this, false);
        SizeChanged += (_, _) => AdjustInfoOverlayScale();
    }

    private partial void LoadAndInsertGlassLayer();

    public bool HasVideo
    {
        get => _hasVideo;
        private set
        {
            if (value == _hasVideo) return;
            _hasVideo = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (value == _isPlaying) return;
            _isPlaying = value;
            OnPropertyChanged();
        }
    }

    public bool ShouldLoop
    {
        get => _shouldLoop;
        private set
        {
            if (value == _shouldLoop) return;
            _shouldLoop = value;
            OnPropertyChanged();
        }
    }

    public bool UseHardwareAcceleration
    {
        get => _useHardwareAcceleration;
        private set
        {
            if (value == _useHardwareAcceleration) return;
            _useHardwareAcceleration = value;
            OnPropertyChanged();
        }
    }

    public BitmapSource CoverArt
    {
        get => _coverArt;
        private set
        {
            if (ReferenceEquals(value, _coverArt)) return;
            if (value == null) return;
            _coverArt = value;
            OnPropertyChanged();
        }
    }

    public void Dispose()
    {
        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        SettingHelper.Set("VolumeDouble", LinearVolume, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("ShouldLoop", ShouldLoop, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("UseHardwareAcceleration", UseHardwareAcceleration, "QuickLook.Plugin.VideoViewer");

        try
        {
            mediaElement?.Close();

            Task.Run(() =>
            {
                mediaElement?.MediaUriPlayer.Dispose();
                mediaElement = null;
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }

        _lyricTimer?.Stop();
        _lyricTimer = null;
        _lyricLines = null;
        _midiPlayer?.Dispose();
        _midiPlayer = null;
    }

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Focus();
            System.Windows.Input.Keyboard.Focus(this);
        }), DispatcherPriority.Input);

        Focus();
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (e.OriginalSource is DependencyObject depObj)
            {
                if (IsDescendantOf(depObj, videoControlContainer) || IsDescendantOf(depObj, volumeSliderLayer))
                {
                    return;
                }
            }

            if (e.ClickCount == 2)
            {
                _isMouseDown = false;
                TogglePlayPause(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            _isMouseDown = true;
            _mouseDownPosition = e.GetPosition(this);
        }
    }

    private void Panel_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isMouseDown && e.LeftButton == MouseButtonState.Pressed)
        {
            Point currentPosition = e.GetPosition(this);
            if (Math.Abs(currentPosition.X - _mouseDownPosition.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(currentPosition.Y - _mouseDownPosition.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                _isMouseDown = false;

                var wnd = Window.GetWindow(this);
                if (wnd != null && wnd.WindowStyle != WindowStyle.None)
                {
                    wnd.DragMove();
                }
            }
        }
    }

    private void Panel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isMouseDown = false;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Focus();
            System.Windows.Input.Keyboard.Focus(this);
        }), DispatcherPriority.Input);
    }

    private void ViewerPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.System && e.SystemKey == Key.K) ||
            (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt))
        {
            RotateVideo();
            TriggerShowControlContainer();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.PageUp:
                PlayNextVideo(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                PlayNextVideo(1);
                e.Handled = true;
                break;
            case Key.Left:
            case Key.A:
                SeekRelative(-3);
                TriggerShowControlContainer();
                e.Handled = true;
                break;
            case Key.Right:
            case Key.D:
                SeekRelative(3);
                TriggerShowControlContainer();
                e.Handled = true;
                break;
            case Key.Q:
                StepFrame(-1);
                TriggerShowControlContainer();
                e.Handled = true;
                break;
            case Key.E:
                StepFrame(1);
                TriggerShowControlContainer();
                e.Handled = true;
                break;

            case Key.Up:
            case Key.W:
                ChangeVolume(0.05);
                ShowAndAutoCollapseVolumeSlider();
                TriggerShowControlContainer();
                e.Handled = true;
                break;
            case Key.Down:
            case Key.S:
                ChangeVolume(-0.05);
                ShowAndAutoCollapseVolumeSlider();
                TriggerShowControlContainer();
                e.Handled = true;
                break;
        }
    }

    private void RotateVideo()
    {
        if (mediaElement == null) return;

        if (mediaElement.LayoutTransform is RotateTransform rotateTransform)
        {
            rotateTransform.Angle = (rotateTransform.Angle + 90) % 360;
        }
        else
        {
            mediaElement.LayoutTransform = new RotateTransform(90);
        }
    }

    private async void ShowAndAutoCollapseVolumeSlider()
    {
        if (volumeSliderLayer == null) return;

        volumeSliderLayer.Visibility = Visibility.Visible;

        _volumeSliderCancelTokenSource?.Cancel();
        _volumeSliderCancelTokenSource = new System.Threading.CancellationTokenSource();
        var token = _volumeSliderCancelTokenSource.Token;

        try
        {
            await Task.Delay(1000, token);

            if (!token.IsCancellationRequested)
            {
                volumeSliderLayer.Visibility = Visibility.Collapsed;
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void SeekRelative(double seconds)
    {
        if (mediaElement == null) return;
        long ticksToSeek = (long)(seconds * TimeSpan.TicksPerSecond);
        long newPosition = mediaElement.MediaPosition + ticksToSeek;
        mediaElement.MediaPosition = Math.Max(0L, Math.Min(mediaElement.MediaDuration, newPosition));
    }

    private void StepFrame(int frames)
    {
        if (mediaElement == null) return;

        if (mediaElement.IsPlaying)
        {
            mediaElement.Pause();
        }

        long ticksPerFrame = (long)(TimeSpan.TicksPerSecond / _fps);
        long newPosition = mediaElement.MediaPosition + (frames * ticksPerFrame);
        mediaElement.MediaPosition = Math.Max(0L, Math.Min(mediaElement.MediaDuration, newPosition));
    }

    private void ToggleFullscreen()
    {
        var wnd = Window.GetWindow(this);
        if (wnd != null)
        {
            var method = wnd.GetType().GetMethod("ToggleFullscreen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            method?.Invoke(wnd, null);
        }
    }

    private void TriggerShowControlContainer()
    {
        var show = (Storyboard)videoControlContainer.FindResource("ShowControlStoryboard");
        show.Begin();
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
    {
        if (child == null || parent == null)
            return false;

        DependencyObject current = child;
        while (current != null)
        {
            if (ReferenceEquals(current, parent))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void MediaOpened(object o, RoutedEventArgs args)
    {
        if (mediaElement == null)
            return;

        HasVideo = mediaElement.HasVideo;

        _context.IsBusy = false;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Focus();
            System.Windows.Input.Keyboard.Focus(this);
        }), DispatcherPriority.Input);
    }

    private void MediaFailed(object sender, MediaFailedEventArgs e)
    {
        ((MediaUriElement)sender).Dispatcher.BeginInvoke(new Action(() =>
        {
            _context.ViewerContent = new TextBlock()
            {
                Text = e.Exception.ToString(),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _context.IsBusy = false;
        }));
    }

    private void MediaEnded(object sender, RoutedEventArgs e)
    {
        if (mediaElement == null)
            return;

        mediaElement.MediaPosition = 0L;
        if (ShouldLoop)
        {
            IsPlaying = true;

            mediaElement.Play();
        }
        else
        {
            IsPlaying = false;

            mediaElement.Pause();
        }
    }

    private void ShowViedoControlContainer(object sender, MouseEventArgs e)
    {
        var show = (Storyboard)videoControlContainer.FindResource("ShowControlStoryboard");
        if (videoControlContainer.Opacity == 0d || videoControlContainer.Opacity == 1d)
            show.Begin();
    }

    private void AutoHideViedoControlContainer(object sender, EventArgs e)
    {
        if (!HasVideo)
            return;

        if (videoControlContainer.IsMouseOver)
            return;

        var hide = (Storyboard)videoControlContainer.FindResource("HideControlStoryboard");

        hide.Begin();
    }

    private void PlayerStateChanged(PlayerState oldState, PlayerState newState)
    {
        switch (newState)
        {
            case PlayerState.Playing:
                IsPlaying = true;
                break;

            case PlayerState.Paused:
            case PlayerState.Stopped:
            case PlayerState.Closed:
                IsPlaying = false;
                break;
        }
    }

    private void UpdateMeta(string path, MediaInfoLib info)
    {
        if (HasVideo)
            return;

        try
        {
            if (info == null)
                throw new NullReferenceException();

            var title = info.Get(StreamKind.General, 0, "Title");
            var artist = info.Get(StreamKind.General, 0, "Performer");
            var album = info.Get(StreamKind.General, 0, "Album");

            metaTitle.Text = !string.IsNullOrWhiteSpace(title) ? title : Path.GetFileName(path);
            metaArtists.Text = artist;
            metaAlbum.Text = album;

            // Extract cover art
            var coverData = info.Get(StreamKind.General, 0, "Cover_Data");
            var coverBytes = CoverDataExtractor.Extract(coverData);
            CoverArt = CoverDataExtractor.Extract(coverBytes);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            metaTitle.Text = Path.GetFileName(path);
            metaArtists.Text = metaAlbum.Text = string.Empty;
        }

        metaArtists.Visibility = string.IsNullOrEmpty(metaArtists.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        metaAlbum.Visibility = string.IsNullOrEmpty(metaAlbum.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var lyricPath = Path.ChangeExtension(path, ".lrc");

        // Stop previous timer if any.
        _lyricTimer?.Stop();
        _lyricTimer = null;
        _lyricLines = null;

        if (File.Exists(lyricPath))
        {
            var buffer = File.ReadAllBytes(lyricPath);
            var encoding = CharsetDetector.DetectFromBytes(buffer).Detected?.Encoding ?? Encoding.Default;

            _lyricLines = [.. LrcHelper.ParseText(encoding.GetString(buffer))];
        }
        else
        {
            // Use embedded lyrics from MediaInfo if present.
            // Common tag: General/Lyrics (may contain LRC formatted content).
            var embeddedLyrics = info?.Get(StreamKind.General, 0, "Lyrics");

            // Only check whether the tag of lyrics is present by MediaInfo
            if (!string.IsNullOrWhiteSpace(embeddedLyrics))
            {
                var file = TagLib.File.Create(path);
                embeddedLyrics = file.Tag.Lyrics;

                // Check whether the tag of lyrics is present by TagLib#
                if (!string.IsNullOrWhiteSpace(embeddedLyrics))
                {
                    _lyricLines = [.. LrcHelper.ParseText(embeddedLyrics)];
                }
            }
        }

        if (_lyricLines != null && _lyricLines.Length != 0)
        {
            _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _lyricTimer.Tick += (sender, e) =>
            {
                if (_lyricLines != null && _lyricLines.Length != 0)
                {
                    var lyric = LrcHelper.GetNearestLrc(_lyricLines, new TimeSpan(mediaElement.MediaPosition));
                    metaLyric.Text = lyric?.LrcText?.Trim();
                }
                else
                {
                    metaLyric.Text = null;
                    metaLyric.Visibility = Visibility.Collapsed;
                }
            };
            _lyricTimer.Start();

            metaLyric.Visibility = Visibility.Visible;
        }
        else
        {
            metaLyric.Visibility = Visibility.Collapsed;
        }
    }

    public double LinearVolume
    {
        get => mediaElement.Volume;
        set
        {
            mediaElement.Volume = value;
            OnPropertyChanged();
        }
    }

    private void ChangeVolume(double delta)
    {
        LinearVolume = Math.Max(0d, Math.Min(1d, LinearVolume + delta));
    }

    private void TogglePlayPause(object sender, EventArgs e)
    {
        if (mediaElement.IsPlaying)
            mediaElement.Pause();
        else
            mediaElement.Play();
    }

    private void ToggleShouldLoop(object sender, EventArgs e)
    {
        ShouldLoop = !ShouldLoop;
    }

    private void ToggleHardwareAcceleration(object sender, EventArgs e)
    {
        UseHardwareAcceleration = !UseHardwareAcceleration;
        SettingHelper.Set("UseHardwareAcceleration", UseHardwareAcceleration, "QuickLook.Plugin.VideoViewer");
        HardwareAccelerationModeChanged(UseHardwareAcceleration);
    }

    private void HardwareAccelerationModeChanged(bool enable)
    {
        try
        {
            var player = mediaElement?.MediaUriPlayer;
            if (player == null) return;

            if (mediaElement.Source == null)
            {
                // No source loaded yet – just store the flag for the next Open
                player.Dispatcher.BeginInvoke(() =>
                    player.EnableLAVHardwareAcceleration = enable);
                return;
            }

            player.Dispatcher.BeginInvoke(() =>
                player.ApplyHardwareAcceleration(enable));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public void LoadAndPlay(string path, MediaInfoLib info)
    {
        if (_currentPath != path)
        {
            _currentPath = path;
            InitializePlaylist(path);
        }

        if (info != null)
        {
            _mediaInfo = info;
            string fpsStr = info.Get(StreamKind.Video, 0, "FrameRate");
            if (double.TryParse(fpsStr, out double parsedFps) && parsedFps > 0)
            {
                _fps = parsedFps;
            }
        }

        if (!HasVideo)
        {
            string audioCodec = info?.Get(StreamKind.Audio, 0, "Format");

            if (audioCodec?.Equals("MIDI", StringComparison.OrdinalIgnoreCase) ?? false)
            {
                _midiPlayer = new MidiPlayer(this, _context);
                _midiPlayer.LoadAndPlay(path);
                return;
            }
        }

        UpdateMeta(path, info);

        // detect rotation
        _ = double.TryParse(info?.Get(StreamKind.Video, 0, "Rotation"), out var rotation);
        // Correct rotation: on some machine the value "90" becomes "90000" by some reason
        if (rotation > 360d)
            rotation /= 1e3;
        if (Math.Abs(rotation) > 0.1d)
            mediaElement.LayoutTransform = new RotateTransform(rotation, 0.5d, 0.5d);

        mediaElement.Source = new Uri(path);
        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        LinearVolume = Math.Max(0d, Math.Min(1d, SettingHelper.Get("VolumeDouble", 1d, "QuickLook.Plugin.VideoViewer")));

        mediaElement.Play();
        PopulateVideoInfo(path, info);
    }

    private void PopulateVideoInfo(string path, MediaInfoLib info)
    {
        try
        {
            infoFileName.Text = Path.GetFileName(path);

            var width = info?.Get(StreamKind.Video, 0, "Width");
            var height = info?.Get(StreamKind.Video, 0, "Height");
            if (!string.IsNullOrEmpty(width) && !string.IsNullOrEmpty(height))
                infoResolution.Text = $"{width} x {height}";
            else
                infoResolution.Text = "未知";

            infoFps.Text = _fps.ToString("0.##");

            var vCodec = info?.Get(StreamKind.Video, 0, "Format");
            infoVideoCodec.Text = !string.IsNullOrEmpty(vCodec) ? vCodec : "未知";

            var aCodec = info?.Get(StreamKind.Audio, 0, "Format");
            infoAudioCodec.Text = !string.IsNullOrEmpty(aCodec) ? aCodec : "无";

            var durationMsStr = info?.Get(StreamKind.General, 0, "Duration");
            if (double.TryParse(durationMsStr, out var ms))
            {
                var t = TimeSpan.FromMilliseconds(ms);
                infoDuration.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
            }
            else
            {
                infoDuration.Text = "未知";
            }

            if (File.Exists(path))
            {
                var len = new FileInfo(path).Length;
                infoFileSize.Text = $"{(len / 1024d / 1024d):0.##} MB";
            }
            else
            {
                infoFileSize.Text = "未知";
            }

            var br = info?.Get(StreamKind.General, 0, "OverallBitRate");
            if (double.TryParse(br, out var bps))
            {
                infoBitrate.Text = $"{(bps / 1000d):0.##} Kbps";
            }
            else
            {
                infoBitrate.Text = "未知";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public void ToggleVideoInfoOverlay()
    {
        if (videoInfoOverlay == null) return;
        videoInfoOverlay.Visibility = videoInfoOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void AdjustInfoOverlayScale()
    {
        if (videoInfoOverlay == null) return;
        if (ActualWidth == 0 || ActualHeight == 0) return;

        double baseWidth = 640.0;
        double scale = ActualWidth / baseWidth;

        // Constraint scaling factor to a reasonable range
        scale = Math.Max(0.7, Math.Min(3.0, scale));

        videoInfoOverlay.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void CopyCurrentFrame()
    {
        try
        {
            if (mediaElement == null || mediaElement.ActualWidth <= 0 || mediaElement.ActualHeight <= 0)
                return;

            double width = mediaElement.ActualWidth;
            double height = mediaElement.ActualHeight;

            RenderTargetBitmap bmp = new RenderTargetBitmap(
                (int)width, (int)height, 96, 96, PixelFormats.Pbgra32);

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                VisualBrush brush = new VisualBrush(mediaElement);
                context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }

            bmp.Render(visual);

            Clipboard.SetImage(bmp);

            ShowCopyToast();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void ShowCopyToast()
    {
        if (copyStatusOverlay == null) return;

        copyStatusOverlay.Visibility = Visibility.Visible;

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.2))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.0))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));

        animation.Completed += (s, e) =>
        {
            copyStatusOverlay.Visibility = Visibility.Collapsed;
        };

        copyStatusOverlay.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    private class NaturalStringComparer : System.Collections.Generic.IComparer<string>
    {
        public int Compare(string x, string y)
        {
            return StrCmpLogicalW(x, y);
        }
    }

    private void PlayNextVideo(int direction)
    {
        if (_playlist == null || _playlist.Count <= 1 || _playlistIndex == -1)
            return;

        int newIndex = _playlistIndex + direction;
        if (newIndex < 0)
        {
            newIndex = _playlist.Count - 1;
        }
        else if (newIndex >= _playlist.Count)
        {
            newIndex = 0;
        }

        string nextPath = _playlist[newIndex];
        _playlistIndex = newIndex;
        _currentPath = nextPath;

        if (_mediaInfo != null)
        {
            try
            {
                _mediaInfo.Open(nextPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MediaInfo open failed: " + ex);
            }
        }

        if (_context != null)
        {
            _context.Title = Path.GetFileName(nextPath);
        }

        LoadAndPlay(nextPath, _mediaInfo);
    }

    private void InitializePlaylist(string currentPath)
    {
        _playlist.Clear();
        _playlistIndex = -1;

        try
        {
            string dir = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(dir)) return;

            IntPtr hostHwnd = IntPtr.Zero;
            try
            {
                var viewWindowManagerType = Type.GetType("QuickLook.ViewWindowManager, QuickLook");
                if (viewWindowManagerType != null)
                {
                    var instanceProp = viewWindowManagerType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                        ?? viewWindowManagerType.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    var managerInstance = instanceProp?.GetValue(null) ?? viewWindowManagerType.GetMethod("GetInstance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
                    if (managerInstance != null)
                    {
                        var lastHostWindowProp = viewWindowManagerType.GetProperty("LastHostWindow", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        hostHwnd = (IntPtr)(lastHostWindowProp?.GetValue(managerInstance) ?? IntPtr.Zero);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Reflect LastHostWindow failed: " + ex);
            }

            System.Collections.Generic.List<string> allFiles = new System.Collections.Generic.List<string>();
            if (hostHwnd != IntPtr.Zero)
            {
                allFiles = GetExplorerFiles(hostHwnd);
            }

            if (allFiles == null || allFiles.Count == 0 || !allFiles.Contains(currentPath))
            {
                allFiles = new System.Collections.Generic.List<string>(Directory.GetFiles(dir));
                allFiles.Sort(new NaturalStringComparer());
            }

            var videoExtensions = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".3gp", ".m4v",
                ".mpg", ".mpeg", ".m2ts", ".rmvb", ".asf", ".ogg", ".ogv", ".ogm"
            };

            foreach (var file in allFiles)
            {
                string ext = Path.GetExtension(file);
                if (videoExtensions.Contains(ext) || ext.Equals(Path.GetExtension(currentPath), StringComparison.OrdinalIgnoreCase))
                {
                    _playlist.Add(file);
                }
            }

            _playlistIndex = _playlist.IndexOf(currentPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Init playlist failed: " + ex);
        }
    }

    private System.Collections.Generic.List<string> GetExplorerFiles(IntPtr hwnd)
    {
        var paths = new System.Collections.Generic.List<string>();
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic windows = shell.Windows();
                int count = windows.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic window = windows.Item(i);
                    if (window == null) continue;

                    long rawHwnd = 0;
                    try
                    {
                        rawHwnd = Convert.ToInt64(window.HWND);
                    }
                    catch { }

                    if (rawHwnd == (long)hwnd)
                    {
                        dynamic document = window.Document;
                        if (document != null)
                        {
                            dynamic folder = document.Folder;
                            dynamic items = folder.Items();
                            int itemCount = items.Count;
                            for (int j = 0; j < itemCount; j++)
                            {
                                dynamic item = items.Item(j);
                                if (item != null)
                                {
                                    paths.Add(item.Path);
                                }
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error getting explorer files: " + ex);
        }
        return paths;
    }
}
