# Appliance icon render pipeline — STATUS: INFEASIBLE, NOT SHIPPED (2026-10-10)

**This pipeline works end-to-end and its geometry/positioning is correct, but the whole approach
was deemed infeasible and is NOT wired into the live app.** The blocker is fundamental, not an
engineering gap: PlateUp's real appliance shader (`Simple Flat`, built with Amplify Shader Editor)
has no recoverable compiled program anywhere in the asset dump - AssetRipper's exported `.shader`
file contains a literal placeholder (`return float4(1,1,1,1);`) under an explicit
`//DummyShaderTextExporter` comment in place of the real logic. Only the shader's raw INPUT
parameter names/defaults (`_Color0`, `_Flatness`, `_Highlight`, `_Overlay*`, etc.) survive
extraction - the formula that turns those inputs into the game's actual stylized on-screen color is
simply not present in the data, at any fidelity. Feeding the same raw numbers into a different
engine's shading model (tried: Blender/Cycles's Principled BSDF) produces colors that look nothing
like the real game, and there is no way to fix that by extracting more carefully - the information
genuinely isn't there. Full writeup: `docs/project-history.md`'s session-20 entry,
`docs/game-domain-knowledge.md`'s "Asset extraction limits" section.

This code is kept, committed, and documented as **validated reference infrastructure for a future
revisit** (e.g. if the compiled shader is ever recoverable by some other method, or a hand-built
stylized Blender shader is judged a good-enough approximation to be worth shipping despite not being
exact) - not as a working feature. The geometry/transform extraction (step 1 below) and the Blender
render environment fixes (step 2) are both genuinely solid and reusable; only the final visual
output (color/shading) is the problem.

## Pipeline

1. **Extract OBJ/MTL models + icon manifest** (`tools/PlateUpSeedTools.DataExtractor/
   ApplianceModelExtractor.cs` + `ApplianceIconManifestExtractor.cs`, wired into the normal
   `DataExtractor` run via an optional 3rd CLI arg - backward compatible, inert unless passed). For
   each of the 451 shop appliances: resolves its `Prefab` reference, walks the full GameObject/
   Transform/MeshFilter/MeshRenderer hierarchy (joined by shared `m_GameObject` references, not
   document order - confirmed real prefabs don't guarantee that), reads each referenced Mesh asset's
   raw interleaved vertex buffer (position channel only - channel 0, Float32 x3, offset 0, always
   unambiguous regardless of how later channels are packed; stride = `m_DataSize / m_VertexCount`)
   and index buffer directly from the YAML, composes each part's full local-to-prefab-root transform
   via `System.Numerics.Matrix4x4` (walking the `m_Father` chain), and writes one combined OBJ+MTL
   pair per appliance with per-part material colors already baked in. Converts Unity's left-handed
   coordinate space to OBJ's right-handed one (mirror X + reverse triangle winding - confirmed a
   pure rotation, not a reflection, so this is the only handedness fix needed here). Also runs
   `FindDisguiseCoverParts`, a geometric filter that detects and excludes thin, full-footprint plates
   sitting above denser geometry - found necessary because some appliance prefabs (confirmed on
   "Hob") bundle unused/leftover cover geometry that would otherwise completely hide real detail (see
   `docs/game-domain-knowledge.md` for the full finding - this filter only catches the "flat lid"
   pattern, not a second "tall enclosing wall" pattern found on the same appliance, which was left
   Hob-specific rather than risk a bad general heuristic). Output:
   `data/settings/shop/appliance-icon-manifest.json` + `<slug>.obj`/`<slug>.mtl` per appliance.
