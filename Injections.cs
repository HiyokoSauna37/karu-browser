namespace Karu;

static class Injections
{
    /// <summary>
    /// 注入スクリプト→ホストの postMessage コマンド認証トークン (起動ごとに変わる)。
    /// WebView2の WebMessageReceived は送信元スクリプトを区別できないため、ページ自身のJSが
    /// 'tabClose' 等を偽装send してタブ操作や強制ナビゲーションを行うのを、この接頭辞の検証で防ぐ。
    /// document-start 注入(Vim層)は postMessage の参照をページスクリプト実行前に捕獲するので、
    /// ページ側がpostMessageをフックしてもトークンは漏れない。
    /// </summary>
    public static readonly string MessageToken = Guid.NewGuid().ToString("N");

    /// <summary>
    /// ページ内読み込みカバー。ナビゲーション直後の「描画済みに見えるが操作できない」期間、
    /// ページ全面を炎アニメーション付きの被いで覆う。ホストとの通信は不要(完全にページ内で完結)。
    ///
    /// WPF側オーバーレイでなくDOM内に置く理由: WPFで被うにはWebViewを非表示にする必要があり(airspace)、
    /// 非表示ページはChromiumがタイマーを1秒間隔に間引くため「落ち着き」判定自体が遅れる悪循環になる。
    /// DOM内ならページは可視のままで、タイマー・動画自動再生・遅延ロードがすべて正常に動く。
    ///
    /// 解除条件は「readyStateがloadingを抜けた」かつ「直近250msに50ms超の主スレッド占有(long task)が無い」。
    /// DOM変化の静止待ちと違い操作可能性そのものを見るので、軽いページには待ち時間の下限がほぼ生じない。
    /// pushState/replaceState/popstateによるSPA内遷移(YouTube/Netflix等)でも同じ被いを出し直す。
    /// タブ切替との整合はDOM自体がタブに属するため自動的に取れる(ホスト側の状態管理が不要)。
    /// </summary>
    public const string PageCover = """
(() => {
  // 内部生成ページ(スタートページ/mpvプレースホルダー=NavigateToString)は一瞬で描画されるので出さない
  if (location.href === 'about:blank' || location.protocol === 'data:') return;
  const QUIET = 250, MAX = 8000;
  let lastBusy = 0, gen = 0, cover = null, pollId = 0;
  // 50ms超の主スレッド占有(long task)を「まだ操作できない」のシグナルとして使う
  try {
    new PerformanceObserver(() => { lastBusy = performance.now(); })
      .observe({ type: 'longtask', buffered: true });
  } catch (e) {}
  const FRAME1 =
    '<svg class="k1" viewBox="0 0 200 200"><defs>' +
    '<linearGradient id="k1o" x1="0" y1="1" x2="0" y2="0"><stop offset="0%" stop-color="#B3401F"/><stop offset="55%" stop-color="#E8672E"/><stop offset="100%" stop-color="#F7A93C"/></linearGradient>' +
    '<linearGradient id="k1i" x1="0" y1="1" x2="0" y2="0"><stop offset="0%" stop-color="#F5952E"/><stop offset="100%" stop-color="#FFDD8A"/></linearGradient></defs>' +
    '<g transform="translate(100,100) scale(1.385) translate(-101,-83)">' +
    '<path fill="url(#k1o)" d="M100,14 C128,44 154,66 154,104 C154,128 142,144 128,152 C134,138 132,120 118,108 C120,126 112,142 96,150 C70,146 48,126 48,100 C48,76 62,56 76,42 C72,58 74,74 86,84 C86,58 88,34 100,14 Z"/>' +
    '<path fill="url(#k1i)" d="M101,72 C112,86 122,98 121,116 C120,130 110,140 99,142 C88,140 78,129 78,115 C78,102 86,92 93,82 C94,92 97,98 102,102 C103,90 100,82 101,72 Z"/></g></svg>';
  const FRAME2 =
    '<svg class="k2" viewBox="0 0 200 200"><defs>' +
    '<linearGradient id="k2o" x1="0" y1="1" x2="0" y2="0"><stop offset="0%" stop-color="#B3401F"/><stop offset="55%" stop-color="#E8672E"/><stop offset="100%" stop-color="#F7A93C"/></linearGradient>' +
    '<linearGradient id="k2i" x1="0" y1="1" x2="0" y2="0"><stop offset="0%" stop-color="#F5952E"/><stop offset="100%" stop-color="#FFDD8A"/></linearGradient></defs>' +
    '<g transform="translate(100,100) scale(1.385) translate(-101,-83)">' +
    '<path fill="url(#k2o)" d="M100,2 C126,36 154,60 154,104 C154,128 142,144 128,152 C134,138 132,120 118,108 C120,126 112,142 96,150 C70,146 48,126 48,100 C48,76 62,56 76,42 C72,58 74,74 86,84 C86,50 88,22 100,2 Z"/>' +
    '<path fill="url(#k2i)" d="M101,62 C110,78 122,98 121,116 C120,130 110,140 99,142 C88,140 78,129 78,115 C78,102 86,92 93,82 C94,86 97,90 102,94 C103,78 100,70 101,62 Z"/></g></svg>';
  const show = () => {
    if (!cover) {
      cover = document.createElement('div');
      cover.id = '__karuLoad';
      cover.style.cssText = 'position:fixed;inset:0;z-index:2147483647;background:#111;' +
        'display:flex;align-items:center;justify-content:center';
      cover.innerHTML =
        '<style>#__karuLoad svg{position:absolute;inset:0;width:88px;height:88px;' +
        'animation:__karuFlick .36s steps(1) infinite!important}' +
        '#__karuLoad .k2{animation-delay:-.18s!important}' +
        '@keyframes __karuFlick{0%,100%{opacity:1}50%{opacity:0}}</style>' +
        '<div style="position:relative;width:88px;height:88px">' + FRAME1 + FRAME2 + '</div>';
    }
    // document-start時点では<html>(documentElement)がまだ無いことがある。そのままdocumentへ
    // appendするとカバー自体がドキュメント要素になり、本来のページ構築を壊して
    // 「カバーを外しても白紙・操作不能」になる → <html>が現れるまでマウントを遅らせる(pollで再試行)
    if (!cover.parentNode && document.documentElement)
      document.documentElement.appendChild(cover);
  };
  const hide = () => { if (cover) { cover.remove(); cover = null; } };
  const poll = (myGen, started) => {
    if (myGen !== gen) return;
    show(); // documentElementが遅れて現れたページでもここでマウントされる
    const now = performance.now();
    if (now - started >= MAX ||
        (document.readyState !== 'loading' && now - lastBusy >= QUIET)) { hide(); return; }
    pollId = setTimeout(() => poll(myGen, started), 100);
  };
  const begin = resetBusy => {
    gen++;
    if (resetBusy) lastBusy = performance.now(); // SPA遷移はクリック時点から計測をやり直す
    show();
    clearTimeout(pollId);
    poll(gen, performance.now());
  };
  begin(false); // 初回(本物のナビゲーション)。long taskが無い軽いページは即座に外れる

  // ---- SPA(pushState/replaceState/popstate)遷移。URLが実際に変わった時だけ被いを出し直す ----
  let lastHref = location.href;
  const onSoftNav = () => {
    if (location.href === lastHref) return;
    lastHref = location.href;
    begin(true);
  };
  for (const fn of ['pushState', 'replaceState']) {
    const orig = history[fn];
    history[fn] = function (...args) {
      const ret = orig.apply(this, args);
      onSoftNav();
      return ret;
    };
  }
  addEventListener('popstate', onSoftNav);
})();
""";

