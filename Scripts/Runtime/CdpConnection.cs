using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tauntastic.WebBrowser
{
    /// <summary>
    /// One Chrome DevTools Protocol connection: JSON commands with numbered ids go out over a WebSocket, answers are matched by id and everything else
    /// (events such as Page.loadEventFired) is raised through <see cref="EventReceived"/>. Does not touch any Unity API and may be used from any thread.
    /// </summary>
    internal sealed class CdpConnection : IDisposable
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> _pending = new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private int _nextId;

        /// <summary>Raised on a background thread with the event name (for example "Page.loadEventFired") and its parameters.</summary>
        public event Action<string, Dictionary<string, object>> EventReceived;

        /// <summary>Raised once when the connection ends, because the browser closed or the connection was disposed.</summary>
        public event Action Closed;

        public bool IsOpen { get { return _socket.State == WebSocketState.Open; } }

        public static async Task<CdpConnection> ConnectAsync(string webSocketUrl, int timeoutMs)
        {
            var connection = new CdpConnection();
            try
            {
                using (var timeout = new CancellationTokenSource(timeoutMs))
                    await connection._socket.ConnectAsync(new Uri(webSocketUrl), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                connection.Dispose();
                throw new WebBrowserException("Could not connect to the browser's DevTools endpoint " + webSocketUrl + ": " + exception.Message, exception);
            }

            var receiving = Task.Run(connection.ReceiveLoopAsync); // runs until the connection ends; failures are handled inside the loop
            return connection;
        }

        /// <summary>Sends a command and returns its "result" object. <paramref name="parametersJson"/> is the JSON text of the parameters object.</summary>
        public async Task<Dictionary<string, object>> SendAsync(string method, string parametersJson, int timeoutMs)
        {
            if (!IsOpen)
                throw new WebBrowserException("The browser connection is closed.");

            int id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes("{\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + (parametersJson ?? "{}") + "}");
                await _sendLock.WaitAsync(_cancel.Token).ConfigureAwait(false);
                try { await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancel.Token).ConfigureAwait(false); }
                finally { _sendLock.Release(); }

                using (var timeout = new CancellationTokenSource(timeoutMs))
                using (timeout.Token.Register(() => completion.TrySetException(new TimeoutException("The browser did not answer \"" + method + "\" within " + timeoutMs + " ms."))))
                    return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                TaskCompletionSource<Dictionary<string, object>> removed;
                _pending.TryRemove(id, out removed);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (_socket.State == WebSocketState.Open && !_cancel.IsCancellationRequested)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancel.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return;
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    Dispatch(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
            }
            catch (Exception)
            {
                // The browser closed the connection or it was disposed: both end the loop.
            }
            finally
            {
                FailPending();
                Action closed = Closed;
                if (closed != null)
                    closed();
            }
        }

        private void Dispatch(string json)
        {
            object parsed;
            var root = SimpleJson.TryParse(json, out parsed) ? parsed as Dictionary<string, object> : null;
            if (root == null)
                return;

            object idValue;
            if (root.TryGetValue("id", out idValue) && idValue is double)
            {
                TaskCompletionSource<Dictionary<string, object>> completion;
                if (!_pending.TryGetValue((int)(double)idValue, out completion))
                    return;

                Dictionary<string, object> error = SimpleJson.GetObject(root, "error");
                if (error != null)
                    completion.TrySetException(new WebBrowserException("The browser reported an error: " + SimpleJson.GetString(error, "message")));
                else
                    completion.TrySetResult(SimpleJson.GetObject(root, "result") ?? new Dictionary<string, object>());
                return;
            }

            string method = SimpleJson.GetString(root, "method");
            if (method.Length == 0)
                return;

            Action<string, Dictionary<string, object>> handler = EventReceived;
            if (handler != null)
                handler(method, SimpleJson.GetObject(root, "params") ?? new Dictionary<string, object>());
        }

        private void FailPending()
        {
            foreach (KeyValuePair<int, TaskCompletionSource<Dictionary<string, object>>> pair in _pending)
                pair.Value.TrySetException(new WebBrowserException("The browser connection was closed."));
        }

        public void Dispose()
        {
            _cancel.Cancel();
            try { _socket.Dispose(); }
            catch (Exception) { }
        }
    }
}
