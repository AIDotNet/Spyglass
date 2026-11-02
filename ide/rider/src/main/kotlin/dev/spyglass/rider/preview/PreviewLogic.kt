// Pure preview logic (no IntelliJ, no Swing state): theme injection and error-position mapping, zoom, auto-open policy,
// status text and key mapping. Kept in step with the Visual Studio implementation (ide/vs/Spyglass.VisualStudio.Core/Preview).
package dev.spyglass.rider.preview

import java.awt.event.KeyEvent
import java.util.Locale

/** The theme variant the previewed XAML is rendered with (RequestedThemeVariant). */
enum class PreviewTheme(val id: String) {
    DEFAULT("default"), LIGHT("light"), DARK("dark");

    companion object {
        fun parse(text: String?): PreviewTheme = entries.firstOrNull { it.id.equals(text?.trim(), ignoreCase = true) } ?: DEFAULT
    }
}

/**
 * A change made to the root start tag before the XAML is sent: an attribute inserted at ([line], [column]), 1-based.
 * Host error positions on that line that lie after the insertion are shifted back so they match the editor's text.
 */
data class PreviewXamlEdit(val line: Int = 0, val column: Int = 0, val length: Int = 0) {
    val isEmpty: Boolean get() = length == 0

    /** Maps a position in the edited XAML back to the editor's text. */
    fun mapBack(line: Int?, column: Int?): Pair<Int?, Int?> {
        if (isEmpty || line != this.line || column == null) return line to column
        return line to when {
            column >= this.column + length -> column - length
            column < this.column -> column
            else -> this.column
        }
    }

    companion object { val NONE = PreviewXamlEdit() }
}

object PreviewXaml {
    private const val ATTRIBUTE = "RequestedThemeVariant"
    private val BACKGROUND_ATTRIBUTE = Regex("""\sBackground\s*=""")

    /**
     * Adds `RequestedThemeVariant="Light|Dark"` (and, when the root has no Background, `Background="{DynamicResource SystemRegionBrush}"`) to the root element (the previewer has no theme message). A root that already
     * sets it is left alone, and so is [PreviewTheme.DEFAULT]. Only a `Window` root takes the property (the previewer rejects
     * it on a UserControl, which would turn the preview into an error), so any other root is sent unchanged.
     */
    fun applyTheme(xaml: String, theme: PreviewTheme): Pair<String, PreviewXamlEdit> {
        if (theme == PreviewTheme.DEFAULT) return xaml to PreviewXamlEdit.NONE
        val nameEnd = findRootNameEnd(xaml)
        if (nameEnd < 0) return xaml to PreviewXamlEdit.NONE
        val nameStart = xaml.lastIndexOf('<', nameEnd - 1) + 1
        val name = xaml.substring(nameStart, nameEnd)
        if (name.substringAfterLast(':') != "Window") return xaml to PreviewXamlEdit.NONE
        val tagEnd = xaml.indexOf('>', nameEnd)
        if (tagEnd < 0) return xaml to PreviewXamlEdit.NONE
        val startTag = xaml.substring(nameEnd, tagEnd)
        if (startTag.contains(ATTRIBUTE)) return xaml to PreviewXamlEdit.NONE

        // The previewer paints a Window without any theme background, so the variant alone shows nothing: give a Window that
        // has no Background of its own the theme's region brush. One insertion, so error columns map back with one shift.
        val background = if (BACKGROUND_ATTRIBUTE.containsMatchIn(startTag)) "" else " Background=\"{DynamicResource SystemRegionBrush}\""
        val insert = " $ATTRIBUTE=\"${if (theme == PreviewTheme.DARK) "Dark" else "Light"}\"$background"
        var line = 1
        var lineStart = 0
        for (i in 0 until nameEnd) if (xaml[i] == '\n') { line++; lineStart = i + 1 }
        return xaml.substring(0, nameEnd) + insert + xaml.substring(nameEnd) to PreviewXamlEdit(line, nameEnd - lineStart + 1, insert.length)
    }

    /** Index just after the root element's name, skipping the XML declaration, comments and DOCTYPE; -1 if none. */
    internal fun findRootNameEnd(s: String): Int {
        var i = 0
        while (i < s.length) {
            val lt = s.indexOf('<', i)
            if (lt < 0 || lt + 1 >= s.length) return -1
            if (s.startsWith("<?", lt)) { val e = s.indexOf("?>", lt); if (e < 0) return -1; i = e + 2; continue }
            if (s.startsWith("<!--", lt)) { val e = s.indexOf("-->", lt); if (e < 0) return -1; i = e + 3; continue }
            if (s.startsWith("<!", lt)) { val e = s.indexOf('>', lt); if (e < 0) return -1; i = e + 1; continue }
            var j = lt + 1
            while (j < s.length && !s[j].isWhitespace() && s[j] != '>' && s[j] != '/') j++
            return if (j > lt + 1) j else -1
        }
        return -1
    }
}

