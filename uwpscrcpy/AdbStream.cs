using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace uwpscrcpy
{
    public struct PooledChunk
    {
        public byte[] Buffer;
        public int Length;
    }

    public class AdbStream : Stream
    {
        private readonly AdbClient _client;
        private readonly uint _localId;
        private readonly uint _remoteId;

        private readonly Queue<PooledChunk> _chunkQueue = new Queue<PooledChunk>();
        private readonly SemaphoreSlim _readSignal = new SemaphoreSlim(0);
        private readonly object _queueLock = new object();

        private readonly SemaphoreSlim _writeAckLatch = new SemaphoreSlim(1, 1);

        private PooledChunk _currentChunk;
        private int _currentChunkOffset;
        private bool _isClosed;

        public AdbStream(AdbClient client, uint localId, uint remoteId)
        {
            _client = client;
            _localId = localId;
            _remoteId = remoteId;
        }

        public void AckWrite()
        {
            if (_writeAckLatch.CurrentCount == 0) _writeAckLatch.Release();
        }

        public void EnqueueData(byte[] buffer, int length)
        {
            if (_isClosed)
            {
                SimpleBufferPool.Return(buffer);
                return;
            }

            lock (_queueLock)
            {
                _chunkQueue.Enqueue(new PooledChunk { Buffer = buffer, Length = length });
            }
            _readSignal.Release();
        }

        public void SignalClose()
        {
            _isClosed = true;
            _readSignal.Release();
            if (_writeAckLatch.CurrentCount == 0) _writeAckLatch.Release();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;

            while (totalRead < count)
            {
                if (_currentChunk.Buffer != null)
                {
                    int available = _currentChunk.Length - _currentChunkOffset;
                    int toCopy = Math.Min(count - totalRead, available);

                    Array.Copy(_currentChunk.Buffer, _currentChunkOffset, buffer, offset + totalRead, toCopy);

                    totalRead += toCopy;
                    _currentChunkOffset += toCopy;

                    if (_currentChunkOffset >= _currentChunk.Length)
                    {
                        SimpleBufferPool.Return(_currentChunk.Buffer);
                        _currentChunk.Buffer = null;
                    }

                    if (totalRead > 0) return totalRead;
                }

                if (_isClosed && _chunkQueue.Count == 0) return 0;

                bool gotChunk = false;
                lock (_queueLock)
                {
                    if (_chunkQueue.Count > 0)
                    {
                        _currentChunk = _chunkQueue.Dequeue();
                        gotChunk = true;
                    }
                }

                if (!gotChunk)
                {
                    try
                    {
                        await _readSignal.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return 0;
                    }

                    lock (_queueLock)
                    {
                        if (_chunkQueue.Count > 0)
                        {
                            _currentChunk = _chunkQueue.Dequeue();
                            _currentChunkOffset = 0;
                        }
                        else if (_isClosed)
                        {
                            return 0;
                        }
                    }
                }
                else
                {
                    if (_readSignal.CurrentCount > 0) await _readSignal.WaitAsync(0);
                    _currentChunkOffset = 0;
                }
            }

            return totalRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_isClosed) return;
            _writeAckLatch.Wait();
            if (_isClosed) return;

            byte[] payload = new byte[count];
            Array.Copy(buffer, offset, payload, 0, count);
            _client.SendPacket(AdbProtocol.A_WRTE, _localId, _remoteId, payload);
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_isClosed)
            {
                _client.SendPacket(AdbProtocol.A_CLSE, _localId, _remoteId, null);
                SignalClose();
            }

            if (_currentChunk.Buffer != null) SimpleBufferPool.Return(_currentChunk.Buffer);

            lock (_queueLock)
            {
                while (_chunkQueue.Count > 0)
                {
                    var c = _chunkQueue.Dequeue();
                    SimpleBufferPool.Return(c.Buffer);
                }
            }
            base.Dispose(disposing);
        }
    }
}