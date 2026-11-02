namespace Spyglass.VisualStudio.Core;

/// <summary>A 0-based, end-exclusive span in a document, as editors want it.</summary>
public readonly record struct TextSpan(int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>Where to draw a squiggle for a 1-based hub diagnostic (like the VS Code extension: the word at the column).</summary>
public static class DiagnosticRange
{
    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '{' or '}' or '-';

    public static TextSpan Compute(string? text, int? line, int? column)
    {
        var l = Math.Max(0, (line ?? 1) - 1);
        var c = Math.Max(0, (column ?? 1) - 1);
        var lines = text?.Split('\n');
        if (lines is null || l >= lines.Length) return new(l, c, l, c + 1);

        var s = lines[l].TrimEnd('\r');
        if (c < s.Length && IsWordChar(s[c]))
        {
            var start = c;
            var end = c;
            while (start > 0 && IsWordChar(s[start - 1])) start--;
            while (end < s.Length && IsWordChar(s[end])) end++;
            return new(l, start, l, end);
        }
        // No word at the column (or no column): the line's text without its indentation.
        var first = s.Length - s.TrimStart().Length;
        return s.Trim().Length == 0 ? new(l, 0, l, Math.Max(1, s.Length)) : new(l, first, l, s.TrimEnd().Length);
    }
}
