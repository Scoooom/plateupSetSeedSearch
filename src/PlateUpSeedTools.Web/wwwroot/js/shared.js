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

// Wires the Export/Import Settings dialog shared by search.html and explorer.html - a client-only
// base64(JSON) blob of the page's own settings object, no server round-trip (unlike Copy Link's
// server-stored hash), so it never expires and needs no cache. Both pages have identical dialog
// markup (#settings-io-dialog and friends) - this is the one place that behavior is written.
function setupSettingsIoDialog({
  exportButton, importButton, dialog, title, status, textarea,
  copyButton, applyButton, closeButton, buildExport, applyImport,
}) {
  function close() {
    dialog.close();
  }

  function setStatus(message, isError) {
    status.textContent = message;
    status.classList.toggle("error", Boolean(isError));
  }

  exportButton.addEventListener("click", () => {
    setStatus("", false);
    title.textContent = "Export Settings";
    textarea.value = btoa(JSON.stringify(buildExport()));
    textarea.readOnly = true;
    copyButton.hidden = false;
    applyButton.hidden = true;
    dialog.showModal();
    textarea.select();
  });

  importButton.addEventListener("click", () => {
    setStatus("", false);
    title.textContent = "Import Settings";
    textarea.value = "";
    textarea.readOnly = false;
    copyButton.hidden = true;
    applyButton.hidden = false;
    dialog.showModal();
    textarea.focus();
  });

  copyButton.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(textarea.value);
      setStatus("Copied to clipboard.", false);
    } catch {
      setStatus("Failed to copy - select the text and copy manually.", true);
    }
  });

  applyButton.addEventListener("click", async () => {
    let parsed;
    try {
      parsed = JSON.parse(atob(textarea.value.trim()));
    } catch {
      setStatus("That doesn't look like a valid exported settings string.", true);
      return;
    }
    try {
      await applyImport(parsed);
      close();
    } catch (err) {
      setStatus(err?.message ?? "Failed to apply these settings.", true);
    }
  });

  closeButton.addEventListener("click", close);
  dialog.addEventListener("click", (event) => {
    if (event.target === dialog) close();
  });
}
