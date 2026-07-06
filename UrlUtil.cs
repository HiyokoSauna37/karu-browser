using System.Text.RegularExpressions;

namespace Karu;

static class UrlUtil
{
    // "localhost:3000" をスキームと誤認しないよう、コロン直後が数字なら除外
    static readonly Regex SchemeRe = new(@"^[a-zA-Z][a-zA-Z0-9+.-]*:(?![0-9])", RegexOptions.Compiled);
    // ドット無しでも "host:8080" 形式はURLとして扱う (開発サーバー等)
    static readonly Regex HostPortRe = new(@"^[a-zA-Z0-9_-]+:\d+(/.*)?$", RegexOptions.Compiled);

    public static string Normalize(string input)
    {
        input = input.Trim();
        if (SchemeRe.IsMatch(input)) return input; // https:, file:, about: など
        if (!input.Contains(' '))
        {
            if (input.Contains('.')) return "https://" + input;
            // ローカル開発サーバーはhttpsを持たないことが多いので http で開く
            if (HostPortRe.IsMatch(input) || input == "localhost" || input.StartsWith("localhost/"))
                return "http://" + input;
        }
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }
}
