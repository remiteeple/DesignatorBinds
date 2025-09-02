# Designator Binds

Create global keybinds for any designator via right-click. Open any Architect tab (or other UI that exposes designators), right-click a designator (e.g., a bed), and choose "Create keybind". A new entry appears under the "Designator Binds" keyboard category that you can bind to a key. Pressing that key selects the designator globally.

## Build (Visual Studio)
1. Set an environment variable `RIMWORLD_DIR` pointing to your RimWorld installation folder, e.g.
   - Steam (Windows): `C:\\Program Files (x86)\\Steam\\steamapps\\common\\RimWorld`
2. Open `DesignatorBinds.sln` in Visual Studio.
3. Build the solution (Debug or Release). The DLL will be emitted to `1.6/DesignatorBinds.dll` inside the mod folder.

## Install
- Copy this mod folder into `RimWorld/Mods/`.
- Enable **Designator Binds** in the in-game mod list and restart.

## Notes
- Works with vanilla and modded designators. For build designators, the binding targets the specific buildable (e.g., Bed vs. Table).
- Existing bindings from earlier versions of this mod are restored automatically.
- Selection respects designator rules (e.g., research locks, disabled state), but does not require the Architect tab to be open.
