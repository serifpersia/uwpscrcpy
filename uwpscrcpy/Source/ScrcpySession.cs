using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml;
using System.Text;
using Windows.Foundation;

namespace uwpscrcpy
{
    public class ScrcpySession : IDisposable
    {
        private AdbClient _adb;
        private AdbCrypto _crypto;
        private AdbStream _videoStream;
        private AdbStream _controlStream;

        public string DeviceName { get; private set; } = "Unknown";
        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public AdbStream VideoStream => _videoStream;

        private bool isUhidMouse = false;

        private const byte SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT = 2;
        private const byte SC_CONTROL_MSG_TYPE_INJECT_SCROLL_EVENT = 3;
        private const byte SC_CONTROL_MSG_TYPE_BACK_OR_SCREEN_ON = 4;
        private const byte SC_CONTROL_MSG_TYPE_UHID_CREATE = 12;
        private const byte SC_CONTROL_MSG_TYPE_UHID_INPUT = 13;
        private const byte SC_CONTROL_MSG_TYPE_UHID_DESTROY = 14;

        private const ushort SC_HID_ID_MOUSE = 2;

        private static readonly byte[] SC_HID_MOUSE_REPORT_DESC = {
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00, 0x05,
            0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01, 0x95, 0x05,
            0x75, 0x01, 0x81, 0x02, 0x95, 0x01, 0x75, 0x03, 0x81, 0x01, 0x05,
            0x01, 0x09, 0x30, 0x09, 0x31, 0x09, 0x38, 0x15, 0x81, 0x25, 0x7F,
            0x75, 0x08, 0x95, 0x03, 0x81, 0x06, 0x05, 0x0C, 0x0A, 0x38, 0x02,
            0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x01, 0x81, 0x06, 0xC0,
            0xC0,
        };

        public ScrcpySession() { _crypto = new AdbCrypto(); _adb = new AdbClient(); }

        public async Task ConnectAndStartAsync(string ip, int port, int bitRate, int maxSize, int maxFps, bool video, bool useUhidMouse, Action<string> logCallback)
        {
            logCallback?.Invoke($"Connecting to {ip}:{port}...");
            await _adb.Connect(ip, port, _crypto);

            logCallback?.Invoke("Deploying server...");
            string jar64 = GetJarBase64();
            await _adb.DeployServer(jar64);

            string cmd;
            if (video)
            {
                string serverArgs = $"log_level=info tunnel_forward=true audio=false send_dummy_byte=false " +
                                    $"send_device_meta=true send_codec_meta=true video=true control=true " +
                                    $"video_bit_rate={bitRate} max_size={maxSize} max_fps={maxFps}";

                cmd = $"CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / com.genymobile.scrcpy.Server 3.3.3 scid=00000001 {serverArgs}";

                _adb.RunServerWithLogging(cmd);
                await Task.Delay(500);

                _videoStream = (AdbStream)await _adb.OpenTunnel("localabstract:scrcpy_00000001");
                _controlStream = (AdbStream)await _adb.OpenTunnel("localabstract:scrcpy_00000001");

                logCallback?.Invoke("Tunnels open. Reading handshake...");
                await ReadInitialMetadataAsync();
            }
            else
            {
                isUhidMouse = useUhidMouse;
                string serverArgs = $"log_level=info tunnel_forward=true video=false audio=false " +
                                    $"control=true send_device_meta=true send_dummy_byte=false";
                if (isUhidMouse)
                {
                    serverArgs += " mouse=uhid";
                    logCallback?.Invoke("UHID Mouse mode enabled.");
                }

                cmd = $"CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / com.genymobile.scrcpy.Server 3.3.3 scid=00000001 {serverArgs}";

                _adb.RunServerWithLogging(cmd);
                await Task.Delay(500);

                _controlStream = (AdbStream)await _adb.OpenTunnel("localabstract:scrcpy_00000001");
                logCallback?.Invoke("Tunnel open. Reading handshake...");
                await ReadInitialControlsMetadataAsync();

                if (isUhidMouse)
                {
                    SendHidCreateMouse();
                }
            }
        }

        private async Task ReadInitialControlsMetadataAsync()
        {
            byte[] nameBuffer = await ReadExactAsync(_controlStream, 64);
            this.DeviceName = Encoding.UTF8.GetString(nameBuffer).Trim('\0');
            this.Width = 720;
            this.Height = 1280;
        }

