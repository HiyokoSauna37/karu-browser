<p align="right"><a href="README.md">English</a> | <b>日本語</b></p>

<p align="center">
  <img src="assets/banner.png" width="430" alt="Karu">
</p>

<p align="center">動画を見てもメモリが溶けない、キーボード操作前提の軽量Windowsブラウザ。</p>

<p align="center">
  <a href="../../releases"><img src="https://img.shields.io/github/v/release/HiyokoSauna37/karu-browser?label=release&color=E8672E" alt="最新リリース"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4" alt="対応OS: Windows 10/11">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-97CA00" alt="ライセンス: MIT"></a>
</p>

---

Karu（軽）は、WPF + WebView2（Chromium）で作った個人用ブラウザです。目的はひとつ、「本物のChromiumエンジンを使いながら、動画視聴込みでメモリ使用量を徹底的に抑える」こと。タブバーもURLバーもツールバーもなく、操作はすべてキーボード（Vim風）で行います。

## なぜ作ったか

Chromium系ブラウザはメモリを大量に消費しますが、世に出回っている「軽量ブラウザ」の多くは機能を削るか、そもそもChromiumではない（ためサイトの挙動・DRM・コーデック対応が本家と異なる）という問題があります。Karuは本物のWebView2（Edge/Chromeと同じエンジン）を使いつつ、以下で軽量化しています。

