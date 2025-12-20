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

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_currentChunk.Buffer != null)
            {
                int available = _currentChunk.Length - _currentChunkOffset;
                if (available > 0)
                {
                    int toCopy = Math.Min(count, available);
                    Buffer.BlockCopy(_currentChunk.Buffer, _currentChunkOffset, buffer, offset, toCopy);
                    _currentChunkOffset += toCopy;
                    if (_currentChunkOffset >= _currentChunk.Length)
                    {
                        SimpleBufferPool.Return(_currentChunk.Buffer);
                        _currentChunk.Buffer = null;
                    }
                    return Task.FromResult(toCopy);
                }
            }
            return ReadAsyncInternal(buffer, offset, count, cancellationToken);
        }

        private async Task<int> ReadAsyncInternal(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_currentChunk.Buffer == null)
            {
                try
                {
                    await _readSignal.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }

            int totalCopied = 0;
            while (count > 0)
            {
                if (_currentChunk.Buffer == null)
                {
                    lock (_queueLock)
                    {
                        if (_chunkQueue.Count > 0)
                        {
                            _currentChunk = _chunkQueue.Dequeue();
                            _currentChunkOffset = 0;
                        }
                        else
                        {
                            if (_isClosed) return totalCopied;
                            if (totalCopied > 0) return totalCopied;
                            break;
                        }
                    }
                }

                int available = _currentChunk.Length - _currentChunkOffset;
                int toCopy = Math.Min(count, available);
                Buffer.BlockCopy(_currentChunk.Buffer, _currentChunkOffset, buffer, offset, toCopy);
                _currentChunkOffset += toCopy;
                offset += toCopy;
                count -= toCopy;
                totalCopied += toCopy;

                if (_currentChunkOffset >= _currentChunk.Length)
                {
                    SimpleBufferPool.Return(_currentChunk.Buffer);
                    _currentChunk.Buffer = null;
                }

                return totalCopied;
            }
            return totalCopied;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_isClosed) return;

            await _writeAckLatch.WaitAsync(cancellationToken);

            if (_isClosed) return;

            byte[] payload = new byte[count];
            Buffer.BlockCopy(buffer, offset, payload, 0, count);
            _client.SendPacket(AdbProtocol.A_WRTE, _localId, _remoteId, payload);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).Wait();
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
            if (_currentChunk.Buffer != null)
            {
                SimpleBufferPool.Return(_currentChunk.Buffer);
                _currentChunk.Buffer = null;
            }
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