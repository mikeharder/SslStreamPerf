using CommandLine;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace SslStreamPerf
{
    public class Program
    {
        private class CommonOptions
        {
            [Option('b', "bufferLength", Default = 1024 * 1024)]
            public int BufferLength { get; set; }

            [Option('c', "connections", Default = 1)]
            public int Connections { get; set; }

            [Option('u', "multiStream")]
            public bool MultiStream { get; set; }

            [Option('p', "port", Default = 8080)]
            public int Port { get; set; }

            [Option('s', "sync")]
            public bool Sync { get; set; }
        }

        [Verb("server")]
        private class ServerOptions : CommonOptions
        {
            [Option('m', "megabytes", HelpText = "Number of megabytes to send", Default = 1024)]
            public long Megabytes { get; set; }
        }

        [Verb("client")]
        private class ClientOptions : CommonOptions
        {
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
            var certPath = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "testCert.pfx");
            Console.WriteLine(certPath);
            var cert = new X509Certificate2(certPath, "testPassword");

            var listener = new TcpListener(IPAddress.Any, options.Port);
            listener.Start();

            Console.WriteLine($"BufferLength: {string.Format("{0:n0}", options.BufferLength)}");
            Console.WriteLine($"Megabytes: {string.Format("{0:n0}", options.Megabytes)}");
            Console.WriteLine($"MultiStream: {options.MultiStream}");
            Console.WriteLine($"Sync: {options.Sync}");
            Console.WriteLine();

            Console.WriteLine($"Listening on port {options.Port}...");

            if (options.MultiStream)
            {
                while (true)
                {
                    var clients = new TcpClient[options.Connections];
                    for (var i = 0; i < options.Connections; i++)
                    {
                        clients[i] = await listener.AcceptTcpClientAsync();
                    }
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    SendDataAsync(options, cert, clients);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
            }
            else
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    SendDataAsync(options, cert, client);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
            }
        }

        private static async Task SendDataAsync(ServerOptions options, X509Certificate2 cert, TcpClient client)
        {
            using (client)
            using (var stream = new SslStream(client.GetStream()))
            {
                await stream.AuthenticateAsServerAsync(cert);

                var data = new ZeroStream(options.Megabytes * 1024 * 1024);

                Console.WriteLine();
                Console.WriteLine($"Sending {string.Format("{0:n0}", data.Length)} bytes...");

                var sw = Stopwatch.StartNew();
                if (options.Sync)
                {
                    data.CopyTo(stream, options.BufferLength);
                }
                else
                {
                    await data.CopyToAsync(stream, options.BufferLength);
                }
                sw.Stop();

                var mbps = ((data.Length * 8) / (1024 * 1024)) / sw.Elapsed.TotalSeconds;
                Console.WriteLine($"Sent {string.Format("{0:n0}", data.Length)} bytes in {Math.Round(sw.Elapsed.TotalSeconds, 3)} seconds ({mbps} Mbps)");
            }
        }

        private static async Task SendDataAsync(ServerOptions options, X509Certificate2 cert, TcpClient[] clients)
        {
            var data = new ZeroStream(options.Megabytes * 1024 * 1024 * options.Connections);

            try
            {
                var streams = new Stream[clients.Length];
                for (var i=0; i < streams.Length; i++)
                {
                    var stream = new SslStream(clients[i].GetStream());
                    await stream.AuthenticateAsServerAsync(cert);
                    streams[i] = stream;
                }

                using (var multiStream = new MultiStream(streams))
                {
                    Console.WriteLine();
                    Console.WriteLine($"Sending {string.Format("{0:n0}", data.Length)} bytes...");

                    var sw = Stopwatch.StartNew();
                    if (options.Sync)
                    {
                        data.CopyTo(multiStream, options.BufferLength);
                    }
                    else
                    {
                        await data.CopyToAsync(multiStream, options.BufferLength);
                    }
                    sw.Stop();

                    var mbps = ((data.Length * 8) / (1024 * 1024)) / sw.Elapsed.TotalSeconds;
                    Console.WriteLine($"Sent {string.Format("{0:n0}", data.Length)} bytes in {Math.Round(sw.Elapsed.TotalSeconds, 3)} seconds ({mbps} Mbps)");
                }
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }
        }

        private static async Task<int> RunClientAsync(ClientOptions options)
        {
            Console.WriteLine($"BufferLength: {string.Format("{0:n0}", options.BufferLength)}");
            Console.WriteLine($"MultiStream: {options.MultiStream}");
            Console.WriteLine($"Sync: {options.Sync}");
            Console.WriteLine();

            Console.WriteLine($"Connecting to {options.Host}:{options.Port}...");

            RemoteCertificateValidationCallback rcvc = (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) => true;

            var totalBytesRead = (long)0;
            var sw = Stopwatch.StartNew();
            if (options.MultiStream)
            {
                var clients = new TcpClient[options.Connections];
                for (var i=0; i < clients.Length; i++)
                {
                    var client = new TcpClient();
                    await client.ConnectAsync(options.Host, options.Port);
                    clients[i] = client;
                }

                await RunClientAsync(options, rcvc, clients);
            }
            else
            {
                var tasks = new Task<long>[options.Connections];
                for (var i = 0; i < options.Connections; i++)
                {
                    tasks[i] = RunClientAsync(options, rcvc);
                }
                await Task.WhenAll(tasks);
                totalBytesRead = tasks.Select(t => t.Result).Sum();

            }

            sw.Stop();
            var mbps = ((totalBytesRead * 8) / (1024 * 1024)) / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"Read {string.Format("{0:n0}", totalBytesRead)} bytes in " +
                $"{Math.Round(sw.Elapsed.TotalSeconds, 3)} seconds ({Math.Round(mbps, 1)} Mbps)");

            return 0;
        }

        private static async Task<long> RunClientAsync(ClientOptions options, RemoteCertificateValidationCallback rcvc)
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(options.Host, options.Port);
                using (var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, userCertificateValidationCallback: rcvc))
                {
                    await stream.AuthenticateAsClientAsync(options.Host);

                    var buffer = new byte[options.BufferLength];
                    var bytesRead = -1;
                    var totalBytesRead = (long)0;

                    if (options.Sync)
                    {
                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            totalBytesRead += bytesRead;
                        }
                    }
                    else
                    {
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            totalBytesRead += bytesRead;
                        }
                    }

                    return totalBytesRead;
                }
            }
        }

        private static async Task<long> RunClientAsync(ClientOptions options, RemoteCertificateValidationCallback rcvc, TcpClient[] clients)
        {
            try
            {
                var streams = new Stream[clients.Length];
                for (var i=0; i < streams.Length; i++)
                {
                    var stream = new SslStream(clients[i].GetStream(), leaveInnerStreamOpen: false, userCertificateValidationCallback: rcvc);
                    await stream.AuthenticateAsClientAsync(options.Host);
                    streams[i] = stream;
                }

                using (var multiStream = new MultiStream(streams))
                {
                    var buffer = new byte[options.BufferLength];
                    var bytesRead = -1;
                    var totalBytesRead = (long)0;

                    if (options.Sync)
                    {
                        while ((bytesRead = multiStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            totalBytesRead += bytesRead;
                        }
                    }
                    else
                    {
                        while ((bytesRead = await multiStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            totalBytesRead += bytesRead;
                        }
                    }

                    return totalBytesRead;
                }
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }
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
