package dev.spyglass.rider.preview

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.awt.event.KeyEvent

class PreviewLogicTest {
    private val window = """<Window xmlns="https://github.com/avaloniaui" x:Class="A.B">
  <TextBlock/>
</Window>"""

    // ---------- theme injection ----------

    @Test
    fun `dark and light add RequestedThemeVariant to a Window root`() {
        val (dark, edit) = PreviewXaml.applyTheme(window, PreviewTheme.DARK)
        val insert = """ RequestedThemeVariant="Dark" Background="{DynamicResource SystemRegionBrush}""""
        assertTrue(dark.startsWith("<Window$insert xmlns="), dark)
        assertEquals(PreviewXamlEdit(1, 8, insert.length), edit)
        assertTrue(PreviewXaml.applyTheme(window, PreviewTheme.LIGHT).first.contains("RequestedThemeVariant=\"Light\""))
    }

    @Test
    fun `a Window with its own Background keeps it and only gets the variant`() {
        val own = window.replace("<Window ", "<Window Background=\"Red\" ")
        val (out, edit) = PreviewXaml.applyTheme(own, PreviewTheme.DARK)
        assertEquals(1, Regex("Background=").findAll(out).count(), out)
        assertTrue(out.startsWith("""<Window RequestedThemeVariant="Dark" Background="Red" """), out)
        assertEquals(""" RequestedThemeVariant="Dark"""".length, edit.length)
        // A Window.Background property element in the body does not count as the attribute.
        val body = """<Window xmlns="u"><Window.Background><SolidColorBrush/></Window.Background></Window>"""
        assertTrue(PreviewXaml.applyTheme(body, PreviewTheme.LIGHT).first.contains("SystemRegionBrush"))
    }

    @Test
    fun `default theme, a root that sets it, and non-Window roots are untouched`() {
        assertEquals(window to PreviewXamlEdit.NONE, PreviewXaml.applyTheme(window, PreviewTheme.DEFAULT))
        val set = window.replace("<Window ", "<Window RequestedThemeVariant=\"Light\" ")
        assertEquals(set to PreviewXamlEdit.NONE, PreviewXaml.applyTheme(set, PreviewTheme.DARK))
        val control = """<UserControl xmlns="https://github.com/avaloniaui"><TextBlock/></UserControl>"""
        assertEquals(control to PreviewXamlEdit.NONE, PreviewXaml.applyTheme(control, PreviewTheme.DARK))
        val styles = """<Styles xmlns="https://github.com/avaloniaui"/>"""
        assertEquals(styles to PreviewXamlEdit.NONE, PreviewXaml.applyTheme(styles, PreviewTheme.DARK))
        assertEquals("" to PreviewXamlEdit.NONE, PreviewXaml.applyTheme("", PreviewTheme.DARK))
        assertEquals("<" to PreviewXamlEdit.NONE, PreviewXaml.applyTheme("<", PreviewTheme.DARK))
    }

    @Test
    fun `prolog comments and a prefixed Window root are skipped over`() {
        val xaml = "<?xml version=\"1.0\"?>\r\n<!-- <Window> in a comment -->\r\n<!DOCTYPE x>\r\n<w:Window xmlns:w=\"u\">\r\n</w:Window>"
        val (out, edit) = PreviewXaml.applyTheme(xaml, PreviewTheme.DARK)
        assertTrue(out.contains("<w:Window RequestedThemeVariant=\"Dark\" Background=\"{DynamicResource SystemRegionBrush}\" xmlns:w"), out)
        assertEquals(4, edit.line)
        assertEquals("<w:Window".length + 1, edit.column)
    }

    @Test
    fun `a self-closing Window root still gets the attribute`() {
        val (out, _) = PreviewXaml.applyTheme("<Window/>", PreviewTheme.DARK)
        assertEquals("""<Window RequestedThemeVariant="Dark" Background="{DynamicResource SystemRegionBrush}"/>""", out)
    }

    // ---------- error position mapping ----------

    @Test
    fun `error columns after the inserted attribute are shifted back to the editor text`() {
        val edit = PreviewXamlEdit(line = 3, column = 8, length = 30)
        assertEquals(3 to 7, edit.mapBack(3, 7))          // before the insertion
        assertEquals(3 to 8, edit.mapBack(3, 20))         // inside the inserted text → its start
        assertEquals(3 to 10, edit.mapBack(3, 40))        // after it → shifted back
        assertEquals(4 to 40, edit.mapBack(4, 40))        // other lines unchanged
        assertEquals(3 to null, edit.mapBack(3, null))
        assertEquals(null to null, edit.mapBack(null, null))
        assertEquals(3 to 40, PreviewXamlEdit.NONE.mapBack(3, 40))
    }

    @Test
    fun `theme injection and mapping round trip a position on the root line`() {
        val (out, edit) = PreviewXaml.applyTheme(window, PreviewTheme.DARK)
        // The column of `x:Class` in the sent text maps back to its column in the editor's text.
        val sent = out.lineSequence().first().indexOf("x:Class") + 1
        val original = window.lineSequence().first().indexOf("x:Class") + 1
        assertEquals(1 to original, edit.mapBack(1, sent))
    }

    // ---------- zoom ----------

    @Test
    fun `fit shrinks but never enlarges and a fixed zoom is its percentage`() {
        assertEquals(1.0, PreviewZoom.FIT.scale(400.0, 300.0, 800.0, 600.0))
        assertEquals(0.5, PreviewZoom.FIT.scale(800.0, 600.0, 400.0, 300.0))
        assertEquals(0.1, PreviewZoom.FIT.scale(10000.0, 10000.0, 100.0, 100.0))   // floor
        assertEquals(1.0, PreviewZoom.FIT.scale(0.0, 0.0, 100.0, 100.0))
        assertEquals(2.0, PreviewZoom(false, 200.0).scale(400.0, 300.0, 100.0, 100.0))
    }

    @Test
    fun `zoom steps move from the current look`() {
        assertEquals(PreviewZoom(false, 125.0), PreviewZoom.zoomIn(1.0))
        assertEquals(PreviewZoom(false, 75.0), PreviewZoom.zoomOut(1.0))
        assertEquals(PreviewZoom(false, 75.0), PreviewZoom.zoomIn(0.6))        // from a Fit result of 60%
        assertEquals(PreviewZoom(false, 50.0), PreviewZoom.zoomOut(0.6))
        assertEquals(PreviewZoom(false, 400.0), PreviewZoom.zoomIn(4.0))
        assertEquals(PreviewZoom(false, 25.0), PreviewZoom.zoomOut(0.25))
    }

    @Test
    fun `zoom parses and prints the setting`() {
        assertEquals(PreviewZoom.FIT, PreviewZoom.parse(null))
        assertEquals(PreviewZoom.FIT, PreviewZoom.parse("Fit"))
        assertEquals(PreviewZoom.FIT, PreviewZoom.parse("banana"))
        assertEquals(PreviewZoom.FIT, PreviewZoom.parse("5%"))     // out of range
        assertEquals(PreviewZoom(false, 150.0), PreviewZoom.parse(" 150% "))
        assertEquals("fit", PreviewZoom.FIT.toString())
        assertEquals("150%", PreviewZoom(false, 150.0).toString())
        assertEquals("12.5%", PreviewZoom(false, 12.5).toString())
        assertEquals(PreviewZoom.ACTUAL, PreviewZoom.parse(PreviewZoom.ACTUAL.toString()))
    }

    @Test
    fun `the host viewport is the pane undone by the zoom`() {
        assertEquals(800.0 to 600.0, PreviewZoom.FIT.viewport(800.0, 600.0))
        assertEquals(400.0 to 300.0, PreviewZoom(false, 200.0).viewport(800.0, 600.0))
        assertEquals(1600.0 to 1200.0, PreviewZoom(false, 50.0).viewport(800.0, 600.0))
        assertEquals(1.0 to 1.0, PreviewZoom(false, 400.0).viewport(0.0, 0.0))
    }

    @Test
    fun `pointer positions are divided by the display scale`() {
        assertEquals(50.0 to 25.0, PreviewInput.toLogical(100.0, 50.0, 2.0))
        assertEquals(100.0 to 50.0, PreviewInput.toLogical(100.0, 50.0, 0.0))
        assertEquals(192.0, PreviewInput.dpi(2.0))
        assertEquals(96.0, PreviewInput.dpi(0.0))
        assertEquals(384.0, PreviewInput.dpi(9.0))   // clamped to 4×
    }

    // ---------- auto open ----------

    @Test
    fun `autoOpen opens for xaml only when enabled and not already open`() {
        assertTrue(AutoOpenPolicy.shouldOpen(autoOpen = true, previewEnabled = true, isXaml = true, previewVisible = false))
        assertFalse(AutoOpenPolicy.shouldOpen(false, true, true, false))
        assertFalse(AutoOpenPolicy.shouldOpen(true, false, true, false))
        assertFalse(AutoOpenPolicy.shouldOpen(true, true, false, false))
        assertFalse(AutoOpenPolicy.shouldOpen(true, true, true, true))
    }

    // ---------- status line ----------

    @Test
    fun `status line reports updated, updating, error and startup`() {
        fun d(p: PreviewPhase, updating: Boolean = false, ms: Long? = null, err: String? = null, msg: String? = null) =
            PreviewStatus.describe(p, updating, ms, err, msg)
        assertEquals(StatusLevel.OK to "Updated · 42 ms", d(PreviewPhase.READY, ms = 42))
        assertEquals(StatusLevel.BUSY to "Updating…", d(PreviewPhase.READY, updating = true, ms = 42))
        assertEquals(StatusLevel.ERROR to "Error · bad tag", d(PreviewPhase.READY, ms = 5, err = "bad tag"))
        assertEquals(StatusLevel.BUSY to "Starting the previewer…", d(PreviewPhase.STARTING, msg = "Starting the previewer…"))
        assertEquals(StatusLevel.ERROR to "The preview process exited (code 1).", d(PreviewPhase.EXITED, msg = "The preview process exited (code 1)."))
        assertEquals(StatusLevel.ERROR to "x is not built yet.", d(PreviewPhase.UNAVAILABLE, msg = "x is not built yet."))
        assertEquals(StatusLevel.IDLE, d(PreviewPhase.IDLE).first)
        assertEquals(StatusLevel.OK to "Ready", d(PreviewPhase.READY))
    }

    // ---------- keys & modifiers ----------

    @Test
    fun `keys map to the Avalonia Key values`() {
        assertEquals(44, PreviewInput.avaloniaKey(KeyEvent.VK_A))
        assertEquals(69, PreviewInput.avaloniaKey(KeyEvent.VK_Z))
        assertEquals(34, PreviewInput.avaloniaKey(KeyEvent.VK_0))
        assertEquals(6, PreviewInput.avaloniaKey(KeyEvent.VK_ENTER))
        assertEquals(2, PreviewInput.avaloniaKey(KeyEvent.VK_BACK_SPACE))
        assertEquals(26, PreviewInput.avaloniaKey(KeyEvent.VK_DOWN))
        assertEquals(26, PreviewInput.avaloniaKey(KeyEvent.VK_KP_DOWN))
        assertEquals(74, PreviewInput.avaloniaKey(KeyEvent.VK_NUMPAD0))
        assertEquals(101, PreviewInput.avaloniaKey(KeyEvent.VK_F12))
        assertEquals(113, PreviewInput.avaloniaKey(KeyEvent.VK_F24))
        assertEquals(140, PreviewInput.avaloniaKey(KeyEvent.VK_SEMICOLON))
        assertEquals(141, PreviewInput.avaloniaKey(KeyEvent.VK_EQUALS))
        assertEquals(143, PreviewInput.avaloniaKey(KeyEvent.VK_MINUS))
        assertEquals(152, PreviewInput.avaloniaKey(KeyEvent.VK_QUOTE))
        assertEquals(85, PreviewInput.avaloniaKey(KeyEvent.VK_ADD))
        assertEquals(116, PreviewInput.avaloniaKey(KeyEvent.VK_SHIFT, KeyEvent.KEY_LOCATION_LEFT))
        assertEquals(117, PreviewInput.avaloniaKey(KeyEvent.VK_SHIFT, KeyEvent.KEY_LOCATION_RIGHT))
        assertEquals(118, PreviewInput.avaloniaKey(KeyEvent.VK_CONTROL))
        assertEquals(70, PreviewInput.avaloniaKey(KeyEvent.VK_META))
        assertNull(PreviewInput.avaloniaKey(KeyEvent.VK_UNDEFINED))
        // Every value is a defined Avalonia Key (0 < key < 172), and A–Z, 0–9 and F1–F24 are all distinct.
        val all = (0..0x10000).mapNotNull { PreviewInput.avaloniaKey(it) }
        assertTrue(all.all { it in 1..171 })
    }

    @Test
    fun `modifiers are single values and wheel notches point up`() {
        assertEquals(listOf(0, 1, 2, 3, 4), PreviewInput.modifiers(control = true, shift = true, alt = true, meta = true, left = true))
        assertEquals(emptyList<Int>(), PreviewInput.modifiers(false, false, false))
        assertEquals(listOf(5, 6), PreviewInput.modifiers(false, false, false, right = true, middle = true))
        assertEquals(1.0, PreviewInput.wheelNotches(-1.0))   // Swing: negative = up
    }

    @Test
    fun `theme setting parses leniently`() {
        assertEquals(PreviewTheme.DARK, PreviewTheme.parse("Dark"))
        assertEquals(PreviewTheme.LIGHT, PreviewTheme.parse(" light "))
        assertEquals(PreviewTheme.DEFAULT, PreviewTheme.parse("nope"))
        assertEquals(PreviewTheme.DEFAULT, PreviewTheme.parse(null))
    }
}
