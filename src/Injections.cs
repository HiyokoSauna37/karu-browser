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
      // 半透明+ぼかし。完全に隠すと「ブラウザが固まった」ように見えるため、
      // 裏でページが実際に描画・変化している気配をうっすら見せて「処理中」だと伝える
      // ぼかし量はブックマーク一覧(bキー)のオーバーレイと揃える(blur(4px))
      cover.style.cssText = 'position:fixed;inset:0;z-index:2147483647;' +
        'background:rgba(17,17,17,.82);backdrop-filter:blur(4px);-webkit-backdrop-filter:blur(4px);' +
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

  // 離脱ナビゲーション開始(=リンククリック)の瞬間、遷移「元」のこのページに被いを出す。
  // ホストからExecuteScriptAsyncで頼む方式はナビゲーション進行中でレースになり届かないことがある。
  // beforeunloadはページ内で同期的に発火するため確実。gen++で自ページのpoll(解除判定)を止め、
  // ナビゲーションが中断された場合はホストのhide呼び出しか8秒の保険で解除する
  // (成功時はドキュメントごと破棄されるので後始末不要)。
  // 注: beforeunloadリスナーはBFCacheを無効にするが、Karuは起動フラグで元々BackForwardCacheを
  // 切っている(メモリ優先)ため実害はない
  addEventListener('beforeunload', () => {
    gen++;
    show();
    clearTimeout(pollId);
    setTimeout(() => hide(), 8000);
  });
  window.__karuCoverHide = () => hide(); // ホストがナビゲーション失敗時に呼ぶ

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
    ///
    /// **YouTube 限定にしている**: この偽装は navigator プロパティを非ネイティブ getter で上書きするため、
    /// 全サイトに当てると Cloudflare 等のボット判定が「改ざんされたブラウザ」と見なして弾く材料になる
    /// (getter の toString がネイティブでない・値が不整合、が検知される)。効果があるのは主に YouTube なので、
    /// そこだけに絞って他サイトでは素の navigator を見せる。
    /// </summary>
    public const string LowSpec = """
(() => {
  if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;
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
    /// ページ翻訳(「このページを翻訳」相当)の DOM 側ヘルパー。ホストから ExecuteScriptAsync で
    /// 必要時だけ呼ぶ(document-start 注入はしない = 通常閲覧に負荷を足さない)。
    ///
    /// **ブロック単位 + インライン要素のプレースホルダ方式**(Chrome 相当): テキストノードを個別に
    /// 訳すと、リンクや &lt;code&gt; で文が分断されたとき語順が崩れる(日本語は語順が変わるのに断片の
    /// 位置が固定されるため)。そこで「葉ブロック」(ブロック子を持たない要素)ごとに、インライン子要素を
    /// {0} のようなトークンに置換した文字列を丸ごと翻訳する。gtx はこのトークンを保持したまま正しい語順へ
    /// 再配置するので、訳文中のトークン位置へ元の要素(リンク等)を戻せば整合する。トグル/復元は
    /// 葉ブロックの innerHTML スナップショット(原文/訳文)を差し替えるだけ。
    /// </summary>

    /// <summary>現在の翻訳状態を JSON で返す ({exists, shown, hasTranslated, changed})。
    /// SPA のソフト遷移(pushState 等)では window(= __karuTrans)が残ったまま DOM だけ入れ替わるため、
    /// 旧状態が現在の DOM をまだ代表しているかを必ず判定する(changed=true なら「実質別ページ」)。
    /// これを見ずに shown フラグでトグルすると、切り離された旧要素への復元/再適用を空振りし続け
    /// 「翻訳が効かなくなる」。判定は (a)既知の葉ブロックが全滅、または (b)現在の本文テキストのうち
    /// 既知ノード(接続中の葉ブロック+orphan)が覆う割合が 2/3 未満、のどちらか。</summary>
    public const string TranslateState = """
