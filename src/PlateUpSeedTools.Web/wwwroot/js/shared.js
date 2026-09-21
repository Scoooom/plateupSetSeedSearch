// Shared between index.html (home) and preview.html - no page-specific DOM references here.

// Seeds can be any alphanumeric string up to 8 characters - the game's "friendly" alphabet
// (bdghjmqrtvwxy346789) is only used when it auto-generates a seed for display, it doesn't
// restrict what a typed/shared seed can contain. Case matters (the hash is case-sensitive), so
// this must NOT lowercase the input.
const VALID_SEED = /^[a-zA-Z0-9]{1,8}$/;

function validateSeed(seed) {
  if (!VALID_SEED.test(seed)) {
    return "Seed must be 1-8 alphanumeric characters.";
  }
  return null;
}

async function fetchSettings() {
  const response = await fetch("/api/settings");
  return response.json();
}

// Fills selectEl with <option>s from settings, preferring preferredSlug if it's one of them
// (falls back to banquet-hall, then to whatever's first).
function populateSettingSelect(selectEl, settings, preferredSlug) {
  selectEl.innerHTML = "";
  for (const setting of settings) {
    const option = document.createElement("option");
    option.value = setting.slug;
    option.textContent = setting.displayName;
    selectEl.appendChild(option);
  }
  const match =
    settings.find((s) => s.slug === preferredSlug) ?? settings.find((s) => s.slug === "banquet-hall");
  if (match) {
    selectEl.value = match.slug;
  }
}

// Only "normal" represents more than one real restaurant setting merged together (session 6's
// layout consolidation - see CLAUDE.md) - the other settings (banquet-hall, witch-hut,
// santa-workshop) each have exactly one real card pack (their own setting.json's unlockPackId, or
// the Default pack if unset), so overriding the card schedule away from that models a
// setting+schedule combination that can never happen in the real game (e.g. Banquet Hall never
// actually uses the Autumn pack). Locks the override back to "use the setting's own schedule" and
// disables the control whenever a non-normal setting is selected.
function updateCardPackLock(settingSelectEl, cardPackSelectEl) {
  const locked = settingSelectEl.value !== "normal";
  cardPackSelectEl.disabled = locked;
  if (locked) {
    cardPackSelectEl.value = "";
  }
  cardPackSelectEl.title = locked
    ? "Only the Normal setting has more than one real card schedule - this setting always uses its own."
    : "";
}