/**
 * How the picture is scaled in the pane: fit the pane (shrinking, never enlarging past 100%) or a fixed percentage. All sizes
 * are device-independent pixels (1/96 inch).
 */
data class PreviewZoom(val fit: Boolean, val percent: Double) {
    /** The scale factor for a picture of the given logical size in a pane of the given size. */
    fun scale(frameWidth: Double, frameHeight: Double, paneWidth: Double, paneHeight: Double): Double {
        if (!fit) return percent / 100
        if (frameWidth <= 0 || frameHeight <= 0 || paneWidth <= 0 || paneHeight <= 0) return 1.0
        return minOf(paneWidth / frameWidth, paneHeight / frameHeight).coerceIn(0.1, 1.0)
    }

    /** The viewport to tell the previewer (logical size): the pane, undone by the zoom (Fit asks for the whole pane). */
    fun viewport(paneWidth: Double, paneHeight: Double): Pair<Double, Double> {
        val s = if (fit) 1.0 else percent / 100
        return maxOf(1.0, Math.floor(paneWidth / s)) to maxOf(1.0, Math.floor(paneHeight / s))
    }

    override fun toString(): String = if (fit) "fit" else (if (percent % 1.0 == 0.0) percent.toLong().toString() else percent.toString()) + "%"

    companion object {
        val FIT = PreviewZoom(true, 100.0)
        val ACTUAL = PreviewZoom(false, 100.0)
        val STEPS = listOf(25.0, 50.0, 75.0, 100.0, 125.0, 150.0, 200.0, 300.0, 400.0)

        /** The next larger step above [currentScale] (the current look, also when it came from Fit). */
        fun zoomIn(currentScale: Double): PreviewZoom {
            val pct = currentScale * 100
            return PreviewZoom(false, STEPS.firstOrNull { it > pct + 0.5 } ?: STEPS.last())
        }

        fun zoomOut(currentScale: Double): PreviewZoom {
            val pct = currentScale * 100
            return PreviewZoom(false, STEPS.lastOrNull { it < pct - 0.5 } ?: STEPS.first())
        }

        fun parse(text: String?): PreviewZoom {
            val t = text?.trim()?.trimEnd('%')?.trim()
            if (t.isNullOrEmpty() || t.equals("fit", ignoreCase = true)) return FIT
            val p = t.toDoubleOrNull() ?: return FIT
            return if (p in 10.0..800.0) PreviewZoom(false, p) else FIT
        }
    }
}

/** When to open the preview by itself. */
object AutoOpenPolicy {
    /** A file was opened in an editor: open the preview for it? (Never when it is already open, so nothing is stolen twice.) */
    fun shouldOpen(autoOpen: Boolean, previewEnabled: Boolean, isXaml: Boolean, previewVisible: Boolean): Boolean =
        autoOpen && previewEnabled && isXaml && !previewVisible
}

enum class StatusLevel { IDLE, BUSY, OK, ERROR }

/** The one-line status under the picture: "Updated · 120 ms", "Updating…", "Error: …", startup and exit messages. */
object PreviewStatus {
    fun describe(phase: PreviewPhase, updating: Boolean, lastUpdateMs: Long?, error: String?, message: String?): Pair<StatusLevel, String> = when {
        phase == PreviewPhase.DISABLED || phase == PreviewPhase.IDLE -> StatusLevel.IDLE to (message ?: "Preview is idle")
        phase == PreviewPhase.UNAVAILABLE -> StatusLevel.ERROR to (message ?: "Preview is unavailable")
        phase == PreviewPhase.EXITED -> StatusLevel.ERROR to (message ?: "The preview process exited")
        phase == PreviewPhase.LOADING || phase == PreviewPhase.STARTING -> StatusLevel.BUSY to (message ?: "Starting…")
        updating -> StatusLevel.BUSY to "Updating…"
        error != null -> StatusLevel.ERROR to "Error · ${error}"
        lastUpdateMs != null -> StatusLevel.OK to "Updated · $lastUpdateMs ms"
        else -> StatusLevel.OK to "Ready"
    }
}

/** AWT → previewer input translation. */
object PreviewInput {
    /** The DPI to render at for an IDE scale factor (1.0 = 96, 2.0 = 192…). */
    fun dpi(scale: Double): Double = 96 * (if (scale <= 0) 1.0 else scale).coerceIn(0.5, 4.0)

    /** A position inside the (scaled) picture, in DIPs from its top-left → the previewer's logical position. */
    fun toLogical(pictureX: Double, pictureY: Double, scale: Double): Pair<Double, Double> {
        val s = if (scale <= 0) 1.0 else scale
        return pictureX / s to pictureY / s
    }

