using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace uwpscrcpy
{
    public sealed partial class MainPage : Page
    {
        private const bool ENABLE_DIAGNOSTICS = false;

        private ScrcpySession _session;
        private CancellationTokenSource _cts;
        private MediaPlayer _mediaPlayer;
        private MediaPlayerElement _currentVideoElement;

        private MediaStreamSource _mss;
        private readonly ConcurrentQueue<MediaStreamSample> _sampleQueue = new ConcurrentQueue<MediaStreamSample>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private byte[] _cachedConfigData = null;
        private long _baselinePts = -1;

        private const int MAX_QUEUE_DEPTH = 2;
        private bool _isBuffering = true;
        private const int JITTER_BUFFER_TARGET = 2;

        private const int DefaultBufferSize = 128 * 1024;
        private readonly ConcurrentStack<byte[]> _bufferPool = new ConcurrentStack<byte[]>();
        private readonly byte[] _headerBuffer = new byte[12];

        public MainPage()
        {
            this.InitializeComponent();

            SystemNavigationManager.GetForCurrentView().BackRequested += (s, e) =>
            {
                if (ApplicationView.GetForCurrentView().IsFullScreenMode)
                {
                    ExitFullScreen();
                    e.Handled = true;
                }
            };

            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown;
        }

        private void CoreWindow_KeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (args.VirtualKey == VirtualKey.Escape)
            {
                if (ApplicationView.GetForCurrentView().IsFullScreenMode)
                {
                    ExitFullScreen();
                    args.Handled = true;
                }
            }
        }

        private void HamburgerButton_Click(object sender, RoutedEventArgs e) => MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;

        private async void ConnectToggle_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectToggle.IsChecked == true)
            {
                ConnectToggle.Content = "Stop";
                SetControlsEnabled(false);
                await StartConnection();
            }
            else
            {
                ConnectToggle.Content = "Start";
                Cleanup();
                SetControlsEnabled(true);
            }
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e) { if (ApplicationView.GetForCurrentView().IsFullScreenMode) ExitFullScreen(); else EnterFullScreen(); }
        private void EnterFullScreen() { HamburgerButton.Visibility = Visibility.Collapsed; MainSplitView.IsPaneOpen = false; ApplicationView.GetForCurrentView().TryEnterFullScreenMode(); }
        private void ExitFullScreen() { HamburgerButton.Visibility = Visibility.Visible; ApplicationView.GetForCurrentView().ExitFullScreenMode(); }
        private void SetControlsEnabled(bool enabled) { IpAddressBox.IsEnabled = enabled; PortBox.IsEnabled = enabled; BitRateBox.IsEnabled = enabled; }

        private void Log(string msg) { Debug.WriteLine($"[SYS] {msg}"); _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { LogBlock.Text = $"[{DateTime.Now:HH:mm:ss}] {msg}\n" + LogBlock.Text; }); }

        private async Task StartConnection()
        {
            try
            {
                Cleanup();
                _cts = new CancellationTokenSource();
                _session = new ScrcpySession();

                string ip = IpAddressBox.Text;
                if (!int.TryParse(PortBox.Text, out int port)) port = 5555;
                int bitRate = 8000000;
                int.TryParse(BitRateBox.Text, out int mbps);
                if (mbps > 0) bitRate = mbps * 1000000;

                int maxSize = 720;
                int.TryParse(maxSizeBox.Text, out int size);
                if (size > 144) maxSize = size;

                _mediaPlayer = new MediaPlayer();
                _mediaPlayer.RealTimePlayback = true;
                _mediaPlayer.AutoPlay = true;

                await _session.ConnectAndStartAsync(ip, port, bitRate, maxSize, Log);
                Log($"Stream Started. Device: {_session.DeviceName} ({_session.Width}x{_session.Height})");

                _ = Task.Run(() => VideoLoop(_cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                Log($"[Connection Error] {ex.Message}");
                Cleanup();
                ConnectToggle.IsChecked = false;
                ConnectToggle.Content = "Start";
                SetControlsEnabled(true);
            }
        }

        private void Cleanup()
        {
            _cts?.Cancel();
            _session?.Dispose();
            _session = null;

            if (_currentVideoElement != null)
            {
                _currentVideoElement.SetMediaPlayer(null);
                VideoSurface.Children.Clear();
                _currentVideoElement = null;
            }

            if (_mediaPlayer != null)
            {
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            VideoSurface.Width = double.NaN;
            VideoSurface.Height = double.NaN;

            FlushQueue();
            _cachedConfigData = null;
            _baselinePts = -1;
            _mss = null;
        }

        private async Task VideoLoop(CancellationToken token)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            long lastPacketTime = 0;

            try
            {
                var stream = _session.VideoStream;
                if (ENABLE_DIAGNOSTICS) Debug.WriteLine("[DEBUG] Video Loop Started");

                while (!token.IsCancellationRequested)
                {
                    if (_sampleQueue.Count > 30)
                    {
                        await Task.Delay(10, token);
                        continue;
                    }

                    if (!await ReadExactToBufferAsync(stream, _headerBuffer, 12, 0, token)) break;

                    long now = sw.ElapsedMilliseconds;
                    long gap = now - lastPacketTime;
                    lastPacketTime = now;

                    ulong ptsData = BitConverter.ToUInt64(StartBigEndian(_headerBuffer, 0, 8), 0);
                    uint packetSize = BitConverter.ToUInt32(StartBigEndian(_headerBuffer, 8, 4), 0);
                    bool isConfig = (ptsData & 0x8000000000000000) != 0;
                    bool isKey = (ptsData & 0x4000000000000000) != 0;
                    ulong ptsUs = ptsData & 0x3FFFFFFFFFFFFFFF;

                    int totalSize = (int)packetSize;
                    int offset = 0;
                    if (isKey && _cachedConfigData != null) { offset = _cachedConfigData.Length; totalSize += offset; }

                    byte[] currentBuffer = RentBuffer(totalSize);
                    if (isKey && _cachedConfigData != null) Array.Copy(_cachedConfigData, 0, currentBuffer, 0, _cachedConfigData.Length);

                    if (!await ReadExactToBufferAsync(stream, currentBuffer, (int)packetSize, offset, token)) break;

                    if (isConfig)
                    {
                        _cachedConfigData = new byte[packetSize];
                        Array.Copy(currentBuffer, 0, _cachedConfigData, 0, (int)packetSize);
                        ReturnBuffer(currentBuffer);

                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => InitVideoPlayer(_cachedConfigData));
                    }
                    else
                    {
                        if (_mss == null) { ReturnBuffer(currentBuffer); continue; }

                        if (_baselinePts == -1) _baselinePts = (long)ptsUs;
                        long relativeUs = (long)ptsUs - _baselinePts;
                        if (relativeUs < 0) relativeUs = 0;

                        var sample = MediaStreamSample.CreateFromBuffer(currentBuffer.AsBuffer(0, totalSize), TimeSpan.FromTicks(relativeUs * 10));
                        sample.KeyFrame = isKey;
                        sample.Processed += (s, e) => ReturnBuffer(currentBuffer);

                        _sampleQueue.Enqueue(sample);

                        if (_isBuffering)
                        {
                            if (_sampleQueue.Count >= JITTER_BUFFER_TARGET)
                            {
                                _isBuffering = false;
                                int count = _sampleQueue.Count;
                                for (int i = 0; i < count; i++) _signal.Release();
                            }
                        }
                        else
                        {
                            _signal.Release();
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[CRASH] {ex.Message}"); }
        }

        private void FlushQueue() { while (_sampleQueue.TryDequeue(out _)) { } while (_signal.CurrentCount > 0) _signal.Wait(0); }

        private void InitVideoPlayer(byte[] codecPrivateData)
        {
            try
            {
                if (_mss != null) return;

                VideoSurface.Width = _session.Width;
                VideoSurface.Height = _session.Height;

                VideoSurface.Children.Clear(); // Just to be safe
                _currentVideoElement = new MediaPlayerElement
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Stretch = Stretch.Uniform,
                    AreTransportControlsEnabled = false,
                    AutoPlay = true
                };

                _currentVideoElement.SetMediaPlayer(_mediaPlayer);

                VideoSurface.Children.Add(_currentVideoElement);

                var videoProps = VideoEncodingProperties.CreateH264();
                videoProps.Width = _session.Width;
                videoProps.Height = _session.Height;
                videoProps.ProfileId = H264ProfileIds.Main;
                videoProps.SetFormatUserData(codecPrivateData);

                _mss = new MediaStreamSource(new VideoStreamDescriptor(videoProps));
                _mss.BufferTime = TimeSpan.FromMilliseconds(0);
                _mss.CanSeek = false;
                _mss.SampleRequested += OnSampleRequested;

                _mediaPlayer.Source = MediaSource.CreateFromMediaStreamSource(_mss);
            }
            catch (Exception ex) { Log($"[Init Error] {ex.Message}"); }
        }

        private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                Stopwatch waitTimer = Stopwatch.StartNew();
                await _signal.WaitAsync(_cts?.Token ?? CancellationToken.None);
                waitTimer.Stop();

                if (_sampleQueue.TryDequeue(out MediaStreamSample sample))
                {
                    if (waitTimer.ElapsedMilliseconds > 30 && _sampleQueue.Count == 0)
                        _isBuffering = true;

                    args.Request.Sample = sample;
                }
            }
            catch { }
            finally { deferral.Complete(); }
        }

        private byte[] RentBuffer(int size) { if (_bufferPool.TryPop(out byte[] buffer) && buffer.Length >= size) return buffer; return new byte[Math.Max(size, DefaultBufferSize)]; }
        private void ReturnBuffer(byte[] buffer) { if (buffer.Length >= DefaultBufferSize) _bufferPool.Push(buffer); }
        private async Task<bool> ReadExactToBufferAsync(AdbStream stream, byte[] buffer, int count, int offset, CancellationToken token) { int current = offset; int end = offset + count; while (current < end) { int read = await stream.ReadAsync(buffer, current, end - current, token); if (read == 0) return false; current += read; } return true; }
        private byte[] StartBigEndian(byte[] input, int offset, int length) { byte[] val = new byte[length]; Array.Copy(input, offset, val, 0, length); if (BitConverter.IsLittleEndian) Array.Reverse(val); return val; }

        private void VideoSurface_PointerPressed(object sender, PointerRoutedEventArgs e) => _session?.SendTouch(0, e, VideoSurface);
        private void VideoSurface_PointerMoved(object sender, PointerRoutedEventArgs e) { if (e.Pointer.IsInContact) _session?.SendTouch(2, e, VideoSurface); }
        private void VideoSurface_PointerReleased(object sender, PointerRoutedEventArgs e) => _session?.SendTouch(1, e, VideoSurface);
    }
}