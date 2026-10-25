namespace Karu;

static partial class Injections
{
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
  // 炎ロゴ(2コマ)。Trusted Types を強制するサイト(YouTube 等)では innerHTML への代入が例外になり、
  // このスクリプトごと止まって被いもSPA遷移の検知も効かなくなる。HTML文字列は使わずDOM APIで組み立てる
  const SVGNS = 'http://www.w3.org/2000/svg';
  const svgEl = (tag, attrs, kids = []) => {
    const e = document.createElementNS(SVGNS, tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    e.append(...kids);
    return e;
  };
  const grad = (id, stops) => svgEl('linearGradient', { id, x1: '0', y1: '1', x2: '0', y2: '0' },
    stops.map(([offset, color]) => svgEl('stop', { offset, 'stop-color': color })));
  const frame = (cls, outer, inner) => svgEl('svg', { class: cls, viewBox: '0 0 200 200' }, [
    svgEl('defs', {}, [
      grad(cls + 'o', [['0%', '#B3401F'], ['55%', '#E8672E'], ['100%', '#F7A93C']]),
      grad(cls + 'i', [['0%', '#F5952E'], ['100%', '#FFDD8A']]),
    ]),
    svgEl('g', { transform: 'translate(100,100) scale(1.385) translate(-101,-83)' }, [
      svgEl('path', { fill: 'url(#' + cls + 'o)', d: outer }),
      svgEl('path', { fill: 'url(#' + cls + 'i)', d: inner }),
    ]),
  ]);
  const flames = () => [
    frame('k1',
      'M100,14 C128,44 154,66 154,104 C154,128 142,144 128,152 C134,138 132,120 118,108 C120,126 112,142 96,150 C70,146 48,126 48,100 C48,76 62,56 76,42 C72,58 74,74 86,84 C86,58 88,34 100,14 Z',
      'M101,72 C112,86 122,98 121,116 C120,130 110,140 99,142 C88,140 78,129 78,115 C78,102 86,92 93,82 C94,92 97,98 102,102 C103,90 100,82 101,72 Z'),
    frame('k2',
      'M100,2 C126,36 154,60 154,104 C154,128 142,144 128,152 C134,138 132,120 118,108 C120,126 112,142 96,150 C70,146 48,126 48,100 C48,76 62,56 76,42 C72,58 74,74 86,84 C86,50 88,22 100,2 Z',
      'M101,62 C110,78 122,98 121,116 C120,130 110,140 99,142 C88,140 78,129 78,115 C78,102 86,92 93,82 C94,86 97,90 102,94 C103,78 100,70 101,62 Z'),
  ];
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
      const css = document.createElement('style');
      css.textContent = '#__karuLoad svg{position:absolute;inset:0;width:88px;height:88px;' +
        'animation:__karuFlick .36s steps(1) infinite!important}' +
        '#__karuLoad .k2{animation-delay:-.18s!important}' +
        '@keyframes __karuFlick{0%,100%{opacity:1}50%{opacity:0}}';
      const box = document.createElement('div');
      box.style.cssText = 'position:relative;width:88px;height:88px';
      box.append(...flames());
      cover.append(css, box);
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

}
