"""
Headless Blender batch-render: turns each appliance's OBJ+MTL 3D model (written by
tools/PlateUpSeedTools.DataExtractor/ApplianceModelExtractor.cs, alongside
data/settings/shop/appliance-icon-manifest.json) into a square, transparent-background 2D icon PNG.

Run via Blender's own bundled Python (bpy is only importable inside Blender itself):

    blender --background --python render_icons.py -- <models_dir> <output_dir> [--only slug1,slug2]

Why OBJ instead of FBX/glTF: PlateUp's Appliance game data has no 2D icon/sprite field at all - only
a 3D Prefab reference. AssetRipper's "GUI.Free" edition (v2.0.0) turned out to have no mesh/model
export format setting at all, and its one relevant live-API endpoint only exports a single raw
Mesh's geometry with no Transform/hierarchy assembly, addressed by session-scoped runtime indices
that don't correspond to anything resolvable from this project's static GUID-based data. So instead,
`ApplianceModelExtractor.cs` parses each appliance's prefab hierarchy and mesh vertex/index buffers
directly from the YAML already sitting in `PlateUpSourceFiles/AssetRipper/ExportedProject/`, bakes
every part's transform and material color into one combined OBJ+MTL pair per appliance. That means
this script is deliberately simple - the OBJ's own `mtllib`/`usemtl` directives already carry
correct per-part colors (and optional textures), so there's no manifest-JSON-driven material
patching to do here at all, unlike an earlier FBX-based design.

Output filenames MUST match the slug both LayoutImageRenderer.Slugify (C#) and
explorer-shop.js's slugifyApplianceName (JS) already produce from an appliance's display name - the
OBJ/MTL files are already named `<slug>.obj`/`<slug>.mtl` by the extractor, so this script just
reuses that stem verbatim for the output PNG. Neither consumer needs any code change: both already
resolve icons purely by File.Exists-on-slugged-filename / <img> onerror fallback.
"""

import glob
import math
import os
import sys

import bpy
from mathutils import Vector

RENDER_SIZE = 512
SAMPLES = 96
# Classic 3/4 "item icon" angle: looking down and slightly rotated, not top-down or side-on.
CAMERA_ELEVATION_DEG = 42
CAMERA_AZIMUTH_DEG = -35


def parse_args():
    argv = sys.argv
    if "--" not in argv:
        print("Usage: blender --background --python render_icons.py -- <models_dir> <output_dir> [--only slug1,slug2]")
        sys.exit(1)
    argv = argv[argv.index("--") + 1:]
    if len(argv) < 2:
        print("Usage: blender --background --python render_icons.py -- <models_dir> <output_dir> [--only slug1,slug2]")
        sys.exit(1)

    models_dir, output_dir = argv[0], argv[1]
    only = None
    if len(argv) >= 4 and argv[2] == "--only":
        only = set(argv[3].split(","))

    return models_dir, output_dir, only


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block_collection in (bpy.data.meshes, bpy.data.materials, bpy.data.images, bpy.data.cameras, bpy.data.lights):
        for block in list(block_collection):
            if block.users == 0:
                block_collection.remove(block)


def setup_world():
    # Blender's default World still contributes ambient/indirect light in Cycles even with a
    # transparent film (film_transparent only affects alpha, not lighting) - left at its default
    # gray-ish strength-1.0 background, combined with the two Sun lights below, this was washing out
    # the tonal contrast between materials (confirmed: the extracted material colors themselves are
    # correct, e.g. a near-black "Metal Black" at 0.20/0.21/0.22 vs. a light tan "Wood 4 - Painted"
    # at 0.78/0.80/0.68 - several appliances were rendering as a near-uniform flat box color despite
    # genuinely distinct underlying materials). Zeroing it makes the two Suns the only light source,
    # fully controlled and reproducible.
    world = bpy.context.scene.world
    if world is None:
        world = bpy.data.worlds.new("IconWorld")
        bpy.context.scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg is not None:
        bg.inputs["Strength"].default_value = 0.0


