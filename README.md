# FPS counter and performance statistics for BepInEx
A BepInEx plugin that measures many performance statistics of Unity engine games. It can be used to help determine causes of performance drops and other issues. Here are some of the features:
- Accurately measures true ms spent per frame (not calculated from FPS)
- Measures time spent in each of the steps Unity takes in order to render a frame (e.g. how long all Update methods took to run collectively)
- Measures time spent in each of the installed BepInEx plugins, including average, last-frame, max-spike, call count, FixedUpdate/Update/LateUpdate/OnGUI breakdowns, and Harmony patch method time
- Measures memory stats, including amount of heap memory used by the GC and GC collection counts (if supported)
- Can log plugin timing snapshots for frames that exceed a configurable hitch threshold

![In-game preview](https://user-images.githubusercontent.com/39247311/77855748-c1764780-71f2-11ea-8e8e-0e9a35d9866b.png)

## How to use
1. Install the latest version of [BepInEx](https://github.com/BepInEx/BepInEx). Use BepInEx v5 for games that use Mono and BepInEx v6 for games that use IL2CPP.
2. Optionally install [BepInEx.ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) to make configuring FPScounter easier. **Warning:** Current BepInEx v6 builds require ConfigurationManager to work.
3. Extract the release for your version of BepInEx into your game root, the .dll should end up in the `BepInEx\plugins\FPSCounter` subdirectory.
4. Start the game and press `U + LeftShift` (default hotkey).

The on/off hotkey and looks can be configured in the config file `BepInEx\config\MarC0.FPSCounter.cfg` (you have to run the game at least once to generate it), or by using BepInEx.ConfigurationManager (F1 by default).

Plugin timing diagnostics can also be configured:
- `Plugin stats max lines` controls how many plugin timing rows are shown.
- `Plugin stats sort mode` sorts rows by average cost, last-frame cost, max spike, or call count.
- `Log frame hitches over ms` writes plugin timing snapshots to the BepInEx log when a frame exceeds the configured threshold. Set it to `0` to disable hitch logging.

Plugin timing row fields:
- `AVG`: moving average of measured plugin time per frame.
- `LAST`: measured plugin time in the most recently completed frame.
- `MAX`: highest measured single-frame plugin time since stats were reset.
- `CALLS`: number of measured plugin method or patch calls in the last frame.
- `HOOKS U/H`: number of attached timing hooks, split into Unity lifecycle hooks (`U`) and Harmony patch-method hooks (`H`).
- `F/U/L/G`: last-frame time spent in `FixedUpdate`, `Update`, `LateUpdate`, and `OnGUI`.
- `PATCH`: last-frame time spent in Harmony prefix, postfix, and finalizer methods.

Plugin timing rows include a `HOOKS U/H` count for Unity event hooks and Harmony patch-method hooks. Plugins with `HOOKS U/H 000/000` are loaded, but no supported methods were found to time, so they are shown as `UNMEASURED`.
The `PATCH` value covers Harmony prefix, postfix, and finalizer methods. Transpilers and IL manipulators run when patches are applied, so their runtime impact cannot be separated from the patched game method.

Toggling the counter off and back on resets captured plugin statistics, including average and max-spike values.
