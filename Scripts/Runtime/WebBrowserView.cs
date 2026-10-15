using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace Tauntastic.WebBrowser
{
    /// <summary>
    /// Shows a web page inside a scene, for example on a UI image or on a 3D screen: add it to a GameObject, set the start address, and show <see cref="Texture"/> where you want it
    /// (the components of Tauntastic.WebBrowser.UI do that and forward pointer input). It needs Chrome, Edge, Brave or Chromium installed on the computer that runs the game,
    /// so it works in the Editor and in Windows, macOS and Linux standalone builds, not on mobile, WebGL or consoles.
    /// <para>You can also show pages your game made with <see cref="LoadHtml"/>, and pages talk to the game with <c>unity.send("text")</c> (see <see cref="MessageReceived"/>).</para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Tauntastic/Web Browser View")]
    public class WebBrowserView : MonoBehaviour
    {
        [Tooltip("Width of the page viewport in pixels.")]
        [SerializeField] private int _width = 1024;

        [Tooltip("Height of the page viewport in pixels.")]
        [SerializeField] private int _height = 768;

        [Tooltip("The page to open: a web address, or leave empty and call LoadHtml yourself.")]
        [SerializeField] private string _startUrl = "about:blank";

        [Tooltip("Open the browser automatically when the scene starts.")]
        [SerializeField] private bool _openOnStart = true;

        [Tooltip("Use a throw-away profile: nothing (sign-ins, cookies) is kept after the view is destroyed. Turn it off to keep sign-ins in the named profile.")]
        [SerializeField] private bool _temporaryProfile = true;

        [Tooltip("Profile name used when Temporary Profile is off. Sign-ins made in a profile stay for the next run.")]
        [SerializeField] private string _profileName = "InGame";

        [Tooltip("JPEG quality of the streamed frames, 1 to 100. Lower is faster.")]
        [Range(1, 100)]
        [SerializeField] private int _jpegQuality = 75;

        [Tooltip("Draw the page over the whole game view with IMGUI and use the mouse and keyboard on it. Handy for a quick test without any canvas.")]
        [SerializeField] private bool _drawFullScreen;

        [Tooltip("Raised with the text a page sends with unity.send(\"text\").")]
        [SerializeField] private UnityEvent<string> _messageReceived = new UnityEvent<string>();

        private WebBrowserScreen _screen;

        /// <summary>The page as a texture (created in Awake, blank until the first frame arrives).</summary>
        public Texture2D Texture { get { return Screen.Texture; } }

        /// <summary>The underlying screen, for input and commands.</summary>
        public WebBrowserScreen Screen
        {
            get
            {
                if (_screen == null)
                {
                    _screen = new WebBrowserScreen(_width, _height) { JpegQuality = _jpegQuality };
                    _screen.MessageReceived += text => _messageReceived.Invoke(text);
                }
                return _screen;
            }
        }

        /// <summary>Raised with the text a page sends with <c>unity.send("text")</c>.</summary>
        public UnityEvent<string> MessageReceived { get { return _messageReceived; } }

        /// <summary>The viewport size; set it before the view opens, or use <see cref="Resize"/> afterwards.</summary>
        public Vector2Int Size { get { return new Vector2Int(Screen.Width, Screen.Height); } }

        public bool IsOpen { get { return _screen != null && _screen.IsOpen; } }

        /// <summary>Whether keyboard input goes to the page.</summary>
        public bool HasFocus
        {
            get { return Screen.HasFocus; }
            set { Screen.HasFocus = value; }
        }

        private void Awake()
        {
            // Create the screen (and its texture) now so other components can bind to Texture in their own Awake or Start.
            var ignored = Screen;
        }

        private async void Start()
        {
            if (_openOnStart)
                await OpenAsync();
        }

        /// <summary>Starts the browser and shows <see cref="_startUrl"/>. Errors (for example no browser installed) are logged.</summary>
        public async Task OpenAsync()
        {
            try
            {
                await Screen.OpenAsync(string.IsNullOrWhiteSpace(_startUrl) || _startUrl == "about:blank" ? null : _startUrl,
                    new WebBrowserOptions { ProfileName = _profileName, Headless = true }, _temporaryProfile);
            }
            catch (Exception exception)
            {
                Debug.LogError("[WebBrowserView] Could not open the browser: " + exception.Message, this);
            }
        }

        /// <summary>Opens an address.</summary>
        public Task NavigateAsync(string url) { return Screen.NavigateAsync(url); }

        /// <summary>Shows HTML text that the game made, such as an in-game website.</summary>
        public Task LoadHtmlAsync(string html) { return Screen.LoadHtmlAsync(html); }

        /// <summary>Runs JavaScript in the page and returns its value.</summary>
        public Task<object> EvaluateAsync(string expression) { return Screen.EvaluateAsync(expression); }

        /// <summary>Sends text to the page (window event "unity-message", detail = the text).</summary>
        public Task SendToPageAsync(string text) { return Screen.SendToPageAsync(text); }

        public void Resize(int width, int height) { Screen.Resize(width, height); }

        private void Update()
        {
            if (_screen != null)
                _screen.Update();
        }

        private void OnGUI()
        {
            if (_screen == null)
                return;

            Event current = Event.current;
            if (_drawFullScreen)
            {
                var rect = new Rect(0f, 0f, UnityEngine.Screen.width, UnityEngine.Screen.height);
                if (current.type == EventType.Repaint)
                    GUI.DrawTexture(rect, _screen.Texture, ScaleMode.StretchToFill, false);
                if (_screen.HandleGuiEvent(current, rect) && current.type != EventType.Repaint)
                    current.Use();
                return;
            }

            // Keyboard input for pages shown on a canvas or a 3D surface: the pointer components set the focus.
            if ((current.type == EventType.KeyDown || current.type == EventType.KeyUp) && _screen.HasFocus && _screen.HandleGuiEvent(current, new Rect(0f, 0f, 1f, 1f)))
                current.Use();
        }

        private void OnDestroy()
        {
            if (_screen != null)
            {
                _screen.Dispose();
                _screen = null;
            }
        }
    }
}
