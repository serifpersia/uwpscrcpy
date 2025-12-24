using System;
using System.Collections.Concurrent;
using System.IO;

namespace uwpscrcpy
{
    public static class SimpleBufferPool
    {
        private static readonly ConcurrentStack<byte[]> _pool = new ConcurrentStack<byte[]>();
        public const int BUFFER_SIZE = 1024 * 1024;

        public static byte[] Rent()
        {
            if (_pool.TryPop(out var buffer)) return buffer;
            return new byte[BUFFER_SIZE];
        }

        public static void Return(byte[] buffer)
        {
            if (buffer != null && buffer.Length == BUFFER_SIZE)
                _pool.Push(buffer);
        }
    }

    public static class AdbProtocol
    {
        public const uint A_OKAY = 0x59414b4f;
        public const uint A_CLSE = 0x45534c43;
        public const uint A_WRTE = 0x45545257;
        public const uint A_OPEN = 0x4e45504f;
        public const uint A_CNXN = 0x4e584e43;
        public const uint A_AUTH = 0x48545541;
        public const uint A_VERSION = 0x01000001;

        public const uint MAX_PAYLOAD = 1024 * 1024;

        public struct AdbPacket
        {
            public uint Command;
            public uint Arg0;
            public uint Arg1;
            public uint DataLength;
            public byte[] Payload;

            public static unsafe AdbPacket Parse(Stream stream, byte[] headerBuffer)
            {
                ReadExact(stream, headerBuffer, 24);

                var pkt = new AdbPacket();

                fixed (byte* ptr = headerBuffer)
                {
                    uint* uptr = (uint*)ptr;
                    pkt.Command = *uptr;
                    pkt.Arg0 = *(uptr + 1);
                    pkt.Arg1 = *(uptr + 2);
                    pkt.DataLength = *(uptr + 3);
                    uint crc = *(uptr + 4);
                    uint magic = *(uptr + 5);

                    if (pkt.Command != (magic ^ 0xFFFFFFFF))
                        throw new Exception("Invalid Magic");
                }

                if (pkt.DataLength > 0)
                {
                    if (pkt.DataLength <= SimpleBufferPool.BUFFER_SIZE)
                    {
                        pkt.Payload = SimpleBufferPool.Rent();
                    }
                    else
                    {
                        pkt.Payload = new byte[pkt.DataLength];
                    }

                    ReadExact(stream, pkt.Payload, (int)pkt.DataLength);
                }

                return pkt;
            }

            private static void ReadExact(Stream s, byte[] buf, int count)
            {
                int offset = 0;
                while (offset < count)
                {
                    int read = s.Read(buf, offset, count - offset);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
            }

            public void Dispose()
            {
                if (Payload != null)
                {
                    SimpleBufferPool.Return(Payload);
                    Payload = null;
                }
            }

            public bool IsCommand(uint cmd) => Command == cmd;
        }
    }
}