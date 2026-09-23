using System.Globalization;

namespace Islet.Search.Providers;

/// <summary>
/// Разбор арифметики из строки поиска: + − × ÷ ^ % !, скобки, «2(3+4)»,
/// функции sqrt sin cos tan ln log abs round floor ceil min max, константы pi и e,
/// шестнадцатеричные 0x1F и десятичная запятая. Никаких eval и рефлексии —
/// рекурсивный спуск на сотню строк.
/// </summary>
internal sealed class Calculator
{
    private readonly string _s;
    private int _i;
    /// <summary>Внутри скобок функции запятая — разделитель аргументов, а не десятичная.</summary>
    private int _inArgs;

    private Calculator(string s) => _s = s;

    public static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        var s = text.Trim();
        if (s.StartsWith('=')) s = s[1..];
        s = s.Replace('×', '*').Replace('·', '*').Replace('÷', '/').Replace('−', '-').Replace("**", "^");
        if (s.Length == 0) return false;

        try
        {
            var calc = new Calculator(s);
            value = calc.Expression();
            calc.SkipSpaces();
            return calc._i == s.Length && double.IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Похоже ли на выражение, а не на имя файла «1-2.docx» или запрос «windows 11».</summary>
    public static bool LooksLikeMath(string text)
    {
        var s = text.Trim();
        if (s.StartsWith('=')) return s.Length > 1;
        var hasDigit = false;
        var hasOperator = false;
        foreach (var c in s)
        {
            if (char.IsDigit(c)) hasDigit = true;
            else if ("+-*/^%×÷−!()".Contains(c)) hasOperator = true;
            else if (!(c is ' ' or '.' or ',' or 'x' or 'X' || char.IsLetter(c))) return false;
        }
        // «sqrt 2», «pi» — функции без операторов тоже считаем.
        var hasFunction = Functions.Keys.Any(f => s.Contains(f + "(", StringComparison.OrdinalIgnoreCase))
            || s.Equals("pi", StringComparison.OrdinalIgnoreCase);
        return (hasDigit && hasOperator) || hasFunction;
    }

    private static readonly Dictionary<string, Func<double[], double>> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = a => Math.Sqrt(a[0]),
        ["sin"] = a => Math.Sin(a[0]),
        ["cos"] = a => Math.Cos(a[0]),
        ["tan"] = a => Math.Tan(a[0]),
        ["ln"] = a => Math.Log(a[0]),
        ["log"] = a => a.Length > 1 ? Math.Log(a[0], a[1]) : Math.Log10(a[0]),
        ["abs"] = a => Math.Abs(a[0]),
        ["round"] = a => a.Length > 1 ? Math.Round(a[0], (int)a[1]) : Math.Round(a[0]),
        ["floor"] = a => Math.Floor(a[0]),
        ["ceil"] = a => Math.Ceiling(a[0]),
        ["min"] = a => a.Min(),
        ["max"] = a => a.Max(),
        ["pow"] = a => Math.Pow(a[0], a[1]),
    };

    private double Expression()
    {
        var value = Term();
        while (true)
        {
            SkipSpaces();
            if (Eat('+')) value += Term();
            else if (Eat('-')) value -= Term();
            else return value;
        }
    }

    private double Term()
    {
        var value = Power();
        while (true)
        {
            SkipSpaces();
            if (Eat('*')) value *= Power();
            else if (Eat('/')) value /= Power();
            else if (Eat('%')) value %= Power();
            // Неявное умножение: 2(3+4), 2pi.
            else if (_i < _s.Length && (_s[_i] == '(' || char.IsLetter(_s[_i]))) value *= Power();
            else return value;
        }
    }

    private double Power()
    {
        var value = Unary();
        SkipSpaces();
        // Правоассоциативно: 2^3^2 = 2^9.
        return Eat('^') ? Math.Pow(value, Power()) : value;
    }

    private double Unary()
    {
        SkipSpaces();
        if (Eat('-')) return -Unary();
        if (Eat('+')) return Unary();
        var value = Primary();
        SkipSpaces();
        while (Eat('!')) value = Factorial(value);
        return value;
    }

    private double Primary()
    {
        SkipSpaces();
        if (Eat('('))
        {
            var inner = Expression();
            SkipSpaces();
            if (!Eat(')')) throw new FormatException();
            return inner;
        }

        if (_i < _s.Length && char.IsLetter(_s[_i]))
        {
            var start = _i;
            while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
            var name = _s[start.._i].ToLowerInvariant();
            if (name is "pi" or "π") return Math.PI;
            if (name == "e") return Math.E;
            if (!Functions.TryGetValue(name, out var fn)) throw new FormatException();

            SkipSpaces();
            var args = new List<double>();
            if (Eat('('))
            {
                _inArgs++;
                do
                {
                    args.Add(Expression());
                    SkipSpaces();
                }
                while (Eat(';') || Eat(','));
                _inArgs--;
                if (!Eat(')')) throw new FormatException();
            }
            else
            {
                args.Add(Power());
            }
            return fn([.. args]);
        }

        return Number();
    }

    private double Number()
    {
        SkipSpaces();
        if (_i + 1 < _s.Length && _s[_i] == '0' && _s[_i + 1] is 'x' or 'X')
        {
            var hexStart = _i += 2;
            while (_i < _s.Length && Uri.IsHexDigit(_s[_i])) _i++;
            return long.Parse(_s[hexStart.._i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        var start = _i;
        var separator = false;
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (char.IsDigit(c)) _i++;
            else if ((c == '.' || c == ',' && _inArgs == 0) && !separator && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1]))
            {
                separator = true;
                _i++;
            }
            else break;
        }
        if (start == _i) throw new FormatException();
        return double.Parse(_s[start.._i].Replace(',', '.'), CultureInfo.InvariantCulture);
    }

    private static double Factorial(double n)
    {
        if (n < 0 || n > 170 || n != Math.Floor(n)) throw new FormatException();
        double r = 1;
        for (var k = 2; k <= n; k++) r *= k;
        return r;
    }

    private bool SkipSpaces()
    {
        while (_i < _s.Length && _s[_i] == ' ') _i++;
        return true;
    }

    private bool Eat(char c)
    {
        if (_i < _s.Length && _s[_i] == c)
        {
            _i++;
            return true;
        }
        return false;
    }

    /// <summary>Как показать: целые — с разрядами, дробные — до 12 значащих цифр.</summary>
    public static (string Display, string Copy) Format(double value)
    {
        var culture = CultureInfo.CurrentCulture;
        if (Math.Abs(value) < 1e15 && value == Math.Floor(value))
        {
            var integer = (long)value;
            return (integer.ToString("N0", culture), integer.ToString(CultureInfo.InvariantCulture));
        }
        var rounded = double.Parse(value.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return (rounded.ToString("G12", culture), rounded.ToString("G12", culture));
    }
}
