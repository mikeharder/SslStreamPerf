using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SslStreamPerf
{
    public class MultiStream : Stream
    {
        private readonly Stream[] _streams;

        private readonly int _blockLength;
        private long _position;

        public MultiStream(IEnumerable<Stream> streams, int blockLength = 16 * 1024)
        {
            _streams = streams.ToArray();
            _blockLength = blockLength;
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
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
        }

        public override void Flush()
        {
            foreach (var stream in _streams)
            {
                stream.Flush();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalBytesRead = 0;
            while (totalBytesRead < count)
            {
                var currentStream = _streams[(_position / _blockLength) % _streams.Length];
                var remainingBytesInCurrentStream = _blockLength - (int)(_position % _blockLength);

                var remainingBytes = Math.Min(count - totalBytesRead, remainingBytesInCurrentStream);

                var bytesRead = currentStream.Read(buffer, offset + totalBytesRead, remainingBytes);

                totalBytesRead += bytesRead;
                _position += bytesRead;
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
            var bytesWritten = 0;
            while (bytesWritten < count)
            {
                var currentStream = _streams[(_position / _blockLength) % _streams.Length];
                var remainingBytesInCurrentStream = _blockLength - (int)(_position % _blockLength);

                var remainingBytes = Math.Min(count - bytesWritten, remainingBytesInCurrentStream);

                currentStream.Write(buffer, offset + bytesWritten, remainingBytes);

                bytesWritten += remainingBytes;
                _position += remainingBytes;
            }
        }
    }
}
