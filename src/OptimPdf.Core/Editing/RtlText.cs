using System.Text;

namespace OptimPdf.Core.Editing;

/// <summary>
/// Right-to-left text for the PDF writer, which draws glyphs left to right without shaping:
/// Arabic letters are replaced by their contextual presentation forms (with lam-alef ligatures), and each line
/// is reordered from logical to visual order with the Unicode Bidirectional Algorithm (UAX #9) for a single
/// paragraph without explicit embeddings. Covers Arabic, Persian and Urdu base letters and Hebrew.
/// </summary>
/// <remarks>
/// Not covered: OpenType features beyond presentation forms (optional ligatures, kashida justification,
/// exact mark positioning: diacritics are drawn after their letter and may sit slightly off), explicit bidi
/// controls (embeddings/isolates are ignored), and Arabic letters without presentation forms (Arabic Supplement
/// and Extended blocks), which <see cref="ContentEditor.NeedsComplexShaping"/> still refuses.
/// </remarks>
public static class RtlText
{
    public static bool HasRtl(string text) => text.Any(c => IsRtlLetter(c));

    /// <summary>
    /// Whether a character of the Arabic block (U+0600–U+06FF) can be laid out: letters with presentation forms,
    /// diacritics, digits and punctuation. Other letters (e.g. Urdu ٹ ڈ ڑ ں ھ ہ ے) would be drawn unjoined.
    /// </summary>
    public static bool CanShape(char c) =>
        Forms.ContainsKey(c) || IsTransparent(c) || c == Tatweel || c is >= '؀' and <= '؟' || c is >= '٠' and <= '٭'
        || c is >= '۰' and <= '۹' || c == '۔';

    static bool IsRtlLetter(char c) => c is >= '֐' and <= '׿' or >= '؀' and <= 'ۿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    /// <summary>True when the first strong character is right-to-left (the paragraph reads right to left).</summary>
    public static bool IsRtlParagraph(string text)
    {
        foreach (var c in text)
        {
            var t = Classify(c);
            if (t == BidiClass.L) return false;
            if (t is BidiClass.R or BidiClass.AL) return true;
        }
        return false;
    }

    /// <summary>Shapes and reorders one line for left-to-right drawing.</summary>
    public static string ToVisual(string line, bool? rtlParagraph = null) => Reorder(Shape(line), rtlParagraph ?? IsRtlParagraph(line));

    // ---------------- Arabic shaping ----------------

