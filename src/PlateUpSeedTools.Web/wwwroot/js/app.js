const settingSelect = document.getElementById("setting-select");
const seedInput = document.getElementById("seed-input");
const form = document.getElementById("layout-form");
const statusEl = document.getElementById("status");
const resultEl = document.getElementById("result");
const resultTitle = document.getElementById("result-title");
const gridEl = document.getElementById("grid");
const legendEl = document.getElementById("legend");
const copyLinkButton = document.getElementById("copy-link");
const speedrunLinkEl = document.getElementById("speedrun-link");
const colorPanelRowsEl = document.getElementById("color-panel-rows");
const colorPanelResetAllButton = document.getElementById("color-panel-reset-all");

// VALID_SEED/validateSeed/fetchSettings/populateSettingSelect come from shared.js.

// Shipped defaults for feature colors (the layout image draws features server-side, see
// LayoutColors.cs) - kept in sync with that C# copy by convention. Used as the fallback in
// shippedDefaultColor below, and as legend/panel swatch colors via resolveColor/buildColorEntries.
const FEATURE_COLORS = {
  frontdoor: "#ffe066",
  door: "#f1e6d8",
  doorreversed: "#f1e6d8",
  hatch: "#7fd4c1",
};
const DEFAULT_FEATURE_COLOR = "#9a8a76";

// Mirrors LayoutColors.WallColor - walls aren't part of the `features` list (there's no "Wall"
// concept in the underlying data; LayoutImageRenderer.DrawWalls derives them from tile/feature
// adjacency), so unlike the feature swatches above this is a fixed legend entry rather than one
// built from data seen on this layout - any real layout has walls somewhere.
const WALL_COLOR = "#b8a688";

// Mirrors LayoutColors.RoomCodes - short codes the server also draws on every room instance in the
// rendered image (LayoutImageRenderer.DrawRoomLabels), so identity never depends on color alone.
// Appended to legend labels here so the legend and the image agree on the same code per room.
const ROOM_CODES = {
  dining: "DIN",
  kitchen: "KIT",
  garden: "GDN",
  frontentrance: "FNT",
  socialspace: "SOC",
  bedroom: "BED",
  garage: "GRG",
  office: "OFF",
  workshop: "WRK",
  contracts: "CON",
  trophies: "TRO",
  locations: "LOC",
  storage: "STO",
};
function roomCode(roomType) {
  return ROOM_CODES[roomType.toLowerCase()] ?? "UNA";
}

// Player color customization (real player request): a swatch per color this SPECIFIC layout
// actually uses - not every RoomType the game engine knows about (most settings only ever show a
// handful of the 14). Room types that appear more than once (Dining, Unassigned - see
// PER_INSTANCE_ROOM_TYPES below) get one row per physical instance, labeled to match its badge on
// the image (R1/R2/... for Unassigned), not one generic type-level row - so the panel always
// mirrors exactly what's in the legend. Saved to localStorage, deliberately browser-local: only
// the <img> request this page makes for itself carries the `colors` query param (see renderLayout)
// - the address bar URL, "Copy link", and the shareable /preview/.../seed.png used for Discord/OG
// embeds never do, so anyone else opening a shared link always sees the shipped default palette.
//
// Key scheme (also what LayoutEndpoints.GetLayoutPng's `?colors=` parsing expects):
//  - single-instance room / wall / feature: the lowercase name ("kitchen", "wall", "door", ...)
//  - a room type with multiple physical instances: "<type>:<0-based index>" (e.g. "unassigned:1"
//    for R2) - overriding this bypasses LayoutColors.ShadeColor's auto-derived variant for that
//    one instance only; resetting it falls back to the auto-shaded color again, not the type's
//    shipped default, since there's no single "default" for an arbitrary Nth instance.
const FIELD_LABELS = {
  dining: "Dining", kitchen: "Kitchen", garden: "Garden", workshop: "Workshop", office: "Office",
  frontentrance: "Front Entrance", locations: "Locations", storage: "Storage", bedroom: "Bedroom",
  trophies: "Trophies", garage: "Garage", contracts: "Contracts", unassigned: "Unassigned",
  socialspace: "Social Space", wall: "Wall", door: "Door", frontdoor: "Front Door", hatch: "Hatch",
};
const KNOWN_COLOR_KEYS = new Set(Object.keys(FIELD_LABELS));

