namespace Karu;

static partial class Injections
{
    /// <summary>
    /// 注入スクリプト→ホストの postMessage コマンド認証トークン (起動ごとに変わる)。
    /// WebView2の WebMessageReceived は送信元スクリプトを区別できないため、ページ自身のJSが
    /// 'tabClose' 等を偽装send してタブ操作や強制ナビゲーションを行うのを、この接頭辞の検証で防ぐ。
    /// document-start 注入(Vim層)は postMessage の参照をページスクリプト実行前に捕獲するので、
    /// ページ側がpostMessageをフックしてもトークンは漏れない。
    /// </summary>
    public static readonly string MessageToken;

    static Injections()
    {
        MessageToken = Guid.NewGuid().ToString("N");
        Vim = VimSrc.Replace("__KARU_MSG_TOKEN__", MessageToken);
    }

    /// <summary>C# の bool を JS リテラルにする(テンプレートの穴とホストからの呼び出し引数の両方で使う)。</summary>
    public static string JsBool(bool value) => value ? "true" : "false";
}