    // Code point → (isolated, final, initial, medial); initial/medial 0 for right-joining letters.
    static readonly Dictionary<char, (char Iso, char Fin, char Ini, char Med)> Forms = new()
    {
        ['ء'] = ('ﺀ', '\0', '\0', '\0'),
        ['آ'] = ('ﺁ', 'ﺂ', '\0', '\0'), ['أ'] = ('ﺃ', 'ﺄ', '\0', '\0'),
        ['ؤ'] = ('ﺅ', 'ﺆ', '\0', '\0'), ['إ'] = ('ﺇ', 'ﺈ', '\0', '\0'),
        ['ئ'] = ('ﺉ', 'ﺊ', 'ﺋ', 'ﺌ'), ['ا'] = ('ﺍ', 'ﺎ', '\0', '\0'),
        ['ب'] = ('ﺏ', 'ﺐ', 'ﺑ', 'ﺒ'), ['ة'] = ('ﺓ', 'ﺔ', '\0', '\0'),
        ['ت'] = ('ﺕ', 'ﺖ', 'ﺗ', 'ﺘ'), ['ث'] = ('ﺙ', 'ﺚ', 'ﺛ', 'ﺜ'),
        ['ج'] = ('ﺝ', 'ﺞ', 'ﺟ', 'ﺠ'), ['ح'] = ('ﺡ', 'ﺢ', 'ﺣ', 'ﺤ'),
        ['خ'] = ('ﺥ', 'ﺦ', 'ﺧ', 'ﺨ'), ['د'] = ('ﺩ', 'ﺪ', '\0', '\0'),
        ['ذ'] = ('ﺫ', 'ﺬ', '\0', '\0'), ['ر'] = ('ﺭ', 'ﺮ', '\0', '\0'),
        ['ز'] = ('ﺯ', 'ﺰ', '\0', '\0'), ['س'] = ('ﺱ', 'ﺲ', 'ﺳ', 'ﺴ'),
        ['ش'] = ('ﺵ', 'ﺶ', 'ﺷ', 'ﺸ'), ['ص'] = ('ﺹ', 'ﺺ', 'ﺻ', 'ﺼ'),
        ['ض'] = ('ﺽ', 'ﺾ', 'ﺿ', 'ﻀ'), ['ط'] = ('ﻁ', 'ﻂ', 'ﻃ', 'ﻄ'),
        ['ظ'] = ('ﻅ', 'ﻆ', 'ﻇ', 'ﻈ'), ['ع'] = ('ﻉ', 'ﻊ', 'ﻋ', 'ﻌ'),
        ['غ'] = ('ﻍ', 'ﻎ', 'ﻏ', 'ﻐ'), ['ف'] = ('ﻑ', 'ﻒ', 'ﻓ', 'ﻔ'),
        ['ق'] = ('ﻕ', 'ﻖ', 'ﻗ', 'ﻘ'), ['ك'] = ('ﻙ', 'ﻚ', 'ﻛ', 'ﻜ'),
        ['ل'] = ('ﻝ', 'ﻞ', 'ﻟ', 'ﻠ'), ['م'] = ('ﻡ', 'ﻢ', 'ﻣ', 'ﻤ'),
        ['ن'] = ('ﻥ', 'ﻦ', 'ﻧ', 'ﻨ'), ['ه'] = ('ﻩ', 'ﻪ', 'ﻫ', 'ﻬ'),
        ['و'] = ('ﻭ', 'ﻮ', '\0', '\0'), ['ى'] = ('ﻯ', 'ﻰ', '\0', '\0'),
        ['ي'] = ('ﻱ', 'ﻲ', 'ﻳ', 'ﻴ'),
        // Persian and Urdu letters (Presentation Forms-A).
        ['پ'] = ('ﭖ', 'ﭗ', 'ﭘ', 'ﭙ'), ['چ'] = ('ﭺ', 'ﭻ', 'ﭼ', 'ﭽ'),
        ['ژ'] = ('ﮊ', 'ﮋ', '\0', '\0'), ['ک'] = ('ﮎ', 'ﮏ', 'ﮐ', 'ﮑ'),
        ['گ'] = ('ﮒ', 'ﮓ', 'ﮔ', 'ﮕ'), ['ی'] = ('ﯼ', 'ﯽ', 'ﯾ', 'ﯿ'),
    };

    // Lam + alef variant → (isolated, final) ligature.
    static readonly Dictionary<char, (char Iso, char Fin)> LamAlef = new()
    {
        ['آ'] = ('ﻵ', 'ﻶ'), ['أ'] = ('ﻷ', 'ﻸ'),
        ['إ'] = ('ﻹ', 'ﻺ'), ['ا'] = ('ﻻ', 'ﻼ'),
    };

    const char Tatweel = 'ـ';

    static bool IsTransparent(char c) => c is >= 'ً' and <= 'ٟ' or 'ٰ' or >= 'ۖ' and <= 'ۭ';

    /// <summary>Can this letter connect to the following (next in logical order) letter?</summary>
    static bool JoinsForward(char c) => c == Tatweel || (Forms.TryGetValue(c, out var f) && f.Ini != '\0');

    static bool Joins(char c) => c == Tatweel || (Forms.TryGetValue(c, out var f) && f.Fin != '\0');

