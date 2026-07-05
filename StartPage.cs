using System.Net;

namespace Karu;

static class StartPage
{
    public static string Generate(IEnumerable<Bookmark> bookmarks)
    {
        var cards = string.Join("\n", bookmarks.Select(b =>
            $"""<a class="card" href="{WebUtility.HtmlEncode(b.Url)}"><span>{WebUtility.HtmlEncode(b.Title)}</span><small>{WebUtility.HtmlEncode(HostOf(b.Url))}</small></a>"""));

        if (cards.Length == 0)
            cards = """<p class="hint">★ でお気に入りに追加するとここに並びます</p>""";

        return $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<title>新しいタブ</title>
<style>
  :root { color-scheme: dark; }
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body {
    background: #111; min-height: 100vh;
    font-family: "Segoe UI", "Yu Gothic UI", sans-serif;
    display: flex; flex-direction: column; align-items: center;
    justify-content: center; gap: 36px; padding: 40px 20px;
    position: relative; overflow: hidden;
  }
  body::before {
    content: ''; position: fixed; inset: 0;
    background: radial-gradient(ellipse 60% 50% at 50% 40%, rgba(212,132,94,.06) 0%, transparent 70%);
    pointer-events: none;
  }
  .logo { text-align: center; user-select: none; }
  .logo h1 {
    font-size: 56px; font-weight: 200; letter-spacing: 10px; color: #888;
    margin-bottom: 6px;
  }
  .logo h1 b { color: #D4845E; font-weight: 600; text-shadow: 0 0 30px rgba(212,132,94,.3); }
  .logo p { font-size: 12px; color: #555; letter-spacing: 3px; text-transform: uppercase; }
  form { width: min(560px, 84vw); }
  input {
    width: 100%; padding: 14px 24px; font-size: 15px; color: #ddd;
    background: rgba(255,255,255,.06); border: 1px solid rgba(255,255,255,.08);
    border-radius: 12px; outline: none; backdrop-filter: blur(10px);
    transition: border-color .2s, background .2s;
  }
  input::placeholder { color: #666; }
  input:focus { border-color: #D4845E; background: rgba(255,255,255,.08); }
  .cards { display: flex; flex-wrap: wrap; gap: 10px; justify-content: center; max-width: 780px; }
  .card {
    display: flex; flex-direction: column; gap: 3px; text-decoration: none;
    background: rgba(255,255,255,.04); border: 1px solid rgba(255,255,255,.06);
    border-radius: 10px; padding: 12px 16px; min-width: 140px; max-width: 200px;
    transition: background .15s, border-color .15s;
  }
  .card:hover { background: rgba(255,255,255,.08); border-color: rgba(212,132,94,.3); }
  .card:focus, .card:focus-visible { outline: 2px solid #D4845E; outline-offset: 1px; }
  .card span { color: #ccc; font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .card small { color: #666; font-size: 11px; }
  .hint { color: #555; font-size: 13px; }
  .keys {
    position: fixed; bottom: 16px; left: 0; right: 0; text-align: center;
    color: #444; font-size: 11px; line-height: 1.8;
    font-family: Consolas, monospace;
  }
  .keys kbd {
    display: inline-block; color: #D4845E; font: inherit;
    padding: 1px 5px; margin: 0 1px;
    border: 1px solid rgba(212,132,94,.45); border-radius: 3px; background: rgba(212,132,94,.06);
  }
</style>
</head>
<body>
  <div class="logo">
    <h1>Ka<b>ru</b></h1>
    <p>lightweight video browser</p>
  </div>
  <form action="https://www.google.com/search">
    <input name="q" placeholder="Search（URLは o キー / タブ一覧は Ctrl+Tab）" autofocus autocomplete="off">
  </form>
  <div class="cards">
{{cards}}
  </div>
  <div class="keys">
    <kbd>f</kbd> links · <kbd>j</kbd><kbd>k</kbd> scroll · <kbd>d</kbd><kbd>u</kbd> half · <kbd>H</kbd><kbd>L</kbd> back/fwd ·
    <kbd>t</kbd> tab · <kbd>x</kbd> close · <kbd>o</kbd> URL · <kbd>Ctrl+Tab</kbd> tabs · <kbd>&gt;</kbd><kbd>&lt;</kbd> speed · <kbd>?</kbd> help ·
    <kbd>Ctrl M</kbd> mpv · <kbd>Ctrl B</kbd> focus · <kbd>Ctrl O</kbd> video-fs · <kbd>F11</kbd> fullscreen
  </div>
</body>
</html>
""";
    }

    static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";

    /// <summary>mpv再生中のプレースホルダー。重いwatchページをこれに差し替えてメモリをほぼゼロにする。
    /// クリックで 'mpvReturn' を送り、アプリ側が元URL+再生位置へ戻す。</summary>
    public static string MpvHold(string title) => $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<title>🎬 {{WebUtility.HtmlEncode(title)}}</title>
<style>
  :root { color-scheme: dark; }
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body {
    background: #111; color: #E8DFD3; min-height: 100vh; cursor: pointer;
    font-family: "Segoe UI", "Yu Gothic UI", sans-serif;
    display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 18px;
  }
  .icon { font-size: 64px; filter: drop-shadow(0 0 24px rgba(212,132,94,.4)); }
  .title { font-size: 16px; color: #ccc; max-width: 70vw; overflow: hidden;
           text-overflow: ellipsis; white-space: nowrap; }
  .msg { font-size: 13px; color: #D4845E; letter-spacing: 1px; }
  .hint { font-size: 12px; color: #666; }
</style>
</head>
<body>
  <div class="icon">🎬</div>
  <div class="title">{{WebUtility.HtmlEncode(title)}}</div>
  <div class="msg">mpv で再生中 — ブラウザ側はメモリ解放済み</div>
  <div class="hint">クリックでページに戻る</div>
  <script>document.body.addEventListener('click', () => {
    try { window.chrome.webview.postMessage('mpvReturn'); } catch (e) {}
  });</script>
</body>
</html>
""";
}
