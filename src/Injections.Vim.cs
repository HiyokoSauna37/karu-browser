namespace Karu;

static partial class Injections
{
    /// <summary>
    /// Vimium風キーボード操作層。全ページに注入。
    /// j/k/d/u/gg/G スクロール、f/F リンクヒント、H/L 履歴、yy URLコピー、? ヘルプ。
    /// タブ操作(x/X/o/J/K)は chrome.webview.postMessage 経由でアプリ側が実行する。
    /// YouTubeのプレーヤーショートカット(j/l シーク・k 再生停止・f フルスクリーン・t シアターモード)とキーが
    /// 重複するが、動画を見ていないとき(検索結果・チャンネルページ等)もKaruの操作性を優先するため、
    /// YouTube上でも常にKaru側が優先する(意図的な選択。無効化はしない)。
    /// </summary>
    public static readonly string Vim;

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
    'yy : URLコピー\nb : お気に入り一覧 (j/k+Enter · Shiftで新タブ · もう一度bで閉じる)\n' +
    'Ctrl+D : お気に入りに登録 / 解除\n? : このヘルプ\n\n' +
    'Ctrl+Shift+Y 翻訳⇄原文 · Ctrl+Shift+D タブを別ウィンドウへ分離\n' +
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

  // ---- スクロール対象の解決 ----
  // Twitch等のSPAは html/body を overflow:hidden にして内側のdivをスクロールさせるため、
  // window.scrollBy() では1pxも動かない。実際にスクロールを持つ要素を探して直接動かす。
  const OVF = /^(auto|scroll|overlay)$/;
  const root = () => document.scrollingElement || document.documentElement;
  const overflows = (el, dx) => dx
    ? el.scrollWidth - el.clientWidth >= 2
    : el.scrollHeight - el.clientHeight >= 2;
  const scrollable = (el, dx) => {
    if (!el || el.nodeType !== 1 || !overflows(el, dx)) return false;
    const st = getComputedStyle(el);
    return OVF.test(dx ? st.overflowX : st.overflowY);
  };
  // その向きにまだ動く余地があるか (端まで来た入れ子は飛ばして外側を探すため)
  const hasRoom = (el, dx, dy) => {
    if (dy > 0) return el.scrollTop < el.scrollHeight - el.clientHeight - 1;
    if (dy < 0) return el.scrollTop > 1;
    if (dx > 0) return el.scrollLeft < el.scrollWidth - el.clientWidth - 1;
    return el.scrollLeft > 1;
  };
  // 祖先をたどる (Shadow DOM境界はホスト要素へ抜ける)
  const up = el => el.parentElement ||
    (el.getRootNode() instanceof ShadowRoot ? el.getRootNode().host : null);
  const fromChain = (el, dx, dy, alt) => {
    for (; el; el = up(el)) {
      if (!scrollable(el, dx)) continue;
      if (hasRoom(el, dx, dy)) return el;
      if (!alt.el) alt.el = el;
    }
    return null;
  };
  // 画面に一番大きく映っているスクロール領域 (マウスもフォーカスも手掛かりが無いとき用)
  const largest = (dx, dy, alt) => {
    let best = null, area = 0;
    for (const el of document.querySelectorAll('div,main,section,article,aside,nav,ul,ol')) {
      if (!overflows(el, dx) || !scrollable(el, dx)) continue;
      const r = el.getBoundingClientRect();
      const w = Math.min(r.right, innerWidth) - Math.max(r.left, 0);
      const h = Math.min(r.bottom, innerHeight) - Math.max(r.top, 0);
      if (w <= 0 || h <= 0) continue;
      if (!hasRoom(el, dx, dy)) { if (!alt.el) alt.el = el; continue; }
      if (w * h > area) { area = w * h; best = el; }
    }
    return best;
  };

  // 直近のマウス位置。ホイールと同じ感覚で、乗せている領域 (Twitchのチャット等) を優先する
  let mouse = null, cached = null, cachedAxis = 0;
  addEventListener('mousemove', e => { mouse = [e.clientX, e.clientY]; cached = null; },
    { capture: true, passive: true });

  const scroller = (dx, dy) => {
    const axis = dx ? 1 : 2;
    // 連打・キーリピート中に毎回DOM全体を走査しないよう、動ける間は前回の対象を使い回す
    if (cached && cachedAxis === axis && cached.isConnected && hasRoom(cached, dx, dy)) return cached;
    const r = root();
    let el = null;
    if (r && overflows(r, dx)) {
      el = r; // 普通のページスクロールが使えるならそれが最優先
    } else {
      const alt = { el: null }, seeds = [];
      if (mouse) seeds.push(document.elementFromPoint(mouse[0], mouse[1]));
      if (document.activeElement && document.activeElement !== document.body) seeds.push(document.activeElement);
      seeds.push(document.elementFromPoint(innerWidth / 2, innerHeight / 2));
      for (const s of seeds) { if (s && (el = fromChain(s, dx, dy, alt))) break; }
      el = el || largest(dx, dy, alt) || alt.el || r;
    }
    cached = el; cachedAxis = axis;
    return el;
  };

  const scrollAxis = (dx, dy) => {
    const el = scroller(dx, dy);
    if (el) el.scrollBy({ left: dx, top: dy, behavior: 'instant' });
  };
  const scrollHalf = sign => {
    const el = scroller(0, sign);
    if (el) el.scrollBy({ top: sign * (el.clientHeight || innerHeight) / 2, behavior: 'instant' });
  };
  const scrollEnd = sign => {
    const el = scroller(0, sign);
    if (el) el.scrollTo({ top: sign > 0 ? el.scrollHeight : 0, behavior: 'instant' });
  };

  const act = {
    j: () => scrollAxis(0, SCROLL), k: () => scrollAxis(0, -SCROLL),
    h: () => scrollAxis(-SCROLL, 0), l: () => scrollAxis(SCROLL, 0),
    d: () => scrollHalf(1), u: () => scrollHalf(-1),
    G: () => scrollEnd(1),
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
    if (combo === 'gg') { block(e); scrollEnd(-1); lastKey = ''; return; }
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