    /// <summary>Replaces Arabic letters with contextual presentation forms (logical order is kept).</summary>
    public static string Shape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!Forms.TryGetValue(c, out var forms)) { sb.Append(c); continue; }

            char prev = Neighbor(text, i, -1), next = Neighbor(text, i, +1);
            bool joinPrev = prev != '\0' && JoinsForward(prev);

            if (c == 'ل' && i + 1 < text.Length && LamAlef.TryGetValue(text[i + 1], out var lig))
            {
                sb.Append(joinPrev ? lig.Fin : lig.Iso);
                i++; // The alef is part of the ligature.
                continue;
            }

            bool joinNext = next != '\0' && Joins(next) && forms.Ini != '\0';
            sb.Append((joinPrev, joinNext) switch
            {
                (true, true) => forms.Med,
                (true, false) => forms.Fin != '\0' ? forms.Fin : forms.Iso,
                (false, true) => forms.Ini,
                _ => forms.Iso,
            });
        }
        return sb.ToString();
    }

    static char Neighbor(string text, int i, int step)
    {
        for (int j = i + step; j >= 0 && j < text.Length; j += step)
            if (!IsTransparent(text[j])) return text[j];
        return '\0';
    }

    // ---------------- Bidi (UAX #9, single paragraph, implicit levels) ----------------

    enum BidiClass { L, R, AL, EN, AN, ES, ET, CS, NSM, WS, ON, B }

    static BidiClass Classify(char c)
    {
        if (c is >= '0' and <= '9') return BidiClass.EN;
        if (c is >= '٠' and <= '٩' or >= '۰' and <= '۹' or '٫' or '٬') return c is >= '۰' and <= '۹' ? BidiClass.EN : BidiClass.AN;
        if (c is '+' or '-' or '−') return BidiClass.ES;
        if (c is '#' or '$' or '%' or '°' or '¢' or '£' or '¥' or '€' or '٪' or '‰') return BidiClass.ET;
        if (c is ',' or '.' or ':' or '/' or ' ' or '،') return BidiClass.CS;
        if (c is ' ' or '\t') return BidiClass.WS;
        if (c is '\n' or '\r') return BidiClass.B;
        if (c is >= '֑' and <= 'ׇ' && c is not ('־' or '׀' or '׃' or '׆')) return BidiClass.NSM;
        if (IsTransparent(c)) return BidiClass.NSM;
        if (c is >= '֐' and <= '׿' or >= 'יִ' and <= 'ﭏ') return BidiClass.R;
        if (c is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return BidiClass.AL;
        var cat = char.GetUnicodeCategory(c);
        if (cat is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark) return BidiClass.NSM;
        return char.IsLetter(c) || cat == System.Globalization.UnicodeCategory.SpacingCombiningMark ? BidiClass.L : BidiClass.ON;
    }

    static string Reorder(string text, bool rtl)
    {
        int n = text.Length;
        if (n == 0) return text;
        int baseLevel = rtl ? 1 : 0;
        var e = rtl ? BidiClass.R : BidiClass.L;
        var t = text.Select(Classify).ToArray();

        // W1: NSM takes the type of the previous character.
        for (int i = 0; i < n; i++) if (t[i] == BidiClass.NSM) t[i] = i == 0 ? e : t[i - 1];
        // W2: EN after AL becomes AN. W3: AL becomes R.
        var lastStrong = e;
        for (int i = 0; i < n; i++)
        {
            if (t[i] is BidiClass.L or BidiClass.R or BidiClass.AL) lastStrong = t[i];
            else if (t[i] == BidiClass.EN && lastStrong == BidiClass.AL) t[i] = BidiClass.AN;
        }
        for (int i = 0; i < n; i++) if (t[i] == BidiClass.AL) t[i] = BidiClass.R;
        // W4: a single separator between two numbers of the same kind joins them.
        for (int i = 1; i < n - 1; i++)
        {
            if (t[i] == BidiClass.ES && t[i - 1] == BidiClass.EN && t[i + 1] == BidiClass.EN) t[i] = BidiClass.EN;
            else if (t[i] == BidiClass.CS && t[i - 1] == t[i + 1] && t[i - 1] is BidiClass.EN or BidiClass.AN) t[i] = t[i - 1];
        }
        // W5: terminators next to European numbers become numbers.
        for (int i = 0; i < n; i++)
        {
            if (t[i] != BidiClass.ET) continue;
            int j = i;
            while (j < n && t[j] == BidiClass.ET) j++;
            bool touches = (i > 0 && t[i - 1] == BidiClass.EN) || (j < n && t[j] == BidiClass.EN);
            for (int k = i; k < j; k++) t[k] = touches ? BidiClass.EN : BidiClass.ON;
            i = j - 1;
        }
        // W6: remaining separators and terminators are neutral.
        for (int i = 0; i < n; i++) if (t[i] is BidiClass.ES or BidiClass.ET or BidiClass.CS) t[i] = BidiClass.ON;
        // W7: European numbers after L (or at an LTR start) are L.
        lastStrong = e;
        for (int i = 0; i < n; i++)
        {
            if (t[i] is BidiClass.L or BidiClass.R) lastStrong = t[i];
            else if (t[i] == BidiClass.EN && lastStrong == BidiClass.L) t[i] = BidiClass.L;
        }
        // N1/N2: neutrals take the surrounding direction when both sides agree, else the paragraph direction.
        for (int i = 0; i < n; i++)
        {
            if (t[i] is not (BidiClass.ON or BidiClass.WS or BidiClass.B)) continue;
            int j = i;
            while (j < n && t[j] is BidiClass.ON or BidiClass.WS or BidiClass.B) j++;
            var before = i == 0 ? e : Strong(t[i - 1]);
            var after = j == n ? e : Strong(t[j]);
            var resolved = before == after ? before : e;
            for (int k = i; k < j; k++) t[k] = resolved;
            i = j - 1;
        }
        // I1/I2: implicit levels.
        var levels = new int[n];
        for (int i = 0; i < n; i++)
            levels[i] = baseLevel % 2 == 0
                ? t[i] switch { BidiClass.R => 1, BidiClass.AN or BidiClass.EN => 2, _ => 0 }
                : t[i] switch { BidiClass.L or BidiClass.EN or BidiClass.AN => 2, _ => 1 };
        // L1: trailing whitespace goes to the paragraph level.
        for (int i = n - 1; i >= 0 && text[i] is ' ' or '\t'; i--) levels[i] = baseLevel;

        // L2: reverse runs from the highest level down to the lowest odd level. Marks stay after their base (L3).
        var units = Clusters(text, levels);
        int max = units.Max(u => u.Level), minOdd = units.Where(u => u.Level % 2 == 1).Select(u => u.Level).DefaultIfEmpty(max + 1).Min();
        for (int level = max; level >= minOdd; level--)
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].Level < level) continue;
                int j = i;
                while (j < units.Count && units[j].Level >= level) j++;
                units.Reverse(i, j - i);
                i = j;
            }

        var sb = new StringBuilder(n);
        foreach (var u in units) sb.Append(u.Level % 2 == 1 ? Mirror(u.Text) : u.Text);
        return sb.ToString();
    }

    static BidiClass Strong(BidiClass c) => c is BidiClass.EN or BidiClass.AN ? BidiClass.R : c is BidiClass.L ? BidiClass.L : BidiClass.R;

    static List<(string Text, int Level)> Clusters(string text, int[] levels)
    {
        var list = new List<(string, int)>();
        for (int i = 0; i < text.Length; i++)
        {
            int j = i + 1;
            while (j < text.Length && (IsTransparent(text[j]) || Classify(text[j]) == BidiClass.NSM || char.IsLowSurrogate(text[j]))) j++;
            list.Add((text[i..j], levels[i]));
            i = j - 1;
        }
        return list;
    }

    static string Mirror(string s) => s.Length != 1 ? s : s[0] switch
    {
        '(' => ")", ')' => "(", '[' => "]", ']' => "[", '{' => "}", '}' => "{", '<' => ">", '>' => "<", '«' => "»", '»' => "«",
        _ => s,
    };
}