2. **Batch-render with Blender** (`render_icons.py`, headless/`--background`, Cycles on CPU). For
   each OBJ: import (via a custom synchronous `mesh.from_pydata()`-based parser, NOT Blender's
   `wm.obj_import` operator - see "Real bugs found" below for why), apply smooth shading
   (`mesh.shade_smooth()` + `mesh.set_sharp_from_angle()`), frame an orthographic camera at a fixed
   3/4 angle around the mesh's actual bounding box, light it, render a transparent-background
   512x512 PNG named `<slug>.png` - the exact same slug `LayoutImageRenderer.Slugify` (C#) and
   `explorer-shop.js`'s `slugifyApplianceName` (JS) already produce, so (were this ever shipped) no
   backend/frontend code would need to change - both already resolve icons purely by
   `File.Exists`-on-slugged-filename / `<img>` `onerror` fallback.

`run_pipeline.sh` drives both steps end-to-end.

## Verified, hands-on, this session (geometry/pipeline mechanics - all solid)

- **Extraction (step 1): 395/428 appliances produced a real, correct OBJ model.** The 33 that didn't
  all have zero mesh-bearing GameObjects in their source prefab (confirmed via each one's own
  manifest `materials: []`) - names like `Force Witch Decor - *`, `Greenhouse Sun Effects`,
  `Interaction Proxy` make clear these are logic/trigger/decorator entities with no visible model at
  all, not a parsing gap. Found and fixed two real bugs that caused 0/428 silent failures initially
  (`m_DataSize`/`_typelessdata` are children of `m_VertexData`, not of `Mesh` directly - the code was
  reading the wrong YAML node and treating the resulting empty read as "legitimately absent" instead
  of a parse error).
- **Transform/position correctness spot-checked directly**: Hob/Counter/Research Desk bounding boxes
  all came out ~1m, sitting at floor level - correct real-world scale. "Dining Table" initially
  looked wrong (floating at Y=1.9-2.8) - investigated rather than assumed-broken, traced to that
  specific prefab's own root Transform genuinely having a non-zero `m_LocalPosition` baked in, not a
  bug - harmless since the render script's camera framing re-centers on actual geometry regardless.
- **Blender render environment: multiple real, now-fixed bugs**, each found by actually running the
  pipeline and tracing the failure, not guessing:
  - Cycles denoising hard-failed every render (no OpenImageDenoise support in this apt-packaged
    Blender build) - fixed by disabling it.
  - Color contrast was washed out by Blender's default World ambient light (real in Cycles even with
    `film_transparent` on) combined with Blender 4.x's AgX view transform (a photographic tone-mapper
    that desaturates on purpose) - fixed by zeroing ambient and switching to the "Standard" view
    transform. (This made colors internally consistent/correct relative to the extracted `_Color0`
    values - it did NOT fix the separate, much bigger shader-formula problem described above.)
  - `bpy.ops.wm.obj_import` (Blender 4.3's only OBJ importer) runs as an async background job
    designed for the interactive UI's event loop; in `--background` scripts it silently half-fails
    on every call after the first. Fixed by replacing it entirely with a custom synchronous
    `mesh.from_pydata()`-based parser (justified since this project controls both ends of the simple
    OBJ subset it needs).
  - `bpy.ops.object.shade_auto_smooth` depends on a bundled geometry-nodes asset library not present
    in this headless install (produced the exact same misleading error text as the import bug above,
    initially causing a misdiagnosis) - fixed with the equivalent pure mesh-data API
    (`mesh.shade_smooth()` + `mesh.set_sharp_from_angle()`), no asset dependency.
  - The custom OBJ importer above needed an explicit Y-up (OBJ/Unity) → Z-up (Blender) axis
    conversion that Blender's built-in importer normally does for free - without it, every model
    rendered lying on its side. Confirmed a pure 90°-about-X rotation (determinant +1), so no
    additional winding-order change was needed alongside it.
- **Geometry-hiding bug found via direct user comparison against a real in-game reference image**:
  Hob's prefab bundles unused/leftover geometry (a near-flat disguise "lid" + a separately-discovered
  tall "enclosing wall" part misleadingly named `Base`) that fully hides a complete, correctly
  extracted, correctly colored burner grate underneath. The lid pattern is now filtered generically
  (`FindDisguiseCoverParts`); the wall pattern is not (left Hob-specific, not generalized - see
  `docs/game-domain-knowledge.md`).

## Not fixed, not fixable here: colors

See the status banner at the top. This is why the feature isn't shipped.

## Running it (for a future revisit only - not part of the normal build/run flow)

```bash
cd /opt/plateupSetSeedProject/plateupSetSeedSearch
bash tools/IconRenderer/run_pipeline.sh 2>&1 | tee /tmp/icon-pipeline.log
```

Rendered PNGs would land in `src/PlateUpSeedTools.Web/wwwroot/images/appliances/` - the same
directory the 5 shipped (fan-sourced) icons live in. **Do not run this and commit the output as-is**
- the colors are confirmed wrong; see the status banner. Any future revisit needs to solve the color
problem first (or explicitly accept/document the inaccuracy) before the output is worth shipping.