    /// <summary>
    /// 低スペックマシン偽装。deviceMemory/hardwareConcurrency/saveData を低く見せることで、
    /// 大手サイト(YouTube等)が自主的に軽量動作(プリバッファ削減・装飾簡略化)へ切り替わる。
    /// </summary>
    public const string LowSpec = """
(() => {
  const def = (o, k, v) => { try { Object.defineProperty(o, k, { get: () => v, configurable: true }); } catch (e) {} };
  def(Navigator.prototype, 'deviceMemory', 2);
  def(Navigator.prototype, 'hardwareConcurrency', 2);
  try {
    const c = navigator.connection;
    if (c) def(Object.getPrototypeOf(c), 'saveData', true);
  } catch (e) {}
})();
""";

    /// <summary>
    /// YouTube 向け注入スクリプト。広告スキップ(常時) + 集中モード + 画質上限 + 再生速度ボタン。
    /// WebView2 では拡張のコンテンツスクリプトが動かないため、ブラウザ側から注入する。
    /// 進捗同期を守るため本物のwatchページに留まり、制御はプレーヤー公式APIとCSS/JSの範囲で行う。
    /// 集中モードは document-start のCSSで初回描画前から隠しつつ(遅延ロードの発火自体を防ぐ)、
    /// DOM変化に強いようJS定期走査でも direct display:none を当てる二段構え。
    /// </summary>
    public static string YouTube(bool focusMode, string maxQuality) => $$"""
(() => {
  if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;
  window.__karuFocus = {{(focusMode ? "true" : "false")}};
  window.__karuMaxQ = '{{maxQuality}}';

  const HIDE = [
    '#secondary', '#related', 'ytd-watch-next-secondary-results-renderer',
    'ytd-comments#comments', '#comments',
    'ytd-merch-shelf-renderer', '#chat',
    'ytd-reel-shelf-renderer', 'ytd-rich-section-renderer',
    '.ytp-ce-element', '.ytp-cards-teaser'
  ];

  const addCss = () => {
    const css = document.createElement('style');
    css.textContent =
      '#player-ads,#masthead-ad,ytd-display-ad-renderer,ytd-in-feed-ad-layout-renderer,' +
      'ytd-ad-slot-renderer,ytd-companion-slot-renderer,.ytp-ad-overlay-container' +
      '{display:none!important}';
    (document.head || document.documentElement).appendChild(css);
    // 集中モード用CSS。初回描画前から効かせてコメント/関連動画の遅延ロードを発火させない。
    // トグルは style.disabled で行う(interval内でwindow.__karuFocusと同期)
    const fcss = document.createElement('style');
    fcss.id = 'karu-focus';
    fcss.textContent = HIDE.join(',') + '{display:none!important}'
      // 集中モード中はUIアニメーション/トランジションも止めてGPU/CPUを節約 (OYO由来の手法)
      + '*,*::before,*::after{animation:none!important;transition:none!important}';
    (document.head || document.documentElement).appendChild(fcss);
    fcss.disabled = !window.__karuFocus;
  };
  if (document.documentElement) addCss();
  else addEventListener('DOMContentLoaded', addCss);

  // --- 画質上限: 本物のプレーヤーAPI(setPlaybackQualityRange)経由なので進捗同期に影響しない ---
  const Q = ['tiny','small','medium','large','hd720','hd1080','hd1440','hd2160','hd2880','highres'];
  const capQuality = () => {
    const q = window.__karuMaxQ;
    if (!q) return;
    const mp = document.getElementById('movie_player');
    if (!mp || !mp.getPlaybackQuality || !mp.setPlaybackQualityRange) return;
    try {
      // 上限を超えているときだけ介入。上限未満はYouTubeのauto判断に任せる
      if (Q.indexOf(mp.getPlaybackQuality()) > Q.indexOf(q)) mp.setPlaybackQualityRange(q, q);
    } catch (e) {}
  };

  // --- 再生速度ボタン (プレーヤーのコントロールバー右側に常駐。embedにも出る) ---
  const SPEEDS = [1, 1.25, 1.5, 1.75, 2, 2.5, 3];
  const fmtRate = r => (Math.round(r * 100) / 100 + '').replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '') + 'x';
  const liveVideo = () => document.querySelector('video');
  const ensureSpeedButton = () => {
    if (document.querySelector('.karu-speed')) return;
    const bar = document.querySelector('.ytp-right-controls');
    if (!bar || !liveVideo()) return;
    const btn = document.createElement('button');
    btn.className = 'ytp-button karu-speed';
    btn.style.cssText = 'font:600 13px "Segoe UI",sans-serif;vertical-align:top';
    btn.textContent = fmtRate(liveVideo().playbackRate || 1);
    btn.title = '再生速度 — クリック: 切替 / 右クリック: 1x / ホイール: ±0.25';
    btn.addEventListener('click', e => {
      e.stopPropagation();
      const v = liveVideo(); if (!v) return;
      v.playbackRate = SPEEDS.find(s => s > v.playbackRate + 0.01) ?? SPEEDS[0];
    });
    btn.addEventListener('contextmenu', e => {
      e.preventDefault(); e.stopPropagation();
      const v = liveVideo(); if (v) v.playbackRate = 1;
    });
    btn.addEventListener('wheel', e => {
      e.preventDefault(); e.stopPropagation();
      const v = liveVideo(); if (!v) return;
      const d = e.deltaY < 0 ? 0.25 : -0.25;
      v.playbackRate = Math.min(3, Math.max(0.25, Math.round((v.playbackRate + d) * 100) / 100));
    }, { passive: false });
    bar.insertBefore(btn, bar.firstChild);
  };

  let hid = false; // 集中モードで実際に要素を隠しているか（無駄な復元走査を避ける）
  setInterval(() => {
    // --- 広告スキップ (バックグラウンド音声再生中も動かす) ---
    const p = document.querySelector('.html5-video-player');
    if (p && p.classList.contains('ad-showing')) {
      const v = p.querySelector('video');
      if (v && isFinite(v.duration) && v.duration > 0) {
        v.muted = true;
        v.currentTime = v.duration;
      }
      const skip = p.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern');
      if (skip) skip.click();
    }
    // --- ここから表示中タブのみ (非表示タブではDOM走査を止めてCPUを節約) ---
    if (document.hidden) return;
    capQuality();
    // 速度ボタンの設置とラベル同期 (SPAナビやプレーヤー再構築で消えたら生やし直す)
    ensureSpeedButton();
    {
      const sb = document.querySelector('.karu-speed');
      const v = liveVideo();
      if (sb && v) {
        const label = fmtRate(v.playbackRate);
        if (sb.textContent !== label) sb.textContent = label;
      }
    }
    // 集中モードCSSのトグル同期
    const fs = document.getElementById('karu-focus');
    if (fs) fs.disabled = !window.__karuFocus;
    if (window.__karuFocus) {
      // ライブチャットのiframeは display:none でも中で動き続けるため、これだけは本当に消す
      // (集中モードOFFに戻してもチャットは再読み込みまで復活しない点は許容)
      const chat = document.querySelector('iframe#chatframe');
      if (chat) chat.remove();
      for (const sel of HIDE)
        for (const el of document.querySelectorAll(sel))
          if (el.dataset.karuHidden !== '1') {
            el.dataset.karuHidden = '1';
            el.style.setProperty('display', 'none', 'important');
          }
      hid = true;
    } else if (hid) {
      for (const el of document.querySelectorAll('[data-karu-hidden="1"]')) {
        el.style.removeProperty('display');
        el.dataset.karuHidden = '0';
      }
      hid = false;
    }
  }, 500);
})();
""";

