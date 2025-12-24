using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.System;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using ScrcpyVideoEngine;

namespace uwpscrcpy
{
    public sealed partial class MainPage : Page
    {
        private VideoEngine _videoEngine;
        private ScrcpySession _session;
        private InputManager _inputManager;
        private CancellationTokenSource _cts;

        private readonly DisplayRequest _displayRequest = new DisplayRequest();
        private readonly byte[] _headerBuffer = new byte[12];
        private byte[] _pendingConfig = null;
        private bool _sessionActive = false;
        private bool _isUhidMouseMode = false;

        public MainPage()
        {
            this.InitializeComponent();

            _videoEngine = new VideoEngine();
            _videoEngine.OnDebugLog += (msg) => Log("[CPP] " + msg);
            _videoEngine.OnResolutionChanged += VideoEngine_OnResolutionChanged;

            SystemNavigationManager.GetForCurrentView().BackRequested += (s, e) => { if (ApplicationView.GetForCurrentView().IsFullScreenMode) { ExitFullScreen(); e.Handled = true; } };
            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown;
        }

        private async void VideoEngine_OnResolutionChanged(uint newWidth, uint newHeight)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Log($"Resolution changed to: {newWidth}x{newHeight}");
                if (_session != null)
                {
                    _session.UpdateDimensions(newWidth, newHeight);
                    VideoPanel.Width = newWidth;
                    VideoPanel.Height = newHeight;
                    _videoEngine.ResizeSwapChain(newWidth, newHeight);
                }
            });
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
                Cleanup();
                _cts = new CancellationTokenSource();
                _session = new ScrcpySession();

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

                if (video) _displayRequest.RequestActive();

                await _session.ConnectAndStartAsync(ip, port, bitRate, maxSize, maxFps, video, _isUhidMouseMode, Log);

                _inputManager = new InputManager(_session, VideoPanel, MousePadArea, LeftClickArea, RightClickArea, InvertScrollToggle);
                _inputManager.RegisterInputHandlers();
                _inputManager.SetUhidMode(_isUhidMouseMode);

                if (_isUhidMouseMode)
                {
                    MouseControlContainer.Visibility = Visibility.Visible;
                    VideoContainer.Visibility = Visibility.Collapsed;
                }
                else if (video)
                {
                    Log($"Connected. Stream: {_session.Width}x{_session.Height}");
                    _videoEngine.Initialize(_session.Width, _session.Height);
                    _videoEngine.SetPanel(VideoPanel);
                    VideoPanel.Width = _session.Width;
                    VideoPanel.Height = _session.Height;
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

            _videoEngine?.Stop();

            _inputManager?.UnregisterInputHandlers();
            _inputManager = null;

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
                    if (!await ReadExactAsync(stream, _headerBuffer, 0, 12, token)) break;

                    ulong ptsData = ((ulong)_headerBuffer[0] << 56) | ((ulong)_headerBuffer[1] << 48) |
                                    ((ulong)_headerBuffer[2] << 40) | ((ulong)_headerBuffer[3] << 32) |
                                    ((ulong)_headerBuffer[4] << 24) | ((ulong)_headerBuffer[5] << 16) |
                                    ((ulong)_headerBuffer[6] << 8) | _headerBuffer[7];

                    uint packetSize = ((uint)_headerBuffer[8] << 24) | ((uint)_headerBuffer[9] << 16) |
                                      ((uint)_headerBuffer[10] << 8) | _headerBuffer[11];

                    bool isConfig = (ptsData & 0x8000000000000000) != 0;
                    ulong ptsUs = ptsData & 0x3FFFFFFFFFFFFFFF;

                    byte[] packetData = new byte[packetSize];
                    if (!await ReadExactAsync(stream, packetData, 0, (int)packetSize, token)) break;

                    if (isConfig)
                    {
                        _pendingConfig = packetData;
                        if (_sessionActive) Log("[Info] Config packet received mid-stream (Orientation change?)");
                        _sessionActive = true;
                        continue;
                    }

                    IBuffer bufferToSend = (_pendingConfig != null)
                        ? MergeBuffer(_pendingConfig, packetData)
                        : packetData.AsBuffer();

                    if (_pendingConfig != null) _pendingConfig = null;

                    _videoEngine.PushFrame(bufferToSend, (long)ptsUs);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Video Loop Error: {ex.Message}");
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
    }
}