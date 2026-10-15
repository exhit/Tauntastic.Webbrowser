using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tauntastic.WebBrowser
{
    /// <summary>
    /// A controlled browser window. <see cref="OpenAsync"/> starts Chrome, Edge, Brave or Chromium with its own persistent profile (or reuses the window of that profile that is
    /// already open) and connects to it through the Chrome DevTools Protocol, so Unity can navigate, wait for the user to sign in, run JavaScript on the page and read the result.
    /// Await the methods from the main thread (an editor window or menu command); the code after the await then runs on the main thread again.
    /// <para>Disposing a session only disconnects: the browser window stays open and keeps its sign-ins. Use <see cref="CloseBrowserAsync"/> to close the window.</para>
    /// </summary>
    public sealed class WebBrowserSession : IDisposable
    {
        private const string _ACTIVE_PORT_FILE = "DevToolsActivePort";
        private const int _DEFAULT_TIMEOUT_MS = 30000;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly List<WebBrowserSession> _open = new List<WebBrowserSession>();

        private readonly CdpConnection _connection;
        private readonly Process _startedProcess;

        /// <summary>The profile this session belongs to.</summary>
        public string ProfileName { get; private set; }

        /// <summary>The local port of the browser's DevTools endpoint (127.0.0.1 only).</summary>
        public int DebugPort { get; private set; }

        /// <summary>True when this call started the browser; false when it found the window of this profile already open.</summary>
        public bool WasStarted { get; private set; }

        /// <summary>True while the connection to the browser is alive (false after the user closed the window).</summary>
        public bool IsOpen { get { return _connection.IsOpen; } }

        /// <summary>Raised on a background thread when the connection ends, usually because the user closed the browser window.</summary>
        public event Action Closed;

        /// <summary>
        /// Raised on a background thread for every DevTools event of the page (name such as "Page.screencastFrame" and its parameters). Used by <see cref="WebBrowserScreen"/>;
        /// handlers must not touch Unity objects.
        /// </summary>
        public event Action<string, Dictionary<string, object>> ProtocolEvent;

        private WebBrowserSession(CdpConnection connection, string profileName, int port, Process startedProcess)
        {
            _connection = connection;
            _startedProcess = startedProcess;
            ProfileName = profileName;
            DebugPort = port;
            WasStarted = startedProcess != null;
            _connection.EventReceived += (name, parameters) =>
            {
                Action<string, Dictionary<string, object>> handler = ProtocolEvent;
                if (handler != null)
                    handler(name, parameters);
            };
            _connection.Closed += () =>
            {
                lock (_open) { _open.Remove(this); }
                Action closed = Closed;
                if (closed != null)
                    closed();
            };
        }

        /// <summary>The folder that holds a profile's browser data (sign-ins, cookies).</summary>
        public static string ProfileDirectory(string profileName)
        {
            var name = new StringBuilder();
            foreach (char c in string.IsNullOrWhiteSpace(profileName) ? "Default" : profileName.Trim())
                name.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Tauntastic", "WebBrowser", name.ToString());
        }

        /// <summary>Starts the browser (or reuses the open window of the profile) and connects to its first page.</summary>
        /// <exception cref="WebBrowserException">No browser was found, it did not start in time, or the DevTools endpoint could not be reached.</exception>
        public static async Task<WebBrowserSession> OpenAsync(WebBrowserOptions options = null, CancellationToken cancellation = default(CancellationToken))
        {
            options = options ?? new WebBrowserOptions();
            string profileDirectory = string.IsNullOrWhiteSpace(options.ProfileDirectory) ? ProfileDirectory(options.ProfileName) : options.ProfileDirectory;
            Directory.CreateDirectory(profileDirectory);

            int port = await ReadLivePortAsync(profileDirectory).ConfigureAwait(false);
            Process started = null;
            if (port == 0)
            {
                Tuple<int, Process> launched = await StartBrowserAsync(options, profileDirectory, cancellation).ConfigureAwait(false);
                port = launched.Item1;
                started = launched.Item2;
            }

            string socketUrl = await FindPageSocketAsync(port).ConfigureAwait(false);
            CdpConnection connection = await CdpConnection.ConnectAsync(socketUrl, 10000).ConfigureAwait(false);
            var session = new WebBrowserSession(connection, options.ProfileName, port, started);
            lock (_open) { _open.Add(session); }

            await connection.SendAsync("Page.enable", "{}", _DEFAULT_TIMEOUT_MS).ConfigureAwait(false);
            return session;
        }

        /// <summary>Navigates to the address and returns when the page has loaded.</summary>
        public async Task NavigateAsync(string url, int timeoutMs = _DEFAULT_TIMEOUT_MS)
        {
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<string, Dictionary<string, object>> handler = (name, parameters) =>
            {
                if (name == "Page.loadEventFired")
                    loaded.TrySetResult(true);
            };

            _connection.EventReceived += handler;
            try
            {
                Dictionary<string, object> result = await _connection.SendAsync("Page.navigate", "{\"url\":" + JsonText.Quote(url) + "}", timeoutMs).ConfigureAwait(false);
                string error = SimpleJson.GetString(result, "errorText");
                if (error.Length > 0)
                    throw new WebBrowserException("The browser could not open " + url + ": " + error);

                // A change of only the #fragment is a navigation inside the same document: it has no loader id and no load event.
                if (!result.ContainsKey("loaderId"))
                    return;

                Task finished = await Task.WhenAny(loaded.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != loaded.Task)
                    throw new TimeoutException("The page " + url + " did not finish loading within " + timeoutMs + " ms.");
            }
            finally
            {
                _connection.EventReceived -= handler;
            }
        }

        /// <summary>
        /// Evaluates a JavaScript expression in the page and returns its value: a string, a double, a bool, null, a List of object or a Dictionary of string to object
        /// (the types of <see cref="SimpleJson"/>). A Promise (for example from an async function call) is awaited. Statements need to be wrapped in a function:
        /// "(() =&gt; { ...; return value; })()".
        /// </summary>
        /// <exception cref="WebBrowserScriptException">The script threw, or the page was changing while it ran.</exception>
        public async Task<object> EvaluateAsync(string expression, int timeoutMs = _DEFAULT_TIMEOUT_MS)
        {
            Dictionary<string, object> result = await _connection.SendAsync(
                "Runtime.evaluate",
                "{\"expression\":" + JsonText.Quote(expression) + ",\"returnByValue\":true,\"awaitPromise\":true,\"userGesture\":true}",
                timeoutMs).ConfigureAwait(false);

            Dictionary<string, object> exception = SimpleJson.GetObject(result, "exceptionDetails");
            if (exception != null)
            {
                string text = SimpleJson.GetString(SimpleJson.GetObject(exception, "exception"), "description");
                throw new WebBrowserScriptException(text.Length > 0 ? text : SimpleJson.GetString(exception, "text"));
            }

            Dictionary<string, object> remote = SimpleJson.GetObject(result, "result");
            object value;
            return remote != null && remote.TryGetValue("value", out value) ? value : null;
        }

        /// <summary>Like <see cref="EvaluateAsync"/> but returns the value as text (null becomes an empty string, other values their JSON text).</summary>
        public async Task<string> EvaluateStringAsync(string expression, int timeoutMs = _DEFAULT_TIMEOUT_MS)
        {
            object value = await EvaluateAsync(expression, timeoutMs).ConfigureAwait(false);
            if (value == null)
                return string.Empty;
            return value is string ? (string)value : JsonText.ToJson(value);
        }

        /// <summary>
        /// Evaluates the condition again and again until it is truthy, the timeout passes or the browser closes. Pages that are loading or navigating (which makes the
        /// evaluation fail for a moment) are simply tried again, so this also waits through a sign-in redirect. Returns true when the condition became true.
        /// </summary>
        /// <exception cref="WebBrowserException">The browser window was closed while waiting.</exception>
        public async Task<bool> WaitForAsync(string conditionExpression, int timeoutMs, int pollIntervalMs = 500, CancellationToken cancellation = default(CancellationToken))
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!IsOpen)
                    throw new WebBrowserException("The browser window was closed.");

                try
                {
                    if (IsTruthy(await EvaluateAsync(conditionExpression, 10000).ConfigureAwait(false)))
                        return true;
                }
                catch (WebBrowserException) { }
                catch (TimeoutException) { }

                if (DateTime.UtcNow >= end)
                    return false;
                await Task.Delay(pollIntervalMs, cancellation).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Downloads a file with the browser, so its sign-in and cookies apply (a page that lists downloads for a signed-in customer, for example). The current page must be on the same
        /// site as <paramref name="url"/>, because the download is started by clicking a link on that page. The file is saved into <paramref name="directory"/> under the name the
        /// server suggests (an existing file of that name is replaced) and its full path is returned.
        /// </summary>
        /// <param name="progress">Called on a background thread with the bytes received and the total bytes (0 when unknown).</param>
        /// <exception cref="WebBrowserException">The download was canceled by the browser or did not produce a file.</exception>
        /// <exception cref="TimeoutException">The download did not finish within <paramref name="timeoutMs"/>.</exception>
        public async Task<string> DownloadAsync(string url, string directory, Action<long, long> progress = null, int timeoutMs = 30 * 60 * 1000, CancellationToken cancellation = default(CancellationToken))
        {
            Directory.CreateDirectory(directory);

            string guid = null;
            string suggestedName = null;
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<string, Dictionary<string, object>> handler = (name, parameters) =>
            {
                if (name == "Browser.downloadWillBegin" && guid == null)
                {
                    suggestedName = SimpleJson.GetString(parameters, "suggestedFilename");
                    guid = SimpleJson.GetString(parameters, "guid");
                }
                else if (name == "Browser.downloadProgress" && guid != null && SimpleJson.GetString(parameters, "guid") == guid)
                {
                    if (progress != null)
                        progress(ReadLong(parameters, "receivedBytes"), ReadLong(parameters, "totalBytes"));

                    string state = SimpleJson.GetString(parameters, "state");
                    if (state == "completed")
                        finished.TrySetResult(true);
                    else if (state == "canceled")
                        finished.TrySetException(new WebBrowserException("The browser canceled the download."));
                }
            };

            ProtocolEvent += handler;
            try
            {
                // "allowAndName" saves the file as <directory>/<guid>; it is renamed below to the suggested name.
                await SendCommandAsync("Browser.setDownloadBehavior",
                    "{\"behavior\":\"allowAndName\",\"downloadPath\":" + JsonText.Quote(directory) + ",\"eventsEnabled\":true}").ConfigureAwait(false);
                await EvaluateAsync("(function(){var a=document.createElement('a');a.href=" + JsonText.Quote(url) +
                                    ";a.download='';a.style.display='none';document.body.appendChild(a);a.click();a.remove();})()").ConfigureAwait(false);

                using (cancellation.Register(() => finished.TrySetCanceled()))
                {
                    Task winner = await Task.WhenAny(finished.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (winner != finished.Task)
                    {
                        CancelDownload(guid);
                        throw new TimeoutException("The download did not finish within " + timeoutMs / 1000 + " seconds.");
                    }

                    try { await finished.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        CancelDownload(guid);
                        throw;
                    }
                }

                string downloaded = Path.Combine(directory, guid ?? string.Empty);
                if (guid == null || !File.Exists(downloaded))
                    throw new WebBrowserException("The browser reported the download as finished but no file was saved.");

                string target = Path.Combine(directory, SafeFileName(suggestedName));
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(downloaded, target);
                return target;
            }
            finally
            {
                ProtocolEvent -= handler;
            }
        }

        private void CancelDownload(string guid)
        {
            if (guid != null && IsOpen)
                _connection.SendAsync("Browser.cancelDownload", "{\"guid\":" + JsonText.Quote(guid) + "}", 5000).ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static long ReadLong(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary.TryGetValue(key, out value) && value is double ? (long)(double)value : 0L;
        }

        private static string SafeFileName(string name)
        {
            var builder = new StringBuilder();
            foreach (char c in string.IsNullOrWhiteSpace(name) ? "download.bin" : name.Trim())
                builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return builder.ToString();
        }

        /// <summary>The address of the page the browser shows now.</summary>
        public Task<string> GetUrlAsync()
        {
            return EvaluateStringAsync("location.href");
        }

        /// <summary>Brings the browser window to the front.</summary>
        public Task BringToFrontAsync()
        {
            return _connection.SendAsync("Page.bringToFront", "{}", _DEFAULT_TIMEOUT_MS);
        }

        /// <summary>Closes the browser window (all of its tabs). The profile keeps its sign-ins for the next <see cref="OpenAsync"/>.</summary>
        public async Task CloseBrowserAsync()
        {
            try { await _connection.SendAsync("Browser.close", "{}", 5000).ConfigureAwait(false); }
            catch (WebBrowserException) { }
            catch (TimeoutException) { }
            Dispose();

            // A browser this session started is also made sure to be gone, so no hidden window keeps running.
            if (_startedProcess != null)
            {
                try
                {
                    if (!_startedProcess.WaitForExit(3000))
                        _startedProcess.Kill();
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        /// <summary>Sends any DevTools Protocol command (for example "Input.dispatchMouseEvent") with the JSON text of its parameters and returns its "result" object.</summary>
        /// <exception cref="WebBrowserException">The browser answered with an error or the connection is closed.</exception>
        public Task<Dictionary<string, object>> SendCommandAsync(string method, string parametersJson = "{}", int timeoutMs = _DEFAULT_TIMEOUT_MS)
        {
            return _connection.SendAsync(method, parametersJson, timeoutMs);
        }

        /// <summary>Disconnects from the browser; the window stays open.</summary>
        public void Dispose()
        {
            lock (_open) { _open.Remove(this); }
            _connection.Dispose();
        }

        /// <summary>Disconnects every open session (done before scripts reload and when the Editor quits). The browser windows stay open.</summary>
        public static void DisconnectAll()
        {
            WebBrowserSession[] sessions;
            lock (_open) { sessions = _open.ToArray(); }
            foreach (WebBrowserSession session in sessions)
                session.Dispose();
        }

        private static bool IsTruthy(object value)
        {
            if (value == null) return false;
            if (value is bool) return (bool)value;
            if (value is double) return (double)value != 0d;
            if (value is string) return ((string)value).Length > 0;
            return true;
        }

        /// <summary>The DevTools port of a browser that is already running with this profile, or 0 (also removes a stale port file left by a crashed browser).</summary>
        private static async Task<int> ReadLivePortAsync(string profileDirectory)
        {
            string file = Path.Combine(profileDirectory, _ACTIVE_PORT_FILE);
            if (!File.Exists(file))
                return 0;

            int port;
            try
            {
                string[] lines = File.ReadAllLines(file);
                if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out port))
                    return 0;
            }
            catch (IOException) { return 0; }

            try
            {
                using (HttpResponseMessage response = await _http.GetAsync("http://127.0.0.1:" + port + "/json/version").ConfigureAwait(false))
                    if (response.IsSuccessStatusCode)
                        return port;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }

            try { File.Delete(file); }
            catch (IOException) { }
            return 0;
        }

        private static async Task<Tuple<int, Process>> StartBrowserAsync(WebBrowserOptions options, string profileDirectory, CancellationToken cancellation)
        {
            string browser = BrowserLocator.Find(options.BrowserPath);
            if (browser == null)
                throw new WebBrowserException("No Chromium based browser was found. Install Google Chrome, Microsoft Edge or Brave, or set WebBrowserOptions.BrowserPath.");

            var arguments = new StringBuilder();
            arguments.Append("--user-data-dir=\"").Append(profileDirectory).Append("\" ")
                     .Append("--remote-debugging-port=0 --remote-debugging-address=127.0.0.1 --no-first-run --no-default-browser-check ");
            if (options.Headless)
                arguments.Append("--headless=new ");
            if (options.MuteAudio)
                arguments.Append("--mute-audio ");
            if (options.WindowWidth > 0 && options.WindowHeight > 0)
                arguments.Append("--window-size=").Append(options.WindowWidth).Append(',').Append(options.WindowHeight).Append(' ');
            if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
                arguments.Append(options.ExtraArguments.Trim()).Append(' ');

            string url = string.IsNullOrWhiteSpace(options.StartUrl) ? "about:blank" : options.StartUrl;
            arguments.Append(options.AppMode ? "--app=\"" + url + "\"" : "\"" + url + "\"");

            Process process = Process.Start(new ProcessStartInfo(browser, arguments.ToString()) { UseShellExecute = false });

            // Chromium writes the port it picked into <profile>/DevToolsActivePort once the DevTools endpoint is up.
            DateTime end = DateTime.UtcNow.AddMilliseconds(Math.Max(options.StartTimeoutMs, 1000));
            while (DateTime.UtcNow < end)
            {
                cancellation.ThrowIfCancellationRequested();
                int port = await ReadLivePortAsync(profileDirectory).ConfigureAwait(false);
                if (port != 0)
                    return Tuple.Create(port, process);
                await Task.Delay(250, cancellation).ConfigureAwait(false);
            }

            throw new WebBrowserException("The browser " + browser + " did not open its DevTools endpoint within " + options.StartTimeoutMs + " ms.");
        }

        /// <summary>The DevTools WebSocket address of the first real page of the browser (a blank tab is opened when there is none).</summary>
        private static async Task<string> FindPageSocketAsync(int port)
        {
            string baseUrl = "http://127.0.0.1:" + port;
            string json;
            try { json = await _http.GetStringAsync(baseUrl + "/json/list").ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException)
            {
                throw new WebBrowserException("The browser's DevTools endpoint on port " + port + " does not answer.", exception);
            }

            object parsed;
            if (SimpleJson.TryParse(json, out parsed) && parsed is List<object>)
            {
                foreach (object item in (List<object>)parsed)
                {
                    var target = item as Dictionary<string, object>;
                    string url = SimpleJson.GetString(target, "webSocketDebuggerUrl");
                    if (target != null && SimpleJson.GetString(target, "type") == "page" && url.Length > 0 && !SimpleJson.GetString(target, "url").StartsWith("devtools://", StringComparison.Ordinal))
                        return url;
                }
            }

            using (var request = new HttpRequestMessage(HttpMethod.Put, baseUrl + "/json/new?about:blank"))
            using (HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(false))
            {
                string created = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (SimpleJson.TryParse(created, out parsed))
                {
                    string url = SimpleJson.GetString(parsed as Dictionary<string, object>, "webSocketDebuggerUrl");
                    if (url.Length > 0)
                        return url;
                }
            }

            throw new WebBrowserException("The browser has no page that can be controlled.");
        }
    }
}
