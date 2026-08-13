const settingSelect = document.getElementById("setting-select");
const seedInput = document.getElementById("seed-input");
const form = document.getElementById("layout-form");
const statusEl = document.getElementById("status");
const resultEl = document.getElementById("result");
const resultTitle = document.getElementById("result-title");
const gridEl = document.getElementById("grid");
const legendEl = document.getElementById("legend");
const copyLinkButton = document.getElementById("copy-link");
const colorPanelRowsEl = document.getElementById("color-panel-rows");
const colorPanelResetAllButton = document.getElementById("color-panel-reset-all");

// VALID_SEED/validateSeed/fetchSettings/populateSettingSelect come from shared.js.

// Used only for the legend's feature swatches now (the layout image itself draws features
// server-side, see LayoutColors.cs) - kept in sync with that C# copy by convention.
const FEATURE_COLORS = {
  frontdoor: "#ffe066",
  door: "#f1e6d8",
  doorreversed: "#f1e6d8",
  hatch: "#7fd4c1",
};
const DEFAULT_FEATURE_COLOR = "#9a8a76";
function featureColor(type) {
  // "doorreversed" shares the "door" customization swatch too - mirrors LayoutImageRenderer.DrawFeatures.
  const key = type.toLowerCase() === "doorreversed" ? "door" : type.toLowerCase();
  if (DEFAULT_COLORS.has(key)) {
    return resolveColor(key);
  }
  return FEATURE_COLORS[type.toLowerCase()] ?? DEFAULT_FEATURE_COLOR;
}

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

// Player color customization (real player request) - full per-swatch pickers over everything in
// LayoutColors.cs (14 room fills + wall + door/frontdoor/hatch), saved to localStorage and
// deliberately browser-local: only the <img> request this page makes for itself carries the
// `colors` query param (see renderLayout below) - the address bar URL, "Copy link", and the
// shareable /preview/.../seed.png used for Discord/OG embeds never do, so anyone else opening a
// shared link always sees the shipped default palette. `kind` drives where each field's shipped
// default color comes from (defaultColorFor below); `key` is what both this page and
// LayoutEndpoints.GetLayoutPng's `?colors=` parsing use to identify a swatch.
const COLOR_FIELDS = [
  { key: "dining", label: "Dining", kind: "room" },
  { key: "kitchen", label: "Kitchen", kind: "room" },
  { key: "garden", label: "Garden", kind: "room" },
  { key: "workshop", label: "Workshop", kind: "room" },
  { key: "office", label: "Office", kind: "room" },
  { key: "frontentrance", label: "Front Entrance", kind: "room" },
  { key: "locations", label: "Locations", kind: "room" },
  { key: "storage", label: "Storage", kind: "room" },
  { key: "bedroom", label: "Bedroom", kind: "room" },
  { key: "trophies", label: "Trophies", kind: "room" },
  { key: "garage", label: "Garage", kind: "room" },
  { key: "contracts", label: "Contracts", kind: "room" },
  { key: "unassigned", label: "Unassigned", kind: "room" },
  { key: "socialspace", label: "Social Space", kind: "room" },
  { key: "wall", label: "Wall", kind: "wall" },
  { key: "door", label: "Door", kind: "feature" },
  { key: "frontdoor", label: "Front Door", kind: "feature" },
  { key: "hatch", label: "Hatch", kind: "feature" },
];

function shippedDefaultColor(field) {
  if (field.kind === "room") {
    return getComputedStyle(document.documentElement).getPropertyValue(`--room-${field.key}`).trim();
  }
  if (field.kind === "wall") {
    return WALL_COLOR;
  }
  return FEATURE_COLORS[field.key] ?? DEFAULT_FEATURE_COLOR;
}

// Computed once - theme.css's custom properties don't change at runtime.
const DEFAULT_COLORS = new Map(COLOR_FIELDS.map((field) => [field.key, shippedDefaultColor(field)]));

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

// The single source of truth for "what color is this thing right now" - a player's override if
// they've set one, else the shipped default. Used both for the legend swatches (computed here,
// client-side) and to build the `colors` param the rendered <img> asks the server for.
function resolveColor(key) {
  return colorOverrides[key] ?? DEFAULT_COLORS.get(key);
}

function buildColorsQueryParam() {
  return Object.keys(colorOverrides).length > 0 ? `?colors=${encodeURIComponent(JSON.stringify(colorOverrides))}` : "";
}

