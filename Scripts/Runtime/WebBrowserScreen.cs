using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Tauntastic.WebBrowser
{
    /// <summary>
    /// A web page shown as a texture, in the Editor or in a game. A hidden (headless) Chromium browser loads the page, streams JPEG frames of it over the DevTools
    /// protocol into <see cref="Texture"/>, and the mouse and keyboard input you forward is replayed on the page. It is not a Unity component: wrap it in
    /// <see cref="WebBrowserView"/> for scenes or in an EditorWindow. Call <see cref="Update"/> every frame from the main thread; that is where new frames and
    /// messages from the page are applied.
    /// <para>Pages can talk to Unity with <c>unity.send("text")</c> (raises <see cref="MessageReceived"/>), and Unity talks to pages with <see cref="EvaluateAsync"/>.</para>
    /// </summary>
    public sealed class WebBrowserScreen : IDisposable
    {
        private const string _BRIDGE_BINDING = "__unityBridge";
        private const string _BRIDGE_SCRIPT = "window.unity={send:function(t){window.__unityBridge(String(t));}};";
        private const int _MIN_SIZE = 64;
        private const int _MAX_SIZE = 4096;
        private const int _STATE_OPEN = 0, _STATE_CLOSED_BY_BROWSER = 1, _STATE_CLOSED_REPORTED = 2;

        private static readonly List<WebBrowserScreen> _all = new List<WebBrowserScreen>();

        private readonly ConcurrentQueue<string> _messages = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _urlChanges = new ConcurrentQueue<string>();
        private readonly Dictionary<KeyCode, char> _heldCharacters = new Dictionary<KeyCode, char>();
        private WebBrowserSession _session;
        private byte[] _latestFrame;
        private string _temporaryProfile;
        private bool _closeBrowserOnDispose;
        private int _state;
        private int _buttons;
        private KeyCode _pendingKey;
        private bool _frameApplied;

        public WebBrowserScreen(int width = 1024, int height = 768)
        {
            Width = Mathf.Clamp(width, _MIN_SIZE, _MAX_SIZE);
            Height = Mathf.Clamp(height, _MIN_SIZE, _MAX_SIZE);
            Texture = new Texture2D(Width, Height, TextureFormat.RGB24, false) { name = "WebBrowserScreen" };
            Url = string.Empty;
            JpegQuality = 75;
        }

        /// <summary>The page as a texture; its size follows the viewport after the first frame.</summary>
        public Texture2D Texture { get; private set; }

        /// <summary>Viewport size in pixels (CSS pixels of the page).</summary>
        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>JPEG quality of the streamed frames, 1 to 100 (default 75). Takes effect the next time streaming starts or the viewport is resized.</summary>
        public int JpegQuality { get; set; }

        /// <summary>The address of the page now, updated when the page navigates.</summary>
        public string Url { get; private set; }

        /// <summary>How many frames were applied to <see cref="Texture"/> so far.</summary>
        public int FrameCount { get; private set; }

        /// <summary>True while the browser is connected.</summary>
        public bool IsOpen { get { return _session != null && _session.IsOpen; } }

        /// <summary>Whether keyboard input is forwarded to the page. Set it when the screen is clicked; <see cref="HandleGuiEvent"/> does that itself.</summary>
        public bool HasFocus { get; set; }

        /// <summary>The underlying browser session, for commands beyond this class.</summary>
        public WebBrowserSession Session { get { return _session; } }

        /// <summary>Raised from <see cref="Update"/> with the text a page sent with <c>unity.send("text")</c>.</summary>
        public event Action<string> MessageReceived;

        /// <summary>Raised from <see cref="Update"/> when the main frame navigated to another address.</summary>
        public event Action<string> UrlChanged;

        /// <summary>Raised from <see cref="Update"/> once when the browser window was closed or crashed.</summary>
        public event Action Closed;

        /// <summary>
        /// Starts a hidden browser (or reuses the running browser of the same profile) and begins streaming. With <paramref name="temporaryProfile"/> the browser gets a fresh
        /// profile folder that is deleted when the screen is disposed, so nothing (sign-ins, cookies) is kept; otherwise the profile of <paramref name="options"/> is used.
        /// </summary>
        public async Task OpenAsync(string startUrl = null, WebBrowserOptions options = null, bool temporaryProfile = false, CancellationToken cancellation = default(CancellationToken))
        {
            options = options ?? new WebBrowserOptions { Headless = true };
            if (temporaryProfile && string.IsNullOrWhiteSpace(options.ProfileDirectory))
            {
                _temporaryProfile = Path.Combine(Path.GetTempPath(), "TauntasticWebBrowser", Guid.NewGuid().ToString("N"));
                options.ProfileDirectory = _temporaryProfile;
            }

            // The browser starts on a blank page so that the page bridge is in place before the start page runs its own scripts.
            options.StartUrl = "about:blank";
            _closeBrowserOnDispose = options.Headless || temporaryProfile;

            _session = await WebBrowserSession.OpenAsync(options, cancellation).ConfigureAwait(false);
            _session.ProtocolEvent += OnProtocolEvent;
            _session.Closed += () => Interlocked.CompareExchange(ref _state, _STATE_CLOSED_BY_BROWSER, _STATE_OPEN);
            lock (_all) { _all.Add(this); }

            await _session.SendCommandAsync("Runtime.enable").ConfigureAwait(false);
            await _session.SendCommandAsync("Runtime.addBinding", "{\"name\":\"" + _BRIDGE_BINDING + "\"}").ConfigureAwait(false);
            await _session.SendCommandAsync("Page.addScriptToEvaluateOnNewDocument", "{\"source\":" + JsonText.Quote(_BRIDGE_SCRIPT) + "}").ConfigureAwait(false);
            await _session.EvaluateAsync(_BRIDGE_SCRIPT).ConfigureAwait(false);
            await StartStreamingAsync().ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(startUrl) && startUrl != "about:blank")
                await _session.NavigateAsync(startUrl).ConfigureAwait(false);
        }

        /// <summary>Opens an address and returns when the page has loaded.</summary>
        public Task NavigateAsync(string url, int timeoutMs = 30000)
        {
            return RequireSession().NavigateAsync(url, timeoutMs);
        }

        /// <summary>
        /// Shows HTML text that your game made, for example the page of an in-game website. The page can use <c>unity.send</c> like any other. Links and relative addresses
        /// have no base address, so use absolute addresses for anything that should load from the network.
        /// </summary>
        public async Task LoadHtmlAsync(string html)
        {
            WebBrowserSession session = RequireSession();
            await session.NavigateAsync("about:blank").ConfigureAwait(false);
            Dictionary<string, object> tree = await session.SendCommandAsync("Page.getFrameTree").ConfigureAwait(false);
            string frameId = SimpleJson.GetString(SimpleJson.GetObject(SimpleJson.GetObject(tree, "frameTree"), "frame"), "id");
            await session.SendCommandAsync("Page.setDocumentContent", "{\"frameId\":" + JsonText.Quote(frameId) + ",\"html\":" + JsonText.Quote(html ?? string.Empty) + "}").ConfigureAwait(false);
        }

        /// <summary>Runs JavaScript in the page and returns its value; see <see cref="WebBrowserSession.EvaluateAsync"/>.</summary>
        public Task<object> EvaluateAsync(string expression, int timeoutMs = 30000)
        {
            return RequireSession().EvaluateAsync(expression, timeoutMs);
        }

        /// <summary>Delivers text to the page: it receives a window event "unity-message" whose detail is the text.</summary>
        public Task SendToPageAsync(string text)
        {
            return EvaluateAsync("window.dispatchEvent(new CustomEvent('unity-message',{detail:" + JsonText.Quote(text ?? string.Empty) + "}))");
        }

        public Task GoBackAsync() { return EvaluateAsync("history.back()"); }

        public Task GoForwardAsync() { return EvaluateAsync("history.forward()"); }

        public Task ReloadAsync() { return EvaluateAsync("location.reload()"); }

        /// <summary>Changes the viewport size (and with it the page layout) and restarts the stream.</summary>
        public void Resize(int width, int height)
        {
            width = Mathf.Clamp(width, _MIN_SIZE, _MAX_SIZE);
            height = Mathf.Clamp(height, _MIN_SIZE, _MAX_SIZE);
            if (width == Width && height == Height)
                return;

            Width = width;
            Height = height;
            if (IsOpen)
                RunInBackground(async () =>
                {
                    await _session.SendCommandAsync("Page.stopScreencast").ConfigureAwait(false);
                    await StartStreamingAsync().ConfigureAwait(false);
                });
        }

        /// <summary>Applies the newest frame to <see cref="Texture"/> and raises the queued events. Call it every frame on the main thread.</summary>
        public void Update()
        {
            byte[] frame = Interlocked.Exchange(ref _latestFrame, null);
            if (frame != null && Texture != null && Texture.LoadImage(frame, false))
            {
                FrameCount++;
                _frameApplied = true;
            }

            string text;
            while (_messages.TryDequeue(out text))
            {
                Action<string> handler = MessageReceived;
                if (handler != null)
                    handler(text);
            }

            while (_urlChanges.TryDequeue(out text))
            {
                Url = text;
                Action<string> handler = UrlChanged;
                if (handler != null)
                    handler(text);
            }

            if (Interlocked.CompareExchange(ref _state, _STATE_CLOSED_REPORTED, _STATE_CLOSED_BY_BROWSER) == _STATE_CLOSED_BY_BROWSER)
            {
                Action closed = Closed;
                if (closed != null)
                    closed();
            }
        }

        /// <summary>True once after a new frame was applied by <see cref="Update"/>; lets an editor window repaint only when needed.</summary>
        public bool ConsumeFrameFlag()
        {
            bool applied = _frameApplied;
            _frameApplied = false;
            return applied;
        }

        // ---------------------------------------------------------------- input

        /// <summary>Mouse moved to a position in page pixels, measured from the top left.</summary>
        public void MouseMove(float x, float y)
        {
            Send("Input.dispatchMouseEvent", MouseJson("mouseMoved", x, y, ButtonName(-1), 0));
        }

        /// <summary>Mouse button pressed (0 left, 1 right, 2 middle).</summary>
        public void MouseDown(float x, float y, int button = 0, int clickCount = 1)
        {
            _buttons |= ButtonMask(button);
            Send("Input.dispatchMouseEvent", MouseJson("mousePressed", x, y, ButtonName(button), Mathf.Max(1, clickCount)));
        }

        /// <summary>Mouse button released.</summary>
        public void MouseUp(float x, float y, int button = 0, int clickCount = 1)
        {
            _buttons &= ~ButtonMask(button);
            Send("Input.dispatchMouseEvent", MouseJson("mouseReleased", x, y, ButtonName(button), Mathf.Max(1, clickCount)));
        }

        /// <summary>Mouse wheel turned; positive <paramref name="deltaY"/> scrolls the page down.</summary>
        public void Scroll(float x, float y, float deltaX, float deltaY)
        {
            Send("Input.dispatchMouseEvent", "{\"type\":\"mouseWheel\",\"x\":" + F(x) + ",\"y\":" + F(y) + ",\"deltaX\":" + F(deltaX) + ",\"deltaY\":" + F(deltaY) + "}");
        }

        /// <summary>Key pressed. Pass a printable <paramref name="character"/> for text, or a <paramref name="keyCode"/> such as Backspace or LeftArrow for other keys.</summary>
        /// <param name="modifiers">Alt = 1, Ctrl = 2, Meta = 4, Shift = 8.</param>
        public void KeyDown(KeyCode keyCode, char character, int modifiers = 0)
        {
            string json = KeyJson(keyCode, character, modifiers, true);
            if (json != null)
                Send("Input.dispatchKeyEvent", json);
        }

        /// <summary>Key released.</summary>
        public void KeyUp(KeyCode keyCode, char character, int modifiers = 0)
        {
            string json = KeyJson(keyCode, character, modifiers, false);
            if (json != null)
                Send("Input.dispatchKeyEvent", json);
        }

        /// <summary>
        /// Forwards an IMGUI event (from OnGUI or an EditorWindow) to the page, with the screen drawn in <paramref name="rect"/> (GUI coordinates). Mouse events inside the rect
        /// and keyboard events while <see cref="HasFocus"/> are used; the return value tells whether the event was used so the caller can call Event.Use().
        /// </summary>
        public bool HandleGuiEvent(Event e, Rect rect)
        {
            if (e == null || rect.width <= 0f || rect.height <= 0f)
                return false;

            bool inside = rect.Contains(e.mousePosition);
            float x = (e.mousePosition.x - rect.x) * Width / rect.width;
            float y = (e.mousePosition.y - rect.y) * Height / rect.height;

            switch (e.type)
            {
                case EventType.MouseDown:
                    HasFocus = inside;
                    if (!inside) return false;
                    MouseDown(x, y, e.button, e.clickCount);
                    return true;
                case EventType.MouseUp:
                    if (_buttons == 0) return false;
                    MouseUp(x, y, e.button, Mathf.Max(1, e.clickCount));
                    return true;
                case EventType.MouseMove:
                case EventType.MouseDrag:
                    if (!inside && _buttons == 0) return false;
                    MouseMove(x, y);
                    return inside;
                case EventType.ScrollWheel:
                    if (!inside) return false;
                    Scroll(x, y, e.delta.x * 40f, e.delta.y * 40f);
                    return true;
                case EventType.KeyDown:
                    if (!HasFocus) return false;
                    return GuiKeyDown(e);
                case EventType.KeyUp:
                    if (!HasFocus) return false;
                    return GuiKeyUp(e);
            }
            return false;
        }

        /// <summary>The modifier bit mask (Alt = 1, Ctrl = 2, Meta = 4, Shift = 8) of an IMGUI event.</summary>
        public static int ModifiersOf(Event e)
        {
            return (e.alt ? 1 : 0) | (e.control ? 2 : 0) | (e.command ? 4 : 0) | (e.shift ? 8 : 0);
        }

        private bool GuiKeyDown(Event e)
        {
            int modifiers = ModifiersOf(e);
            if (e.character != '\0' && !char.IsControl(e.character))
            {
                // IMGUI sends the typed character in its own event (keyCode None) after the raw key event.
                KeyDown(KeyCode.None, e.character, modifiers);
                if (_pendingKey != KeyCode.None)
                    _heldCharacters[_pendingKey] = e.character;
                return true;
            }

            if (e.keyCode == KeyCode.None)
                return false;

            if (KeyTable.ContainsKey(e.keyCode))
            {
                KeyDown(e.keyCode, '\0', modifiers);
                return true;
            }

            _pendingKey = e.keyCode;
            return true;
        }

        private bool GuiKeyUp(Event e)
        {
            int modifiers = ModifiersOf(e);
            if (KeyTable.ContainsKey(e.keyCode))
            {
                KeyUp(e.keyCode, '\0', modifiers);
                return true;
            }

            char character;
            if (_heldCharacters.TryGetValue(e.keyCode, out character))
            {
                _heldCharacters.Remove(e.keyCode);
                KeyUp(KeyCode.None, character, modifiers);
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- internals

        private WebBrowserSession RequireSession()
        {
            if (_session == null)
                throw new WebBrowserException("The screen is not open yet: await OpenAsync first.");
            return _session;
        }

        private async Task StartStreamingAsync()
        {
            await _session.SendCommandAsync("Emulation.setDeviceMetricsOverride",
                "{\"width\":" + Width + ",\"height\":" + Height + ",\"deviceScaleFactor\":1,\"mobile\":false}").ConfigureAwait(false);
            await _session.SendCommandAsync("Page.startScreencast",
                "{\"format\":\"jpeg\",\"quality\":" + Mathf.Clamp(JpegQuality, 1, 100) + ",\"maxWidth\":" + Width + ",\"maxHeight\":" + Height + ",\"everyNthFrame\":1}").ConfigureAwait(false);
        }

        /// <summary>Runs on a background thread (DevTools events) so it must not touch Unity objects.</summary>
        private void OnProtocolEvent(string name, Dictionary<string, object> parameters)
        {
            switch (name)
            {
                case "Page.screencastFrame":
                    string data = SimpleJson.GetString(parameters, "data");
                    if (data.Length > 0)
                        Interlocked.Exchange(ref _latestFrame, Convert.FromBase64String(data));

                    object sessionId;
                    if (parameters.TryGetValue("sessionId", out sessionId) && sessionId is double)
                        Send("Page.screencastFrameAck", "{\"sessionId\":" + (int)(double)sessionId + "}");
                    break;

                case "Runtime.bindingCalled":
                    if (SimpleJson.GetString(parameters, "name") == _BRIDGE_BINDING)
                        _messages.Enqueue(SimpleJson.GetString(parameters, "payload"));
                    break;

                case "Page.frameNavigated":
                    Dictionary<string, object> frame = SimpleJson.GetObject(parameters, "frame");
                    if (frame != null && SimpleJson.GetString(frame, "parentId").Length == 0)
                        _urlChanges.Enqueue(SimpleJson.GetString(frame, "url"));
                    break;
            }
        }

        /// <summary>Sends a command without waiting for the answer; failures (for example a closed browser) are ignored.</summary>
        private void Send(string method, string parametersJson)
        {
            WebBrowserSession session = _session;
            if (session == null || !session.IsOpen)
                return;

            session.SendCommandAsync(method, parametersJson, 5000).ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static void RunInBackground(Func<Task> work)
        {
            Task.Run(async () =>
            {
                try { await work().ConfigureAwait(false); }
                catch (WebBrowserException) { }
                catch (TimeoutException) { }
            });
        }

        private string MouseJson(string type, float x, float y, string button, int clickCount)
        {
            return "{\"type\":\"" + type + "\",\"x\":" + F(x) + ",\"y\":" + F(y) + ",\"button\":\"" + button + "\",\"buttons\":" + _buttons + ",\"clickCount\":" + clickCount + "}";
        }

        private static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string ButtonName(int button)
        {
            return button == 0 ? "left" : button == 1 ? "right" : button == 2 ? "middle" : "none";
        }

        private static int ButtonMask(int button)
        {
            return button == 0 ? 1 : button == 1 ? 2 : button == 2 ? 4 : 0;
        }

        private struct KeyInfo
        {
            public string Key, Code, Text;
            public int VirtualKey;

            public KeyInfo(string key, string code, int virtualKey, string text = null)
            {
                Key = key;
                Code = code;
                VirtualKey = virtualKey;
                Text = text;
            }
        }

        private static readonly Dictionary<KeyCode, KeyInfo> KeyTable = BuildKeyTable();

        private static Dictionary<KeyCode, KeyInfo> BuildKeyTable()
        {
            var table = new Dictionary<KeyCode, KeyInfo>
            {
                { KeyCode.Backspace, new KeyInfo("Backspace", "Backspace", 8) },
                { KeyCode.Tab, new KeyInfo("Tab", "Tab", 9) },
                { KeyCode.Return, new KeyInfo("Enter", "Enter", 13, "\r") },
                { KeyCode.KeypadEnter, new KeyInfo("Enter", "NumpadEnter", 13, "\r") },
                { KeyCode.Escape, new KeyInfo("Escape", "Escape", 27) },
                { KeyCode.PageUp, new KeyInfo("PageUp", "PageUp", 33) },
                { KeyCode.PageDown, new KeyInfo("PageDown", "PageDown", 34) },
                { KeyCode.End, new KeyInfo("End", "End", 35) },
                { KeyCode.Home, new KeyInfo("Home", "Home", 36) },
                { KeyCode.LeftArrow, new KeyInfo("ArrowLeft", "ArrowLeft", 37) },
                { KeyCode.UpArrow, new KeyInfo("ArrowUp", "ArrowUp", 38) },
                { KeyCode.RightArrow, new KeyInfo("ArrowRight", "ArrowRight", 39) },
                { KeyCode.DownArrow, new KeyInfo("ArrowDown", "ArrowDown", 40) },
                { KeyCode.Insert, new KeyInfo("Insert", "Insert", 45) },
                { KeyCode.Delete, new KeyInfo("Delete", "Delete", 46) },
            };

            KeyCode[] functionKeys = { KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12 };
            for (int i = 0; i < functionKeys.Length; i++)
                table[functionKeys[i]] = new KeyInfo("F" + (i + 1), "F" + (i + 1), 112 + i);
            return table;
        }

        /// <summary>The Input.dispatchKeyEvent parameters of a key, or null when there is nothing to send.</summary>
        private static string KeyJson(KeyCode keyCode, char character, int modifiers, bool down)
        {
            KeyInfo info;
            if (KeyTable.TryGetValue(keyCode, out info))
            {
                string type = down ? (info.Text != null ? "keyDown" : "rawKeyDown") : "keyUp";
                string text = down && info.Text != null ? ",\"text\":" + JsonText.Quote(info.Text) : string.Empty;
                return "{\"type\":\"" + type + "\",\"modifiers\":" + modifiers + ",\"key\":\"" + info.Key + "\",\"code\":\"" + info.Code + "\",\"windowsVirtualKeyCode\":" + info.VirtualKey +
                       ",\"nativeVirtualKeyCode\":" + info.VirtualKey + text + "}";
            }

            if (character == '\0' || char.IsControl(character))
                return null;

            string key = JsonText.Quote(character.ToString());
            int virtualKey = VirtualKeyOf(character);

            // Ctrl and Alt held together is AltGr (how "@", "{", "\" and similar characters are typed on many keyboard layouts): that is text, not a shortcut.
            const int alt = 1, ctrl = 2, meta = 4;
            if ((modifiers & (alt | ctrl)) == (alt | ctrl))
                modifiers &= ~(alt | ctrl);

            bool shortcut = (modifiers & (alt | ctrl | meta)) != 0; // Alt, Ctrl or Meta alone: a shortcut such as Ctrl+A, not text
            string typed = down && !shortcut ? ",\"text\":" + key + ",\"unmodifiedText\":" + key : string.Empty;
            return "{\"type\":\"" + (down ? "keyDown" : "keyUp") + "\",\"modifiers\":" + modifiers + ",\"key\":" + key + ",\"windowsVirtualKeyCode\":" + virtualKey + typed + "}";
        }

        private static int VirtualKeyOf(char c)
        {
            if (c >= 'a' && c <= 'z') return c - 'a' + 65;
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
            return c == ' ' ? 32 : 0;
        }

        // ---------------------------------------------------------------- closing

        /// <summary>
        /// Stops streaming and disconnects. A browser this screen started hidden (and any browser with a temporary profile) is closed too, and a temporary profile folder is deleted;
        /// a visible browser window of a persistent profile stays open.
        /// </summary>
        public void Dispose()
        {
            DisposeCore();
        }

        /// <summary>
        /// Disconnects without closing the browser, so a hidden browser keeps its page while scripts reload; the next screen with the same profile reuses it. The browser of a
        /// temporary profile cannot be found again, so use <see cref="Dispose"/> for that.
        /// </summary>
        public void Detach()
        {
            _closeBrowserOnDispose = false;
            DeleteProfileOnDispose = false;
            DisposeCore();
        }

        private bool DeleteProfileOnDispose = true;

        private Task DisposeCore()
        {
            lock (_all) { _all.Remove(this); }

            WebBrowserSession session = _session;
            _session = null;
            string temporaryProfile = DeleteProfileOnDispose ? _temporaryProfile : null;
            _temporaryProfile = null;

            Task closing = Task.FromResult(true);
            if (session != null)
            {
                if (_closeBrowserOnDispose && session.IsOpen)
                    closing = session.CloseBrowserAsync().ContinueWith(task => DeleteProfile(temporaryProfile));
                else
                {
                    session.Dispose();
                    DeleteProfile(temporaryProfile);
                }
            }
            else
                DeleteProfile(temporaryProfile);

            if (Texture != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(Texture);
                else UnityEngine.Object.DestroyImmediate(Texture);
                Texture = null;
            }
            return closing;
        }

        /// <summary>Disposes every open screen and waits a moment for the browsers to close (the Editor calls this when it quits so no hidden browser keeps running).</summary>
        public static void DisposeAll()
        {
            WebBrowserScreen[] screens;
            lock (_all) { screens = _all.ToArray(); }

            var closing = new List<Task>();
            foreach (WebBrowserScreen screen in screens)
                closing.Add(screen.DisposeCore());

            try { Task.WaitAll(closing.ToArray(), 3000); }
            catch (AggregateException) { }
        }

        private static void DeleteProfile(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    try
                    {
                        if (Directory.Exists(directory))
                            Directory.Delete(directory, true);
                        return;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            });
        }
    }
}
