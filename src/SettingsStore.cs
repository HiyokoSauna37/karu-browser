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
}

static class SettingsStore
{
    static readonly string FilePath = Path.Combine(Paths.AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(AppSettings s)
    {
        try { Paths.AtomicWrite(FilePath, JsonSerializer.Serialize(s)); } catch { }
    }
}