(() => {
  const st = window.__karuTrans;
  if (!st) return { exists: false, shown: '', hasTranslated: false, changed: false };
  const SKIP = new Set(['SCRIPT','STYLE','NOSCRIPT','TEXTAREA','PRE','SVG','CANVAS']);
  let covered = 0;
  let live = 0;
  for (const el of st.leafEls) if (el.isConnected) { live++; covered += (el.textContent || '').length; }
  for (const o of st.orphans) if (o.node.isConnected) covered += (o.node.nodeValue || '').length;
  let cur = 0;
  if (document.body) {
    const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let tn;
    while ((tn = w.nextNode())) {
      if (!tn.nodeValue || !tn.nodeValue.trim()) continue;
      const pe = tn.parentElement;
      if (pe && SKIP.has(pe.tagName)) continue;
      cur += tn.nodeValue.length;
    }
  }
  const changed = (st.leafEls.length > 0 && live === 0) || covered * 3 < cur * 2;
  return { exists: true, shown: st.shown, hasTranslated: Array.isArray(st.transHTML) && st.transHTML.length > 0, changed };
})()
""";

    /// <summary>葉ブロックとインライン要素からプレースホルダ入りテンプレートを作り、翻訳対象文字列の配列を返す。</summary>
    public const string TranslateCollect = """
(() => {
  const INLINE = new Set(['A','ABBR','B','BDI','BDO','CITE','CODE','DATA','DFN','EM','I','KBD','MARK','Q','RP','RT','RUBY','S','SAMP','SMALL','SPAN','STRONG','SUB','SUP','TIME','U','VAR','WBR','FONT','TT','INS','DEL','BIG','LABEL','OUTPUT']);
  const CODEISH = new Set(['CODE','KBD','SAMP','VAR','TT']);
  const SKIP = new Set(['SCRIPT','STYLE','NOSCRIPT','TEXTAREA','PRE','SVG','CANVAS']);
  const isInline = el => INLINE.has(el.tagName);
  if (!document.body) return [];

  // 葉ブロック = 非インライン要素で、要素の子がすべてインライン(=ブロック子を持たない)、かつ非空白テキストを含む
  const leafEls = [];
  for (const el of document.body.querySelectorAll('*')) {
    if (isInline(el) || SKIP.has(el.tagName)) continue;
    let allInline = true;
    for (const c of el.children) { if (!isInline(c) && !SKIP.has(c.tagName)) { allInline = false; break; } }
    if (!allInline) continue;
    if (!el.textContent || !el.textContent.trim()) continue;
    leafEls.push(el);
  }

  // ユニット = {el, tmpl, childElems}。葉ブロックとその中の非コードのインライン要素を再帰的に作る。
  // tmpl は空白を1つに畳む(HTML の描画と等価。改行が残ると翻訳のバッチ整列が崩れるため)。
  const units = [];
  const makeUnit = (el) => {
    let tmpl = '';
    const childElems = [];
    for (const node of el.childNodes) {
      if (node.nodeType === 3) { tmpl += node.nodeValue; }
      else if (node.nodeType === 1) {
        if (isInline(node) && !SKIP.has(node.tagName)) {
          const k = childElems.length;
          childElems.push(node);
          tmpl += '{' + k + '}';
          if (!CODEISH.has(node.tagName) && node.textContent && node.textContent.trim()) makeUnit(node);
        } else {
          tmpl += node.textContent || ''; // 想定外のブロック子等はテキスト平坦化(取りこぼし防止)
        }
      }
    }
    units.push({ el, tmpl: tmpl.replace(/\s+/g, ' '), childElems });
  };
  const origHTML = leafEls.map(el => el.innerHTML);
  for (const el of leafEls) makeUnit(el);

  // 葉ブロックに含まれない裸テキスト(混在コンテナ直下)は個別翻訳(orphan)にフォールバック
  const leafSet = new Set(leafEls);
  const inLeaf = (n) => { let p = n.parentElement; while (p) { if (leafSet.has(p)) return true; p = p.parentElement; } return false; };
  const orphans = [];
  const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  let tn;
  while ((tn = w.nextNode())) {
    if (!tn.nodeValue || !tn.nodeValue.trim()) continue;
    const pe = tn.parentElement;
    if (pe && (SKIP.has(pe.tagName) || CODEISH.has(pe.tagName))) continue;
    if (inLeaf(tn)) continue;
    orphans.push({ node: tn, orig: tn.nodeValue });
  }

  // 世代番号: 翻訳 API 往復中の再収集・ページ遷移をまたいだ古い適用を ApplyFn 側で捨てるための印
  const gen = (window.__karuTransGen = (window.__karuTransGen || 0) + 1);
  window.__karuTrans = { gen, units, leafEls, origHTML, transHTML: null, orphans, shown: 'original' };
  // 平坦配列: ユニットの template → orphan のテキスト(順序は apply と合わせる)
  return { gen, segs: units.map(u => u.tmpl).concat(orphans.map(o => o.orig.trim())) };
})()
""";

    /// <summary>訳文配列(units → orphans の順)を適用して DOM を組み立てる関数式。
    /// gen は Collect が返した世代番号 — 翻訳 API 往復中に再収集・ページ遷移した場合の古い適用を捨てる。
    /// final=true(最終適用)のときだけ訳文 HTML をキャッシュする(途中で失敗しても不完全な訳キャッシュが
    /// 残らず、次のトグルで翻訳し直せる)。切り離された要素への書き込みはスキップする。</summary>
    public const string TranslateApplyFn = """
