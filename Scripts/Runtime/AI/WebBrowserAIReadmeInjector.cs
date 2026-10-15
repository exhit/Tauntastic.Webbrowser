#if UNITY_EDITOR
// Generated from ARTIFICIAL_INTELLIGENCE_README.md of com.tauntastic.webbrowser.
// Keeps the paragraph below in sync with Assets/ARTIFICIAL_INTELLIGENCE_README.md so AI agents can read how to use this package.
namespace Tauntastic.AIReadme
{
    [UnityEditor.InitializeOnLoad]
    internal static class WebBrowserAIReadmeInjector
    {
        private const string FileName = "ARTIFICIAL_INTELLIGENCE_README.md";
        private const string Header = "# ARTIFICIAL INTELLIGENCE README";
        private const string PackageKey = "Package \"com.tauntastic.webbrowser\"";

        private const string Paragraph = @"Package ""com.tauntastic.webbrowser"" (assemblies Tauntastic.WebBrowser, Tauntastic.WebBrowser.UI and Tauntastic.WebBrowser.Editor, namespaces Tauntastic.WebBrowser, Tauntastic.WebBrowser.UI and Tauntastic.WebBrowser.Editor, no dependencies): drives a real Chromium browser (Chrome, Edge, Brave or Chromium must be installed on the machine) through the DevTools protocol, in the Editor and in Windows, macOS and Linux standalone builds (not WebGL, mobile or consoles). WebBrowserSession.OpenAsync(WebBrowserOptions) starts the browser, or reuses the running one of the same ProfileName, and returns a session with NavigateAsync, EvaluateAsync (returns SimpleJson types: string, double, bool, null, List<object>, Dictionary<string, object>, and awaits Promises), EvaluateStringAsync, WaitForAsync (polls a JavaScript condition and survives navigations and sign-in redirects), GetUrlAsync, DownloadAsync (downloads a file with the browser so its sign-in applies: the current page must be on the same site as the link, the file is saved under the name the server suggests and its path is returned, with optional progress callback and cancellation), SendCommandAsync (any DevTools command), CloseBrowserAsync and Dispose (which only disconnects); a named profile keeps sign-ins between runs, await the methods from the main thread, and ProtocolEvent handlers run on a background thread so they must not touch Unity objects. WebBrowserScreen renders a page into its Texture (hidden headless browser streaming JPEG frames): call Update() every frame, forward input with MouseMove, MouseDown, MouseUp, Scroll, KeyDown, KeyUp or HandleGuiEvent(Event, Rect), show game-made HTML with LoadHtmlAsync, pages call unity.send(""text"") which raises MessageReceived, SendToPageAsync delivers a window event ""unity-message"", and Dispose closes the browser while Detach keeps it running for the next screen of the same profile. For scenes use the WebBrowserView MonoBehaviour (Texture, MessageReceived, NavigateAsync, LoadHtmlAsync, EvaluateAsync, optional full-screen IMGUI drawing) together with WebBrowserPointerInput (uGUI RawImage, needs an EventSystem) or WebBrowserSurfaceInput (3D object with a non-convex MeshCollider, needs a PhysicsRaycaster). In the Editor, Tools > Tauntastic > Web Browser opens WebBrowserWindow, and WebBrowserWindow.OpenAt(url, profileName) opens it on an address with a persistent profile and exposes its Screen. There is no audio, no clipboard shortcut forwarding, files only download through DownloadAsync, and the DevTools port is bound to 127.0.0.1 only while the browser runs.";
        static WebBrowserAIReadmeInjector()
        {
            UnityEditor.EditorApplication.delayCall += Inject;
        }

        private static void Inject()
        {
            if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating)
            {
                UnityEditor.EditorApplication.delayCall += Inject;
                return;
            }

            try
            {
                string path = System.IO.Path.Combine(UnityEngine.Application.dataPath, FileName);
                bool fileExisted = System.IO.File.Exists(path);
                string existingText = fileExisted ? Normalize(System.IO.File.ReadAllText(path)) : string.Empty;

                var paragraphs = new System.Collections.Generic.List<string>();
                if (fileExisted)
                {
                    foreach (string block in existingText.Split(new[] { "\n\n" }, System.StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = block.Trim();
                        if (trimmed.Length > 0)
                            paragraphs.Add(trimmed);
                    }
                }
                else
                {
                    paragraphs.Add(Header);
                }

                // Identical paragraph already present: add nothing.
                bool alreadyPresent = paragraphs.Contains(Paragraph);

                // Drop every other paragraph that refers to this same package (outdated versions).
                paragraphs.RemoveAll(p => p.StartsWith(PackageKey, System.StringComparison.Ordinal) && !string.Equals(p, Paragraph, System.StringComparison.Ordinal));

                if (!alreadyPresent)
                    paragraphs.Add(Paragraph);

                string newText = string.Join("\n\n", paragraphs) + "\n";
                if (fileExisted && string.Equals(newText, existingText, System.StringComparison.Ordinal))
                    return;

                System.IO.File.WriteAllText(path, newText, new System.Text.UTF8Encoding(false));
                UnityEditor.AssetDatabase.ImportAsset("Assets/" + FileName);
            }
            catch (System.Exception exception)
            {
                UnityEngine.Debug.LogWarning("[WebBrowserAIReadmeInjector] Could not update Assets/" + FileName + ": " + exception.Message);
            }
        }

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }
}
#endif