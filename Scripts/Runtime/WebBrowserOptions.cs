namespace Tauntastic.WebBrowser
{
    /// <summary>How <see cref="WebBrowserSession.OpenAsync"/> finds or starts the browser.</summary>
    public sealed class WebBrowserOptions
    {
        /// <summary>
        /// Name of the browser profile (a folder under your local application data, Tauntastic/WebBrowser/&lt;name&gt;). The profile is separate from your everyday browser
        /// and keeps its sign-ins and cookies between runs, so give each website or feature its own name. Default: "Default".
        /// </summary>
        public string ProfileName = "Default";

        /// <summary>Full path of the profile folder; when set it replaces the folder derived from <see cref="ProfileName"/> (used for temporary profiles).</summary>
        public string ProfileDirectory = string.Empty;

        /// <summary>Starts the browser muted, because its sound cannot be routed into Unity (default true).</summary>
        public bool MuteAudio = true;

        /// <summary>The page the browser opens when it is started (ignored when a browser of this profile is already running). Default: a blank page.</summary>
        public string StartUrl = "about:blank";

        /// <summary>Opens a window without tabs or address bar (Chromium's app mode). The user cannot type another address, so leave it off for pages that need a sign-in.</summary>
        public bool AppMode;

        /// <summary>Starts the browser without a visible window. Sign-ins that need the user, or a captcha, cannot work that way.</summary>
        public bool Headless;

        /// <summary>Size of the new window in pixels (0 keeps the browser's own choice).</summary>
        public int WindowWidth;

        /// <summary>Size of the new window in pixels (0 keeps the browser's own choice).</summary>
        public int WindowHeight;

        /// <summary>Full path of the browser to use; empty finds Chrome, Edge, Brave or Chromium by itself.</summary>
        public string BrowserPath = string.Empty;

        /// <summary>Extra command-line arguments for a newly started browser, for example "--lang=en-US".</summary>
        public string ExtraArguments = string.Empty;

        /// <summary>Milliseconds to wait for a newly started browser to be ready (default 20000).</summary>
        public int StartTimeoutMs = 20000;
    }
}
