using System;
using System.Collections.Generic;
using System.IO;

namespace Tauntastic.WebBrowser
{
    /// <summary>Finds a Chromium based browser (Chrome, Edge, Brave or Chromium) installed on this computer. Chrome is preferred, then Edge, Brave and Chromium.</summary>
    public static class BrowserLocator
    {
        /// <summary>The full path of the first installed browser, or null when none is found. <paramref name="preferredPath"/> wins when that file exists.</summary>
        public static string Find(string preferredPath = null)
        {
            if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
                return preferredPath;

            foreach (string candidate in Candidates())
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        /// <summary>The places browsers are normally installed on this operating system, most preferred first.</summary>
        public static IEnumerable<string> Candidates()
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                string programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
                string programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                foreach (string root in new[] { programFiles, programFilesX86, local })
                {
                    if (!string.IsNullOrEmpty(root))
                        yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                }
                foreach (string root in new[] { programFilesX86, programFiles })
                {
                    if (!string.IsNullOrEmpty(root))
                        yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
                }
                foreach (string root in new[] { programFiles, programFilesX86, local })
                {
                    if (!string.IsNullOrEmpty(root))
                        yield return Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
                }
            }
            else if (Directory.Exists("/Applications"))
            {
                yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
                yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
                yield return "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser";
                yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
            }
            else
            {
                yield return "/usr/bin/google-chrome";
                yield return "/usr/bin/google-chrome-stable";
                yield return "/usr/bin/microsoft-edge";
                yield return "/usr/bin/brave-browser";
                yield return "/usr/bin/chromium";
                yield return "/usr/bin/chromium-browser";
                yield return "/snap/bin/chromium";
            }
        }
    }
}
