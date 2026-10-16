# Desktop Modern UI

## Goal
Modernize the Windows desktop POS visual layer so the app feels like a contemporary Windows point-of-sale system, using the provided POS reference as direction while preserving existing offline-first sale behavior.

## Scope
- Shared WPF visual system for the desktop app.
- Pairing and operator login access screens.
- Main POS sale shell and sale entry surface.

## Non-goals for first cut
- Rewriting business logic.
- Migrating the app from WPF to another desktop stack.
- Reworking customer/staff administration screens beyond shared baseline styles.
- Implementing real product imagery/categories if the current local catalog does not expose that data yet.

## Framework decision
Stay on WPF for this cut and build a modern in-repo style system first. A full WinUI 3/Avalonia migration would be a product/platform rewrite, not a visual pass. External UI libraries can be considered only if the native WPF style layer becomes a blocker.

## Tasks

- [x] Create shared desktop design system resources
  - Evidence: Added `src/Commerce.Pos.Windows/Themes/DesktopTheme.xaml` with dark shell/surface colors, primary/accent brushes, text styles, rounded card styles, and practical WPF defaults for buttons, inputs, lists, combo boxes, and separators. Merged it from `src/Commerce.Pos.Windows/App.xaml`.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` succeeded with 8 existing package vulnerability warnings and 0 errors.
  - Commit: not created; user requested no commits.

- [x] Modernize pairing and operator login windows
  - Evidence: Reworked `PairingWindow.xaml` and `OperatorLoginWindow.xaml` into dark rounded access cards with clearer headings, muted helper text, full-width primary actions, styled branch/operator/provisioning panels, and preserved existing `x:Name` values and event handler hookups. Polish pass collapsed each red status border when its status text is empty so empty error boxes are not visible.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` succeeded with 8 existing package vulnerability warnings and 0 errors.
  - Commit: not created; user requested no commits.

- [x] Modernize main POS sale shell
  - Evidence: Reworked `MainWindow.xaml` into a three-zone POS shell with left terminal/status sidebar, central scan-first sale card and manual fallback card, and right sync/action area. Preserved existing named controls and event handler hookups used by code-behind. Polish pass added a window minimum size, slightly reduced fixed sidebar widths, gave the center column a practical minimum, and made the manual-sale customer/button area less prone to clipping.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` succeeded with 8 existing package vulnerability warnings and 0 errors.
  - Commit: not created; user requested no commits.

- [x] Refine operator-first Spanish POS shell
  - Evidence: Reworked `MainWindow.xaml` into a sales-first POS layout with top navigation for Venta, Clientes, Personal, Sincronización, and Configuración/Terminal; added visible current-operator display; moved technical identity/status/sync details into the configuration side area; updated modified operator-facing UI copy in `MainWindow.xaml.cs` to Spanish without changing sale/sync business logic.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` succeeded after closing the previously running app, with 8 existing NU1903 `System.IO.Packaging` warnings and 0 errors.
  - Commit: not created; user requested no commits.

- [x] Add visible multi-theme resource structure
  - Evidence: Split palette colors into `Themes/DarkTheme.xaml` and `Themes/LightTheme.xaml`; kept semantic brushes/styles in `Themes/DesktopTheme.xaml`; updated `App.xaml` to merge the dark palette first and the semantic theme second.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` succeeded after closing the previously running app, with 8 existing NU1903 `System.IO.Packaging` warnings and 0 errors.
  - Commit: not created; user requested no commits.

- [x] Compact Spanish login/access screens
  - Evidence: Removed the `ScrollViewer` from `OperatorLoginWindow.xaml`, reduced spacing/heights for the default state, translated login/pairing labels and buttons to Spanish, and preserved existing named panels and event hookups. `PairingWindow.xaml` was also translated and kept existing collapsed branch selection and event hookups.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` succeeded after closing the previously running app, with 8 existing NU1903 `System.IO.Packaging` warnings and 0 errors.
  - Commit: not created; user requested no commits.

- [x] Add bounded desktop theme switch
  - Evidence: Kept the theme selector only in Settings, replaced the ComboBox with clean Oscuro/Claro selector buttons, and made selection save plus apply the palette immediately through the shared resource dictionaries. Pairing and operator login keep no theme selector.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` succeeded with 8 existing NU1903 `System.IO.Packaging` warnings and 0 errors.
  - Commit: not created; user requested no commits.

- [x] Add Vaca Verde desktop theme option
  - Evidence: Added the `Vaca Verde` theme to the shared theme registry and Settings theme selector, backed by `Themes/VacaVerdeTheme.xaml` with a clean white/soft-green palette and accessible dark-green accents. Theme selection still applies immediately and persists locally through `DesktopThemeService`.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` failed because `Commerce.Pos.Windows.exe` is locked by the running `Commerce.Pos.Windows (7788)` process; restore and dependent project compilation reached the copy step, with existing NU1903 `System.IO.Packaging` warnings.
  - Commit: not created; user requested no commits.

- [x] Build and smoke-check desktop project
  - Evidence: Build verification completed; interactive smoke check was not run in this task.
  - Checks: `dotnet build src/Commerce.Pos.Windows/Commerce.Pos.Windows.csproj` succeeded with 8 existing NU1903 `System.IO.Packaging` warnings and 0 errors.
  - Commit: not created; user requested no commits.

## Reference
- `D:\Incoders\Customers\Vaca Verde\UI POS.jpeg`
