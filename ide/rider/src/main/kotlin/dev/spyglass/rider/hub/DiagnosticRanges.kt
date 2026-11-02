package dev.spyglass.rider.hub

/** Where a diagnostic is drawn; no IntelliJ dependency so it can be unit-tested. */
object DiagnosticRanges {
    private fun isWordChar(c: Char) = c.isLetterOrDigit() || c == '_' || c == ':' || c == '.' || c == '{' || c == '}' || c == '-'

    /**
     * Offsets [start, end) for a 1-based [line]/[column] in [text] (lines split at '\n'): the word at the column (like
     * VS Code's getWordRangeAtPosition with `[\w:.{}-]+`), else the line without its indentation, else one character.
     */
    fun range(text: CharSequence, line: Int?, column: Int?): IntRange {
        // Line starts.
        val l0 = maxOf(0, (line ?: 1) - 1)
        var lineStart = 0
        var n = 0
        while (n < l0) {
            val i = indexOf(text, '\n', lineStart)
            if (i < 0) return lastChar(text)
            lineStart = i + 1
            n++
        }
        var lineEnd = indexOf(text, '\n', lineStart).let { if (it < 0) text.length else it }
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r') lineEnd--
        val col = lineStart + maxOf(0, (column ?: 1) - 1)
        if (column != null && col < lineEnd && isWordChar(text[col])) {
            var s = col
            var e = col
            while (s > lineStart && isWordChar(text[s - 1])) s--
            while (e < lineEnd && isWordChar(text[e])) e++
            return s until e
        }
        var s = lineStart
        while (s < lineEnd && text[s].isWhitespace()) s++
        return if (s < lineEnd) s until lineEnd else lastChar(text, lineStart)
    }

    private fun lastChar(text: CharSequence, at: Int = text.length - 1): IntRange {
        if (text.isEmpty()) return 0 until 0
        val s = at.coerceIn(0, text.length - 1)
        return s until s + 1
    }

    private fun indexOf(text: CharSequence, c: Char, from: Int): Int {
        for (i in from until text.length) if (text[i] == c) return i
        return -1
    }
}