function renderColorPanel() {
  colorPanelRowsEl.innerHTML = "";
  for (const field of COLOR_FIELDS) {
    const row = document.createElement("div");
    row.className = "color-panel-row";

    const label = document.createElement("span");
    label.textContent = field.label;

    const input = document.createElement("input");
    input.type = "color";
    input.value = resolveColor(field.key);
    input.setAttribute("aria-label", `${field.label} color`);
    input.addEventListener("input", () => {
      colorOverrides[field.key] = input.value;
      saveColorOverrides();
      refreshCurrentLayout();
    });

    const reset = document.createElement("button");
    reset.type = "button";
    reset.className = "color-reset";
    reset.title = `Reset ${field.label} to default`;
    reset.setAttribute("aria-label", `Reset ${field.label} to default`);
    reset.textContent = "↺";
    reset.addEventListener("click", () => {
      delete colorOverrides[field.key];
      saveColorOverrides();
      input.value = resolveColor(field.key);
      refreshCurrentLayout();
    });

    row.append(label, input, reset);
    colorPanelRowsEl.appendChild(row);
  }
}

colorPanelResetAllButton.addEventListener("click", () => {
  colorOverrides = {};
  saveColorOverrides();
  renderColorPanel();
  refreshCurrentLayout();
});

// Set by renderLayout so a color change can redraw without re-fetching /api/layout - the palette
// is global, not per-seed, so only the image/legend need to change, not the underlying layout data.
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

  // Tile/feature loops below don't touch the DOM anymore - they only exist to compute the legend
  // (color per room-type/instance, feature types present), which still needs the same per-instance
  // Dining/Unassigned grouping used server-side. Group tiles by physical room instance (roomId), not
  // just roomType, for the two room types where
  // that distinction actually matters to a player:
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
  const baseColorByType = new Map([...PER_INSTANCE_ROOM_TYPES].map((t) => [t, resolveColor(t.toLowerCase())]));

  function styleForTile(tile) {
    // Unassigned rooms have no real per-room identity in the game data - a shared "UNA" code on
    // every one of them was ambiguous when a layout has several, so each physical instance gets its
    // own R1/R2/R3... instead (mirrors LayoutColors/LayoutImageRenderer server-side), in the same
    // first-seen order used for shading below, so "R2" always lines up with the second-shaded tile.
    const code = tile.roomType === "Unassigned" ? `R${indexByRoomId.get(tile.roomId) + 1}` : roomCode(tile.roomType);
    const order = roomOrderByType.get(tile.roomType);
    if (order && order.length > 1) {
      const index = indexByRoomId.get(tile.roomId);
      return { color: shadeColor(baseColorByType.get(tile.roomType), index), label: `${tile.roomType} ${index + 1} (${code})` };
    }
    return { color: resolveColor(tile.roomType.toLowerCase()), label: `${tile.roomType} (${code})` };
  }

  const legendRoomsSeen = new Map();
  for (const tile of layout.tiles) {
    const style = styleForTile(tile);
    legendRoomsSeen.set(style.label, style.color);
  }

  const featureTypesSeen = new Set();
  for (const feature of layout.features) {
    featureTypesSeen.add(feature.type);
  }

  legendEl.innerHTML = "";
  for (const label of [...legendRoomsSeen.keys()].sort()) {
    const span = document.createElement("span");
    const swatch = document.createElement("span");
    swatch.className = "swatch";
    swatch.style.background = legendRoomsSeen.get(label);
    span.appendChild(swatch);
    span.appendChild(document.createTextNode(label));
    legendEl.appendChild(span);
  }
  for (const featureType of [...featureTypesSeen].sort()) {
    const span = document.createElement("span");
    const swatch = document.createElement("span");
    swatch.className = "swatch";
    swatch.style.background = featureColor(featureType);
    span.appendChild(swatch);
    span.appendChild(document.createTextNode(featureType));
    legendEl.appendChild(span);
  }
  {
    const span = document.createElement("span");
    const swatch = document.createElement("span");
    swatch.className = "swatch";
    swatch.style.background = resolveColor("wall");
    span.appendChild(swatch);
    span.appendChild(document.createTextNode("Wall"));
    legendEl.appendChild(span);
  }

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

renderColorPanel();
loadSettings().catch(() => setStatus("Failed to load restaurant settings.", true));