function shippedDefaultColor(key) {
  if (key === "wall") {
    return WALL_COLOR;
  }
  if (key === "door" || key === "frontdoor" || key === "hatch") {
    return FEATURE_COLORS[key] ?? DEFAULT_FEATURE_COLOR;
  }
  return getComputedStyle(document.documentElement).getPropertyValue(`--room-${key}`).trim();
}

const COLOR_OVERRIDES_STORAGE_KEY = "plateup:colorOverrides";
function loadColorOverrides() {
  try {
    const raw = localStorage.getItem(COLOR_OVERRIDES_STORAGE_KEY);
    return raw ? JSON.parse(raw) : {};
  } catch {
    return {};
  }
}
function saveColorOverrides() {
  try {
    localStorage.setItem(COLOR_OVERRIDES_STORAGE_KEY, JSON.stringify(colorOverrides));
  } catch {
    // localStorage unavailable (private browsing, storage full, ...) - customization just won't
    // persist across reloads; still works for the current page view.
  }
}
let colorOverrides = loadColorOverrides();

// The single source of truth for "what color is this thing right now" for a type-level/wall/
// feature key - a player's override if they've set one, else the shipped default. Per-instance
// keys ("unassigned:1") are resolved separately in buildColorEntries, since their fallback is a
// computed shade, not a fixed default.
function resolveColor(key) {
  return colorOverrides[key] ?? shippedDefaultColor(key);
}

function buildColorsQueryParam() {
  return Object.keys(colorOverrides).length > 0 ? `?colors=${encodeURIComponent(JSON.stringify(colorOverrides))}` : "";
}

// Builds the ordered set of {key, label, legendLabel, color} rows this layout actually needs -
// shared by both the legend and the customize panel so they can never drift apart (`label` is the
// compact panel text, e.g. "R2"; `legendLabel` is the fuller legend text, e.g. "Unassigned 2 (R2)").
// `roomOrderByType`/`indexByRoomId` come from renderLayout's existing per-instance grouping.
//
// Ordering: every non-Unassigned-instance entry (rooms alphabetically, then features, then Wall)
// comes first, with the R1/R2/... Unassigned rows always last, in R-number order - real player
// request, since Unassigned rooms are the "extra" ones with no fixed identity and read better as a
// trailing group than interleaved alphabetically among named rooms.
function buildColorEntries(layout, roomOrderByType, indexByRoomId) {
  const namedRooms = new Map();
  const unassignedInstances = [];
  for (const tile of layout.tiles) {
    const typeKey = tile.roomType.toLowerCase();

    // Unassigned always uses its per-instance key ("unassigned:0", ...), even when this layout
    // only has one - mirrors LayoutImageRenderer.ColorForTile/DrawRoomLabels, which always number
    // Unassigned rooms R1/R2/... regardless of count. Without this, a color set on "R1" while
    // viewing a seed with 2+ Unassigned rooms (key "unassigned:0") silently stopped applying on a
    // seed with only one (which used to fall back to the bare "unassigned" key instead) - real
    // player-reported bug: a customized color "not carrying over" to a new seed.
    if (tile.roomType === "Unassigned") {
      const index = indexByRoomId.get(tile.roomId);
      const key = `${typeKey}:${index}`;
      if (!unassignedInstances.some((e) => e[0] === key)) {
        const label = `R${index + 1}`;
        const color = colorOverrides[key] ?? shadeColor(resolveColor(typeKey), index);
        unassignedInstances.push([key, { index, label, legendLabel: `Unassigned ${index + 1} (${label})`, color }]);
      }
      continue;
    }

    const order = roomOrderByType.get(tile.roomType);
    if (order && order.length > 1) {
      const index = indexByRoomId.get(tile.roomId);
      const key = `${typeKey}:${index}`;
      if (!namedRooms.has(key)) {
        const label = `${tile.roomType} ${index + 1}`;
        const color = colorOverrides[key] ?? shadeColor(resolveColor(typeKey), index);
        namedRooms.set(key, { label, legendLabel: label, color });
      }
    } else if (!namedRooms.has(typeKey)) {
      const label = FIELD_LABELS[typeKey] ?? tile.roomType;
      namedRooms.set(typeKey, { label, legendLabel: `${label} (${roomCode(tile.roomType)})`, color: resolveColor(typeKey) });
    }
  }

  const features = new Map();
  for (const feature of layout.features) {
    const key = feature.type.toLowerCase() === "doorreversed" ? "door" : feature.type.toLowerCase();
    if (KNOWN_COLOR_KEYS.has(key) && !features.has(key)) {
      features.set(key, { label: FIELD_LABELS[key], legendLabel: FIELD_LABELS[key], color: resolveColor(key) });
    }
  }

  const entries = new Map([...namedRooms].sort((a, b) => a[1].label.localeCompare(b[1].label)));
  for (const [key, entry] of [...features].sort()) {
    entries.set(key, entry);
  }
  entries.set("wall", { label: "Wall", legendLabel: "Wall", color: resolveColor("wall") });
  for (const [key, entry] of unassignedInstances.sort((a, b) => a[1].index - b[1].index)) {
    entries.set(key, entry);
  }
  return entries;
}

