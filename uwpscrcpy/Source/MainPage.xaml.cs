using System;
using System.Reflection;
using System.Threading.Tasks;
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
        private InputManager _inputManager; // New Field

        public MainPage()
        {
            this.InitializeComponent();

            _crypto = new AdbCrypto();
            _controller = new ScrcpyController();

            _controller.SetDispatcher(Window.Current.CoreWindow.Dispatcher);

            _controller.OnLog += (msg) => Log(msg);
            _controller.OnResolutionChanged += Controller_OnResolutionChanged;
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

                bool isVideoEnabled = !ControlsOnlyToggle.IsOn;
                bool isUhidEnabled = UhidMouseToggle.IsOn;

                _controller.AuthSignCallback = (token) => _crypto.Sign(token);
                _controller.AuthKeyCallback = () => _crypto.GetPublicKeyBlob();
                _controller.SetPanel(VideoPanel);

                // Reset Panel Alignment/Size defaults
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

                // Note: SetUhidMode is just for the InputManager state now, 
                // the C++ side handles the creation packet automatically on connect.
                _inputManager.SetUhidMode(isUhidEnabled);

                // --- FIX: Full Screen Mapping for Controls Only ---
                if (!isVideoEnabled)
                {
                    // Remove hardcoded size. Let it fill the Viewbox/Window.
                    VideoPanel.Width = 720;
                    VideoPanel.Height = 1280;
                    Log("Controls Only Mode: Fullscreen Input Mapping active.");
                }

                UpdateInterfaceLayout();
                VolumeControlPanel.Visibility = Visibility.Visible;
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

            // Clean up Input
            if (_inputManager != null)
            {
                _inputManager.UnregisterInputHandlers();
                _inputManager = null;
            }

            // Stop Network
            await Task.Run(() =>
            {
                _controller?.Stop();
            });

            // Reset UI
            VolumeControlPanel.Visibility = Visibility.Collapsed;
            VideoContainer.Visibility = Visibility.Visible;
            MouseControlContainer.Visibility = Visibility.Collapsed;

            Log("Connection stopped.");
        }

        private void UpdateInterfaceLayout()
        {
            bool isControlsOnly = ControlsOnlyToggle.IsOn;
            bool isUhid = UhidMouseToggle.IsOn;

            if (isControlsOnly)
            {
                if (isUhid)
                {
                    // UHID Mode: Show Mouse Control UI
                    VideoContainer.Visibility = Visibility.Collapsed;
                    MouseControlContainer.Visibility = Visibility.Visible;
                }
                else
                {
                    // Normal Controls Mode: 
                    // We MUST keep VideoContainer VISIBLE so it can receive Pointer Events.
                    // It will just be a black screen (since no video frames), which is what we want.
                    VideoContainer.Visibility = Visibility.Visible;
                    MouseControlContainer.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                // Video Mode
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
            UhidMouseToggle.IsEnabled = enabled && ControlsOnlyToggle.IsOn;
            InvertScrollToggle.IsEnabled = enabled && UhidMouseToggle.IsOn;
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

        private void HamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
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

        private void ControlsOnlyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (UhidMouseToggle != null)
            {
                UhidMouseToggle.IsEnabled = ControlsOnlyToggle.IsOn;
                if (!ControlsOnlyToggle.IsOn) UhidMouseToggle.IsOn = false;

                // If we toggle this while connected, update the layout immediately
                if (_controller != null) UpdateInterfaceLayout();
            }
        }

        private void UhidMouseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (InvertScrollToggle != null) InvertScrollToggle.IsEnabled = UhidMouseToggle.IsOn;

            // If connected, switch the input mode on the fly
            if (_inputManager != null)
            {
                _inputManager.SetUhidMode(UhidMouseToggle.IsOn);
                UpdateInterfaceLayout();
            }
        }

        private void VolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            Log("Volume control not implemented in this step.");
        }
    }
}