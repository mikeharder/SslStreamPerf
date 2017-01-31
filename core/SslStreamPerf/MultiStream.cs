using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SslStreamPerf
{
    public class MultiStream : Stream
    {
        private readonly Stream[] _streams;
        private readonly Task[] _writeTasks;
        private readonly Task<int>[] _readTasks;

        private readonly int _blockLength;
        private long _position;

        public MultiStream(IEnumerable<Stream> streams, int blockLength = 16 * 1024)
        {
            _streams = streams.ToArray();
            _blockLength = blockLength;

            _writeTasks = new Task[_streams.Length];
            for (var i=0; i < _writeTasks.Length; i++)
            {
                _writeTasks[i] = Task.FromResult(0);
            }

            _readTasks = new Task<int>[_streams.Length];
        }

        public override bool CanRead
        {
            get
            {
                return true;
            }
        }

        public override bool CanSeek
        {
            get
            {
                return false;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return true;
            }
        }

        public override long Length
        {
            get
            {
                return _streams.Sum(s => s.Length);
            }
        }

        public override long Position
        {
            get
            {
                return _position;
            }

            set
            {
                throw new NotImplementedException();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Flush();
                foreach (var stream in _streams)
                {
                    stream.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        public override void Flush()
        {
            FlushAsync(CancellationToken.None).Wait();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await Task.WhenAll(_writeTasks);

            foreach (var stream in _streams)
            {
                await stream.FlushAsync(cancellationToken);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).Result;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var totalBytesRead = 0;

            var queuedPosition = _position;
            var totalBytesQueued = 0;
            while (totalBytesQueued < count)
            {
                var current = (queuedPosition / _blockLength) % _streams.Length;
                var currentStream = _streams[current];
                var remainingBytesInCurrentStream = _blockLength - (int)(queuedPosition % _blockLength);

                var remainingBytes = Math.Min(count - totalBytesQueued, remainingBytesInCurrentStream);

                var previousReadTask = _readTasks[current];
                if (previousReadTask != null)
                {
                    // Need to await previous read before another can be queued
                    var bytesRead = await previousReadTask;
                    totalBytesRead += bytesRead;
                    _position += bytesRead;               
                }

                // Queue the current read rather than awaiting, so multiple reads can execute in parallel
                _readTasks[current] = ReadAllAsync(currentStream, buffer, offset + totalBytesQueued, remainingBytes, cancellationToken);

                totalBytesQueued += remainingBytes;
                queuedPosition += remainingBytes;
            }

            for (var i=0; i < _readTasks.Length; i++)
            {
                var readTask = _readTasks[i];
                if (readTask != null)
                {
                    var bytesRead = await readTask;
                    totalBytesRead += bytesRead;
                    _position += bytesRead;
                    _readTasks[i] = null;
                }
            }

            return totalBytesRead;
        }

        private async Task<int> ReadAllAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var totalBytesRead = 0;
            var remainingBytes = count;

            while (totalBytesRead < count)
            {
                var bytesRead = await stream.ReadAsync(buffer, offset + totalBytesRead, remainingBytes);
                if (bytesRead == 0)
                {
                    break;
                }

                totalBytesRead += bytesRead;
                remainingBytes -= bytesRead;
            }

            return totalBytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).Wait();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesWritten = 0;
            while (bytesWritten < count)
            {
                var current = (_position / _blockLength) % _streams.Length;
                var currentStream = _streams[current];
                var remainingBytesInCurrentStream = _blockLength - (int)(_position % _blockLength);

                var remainingBytes = Math.Min(count - bytesWritten, remainingBytesInCurrentStream);

                // Need to await the previous write before another can be queued
                await _writeTasks[current];

                // Queue the current write rather than awaiting, so multiple writes can execute in parallel
                _writeTasks[current] = currentStream.WriteAsync(buffer, offset + bytesWritten, remainingBytes, cancellationToken);

                bytesWritten += remainingBytes;
                _position += remainingBytes;
            }
        }
    }
}
