const settingSelect = document.getElementById("setting-select");
const seedInput = document.getElementById("seed-input");
const form = document.getElementById("layout-form");
const statusEl = document.getElementById("status");
const resultEl = document.getElementById("result");
const resultTitle = document.getElementById("result-title");
const gridEl = document.getElementById("grid");
const legendEl = document.getElementById("legend");

const FRIENDLY_ALPHABET = "bdghjmqrtvwxy346789";

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
  if (seed.length === 0 || seed.length > 8) {
    return "Seed must be 1-8 characters.";
  }
  for (const ch of seed) {
    if (!FRIENDLY_ALPHABET.includes(ch)) {
      return `Seed can only contain: ${FRIENDLY_ALPHABET}`;
    }
  }
  return null;
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

  resultTitle.textContent =
    `${layout.settingName} — seed "${layout.seed}" — ${width}×${height} tiles, ` +
    `${layout.appliances.length} appliance placement(s)`;
  resultEl.hidden = false;
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();

  const setting = settingSelect.value;
  const seed = seedInput.value.trim().toLowerCase();

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
