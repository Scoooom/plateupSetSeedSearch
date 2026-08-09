const settingSelect = document.getElementById("setting-select");
const seedInput = document.getElementById("seed-input");
const form = document.getElementById("layout-form");
const statusEl = document.getElementById("status");
const resultEl = document.getElementById("result");
const resultTitle = document.getElementById("result-title");
const gridEl = document.getElementById("grid");
const legendEl = document.getElementById("legend");

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

// Seeds can be any alphanumeric string up to 8 characters - the game's "friendly" alphabet
// (bdghjmqrtvwxy346789) is only used when it auto-generates a seed for display, it doesn't
// restrict what a typed/shared seed can contain. Case matters (the hash is case-sensitive), so
// this must NOT lowercase the input.
const VALID_SEED = /^[a-zA-Z0-9]{1,8}$/;

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

async function loadSettings() {
  const response = await fetch("/api/settings");
  const settings = await response.json();
  settingSelect.innerHTML = "";
  for (const setting of settings) {
    const option = document.createElement("option");
    option.value = setting.slug;
    option.textContent = setting.displayName;
    settingSelect.appendChild(option);
  }

  const banquet = settings.find((s) => s.slug === "banquet-hall");
  if (banquet) {
    settingSelect.value = banquet.slug;
  }
}

function setStatus(message, isError) {
  statusEl.textContent = message;
  statusEl.classList.toggle("error", Boolean(isError));
}

function validateSeed(seed) {
  if (!VALID_SEED.test(seed)) {
    return "Seed must be 1-8 alphanumeric characters.";
  }
  return null;
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

// Short label for an appliance's on-tile text - used as a fallback for appliances with no matching
// icon (see iconPathFor), since names like "Blueprint Cabinet" don't fit the small appliance box.
function abbreviate(name) {
  const words = name.split(/\s+/).filter(Boolean);
  if (words.length === 0) return "?";
  if (words.length === 1) return words[0].slice(0, 3).toUpperCase();
  return words.slice(0, 3).map((w) => w[0].toUpperCase()).join("");
}

// Appliance icons (images/appliances/*.png) are keyed by kebab-cased display name, sourced from
// https://github.com/plateupplanner/plateupplanner.github.io (community PlateUp planner's sprite
// set) - not every resolved appliance name has a matching icon there (e.g. "Enchanting Desk", and
// "Provider" is our own synthesized label for a generic placeholder slot with no single real-world
// appliance to depict), so callers must handle the <img> failing to load (see renderLayout).
function iconPathFor(name) {
  const slug = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return `images/appliances/${slug}.png`;
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
    div.style.background = style.color;
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

    // Real appliance icon when one exists for this name; falls back to the abbreviated-text box
    // (rotated, matching the old behavior) if the image 404s - not every appliance name has a
    // matching sprite (see iconPathFor).
    const img = document.createElement("img");
    img.src = iconPathFor(appliance.applianceName);
    img.alt = appliance.applianceName;
    img.style.transform = `rotate(${appliance.rotationDegrees}deg)`;
    img.addEventListener("error", () => {
      img.remove();
      div.textContent = abbreviate(appliance.applianceName);
      div.style.transform = `rotate(${appliance.rotationDegrees}deg)`;
    });
    div.appendChild(img);

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
    div.style.background = featureColor(feature.type);
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

form.addEventListener("submit", async (event) => {
  event.preventDefault();

  const setting = settingSelect.value;
  const seed = seedInput.value.trim();

  const seedError = validateSeed(seed);
  if (seedError) {
    setStatus(seedError, true);
    return;
  }

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
  } catch (err) {
    setStatus("Failed to reach the server.", true);
  }
});

loadSettings().catch(() => setStatus("Failed to load restaurant settings.", true));