    public const string ToggleFocusOn = "window.__karuFocus = true";
    public const string ToggleFocusOff = "window.__karuFocus = false";

    /// <summary>
    /// メディアポリシー注入 (h264ify方式の拡張)。コーデック検出に使われる3つのAPI
    /// (MediaSource.isTypeSupported / canPlayType / mediaCapabilities.decodingInfo) すべてに
    /// 同じポリシーを適用する:
    ///   - forceH264: VP8/VP9/AV1を「非対応」と答えてH.264を選ばせる (全GPUでハードウェアデコード可)
    ///   - maxFps: framerateがこれを超える形式を「非対応」と答える (1080p60→30でCPU2〜4倍削減の報告)
    /// YouTubeは高解像度時に decodingInfo() でAV1を選ぶため、isTypeSupportedだけでは穴が残る。
    /// ページ読み込み前(document-start)に実行される必要がある。
    /// </summary>
    public static string MediaPolicy(bool forceH264, int maxFps) => $$"""
(() => {
  if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;
  const blockCodec = {{(forceH264 ? "true" : "false")}};
  const maxFps = {{maxFps}};
  const badCodec = t => blockCodec && typeof t === 'string' && /vp0?8|vp0?9|av01/i.test(t);
  const badFps = t => {
    if (maxFps <= 0 || typeof t !== 'string') return false;
    const m = t.match(/framerate=(\d+)/);
    return !!m && parseInt(m[1], 10) > maxFps;
  };
  try {
    const orig = MediaSource.isTypeSupported.bind(MediaSource);
    Object.defineProperty(MediaSource, 'isTypeSupported', {
      value: t => (badCodec(t) || badFps(t)) ? false : orig(t),
      configurable: true,
    });
  } catch (e) {}
  try {
    const orig = HTMLMediaElement.prototype.canPlayType;
    HTMLMediaElement.prototype.canPlayType = function (t) {
      return (badCodec(t) || badFps(t)) ? '' : orig.call(this, t);
    };
  } catch (e) {}
  try {
    const mc = navigator.mediaCapabilities;
    if (mc && mc.decodingInfo) {
      const orig = mc.decodingInfo.bind(mc);
      mc.decodingInfo = cfg => {
        try {
          const v = cfg && cfg.video;
          if (v && (badCodec(v.contentType) || (maxFps > 0 && v.framerate > maxFps)))
            return Promise.resolve({ supported: false, smooth: false, powerEfficient: false });
        } catch (e) {}
        return orig(cfg);
      };
    }
  } catch (e) {}
})();
""";

