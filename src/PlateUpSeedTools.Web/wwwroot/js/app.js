const settingSelect = document.getElementById("setting-select");
const seedInput = document.getElementById("seed-input");
const form = document.getElementById("layout-form");
const statusEl = document.getElementById("status");
const resultEl = document.getElementById("result");
const resultTitle = document.getElementById("result-title");
const gridEl = document.getElementById("grid");
const legendEl = document.getElementById("legend");

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

  gridEl.style.gridTemplateColumns = `repeat(${width}, var(--tile-size))`;
  gridEl.style.gridTemplateRows = `repeat(${height}, var(--tile-size))`;
  gridEl.innerHTML = "";

  const roomTypesSeen = new Set();

  for (const tile of layout.tiles) {
    const div = document.createElement("div");
    const cssClass = tile.roomType.toLowerCase();
    div.className = "tile";
    div.style.background = `var(--room-${cssClass}, var(--room-unassigned))`;
    div.style.gridColumn = String(tile.x - minX + 1);
    div.style.gridRow = String(maxY - tile.y + 1); // flip Y so north is up
    div.title = `${tile.roomType} (${tile.x}, ${tile.y})`;
    gridEl.appendChild(div);
    roomTypesSeen.add(tile.roomType);
  }

  for (const appliance of layout.appliances) {
    const div = document.createElement("div");
    div.className = "appliance";
    div.style.gridColumn = String(appliance.x - minX + 1);
    div.style.gridRow = String(maxY - appliance.y + 1);
    div.style.transform = `rotate(${appliance.rotationDegrees}deg)`;
    div.textContent = appliance.applianceId;
    div.title = `Appliance ${appliance.applianceId} (${appliance.x}, ${appliance.y}), ${appliance.rotationDegrees}°`;
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
  for (const roomType of [...roomTypesSeen].sort()) {
    const span = document.createElement("span");
    const swatch = document.createElement("span");
    swatch.className = "swatch";
    swatch.style.background = `var(--room-${roomType.toLowerCase()}, var(--room-unassigned))`;
    span.appendChild(swatch);
    span.appendChild(document.createTextNode(roomType));
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
