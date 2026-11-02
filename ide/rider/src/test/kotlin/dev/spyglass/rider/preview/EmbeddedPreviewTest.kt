package dev.spyglass.rider.preview

import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class EmbeddedPreviewTest {
    @Test fun avalonia_files_are_ours() {
        assertTrue(EmbeddedPreview.accepts("axaml", "AVALONIA"))
        assertTrue(EmbeddedPreview.accepts("xaml", "AVALONIA"))   // Avalonia projects may still use .xaml
    }

    @Test fun undefined_platform_is_ours_only_for_axaml() {
        assertTrue(EmbeddedPreview.accepts("axaml", "UNDEFINED"))
        assertTrue(EmbeddedPreview.accepts("AXAML", "UNDEFINED"))
        assertFalse(EmbeddedPreview.accepts("xaml", "UNDEFINED"))
        assertFalse(EmbeddedPreview.accepts(null, "UNDEFINED"))
    }

    @Test fun wpf_and_noesis_are_left_to_rider() {
        assertFalse(EmbeddedPreview.accepts("xaml", "WPF"))
        assertFalse(EmbeddedPreview.accepts("axaml", "WPF"))
        assertFalse(EmbeddedPreview.accepts("xaml", "NOESIS"))
    }
}
