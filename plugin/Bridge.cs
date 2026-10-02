using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace CorticoSisterDrive
{
    /// <summary>
    /// TCP server on 127.0.0.1 speaking UTF-8 JSON lines. One client at a time; a new connection
    /// replaces the old one. Incoming frames are queued for the Unity main thread; Send is
    /// thread-safe and drops frames while no client is connected.
    /// </summary>
    internal sealed class Bridge : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _acceptThread;
        private readonly object _writeLock = new object();
        private TcpClient _client;
        private StreamWriter _writer;
        private volatile bool _disposed;

        public readonly ConcurrentQueue<JObject> Inbox = new ConcurrentQueue<JObject>();

        public Bridge(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            NoInherit(_listener.Server);
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "cortico-sisterdrive-accept" };
            _acceptThread.Start();
        }

        public bool Connected => _writer != null;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        // The game starts a TTS sidecar process that inherits handles; an inherited listening
        // socket keeps the port bound after the game exits and swallows the next connection.
        private static void NoInherit(Socket socket) => SetHandleInformation(socket.Handle, 1 /* HANDLE_FLAG_INHERIT */, 0);

        private void AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (_disposed) return; continue; }
                client.NoDelay = true;
                NoInherit(client.Client);
                lock (_writeLock)
                {
                    try { _client?.Close(); } catch { }
                    _client = client;
                    _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                }
                Inbox.Enqueue(new JObject { ["kind"] = "_connected" });
                var reader = new Thread(() => ReadLoop(client)) { IsBackground = true, Name = "cortico-sisterdrive-read" };
                reader.Start();
            }
        }

        private void ReadLoop(TcpClient client)
        {
            try
            {
                using (var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        try { Inbox.Enqueue(JObject.Parse(line)); }
                        catch (Exception err) { Plugin.Log.LogWarning("[bridge] bad frame: " + err.Message); }
                    }
                }
            }
            catch { }
            lock (_writeLock)
            {
                if (_client == client) { _client = null; _writer = null; }
            }
        }

        public void Send(JObject frame)
        {
            lock (_writeLock)
            {
                if (_writer == null) return;
                try { _writer.WriteLine(frame.ToString(Newtonsoft.Json.Formatting.None)); }
                catch { try { _client?.Close(); } catch { } _client = null; _writer = null; }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _listener.Stop(); } catch { }
            lock (_writeLock) { try { _client?.Close(); } catch { } _client = null; _writer = null; }
        }
    }
}