// Rebuilding every <input> on each color tweak would close any native color-picker popup the
// player currently has open mid-drag (an <input type=color>'s picker closes the moment its
// element is removed from the DOM) - so rows are only rebuilt when the actual set of colorable
// things changes (a new seed/setting with different room types), tracked via this signature; a
// plain color change just updates existing inputs' values in place via colorPanelInputsByKey.
let colorPanelSignature = null;
let colorPanelInputsByKey = new Map();

// Real player feedback: the browser's native <input type=color> popup shows RGB number fields,
// not hex, and there's no way to suppress that (it's OS/browser chrome we don't control) or add a
// hex field into it. So the picker itself is custom-built here: a saturation/value square + a hue
// strip, no RGB anywhere - the hex text box (always visible per row) is the only numeric readout,
// and can be typed/pasted into directly. One popover instance is shared across all rows (opened/
// repositioned per swatch click) rather than one per row, since only one can be open at a time.
const HEX_COLOR_PATTERN = /^#[0-9a-fA-F]{6}$/;

const colorPickerPopover = document.createElement("div");
colorPickerPopover.className = "color-picker-popover";
colorPickerPopover.hidden = true;
colorPickerPopover.innerHTML = `
  <div class="color-picker-sv"><div class="color-picker-sv-thumb"></div></div>
  <div class="color-picker-hue"><div class="color-picker-hue-thumb"></div></div>
`;
document.body.appendChild(colorPickerPopover);
const colorPickerSv = colorPickerPopover.querySelector(".color-picker-sv");
const colorPickerSvThumb = colorPickerPopover.querySelector(".color-picker-sv-thumb");
const colorPickerHue = colorPickerPopover.querySelector(".color-picker-hue");
const colorPickerHueThumb = colorPickerPopover.querySelector(".color-picker-hue-thumb");

let pickerH = 0;
let pickerS = 1;
let pickerV = 1;
let pickerOnChange = null; // (hex) => void, set per-row while its popover is open

function updatePickerVisuals() {
  const [hr, hg, hb] = hsvToRgb(pickerH, 1, 1);
  colorPickerSv.style.backgroundColor = rgbToHex(hr, hg, hb);
  colorPickerSvThumb.style.left = `${pickerS * 100}%`;
  colorPickerSvThumb.style.top = `${(1 - pickerV) * 100}%`;
  colorPickerHueThumb.style.left = `${pickerH * 100}%`;
}

// deferImage=true while actively dragging (updates colorOverrides + the legend/panel swatches
// only, no server round-trip); the final call on pointer-release passes deferImage=false so the
// rendered PNG re-fetches exactly once per drag, not on every intermediate pointermove tick.
function applyPickerColor(deferImage) {
  const [r, g, b] = hsvToRgb(pickerH, pickerS, pickerV);
  pickerOnChange?.(rgbToHex(r, g, b), deferImage);
}

function openColorPicker(anchorEl, hex, onChange) {
  pickerOnChange = onChange;
  const [r, g, b] = hexToRgb(hex);
  [pickerH, pickerS, pickerV] = rgbToHsv(r, g, b);
  updatePickerVisuals();

  const rect = anchorEl.getBoundingClientRect();
  colorPickerPopover.style.left = `${window.scrollX + rect.left}px`;
  colorPickerPopover.style.top = `${window.scrollY + rect.bottom + 6}px`;
  colorPickerPopover.hidden = false;
}