def setup_render_settings(output_path):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = SAMPLES
    # Blender 4.x defaults to the "AgX" view transform, which desaturates/compresses colors for a
    # photographic look - great for realistic scenes, bad for a flat icon render where the literal
    # material color should read clearly. "Standard" renders the Kd values closer to as-specified.
    scene.view_settings.view_transform = "Standard"
    # This Blender build has no OpenImageDenoise support, and Cycles denoising is on by default in
    # the startup scene - left enabled, every single render hard-fails with "Failed to denoise"
    # instead of just rendering noisier. Not worth the quality tradeoff anyway at this sample count/
    # resolution for a small icon. Both the scene-level and view-layer-level toggles exist across
    # different Blender versions - set whichever this build actually has.
    scene.cycles.use_denoising = False
    for view_layer in scene.view_layers:
        if hasattr(view_layer, "cycles") and hasattr(view_layer.cycles, "use_denoising"):
            view_layer.cycles.use_denoising = False
    scene.render.resolution_x = RENDER_SIZE
    scene.render.resolution_y = RENDER_SIZE
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.filepath = output_path


def parse_mtl(mtl_path):
    """Minimal MTL reader matching exactly what ApplianceModelExtractor.cs writes - newmtl/Kd/map_Kd
    only, one block per material, no other directives. Returns {name: (r, g, b, texture_path_or_None)}."""
    materials = {}
    name = None
    color = (0.8, 0.8, 0.8)
    texture_path = None
    with open(mtl_path, encoding="utf-8") as f:
        for line in f:
            parts = line.split()
            if not parts:
                continue
            if parts[0] == "newmtl":
                if name is not None:
                    materials[name] = (*color, texture_path)
                name = parts[1]
                color = (0.8, 0.8, 0.8)
                texture_path = None
            elif parts[0] == "Kd":
                color = tuple(float(x) for x in parts[1:4])
            elif parts[0] == "map_Kd":
                texture_path = " ".join(parts[1:])  # path may contain spaces, not quoted
    if name is not None:
        materials[name] = (*color, texture_path)
    return materials


def import_obj(obj_path):
    """Builds Blender mesh/object/material data directly from the OBJ+MTL files via from_pydata,
    bypassing Blender's own OBJ-import operators entirely.

    Why: Blender 4.x's `wm.obj_import` (the only importer available in 4.3+, `import_scene.obj` was
    removed) runs as an asynchronous background job - designed for the interactive UI's job system,
    where Blender's event loop polls job completion. In a plain `--background --python` script there
    is no such event loop running, so the operator call returns before the import has actually
    finished, and `bpy.data.objects` doesn't yet contain the new mesh when this function returns -
    confirmed from a real batch run where every import after the first one failed with
    "No asset found" and the importer's own log lines appeared AFTER the whole script had already
    finished. Since this project controls both ends of the file format (its own OBJ writer in
    ApplianceModelExtractor.cs never emits anything beyond `v`/`f`/`usemtl`/`mtllib`, no normals/UVs/
    quads), a tiny direct parser is both simpler and fully synchronous/reliable - no operator, no job
    queue, no version-specific import-API surface to track.
    """
    vertices = []
    # face_groups: material_name -> list of (i, j, k) 0-based vertex indices
    face_groups = {}
    current_material = None
    mtl_path = None

    with open(obj_path, encoding="utf-8") as f:
        for line in f:
            parts = line.split()
            if not parts:
                continue
            if parts[0] == "v":
                # OBJ (and the Unity data it was derived from) is Y-up; Blender's native space is
                # Z-up. Blender's built-in OBJ importer always did this conversion for us
                # automatically - replacing it with a direct from_pydata parser dropped that for
                # free, so every model was importing "lying on its side" (confirmed from a real
                # render: a box-shaped appliance showing thin edge-on slabs instead of its broad
                # face). This is a pure 90-degree rotation about X (determinant +1, not a
                # reflection), so no winding-order change is needed alongside it - that's a
                # separate, already-handled concern (the X-mirror + winding reversal
                # ApplianceModelExtractor.cs does to convert Unity's left-handed space to OBJ's
                # right-handed one).
                x, y, z = (float(v) for v in parts[1:4])
                vertices.append((x, -z, y))
            elif parts[0] == "mtllib":
                mtl_path = os.path.join(os.path.dirname(obj_path), " ".join(parts[1:]))
            elif parts[0] == "usemtl":
                current_material = parts[1]
                face_groups.setdefault(current_material, [])
            elif parts[0] == "f":
                idxs = tuple(int(p) - 1 for p in parts[1:4])
                face_groups.setdefault(current_material, []).append(idxs)

    if not vertices or not any(face_groups.values()):
        return []

    materials_by_name = parse_mtl(mtl_path) if mtl_path and os.path.isfile(mtl_path) else {}

    mesh = bpy.data.meshes.new(os.path.basename(obj_path))
    all_faces = []
    face_material_indices = []
    material_slot_index = {}
    blender_materials = []

    for material_name, faces in face_groups.items():
        if material_name not in material_slot_index:
            material_slot_index[material_name] = len(blender_materials)
            blender_materials.append(build_material(material_name, materials_by_name.get(material_name)))
        slot = material_slot_index[material_name]
        for face in faces:
            all_faces.append(face)
            face_material_indices.append(slot)

    mesh.from_pydata(vertices, [], all_faces)
    mesh.update()
    for mat in blender_materials:
        mesh.materials.append(mat)
    for poly, slot in zip(mesh.polygons, face_material_indices):
        poly.material_index = slot

    obj = bpy.data.objects.new(os.path.basename(obj_path), mesh)
    bpy.context.collection.objects.link(obj)

    apply_smooth_shading([obj])
    return [obj]


