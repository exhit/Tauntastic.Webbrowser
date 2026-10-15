using System;

namespace Tauntastic.WebBrowser
{
    /// <summary>Thrown when the browser cannot be found, started or reached, or when it answers a command with an error.</summary>
    public class WebBrowserException : Exception
    {
        public WebBrowserException(string message) : base(message) { }

        public WebBrowserException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Thrown when JavaScript run in the page throws, or cannot run because the page is changing (for example while it navigates).</summary>
    public sealed class WebBrowserScriptException : WebBrowserException
    {
        public WebBrowserScriptException(string message) : base(message) { }
    }
}
