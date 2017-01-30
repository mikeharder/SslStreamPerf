using CommandLine;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace SslStreamPerf
{
    public class Program
    {
        private class CommonOptions
        {
            [Option('p', "port", Default = 8080)]
            public int Port { get; set; }
        }

        [Verb("server")]
        private class ServerOptions : CommonOptions
        {
            [Option('b', "bytes", HelpText = "Number of bytes to send", Default = 100 * 1024 * 1024)]
            public long Bytes { get; set; }
        }

        [Verb("client")]
        private class ClientOptions : CommonOptions
        {
            [Option('b', "bufferLength", Default = 1024 * 1024)]
            public int BufferLength { get; set; }

            [Option('h', "host", Required = true)]
            public string Host { get; set; }
        }

        public static int Main(string[] args)
        {
            return Parser.Default.ParseArguments<ServerOptions, ClientOptions>(args).MapResult(
                (ServerOptions options) => RunServerAsync(options).Result,
                (ClientOptions options) => RunClientAsync(options).Result,
                errs => 1
                );
        }

        private static async Task<int> RunServerAsync(ServerOptions options)
        {
            var data = new ZeroStream(options.Bytes);

            var cert = new X509Certificate2("testCert.pfx", "testPassword");

            var listener = new TcpListener(IPAddress.Any, options.Port);
            listener.Start();

            Console.WriteLine($"Bytes: {string.Format("{0:n0}", options.Bytes)}");
            Console.WriteLine($"Listening on port {options.Port}...");

            while (true)
            {
                using (var client = await listener.AcceptTcpClientAsync())
                using (var stream = new SslStream(client.GetStream()))
                {
                    await stream.AuthenticateAsServerAsync(cert);

                    Console.WriteLine();
                    Console.WriteLine($"Sending {string.Format("{0:n0}", data.Length)} bytes...");

                    var sw = Stopwatch.StartNew();
                    await data.CopyToAsync(stream);
                    sw.Stop();

                    var mbps = ((data.Length * 8) / (1024 * 1024)) / sw.Elapsed.TotalSeconds;
                    Console.WriteLine($"Sent {string.Format("{0:n0}", data.Length)} bytes in {Math.Round(sw.Elapsed.TotalSeconds, 3)} seconds ({mbps} Mbps)");
                }
            }
        }

        private static async Task<int> RunClientAsync(ClientOptions options)
        {
            RemoteCertificateValidationCallback rcvc = (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) => true;

            using (var client = new TcpClient())
            {
                Console.WriteLine($"Connecting to {options.Host}:{options.Port}...");

                await client.ConnectAsync(options.Host, options.Port);
                using (var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, userCertificateValidationCallback: rcvc))
                {
                    await stream.AuthenticateAsClientAsync(options.Host);

                    var buffer = new byte[options.BufferLength];
                    var bytesRead = -1;
                    var totalBytesRead = (long)0;

                    Console.WriteLine("Reading...");

                    var sw = Stopwatch.StartNew();
                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        totalBytesRead += bytesRead;
                    }
                    sw.Stop();

                    var mbps = ((totalBytesRead * 8) / (1024 * 1024)) / sw.Elapsed.TotalSeconds;
                    Console.WriteLine($"Read {string.Format("{0:n0}", totalBytesRead)} bytes in " +
                        $"{Math.Round(sw.Elapsed.TotalSeconds, 3)} seconds ({Math.Round(mbps, 1)} Mbps)");
                }
            }

            return 0;
        }

        private class ZeroStream : Stream
        {
            private long _length;
            private long _position;

            public ZeroStream(long length)
            {
                _length = length;
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
                    return false;
                }
            }

            public override long Length
            {
                get
                {
                    return _length;
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

            public override void Flush()
            {
                throw new NotImplementedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var bytesRemaining = _length - _position;
                var bytesRead = (int)Math.Min(bytesRemaining, count);

                Array.Clear(buffer, offset, bytesRead);
                _position += bytesRead;

                return bytesRead;
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
                throw new NotImplementedException();
            }
        }
    }
}