function closeColorPicker() {
  colorPickerPopover.hidden = true;
  pickerOnChange = null;
}

function trackPointer(el, onMove, onEnd) {
  el.addEventListener("pointerdown", (event) => {
    el.setPointerCapture(event.pointerId);
    onMove(event);
    const move = (moveEvent) => onMove(moveEvent);
    const up = () => {
      el.removeEventListener("pointermove", move);
      onEnd();
    };
    el.addEventListener("pointermove", move);
    el.addEventListener("pointerup", up, { once: true });
    event.preventDefault();
  });
}

trackPointer(
  colorPickerSv,
  (event) => {
    const rect = colorPickerSv.getBoundingClientRect();
    pickerS = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
    pickerV = 1 - Math.min(1, Math.max(0, (event.clientY - rect.top) / rect.height));
    updatePickerVisuals();
    applyPickerColor(true);
  },
  () => applyPickerColor(false),
);

trackPointer(
  colorPickerHue,
  (event) => {
    const rect = colorPickerHue.getBoundingClientRect();
    pickerH = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
    updatePickerVisuals();
    applyPickerColor(true);
  },
  () => applyPickerColor(false),
);

document.addEventListener("pointerdown", (event) => {
  if (!colorPickerPopover.hidden && !colorPickerPopover.contains(event.target) && !event.target.closest(".color-swatch")) {
    closeColorPicker();
  }
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    closeColorPicker();
  }
});

function buildColorPanelRows(entries) {
  colorPanelRowsEl.innerHTML = "";
  colorPanelInputsByKey = new Map();
  for (const [key, entry] of entries) {
    const row = document.createElement("div");
    row.className = "color-panel-row";

    const label = document.createElement("span");
    label.textContent = entry.label;

    const swatch = document.createElement("button");
    swatch.type = "button";
    swatch.className = "color-swatch";
    swatch.style.background = entry.color;
    swatch.setAttribute("aria-label", `${entry.label} color`);

    const hexInput = document.createElement("input");
    hexInput.type = "text";
    hexInput.className = "color-hex";
    hexInput.value = entry.color;
    hexInput.maxLength = 7;
    hexInput.spellcheck = false;
    hexInput.autocomplete = "off";
    hexInput.setAttribute("aria-label", `${entry.label} hex code`);

    function commit(value, deferImage = false) {
      swatch.style.background = value;
      hexInput.value = value;
      colorOverrides[key] = value;
      saveColorOverrides();
      // While dragging in the picker popover, only the legend/panel need to update live - the
      // rendered PNG re-fetches once, when the drag actually finishes (deferImage=false).
      if (deferImage) {
        refreshLegendAndPanel();
      } else {
        refreshCurrentLayout();
      }
    }

    swatch.addEventListener("click", () => {
      openColorPicker(swatch, hexInput.value, commit);
    });

    hexInput.addEventListener("input", () => {
      let value = hexInput.value.trim();
      if (value && !value.startsWith("#")) {
        value = `#${value}`;
      }
      if (!HEX_COLOR_PATTERN.test(value)) {
        return;
      }
      commit(value.toLowerCase());
    });

    const reset = document.createElement("button");
    reset.type = "button";
    reset.className = "color-reset";
    reset.title = `Reset ${entry.label} to default`;
    reset.setAttribute("aria-label", `Reset ${entry.label} to default`);
    reset.textContent = "↺";
    reset.addEventListener("click", () => {
      delete colorOverrides[key];
      saveColorOverrides();
      refreshCurrentLayout();
    });

    row.append(label, swatch, hexInput, reset);
    colorPanelRowsEl.appendChild(row);
    colorPanelInputsByKey.set(key, { swatch, hexInput });
  }
}

function syncColorPanel(entries) {
  const signature = [...entries.keys()].join("|");
  if (signature !== colorPanelSignature) {
    colorPanelSignature = signature;
    buildColorPanelRows(entries);
    return;
  }
  for (const [key, entry] of entries) {
    const pair = colorPanelInputsByKey.get(key);
    // Skip the hex box the player is actively typing/pasting into - overwriting it mid-edit would
    // clobber a partial value (e.g. "#ff") before it ever becomes a complete, valid hex.
    if (pair && document.activeElement !== pair.hexInput) {
      pair.swatch.style.background = entry.color;
      pair.hexInput.value = entry.color;
    }
  }
}