        private async Task ReadInitialMetadataAsync()
        {
            byte[] nameBuffer = await ReadExactAsync(_videoStream, 64);
            this.DeviceName = Encoding.UTF8.GetString(nameBuffer).Trim('\0');

            await ReadExactAsync(_videoStream, 4);
            byte[] w = await ReadExactAsync(_videoStream, 4);
            this.Width = ((uint)w[0] << 24) | ((uint)w[1] << 16) | ((uint)w[2] << 8) | w[3];
            byte[] h = await ReadExactAsync(_videoStream, 4);
            this.Height = ((uint)h[0] << 24) | ((uint)h[1] << 16) | ((uint)h[2] << 8) | h[3];
        }

        private async Task<byte[]> ReadExactAsync(AdbStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count) { int read = await stream.ReadAsync(buffer, offset, count - offset, CancellationToken.None); if (read == 0) throw new EndOfStreamException(); offset += read; }
            return buffer;
        }

        public void SendTouch(byte action, PointerRoutedEventArgs e, FrameworkElement relativeTo)
        {
            if (_controlStream == null || Width == 0 || Height == 0) return;

            var pos = e.GetCurrentPoint(relativeTo).Position;
            double actualWidth = relativeTo.ActualWidth;
            double actualHeight = relativeTo.ActualHeight;

            Task.Run(async () =>
            {
                try
                {
                    double scaleX = (double)Width / actualWidth;
                    double scaleY = (double)Height / actualHeight;

                    int x = Math.Max(0, Math.Min((int)Width, (int)(pos.X * scaleX)));
                    int y = Math.Max(0, Math.Min((int)Height, (int)(pos.Y * scaleY)));

                    byte[] p = new byte[32];
                    p[0] = SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT;
                    p[1] = action;
                    for (int i = 2; i < 10; i++) p[i] = 0xFF;
                    WriteInt32BE(p, 10, x);
                    WriteInt32BE(p, 14, y);
                    WriteUInt16BE(p, 18, (ushort)Width);
                    WriteUInt16BE(p, 20, (ushort)Height);
                    p[22] = 0xFF; p[23] = 0xFF;
                    WriteInt32BE(p, 24, 1);

                    await _controlStream.WriteAsync(p, 0, 32, CancellationToken.None);
                }
                catch (Exception ex) { Debug.WriteLine($"[Touch Error] {ex.Message}"); }
            });
        }

        public void SendScrollEvent(int x, int y, short hScroll, short vScroll, int buttons)
        {
            if (_controlStream == null || Width == 0 || Height == 0) return;

            Task.Run(async () =>
            {
                try
                {
                    byte[] p = new byte[21];
                    int offset = 0;
                    p[offset++] = SC_CONTROL_MSG_TYPE_INJECT_SCROLL_EVENT;
                    WriteInt32BE(p, offset, x); offset += 4;
                    WriteInt32BE(p, offset, y); offset += 4;
                    WriteUInt16BE(p, offset, (ushort)Width); offset += 2;
                    WriteUInt16BE(p, offset, (ushort)Height); offset += 2;
                    WriteInt16BE(p, offset, hScroll); offset += 2;
                    WriteInt16BE(p, offset, vScroll); offset += 2;
                    WriteInt32BE(p, offset, buttons);

                    await _controlStream.WriteAsync(p, 0, p.Length, CancellationToken.None);
                }
                catch (Exception ex) { Debug.WriteLine($"[Scroll Error] {ex.Message}"); }
            });
        }

        public void SendBackEvent(byte action)
        {
            if (_controlStream == null) return;
            Task.Run(async () =>
            {
                try
                {
                    byte[] p = new byte[2];
                    p[0] = SC_CONTROL_MSG_TYPE_BACK_OR_SCREEN_ON;
                    p[1] = action;
                    await _controlStream.WriteAsync(p, 0, p.Length, CancellationToken.None);
                }
                catch (Exception ex) { Debug.WriteLine($"[Back Error] {ex.Message}"); }
            });
        }

