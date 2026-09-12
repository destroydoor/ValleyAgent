using System;
using System.Net;
using System.Text;
using System.Threading;
using StardewModdingAPI;

namespace ValleyAgent.Autopilot.HttpServer
{
    public sealed class AutopilotHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly IMonitor _monitor;
        private readonly RouteHandlers _handlers;
        private Thread? _listenerThread;
        private volatile bool _running;

        public AutopilotHttpServer(IMonitor monitor, RouteHandlers handlers, int port = 5555)
        {
            _monitor = monitor;
            _handlers = handlers;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
        }

        public void Start()
        {
            _running = true;
            _listener.Start();
            _listenerThread = new Thread(ListenLoop) { IsBackground = true };
            _listenerThread.Start();
            _monitor.Log("Autopilot HTTP Server started on localhost:5555", LogLevel.Info);
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ProcessRequest(context);
                }
                catch (HttpListenerException) when (!_running)
                {
                }
                catch (ObjectDisposedException) when (!_running)
                {
                }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var method = context.Request.HttpMethod;

            string responseBody;
            int statusCode;

            try
            {
                (responseBody, statusCode) = _handlers.Handle(method, path, context.Request);
            }
            catch (System.Collections.Generic.KeyNotFoundException ex)
            {
                responseBody = $"{{\"error\":\"Missing field: {EscapeJson(ex.Message)}\"}}";
                statusCode = 400;
            }
            catch (InvalidOperationException ex)
            {
                responseBody = $"{{\"error\":\"{EscapeJson(ex.Message)}\"}}";
                statusCode = 500;
            }
            catch (ArgumentException ex)
            {
                responseBody = $"{{\"error\":\"{EscapeJson(ex.Message)}\"}}";
                statusCode = 400;
            }
            catch (System.Text.Json.JsonException ex)
            {
                responseBody = $"{{\"error\":\"Invalid JSON: {EscapeJson(ex.Message)}\"}}";
                statusCode = 400;
            }

            var buffer = Encoding.UTF8.GetBytes(responseBody);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.Close(buffer, false);
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch (HttpListenerException) { }
            try { _listener.Close(); } catch (ObjectDisposedException) { }
        }
    }
}
