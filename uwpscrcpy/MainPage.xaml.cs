using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.System;
using Windows.System.Display;
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
        private const bool ENABLE_DEBUG_LOGGING = false;
        private const int POOL_BUFFER_SIZE = 512 * 1024;
        private const int MAX_QUEUED_FRAMES = 15;

        private readonly TimeSpan PLAYER_BUFFER_TIME = TimeSpan.FromMilliseconds(150);

        private ScrcpySession _session;
        private CancellationTokenSource _cts;
        private MediaPlayer _mediaPlayer;
        private MediaPlayerElement _currentVideoElement;
        private MediaStreamSource _mss;
        private readonly ConcurrentQueue<MediaStreamSample> _sampleQueue = new ConcurrentQueue<MediaStreamSample>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private byte[] _cachedConfigData = null;
        private long _baselinePts = -1;
        private bool _waitingForKeyFrame = true;
        private readonly byte[] _headerBuffer = new byte[12];
        private readonly ConcurrentStack<byte[]> _frameDataPool = new ConcurrentStack<byte[]>();
        private readonly DisplayRequest _displayRequest = new DisplayRequest();

        private bool _isUhidMouseMode = false;
        private bool _isLeftMouseButtonDown = false;
        private const byte MOUSE_BUTTON_LEFT = 1 << 0;
        private const byte MOUSE_BUTTON_RIGHT = 1 << 1;
        private Dictionary<uint, Point> _pointerPositions = new Dictionary<uint, Point>();
        private bool _isDragging = false;
        private long _lastTapTimestamp = 0;
        private const int DOUBLE_TAP_THRESHOLD_MS = 200;

        private double _scrollAccumulatorY = 0;
        private const double SCROLL_SENSITIVITY = 0.025;

        private long _lastTouchSendTime = 0;
        private const int TOUCH_RATE_LIMIT_MS = 33;

        private double _pendingMouseX = 0;
        private double _pendingMouseY = 0;

        public MainPage()
        {
            this.InitializeComponent();
            SystemNavigationManager.GetForCurrentView().BackRequested += (s, e) => { if (ApplicationView.GetForCurrentView().IsFullScreenMode) { ExitFullScreen(); e.Handled = true; } };
            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown;
        }

        private void CoreWindow_KeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (args.VirtualKey == VirtualKey.Escape && ApplicationView.GetForCurrentView().IsFullScreenMode)
            {
                ExitFullScreen();
                args.Handled = true;
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

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            if (ApplicationView.GetForCurrentView().IsFullScreenMode)
                ExitFullScreen();
            else
                EnterFullScreen();
        }

        private void EnterFullScreen()
        {
            HamburgerButton.Visibility = Visibility.Collapsed;
            MainSplitView.IsPaneOpen = false;
            ApplicationView.GetForCurrentView().TryEnterFullScreenMode();
        }

        private void ExitFullScreen()
        {
            HamburgerButton.Visibility = Visibility.Visible;
            ApplicationView.GetForCurrentView().ExitFullScreenMode();
        }

        private void SetControlsEnabled(bool enabled)
        {
            IpAddressBox.IsEnabled = enabled;
            PortBox.IsEnabled = enabled;
            BitRateBox.IsEnabled = enabled;
            maxSizeBox.IsEnabled = enabled;
            ControlsOnlyToggle.IsEnabled = enabled;
            UhidMouseToggle.IsEnabled = enabled && ControlsOnlyToggle.IsOn;
            InvertScrollToggle.IsEnabled = enabled && UhidMouseToggle.IsOn;
        }

        private void Log(string msg)
        {
            if (!ENABLE_DEBUG_LOGGING && msg.StartsWith("[DEBUG]")) return;
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (LogBlock.Text.Length > 500) LogBlock.Text = LogBlock.Text.Substring(0, 250);
                LogBlock.Text = $"[{DateTime.Now:mm:ss}] {msg}\n" + LogBlock.Text;
            });
        }

        private async Task StartConnection()
        {
            try
            {
                Cleanup();
                _cts = new CancellationTokenSource();
                _session = new ScrcpySession();

                string ip = IpAddressBox.Text;
                if (!int.TryParse(PortBox.Text, out int port)) port = 5555;

                int.TryParse(BitRateBox.Text, out int mbps);
                int bitRate = (mbps > 0) ? mbps * 1000000 : 4000000;

                int.TryParse(maxSizeBox.Text, out int size);
                int maxSize = (size > 144) ? size : 144;

                bool video = !ControlsOnlyToggle.IsOn;
                _isUhidMouseMode = UhidMouseToggle.IsOn && !video;

                if (video)
                {
                    _displayRequest.RequestActive();
                    _mediaPlayer = new MediaPlayer
                    {
                        RealTimePlayback = true,
                        AutoPlay = true,
                        AudioCategory = MediaPlayerAudioCategory.Communications
                    };
                }

                await _session.ConnectAndStartAsync(ip, port, bitRate, maxSize, video, _isUhidMouseMode, Log);

                if (_isUhidMouseMode)
                {
                    MouseControlContainer.Visibility = Visibility.Visible;
                    VideoContainer.Visibility = Visibility.Collapsed;
                }
                else if (video)
                {
                    Log($"Stream: {_session.Width}x{_session.Height} @ {bitRate / 1000000.0:F1}Mbps");
                    _ = Task.Factory.StartNew(() => VideoLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                else
                {
                    Log($"Controls-Only Mode.");
                    VideoSurface.Width = 720;
                    VideoSurface.Height = 1280;
                }
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                Cleanup();
                ConnectToggle.IsChecked = false;
                ConnectToggle.Content = "Start";
                SetControlsEnabled(true);
            }
        }

        private void Cleanup()
        {
            _isUhidMouseMode = false;
            _isLeftMouseButtonDown = false;
            _isDragging = false;
            _lastTapTimestamp = 0;
            _pointerPositions.Clear();
            _scrollAccumulatorY = 0;
            _pendingMouseX = 0;
            _pendingMouseY = 0;
            _waitingForKeyFrame = true;

            MouseControlContainer.Visibility = Visibility.Collapsed;
            VideoContainer.Visibility = Visibility.Visible;

            try { _displayRequest.RequestRelease(); } catch { }

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
            _frameDataPool.Clear();
        }

        private byte[] RentFrameBuffer(int requiredSize)
        {
            if (requiredSize > POOL_BUFFER_SIZE) return new byte[requiredSize];
            if (_frameDataPool.TryPop(out byte[] buffer)) return buffer;
            return new byte[POOL_BUFFER_SIZE];
        }

        private void ReturnFrameBuffer(byte[] buffer)
        {
            if (buffer.Length == POOL_BUFFER_SIZE && _frameDataPool.Count < 15)
                _frameDataPool.Push(buffer);
        }

        private async Task VideoLoop(CancellationToken token)
        {
            try
            {
                var stream = _session.VideoStream;
                if (stream == null) return;

                while (!token.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, _headerBuffer, 0, 12, token)) break;

                    ulong ptsData = ((ulong)_headerBuffer[0] << 56) | ((ulong)_headerBuffer[1] << 48) |
                                    ((ulong)_headerBuffer[2] << 40) | ((ulong)_headerBuffer[3] << 32) |
                                    ((ulong)_headerBuffer[4] << 24) | ((ulong)_headerBuffer[5] << 16) |
                                    ((ulong)_headerBuffer[6] << 8) | _headerBuffer[7];

                    uint packetSize = ((uint)_headerBuffer[8] << 24) | ((uint)_headerBuffer[9] << 16) |
                                      ((uint)_headerBuffer[10] << 8) | _headerBuffer[11];

                    bool isConfig = (ptsData & 0x8000000000000000) != 0;
                    bool isKey = (ptsData & 0x4000000000000000) != 0;
                    ulong ptsUs = ptsData & 0x3FFFFFFFFFFFFFFF;

                    if (isConfig)
                    {
                        var configData = new byte[packetSize];
                        if (!await ReadExactAsync(stream, configData, 0, (int)packetSize, token)) break;
                        _cachedConfigData = configData;
                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => InitVideoPlayer(_cachedConfigData));
                        continue;
                    }

                    if (_sampleQueue.Count > MAX_QUEUED_FRAMES && !_waitingForKeyFrame)
                    {
                        _waitingForKeyFrame = true;
                        FlushQueue();
                    }

                    if (_waitingForKeyFrame && !isKey)
                    {
                        var trash = RentFrameBuffer((int)packetSize);
                        await ReadExactAsync(stream, trash, 0, (int)packetSize, token);
                        ReturnFrameBuffer(trash);
                        continue;
                    }

                    int headerOffset = 0;
                    int totalSize = (int)packetSize;

                    if (isKey && _cachedConfigData != null)
                    {
                        headerOffset = _cachedConfigData.Length;
                        totalSize += headerOffset;
                    }

                    byte[] decoderBuffer = RentFrameBuffer(totalSize);

                    if (isKey && _cachedConfigData != null)
                    {
                        System.Buffer.BlockCopy(_cachedConfigData, 0, decoderBuffer, 0, _cachedConfigData.Length);
                    }

                    if (!await ReadExactAsync(stream, decoderBuffer, headerOffset, (int)packetSize, token))
                    {
                        ReturnFrameBuffer(decoderBuffer);
                        break;
                    }

                    if (_waitingForKeyFrame && isKey) _waitingForKeyFrame = false;

                    if (_mss != null)
                    {
                        if (_baselinePts == -1) _baselinePts = (long)ptsUs;
                        long relativeUs = (long)ptsUs - _baselinePts;
                        if (relativeUs < 0) relativeUs = 0;

                        var sample = MediaStreamSample.CreateFromBuffer(
                            decoderBuffer.AsBuffer(0, totalSize),
                            TimeSpan.FromTicks(relativeUs * 10));
                        sample.KeyFrame = isKey;
                        sample.Processed += (s, e) => ReturnFrameBuffer(decoderBuffer);

                        _sampleQueue.Enqueue(sample);
                        _signal.Release();
                    }
                    else
                    {
                        ReturnFrameBuffer(decoderBuffer);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"Video Err: {ex.Message}"); }
        }

        private async Task<bool> ReadExactAsync(AdbStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, token);
                if (read == 0) return false;
                totalRead += read;
            }
            return true;
        }

        private void FlushQueue()
        {
            while (_sampleQueue.TryDequeue(out var s)) { }
            while (_signal.CurrentCount > 0) _signal.Wait(0);
        }

        private void InitVideoPlayer(byte[] codecPrivateData)
        {
            if (_mss != null) return;
            VideoSurface.Width = _session.Width;
            VideoSurface.Height = _session.Height;

            _currentVideoElement = new MediaPlayerElement { Stretch = Stretch.Uniform, AutoPlay = true, AreTransportControlsEnabled = false };
            _currentVideoElement.SetMediaPlayer(_mediaPlayer);
            VideoSurface.Children.Add(_currentVideoElement);

            var videoProps = VideoEncodingProperties.CreateH264();
            videoProps.Width = _session.Width;
            videoProps.Height = _session.Height;
            videoProps.ProfileId = H264ProfileIds.Main;
            videoProps.SetFormatUserData(codecPrivateData);

            _mss = new MediaStreamSource(new VideoStreamDescriptor(videoProps))
            {
                BufferTime = PLAYER_BUFFER_TIME,
                CanSeek = false
            };
            _mss.SampleRequested += OnSampleRequested;
            _mediaPlayer.Source = MediaSource.CreateFromMediaStreamSource(_mss);
        }

        private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                if (await _signal.WaitAsync(1000, _cts.Token))
                {
                    if (_sampleQueue.TryDequeue(out MediaStreamSample sample))
                    {
                        args.Request.Sample = sample;
                    }
                }
            }
            catch { }
            finally { deferral.Complete(); }
        }

        private void VideoSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode) return;
            _session?.SendTouch(0, e, VideoSurface);
        }

        private void VideoSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode) return;

            if (e.Pointer.IsInContact)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTouchSendTime >= TOUCH_RATE_LIMIT_MS)
                {
                    _session?.SendTouch(2, e, VideoSurface);
                    _lastTouchSendTime = now;
                }
            }
        }

        private void VideoSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode) return;
            _session?.SendTouch(1, e, VideoSurface);
        }

        private void VideoSurface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode) return;
            _session?.SendScrollEvent(e, VideoSurface, false);
        }

        private void ControlsOnlyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (UhidMouseToggle != null)
            {
                UhidMouseToggle.IsEnabled = ControlsOnlyToggle.IsOn;
                if (!ControlsOnlyToggle.IsOn)
                {
                    UhidMouseToggle.IsOn = false;
                }
            }
        }

        private void UhidMouseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (InvertScrollToggle != null)
            {
                InvertScrollToggle.IsEnabled = UhidMouseToggle.IsOn;
            }
        }

        private void MousePadArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            uint ptrId = e.Pointer.PointerId;
            var pos = e.GetCurrentPoint(MousePadArea).Position;
            if (!_pointerPositions.ContainsKey(ptrId))
                _pointerPositions.Add(ptrId, pos);
            else
                _pointerPositions[ptrId] = pos;

            if (_pointerPositions.Count == 1)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTapTimestamp < DOUBLE_TAP_THRESHOLD_MS)
                {
                    _isDragging = true;
                    _session?.SendHidInputEvent(MOUSE_BUTTON_LEFT, new Point(0, 0));
                    _lastTapTimestamp = 0;
                }
                else
                {
                    _lastTapTimestamp = now;
                }
            }
        }

        private void MousePadArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!e.Pointer.IsInContact) return;

            uint ptrId = e.Pointer.PointerId;
            var currentPosition = e.GetCurrentPoint(MousePadArea).Position;

            if (!_pointerPositions.TryGetValue(ptrId, out Point previousPosition))
            {
                _pointerPositions[ptrId] = currentPosition;
                return;
            }

            double MOUSE_SENSITIVITY = 1.25;

            double rawDX = (currentPosition.X - previousPosition.X) * MOUSE_SENSITIVITY;
            double rawDY = (currentPosition.Y - previousPosition.Y) * MOUSE_SENSITIVITY;

            _pointerPositions[ptrId] = currentPosition;

            if (_pointerPositions.Count == 1)
            {
                _pendingMouseX += rawDX;
                _pendingMouseY += rawDY;

                byte buttons = 0;
                if (_isDragging || _isLeftMouseButtonDown)
                    buttons |= MOUSE_BUTTON_LEFT;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTouchSendTime >= TOUCH_RATE_LIMIT_MS)
                {
                    if (Math.Abs(_pendingMouseX) >= 1.0 || Math.Abs(_pendingMouseY) >= 1.0)
                    {
                        var deltaToSend = new Point(_pendingMouseX, _pendingMouseY);
                        _session?.SendHidInputEvent(buttons, deltaToSend);

                        _pendingMouseX = 0;
                        _pendingMouseY = 0;
                        _lastTouchSendTime = now;
                    }
                }
            }
            else if (_pointerPositions.Count >= 2)
            {
                double rawDeltaY = rawDY * SCROLL_SENSITIVITY;
                if (Math.Abs(rawDY) < 0.5) rawDeltaY = 0;

                _scrollAccumulatorY += rawDeltaY;

                if (Math.Abs(_scrollAccumulatorY) >= 1.0)
                {
                    int vScrollToSend = (int)_scrollAccumulatorY;
                    _scrollAccumulatorY -= vScrollToSend;

                    if (InvertScrollToggle.IsOn)
                        vScrollToSend = -vScrollToSend;

                    _session?.SendHidInputEvent(0, new Point(0, 0), vScrollToSend, 0);
                }
            }
        }

        private void MousePadArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            CleanupPointer(e.Pointer.PointerId);
        }

        private void MousePadArea_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            CleanupPointer(e.Pointer.PointerId);
        }

        private void MousePadArea_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            CleanupPointer(e.Pointer.PointerId);
        }

        private void CleanupPointer(uint ptrId)
        {
            if (_pointerPositions.ContainsKey(ptrId))
                _pointerPositions.Remove(ptrId);

            _pendingMouseX = 0;
            _pendingMouseY = 0;

            if (_isDragging && _pointerPositions.Count == 0)
            {
                _isDragging = false;
                _session?.SendHidInputEvent(0, new Point(0, 0));
            }
        }

        private void LeftClickArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isLeftMouseButtonDown = true;
            _session?.SendHidInputEvent(MOUSE_BUTTON_LEFT, new Point(0, 0));
        }

        private void LeftClickArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isLeftMouseButtonDown = false;
            if (!_isDragging)
                _session?.SendHidInputEvent(0, new Point(0, 0));
        }

        private void RightClickArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _session?.SendHidInputEvent(MOUSE_BUTTON_RIGHT, new Point(0, 0));
        }

        private void RightClickArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _session?.SendHidInputEvent(0, new Point(0, 0));
        }
    }
}