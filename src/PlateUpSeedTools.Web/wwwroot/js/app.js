const settingSelect = document.getElementById("setting-select");
const seedInput = document.getElementById("seed-input");
const form = document.getElementById("layout-form");
const statusEl = document.getElementById("status");
const resultEl = document.getElementById("result");
const resultTitle = document.getElementById("result-title");
const gridEl = document.getElementById("grid");
const legendEl = document.getElementById("legend");
const copyLinkButton = document.getElementById("copy-link");

// One reusable tooltip element, created once (not per-appliance/per-render) - a custom styled
// popup instead of the native title-attribute tooltip, positioned to follow the cursor.
const tooltipEl = document.createElement("div");
tooltipEl.className = "tooltip";
tooltipEl.hidden = true;
document.body.appendChild(tooltipEl);

function showTooltip(text, x, y) {
  tooltipEl.textContent = text;
  tooltipEl.style.left = `${x + 14}px`;
  tooltipEl.style.top = `${y + 14}px`;
  tooltipEl.hidden = false;
}

function hideTooltip() {
  tooltipEl.hidden = true;
}

// VALID_SEED/validateSeed/fetchSettings/populateSettingSelect come from shared.js.

// Kept in sync with theme.css's .feature.type-* rules.
const FEATURE_COLORS = {
  frontdoor: "#ffe066",
  door: "#f1e6d8",
  doorreversed: "#f1e6d8",
  hatch: "#7fd4c1",
};
const DEFAULT_FEATURE_COLOR = "#9a8a76";
function featureColor(type) {
  return FEATURE_COLORS[type.toLowerCase()] ?? DEFAULT_FEATURE_COLOR;
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

// Which edge of Tile1's cell a feature sits on, based on the direction to Tile2. North is up
// (matches the tile rendering's Y-flip below). FrontDoor's Tile2 is one step *outside* the tile
// grid (see CreateFrontDoorNode) so it's always a cardinal direction here, never zero.
function edgeClassFor(dx, dy) {
  if (dy > 0) return "edge-north";
  if (dy < 0) return "edge-south";
  if (dx > 0) return "edge-east";
  return "edge-west";
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
  l = Math.min(0.72, Math.max(0.28, l + (index % 2 === 0 ? 0.12 : -0.12) * Math.ceil(index / 2)));
  const [nr, ng, nb] = hslToRgb(h, s, l);
  return rgbToHex(nr, ng, nb);
}

function renderLayout(layout) {
  hideTooltip();

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

  gridEl.style.gridTemplateColumns = `repeat(${width}, var(--tile-size))`;
  gridEl.style.gridTemplateRows = `repeat(${height}, var(--tile-size))`;
  gridEl.innerHTML = "";

  // The server-rendered layout image (see Rendering/LayoutImageRenderer.cs) is the single visual
  // source of truth - also what gets shared/unfurled at /preview/<setting>/<seed>.png. It's stretched
  // to fill the whole grid via CSS (see .layout-image in theme.css), so every tile/appliance/feature
  // hotspot div below - still positioned via the same gridColumn/gridRow math as before - lines up
  // with the image regardless of on-screen scale/zoom: both are simple W-way/H-way subdivisions of
  // the same box (this is *why* .grid's `gap` had to become 0 - see theme.css's comment on .grid).
  const image = document.createElement("img");
  image.className = "layout-image";
  image.alt = `${layout.settingName} — seed "${layout.seed}"`;
  image.src = `/preview/${encodeURIComponent(layout.settingSlug)}/${encodeURIComponent(layout.seed)}.png`;
  gridEl.appendChild(image);

  // Group tiles by physical room instance (roomId), not just roomType, for the two room types where
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
  const baseColorByType = new Map(
    [...PER_INSTANCE_ROOM_TYPES].map((t) => [t, getComputedStyle(document.documentElement).getPropertyValue(`--room-${t.toLowerCase()}`)])
  );

  function styleForTile(tile) {
    const order = roomOrderByType.get(tile.roomType);
    if (order && order.length > 1) {
      const index = indexByRoomId.get(tile.roomId);
      return { color: shadeColor(baseColorByType.get(tile.roomType), index), label: `${tile.roomType} ${index + 1}` };
    }
    return { color: `var(--room-${tile.roomType.toLowerCase()}, var(--room-unassigned))`, label: tile.roomType };
  }

  const legendRoomsSeen = new Map();

  for (const tile of layout.tiles) {
    const div = document.createElement("div");
    const style = styleForTile(tile);
    div.className = "tile";
    div.style.gridColumn = String(tile.x - minX + 1);
    div.style.gridRow = String(maxY - tile.y + 1); // flip Y so north is up
    div.title = `${style.label} (${tile.x}, ${tile.y})`;
    gridEl.appendChild(div);
    legendRoomsSeen.set(style.label, style.color);
  }

  for (const appliance of layout.appliances) {
    const div = document.createElement("div");
    div.className = "appliance";
    div.style.gridColumn = String(appliance.x - minX + 1);
    div.style.gridRow = String(maxY - appliance.y + 1);

    // Icon/text-abbreviation rendering (icon-if-available, rotated abbreviated text otherwise) now
    // happens server-side, baked into the shared layout image (see Rendering/LayoutImageRenderer.cs)
    // - this div is just an invisible hover target lined up with the image's cell.
    const tooltipText = `${appliance.applianceName} (${appliance.x}, ${appliance.y}), ${appliance.rotationDegrees}°`;
    div.addEventListener("pointerenter", (event) => showTooltip(tooltipText, event.clientX, event.clientY));
    div.addEventListener("pointermove", (event) => showTooltip(tooltipText, event.clientX, event.clientY));
    div.addEventListener("pointerleave", hideTooltip);
    gridEl.appendChild(div);
  }

  const featureTypesSeen = new Set();

  for (const feature of layout.features) {
    const dx = feature.tile2X - feature.tile1X;
    const dy = feature.tile2Y - feature.tile1Y;
    const div = document.createElement("div");
    const typeClass = feature.type.toLowerCase();
    div.className = `feature type-${typeClass} ${edgeClassFor(dx, dy)}`;
    div.style.gridColumn = String(feature.tile1X - minX + 1);
    div.style.gridRow = String(maxY - feature.tile1Y + 1);
    div.title = `${feature.type} between (${feature.tile1X}, ${feature.tile1Y}) and (${feature.tile2X}, ${feature.tile2Y})`;
    gridEl.appendChild(div);
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
