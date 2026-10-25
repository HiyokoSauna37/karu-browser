using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;

namespace Karu;

public partial class MainWindow
{
    /// <summary>注入スクリプト(Vim層・YouTube広告スキップ・mpvプレースホルダー)からのコマンドを処理する。</summary>
    void OnWebMessage(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? raw = null;
        try { raw = args.TryGetWebMessageAsString(); } catch { }
        // 注入スクリプト由来の証明としてトークン接頭辞を検証する。
        // これが無いと任意のページのJSがタブ操作コマンドを偽装できてしまう。
        if (raw is null || !raw.StartsWith(Injections.MessageToken + "|", StringComparison.Ordinal)) return;
        var cmd = raw[(Injections.MessageToken.Length + 1)..];
        // YouTube広告スキップ: 座標付きコマンドなので switch の前で処理する
        if (cmd.StartsWith(YouTubeAdSkip.Command + "|", StringComparison.Ordinal)) { _ = TrustedAdSkipClickAsync(tab, cmd); return; }
        switch (cmd)
        {
            case "tabClose": CloseTab(tab); break;
            case "tabRestore": RestoreClosedTab(); break;
            case "tabNext": CycleTab(1); break;
            case "tabPrev": CycleTab(-1); break;
            case "tabNew": _ = AddTabAsync(null); break;
            case "focusUrl": ShowUrlOverlay(); break;
            case "tabList": ShowTabOverlay(); break;
            case "bookmarkList": ShowBookmarkOverlay(); break;
            case "mpvReturn": MpvReturn(tab); break;
        }
    }

    async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (BookmarkListOverlay.Visibility == Visibility.Visible)
        {
            HandleBookmarkOverlayKey(key, shift, ctrl);
            e.Handled = true;
            return;
        }

        // タブ一覧オーバーレイ表示中はそちらでキーを処理する
        if (TabOverlay.Visibility == Visibility.Visible)
        {
            HandleTabOverlayKey(key, shift, ctrl);
            e.Handled = true;
            return;
        }

        // Shift付きを先に判定しないと Ctrl+T が Ctrl+Shift+T を食う
        if (ctrl && shift && key == Key.W) { e.Handled = true; Close(); }
        else if (ctrl && shift && key == Key.T) { e.Handled = true; RestoreClosedTab(); }
        // Ctrl+Shift+D は Ctrl+D(ブックマーク)より先に判定する
        else if (ctrl && shift && key == Key.D) { e.Handled = true; if (_active is not null) DetachTab(_active); }
        else if (ctrl && shift && key == Key.Y) { e.Handled = true; if (_active is not null) _ = TranslatePageToggleAsync(_active); }
        else if (ctrl && shift && key == Key.PageDown) { e.Handled = true; MoveTab(1); }
        else if (ctrl && shift && key == Key.PageUp) { e.Handled = true; MoveTab(-1); }
        else if (ctrl && key == Key.PageDown) { e.Handled = true; CycleTab(1); }
        else if (ctrl && key == Key.PageUp) { e.Handled = true; CycleTab(-1); }
        else if (ctrl && key == Key.T) { e.Handled = true; await AddTabAsync(null); }
        else if (ctrl && key == Key.W) { e.Handled = true; if (_active != null) CloseTab(_active); }
        else if (ctrl && key == Key.L) { e.Handled = true; ShowUrlOverlay(); }
        else if (ctrl && key == Key.D) { e.Handled = true; Star_Click(this, new RoutedEventArgs()); }
        else if (ctrl && key == Key.E) { e.Handled = true; OpenInEdge(); }
        else if (ctrl && key == Key.M) { e.Handled = true; OpenInMpv(); }
        else if (ctrl && key == Key.B) { e.Handled = true; Focus_Click(this, new RoutedEventArgs()); }
        else if (ctrl && key == Key.O) { e.Handled = true; ToggleVideoFullscreen(); }
        // 素のShift+Tabはページ内フォーカスの逆順移動(ホームページ等の要素選択)に譲るため奪わない。
        // タブ一覧はCtrl+Tab専用にする(旧: Ctrl+Tabでの単純サイクルは廃止しタブ一覧に統合)
        else if (ctrl && key == Key.Tab) { e.Handled = true; ShowTabOverlay(); }
        else if (ctrl && key >= Key.D1 && key <= Key.D8) { e.Handled = true; JumpTab(key - Key.D1); }
        else if (ctrl && key == Key.D9) { e.Handled = true; JumpTab(Tabs.Count - 1); }
        else if (key == Key.F7) { e.Handled = true; ToggleCaretBrowsing(); }
        else if (key == Key.F11) { e.Handled = true; SetFullscreen(!_fullscreen); }
    }

    void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var args = ContentUrls();
        if (((App)Application.Current).IsDetachedWindow) args = args.Prepend(App.DetachedFlag);
        ExternalProcess.Start(exe, args);
        Close();
    }

    void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (on)
        {
            _preFsState = WindowState;
            TopBar.Visibility = Visibility.Collapsed;
            chrome.CaptionHeight = 0; // 全画面中は動画上部がドラッグ領域にならないように
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // WindowStyle=None で最大化してもタスクバーを覆うには一度 Normal を経由する
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            TopBar.Visibility = Visibility.Visible;
            chrome.CaptionHeight = 40;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFsState;
        }
        UpdateRootMargin();
    }

    // WindowChrome 使用時、最大化するとリサイズ枠ぶん画面外にはみ出すのを補正
    void UpdateRootMargin()
        => RootGrid.Margin = WindowState == WindowState.Maximized && !_fullscreen
            ? new Thickness(8) : new Thickness(0);

    void MinWin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void MaxWin_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void CloseWin_Click(object sender, RoutedEventArgs e) => Close();
}