    /// <summary>
    /// Twitchの既定画質を抑える (localStorage注入・userscript実績のある方式)。
    /// Twitchのtranscodeは source(1080p60)/720p60/480p30/... の階段のため、
    /// 画質上限ONのときは source を避けて 720p60 を既定にする。
    /// ユーザーがプレーヤーで手動変更した場合はそのセッション中は尊重される(次回読み込みで再適用)。
    /// キー名変更等で効かなくなっても害はない。
    /// </summary>
    public const string TwitchQuality = """
(() => {
  if (!/(^|\.)twitch\.tv$/.test(location.hostname)) return;
  try { localStorage.setItem('video-quality', '{"default":"720p60"}'); } catch (e) {}
})();
""";

    /// <summary>ブックマーク一覧オーバーレイ。{0} に JSON 配列 [{title,url},...] を埋め込む。
    /// キー=直接開く / j·k(↑↓)=選択移動 / Enter=選択を開く / Shift併用=新しいタブ。</summary>
    public static string BookmarkOverlay => BookmarkOverlaySrc.Replace("__KARU_MSG_TOKEN__", MessageToken);

    const string BookmarkOverlaySrc = """
(function(){
  if(document.getElementById('__karuBM')) return;
  var bm = {0};
  if(!bm.length){ return; }
  var keys = '1234567890acdefghilmnopqrstuvwyz'.split(''); // j/k は選択移動、b は閉じるキーに予約
  var bg = document.createElement('div');
  bg.id = '__karuBM';
  bg.style.cssText = 'position:fixed;inset:0;z-index:2147483647;background:rgba(0,0,0,.6);' +
    'display:flex;align-items:center;justify-content:center;backdrop-filter:blur(4px)';
  var box = document.createElement('div');
  box.style.cssText = 'background:rgba(17,17,17,.97);border:1px solid #333;border-radius:12px;' +
    'padding:16px 20px;max-width:480px;width:90vw;max-height:70vh;overflow-y:auto;' +
    'font:14px "Segoe UI","Yu Gothic UI",sans-serif;color:#ddd;box-shadow:0 8px 32px rgba(0,0,0,.5)';
  var hdr = document.createElement('div');
  hdr.style.cssText = 'display:flex;justify-content:space-between;align-items:baseline;margin-bottom:10px';
  var ttl = document.createElement('span');
  ttl.textContent = 'Bookmarks';
  ttl.style.cssText = 'font-size:11px;color:#D4845E;letter-spacing:2px;text-transform:uppercase';
  var hint = document.createElement('span');
  hint.textContent = 'j/k: 選択 · Enter: 開く · Shift: 新しいタブ · b/Esc: 閉じる';
  hint.style.cssText = 'font-size:11px;color:#777';
  hdr.appendChild(ttl);
  hdr.appendChild(hint);
  box.appendChild(hdr);
  var map = {};
  var rows = [];
  var sel = 0;
  function setSel(i){
    sel = (i + rows.length) % rows.length;
    for(var r=0;r<rows.length;r++) rows[r].style.background = r===sel ? '#2A2A2A' : 'transparent';
    if(rows[sel].scrollIntoView) rows[sel].scrollIntoView({block:'nearest'});
  }
  bm.forEach(function(b,i){
    var k = i < keys.length ? keys[i] : '';
    if(k) map[k] = b.url;
    var row = document.createElement('div');
    row.style.cssText = 'display:flex;align-items:center;gap:10px;padding:7px 8px;border-radius:6px;cursor:pointer';
    row.onmouseenter = function(){ setSel(i); };
    row.onclick = function(ev){ go(b.url, ev.shiftKey); };
    var badge = document.createElement('span');
    badge.textContent = k;
    badge.style.cssText = 'display:inline-block;width:20px;height:20px;line-height:20px;text-align:center;' +
      'font:bold 12px Consolas,monospace;color:#D4845E;border:1px solid #D4845E;border-radius:4px;flex-shrink:0';
    if(!k) badge.style.visibility = 'hidden'; // キーがあふれた行はj/k選択とマウスで開く
    var title = document.createElement('span');
    title.textContent = b.title;
    title.style.cssText = 'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1';
    row.appendChild(badge);
    row.appendChild(title);
    rows.push(row);
    box.appendChild(row);
  });
  bg.appendChild(box);
  document.documentElement.appendChild(bg);
  setSel(0);
  function close(){ bg.remove(); document.removeEventListener('keydown',onKey,true); }
  function go(url, newTab){
    close();
    try{window.chrome.webview.postMessage('__KARU_MSG_TOKEN__|'+(newTab?'bookmarkGoNew:':'bookmarkGo:')+url);}catch(e){}
  }
  function onKey(e){
    e.preventDefault(); e.stopImmediatePropagation();
    if(e.key==='Escape' || e.code==='KeyB'){ close(); return; } // bで開いた一覧はbで閉じる(トグル)
    if(e.key==='Enter'){ go(bm[sel].url, e.shiftKey); return; }
    // Shift押下時も動くよう、文字キーは物理キー(e.code)で判定する
    if(e.key==='ArrowDown' || e.code==='KeyJ'){ setSel(sel+1); return; }
    if(e.key==='ArrowUp' || e.code==='KeyK'){ setSel(sel-1); return; }
    var k = '';
    if(/^Digit\d$/.test(e.code)) k = e.code.slice(5);
    else if(/^Key[A-Z]$/.test(e.code)) k = e.code.slice(3).toLowerCase();
    else if(e.key.length===1) k = e.key.toLowerCase();
    if(map[k]) go(map[k], e.shiftKey);
  }
  document.addEventListener('keydown',onKey,true);
  bg.addEventListener('click',function(e){ if(e.target===bg) close(); });
})()
""";