        private void SendHidCreateMouse()
        {
            if (_controlStream == null) return;
            Task.Run(async () =>
            {
                try
                {
                    byte[] message = new byte[1 + 2 + 2 + 2 + 1 + 0 + 2 + SC_HID_MOUSE_REPORT_DESC.Length];
                    int offset = 0;
                    message[offset++] = SC_CONTROL_MSG_TYPE_UHID_CREATE;
                    offset += WriteUInt16BE(message, offset, SC_HID_ID_MOUSE);
                    offset += WriteUInt16BE(message, offset, 0);
                    offset += WriteUInt16BE(message, offset, 0);
                    message[offset++] = 0;
                    offset += WriteUInt16BE(message, offset, (ushort)SC_HID_MOUSE_REPORT_DESC.Length);
                    Array.Copy(SC_HID_MOUSE_REPORT_DESC, 0, message, offset, SC_HID_MOUSE_REPORT_DESC.Length);
                    await _controlStream.WriteAsync(message, 0, message.Length, CancellationToken.None);
                }
                catch (Exception ex) { Debug.WriteLine($"[HID Create Error] {ex.Message}"); }
            });
        }

        public void SendHidInputEvent(byte buttons, Point delta, int vScroll = 0, int hScroll = 0)
        {
            if (_controlStream == null) return;
            Task.Run(async () =>
            {
                try
                {
                    byte[] hidReport = new byte[5];
                    hidReport[0] = buttons;
                    hidReport[1] = (byte)(sbyte)Math.Max(-127, Math.Min(127, (int)delta.X));
                    hidReport[2] = (byte)(sbyte)Math.Max(-127, Math.Min(127, (int)delta.Y));
                    hidReport[3] = (byte)(sbyte)Math.Max(-127, Math.Min(127, vScroll));
                    hidReport[4] = (byte)(sbyte)Math.Max(-127, Math.Min(127, hScroll));

                    byte[] fullMessage = new byte[1 + 2 + 2 + hidReport.Length];
                    int offset = 0;
                    fullMessage[offset++] = SC_CONTROL_MSG_TYPE_UHID_INPUT;
                    offset += WriteUInt16BE(fullMessage, offset, SC_HID_ID_MOUSE);
                    offset += WriteUInt16BE(fullMessage, offset, (ushort)hidReport.Length);
                    Array.Copy(hidReport, 0, fullMessage, offset, hidReport.Length);

                    await _controlStream.WriteAsync(fullMessage, 0, fullMessage.Length, CancellationToken.None);
                }
                catch (Exception ex) { Debug.WriteLine($"[HID Input Error] {ex.Message}"); }
            });
        }

        private void SendHidDestroyMouse()
        {
            if (_controlStream == null) return;
            Task.Run(async () =>
            {
                try
                {
                    byte[] message = new byte[1 + 2];
                    int offset = 0;
                    message[offset++] = SC_CONTROL_MSG_TYPE_UHID_DESTROY;
                    WriteUInt16BE(message, offset, SC_HID_ID_MOUSE);
                    await _controlStream.WriteAsync(message, 0, message.Length, CancellationToken.None);
                }
                catch (Exception ex) { Debug.WriteLine($"[HID Destroy Error] {ex.Message}"); }
            });
        }

        private int WriteInt16BE(byte[] b, int offset, short val)
        {
            b[offset] = (byte)(val >> 8);
            b[offset + 1] = (byte)val;
            return 2;
        }

        private int WriteUInt16BE(byte[] b, int offset, ushort val)
        {
            b[offset] = (byte)(val >> 8);
            b[offset + 1] = (byte)val;
            return 2;
        }

        private void WriteInt32BE(byte[] b, int offset, int val)
        {
            b[offset] = (byte)(val >> 24);
            b[offset + 1] = (byte)(val >> 16);
            b[offset + 2] = (byte)(val >> 8);
            b[offset + 3] = (byte)val;
        }

        private string GetJarBase64()
        {
            using (var s = typeof(ScrcpySession).GetTypeInfo()
                                             .Assembly
                                             .GetManifestResourceStream("uwpscrcpy.Vendor.scrcpy-server.jar"))
            {
                if (s == null)
                    throw new Exception("scrcpy-server.jar not found.");
                var b = new byte[s.Length];
                s.Read(b, 0, b.Length);
                return Convert.ToBase64String(b);
            }
        }

        public void Dispose()
        {
            if (isUhidMouse) SendHidDestroyMouse();
            _videoStream?.Dispose();
            _controlStream?.Dispose();
            _adb?.Dispose();
        }
    }
}