def build_material(name, info):
    r, g, b, texture_path = info if info is not None else (0.8, 0.8, 0.8, None)
    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (r, g, b, 1.0)
    if "Roughness" in bsdf.inputs:
        bsdf.inputs["Roughness"].default_value = 0.6

    if texture_path and os.path.isfile(texture_path):
        try:
            image = bpy.data.images.load(texture_path, check_existing=True)
            tex_node = mat.node_tree.nodes.new("ShaderNodeTexImage")
            tex_node.image = image
            mat.node_tree.links.new(tex_node.outputs["Color"], bsdf.inputs["Base Color"])
        except RuntimeError:
            pass  # keep the flat Kd color if the referenced texture can't load

    return mat


def apply_smooth_shading(mesh_objects):
    # The OBJ files intentionally carry no normal data (ApplianceModelExtractor only extracts vertex
    # POSITIONS - the mesh format's other channels are ambiguously packed and weren't worth decoding
    # for a flat-colored icon render). With no normals, Blender defaults every face to its own flat
    # face-normal - fine for genuinely boxy props, but makes anything meant to look curved/rounded
    # (e.g. balloon spheres) show visible low-poly facets instead of a smooth surface.
    #
    # Deliberately NOT using the bpy.ops.object.shade_auto_smooth operator here (Blender 4.1+): it
    # implements "auto smooth" by appending a "Smooth by Angle" geometry-nodes modifier from
    # Blender's bundled essentials asset library - and that asset library isn't available in this
    # apt-packaged headless install, so the operator hard-fails with "No asset found at path ''"
    # (confirmed via a direct traceback - this was being silently mislabeled as an OBJ *import*
    # failure before, since it was called from inside import_obj() and caught by that function's
    # broad except RuntimeError). mesh.shade_smooth() + mesh.set_sharp_from_angle() are the
    # equivalent pure mesh-data API (no asset/operator dependency at all) - smooth every face, then
    # mark edges sharper than the angle threshold as sharp so Blender's shading doesn't interpolate
    # across them, preserving genuinely hard edges like box corners.
    for obj in mesh_objects:
        obj.data.shade_smooth()
        obj.data.set_sharp_from_angle(angle=math.radians(30))


