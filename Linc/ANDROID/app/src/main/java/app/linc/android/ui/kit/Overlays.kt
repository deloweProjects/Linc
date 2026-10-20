@file:OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui.kit

import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AlertDialogDefaults
import androidx.compose.material3.BottomSheetDefaults
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.SheetState
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.window.DialogProperties
import app.linc.android.ui.theme.OverlayKind
import app.linc.android.ui.theme.OverlayWindowBlur
import app.linc.android.ui.theme.overlayContainerColor

// Overlays that stay legible over the wallpaper background: Material's own AlertDialog and
// ModalBottomSheet with overlayContainerColor (frosted glass tinted with the theme, never as
// see-through as the page's cards) and, on Android 12+, the dialog window blurring what is behind
// it. With the wallpaper off they are exactly Material's.

/** Material's AlertDialog, frosted over the wallpaper. */
@Composable
fun LincAlertDialog(
    onDismissRequest: () -> Unit,
    confirmButton: @Composable () -> Unit,
    modifier: Modifier = Modifier,
    dismissButton: @Composable (() -> Unit)? = null,
    icon: @Composable (() -> Unit)? = null,
    title: @Composable (() -> Unit)? = null,
    text: @Composable (() -> Unit)? = null,
    shape: Shape = AlertDialogDefaults.shape,
    properties: DialogProperties = DialogProperties(),
) {
    AlertDialog(
        onDismissRequest = onDismissRequest,
        // The confirm button is the one slot every dialog has, composed inside the dialog's
        // window: the blur hook rides along there and draws nothing.
        confirmButton = { OverlayWindowBlur(); confirmButton() },
        modifier = modifier,
        dismissButton = dismissButton,
        icon = icon,
        title = title,
        text = text,
        shape = shape,
        containerColor = overlayContainerColor(OverlayKind.Dialog),
        properties = properties,
    )
}

/** Material's ModalBottomSheet, frosted over the wallpaper. */
@Composable
fun LincBottomSheet(
    onDismissRequest: () -> Unit,
    modifier: Modifier = Modifier,
    sheetState: SheetState = rememberModalBottomSheetState(),
    contentWindowInsets: @Composable () -> WindowInsets = { BottomSheetDefaults.windowInsets },
    content: @Composable ColumnScope.() -> Unit,
) {
    ModalBottomSheet(
        onDismissRequest = onDismissRequest,
        modifier = modifier,
        sheetState = sheetState,
        containerColor = overlayContainerColor(OverlayKind.Sheet),
        contentWindowInsets = contentWindowInsets,
    ) {
        OverlayWindowBlur()
        content()
    }
}
