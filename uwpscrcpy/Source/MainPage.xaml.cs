using System;
using System.Reflection;
using System.Threading.Tasks;
using Windows.System;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using ScrcpyVideoEngine;

namespace uwpscrcpy
{
    public sealed partial class MainPage : Page
    {
        private readonly ScrcpyController _controller;
        private readonly AdbCrypto _crypto;
        private InputManager _inputManager;
        private readonly DisplayRequest _displayRequest = new DisplayRequest();

        public MainPage()
        {
            this.InitializeComponent();
            _crypto = new AdbCrypto();
            _controller = new ScrcpyController();
            _controller.SetDispatcher(Window.Current.CoreWindow.Dispatcher);
            _controller.OnLog += (msg) => Log(msg);
            _controller.OnResolutionChanged += Controller_OnResolutionChanged;
            SystemNavigationManager.GetForCurrentView().BackRequested += MainPage_BackRequested;
            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown;
        }

        private void Controller_OnResolutionChanged(uint newWidth, uint newHeight)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Log($"Resolution received: {newWidth}x{newHeight}. Updating video subsystem...");
                _controller.InitializeVideo(newWidth, newHeight);
                VideoPanel.Width = newWidth;
                VideoPanel.Height = newHeight;
            });
        }

        private async void ConnectToggle_Click(object sender, RoutedEventArgs e)
        {
            var button = (ToggleButton)sender;
            if (button.IsChecked == true)
            {
                button.Content = "Connecting...";
                SetControlsEnabled(false);
                bool success = await StartConnectionAsync();
                if (success)
                {
                    button.Content = "Stop";
                }
                else
                {
                    button.Content = "Start";
                    button.IsChecked = false;
                    SetControlsEnabled(true);
                }
            }
            else
            {
                button.Content = "Start";
                await StopConnectionAsync();
                SetControlsEnabled(true);
            }
        }

        private async Task<bool> StartConnectionAsync()
        {
            try
            {
                string ip = IpAddressBox.Text;
                int port = int.Parse(PortBox.Text);
                int.TryParse(BitRateBox.Text, out int mbps);
                int bitRate = (mbps > 0) ? mbps * 1000000 : 8000000;
                int.TryParse(MaxSizeBox.Text, out int maxSize);
                int.TryParse(MaxFpsBox.Text, out int maxFps);
                byte[] jarBytes = GetJarBytes();

                bool isControlsOnly = ControlsOnlyToggle.IsOn;
                bool isVideoEnabled = !isControlsOnly;
                bool isUhidEnabled = isControlsOnly;

                _controller.AuthSignCallback = (token) => _crypto.Sign(token);
                _controller.AuthKeyCallback = () => _crypto.GetPublicKeyBlob();
                _controller.SetPanel(VideoPanel);

                VideoPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                VideoPanel.VerticalAlignment = VerticalAlignment.Stretch;

                await Task.Run(() =>
                {
                    Log("Connecting to ADB...");
                    if (!_controller.Connect(ip, port)) throw new Exception("ADB Fail");
                    Log("Deploying server...");
                    _controller.DeployServer(jarBytes);
                    Log($"Starting scrcpy (Video: {isVideoEnabled}, UHID: {isUhidEnabled})...");
                    _controller.StartScrcpy(bitRate, maxSize, maxFps, isVideoEnabled, isUhidEnabled);
                });

                _inputManager = new InputManager(_controller, VideoPanel, MousePadArea, LeftClickArea, RightClickArea, InvertScrollToggle);
                _inputManager.RegisterInputHandlers();
                _inputManager.SetUhidMode(isUhidEnabled);

                if (isControlsOnly)
                {
                    VideoPanel.Width = double.NaN;
                    VideoPanel.Height = double.NaN;
                    Log("Controls Only (UHID) Active.");
                }

                UpdateInterfaceLayout();
                VolumeControlPanel.Visibility = Visibility.Visible;
                _displayRequest.RequestActive();

                int currentVol = await _controller.GetVolumeAsync();
                if (currentVol != -1) VolumeSlider.Value = currentVol;
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                await StopConnectionAsync();
                return false;
            }
        }

        private async Task StopConnectionAsync()
        {
            Log("Stopping connection...");
            _displayRequest.RequestRelease();

            if (_inputManager != null)
            {
                _inputManager.UnregisterInputHandlers();
                _inputManager = null;
            }

            await Task.Run(() => { _controller?.Stop(); });

            VolumeControlPanel.Visibility = Visibility.Collapsed;
            VideoContainer.Visibility = Visibility.Visible;
            MouseControlContainer.Visibility = Visibility.Collapsed;
            Log("Connection stopped.");
        }

        private void UpdateInterfaceLayout()
        {
            bool isControlsOnly = ControlsOnlyToggle.IsOn;
            if (isControlsOnly)
            {
                VideoContainer.Visibility = Visibility.Collapsed;
                MouseControlContainer.Visibility = Visibility.Visible;
            }
            else
            {
                VideoContainer.Visibility = Visibility.Visible;
                MouseControlContainer.Visibility = Visibility.Collapsed;
            }
        }

        private byte[] GetJarBytes()
        {
            var assembly = typeof(MainPage).GetTypeInfo().Assembly;
            string resourceName = "uwpscrcpy.Vendor.scrcpy-server.jar";
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new Exception("scrcpy-server.jar not found.");
                byte[] buffer = new byte[stream.Length];
                stream.Read(buffer, 0, buffer.Length);
                return buffer;
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            IpAddressBox.IsEnabled = enabled;
            PortBox.IsEnabled = enabled;
            BitRateBox.IsEnabled = enabled;
            MaxSizeBox.IsEnabled = enabled;
            MaxFpsBox.IsEnabled = enabled;
            ControlsOnlyToggle.IsEnabled = enabled;
            InvertScrollToggle.IsEnabled = enabled && ControlsOnlyToggle.IsOn;
        }

        private void Log(string msg)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (LogBlock.Text.Length > 2000) LogBlock.Text = "";
                string time = DateTime.Now.ToString("HH:mm:ss");
                LogBlock.Text = $"[{time}] {msg}\n" + LogBlock.Text;
            });
        }

        private async void HamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
            if (MainSplitView.IsPaneOpen && _controller != null && ConnectToggle.IsChecked == true)
            {
                int vol = await _controller.GetVolumeAsync();
                if (vol != -1)
                {
                    VolumeSlider.PointerCaptureLost -= VolumeSlider_PointerCaptureLost;
                    VolumeSlider.Value = vol;
                    VolumeSlider.PointerCaptureLost += VolumeSlider_PointerCaptureLost;
                }
            }
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            var view = ApplicationView.GetForCurrentView();
            if (view.IsFullScreenMode)
            {
                view.ExitFullScreenMode();
                HamburgerButton.Visibility = Visibility.Visible;
            }
            else
            {
                if (view.TryEnterFullScreenMode())
                {
                    HamburgerButton.Visibility = Visibility.Collapsed;
                    MainSplitView.IsPaneOpen = false;
                }
            }
        }

        private void MainPage_BackRequested(object sender, BackRequestedEventArgs e)
        {
            var view = ApplicationView.GetForCurrentView();
            if (view.IsFullScreenMode)
            {
                view.ExitFullScreenMode();
                HamburgerButton.Visibility = Visibility.Visible;
                e.Handled = true;
            }
        }

        private void CoreWindow_KeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (args.VirtualKey == VirtualKey.Escape)
            {
                var view = ApplicationView.GetForCurrentView();
                if (view.IsFullScreenMode)
                {
                    view.ExitFullScreenMode();
                    HamburgerButton.Visibility = Visibility.Visible;
                    args.Handled = true;
                }
            }
        }

        private void ControlsOnlyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (InvertScrollToggle != null) InvertScrollToggle.IsEnabled = ControlsOnlyToggle.IsOn;
            if (_controller != null) UpdateInterfaceLayout();
        }

        private void VolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_controller != null)
            {
                int vol = (int)VolumeSlider.Value;
                Log($"Setting volume to {vol}...");
                _controller.SetVolume(vol);
            }
        }
    }
}