using UnityEditor;

namespace Tauntastic.WebBrowser.Editor
{
    /// <summary>Keeps browsers tidy around script reloads and Editor quit: a reload only disconnects (the hidden browser and its page stay), quitting closes the hidden browsers.</summary>
    [InitializeOnLoad]
    internal static class WebBrowserEditorLifecycle
    {
        /// <summary>True from just before scripts reload; windows use it to detach their browser instead of closing it.</summary>
        public static bool Reloading { get; private set; }

        static WebBrowserEditorLifecycle()
        {
            Reloading = false;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                Reloading = true;
                WebBrowserSession.DisconnectAll();
            };
            EditorApplication.quitting += WebBrowserScreen.DisposeAll;
        }
    }
}
