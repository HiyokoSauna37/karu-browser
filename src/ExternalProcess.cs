using System.Diagnostics;

namespace Karu;

/// <summary>
/// 外部プロセスの起動(mpv / Edge / explorer / 自分自身の再起動・分離)。
/// 引数は必ず ArgumentList で個別に渡す: URL等を文字列連結して1本のコマンドラインにすると、
/// 引用符や空白を含む入力で引数の切れ目を偽装できてしまう。ここを通せばその組み立てを忘れない。
/// 例外は呼び出し側の判断(無視・案内表示・中止)に委ねるためそのまま投げる。
/// </summary>
static class ExternalProcess
{
    public static Process? Start(string exe, params string[] args) => Start(exe, (IEnumerable<string>)args);

    public static Process? Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }
}
