const settingSelect = document.getElementById("setting-select");
const seedInput = document.getElementById("seed-input");
const form = document.getElementById("quick-form");
const statusEl = document.getElementById("status");

function setStatus(message, isError) {
  statusEl.textContent = message;
  statusEl.classList.toggle("error", Boolean(isError));
}

async function loadSettings() {
  const settings = await fetchSettings();
  populateSettingSelect(settingSelect, settings);
}

// The homepage doesn't render a layout itself - it just hands off to the previewer with the
// setting/seed baked into the URL, which is also what makes the resulting preview link hotlinkable.
form.addEventListener("submit", (event) => {
  event.preventDefault();

  const setting = settingSelect.value;
  const seed = seedInput.value.trim();

  const seedError = validateSeed(seed);
  if (seedError) {
    setStatus(seedError, true);
    return;
  }

  window.location.href = `/preview/${encodeURIComponent(setting)}/${encodeURIComponent(seed)}`;
});

loadSettings().catch(() => setStatus("Failed to load restaurant settings.", true));
