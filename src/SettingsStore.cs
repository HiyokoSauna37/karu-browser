using System.IO;
using System.Text.Json;

namespace Karu;

public class AppSettings
{
    public bool FocusMode { get; set; } = true;
    public bool CaretBrowsing { get; set; } = false;
    /// <summary>再生画質の上限 (hd720/hd1080/hd1440など。空文字で無制限)。
    /// 本物のプレーヤーAPI経由なので視聴履歴・進捗同期には影響しない</summary>
    public string MaxQuality { get; set; } = "hd1080";
    /// <summary>VP9/AV1を無効化してH.264(全GPUでハードウェアデコード可)を選ばせる。反映は再読み込み後</summary>
    public bool ForceH264 { get; set; } = true;
    /// <summary>フレームレート上限。60fps動画を30fps版にする(CPU 2〜4倍削減)。0で無制限。反映は再読み込み後</summary>
    public int MaxFps { get; set; } = 30;
    /// <summary>Twitchのサーバー側挿入広告(SSAI)を、プレイリストを取り直して差し替えることで回避する。
    /// 消せなかった広告は消音+被いに切り替える。反映は再読み込み後 (TwitchAds.cs)</summary>
    public bool TwitchAdBlock { get; set; } = true;

    // ---- ネットワーク / 検閲回避 (変更後は再起動) ----
    /// <summary>HTTP(S) または SOCKS5 proxy。例: socks5://127.0.0.1:9050。空ならシステム設定。</summary>
    public string ProxyServer { get; set; } = "";

    /// <summary>サイト分離・SmartScreen・WebView2追跡防止を有効にする。通常は true のまま使う。</summary>
    public bool EnableBrowserProtections { get; set; } = true;

    // ---- タブ休眠(ハイバネート)ポリシー (settings.json で調整可能) ----
    /// <summary>アクティブ以外に「温存」する直近使用タブの数</summary>
    public int WarmTabs { get; set; } = 2;
    /// <summary>温存タブを休眠させるまでの分数</summary>
    public int HibernateWarmMinutes { get; set; } = 10;
    /// <summary>温存枠から溢れたタブを休眠させるまでの秒数</summary>
    public int HibernateColdSeconds { get; set; } = 180;
    /// <summary>空き物理メモリがこのGB未満になったら緊急休眠(30秒で休眠)する</summary>
    public double PressureAvailableGB { get; set; } = 1.5;
    /// <summary>最小化がこの分数続いたら全タブ休眠(パーキングモード。音声再生中は除外)</summary>
    public int ParkMinutes { get; set; } = 10;

    public void Normalize()
    {
        string[] qualities = ["", "tiny", "small", "medium", "large", "hd720", "hd1080", "hd1440", "hd2160"];
        if (!qualities.Contains(MaxQuality, StringComparer.Ordinal)) MaxQuality = "hd1080";
        MaxFps = Math.Clamp(MaxFps, 0, 240);
        WarmTabs = Math.Clamp(WarmTabs, 0, 50);
        HibernateWarmMinutes = Math.Clamp(HibernateWarmMinutes, 1, 24 * 60);
        HibernateColdSeconds = Math.Clamp(HibernateColdSeconds, 15, 24 * 60 * 60);
        PressureAvailableGB = double.IsFinite(PressureAvailableGB)
            ? Math.Clamp(PressureAvailableGB, 0.25, 1024) : 1.5;
        ParkMinutes = Math.Clamp(ParkMinutes, 1, 24 * 60);
        ProxyServer = NetworkRouting.NormalizeProxy(ProxyServer) ?? "";
    }
}

static class SettingsStore
{
    static readonly string FilePath = Path.Combine(Paths.AppDataDir, "settings.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                settings.Normalize();
                return settings;
            }
        }
        catch { }
        var defaults = new AppSettings();
        defaults.Normalize();
        if (!File.Exists(FilePath)) Save(defaults); // 手編集できる雛形を初回起動時から用意する
        return defaults;
    }

    public static void Save(AppSettings s)
    {
        s.Normalize();
        try { Paths.AtomicWrite(FilePath, JsonSerializer.Serialize(s, JsonOpts)); } catch { }
    }
}
