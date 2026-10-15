using System;
using UnityEditor;
using UnityEngine;

namespace Tauntastic.WebBrowser.Editor
{
    /// <summary>
    /// A browser tab inside the Unity Editor (Tools > Tauntastic > Web Browser): an address bar, back, forward and reload, and the page drawn in the window with mouse and keyboard
    /// forwarded to it. A hidden Chromium browser (Chrome, Edge, Brave) with its own profile renders the page, so sign-ins you make here stay for the next time.
    /// Other editor code can open it with <see cref="OpenAt"/> and use <see cref="Screen"/> to read pages.
    /// </summary>
    public class WebBrowserWindow : EditorWindow
    {
        private const string _DEFAULT_ADDRESS = "https://www.google.com";
        private const double _RESIZE_DELAY_SECONDS = 0.25;

        [SerializeField] private string _profileName = "Editor";
        [SerializeField] private string _address = _DEFAULT_ADDRESS;

        private WebBrowserScreen _screen;
        private Rect _contentRect = new Rect(0f, 0f, 1024f, 768f);
        private string _status = "Press Open to start the browser.";
        private bool _opening;
        private double _resizeAt;
        private Vector2Int _pendingSize;

        /// <summary>The screen of this window (null until the browser was opened), for code that reads or drives the page.</summary>
        public WebBrowserScreen Screen { get { return _screen; } }

        [MenuItem("Tools/Tauntastic/Web Browser")]
        public static WebBrowserWindow Open()
        {
            var window = GetWindow<WebBrowserWindow>("Web Browser");
            window.minSize = new Vector2(420f, 260f);
            window.Show();
            return window;
        }

        /// <summary>Opens the window and shows an address, using the named profile (a profile keeps its sign-ins). Starts the browser if needed.</summary>
        public static WebBrowserWindow OpenAt(string url, string profileName = null)
        {
            WebBrowserWindow window = Open();
            if (!string.IsNullOrWhiteSpace(profileName) && profileName != window._profileName)
            {
                window.CloseBrowser();
                window._profileName = profileName;
            }

            window._address = url;
            if (window._screen != null && window._screen.IsOpen)
                window.Navigate();
            else
                window.OpenBrowser();
            return window;
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            EditorApplication.update += Tick;

            // A script reload keeps the hidden browser running; pick it up again.
            if (SessionState.GetBool(SessionKey(), false))
                OpenBrowser();
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            SessionState.SetBool(SessionKey(), _screen != null);
            if (_screen == null)
                return;

            if (WebBrowserEditorLifecycle.Reloading)
                _screen.Detach();
            else
                _screen.Dispose();
            _screen = null;
        }

        private string SessionKey()
        {
            return "Tauntastic.WebBrowser.Window.Open." + GetInstanceID();
        }

        private void Tick()
        {
            if (_screen == null)
                return;

            _screen.Update();
            if (_screen.ConsumeFrameFlag())
                Repaint();

            if (_pendingSize.x > 0 && EditorApplication.timeSinceStartup >= _resizeAt)
            {
                _screen.Resize(_pendingSize.x, _pendingSize.y);
                _pendingSize = Vector2Int.zero;
            }
        }

        private async void OpenBrowser()
        {
            if (_opening)
                return;

            _opening = true;
            _status = "Starting the browser...";
            Repaint();
            try
            {
                if (_screen != null)
                    _screen.Dispose();

                _screen = new WebBrowserScreen(Mathf.RoundToInt(_contentRect.width), Mathf.RoundToInt(_contentRect.height));
                _screen.UrlChanged += url => { _address = url; Repaint(); };
                _screen.Closed += () => { _status = "The browser was closed."; Repaint(); };
                await _screen.OpenAsync(string.IsNullOrWhiteSpace(_address) ? null : _address, new WebBrowserOptions { ProfileName = _profileName, Headless = true });
                _status = "Ready.";
            }
            catch (Exception exception)
            {
                _status = exception.Message;
                if (_screen != null)
                {
                    _screen.Dispose();
                    _screen = null;
                }
            }
            finally
            {
                _opening = false;
                Repaint();
            }
        }

