using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime; // Required for AsBuffer()
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.Storage.Streams;
// Add your C++ Component namespace
using ScrcpyVideoEngine;

namespace uwpscrcpy
{
    public sealed partial class MainPage : Page
    {
        private const bool ENABLE_DEBUG_LOGGING = true;

        // C++ Video Engine
        private VideoEngine _videoEngine;

        private ScrcpySession _session;
        private CancellationTokenSource _cts;
        private readonly DisplayRequest _displayRequest = new DisplayRequest();

        // Frame reading state
        private readonly byte[] _headerBuffer = new byte[12];
        private byte[] _pendingConfig = null;
        private bool _sessionActive = false;

        // Mouse/Input state
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
        private const int TOUCH_RATE_LIMIT_MS = 16;
        private double _pendingMouseX = 0;
        private double _pendingMouseY = 0;

        public MainPage()
        {
            this.InitializeComponent();

            // Initialize C++ Engine
            _videoEngine = new VideoEngine();
            _videoEngine.OnDebugLog += (msg) => Log("[CPP] " + msg);

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
            MaxSizeBox.IsEnabled = enabled;
            MaxFpsBox.IsEnabled = enabled;
            ControlsOnlyToggle.IsEnabled = enabled;
            UhidMouseToggle.IsEnabled = enabled && ControlsOnlyToggle.IsOn;
            InvertScrollToggle.IsEnabled = enabled && UhidMouseToggle.IsOn;
        }