((trans, gen, final) => {
  const st = window.__karuTrans;
  if (!st || st.gen !== gen) return -1;
  const units = st.units, leafEls = st.leafEls, orphans = st.orphans;
  // SPA 遷移で既知ノードが全滅していたら何もしない(shown フラグを汚さない)
  const anyLive = leafEls.some(el => el.isConnected) || orphans.some(o => o.node.isConnected);
  if ((leafEls.length || orphans.length) && !anyLive) return -1;
  const parse = (s) => {
    const out = []; let last = 0; const re = /\{(\d+)\}/g; let m;
    while ((m = re.exec(s))) { if (m.index > last) out.push({ t: s.slice(last, m.index) }); out.push({ k: +m[1] }); last = m.index + m[0].length; }
    if (last < s.length) out.push({ t: s.slice(last) });
    return out;
  };
  // ユニットは「子(インライン)→親(葉ブロック)」の順に並ぶので、この順で組み立てれば
  // 子要素の中身を先に確定してから親へ差し込める。
  for (let i = 0; i < units.length; i++) {
    const u = units[i];
    if (!u.el.isConnected) continue;
    const tr = (trans[i] != null && trans[i] !== '') ? trans[i] : u.tmpl;
    const toks = parse(tr);
    const used = new Set();
    while (u.el.firstChild) u.el.removeChild(u.el.firstChild);
    for (const tok of toks) {
      if (tok.t != null) { if (tok.t.length) u.el.appendChild(document.createTextNode(tok.t)); }
      else { const c = u.childElems[tok.k]; if (c) { u.el.appendChild(c); used.add(tok.k); } }
    }
    // 訳文がプレースホルダを落とした場合でも要素を失わないよう末尾に付ける
    for (let k = 0; k < u.childElems.length; k++) if (!used.has(k)) u.el.appendChild(u.childElems[k]);
  }
  // orphan テキストノード(前後の空白は原文のものを保つ)
  for (let j = 0; j < orphans.length; j++) {
    const o = orphans[j];
    const t = trans[units.length + j];
    if (t != null && t !== '' && o.node.isConnected) {
      const raw = o.orig;
      const lead = (raw.match(/^\s*/) || [''])[0];
      const trail = (raw.match(/\s*$/) || [''])[0];
      o.trans = lead + t + trail;
      try { o.node.nodeValue = o.trans; } catch (e) {}
    }
  }
  if (final) st.transHTML = leafEls.map(el => el.innerHTML);
  st.shown = 'translated';
  return units.length + orphans.length;
})
""";

    /// <summary>キャッシュ済みの訳文へ戻す(ネットワーク不要。葉ブロックは innerHTML 差し替え)。</summary>
    public const string TranslateReapply = """