        private void CloseBrowser()
        {
            if (_screen != null)
            {
                _screen.Dispose();
                _screen = null;
            }
            _status = "The browser is closed.";
        }

        private async void Navigate()
        {
            if (_screen == null || !_screen.IsOpen)
                return;

            string url = _address.Trim();
            if (url.Length > 0 && url.IndexOf("://", StringComparison.Ordinal) < 0 && !url.StartsWith("about:", StringComparison.Ordinal))
                url = "https://" + url;

            try { await _screen.NavigateAsync(url); _status = "Loaded."; }
            catch (Exception exception) { _status = exception.Message; }
            Repaint();
        }

        private void OnGUI()
        {
            DrawToolbar();

            Rect area = GUILayoutUtility.GetRect(0f, 100000f, 0f, 100000f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Repaint)
                _contentRect = area;

            bool open = _screen != null && _screen.IsOpen;
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(_contentRect, new Color(0.16f, 0.16f, 0.16f));
                if (open && _screen.Texture != null)
                    GUI.DrawTexture(_contentRect, _screen.Texture, ScaleMode.StretchToFill, false);
                else
                    GUI.Label(_contentRect, _status, new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true });
            }

            if (open)
            {
                RequestResize();
                if (_screen.HandleGuiEvent(Event.current, _contentRect))
                {
                    GUIUtility.keyboardControl = 0;
                    if (Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout)
                        Event.current.Use();
                }
            }

            EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                bool open = _screen != null && _screen.IsOpen;
                using (new EditorGUI.DisabledScope(!open))
                {
                    if (GUILayout.Button("<", EditorStyles.toolbarButton, GUILayout.Width(26f))) RunAndRepaint(_screen.GoBackAsync());
                    if (GUILayout.Button(">", EditorStyles.toolbarButton, GUILayout.Width(26f))) RunAndRepaint(_screen.GoForwardAsync());
                    if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(52f))) RunAndRepaint(_screen.ReloadAsync());
                }

                GUI.SetNextControlName("Address");
                _address = EditorGUILayout.TextField(_address, EditorStyles.toolbarTextField);
                bool enter = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "Address";
                if (enter)
                    Event.current.Use();

                if (GUILayout.Button(open ? "Go" : "Open", EditorStyles.toolbarButton, GUILayout.Width(42f)) || enter)
                {
                    if (open) Navigate();
                    else OpenBrowser();
                }

                GUILayout.Label("Profile", EditorStyles.miniLabel, GUILayout.Width(38f));
                using (new EditorGUI.DisabledScope(open))
                    _profileName = EditorGUILayout.TextField(_profileName, EditorStyles.toolbarTextField, GUILayout.Width(80f));

                using (new EditorGUI.DisabledScope(_screen == null))
                {
                    if (GUILayout.Button("Close", EditorStyles.toolbarButton, GUILayout.Width(44f)))
                        CloseBrowser();
                }
            }
        }

        private async void RunAndRepaint(System.Threading.Tasks.Task task)
        {
            try { await task; }
            catch (Exception exception) { _status = exception.Message; }
            Repaint();
        }

        /// <summary>Makes the page follow the size of the window, after a short pause so dragging the window edge does not restart the stream for every pixel.</summary>
        private void RequestResize()
        {
            int width = Mathf.RoundToInt(_contentRect.width);
            int height = Mathf.RoundToInt(_contentRect.height);
            if (width < 64 || height < 64 || (Mathf.Abs(width - _screen.Width) < 4 && Mathf.Abs(height - _screen.Height) < 4))
                return;

            if (_pendingSize.x != width || _pendingSize.y != height)
            {
                _pendingSize = new Vector2Int(width, height);
                _resizeAt = EditorApplication.timeSinceStartup + _RESIZE_DELAY_SECONDS;
            }
        }
    }
}