colorPanelResetAllButton.addEventListener("click", () => {
  colorOverrides = {};
  saveColorOverrides();
  refreshCurrentLayout();
});

// Set by renderLayout so a color change can redraw without re-fetching /api/layout - the palette
// is global, not per-seed, so only the image/legend/panel need to change, not the underlying layout data.
let currentLayout = null;
function refreshCurrentLayout() {
  if (currentLayout) {
    renderLayout(currentLayout);
  }
}

// Hotlinking: /preview/<slug>/<seed> loads straight into that layout without the user touching the
// form - this page is served for that whole path family via the server's MapFallbackToFile (see
// Program.cs), so setting/seed have to be pulled out of the URL path here rather than routed
// server-side. ?setting=&seed= query params are still read as a fallback so links shared before
// this switched to path-style URLs keep working. previewLayout() below keeps the URL in sync with
// whatever's currently shown (via history.replaceState) so the address bar itself is always a
// valid hotlink, even if the user got there by typing into the form rather than following a link.
function paramsFromPath() {
  // location.pathname is "/preview/<setting>/<seed>" (both segments optional) when reached via a
  // clean-URL hotlink; "/preview.html" (no extra segments) when the static file is hit directly.
  const segments = window.location.pathname.split("/").filter(Boolean);
  return { setting: segments[1] ?? null, seed: segments[2] ?? null };
}

async function loadSettings() {
  const settings = await fetchSettings();
  const params = new URLSearchParams(window.location.search);
  const fromPath = paramsFromPath();
  const settingFromUrl = fromPath.setting ?? params.get("setting");
  const seedFromUrl = fromPath.seed ?? params.get("seed");

  populateSettingSelect(settingSelect, settings, settingFromUrl);

  const settingIsValid = settings.some((s) => s.slug === settingFromUrl);
  if (settingIsValid && seedFromUrl && !validateSeed(seedFromUrl)) {
    seedInput.value = seedFromUrl;
    await previewLayout(settingFromUrl, seedFromUrl, false);
  }
}

function setStatus(message, isError) {
  statusEl.textContent = message;
  statusEl.classList.toggle("error", Boolean(isError));
}

function hexToRgb(hex) {
  const h = hex.trim().replace("#", "");
  return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
}

function rgbToHex(r, g, b) {
  const c = (n) => Math.max(0, Math.min(255, Math.round(n))).toString(16).padStart(2, "0");
  return `#${c(r)}${c(g)}${c(b)}`;
}

// HSV (not HSL) - the standard model for a saturation/value picker square, used only by the
// custom color-picker popover below (buildColorPickerPopover). Distinct from rgbToHsl/hslToRgb
// above, which drive shadeColor's per-instance variants.
function rgbToHsv(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  const v = max;
  const d = max - min;
  const s = max === 0 ? 0 : d / max;
  if (max === min) {
    return [0, s, v];
  }
  let h;
  switch (max) {
    case r: h = (g - b) / d + (g < b ? 6 : 0); break;
    case g: h = (b - r) / d + 2; break;
    default: h = (r - g) / d + 4; break;
  }
  return [h / 6, s, v];
}

function hsvToRgb(h, s, v) {
  const i = Math.floor(h * 6);
  const f = h * 6 - i;
  const p = v * (1 - s);
  const q = v * (1 - f * s);
  const t = v * (1 - (1 - f) * s);
  switch (i % 6) {
    case 0: return [v * 255, t * 255, p * 255];
    case 1: return [q * 255, v * 255, p * 255];
    case 2: return [p * 255, v * 255, t * 255];
    case 3: return [p * 255, q * 255, v * 255];
    case 4: return [t * 255, p * 255, v * 255];
    default: return [v * 255, p * 255, q * 255];
  }
}

function rgbToHsl(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  const l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h;
  switch (max) {
    case r: h = (g - b) / d + (g < b ? 6 : 0); break;
    case g: h = (b - r) / d + 2; break;
    default: h = (r - g) / d + 4; break;
  }
  return [h / 6, s, l];
}