        private void Log(string msg)
        {
            // Simple thread-safe logging
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (LogBlock.Text.Length > 2000) LogBlock.Text = LogBlock.Text.Substring(0, 1000);
                string time = DateTime.Now.ToString("mm:ss");
                LogBlock.Text = $"[{time}] {msg}\n" + LogBlock.Text;
            });
        }

        private async Task StartConnection()
        {
            try
            {
                // 1. Ensure clean state
                Cleanup();

                _cts = new CancellationTokenSource();
                _session = new ScrcpySession();

                // 2. Parse Inputs
                string ip = IpAddressBox.Text;
                if (!int.TryParse(PortBox.Text, out int port)) port = 5555;
                int.TryParse(BitRateBox.Text, out int mbps);
                int bitRate = (mbps > 0) ? mbps * 1000000 : 4000000;
                int.TryParse(MaxSizeBox.Text, out int size);
                int maxSize = size;
                int.TryParse(MaxFpsBox.Text, out int fps);
                int maxFps = (fps > 0) ? fps : 30;
                bool video = !ControlsOnlyToggle.IsOn;
                _isUhidMouseMode = UhidMouseToggle.IsOn && !video;

                if (video)
                {
                    _displayRequest.RequestActive();
                }

                // 3. Connect via ADB
                await _session.ConnectAndStartAsync(ip, port, bitRate, maxSize, maxFps, video, _isUhidMouseMode, Log);

                if (_isUhidMouseMode)
                {
                    MouseControlContainer.Visibility = Visibility.Visible;
                    VideoContainer.Visibility = Visibility.Collapsed;
                }
                else if (video)
                {
                    Log($"Connected. Stream: {_session.Width}x{_session.Height}");

                    // 4. Initialize C++ Engine
                    _videoEngine.Initialize(_session.Width, _session.Height);

                    // IMPORTANT: Pass the SwapChainPanel to the C++ component
                    _videoEngine.SetPanel(VideoPanel);

                    // Update UI size
                    VideoPanel.Width = _session.Width;
                    VideoPanel.Height = _session.Height;

                    // 5. Start Reading Loop
                    _ = Task.Factory.StartNew(() => VideoLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                else
                {
                    Log($"Controls-Only Mode.");

                    VideoPanel.Width = _session.Width;
                    VideoPanel.Height = _session.Height;
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
            _sessionActive = false;
            _pendingConfig = null;

            // Stop C++ Engine
            if (_videoEngine != null)
            {
                _videoEngine.Stop();
            }

            // UI Reset
            _isUhidMouseMode = false;
            MouseControlContainer.Visibility = Visibility.Collapsed;
            VideoContainer.Visibility = Visibility.Visible;
            VideoPanel.Width = double.NaN;
            VideoPanel.Height = double.NaN;

            try { _displayRequest.RequestRelease(); } catch { }

            _cts?.Cancel();
            _session?.Dispose();
            _session = null;
        }

        private async Task VideoLoop(CancellationToken token)
        {
            try
            {
                var stream = _session.VideoStream;
                if (stream == null) return;

                while (!token.IsCancellationRequested)
                {
                    // 1. Read Header (12 bytes)
                    if (!await ReadExactAsync(stream, _headerBuffer, 0, 12, token)) break;

                    // 2. Parse Header
                    ulong ptsData = ((ulong)_headerBuffer[0] << 56) | ((ulong)_headerBuffer[1] << 48) |
                                    ((ulong)_headerBuffer[2] << 40) | ((ulong)_headerBuffer[3] << 32) |
                                    ((ulong)_headerBuffer[4] << 24) | ((ulong)_headerBuffer[5] << 16) |
                                    ((ulong)_headerBuffer[6] << 8) | _headerBuffer[7];

                    uint packetSize = ((uint)_headerBuffer[8] << 24) | ((uint)_headerBuffer[9] << 16) |
                                      ((uint)_headerBuffer[10] << 8) | _headerBuffer[11];

                    bool isConfig = (ptsData & 0x8000000000000000) != 0;
                    ulong ptsUs = ptsData & 0x3FFFFFFFFFFFFFFF; // Raw PTS

                    // 3. Read Body
                    byte[] packetData = new byte[packetSize];
                    if (!await ReadExactAsync(stream, packetData, 0, (int)packetSize, token)) break;

                    // 4. Handle Configuration (SPS/PPS)
                    if (isConfig)
                    {
                        // Save this configuration to merge with the next Keyframe
                        _pendingConfig = packetData;

                        if (_sessionActive)
                        {
                            // If we receive config mid-stream, the resolution probably changed.
                            // The C++ engine handles stream changes via MF_E_TRANSFORM_STREAM_CHANGE,
                            // but we might want to alert the user or restart.
                            Log("[Info] Config packet received mid-stream (Orientation change?)");
                        }

                        _sessionActive = true;
                        continue;
                    }

                    // 5. Prepare Buffer for Engine
                    IBuffer bufferToSend;

                    if (_pendingConfig != null)
                    {
                        // Merge Config + Frame (Critical for DX Hardware Decoder to init)
                        bufferToSend = MergeBuffer(_pendingConfig, packetData);
                        _pendingConfig = null; // Clear after use
                    }
                    else
                    {
                        bufferToSend = packetData.AsBuffer();
                    }

                    // 6. Push to C++ Engine
                    // Convert raw PTS (microseconds) to what the engine expects.
                    // The engine sample says: sample->SetSampleTime((packet.pts - m_baselinePts) * 10);
                    // Pass the raw PTS here, let C++ handle baseline calc.
                    _videoEngine.PushFrame(bufferToSend, (long)ptsUs);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Video Loop Error: {ex.Message}");
                // Handle disconnect on UI thread
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (ConnectToggle.IsChecked == true)
                    {
                        Cleanup();
                        ConnectToggle.IsChecked = false;
                        ConnectToggle.Content = "Start";
                        SetControlsEnabled(true);
                    }
                });
            }
        }

        // Helper to combine config (SPS/PPS) with keyframe data
        private IBuffer MergeBuffer(byte[] config, byte[] frame)
        {
            byte[] combined = new byte[config.Length + frame.Length];
            System.Buffer.BlockCopy(config, 0, combined, 0, config.Length);
            System.Buffer.BlockCopy(frame, 0, combined, config.Length, frame.Length);
            return combined.AsBuffer();
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

        // =============================================================
        // INPUT HANDLING (Updated to use VideoPanel)
        // =============================================================

        private void VideoSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _session == null) return;
            var properties = e.GetCurrentPoint(VideoPanel).Properties;
            if (properties.IsRightButtonPressed) { e.Handled = true; return; }
            _session.SendTouch(0, e, VideoPanel);
        }

        private void VideoSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode) return;
            if (e.Pointer.IsInContact)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTouchSendTime >= TOUCH_RATE_LIMIT_MS)
                {
                    _session?.SendTouch(2, e, VideoPanel);
                    _lastTouchSendTime = now;
                }
            }
        }

        private void VideoSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _session == null) return;
            var updateKind = e.GetCurrentPoint(VideoPanel).Properties.PointerUpdateKind;
            if (updateKind == Windows.UI.Input.PointerUpdateKind.RightButtonReleased)
            {
                _session.SendBackEvent(0);
                _session.SendBackEvent(1);
                e.Handled = true;
                return;
            }
            _session.SendTouch(1, e, VideoPanel);
        }

        private void VideoSurface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _session == null) return;

            var point = e.GetCurrentPoint(VideoPanel);
            var pos = point.Position;
            var props = point.Properties;

            double actualWidth = VideoPanel.ActualWidth;
            double actualHeight = VideoPanel.ActualHeight;

            double scaleX = (double)_session.Width / actualWidth;
            double scaleY = (double)_session.Height / actualHeight;
            int x = Math.Max(0, Math.Min((int)_session.Width, (int)(pos.X * scaleX)));
            int y = Math.Max(0, Math.Min((int)_session.Height, (int)(pos.Y * scaleY)));

            const float ScrollSensitivity = 0.05f;
            const float MaxScroll = 1.0f;

            float vScrollFloat = (float)props.MouseWheelDelta / 120.0f;
            vScrollFloat *= ScrollSensitivity;
            if (InvertScrollToggle.IsOn) vScrollFloat = -vScrollFloat;

            vScrollFloat = Math.Max(-MaxScroll, Math.Min(MaxScroll, vScrollFloat));
            short vScrollFixed = (short)Math.Round(vScrollFloat * 32767.0f);

            int buttons = 0;
            if (props.IsLeftButtonPressed) buttons |= 1;
            if (props.IsRightButtonPressed) buttons |= 2;
            if (props.IsMiddleButtonPressed) buttons |= 4;

            _session.SendScrollEvent(x, y, 0, vScrollFixed, buttons);
        }

        // =============================================================
        // UI TOGGLES
        // =============================================================

        private void ControlsOnlyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (UhidMouseToggle != null)
            {
                UhidMouseToggle.IsEnabled = ControlsOnlyToggle.IsOn;
                if (!ControlsOnlyToggle.IsOn) UhidMouseToggle.IsOn = false;
            }
        }

        private void UhidMouseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (InvertScrollToggle != null) InvertScrollToggle.IsEnabled = UhidMouseToggle.IsOn;
        }

        // =============================================================
        // UHID MOUSE IMPLEMENTATION (Unchanged from your code)
        // =============================================================

        private void MousePadArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            uint ptrId = e.Pointer.PointerId;
            var pos = e.GetCurrentPoint(MousePadArea).Position;
            if (!_pointerPositions.ContainsKey(ptrId)) _pointerPositions.Add(ptrId, pos);
            else _pointerPositions[ptrId] = pos;

            if (_pointerPositions.Count == 1)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTapTimestamp < DOUBLE_TAP_THRESHOLD_MS)
                {
                    _isDragging = true;
                    _session?.SendHidInputEvent(MOUSE_BUTTON_LEFT, new Point(0, 0));
                    _lastTapTimestamp = 0;
                }
                else _lastTapTimestamp = now;
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
                if (_isDragging || _isLeftMouseButtonDown) buttons |= MOUSE_BUTTON_LEFT;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTouchSendTime >= TOUCH_RATE_LIMIT_MS)
                {
                    if (Math.Abs(_pendingMouseX) >= 1.0 || Math.Abs(_pendingMouseY) >= 1.0)
                    {
                        var deltaToSend = new Point(_pendingMouseX, _pendingMouseY);
                        _session?.SendHidInputEvent(buttons, deltaToSend);
                        _pendingMouseX = 0; _pendingMouseY = 0;
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
                    if (InvertScrollToggle.IsOn) vScrollToSend = -vScrollToSend;
                    _session?.SendHidInputEvent(0, new Point(0, 0), vScrollToSend, 0);
                }
            }
        }

        private void MousePadArea_PointerReleased(object sender, PointerRoutedEventArgs e) => CleanupPointer(e.Pointer.PointerId);
        private void MousePadArea_PointerCanceled(object sender, PointerRoutedEventArgs e) => CleanupPointer(e.Pointer.PointerId);
        private void MousePadArea_PointerExited(object sender, PointerRoutedEventArgs e) => CleanupPointer(e.Pointer.PointerId);

        private void CleanupPointer(uint ptrId)
        {
            if (_pointerPositions.ContainsKey(ptrId)) _pointerPositions.Remove(ptrId);
            _pendingMouseX = 0; _pendingMouseY = 0;
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
            if (!_isDragging) _session?.SendHidInputEvent(0, new Point(0, 0));
        }

        private void RightClickArea_PointerPressed(object sender, PointerRoutedEventArgs e) => _session?.SendHidInputEvent(MOUSE_BUTTON_RIGHT, new Point(0, 0));
        private void RightClickArea_PointerReleased(object sender, PointerRoutedEventArgs e) => _session?.SendHidInputEvent(0, new Point(0, 0));
    }
}