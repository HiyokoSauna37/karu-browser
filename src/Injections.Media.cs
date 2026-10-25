namespace Karu;

static partial class Injections
{
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
  const blockCodec = {{JsBool(forceH264)}};
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

}