function hslToRgb(h, s, l) {
  if (s === 0) {
    const v = l * 255;
    return [v, v, v];
  }
  const hue2rgb = (p, q, t) => {
    if (t < 0) t += 1;
    if (t > 1) t -= 1;
    if (t < 1 / 6) return p + (q - p) * 6 * t;
    if (t < 1 / 2) return q;
    if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
    return p;
  };
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;
  return [hue2rgb(p, q, h + 1 / 3) * 255, hue2rgb(p, q, h) * 255, hue2rgb(p, q, h - 1 / 3) * 255];
}

// Derives a visually distinct variant of a base room color for the Nth (0-based) separate physical
// room of the same type in one layout (used for Dining and Unassigned - see renderLayout) - shifts
// hue/lightness by a fixed step per index so instances stay "in the family" of the base color while
// being distinguishable from each other.
//
// Saturation is floored (not just shifted) because the "Unassigned" base color is a near-neutral
// gray (~0 saturation) - hue rotation alone is invisible on a desaturated color (hue is meaningless
// once there's no color to rotate), which made "Unassigned 1"/"Unassigned 2" render as two shades
// of gray 2/255 apart, indistinguishable in practice. Flooring saturation makes the hue shift
// actually show up as a real color difference; it's a no-op for already-saturated bases like Dining.
function shadeColor(baseHex, index) {
  if (index === 0) {
    return baseHex;
  }
  const [r, g, b] = hexToRgb(baseHex);
  let [h, s, l] = rgbToHsl(r, g, b);
  h = (h + 0.13 * index) % 1;
  s = Math.max(s, 0.4);
  // Floor raised from 0.28 to 0.5 (ceiling to 0.82) - see LayoutColors.ShadeColor's comment (kept in
  // sync by convention): with the brighter room palette, the old floor let a shaded instance collapse
  // to a near-black/navy variant, undoing the point of brightening the base colors.
  l = Math.min(0.82, Math.max(0.5, l + (index % 2 === 0 ? 0.12 : -0.12) * Math.ceil(index / 2)));
  const [nr, ng, nb] = hslToRgb(h, s, l);
  return rgbToHex(nr, ng, nb);
}

// Tile/feature loops below don't touch the DOM - they only exist to compute the legend (color per
// room-type/instance, feature types present), which needs the same per-instance Dining/Unassigned
// grouping used server-side. Group tiles by physical room instance (roomId), not just roomType,
// for the two room types where that distinction actually matters to a player:
//  - Dining: DiningDecorator only ever places tables in Dining rooms, so two separate dining
//    rooms in one layout are two real rooms a player eats in, not one.
//  - Unassigned: confirmed via real-game ground truth (session 3) that these are NOT empty
//    padding - they're genuine walkable side-rooms/corridors, each connected to the rest of the
//    layout by its own door, that the graph's room-generation modules just never assign a
//    gameplay type to (see game-domain-knowledge.md's Extended/Huge Layout room-shape notes).
//    This is exactly the "extra rooms" a user originally asked to see distinguished.
// Every other room type (Kitchen, Garden, ...) keeps one color/legend entry per type, however
// many separate physical rooms of that type exist - per explicit user decision, only rooms that
// are individually meaningful to a player (eaten in, or walked through as their own space) get
// split out.
function computeRoomGrouping(layout) {
  const PER_INSTANCE_ROOM_TYPES = new Set(["Dining", "Unassigned"]);
  const roomOrderByType = new Map(); // roomType -> [roomId, ...] in first-seen order
  const seenRoomIds = new Set();
  for (const tile of layout.tiles) {
    if (PER_INSTANCE_ROOM_TYPES.has(tile.roomType) && !seenRoomIds.has(tile.roomId)) {
      if (!roomOrderByType.has(tile.roomType)) {
        roomOrderByType.set(tile.roomType, []);
      }
      roomOrderByType.get(tile.roomType).push(tile.roomId);
    }
    seenRoomIds.add(tile.roomId);
  }
  const indexByRoomId = new Map();
  for (const order of roomOrderByType.values()) {
    order.forEach((id, i) => indexByRoomId.set(id, i));
  }
  return { roomOrderByType, indexByRoomId };
}

