using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml;

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

        public ScrcpySession()
        {
            _crypto = new AdbCrypto();
            _adb = new AdbClient();
        }

        public async Task ConnectAndStartAsync(string ip, int port, int bitRate, int maxSize, Action<string> logCallback)
        {
            logCallback?.Invoke($"Connecting to {ip}:{port}...");

            await _adb.Connect(ip, port, _crypto);

            logCallback?.Invoke("Deploying server...");
            string jar64 = GetJarBase64();
            await _adb.DeployServer(jar64);

            string serverArgs = $"audio=false send_device_meta=true send_codec_meta=true send_frame_meta=true send_dummy_byte=false " +
                                $"video_bit_rate={bitRate} max_size={maxSize}";

            string cmd = $"CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / com.genymobile.scrcpy.Server 3.3.3 " +
                         $"scid=00000001 log_level=info control=true tunnel_forward=true {serverArgs}";

            _adb.RunServerWithLogging(cmd);
            await Task.Delay(500);

            _videoStream = (AdbStream)await _adb.OpenTunnel("localabstract:scrcpy_00000001");
            _controlStream = (AdbStream)await _adb.OpenTunnel("localabstract:scrcpy_00000001");

            logCallback?.Invoke("Tunnels open. Reading handshake...");

            await ReadInitialMetadataAsync();
        }

        private async Task ReadInitialMetadataAsync()
        {
            byte[] nameBuffer = await ReadExactAsync(64);
            DeviceName = System.Text.Encoding.UTF8.GetString(nameBuffer).Trim('\0');

            await ReadExactAsync(4);

            byte[] w = await ReadExactAsync(4);
            Width = ((uint)w[0] << 24) | ((uint)w[1] << 16) | ((uint)w[2] << 8) | w[3];

            byte[] h = await ReadExactAsync(4);
            Height = ((uint)h[0] << 24) | ((uint)h[1] << 16) | ((uint)h[2] << 8) | h[3];
        }

        public void SendTouch(byte action, PointerRoutedEventArgs e, FrameworkElement relativeTo)
        {
            if (_controlStream == null || Width == 0 || Height == 0) return;

            var pos = e.GetCurrentPoint(relativeTo).Position;

            try
            {
                double scaleX = (double)Width / relativeTo.ActualWidth;
                double scaleY = (double)Height / relativeTo.ActualHeight;

                int x = Math.Max(0, Math.Min((int)Width, (int)(pos.X * scaleX)));
                int y = Math.Max(0, Math.Min((int)Height, (int)(pos.Y * scaleY)));

                byte[] p = new byte[32];

                p[0] = 2;
                p[1] = action;

                for (int i = 2; i < 10; i++) p[i] = 0xFF;

                WriteInt32BE(p, 10, x);
                WriteInt32BE(p, 14, y);
                WriteUInt16BE(p, 18, (int)Width);
                WriteUInt16BE(p, 20, (int)Height);

                p[22] = 0xFF; p[23] = 0xFF;
                WriteInt32BE(p, 24, 1);

                _controlStream.Write(p, 0, 32);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Touch Error] {ex.Message}");
            }
        }

        private async Task<byte[]> ReadExactAsync(int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await _videoStream.ReadAsync(buffer, offset, count - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }

        private void WriteInt32BE(byte[] b, int offset, int val)
        {
            b[offset] = (byte)(val >> 24);
            b[offset + 1] = (byte)(val >> 16);
            b[offset + 2] = (byte)(val >> 8);
            b[offset + 3] = (byte)(val);
        }

        private void WriteUInt16BE(byte[] b, int offset, int val)
        {
            b[offset] = (byte)(val >> 8);
            b[offset + 1] = (byte)(val);
        }

        private string GetJarBase64()
        {
            using (var s = typeof(ScrcpySession).GetTypeInfo().Assembly.GetManifestResourceStream("uwpscrcpy.scrcpy-server.jar"))
            {
                if (s == null) return null;
                var b = new byte[s.Length];
                s.Read(b, 0, b.Length);
                return Convert.ToBase64String(b);
            }
        }

        public void Dispose()
        {
            _videoStream?.Dispose();
            _controlStream?.Dispose();
            _adb?.Dispose();
        }
    }
}