namespace Karu;

static partial class Injections
{
    /// <summary>
    /// ページ翻訳(「このページを翻訳」相当)の DOM 側ヘルパー。ホストから ExecuteScriptAsync で
    /// 必要時だけ呼ぶ(document-start 注入はしない = 通常閲覧に負荷を足さない)。
    ///
    /// **ブロック単位 + インライン要素のプレースホルダ方式**(Chrome 相当): テキストノードを個別に
    /// 訳すと、リンクや &lt;code&gt; で文が分断されたとき語順が崩れる(日本語は語順が変わるのに断片の
    /// 位置が固定されるため)。そこで「葉ブロック」(ブロック子を持たない要素)ごとに、インライン子要素を
    /// {0} のようなトークンに置換した文字列を丸ごと翻訳する。gtx はこのトークンを保持したまま正しい語順へ
    /// 再配置するので、訳文中のトークン位置へ元の要素(リンク等)を戻せば整合する。トグル/復元は
    /// 葉ブロックの子ノードの控え(原文/訳文の複製)を差し替えるだけ。HTML文字列で控えて innerHTML へ
    /// 書き戻す方式は、Trusted Types を強制するサイト(YouTube 等)で代入が例外になり切り替えられない。
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
  return { exists: true, shown: st.shown, hasTranslated: Array.isArray(st.transNodes) && st.transNodes.length > 0, changed };
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
  // 原文の控えは子ノードの複製で持つ(HTML文字列だと Trusted Types のサイトで書き戻せない)
  const origNodes = leafEls.map(el => Array.from(el.childNodes, n => n.cloneNode(true)));
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
  window.__karuTrans = { gen, units, leafEls, origNodes, transNodes: null, orphans, shown: 'original' };
  // 平坦配列: ユニットの template → orphan のテキスト(順序は apply と合わせる)
  return { gen, segs: units.map(u => u.tmpl).concat(orphans.map(o => o.orig.trim())) };
})()
""";

    /// <summary>訳文配列(units → orphans の順)を適用して DOM を組み立てる関数式。
    /// gen は Collect が返した世代番号 — 翻訳 API 往復中に再収集・ページ遷移した場合の古い適用を捨てる。
    /// final=true(最終適用)のときだけ訳文の控え(子ノードの複製)を取る(途中で失敗しても不完全な訳キャッシュが
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
  if (final) {
    st.transNodes = leafEls.map(el => Array.from(el.childNodes, n => n.cloneNode(true)));
    // 最終適用後の再表示は transNodes だけで行える。template/子要素を保持する units を切り離す。
    st.units = null;
  }
  st.shown = 'translated';
  return units.length + orphans.length;
})
""";

    /// <summary>キャッシュ済みの訳文へ戻す(ネットワーク不要。葉ブロックは訳文の控えの複製に差し替え)。</summary>
    public const string TranslateReapply = """
(() => {
  const st = window.__karuTrans;
  if (!st || !st.transNodes) return 0;
  // 控えは何度でも切り替えられるよう、毎回複製を差し込む
  for (let i = 0; i < st.leafEls.length; i++) if (st.leafEls[i].isConnected) { try { st.leafEls[i].replaceChildren(...st.transNodes[i].map(n => n.cloneNode(true))); } catch (e) {} }
  for (const o of st.orphans) if (o.trans != null && o.node.isConnected) { try { o.node.nodeValue = o.trans; } catch (e) {} }
  st.shown = 'translated';
  return st.leafEls.length;
})()
""";

    /// <summary>原文へ戻す(葉ブロックは原文の控えの複製に差し替え、orphan は原文 nodeValue に戻す)。</summary>
    public const string TranslateRestore = """
(() => {
  const st = window.__karuTrans;
  if (!st) return 0;
  for (let i = 0; i < st.leafEls.length; i++) if (st.leafEls[i].isConnected) { try { st.leafEls[i].replaceChildren(...st.origNodes[i].map(n => n.cloneNode(true))); } catch (e) {} }
  for (const o of st.orphans) if (o.node.isConnected) { try { o.node.nodeValue = o.orig; } catch (e) {} }
  st.shown = 'original';
  return st.leafEls.length;
})()
""";
}
