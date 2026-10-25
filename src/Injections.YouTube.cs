namespace Karu;

static partial class Injections
{
    /// <summary>
    /// YouTube 向け注入スクリプト。広告スキップ(常時) + 集中モード + 画質上限 + 再生速度ボタン。
    /// WebView2 では拡張のコンテンツスクリプトが動かないため、ブラウザ側から注入する。
    /// 進捗同期を守るため本物のwatchページに留まり、制御はプレーヤー公式APIとCSS/JSの範囲で行う。
    /// 集中モードは document-start のCSSで初回描画前から隠しつつ(遅延ロードの発火自体を防ぐ)、
    /// DOM変化に強いようJS定期走査でも direct display:none を当てる二段構え。
    ///
    /// 広告スキップの前提(2026-10時点の base.js 解析とその裏取り):
    ///   - スキップボタンの onClick は isTrusted=false のイベントを「広告ブロッカー」として記録したうえで
    ///     スキップ処理を実行しない。ページ内JSの click()/dispatchEvent では越えられないため、見えている
    ///     ボタンの座標だけをホストへ送り、ホストが CDP(Input.dispatchMouseEvent)で本物の入力を打つ
    ///     (<see cref="YouTubeAdSkip"/>)。
    ///   - サイズ0・opacity 0 のおとり .ytp-ad-skip-button が置かれ、押すと記録される。可視判定は必須。
    ///   - 広告中の seeking は無条件に検知として報告される("a.s_")。しかも再生が始まる前に末尾へ飛ぶと、
    ///     広告の読み込みタイムアウト(10秒)が切れるまで本編へ進まない(2026-10-12 実測: 10本中5本が10秒超)。
    ///     だからシークはしない。
    ///   - 広告の再生位置が実時間の2.5倍より速く進むと検知として報告され("n.k_")、プリロールでは
    ///     onAbnormalityDetected まで発火する。実時間は再生開始(onAdStart)から数えるので、最初の0.3秒だけ等速で
    ///     流してから2.4倍にする(adSpeedUp=false なら等速)。
    ///   - 広告の間はプレーヤーをKaruのロゴの被いで隠す(adCover=false で出さない)。被いはクリックで外せる。
    /// </summary>
    public static string YouTube(bool focusMode, string maxQuality, bool adSpeedUp, bool adCover) => $$"""
(() => {
  if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;
  window.__karuFocus = {{JsBool(focusMode)}};
  window.__karuMaxQ = {{System.Text.Json.JsonSerializer.Serialize(maxQuality)}};
  const AD_SPEED_UP = {{JsBool(adSpeedUp)}};
  const AD_COVER = {{JsBool(adCover)}};

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
      // id系はアプリルート(ytd-app)配下に限定する。YouTubeは同名のclassを持つおとり要素をbody直下に
      // 置き、非表示にされたら広告ブロッカーとして記録するため、無差別な #player-ads 等は踏まない
      'ytd-app #player-ads,ytd-app #masthead-ad,ytd-display-ad-renderer,ytd-in-feed-ad-layout-renderer,' +
      'ytd-ad-slot-renderer,ytd-companion-slot-renderer,.html5-video-player .ytp-ad-overlay-container' +
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

  // --- 広告スキップ ---
  // 合成クリックは効かない: スキップボタンは isTrusted=false の click を広告ブロッカーとして記録したうえで
  // スキップを実行しない。加えて不可視(サイズ0・opacity 0)のおとり .ytp-ad-skip-button があり、押すと
  // それも記録される。そのためページ内では click() せず、「見えている」ボタンの中心座標だけをホストへ送り、
  // ホストが CDP の Input.dispatchMouseEvent でユーザー操作と区別のつかないクリックを打つ。
  // document-start時点の postMessage を閉包に捕獲する理由はVim層と同じ(ページ側のフック/すり替え対策)。
  const send = (() => {
    try {
      const f = window.chrome.webview.postMessage.bind(window.chrome.webview);
      return c => { try { f('{{MessageToken}}|' + c); } catch (e) {} };
    } catch (e) { return () => {}; }
  })();
  // 部分一致で拾う: ytp-skip-ad-button(現行) / ytp-ad-skip-button(-modern, -container) / id=skip-button:N
  const SKIP_SEL = '[class*="ytp-skip-ad-button"],[class*="ytp-ad-skip-button"],.ytp-skip-ad button,[id^="skip-button"]';
  const visibleSkip = p => {
    for (const el of p.querySelectorAll(SKIP_SEL)) {
      const b = el.closest('button') || el;
      if (b.disabled) continue;
      const r = b.getBoundingClientRect();
      if (r.width < 8 || r.height < 8) continue;
      if (r.right <= 0 || r.bottom <= 0 || r.left >= innerWidth || r.top >= innerHeight) continue;
      const st = getComputedStyle(b);
      if (st.visibility === 'hidden' || st.pointerEvents === 'none' || parseFloat(st.opacity) < 0.5) continue;
      // 中心点に本当にそのボタンがある(他要素に覆われていない)ことを確認してから渡す。
      // ホスト側のクリックは本物なので、ずれて広告本体を押すと広告主サイトが開いてしまう
      const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
      const hit = document.elementFromPoint(cx, cy);
      if (!hit || (hit !== b && !b.contains(hit))) continue;
      return { x: Math.round(cx), y: Math.round(cy) };
    }
    return null;
  };
  // 広告中の再生速度と、その前に等速で流す長さ(再生位置の秒)。プレーヤーは広告の再生開始からの実時間が
  // 再生位置×0.4 を下回ると(=2.5倍超)検知する。開始時刻を記録するのは再生が始まった後なので、出だしから
  // 2.4倍だと記録が0.08秒遅れただけで割り込む。最初の0.3秒を等速にして余裕を0.25秒以上取る(上の注記参照)
  const AD_RATE = 2.4, AD_LEAD = 0.3;
  // 広告の状態。key は今流れている広告メディア(src と長さ)で、連続広告では1本ごとにクリック回数と等速区間を
  // 数え直す(t0 はその広告を最初に見た時の再生位置)。contentRate は広告でない間の再生速度、setRate は
  // 広告中に自分で入れた速度で、広告用の速度を本編へ持ち越さないよう終了時に戻すのに使う
  const ad = { on: false, muted: false, sped: false, clicks: 0, lastClick: 0, key: '', t0: 0, setRate: 1, contentRate: 1 };
  // 広告中の被い。広告の映像をKaruのロゴで隠す。プレーヤーの中に置くので全画面・シアター・ミニプレーヤーにも付いていく。
  // YouTube は Trusted Types で innerHTML を禁じているので、ロゴは data: URL の背景画像、文字は textContent で作る。
  // 被いへのクリックはプレーヤーへ渡さない(再生/停止や広告主ページを開くクリックにしない)。クリックされたら
  // その広告の間だけ外す(自動スキップが効かないときに自分で押せるように)
  const LOGO = 'url("data:image/svg+xml,' + encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200"><defs>' +
    '<linearGradient id="o" x1="0" y1="1" x2="0" y2="0"><stop offset="0%" stop-color="#B3401F"/><stop offset="55%" stop-color="#E8672E"/><stop offset="100%" stop-color="#F7A93C"/></linearGradient>' +
    '<linearGradient id="i" x1="0" y1="1" x2="0" y2="0"><stop offset="0%" stop-color="#F5952E"/><stop offset="100%" stop-color="#FFDD8A"/></linearGradient></defs>' +
    '<g transform="translate(100,100) scale(1.385) translate(-101,-83)">' +
    '<path fill="url(#o)" d="M100,14 C128,44 154,66 154,104 C154,128 142,144 128,152 C134,138 132,120 118,108 C120,126 112,142 96,150 C70,146 48,126 48,100 C48,76 62,56 76,42 C72,58 74,74 86,84 C86,58 88,34 100,14 Z"/>' +
    '<path fill="url(#i)" d="M101,72 C112,86 122,98 121,116 C120,130 110,140 99,142 C88,140 78,129 78,115 C78,102 86,92 93,82 C94,92 97,98 102,102 C103,90 100,82 101,72 Z"/></g></svg>') + '")';
  const cover = { el: null, off: false, passTimer: 0 };
  const showCover = p => {
    if (!AD_COVER || cover.off) return;
    if (!cover.el) {
      const el = document.createElement('div');
      el.id = 'karu-ad-cover';
      el.title = 'クリックでこの広告を表示';
      el.style.cssText = 'position:absolute;inset:0;z-index:2147483000;background:#0f0f0f;cursor:pointer;' +
        'display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;' +
        'color:#9b8f82;font:600 13px "Segoe UI",sans-serif;letter-spacing:.05em';
      const logo = document.createElement('div');
      logo.style.cssText = 'width:72px;height:72px;background:center/contain no-repeat ' + LOGO;
      const label = document.createElement('div');
      label.textContent = '広告をスキップしています';
      el.append(logo, label);
      for (const t of ['pointerdown', 'mousedown', 'mouseup', 'dblclick']) el.addEventListener(t, e => e.stopPropagation());
      el.addEventListener('click', e => { e.stopPropagation(); cover.off = true; hideCover(); });
      cover.el = el;
    }
    if (cover.el.parentNode !== p) p.appendChild(cover.el);
  };
  const hideCover = () => { if (cover.el) cover.el.remove(); };
  // 被いの下のスキップボタンへ本物のクリックを通すため、クリックが届くまでの間だけ被いを素通りにする
  const passCover = ms => {
    if (!cover.el) return;
    cover.el.style.pointerEvents = 'none';
    clearTimeout(cover.passTimer);
    cover.passTimer = setTimeout(() => { cover.passTimer = 0; if (cover.el) cover.el.style.pointerEvents = ''; }, ms);
  };
  // 本編のプレーヤー。トップページでは masthead 広告用の .html5-video-player が先に並ぶので id で取る
  const mainPlayer = () => document.getElementById('movie_player') || document.querySelector('.html5-video-player');
  let adPollId = 0;
  const syncAdPolling = () => {
    if (ad.on && !adPollId) adPollId = setInterval(adTick, 100);
    else if (!ad.on && adPollId) { clearInterval(adPollId); adPollId = 0; }
  };
  const adTick = () => {
    const p = mainPlayer();
    const v = p && p.querySelector('video');
    const showing = !!p && (p.classList.contains('ad-showing') || p.classList.contains('ad-interrupting'));
    if (!showing) {
      if (ad.on) {
        ad.on = false;
        // 自分で掛けたミュートだけ戻す(広告前からのミュート/プレーヤー自身のミュートは触らない)
        if (ad.muted && v && v.muted) {
          let pm = false; try { pm = !!(p.isMuted && p.isMuted()); } catch (e) {}
          if (!pm) v.muted = false;
        }
        // 広告用の速度が残っていれば広告前の速度へ戻す。プレーヤーが本編の速度を入れ直していれば触らない。
        // 終了直後の tick はメディア切替中のことがあるので contentRate はここでは取り直さない
        if (ad.sped && v && v.playbackRate === ad.setRate) v.playbackRate = ad.contentRate;
        ad.muted = ad.sped = false;
        hideCover();
        cover.off = false;
        syncAdPolling();
        return;
      }
      if (v) ad.contentRate = v.playbackRate;
      return;
    }
    if (!ad.on) { ad.on = true; ad.key = ''; }
    showCover(p);
    if (v) {
      const key = v.src + '|' + v.duration;
      if (key !== ad.key) { ad.key = key; ad.clicks = 0; ad.lastClick = 0; ad.t0 = v.currentTime; }
      if (!v.muted) { v.muted = true; ad.muted = true; }
      if (AD_SPEED_UP) {
        const rate = v.currentTime - ad.t0 >= AD_LEAD ? AD_RATE : 1;
        if (v.playbackRate !== rate) { v.playbackRate = rate; ad.sped = true; ad.setRate = rate; }
      }
    }
    // スキップボタンが見えたらホストに本物のクリックを頼む(広告1本あたり最大8回・0.7秒間隔)。
    // 被いは自分のものなので、ヒット判定では素通りさせて「被いの下で見えているボタン」を探す
    const passing = cover.passTimer !== 0;
    if (cover.el) cover.el.style.pointerEvents = 'none';
    const s = visibleSkip(p);
    if (cover.el && !passing) cover.el.style.pointerEvents = '';
    const now = Date.now();
    if (s && ad.clicks < 8 && now - ad.lastClick > 700) {
      ad.clicks++; ad.lastClick = now;
      passCover(1500);
      send('adSkip|' + s.x + '|' + s.y);
    }
    syncAdPolling();
  };
  // 広告の始まりはプレーヤーの class の変化で即座に拾い(被いと消音を最初から効かせる)、広告中は0.1秒ごとに
  // 見直す(スキップボタンが出た瞬間に押し、等速区間の終わりですぐ速度を上げる)。広告が無い間は下の0.5秒ループだけ
  const classWatch = new MutationObserver(adTick);
  let watched = null;
  const watchPlayer = () => {
    const p = mainPlayer();
    if (!p || p === watched) return;
    classWatch.disconnect();
    classWatch.observe(p, { attributes: true, attributeFilter: ['class'] });
    watched = p;
  };
  let hid = false; // 集中モードで実際に要素を隠しているか（無駄な復元走査を避ける）
  setInterval(() => {
    // --- 広告スキップ (バックグラウンド音声再生中も動かす) ---
    watchPlayer();
    adTick();
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
      // 新しく追加される要素は上のCSSで隠れる。direct styleの全DOM走査は
      // 集中モードに入った最初の1回だけ行い、0.5秒ごとの12 selector走査を避ける。
      if (!hid) {
        for (const sel of HIDE)
          for (const el of document.querySelectorAll(sel)) {
            el.dataset.karuHidden = '1';
            el.style.setProperty('display', 'none', 'important');
          }
        hid = true;
      }
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
}