    /** AWT virtual key (+ key location for Shift/Ctrl/Alt) → Avalonia `Key` (Avalonia.Remote.Protocol.Input.Key); null when unmapped. */
    fun avaloniaKey(vk: Int, location: Int = KeyEvent.KEY_LOCATION_STANDARD): Int? {
        val right = location == KeyEvent.KEY_LOCATION_RIGHT
        return when (vk) {
            in KeyEvent.VK_A..KeyEvent.VK_Z -> 44 + (vk - KeyEvent.VK_A)
            in KeyEvent.VK_0..KeyEvent.VK_9 -> 34 + (vk - KeyEvent.VK_0)
            in KeyEvent.VK_NUMPAD0..KeyEvent.VK_NUMPAD9 -> 74 + (vk - KeyEvent.VK_NUMPAD0)
            in KeyEvent.VK_F1..KeyEvent.VK_F12 -> 90 + (vk - KeyEvent.VK_F1)
            in KeyEvent.VK_F13..KeyEvent.VK_F24 -> 102 + (vk - KeyEvent.VK_F13)
            KeyEvent.VK_CANCEL -> 1
            KeyEvent.VK_BACK_SPACE -> 2
            KeyEvent.VK_TAB -> 3
            KeyEvent.VK_CLEAR -> 5
            KeyEvent.VK_ENTER -> 6
            KeyEvent.VK_PAUSE -> 7
            KeyEvent.VK_CAPS_LOCK -> 8
            KeyEvent.VK_ESCAPE -> 13
            KeyEvent.VK_SPACE -> 18
            KeyEvent.VK_PAGE_UP -> 19
            KeyEvent.VK_PAGE_DOWN -> 20
            KeyEvent.VK_END -> 21
            KeyEvent.VK_HOME -> 22
            KeyEvent.VK_LEFT, KeyEvent.VK_KP_LEFT -> 23
            KeyEvent.VK_UP, KeyEvent.VK_KP_UP -> 24
            KeyEvent.VK_RIGHT, KeyEvent.VK_KP_RIGHT -> 25
            KeyEvent.VK_DOWN, KeyEvent.VK_KP_DOWN -> 26
            KeyEvent.VK_PRINTSCREEN -> 30
            KeyEvent.VK_INSERT -> 31
            KeyEvent.VK_DELETE -> 32
            KeyEvent.VK_HELP -> 33
            KeyEvent.VK_WINDOWS, KeyEvent.VK_META -> if (right) 71 else 70
            KeyEvent.VK_CONTEXT_MENU -> 72
            KeyEvent.VK_MULTIPLY -> 84
            KeyEvent.VK_ADD -> 85
            KeyEvent.VK_SEPARATOR -> 86
            KeyEvent.VK_SUBTRACT -> 87
            KeyEvent.VK_DECIMAL -> 88
            KeyEvent.VK_DIVIDE -> 89
            KeyEvent.VK_NUM_LOCK -> 114
            KeyEvent.VK_SCROLL_LOCK -> 115
            KeyEvent.VK_SHIFT -> if (right) 117 else 116
            KeyEvent.VK_CONTROL -> if (right) 119 else 118
            KeyEvent.VK_ALT -> if (right) 121 else 120
            KeyEvent.VK_SEMICOLON -> 140
            KeyEvent.VK_EQUALS -> 141
            KeyEvent.VK_COMMA -> 142
            KeyEvent.VK_MINUS -> 143
            KeyEvent.VK_PERIOD -> 144
            KeyEvent.VK_SLASH -> 145
            KeyEvent.VK_BACK_QUOTE -> 146
            KeyEvent.VK_OPEN_BRACKET -> 149
            KeyEvent.VK_BACK_SLASH -> 150
            KeyEvent.VK_CLOSE_BRACKET -> 151
            KeyEvent.VK_QUOTE -> 152
            else -> null
        }
    }

    /** The modifier array the previewer expects (each element is one modifier). */
    fun modifiers(control: Boolean, shift: Boolean, alt: Boolean, meta: Boolean = false, left: Boolean = false, right: Boolean = false, middle: Boolean = false): List<Int> {
        val list = ArrayList<Int>(5)
        if (alt) list += Modifiers.ALT
        if (control) list += Modifiers.CONTROL
        if (shift) list += Modifiers.SHIFT
        if (meta) list += Modifiers.META
        if (left) list += Modifiers.LEFT_BUTTON
        if (right) list += Modifiers.RIGHT_BUTTON
        if (middle) list += Modifiers.MIDDLE_BUTTON
        return list
    }

    /** A Swing wheel rotation (positive = down) → the previewer's scroll delta (notches, up positive). */
    fun wheelNotches(preciseRotation: Double): Double = -preciseRotation

    fun label(zoom: PreviewZoom): String = if (zoom.fit) "Fit" else zoom.toString().uppercase(Locale.ROOT)
}
