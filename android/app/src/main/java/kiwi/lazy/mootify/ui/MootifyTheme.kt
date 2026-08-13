package kiwi.lazy.mootify.ui

import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color

/**
 * The website's palette: ink, cream and brass. Fixed rather than dynamic-colour, so the app looks
 * like Mootify on any phone — and because the same three colours are what the car shows behind the
 * album art it fetches.
 *
 * **Dark only, deliberately.** Mootify's website has one palette and it is this one; there is no
 * light stylesheet to match. Following the system setting instead was the bug that made the login
 * screen unreadable: in light mode Compose drew ink-coloured text and a dim brass button, while the
 * window background behind it was still the ink colour from themes.xml — black on black.
 *
 * The [Surface] is the other half of that fix. A screen that paints no background of its own shows
 * whatever the Activity's window background happens to be, which is a different setting in a
 * different file; painting `colorScheme.background` here means the theme is the only thing that
 * decides what colour anything is.
 */
private val Ink = Color(0xFF141317)
private val InkRaised = Color(0xFF1E1D23)
private val Cream = Color(0xFFF4EFE6)
private val Brass = Color(0xFFD9A13B)
private val BrassDim = Color(0xFF8A6522)
private val Rust = Color(0xFFB3452B)

private val darkScheme = darkColorScheme(
    primary = Brass,
    onPrimary = Ink,
    primaryContainer = BrassDim,
    onPrimaryContainer = Cream,
    secondary = Cream,
    onSecondary = Ink,
    background = Ink,
    onBackground = Cream,
    surface = Ink,
    onSurface = Cream,
    surfaceVariant = InkRaised,
    onSurfaceVariant = Color(0xFFB9B4AC),
    error = Rust,
    onError = Cream,
)

@Composable
fun MootifyTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = darkScheme) {
        Surface(
            modifier = Modifier.fillMaxSize(),
            color = MaterialTheme.colorScheme.background,
            content = content,
        )
    }
}
