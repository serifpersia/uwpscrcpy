using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace uwpscrcpy
{
    public class AdbClient : IDisposable
    {
        private TcpClient _tcp;
        private NetworkStream _netStream;
        private bool _isDisposed;
        private int _idCounter = 1;
        private readonly byte[] _headerBuffer = new byte[24];
        private readonly object _writeLock = new object();

        private bool _hasSentSignature = false;

        public bool IsConnected => _tcp != null && _tcp.Connected && !_isDisposed;

        private readonly ConcurrentDictionary<uint, AdbStream> _activeStreams = new ConcurrentDictionary<uint, AdbStream>();
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<AdbStream>> _pendingOpens = new ConcurrentDictionary<uint, TaskCompletionSource<AdbStream>>();

        public event Action<string> OnLog;

        public async Task Connect(string ip, int port, AdbCrypto crypto)
        {
            OnLog?.Invoke($"[ADB] Connecting to {ip}:{port}...");
            _tcp = new TcpClient { NoDelay = true };
            _tcp.ReceiveBufferSize = 1024 * 1024;

            await _tcp.ConnectAsync(ip, port);
            _netStream = _tcp.GetStream();

            _hasSentSignature = false;

            SendPacketInternal(AdbProtocol.A_CNXN, AdbProtocol.A_VERSION, AdbProtocol.MAX_PAYLOAD, Encoding.UTF8.GetBytes("host::\0"));

            _ = Task.Factory.StartNew(() => ReceiverLoop(crypto), TaskCreationOptions.LongRunning);

            await Task.Delay(200);
        }

        private void ReceiverLoop(AdbCrypto crypto)
        {
            try
            {
                while (!_isDisposed)
                {
                    var pkt = AdbProtocol.AdbPacket.Parse(_netStream, _headerBuffer);
                    bool payloadConsumed = false;
                    try
                    {
                        if (pkt.IsCommand(AdbProtocol.A_WRTE))
                        {
                            SendPacketInternal(AdbProtocol.A_OKAY, pkt.Arg1, pkt.Arg0, null);
                            if (pkt.DataLength > 0 && _activeStreams.TryGetValue(pkt.Arg1, out var stream))
                            {
                                stream.EnqueueData(pkt.Payload, (int)pkt.DataLength);
                                payloadConsumed = true;
                            }
                        }
                        else if (pkt.IsCommand(AdbProtocol.A_OKAY))
                        {
                            if (_pendingOpens.TryRemove(pkt.Arg1, out var tcs))
                            {
                                var stream = new AdbStream(this, pkt.Arg1, pkt.Arg0);
                                _activeStreams[pkt.Arg1] = stream;
                                tcs.TrySetResult(stream);
                            }
                            else if (_activeStreams.TryGetValue(pkt.Arg1, out var stream))
                            {
                                stream.AckWrite();
                            }
                        }
                        else if (pkt.IsCommand(AdbProtocol.A_CLSE))
                        {
                            SendPacketInternal(AdbProtocol.A_OKAY, pkt.Arg1, pkt.Arg0, null);
                            if (_activeStreams.TryRemove(pkt.Arg1, out var stream)) stream.SignalClose();
                        }
                        else if (pkt.IsCommand(AdbProtocol.A_AUTH))
                        {
                            if (pkt.Arg0 == 1)
                            {

                                byte[] token = new byte[pkt.DataLength];

                                Array.Copy(pkt.Payload, 0, token, 0, (int)pkt.DataLength);

                                if (_hasSentSignature)
                                {
                                    OnLog?.Invoke("[ADB] Auth failed (Key Rejected). Sending Public Key...");
                                    SendPacketInternal(AdbProtocol.A_AUTH, 3, 0, crypto.GetPublicKeyBlob());
                                    _hasSentSignature = false;
                                }
                                else
                                {
                                    OnLog?.Invoke("[ADB] Signing Auth Token...");
                                    byte[] sig = crypto.Sign(token);
                                    SendPacketInternal(AdbProtocol.A_AUTH, 2, 0, sig);
                                    _hasSentSignature = true;
                                }
                            }
                        }
                        else if (pkt.IsCommand(AdbProtocol.A_CNXN))
                        {
                            OnLog?.Invoke("Connected.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CRITICAL] Error handling packet {pkt.Command:X}: {ex.Message}");
                    }
                    finally
                    {
                        if (!payloadConsumed) pkt.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FATAL] Receiver Loop Died: {ex.Message}");
                Dispose();
            }
        }

        public async Task<Stream> OpenTunnel(string dest)
        {
            uint localId = (uint)Interlocked.Increment(ref _idCounter);
            var tcs = new TaskCompletionSource<AdbStream>();
            _pendingOpens[localId] = tcs;

            SendPacketInternal(AdbProtocol.A_OPEN, localId, 0, Encoding.UTF8.GetBytes(dest + "\0"));

            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _pendingOpens.TryRemove(localId, out _);
                throw new TimeoutException("ADB OpenTunnel Timed out");
            }

            return await tcs.Task;
        }

        public async Task<string> ExecuteShell(string cmd)
        {
            using (var stream = await OpenTunnel($"shell:{cmd}"))
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public async Task DeployServer(string b64)
        {
            await ExecuteShell("rm /data/local/tmp/scrcpy-server.jar");
            OnLog?.Invoke("[ADB] Uploading JAR...");
            await ExecuteShell("rm /data/local/tmp/scrcpy.b64");

            int chunk = 4096;
            for (int i = 0; i < b64.Length; i += chunk)
            {
                string s = b64.Substring(i, Math.Min(chunk, b64.Length - i));
                await ExecuteShell($"echo -n \"{s}\" >> /data/local/tmp/scrcpy.b64");
            }
            await ExecuteShell("base64 -d /data/local/tmp/scrcpy.b64 > /data/local/tmp/scrcpy-server.jar");
        }

        public void RunServerWithLogging(string cmd)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using (var stream = await OpenTunnel($"shell:{cmd}"))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        while (true)
                        {
                            string line = await reader.ReadLineAsync();
                            if (line == null) break;
                            if (!string.IsNullOrWhiteSpace(line)) OnLog?.Invoke($"[S] {line}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Server Error] {ex.Message}");
                }
            });
        }

        public void SendPacket(uint c, uint a0, uint a1, byte[] d) => SendPacketInternal(c, a0, a1, d);

        private void SendPacketInternal(uint c, uint a0, uint a1, byte[] d)
        {
            try
            {
                int dLen = d?.Length ?? 0;
                uint crc = 0;
                if (d != null) foreach (byte b in d) crc += b;

                byte[] h = new byte[24 + dLen];
                BitConverter.GetBytes(c).CopyTo(h, 0);
                BitConverter.GetBytes(a0).CopyTo(h, 4);
                BitConverter.GetBytes(a1).CopyTo(h, 8);
                BitConverter.GetBytes(dLen).CopyTo(h, 12);
                BitConverter.GetBytes(crc).CopyTo(h, 16);
                BitConverter.GetBytes(c ^ 0xFFFFFFFF).CopyTo(h, 20);
                if (d != null) Array.Copy(d, 0, h, 24, dLen);

                lock (_writeLock)
                {
                    if (!_isDisposed && _netStream != null) _netStream.Write(h, 0, h.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SendPacket] Failed: {ex.Message}");
                Dispose();
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
            _tcp?.Dispose();
            foreach (var s in _activeStreams.Values) s.SignalClose();
            _activeStreams.Clear();
        }
    }
}