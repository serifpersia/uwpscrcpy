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
                // --- THIS IS THE CRITICAL FIX ---
                Log($"Resolution received: {newWidth}x{newHeight}. Initializing C++ video subsystem...");

                // Explicitly tell the C++ controller to start the video engine.
                _controller.InitializeVideo(newWidth, newHeight);

                // Update the UI panel size to match.
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
                // --- THIS IS THE CRITICAL FIX ---
                // STEP 1: We are currently on the UI thread. Read all values from UI controls now.
                string ip = IpAddressBox.Text;
                int port = int.Parse(PortBox.Text);
                int.TryParse(BitRateBox.Text, out int mbps);
                int bitRate = (mbps > 0) ? mbps * 1000000 : 8000000;
                int.TryParse(MaxSizeBox.Text, out int maxSize);
                int.TryParse(MaxFpsBox.Text, out int maxFps);
                byte[] jarBytes = GetJarBytes();

                // STEP 2: Perform any other operations that MUST be on the UI thread.
                _controller.AuthSignCallback = (token) => _crypto.Sign(token);
                _controller.AuthKeyCallback = () => _crypto.GetPublicKeyBlob();
                _controller.SetPanel(VideoPanel);

                // STEP 3: Now that we have all the data, switch to a background thread for the
                // long-running network operations.
                await Task.Run(() =>
                {
                    Log("Connecting to ADB...");
                    // Use the local variables, NOT the UI controls directly.
                    bool connected = _controller.Connect(ip, port);
                    if (!connected)
                    {
                        // We can log from here because the Log method safely dispatches to the UI thread.
                        Log("ADB connection failed.");
                        throw new Exception("ADB Connection Failed"); // Abort the operation
                    }

                    Log("Connected. Deploying server...");
                    _controller.DeployServer(jarBytes);
                    Log("Server deployed.");

                    Log($"Starting scrcpy server (Bitrate: {mbps}Mbps, Size: {maxSize}p, FPS: {maxFps})...");
                    _controller.StartScrcpy(bitRate, maxSize, maxFps);
                });

                // After await, we are back on the UI thread. It is safe to update the UI.
                VolumeControlPanel.Visibility = Visibility.Visible;
                return true;
            }
            catch (Exception ex)
            {
                // If anything fails (either on the UI or background thread), this will catch it.
                Log($"Error starting connection: {ex.Message}");
                await StopConnectionAsync(); // Ensure we clean up
                return false;
            }
        }

        private async Task StopConnectionAsync()
        {
            Log("Stopping connection...");
            await Task.Run(() =>
            {
                _controller?.Stop();
            });
            VolumeControlPanel.Visibility = Visibility.Collapsed;
            Log("Connection stopped.");
        }


        private byte[] GetJarBytes()
        {
            var assembly = typeof(MainPage).GetTypeInfo().Assembly;
            string resourceName = "uwpscrcpy.Vendor.scrcpy-server.jar";
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new Exception("scrcpy-server.jar not found in embedded resources.");
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
                if (LogBlock.Text.Length > 4000) LogBlock.Text = LogBlock.Text.Substring(0, 2000);
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
            }
        }

        private void UhidMouseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (InvertScrollToggle != null) InvertScrollToggle.IsEnabled = UhidMouseToggle.IsOn;
        }

        private void VolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            Log("Volume control not yet implemented in this version.");
        }
    }
}