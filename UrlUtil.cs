using System.Text.RegularExpressions;

namespace Karu;

static class UrlUtil
{
    // "localhost:3000" をスキームと誤認しないよう、コロン直後が数字なら除外
    static readonly Regex SchemeRe = new(@"^[a-zA-Z][a-zA-Z0-9+.-]*:(?![0-9])", RegexOptions.Compiled);

    public static string Normalize(string input)
    {
        input = input.Trim();
        if (SchemeRe.IsMatch(input)) return input; // https:, file:, about: など
        if (!input.Contains(' ') && input.Contains('.')) return "https://" + input;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }
}
