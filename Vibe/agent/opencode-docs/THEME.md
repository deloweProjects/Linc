# Theme — Dynamic Material You, synced phone → desktop

Both apps follow the **phone's Material You dynamic color** (derived from the phone's wallpaper). The Android app uses `dynamicLightColorScheme`/`dynamicDarkColorScheme` (Android 12+); the desktop receives that palette live over the companion protocol (v4 `status` fields `themeLight`/`themeDark`) and retints itself to match. See [DECISIONS.md](DECISIONS.md) D-011.

So the source of the colors is the **phone at runtime**, not a hardcoded table. Change the phone's wallpaper and both apps follow within one status cycle (~15 s, or immediately on the next connect).

## Fallback palette (Android 11, or before a phone is connected)

Dynamic color doesn't exist on Android 11, and the desktop has no phone palette until it connects. Both fall back to a fixed Linc purple, defined in:
- Desktop: [`DESKTOP/Linc.Desktop/Themes/MaterialExpressive.xaml`](../DESKTOP/Linc.Desktop/Themes/MaterialExpressive.xaml) (the `SolidColorBrush` resources the sync service retints).
- Android: [`ANDROID/app/src/main/java/app/linc/android/ui/theme/Color.kt`](../ANDROID/app/src/main/java/app/linc/android/ui/theme/Color.kt) (the `Linc*ColorScheme` fallbacks in `Theme.kt`).

These two fallbacks should stay roughly in sync by hand, but they only show when dynamic color is unavailable — the normal path is the live phone palette.

## Fallback token table

| Token | Light | Dark |
|---|---|---|
| Primary | `#6750A4` | `#D0BCFF` |
| On Primary | `#FFFFFF` | `#381E72` |
| Primary Container | `#EADDFF` | `#4F378B` |
| On Primary Container | `#4F378B` | `#EADDFF` |
| Secondary Container | `#E8DEF8` | `#4A4458` |
| On Secondary Container | `#4A4458` | `#E8DEF8` |
| Tertiary Container | `#FFD8E4` | `#633B48` |
| On Tertiary Container | `#633B48` | `#FFD8E4` |
| Surface | `#FEF7FF` | `#141218` |
| On Surface | `#1D1B20` | `#E6E0E9` |
| Surface Container | `#F3EDF7` | `#211F26` |
| Surface Container High | `#ECE6F0` | `#2B2930` |
| On Surface Variant | `#49454F` | `#CAC4D0` |
| Outline | `#79747E` | `#938F99` |
| Outline Variant | `#CAC4D0` | `#49454F` |
| Error | `#B3261E` | `#F2B8B5` |
| Error Container | `#F9DEDC` | `#8C1D18` |
| On Error Container | `#8C1D18` | `#F9DEDC` |

The desktop's `ThemeSyncService` maps the phone's palette onto these same 18 roles by name.
