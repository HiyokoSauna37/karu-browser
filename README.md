<p align="right"><b>English</b> | <a href="README.ja.md">日本語</a></p>

<p align="center">
  <img src="assets/banner.png" width="430" alt="Karu">
</p>

<p align="center">A lightweight, keyboard-first Windows browser with configurable proxy routing.</p>

<p align="center">
  <a href="../../releases"><img src="https://img.shields.io/github/v/release/HiyokoSauna37/karu-browser?label=release&color=E8672E" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4" alt="Platform: Windows 10/11">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-97CA00" alt="License: MIT"></a>
</p>

---

Karu (軽 — "light") is a personal WPF + WebView2 (Chromium) browser for Windows, designed around one goal: keep memory usage low while browsing and watching video, without giving up a real Chromium engine. It has no tab bar, no URL bar, and no toolbar — everything is driven from the keyboard, Vim-style.

## Why

Chromium-based browsers are memory-hungry, and most "lightweight browser" alternatives either drop features or aren't actually Chromium (so sites/DRM/codecs behave differently). Karu instead runs real WebView2 (the same engine as Edge/Chrome) but:

- aggressively suspends and hibernates background tabs,
- tunes dozens of Chromium flags/features for low memory instead of speed,
- can hand video playback off to [mpv](https://mpv.io/) entirely, dropping Chromium's own resource usage for that tab to near zero,
- strips the browser chrome itself (tab bar/URL bar/toolbar) down to a single 40px drag bar, replaced by keyboard-driven overlays.

## Features

- **Chromeless UI** — no tab bar, no URL bar. Everything is a keyboard overlay or a single 40px title bar.
- **3-stage tab lifecycle** — Active → Suspended (WebView2 `TrySuspendAsync`, ~15s after losing focus) → Hibernated (WebView fully disposed, only URL + video position kept). A soft cap keeps the six most recent live tabs by default; older suspended tabs hibernate after ~60s, while other tabs use the existing ~3–10 min warmth policy. On session restore, only the visible tab is materialized, and low system memory still triggers emergency hibernation. Automatic hibernation never interrupts audio, loading, translation, or download activity.
- **Vim-style keyboard layer** — injected into every page: `j/k/h/l` scroll, `d/u` half-page, `gg/G` top/bottom, `f/F` link hints, `H/L` back/forward, `yy` copy URL, `?` help overlay, and more.
- **Keyboard-driven tab list** (`Ctrl+Tab`) and **bookmark list** (`b`) — both support `j/k` + `Enter` to select, and toggle closed by pressing the same key again.
- **YouTube-focused tuning** — in-player ad skipping, playback quality cap, forced H.264 (avoids VP9/AV1 decode cost), a "focus mode" that hides comments/related/shorts shelves, and an on-player speed button. The Skip button is pressed with a **real, trusted click injected through CDP**: YouTube's player now records a scripted `click()` (`isTrusted: false`) as an ad blocker and refuses to skip, and it plants an invisible decoy skip button to catch scripts, so Karu only clicks a button that is actually visible at the point it clicks. Karu does not seek inside ads: the player reports every seek during an ad, and jumping to the end before the ad has started leaves it stuck until a 10-second load timeout. Ads instead play muted at 2.4x behind a cover with the Karu logo, and Skip is pressed the moment it appears. The player flags ads that advance faster than 2.5x real time, counting from the moment the ad starts, so the first 0.3 s plays at 1x for margin. Click the cover to reveal the ad. Set `YouTubeAdSpeedUp` or `YouTubeAdCover` to `false` in `settings.json` to turn either off (ads are still muted and Skip is still pressed).
- **[mpv](https://mpv.io/) handoff** (`Ctrl+M`) — plays the current video in mpv (via yt-dlp) and collapses the page to a lightweight placeholder once playback is confirmed, keeping playback position for switching back.
- **Translate the page** (`Ctrl+Shift+Y`) — like Chrome's "Translate this page": rewrites the body into Japanese in place; press again to flip back (translation ⇄ original toggle). Links and `<code>` are kept intact and repositioned to the correct Japanese word order. See [Translating pages](#translating-pages).
- **Pop a tab into its own window** (`Ctrl+Shift+D`) — moves the current tab into a new window, for side-by-side viewing. When a detached window closes, its tabs are merged into the saved session instead of replacing it.
- **Presents as a normal browser** — hides `navigator.webdriver` and scopes the low-spec spoof to YouTube only, so bot checks like Cloudflare are less likely to flag it as automated.
- **Censorship-routing controls** — supports HTTP(S)/SOCKS5 proxies. Page traffic, translation, and Twitch interception use the same proxy. While a proxy is active, non-proxied WebRTC UDP and QUIC are disabled to avoid a direct IP leak.
- **Ad/tracker blocking** — a built-in domain blocklist (`%APPDATA%\Karu\blocklist.txt`). Two doubleclick URLs that YouTube uses only as ad-blocker probes (`ad_status.js` and `pagead/id`) are let through: blocking them removes no ads and makes YouTube report Karu as an ad blocker. **Sideloaded extensions cannot do this**: WebView2 runs no content scripts, and MV2 `webRequest` blocking has no effect either — measured, with uBlock Origin loaded and blocking nothing while costing memory.
- **Twitch ad removal** — Twitch stitches ads into the same HLS stream as the broadcast (server-side ad insertion), so domain blocking cannot touch them. Karu watches the playlist for ad markers and swaps in a freshly fetched, clean playlist instead. Verified against real mid-rolls on a live channel (43 detections, 43 successful swaps, original quality preserved). **This only defeats mid-rolls.** A pre-roll at stream open cannot be dodged this way, because the replacement session draws a pre-roll of its own. Ads that cannot be replaced are muted and covered over the player area only (never the whole page). Turn it off with `TwitchAdBlock` in `settings.json`.
- **Session restore, saved passwords/autofill, bookmarks.**
- **Caret browsing, video fullscreen via CDP, live memory usage breakdown.**

## Keybindings

| Key | Action |
|---|---|
| `j` / `k` | Scroll down / up |
| `h` / `l` | Scroll left / right |
| `d` / `u` | Half-page down / up |
| `gg` / `G` | Scroll to top / bottom |
| `f` / `F` | Link hints (open / open in new tab) |
| `H` / `L` | Back / forward |
| `r` | Reload |
| `yy` | Copy current URL |
| `>` / `<` / `=` | Playback speed +0.25 / −0.25 / reset |
| `?` | Toggle help overlay |
| `t` | New tab |
| `x` / `X` | Close tab / restore closed tab |
| `o` / `Ctrl+L` | URL / search overlay |
| `Ctrl+Tab` | Tab list overlay (`j/k`+`Enter` to switch, `Ctrl+W` to close selected) |
| `b` | Bookmark list overlay (`j/k`+`Enter`, `Shift+Enter` new tab, `b`/`Esc` to close) |
| `J` / `K` | Previous / next tab |
| `Ctrl+Shift+Y` | Toggle page translation (Japanese ⇄ original) |
| `Ctrl+Shift+D` | Detach the current tab into a new window |
| `Ctrl+T` / `Ctrl+W` | New tab / close tab |
| `Ctrl+D` | Bookmark current page |
| `Ctrl+1`–`9` | Jump to tab N / last tab |
| `Ctrl+Shift+T` | Reopen last closed tab |
| `Ctrl+M` | Open current video in mpv |
| `Ctrl+E` | Open current page in Edge |
| `Ctrl+B` | Toggle video focus mode |
| `Ctrl+O` | Toggle video fullscreen |
| `F7` | Toggle caret browsing (restarts app) |
| `F11` | Toggle window fullscreen |
| `Ctrl+Shift+W` | Quit |

## Translating pages

`Ctrl+Shift+Y` (or the top-right `≡` menu) rewrites the page into Japanese, like Chrome's "Translate this page". Press it again to flip back — it's a translation ⇄ original toggle, and re-translating/restoring is instant (cached).

Rather than translating each text run on its own, it translates block by block with inline elements (links, `<code>`, …) swapped for placeholders. That way **links and code keep their contents and get repositioned to the correct Japanese word order** (code contents are left untranslated). Translating text nodes individually would scramble the word order of a sentence split across a link and misplace the link — this avoids that.

Single-page apps are handled too: when a site swaps its content without a real page load (soft navigation, e.g. YouTube), the toggle notices the page has effectively changed and translates the new content instead of flipping a stale cache.

It uses Google Translate's free endpoint (no API key; the request runs on the app side). Repeated text (menus, headers) is cached app-wide, so browsing across pages of the same site only translates what's new. Being unofficial, very large pages can hit rate limits (the app retries once on a rate-limit response; any leftover text stays in the original language).

## Requirements

- Windows 10/11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) — already present on most Windows 11/10 machines with Edge installed
- [mpv](https://mpv.io/) (optional, for `Ctrl+M`) — `winget install mpv-player.mpv-CI.MSVC`

## Install

Download the latest zip from [Releases](../../releases), extract it anywhere, and run `Karu.exe`.

> Karu isn't code-signed yet, so Windows SmartScreen may warn about an unrecognized app on first run. Click **More info → Run anyway**. See [why](#a-note-on-smartscreen) below if you'd rather build it yourself.

## Build from source

```
dotnet publish src/Karu.csproj -c Release -o dist
```

The result is a framework-dependent build in `dist/` (requires the .NET 10 Desktop Runtime on the target machine).

## Configuration

Settings, bookmarks, the ad blocklist, and sideloaded extensions all live in `%APPDATA%\Karu`. WebView2's own profile data (cookies, login sessions) is under `%LOCALAPPDATA%\Karu\WebView2Data`. Open the settings folder from the in-app menu (top-right `≡` button). `settings.json` is created on first launch; restart Karu after changing network settings.

Example censorship-routing settings:

```json
{
  "ProxyServer": "socks5://127.0.0.1:9050",
  "EnableBrowserProtections": true
}
```

- `ProxyServer`: `http://host:port`, `https://host:port`, or `socks5://host:port`. Leave empty to use the system route. Embedded credentials are rejected so they cannot leak through process listings. If the value does not parse, Karu starts without a proxy and says so in a warning dialog and in the menu; the value is left in `settings.json` for you to fix.
- `EnableBrowserProtections`: the default `true` keeps site isolation, SmartScreen, and WebView2 tracking prevention enabled. `false` restores the older, more aggressive low-memory setup and is not recommended for browsing arbitrary sites.
- `MaxLiveTabs`: soft cap for simultaneously materialized tabs, including the active tab (default `6`; `0` disables the cap). Only an inactive tab that has stayed hidden for 60 seconds and was successfully suspended is hibernated; audio, loading, translation, and download activity is protected.

While `ProxyServer` is active, handoff to external mpv or Edge is disabled because Karu cannot guarantee that those processes inherit its route. Leave it empty when using a system-wide VPN if you need those integrations.

Karu does not ship a relay service. Bypassing DNS/IP/SNI-level blocks requires a reachable external proxy, VPN, or Tor that also resolves destination names remotely. WebView2 has no supported per-app DoH API, so Karu does not claim DoH support through Chromium-internal flags that may be ignored.

Set the `KARU_PROFILE_DIR` environment variable to run Karu against a separate profile (settings and WebView2 data under that folder), for example to test a build without touching your real session.

Unpacked Chromium extensions under `%APPDATA%\Karu\extensions\` are loaded, but **do not put an ad blocker there**. WebView2 runs neither content scripts nor MV2 `webRequest` blocking, so it blocks nothing — and the mere presence of an extension disables the built-in blocklist (measured: ~105 MB for the extension renderer alone, zero requests blocked).

## A note on SmartScreen

Karu keeps WebView2's SmartScreen integration, site isolation, and tracking prevention enabled by default. The Windows “unrecognized app” warning on first launch is separate: it appears because the distributed binary is unsigned and has little reputation. You can build from source using the instructions above if preferred.

## License

[MIT](LICENSE)
