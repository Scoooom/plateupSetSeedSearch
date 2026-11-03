// Shared across all four pages (index, preview, search, explorer) - no page-specific DOM
// references here.

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
  // Title tooltip alone is invisible on mobile (no hover) - dim the whole field-group so
  // "disabled" reads as a deliberate lock, not a broken control. The field-note sibling spells
  // out why it's locked.
  cardPackSelectEl.closest(".field-group")?.classList.toggle("locked", locked);
}

let cardInputCounter = 0;

function setDatalistOptions(datalist, names) {
  datalist.innerHTML = "";
  for (const name of names) {
    const option = document.createElement("option");
    option.value = name;
    datalist.appendChild(option);
  }
}

// The datalist backing a card input built by createCardInput below, looked up by stored id - NOT
// via the `list` attribute (createCardInput deliberately never sets one, see its own comment).
function datalistFor(input) {
  return document.getElementById(input.dataset.datalistId);
}

// Builds a free-text input for picking a card by name, backed by a <datalist> that holds the
// current candidate list (read elsewhere via datalistFor(input)), plus a hand-rolled suggestion
// dropdown rendered in plain DOM. The dropdown exists because the native <datalist> popup turned
// out to depend on the OS keyboard, not just the browser: confirmed on a real iPhone that
// suggestions show fine with Apple's stock keyboard but never appear at all with a third-party
// keyboard app (SwiftKey, Gboard, etc.), since those don't participate in Safari's own
// suggestion-strip UI - no code-level fix makes a native datalist popup appear there. This input
// deliberately never sets the `list` attribute at all, so no browser's own datalist popup can
// render and visually stack with this one on platforms where it otherwise would.
function createCardInput(className, idPrefix = "card") {
  const input = document.createElement("input");
  input.type = "text";
  input.className = `card-name-input ${className}`;
  input.autocomplete = "off";
  input.placeholder = "Card name";

  const datalistId = `${idPrefix}-datalist-${cardInputCounter++}`;
  input.dataset.datalistId = datalistId;
  const datalist = document.createElement("datalist");
  datalist.id = datalistId;

  const wrapper = document.createElement("div");
  wrapper.className = "card-input-wrapper";
  const suggestionsBox = document.createElement("div");
  suggestionsBox.className = "card-suggestions";
  suggestionsBox.hidden = true;
  wrapper.appendChild(input);
  wrapper.appendChild(suggestionsBox);

  function closeSuggestions() {
    suggestionsBox.hidden = true;
    suggestionsBox.innerHTML = "";
  }

  function openSuggestions() {
    if (input.disabled) {
      return;
    }
    const query = input.value.trim().toLowerCase();
    const options = [...datalist.options].map((option) => option.value);
    const matches = (query ? options.filter((name) => name.toLowerCase().includes(query)) : options).slice(0, 25);
    suggestionsBox.innerHTML = "";
    if (matches.length === 0) {
      suggestionsBox.hidden = true;
      return;
    }
    for (const name of matches) {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "card-suggestion-item";
      item.textContent = name;
      // mousedown (not click) fires before the input would blur, and preventDefault stops that
      // blur from happening at all - so picking a suggestion can't race the blur-close below.
      item.addEventListener("mousedown", (event) => {
        event.preventDefault();
        input.value = name;
        closeSuggestions();
        input.dispatchEvent(new Event("change", { bubbles: true }));
      });
      suggestionsBox.appendChild(item);
    }
    suggestionsBox.hidden = false;
  }

  input.addEventListener("focus", openSuggestions);
  input.addEventListener("input", openSuggestions);
  input.addEventListener("blur", closeSuggestions);

  return { input, datalist, wrapper };
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
