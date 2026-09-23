using System.Text;

namespace Islet.Search;

/// <summary>
/// Запрос, набранный не в той раскладке: «ntktuhfv» — это «телеграм».
///
/// Как на Kawaki: запрос уходит и в своём виде, и в другой раскладке, если
/// своих находок мало, — и человек находит нужное с первого раза, а не после
/// того, как заметит индикатор языка. Строка-подсказка сверху показывает,
/// почему по абракадабре что-то нашлось.
/// </summary>
internal static class KeyboardLayout
{
    private const string En = "`qwertyuiop[]asdfghjkl;'zxcvbnm,.~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>";
    private const string Ru = "ёйцукенгшщзхъфывапролджэячсмитьбюЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ";

    private static readonly Dictionary<char, char> EnToRu = Build(En, Ru);
    private static readonly Dictionary<char, char> RuToEn = Build(Ru, En);

    private static Dictionary<char, char> Build(string from, string to)
    {
        var map = new Dictionary<char, char>();
        for (var i = 0; i < from.Length; i++) map[from[i]] = to[i];
        return map;
    }

    /// <summary>
    /// Прочтение в другой раскладке или null, если запрос уже смешанный, без
    /// букв или после перевода не меняется.
    /// </summary>
    public static string? Alternate(string query)
    {
        var q = query.Trim();
        if (q.Length < 2) return null;

        bool latin = false, cyrillic = false;
        foreach (var c in q)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin = true;
            else if (c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') cyrillic = true;
        }
        if (latin == cyrillic) return null;

        var map = latin ? EnToRu : RuToEn;
        var sb = new StringBuilder(q.Length);
        foreach (var c in q)
            sb.Append(map.TryGetValue(c, out var mapped) ? mapped : c);
        var converted = sb.ToString();

        // Латиница, которая не превращается в кириллицу целиком («C:\Users»), — не опечатка раскладки.
        if (latin && converted.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) return null;
        return string.Equals(converted, q, StringComparison.OrdinalIgnoreCase) ? null : converted;
    }
}
