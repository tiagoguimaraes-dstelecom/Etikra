namespace Etikra.Services;

/// <summary>Small dependency-free Code 128-B encoder used by the editor and rasterizer.</summary>
public static class Code128Encoder
{
    // Bar/space widths for symbols 0-106. The stop symbol is intentionally 13 modules.
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112"
    ];

    public static IReadOnlyList<int> Encode(string value)
    {
        var normalized = string.Concat((value ?? string.Empty).Where(ch => ch is >= ' ' and <= '~'));
        if (normalized.Length == 0)
        {
            normalized = " ";
        }

        var symbols = new List<int>(normalized.Length + 3) { 104 }; // Start B
        var checksum = 104;
        for (var i = 0; i < normalized.Length; i++)
        {
            var code = normalized[i] - 32;
            symbols.Add(code);
            checksum += code * (i + 1);
        }

        symbols.Add(checksum % 103);
        symbols.Add(106);
        return symbols;
    }

    public static IReadOnlyList<(bool IsBar, int Modules)> GetRuns(string value)
    {
        var runs = new List<(bool, int)> { (false, 10) };
        foreach (var symbol in Encode(value))
        {
            var pattern = Patterns[symbol];
            for (var i = 0; i < pattern.Length; i++)
            {
                runs.Add((i % 2 == 0, pattern[i] - '0'));
            }
        }
        runs.Add((false, 10));
        return runs;
    }
}