// Updates the legend and the customize panel only - no network request, no touching the <img>.
// Split out from renderLayout so a color-picker drag can update these live on every pointermove
// (cheap, client-side) while deferring the actual image re-fetch until the drag finishes (real
// player request - re-fetching the PNG on every drag tick was wasteful/janky).
function updateLegendAndPanel(layout) {
  const { roomOrderByType, indexByRoomId } = computeRoomGrouping(layout);
  // Single ordered source for both the legend and the customize panel (buildColorEntries) - keeps
  // them from ever drifting apart, and means the "R1/R2 always last" ordering (real player request)
  // only has to be implemented once.
  const colorEntries = buildColorEntries(layout, roomOrderByType, indexByRoomId);

  legendEl.innerHTML = "";
  for (const [, entry] of colorEntries) {
    const span = document.createElement("span");
    const swatch = document.createElement("span");
    swatch.className = "swatch";
    swatch.style.background = entry.color;
    span.appendChild(swatch);
    span.appendChild(document.createTextNode(entry.legendLabel));
    legendEl.appendChild(span);
  }

  syncColorPanel(colorEntries);
}

function refreshLegendAndPanel() {
  if (currentLayout) {
    updateLegendAndPanel(currentLayout);
  }
}

function renderLayout(layout) {
  const xs = layout.tiles.map((t) => t.x);
  const ys = layout.tiles.map((t) => t.y);
  const applianceXs = layout.appliances.map((a) => a.x);
  const applianceYs = layout.appliances.map((a) => a.y);
  const minX = Math.min(...xs, ...applianceXs);
  const maxX = Math.max(...xs, ...applianceXs);
  const minY = Math.min(...ys, ...applianceYs);
  const maxY = Math.max(...ys, ...applianceYs);
  const width = maxX - minX + 1;
  const height = maxY - minY + 1;

  // The server-rendered layout image (see Rendering/LayoutImageRenderer.cs) is the single visual
  // source of truth - also what gets shared/unfurled at /preview/<setting>/<seed>.png. No hover
  // hotspots/tooltips over it (dropped from scope) - it's just the image.
  gridEl.innerHTML = "";
  const image = document.createElement("img");
  image.className = "layout-image";
  image.alt = `${layout.settingName} — seed "${layout.seed}"`;
  // The colors query param is intentionally NOT part of the address-bar hotlink/Copy Link URL
  // (see previewLayout) - it only ever rides along on this <img> request the page makes for
  // itself, so a player's customization stays browser-local.
  image.src = `/preview/${encodeURIComponent(layout.settingSlug)}/${encodeURIComponent(layout.seed)}.png${buildColorsQueryParam()}`;
  gridEl.appendChild(image);

  currentLayout = layout;
  updateLegendAndPanel(layout);

  speedrunLinkEl.href = buildSpeedRunToolsUrl({
    seed: layout.seed,
    settingSlug: layout.settingSlug,
    cardPackSlug: null,
    dishName: null,
    heat: null,
    day: null,
  });

  const frontDoor = layout.features.find((f) => f.type === "FrontDoor");
  const frontDoorText = frontDoor ? `, front door at (${frontDoor.tile1X}, ${frontDoor.tile1Y})` : "";

  resultTitle.textContent =
    `${layout.settingName} — seed "${layout.seed}" — ${width}×${height} tiles, ` +
    `${layout.appliances.length} appliance placement(s)${frontDoorText}`;
  resultEl.hidden = false;
}

async function previewLayout(setting, seed, updateUrl = true) {
  setStatus("Loading...", false);
  resultEl.hidden = true;

  try {
    const response = await fetch(`/api/layout/${encodeURIComponent(setting)}/${encodeURIComponent(seed)}`);
    const body = await response.json();
    if (!response.ok) {
      setStatus(body.error ?? "Something went wrong.", true);
      return;
    }

    setStatus("", false);
    renderLayout(body);

    if (updateUrl) {
      const path = `/preview/${encodeURIComponent(setting)}/${encodeURIComponent(seed)}`;
      history.replaceState(null, "", path);
    }
  } catch (err) {
    setStatus("Failed to reach the server.", true);
  }
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();

  const setting = settingSelect.value;
  const seed = seedInput.value.trim();

  const seedError = validateSeed(seed);
  if (seedError) {
    setStatus(seedError, true);
    return;
  }

  await previewLayout(setting, seed);
});

copyLinkButton.addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(window.location.href);
    setStatus("Link copied to clipboard.", false);
  } catch {
    setStatus("Couldn't copy the link automatically - copy it from the address bar.", true);
  }
});

loadSettings().catch(() => setStatus("Failed to load restaurant settings.", true));