(() => {
  const st = window.__karuTrans;
  if (!st || !st.transHTML) return 0;
  for (let i = 0; i < st.leafEls.length; i++) if (st.leafEls[i].isConnected) { try { st.leafEls[i].innerHTML = st.transHTML[i]; } catch (e) {} }
  for (const o of st.orphans) if (o.trans != null && o.node.isConnected) { try { o.node.nodeValue = o.trans; } catch (e) {} }
  st.shown = 'translated';
  return st.leafEls.length;
})()
""";

    /// <summary>原文へ戻す(葉ブロックは原文 innerHTML を差し替え、orphan は原文 nodeValue に戻す)。</summary>
    public const string TranslateRestore = """
(() => {
  const st = window.__karuTrans;
  if (!st) return 0;
  for (let i = 0; i < st.leafEls.length; i++) if (st.leafEls[i].isConnected) { try { st.leafEls[i].innerHTML = st.origHTML[i]; } catch (e) {} }
  for (const o of st.orphans) if (o.node.isConnected) { try { o.node.nodeValue = o.orig; } catch (e) {} }
  st.shown = 'original';
  return st.leafEls.length;
})()
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

    /// <summary>
    /// Twitch の広告が消せなかったときの逃げ道。ホストが window.__karuTwitchAd(true/false) を呼ぶと
    /// プレーヤーを消音して被いを出す。SSAI は配信本体と同じタイムラインなので尺は縮まらないが、
    /// 少なくとも音と映像は遮れる。
    ///
    /// 消音は「元の状態を覚えてから muted=true」にし、解除で元へ戻す
    /// (もともとユーザーが消音していたら、解除時に勝手に音を出さない)。
    /// </summary>
    public const string TwitchAdCover = """
(() => {
  if (!/(^|\.)twitch\.tv$/.test(location.hostname)) return;
  const MAX = 180000; // 保険: ホストからの解除が届かなくても必ず外す
  let cover = null, prevMuted = null, guard = 0;
  const video = () => document.querySelector('video');
  // 被うのはプレーヤーの中だけ。ページ全体を覆うとチャットもUIも真っ暗になる
  const box = () => {
    const v = video();
    if (!v) return null;
    return v.closest('.video-player__container') ||
           v.closest('[data-a-target="video-player"]') ||
           v.parentElement;
  };
  const show = () => {
    const v = video();
    if (v && prevMuted === null) { prevMuted = v.muted; v.muted = true; }
    clearTimeout(guard);
    guard = setTimeout(hide, MAX);
    if (cover && cover.isConnected) return;
    const b = box();
    if (!b) return;  // プレーヤーが見つからないときは消音だけにして、何も覆わない
    if (getComputedStyle(b).position === 'static') b.style.position = 'relative';
    cover = document.createElement('div');
    cover.id = '__karuTwAd';
    cover.style.cssText = 'position:absolute;inset:0;z-index:100;background:#0e0e10;' +
      'display:flex;align-items:center;justify-content:center;' +
      'color:#9b8f82;font:600 14px Consolas,monospace;letter-spacing:.05em;pointer-events:none';
    cover.textContent = '広告を再生中 — 音声と映像を遮断しています';
    b.appendChild(cover);
  };
  const hide = () => {
    clearTimeout(guard);
    guard = 0;
    const v = video();
    if (v && prevMuted !== null) v.muted = prevMuted;
    prevMuted = null;
    if (cover) { cover.remove(); cover = null; }
  };
  window.__karuTwitchAd = on => { try { on ? show() : hide(); } catch (e) { hide(); } };
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
