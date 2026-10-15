# Tauntastic.WebBrowser

A real web browser for Unity, in the Editor and in games. It drives a Chrome, Edge, Brave or Chromium browser that is installed on the computer: pages are rendered into a texture, mouse and keyboard are forwarded to them, and your code can open pages, wait for a sign-in, run JavaScript and read the results. Use it for in-editor tools that need a login, and for websites inside a game (a real page, or HTML your game makes).

- Package: `com.tauntastic.webbrowser` (v1.0.0, Unity 2023.2+)
- Assemblies: `Tauntastic.WebBrowser` (runtime), `Tauntastic.WebBrowser.UI` (uGUI and 3D input components), `Tauntastic.WebBrowser.Editor` (editor window)
- Namespaces: `Tauntastic.WebBrowser`, `Tauntastic.WebBrowser.UI`, `Tauntastic.WebBrowser.Editor`
- Dependencies: none (the UI assembly uses `UnityEngine.UI`)
- AI agents: see [ARTIFICIAL_INTELLIGENCE_README.md](ARTIFICIAL_INTELLIGENCE_README.md)

## Installation

```
https://github.com/exhit/Tauntastic.Webbrowser.git
```

## Requirements and limits

- A Chromium based browser must be installed where Unity or the game runs: Google Chrome, Microsoft Edge (every Windows 10/11 has it), Brave or Chromium. Set `WebBrowserOptions.BrowserPath` to use another one.
- Works in the Editor and in Windows, macOS and Linux standalone builds. It does not work in WebGL, on mobile or on consoles, because no browser can be started there.
- Pages are streamed as JPEG frames: fine for pages, menus and light animation, not for games or video. There is no audio (the browser is started muted), no clipboard shortcut forwarding (Ctrl+C / Ctrl+V); files can only be downloaded through `WebBrowserSession.DownloadAsync`.
- Pages the browser cannot reach (offline, blocked by a firewall) just do not load.

## In the Editor

**Tools > Tauntastic > Web Browser** opens a browser tab inside the Editor: address bar, back, forward, reload, the page drawn in the window, mouse and keyboard forwarded to it. Sign-ins stay in the **Profile** you pick (default `Editor`), so you sign in to a site once.

Other editor code can open it on an address and read pages:

```csharp
WebBrowserWindow window = WebBrowserWindow.OpenAt("https://example.com", "MyTool");
// later, once window.Screen is open:
object title = await window.Screen.EvaluateAsync("document.title");
```

## In a game

Add **Tauntastic > Web Browser View** to a GameObject, set the start address, and show the texture:

- **UI:** add a `RawImage` and **Web Browser Pointer Input** to the same GameObject. The page appears on the image and the mouse works on it (the scene needs an `EventSystem`). A click gives the page keyboard focus.
- **3D screen:** add **Web Browser Surface Input** to an object with a `Renderer` and a non-convex `MeshCollider`; add a `PhysicsRaycaster` to the camera. The page is assigned as the material's main texture.
- **Quick test:** tick **Draw Full Screen** on the view.

The first frame needs a moment while the browser starts. Pages can talk to the game:

```csharp
WebBrowserView view = GetComponent<WebBrowserView>();

// A page your game made: an in-game website.
await view.LoadHtmlAsync("<h1>Welcome</h1><button onclick=\"unity.send('buy')\">Buy</button>");
view.MessageReceived.AddListener(text => Debug.Log("The page said: " + text));   // "buy"

// Talk to the page; it receives a window event named "unity-message".
await view.SendToPageAsync("coins:42");
object value = await view.EvaluateAsync("document.title");
```

In the page: `unity.send("text")` sends text to the game, and `window.addEventListener("unity-message", e => ... e.detail)` receives text from it.

`WebBrowserView` uses a throw-away profile by default, so nothing a player does in a page (sign-ins, cookies) is kept. Untick **Temporary Profile** to keep it in the named profile.

## Without a texture: the session

`WebBrowserSession` is the core, usable from any editor or game code:

```csharp
using (WebBrowserSession session = await WebBrowserSession.OpenAsync(new WebBrowserOptions { ProfileName = "MySite", StartUrl = "https://example.com/login" }))
{
    // The window is visible, the user signs in; their sign-in is kept in the profile.
    await session.WaitForAsync("document.querySelector('.account-name') != null", 10 * 60 * 1000);
    object names = await session.EvaluateAsync("[...document.querySelectorAll('.item')].map(e => e.textContent.trim())");
    // names is a List<object> of strings
}   // disposing only disconnects; the window stays open. Use CloseBrowserAsync to close it.
```

`WaitForAsync` keeps retrying through page loads and sign-in redirects. `DownloadAsync(url, folder, progress)` downloads a file with the browser, so the page's sign-in applies (the current page must be on the same site as the link): the file is saved under the name the server suggests and its path is returned.

```csharp
string file = await session.DownloadAsync("/downloads/42?token=abc", folder, (received, total) => Debug.Log(received + " of " + total));
```

It `SendCommandAsync` sends any [DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/) command. Await the methods from the main thread, and do not touch Unity objects inside `ProtocolEvent` handlers (they run on a background thread).

## How it works and what it can see

The package starts the browser with its own profile folder (under your local application data, `Tauntastic/WebBrowser/<name>`) and `--remote-debugging-port=0`, so the browser picks a free port and writes it into the profile. The package connects to that port over WebSocket and speaks the Chrome DevTools Protocol. The profile is separate from your everyday browser, so it never sees your other sign-ins, passwords or history. While a controlled browser runs, its DevTools port listens on `127.0.0.1` only; other programs on your computer could connect to it, so do not leave a controlled browser open with a sensitive session longer than you need.

The browser of a `WebBrowserScreen` runs hidden (headless) and is closed when the screen is disposed or the Editor quits. A script reload in the Editor only disconnects, so the page stays.