    /// <summary>
    /// Vimium風キーボード操作層。全ページに注入。
    /// j/k/d/u/gg/G スクロール、f/F リンクヒント、H/L 履歴、yy URLコピー、? ヘルプ。
    /// タブ操作(x/X/o/J/K)は chrome.webview.postMessage 経由でアプリ側が実行する。
    /// YouTubeのプレーヤーショートカット(j/l シーク・k 再生停止・f フルスクリーン・t シアターモード)とキーが
    /// 重複するが、動画を見ていないとき(検索結果・チャンネルページ等)もKaruの操作性を優先するため、
    /// YouTube上でも常にKaru側が優先する(意図的な選択。無効化はしない)。
    /// </summary>
    public static string Vim => VimSrc.Replace("__KARU_MSG_TOKEN__", MessageToken);

    const string VimSrc = """
(() => {
  if (window.__karuVim) return;
  window.__karuVim = 1;

  // document-start時点の postMessage を閉包に捕獲 (ページによる後からのフック/すり替えを無効化)
  const send = (() => {
    try {
      const f = window.chrome.webview.postMessage.bind(window.chrome.webview);
      return c => { try { f('__KARU_MSG_TOKEN__|' + c); } catch (e) {} };
    } catch (e) { return () => {}; }
  })();
  const SCROLL = 80;
  let hint = null, hintBox = null, lastKey = '', lastT = 0, helpBox = null;

  const inInput = e => {
    const t = e.composedPath ? e.composedPath()[0] : e.target;
    return !!(t && t.tagName && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)));
  };

  const labelsFor = n => {
    const A = 'asdfghjkl'.split('');
    if (n <= A.length) return A.slice(0, n);
    const out = [];
    for (const x of A) { for (const y of A) { out.push(x + y); if (out.length >= n) return out; } }
    return out;
  };

  const clickables = () => {
    const out = [];
    const els = document.querySelectorAll(
    'a[href],button,input:not([type=hidden]),select,textarea,summary,[onclick],[role=button],[role=link],[role=tab],[contenteditable=true]');
    for (const el of els) {
      const r = el.getBoundingClientRect();
      if (r.width < 3 || r.height < 3) continue;
      if (r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) continue;
      const st = getComputedStyle(el);
      if (st.visibility === 'hidden' || st.opacity === '0') continue;
      out.push([el, r]);
      if (out.length >= 81) break;
    }
    return out;
  };

  const stopHints = () => { if (hintBox) hintBox.remove(); hintBox = null; hint = null; };

  const startHints = newTab => {
    stopHints();
    const els = clickables();
    if (!els.length) return;
    const labs = labelsFor(els.length);
    hintBox = document.createElement('div');
    hintBox.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;pointer-events:none';
    hint = { map: new Map(), typed: '', newTab, shifted: false };
    els.forEach((pair, i) => {
      const lab = labs[i];
      if (!lab) return;
      const d = document.createElement('div');
      d.textContent = lab.toUpperCase();
      d.style.cssText = 'position:fixed;left:' + Math.max(0, pair[1].left - 2) + 'px;top:' + Math.max(0, pair[1].top - 2) +
        'px;background:#D4845E;color:#1b0f08;font:bold 12px Consolas,monospace;padding:1px 4px;border-radius:3px;' +
        'border:1px solid #8f4a2e;box-shadow:0 1px 3px rgba(0,0,0,.45)';
      hintBox.appendChild(d);
      hint.map.set(lab, { el: pair[0], d });
    });
    document.documentElement.appendChild(hintBox);
  };

  const activate = (el, newTab) => {
    if (newTab && el.tagName === 'A' && el.href) { window.open(el.href, '_blank'); return; }
    if (el.focus) el.focus();
    if (el.click) el.click();
  };

  const hintKey = e => {
    if (e.key === 'Escape') { stopHints(); return; }
    if (e.key === 'Backspace') {
      hint.typed = hint.typed.slice(0, -1);
      for (const [lab, v] of hint.map) v.d.style.display = lab.startsWith(hint.typed) ? '' : 'none';
      return;
    }
    if (!/^[a-zA-Z]$/.test(e.key)) return;
    hint.typed += e.key.toLowerCase();
    if (e.shiftKey) hint.shifted = true; // ヒント選択中にShiftが押されたら新しいタブで開く
    const hit = hint.map.get(hint.typed);
    if (hit) { const el = hit.el, nt = hint.newTab || hint.shifted; stopHints(); activate(el, nt); return; }
    let alive = 0;
    for (const [lab, v] of hint.map) {
      const ok = lab.startsWith(hint.typed);
      v.d.style.display = ok ? '' : 'none';
      if (ok) alive++;
    }
    if (!alive) stopHints();
  };

  const HELP =
    'j / k : スクロール\nd / u : 半ページ\ngg / G : 先頭 / 末尾\nh / l : 横スクロール\n' +
    'f / F : リンク選択 (Fまたは選択中Shiftで新タブ)\nH / L : 戻る / 進む\nJ / K : 前 / 次のタブ\n' +
    't : 新しいタブ\nx / X : タブを閉じる / 復元\no : URL / 検索\n' +
    'Ctrl+Tab : タブ一覧 (j/k+Enter · Ctrl+Wで閉じる)\nr : 再読み込み\n' +
    '> / < : 再生速度 ±0.25\n= : 等速に戻す\n' +
    'yy : URLコピー\nb : お気に入り一覧 (j/k+Enter · Shiftで新タブ · もう一度bで閉じる)\n? : このヘルプ\n\n' +
    'Ctrl+B 動画集中モード · Ctrl+O 動画フルスクリーン · Ctrl+Shift+W 終了';

  // ---- 動画の再生速度 (embedプレーヤーには倍速UIが無いため自前で提供。全サイトの<video>に効く) ----
  let toast = null, toastTimer = 0;
  const showToast = txt => {
    if (!toast) {
      toast = document.createElement('div');
      toast.style.cssText = 'position:fixed;top:18px;right:18px;z-index:2147483647;' +
        'background:rgba(17,17,17,.92);color:#e8dfd3;font:600 14px Consolas,monospace;' +
        'padding:8px 14px;border-radius:8px;border:1px solid #D4845E;pointer-events:none;' +
        'opacity:0;transition:opacity .15s';
      document.documentElement.appendChild(toast);
    }
    toast.textContent = txt;
    toast.style.opacity = '1';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toast.style.opacity = '0'; }, 1200);
  };
  const setSpeed = d => {
    const v = document.querySelector('video');
    if (!v) return;
    let r = d === 0 ? 1 : Math.round((v.playbackRate + d) * 100) / 100;
    r = Math.min(3, Math.max(0.25, r));
    v.playbackRate = r;
    showToast('▶ ' + (r + '').replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '') + 'x');
  };

  const toggleHelp = () => {
    if (helpBox) { helpBox.remove(); helpBox = null; return; }
    helpBox = document.createElement('div');
    helpBox.textContent = HELP;
    helpBox.style.cssText = 'position:fixed;right:16px;bottom:16px;z-index:2147483647;background:rgba(17,17,17,.96);' +
      'color:#ddd;font:13px/1.7 Consolas,monospace;padding:14px 18px;border-radius:10px;border:1px solid #333;' +
      'white-space:pre;box-shadow:0 4px 20px rgba(0,0,0,.6)';
    document.documentElement.appendChild(helpBox);
  };

  const block = e => { e.preventDefault(); e.stopImmediatePropagation(); };

  const act = {
    j: () => scrollBy(0, SCROLL), k: () => scrollBy(0, -SCROLL),
    h: () => scrollBy(-SCROLL, 0), l: () => scrollBy(SCROLL, 0),
    d: () => scrollBy(0, innerHeight / 2), u: () => scrollBy(0, -innerHeight / 2),
    G: () => scrollTo(0, (document.scrollingElement || document.documentElement).scrollHeight),
    H: () => history.back(), L: () => history.forward(),
    r: () => location.reload(),
    f: () => startHints(false), F: () => startHints(true),
    b: () => send('bookmarkList'),
    x: () => send('tabClose'), X: () => send('tabRestore'),
    t: () => send('tabNew'), o: () => send('focusUrl'),
    J: () => send('tabPrev'), K: () => send('tabNext'),
    '>': () => setSpeed(0.25), '<': () => setSpeed(-0.25),
    '=': () => setSpeed(0),
    '?': () => toggleHelp(),
  };

  addEventListener('keydown', e => {
    if (e.isComposing || e.keyCode === 229) return;
    // ブックマーク一覧オーバーレイ表示中は全キーをそちらに任せる
    // (このハンドラはwindowキャプチャで先に走るため、譲らないとj/k等を横取りしてしまう)
    if (document.getElementById('__karuBM')) return;
    if (hint) { block(e); hintKey(e); return; }
    if (inInput(e)) return;
    // Ctrl+Tab: タブ一覧 (ホスト側でも処理するが、ページ内フォーカス移動を確実に抑止するため二重化)。
    // 素のShift+Tabはここでは一切処理せず、ページ標準の逆順フォーカス移動に渡す。
    if (e.key === 'Tab' && e.ctrlKey) { block(e); send('tabList'); return; }
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    const k = e.key;
    const now = Date.now();
    const combo = (now - lastT < 600 ? lastKey : '') + k;
    lastKey = k; lastT = now;
    if (combo === 'gg') { block(e); scrollTo(0, 0); lastKey = ''; return; }
    if (combo === 'yy') {
      block(e);
      if (navigator.clipboard) navigator.clipboard.writeText(location.href).catch(() => {});
      lastKey = '';
      return;
    }
    if (k === 'Escape' && helpBox) { block(e); toggleHelp(); return; }
    const fn = act[k];
    if (fn) { block(e); fn(); }
  }, true);
})();
""";
}