- 背面タブを段階的にサスペンド・休眠させる
- 速度より低メモリを優先して数十個のChromiumフラグ・機能を調整する
- 動画再生をまるごと [mpv](https://mpv.io/) に肩代わりさせ、そのタブのChromium側消費をほぼゼロにできる
- ブラウザchrome自体（タブバー・URLバー・ツールバー）を高さ40pxのドラッグバー1本に削ぎ落とし、代わりにキーボード操作のオーバーレイで全操作をまかなう

## 主な機能

- **クロームレスUI** — タブバーもURLバーもなし。全操作はキーボードオーバーレイと高さ40pxのタイトルバーのみ。
- **3段階タブライフサイクル** — Active(表示中) → Suspended(WebView2の`TrySuspendAsync`、非表示15秒後) → Hibernated(WebViewを完全破棄しURL・動画再生位置のみ保持、タブの「温存度」に応じて3〜10分)。空きメモリが閾値を切ると緊急休眠も発動。音声再生中のタブには一切手を出しません。
- **Vim風キーボード操作層** — 全ページに自動注入。`j/k/h/l`スクロール、`d/u`半ページ、`gg/G`先頭/末尾、`f/F`リンクヒント、`H/L`戻る/進む、`yy`URLコピー、`?`ヘルプ、など。
- **キーボードで完結するタブ一覧**（`Ctrl+Tab`）と**ブックマーク一覧**（`b`） — どちらも`j/k`+`Enter`で選択・決定、同じキーをもう一度押せば閉じます。
- **YouTube向けの作り込み** — プレーヤー内広告スキップ、画質上限、H.264強制（VP9/AV1のデコード負荷回避）、コメント・関連動画・ショート棚を隠す集中モード、プレーヤー内の再生速度ボタン。
- **[mpv](https://mpv.io/) 連携**（`Ctrl+M`） — 現在の動画をyt-dlp経由でmpv再生に切り替え、再生成功を確認したらページを軽量なプレースホルダーに差し替え（再生位置は引き継いで戻れます）。
- **広告・トラッカーブロック** — 内蔵のドメインブロックリスト、またはuBlock Originの非パッケージ版拡張をサイドロード可能（WebView2は拡張のcontent scriptを自前実行しないため、Karu側で注入して補っています）。
- **セッション復元、パスワード保存/自動入力、ブックマーク。**
- **テキストカーソルモード、CDP経由の動画フルスクリーン、実測メモリ使用量の表示。**

## キーバインド

| キー | 動作 |
|---|---|
| `j` / `k` | 下 / 上スクロール |
| `h` / `l` | 左 / 右スクロール |
| `d` / `u` | 半ページ 下 / 上 |
| `gg` / `G` | 先頭 / 末尾へ |
| `f` / `F` | リンクヒント（開く / 新しいタブで開く） |
| `H` / `L` | 戻る / 進む |
| `r` | 再読み込み |
| `yy` | 現在のURLをコピー |
| `>` / `<` / `=` | 再生速度 +0.25 / −0.25 / 等速に戻す |
| `?` | ヘルプオーバーレイの表示切替 |
| `t` | 新しいタブ |
| `x` / `X` | タブを閉じる / 閉じたタブを復元 |
| `o` / `Ctrl+L` | URL / 検索オーバーレイ |
| `Ctrl+Tab` | タブ一覧オーバーレイ（`j/k`+`Enter`で切替、`Ctrl+W`で選択タブを閉じる） |
| `b` | ブックマーク一覧オーバーレイ（`j/k`+`Enter`、`Shift+Enter`で新しいタブ、`b`/`Esc`で閉じる） |
| `J` / `K` | 前 / 次のタブ |
| `Ctrl+T` / `Ctrl+W` | 新しいタブ / タブを閉じる |
| `Ctrl+D` | 現在のページをブックマーク |
| `Ctrl+1`〜`9` | N番目 / 最後のタブへ移動 |
| `Ctrl+Shift+T` | 閉じたタブを復元 |
| `Ctrl+M` | 現在の動画をmpvで開く |
| `Ctrl+E` | 現在のページをEdgeで開く（DRM動画向け） |
| `Ctrl+B` | 動画集中モードの切替 |
| `Ctrl+O` | 動画フルスクリーンの切替 |
| `F7` | テキストカーソルモードの切替（要再起動） |
| `F11` | ウィンドウの全画面切替 |
| `Ctrl+Shift+W` | 終了 |

## 必要環境

- Windows 10/11
- [.NET 8 デスクトップランタイム](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WebView2 ランタイム](https://developer.microsoft.com/microsoft-edge/webview2/) — Edgeが入っているWindows 11/10ならほぼ標準で入っています
- [mpv](https://mpv.io/)（任意、`Ctrl+M`用） — `winget install mpv-player.mpv-CI.MSVC`

## インストール

[Releases](../../releases) から最新のzipをダウンロードして展開し、`Karu.exe` を実行してください。

> Karuはまだコード署名していないため、初回実行時にWindows SmartScreenが警告を出すことがあります。「詳細情報」→「実行」で進めます。署名していない理由は下記「[SmartScreenについて](#smartscreenについて)」を参照するか、自分でソースからビルドしてください。

## ソースからビルド

```
dotnet publish src/Karu.csproj -c Release -o dist
```

`dist/` にフレームワーク依存ビルドが出力されます（実行先マシンに.NET 8 デスクトップランタイムが必要です）。

## 設定

設定・ブックマーク・広告ブロックリスト・サイドロードした拡張機能は、すべて `%APPDATA%\Karu` に保存されます。WebView2自体のプロファイルデータ（Cookie・ログインセッション）は `%LOCALAPPDATA%\Karu\WebView2Data` に保存されます。アプリ内メニュー（右上の`≡`ボタン）から設定フォルダを開けます。

内蔵ブロックリストの代わりにuBlock Originを使いたい場合は、非パッケージ版（unpacked）のChromium拡張をダウンロードして `%APPDATA%\Karu\extensions\` 以下に配置してください。

## SmartScreenについて

Karuは低メモリ化の一環として、Chromiumのテレメトリ・保護機能の一部（SmartScreen連携を含む）を無効化しており、またVim操作層やYouTube向け調整のために全ページへスクリプトを注入しています。これはこのプロジェクトの設計上必要な挙動ですが、未署名で実績のないバイナリに対してヒューリスティックなアンチウイルス/SmartScreenが警戒しやすい挙動パターンでもあります。初回実行時の警告はそのためです。気になる場合は上記の手順でソースからビルドすれば警告を回避できます。

## ライセンス

[MIT](LICENSE)