def frame_camera(mesh_objects):
    min_corner = Vector((math.inf, math.inf, math.inf))
    max_corner = Vector((-math.inf, -math.inf, -math.inf))
    for obj in mesh_objects:
        for corner in obj.bound_box:
            world_corner = obj.matrix_world @ Vector(corner)
            min_corner = Vector(min(a, b) for a, b in zip(min_corner, world_corner))
            max_corner = Vector(max(a, b) for a, b in zip(max_corner, world_corner))

    center = (min_corner + max_corner) / 2
    radius = max((max_corner - min_corner).length / 2, 0.05)

    distance = radius * 2.6
    elevation = math.radians(CAMERA_ELEVATION_DEG)
    azimuth = math.radians(CAMERA_AZIMUTH_DEG)
    cam_offset = Vector((
        distance * math.cos(elevation) * math.sin(azimuth),
        -distance * math.cos(elevation) * math.cos(azimuth),
        distance * math.sin(elevation),
    ))

    cam_data = bpy.data.cameras.new("IconCamera")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = radius * 2.15
    cam_obj = bpy.data.objects.new("IconCamera", cam_data)
    bpy.context.collection.objects.link(cam_obj)
    cam_obj.location = center + cam_offset

    direction = center - cam_obj.location
    cam_obj.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = cam_obj

    return center, radius


def add_lighting(center, radius):
    key = bpy.data.lights.new("KeyLight", type="SUN")
    key.energy = 3.0
    key_obj = bpy.data.objects.new("KeyLight", key)
    bpy.context.collection.objects.link(key_obj)
    key_obj.location = center + Vector((radius * 2, -radius * 2, radius * 3))
    key_obj.rotation_euler = (math.radians(55), 0, math.radians(35))

    fill = bpy.data.lights.new("FillLight", type="SUN")
    fill.energy = 1.2
    fill_obj = bpy.data.objects.new("FillLight", fill)
    bpy.context.collection.objects.link(fill_obj)
    fill_obj.location = center + Vector((-radius * 2, radius * 1.5, radius * 1.5))
    fill_obj.rotation_euler = (math.radians(100), 0, math.radians(-120))


def render_one(obj_path, slug, output_dir):
    clear_scene()

    try:
        mesh_objects = import_obj(obj_path)
    except RuntimeError as exc:
        return f"failed (OBJ import error: {exc})"

    if not mesh_objects:
        return "skipped (no mesh geometry after import)"

    center, radius = frame_camera(mesh_objects)
    add_lighting(center, radius)

    output_path = os.path.join(output_dir, f"{slug}.png")
    setup_render_settings(output_path)
    bpy.ops.render.render(write_still=True)
    return f"rendered -> {output_path}"


def main():
    models_dir, output_dir, only = parse_args()
    os.makedirs(output_dir, exist_ok=True)

    obj_paths = sorted(glob.glob(os.path.join(models_dir, "*.obj")))
    if only:
        obj_paths = [p for p in obj_paths if os.path.splitext(os.path.basename(p))[0] in only]

    setup_world()
    results = {"rendered": 0, "skipped": 0, "failed": 0}
    for i, obj_path in enumerate(obj_paths, 1):
        slug = os.path.splitext(os.path.basename(obj_path))[0]
        print(f"[{i}/{len(obj_paths)}] {slug}...")
        try:
            outcome = render_one(obj_path, slug, output_dir)
        except Exception as exc:  # noqa: BLE001 - this is a long batch job, one bad entry must not kill the rest
            outcome = f"failed (unexpected error: {exc})"

        print(f"    {outcome}")
        if outcome.startswith("rendered"):
            results["rendered"] += 1
        elif outcome.startswith("skipped"):
            results["skipped"] += 1
        else:
            results["failed"] += 1

    print()
    print(f"Done: {results['rendered']} rendered, {results['skipped']} skipped, {results['failed']} failed (of {len(obj_paths)}).")


if __name__ == "__main__":
    main()
