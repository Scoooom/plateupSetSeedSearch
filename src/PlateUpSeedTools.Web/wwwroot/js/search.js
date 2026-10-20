const settingSelect = document.getElementById("setting-select");
const dishSelect = document.getElementById("dish-select");
const subOptionField = document.getElementById("sub-option-field");
const subOption1Label = document.getElementById("sub-option-1-label");
const subOption2Label = document.getElementById("sub-option-2-label");
const heatSelect = document.getElementById("heat-select");
const cardPackSelect = document.getElementById("card-pack-select");
const tierInput = document.getElementById("tier-input");
const alphabetInput = document.getElementById("alphabet-input");
const lengthInput = document.getElementById("length-input");
const limitInput = document.getElementById("limit-input");
const maxResultsInput = document.getElementById("max-results-input");
const dayTargetRowsEl = document.getElementById("day-target-rows");
const addDayTargetButton = document.getElementById("add-day-target");
const franchiseCardRowsEl = document.getElementById("franchise-card-rows");
const addFranchiseCardButton = document.getElementById("add-franchise-card");
const stopAfterFirstMatchInput = document.getElementById("stop-after-first-match");
const notifyField = document.getElementById("notify-field");
const notifyOnCompletionInput = document.getElementById("notify-on-completion");
const notifyPermissionWarning = document.getElementById("notify-permission-warning");
const otFriendlyInput = document.getElementById("ot-friendly");
const mapSizeFieldGroup = document.getElementById("map-size-field-group");
const mapSizeCheckboxes = [...document.querySelectorAll(".map-size-checkbox")];
const achievementPresetSelect = document.getElementById("achievement-preset-select");
const achievementPresetExplanation = document.getElementById("achievement-preset-explanation");
const allowRerollInput = document.getElementById("allow-reroll");
const excludeSeedsInput = document.getElementById("exclude-seeds-input");
const recentSearchesSection = document.getElementById("recent-searches");
const recentSearchesList = document.getElementById("recent-searches-list");
const form = document.getElementById("search-form");
const statusEl = document.getElementById("status");
const jobEl = document.getElementById("job");
const jobStatusText = document.getElementById("job-status-text");
const cancelButton = document.getElementById("cancel-search");
const progressBar = document.getElementById("progress-bar");
const progressBarFill = document.getElementById("progress-bar-fill");
const jobStats = document.getElementById("job-stats");
const matchesList = document.getElementById("matches-list");
const copyMatchesButton = document.getElementById("copy-matches");
const copyLinkButton = document.getElementById("copy-link");
const saveSearchButton = document.getElementById("save-search");
const previewDialog = document.getElementById("preview-dialog");
const previewDialogTitle = document.getElementById("preview-dialog-title");
const previewDialogClose = document.getElementById("preview-dialog-close");
const previewDialogStatus = document.getElementById("preview-dialog-status");
const previewDialogImageLink = document.getElementById("preview-dialog-image-link");
const previewDialogImage = document.getElementById("preview-dialog-image");
const previewDialogStats = document.getElementById("preview-dialog-stats");
const previewDialogEquivalents = document.getElementById("preview-dialog-equivalents");
const previewDialogPathTabs = document.getElementById("preview-dialog-path-tabs");
const previewDialogPath = document.getElementById("preview-dialog-path");
const previewDialogExplorerLink = document.getElementById("preview-dialog-explorer-link");
const previewDialogSaveFavorite = document.getElementById("preview-dialog-save-favorite");
const previewDialogPrev = document.getElementById("preview-dialog-prev");
const previewDialogNext = document.getElementById("preview-dialog-next");
const previewDialogPosition = document.getElementById("preview-dialog-position");

// VALID_SEED/fetchSettings/populateSettingSelect come from shared.js.

let unlocksForSetting = [];
let pollHandle = null;
let currentJobId = null;
let currentSearchSetting = null;
let currentSearchPayload = null;
let currentMatches = [];
// Index into currentMatches (the FULL list, not the 500-capped DOM chips) of the seed currently
// shown in the preview dialog - drives the prev/next navigation. -1 when the open seed isn't in
// the list (shouldn't happen from a chip click, but guard anyway).
let currentPreviewIndex = -1;
// Bumped on every populatePreviewDialog call; a stale in-flight populate (slow /api/layout or a
// cold PNG render) checks this after each await and bails rather than clobbering a newer selection.
let previewRequestToken = 0;
// seed -> resolved preview entry (image URL/alt/stats + retained decoded Image + card paths), so
// paging back to an already-viewed match is instant and the match on each side is warmed ahead of
// time. Scoped to the current search: cleared + generation bumped whenever currentSearchPayload
// changes. LRU-capped so a long page-through of a big result set can't grow it without bound.
const previewCache = new Map();
const previewInFlight = new Map(); // seed -> Promise<entry>, dedupes concurrent resolves
let previewCacheGeneration = 0;
const PREVIEW_CACHE_MAX = 60;
// Cached per currentJobId - repeated Copy Link clicks for the same job's results must reuse the
// same hash/DB row rather than minting a new one every click (a real bug: clicking rapidly used to
// duplicate a cache row per click). Reset to null anywhere currentJobId itself changes. Also set
// directly (not via a click) for a notify-enabled job, which gets its hash eagerly at job start -
// see the submit handler and loadInit's "live" link branch.
let currentShareHash = null;
let datalistCounter = 0;

// "Notify on completion" (Web Push) state - see notifyOnCompletionInput's change handler below.
// pushSupported/pushConfigured gate whether notifyField is ever shown at all; currentPushSubscription
// is the live PushSubscription object (or null) the browser handed back from pushManager.subscribe(),
// attached to a job via POST /api/search/{jobId}/push-subscription right after it's created. Not
// reset between searches - once subscribed, the same subscription is reused for every subsequent
// notify-enabled search in this tab (subscribing again when already subscribed is a no-op that
// returns the same subscription).
let pushSupported = false;
let pushConfigured = false;
let vapidPublicKey = null;
let currentPushSubscription = null;

// Standard Web Push boilerplate: PushManager.subscribe's applicationServerKey wants a raw
// Uint8Array, not the URL-safe base64 string the server hands out.
function urlBase64ToUint8Array(base64String) {
  const padding = "=".repeat((4 - (base64String.length % 4)) % 4);
  const base64 = (base64String + padding).replace(/-/g, "+").replace(/_/g, "/");
  const rawData = atob(base64);
  const outputArray = new Uint8Array(rawData.length);
  for (let i = 0; i < rawData.length; i++) {
    outputArray[i] = rawData.charCodeAt(i);
  }
  return outputArray;
}

// "Recent searches" - a browser-local memory of jobs THIS browser started, so closing the tab
// (the expected thing to do for a notify-enabled search - that's the whole point of the feature)
// doesn't strand the user with no way back to it. There's no account system, so this is
// deliberately client-side only (localStorage, same pattern as app.js's colorOverrides) - a hash-
// backed entry (notify-enabled, or any job someone's since clicked Copy Link on) resolves forever
// via the permanent cache; a hash-less entry (a regular job nobody's shared yet) only resolves
// while the in-memory SearchJob is still alive (until it's evicted, ~30 min after it finishes) -
// resuming one that's gone just removes it from the list rather than erroring forever.
const RECENT_SEARCHES_STORAGE_KEY = "plateup:recentSearches";
const MAX_RECENT_SEARCHES = 15;

function loadRecentSearches() {
  try {
    const raw = localStorage.getItem(RECENT_SEARCHES_STORAGE_KEY);
    return raw ? JSON.parse(raw) : [];
  } catch {
    return [];
  }
}

function saveRecentSearches(entries) {
  try {
    localStorage.setItem(RECENT_SEARCHES_STORAGE_KEY, JSON.stringify(entries));
  } catch {
    // localStorage unavailable (private browsing, storage full, ...) - the list just won't
    // persist across reloads; still works for the current page view.
  }
}

// Called right after a search starts (both notify and regular jobs - a regular job closed early
// is still better recoverable-by-jobId for ~30 minutes than not tracked at all). Stores the full
// payload (not just setting/dishName) so a resumed entry can also prefill the form, same as
// loadSearchLink's "live" branch does for a hash-backed link.
function recordRecentSearch(jobId, hash, payload) {
  const entries = loadRecentSearches();
  entries.unshift({ jobId, hash, payload, startedAt: new Date().toISOString() });
  saveRecentSearches(entries.slice(0, MAX_RECENT_SEARCHES));
  renderRecentSearches();
}

// Called once a hash-less entry gains a hash later (the user clicked Copy Link after the fact) -
// upgrades it in place so it becomes permanently recoverable instead of only until eviction.
function attachHashToRecentSearch(jobId, hash) {
  const entries = loadRecentSearches();
  const entry = entries.find((e) => e.jobId === jobId);
  if (entry) {
    entry.hash = hash;
    saveRecentSearches(entries);
    renderRecentSearches();
  }
}

function removeRecentSearch(jobId) {
  saveRecentSearches(loadRecentSearches().filter((e) => e.jobId !== jobId));
  renderRecentSearches();
}

function formatRelativeTime(isoString) {
  const seconds = Math.max(0, Math.round((Date.now() - new Date(isoString).getTime()) / 1000));
  if (seconds < 60) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

// Resumes a hash-less entry by jobId - only works while the in-memory SearchJob is still alive.
// A 404 (evicted, or server restarted since) prunes the entry rather than leaving a dead link
// around forever.
async function resumeRecentSearch(jobId, payload) {
  try {
    const response = await fetch(`/api/search/${encodeURIComponent(jobId)}`);
    if (!response.ok) {
      setStatus("That search is no longer available on the server (it finished too long ago).", true);
      removeRecentSearch(jobId);
      return;
    }

    await prefillFormFromPayload(payload);
    currentSearchSetting = payload.setting;
    currentSearchPayload = payload;
    currentJobId = jobId;
    currentShareHash = null;
    resetPreviewCache();
    jobEl.hidden = false;
    matchesList.innerHTML = "";
    stopPolling();
    pollHandle = setInterval(() => pollJob(currentJobId), 1000);
    await pollJob(currentJobId);
  } catch {
    setStatus("Failed to reach the server.", true);
  }
}

function renderRecentSearches() {
  const entries = loadRecentSearches();
  recentSearchesSection.hidden = entries.length === 0;
  recentSearchesList.innerHTML = "";

  for (const entry of entries) {
    const li = document.createElement("li");
    const label = `${entry.payload?.dishName ?? "?"} / ${entry.payload?.setting ?? "?"} - started ${formatRelativeTime(entry.startedAt)}`;

    if (entry.hash) {
      const link = document.createElement("a");
      link.className = "link-button";
      link.href = `/search/cached#${entry.hash}`;
      link.textContent = label;
      li.appendChild(link);
    } else {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "link-button";
      button.textContent = label;
      button.addEventListener("click", () => resumeRecentSearch(entry.jobId, entry.payload));
      li.appendChild(button);
    }

    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.className = "link-button";
    removeButton.textContent = "Remove";
    removeButton.title = "Remove from this list (doesn't cancel the search)";
    removeButton.addEventListener("click", () => removeRecentSearch(entry.jobId));
    li.append(" ", removeButton);

    recentSearchesList.appendChild(li);
  }
}

// Per-row refresh state (debounce timer + a generation counter to drop stale out-of-order
// responses), keyed by the row's <div class="dynamic-row"> element - see refreshDayTargetRow/
// refreshFranchiseRow. A WeakMap means rows removed from the DOM are naturally garbage-collected
// along with their state, no manual cleanup needed.
const rowState = new WeakMap();

function getRowState(row) {
  let state = rowState.get(row);
  if (!state) {
    state = { generation: 0, timer: null };
    rowState.set(row, state);
  }
  return state;
}

function setStatus(message, isError) {
  statusEl.textContent = message;
  statusEl.classList.toggle("error", Boolean(isError));
}

// Builds a text input backed by a <datalist> (rather than a <select>) so it supports typing, not
// just clicking through a closed list - each row gets its own <datalist> since candidate lists
// differ per row/day. Repopulating a row's candidates only ever rebuilds the <datalist>'s options,
// never touches the <input>'s own .value - see refreshDayTargetRow/refreshFranchiseRow for why
// that matters (a stale/no-longer-reachable selection simply stays typed rather than being
// silently cleared).
function createCardInput(className) {
  const input = document.createElement("input");
  input.type = "text";
  input.className = className;
  input.autocomplete = "off";
  input.placeholder = "Card name";
  const datalistId = `card-datalist-${datalistCounter++}`;
  input.setAttribute("list", datalistId);

  const datalist = document.createElement("datalist");
  datalist.id = datalistId;

  input.addEventListener("blur", () => validateCardInput(input));

  return { input, datalist };
}

function setDatalistOptions(datalist, names) {
  datalist.innerHTML = "";
  for (const name of names) {
    const option = document.createElement("option");
    option.value = name;
    datalist.appendChild(option);
  }
}

// Unlike setDatalistOptions (a <datalist> backing a free-text <input>), this populates a real
// <select> - so an already-chosen value that's still a valid option must be preserved rather than
// reset to nothing on every repopulate (a free-text input never has this problem, since typed text
// isn't cleared by changing the datalist). A value staged before options existed yet (see
// addDayTargetRow's prefill-ordering note) is applied here once options actually exist, then cleared.
function setClassSelectOptions(select, classes) {
  const previousValue = select.dataset.pendingClassValue ?? select.value;
  select.innerHTML = "";

  const placeholder = document.createElement("option");
  placeholder.value = "";
  placeholder.textContent = classes.length === 0 ? "No classes reachable" : "Choose a class";
  select.appendChild(placeholder);

  for (const cls of classes) {
    const option = document.createElement("option");
    option.value = String(cls.dishType);
    option.textContent = cls.displayName;
    select.appendChild(option);
  }

  if (previousValue && [...select.options].some((o) => o.value === previousValue)) {
    select.value = previousValue;
  }

  delete select.dataset.pendingClassValue;
}

// <select>-flavored sibling of validateCardInput - invalid if class mode is active and nothing
// valid is currently selected (covers both "never chose one" and "a previously-valid class stopped
// being reachable after an earlier row's card changed").
function validateClassInput(select) {
  if (select.disabled) {
    select.setCustomValidity("");
    return true;
  }

  if (!select.value) {
    select.setCustomValidity("Choose a dish class.");
    return false;
  }

  select.setCustomValidity("");
  return true;
}

// Enforces the user's decision: a typed value that isn't one of the row's current valid options
// blocks submit with a message (via the input's native validity state), rather than silently
// reverting to the last valid value.
function validateCardInput(input) {
  const datalist = document.getElementById(input.getAttribute("list"));
  const options = datalist ? [...datalist.options].map((o) => o.value) : [];
  const value = input.value.trim();

  if (value === "" && !input.required) {
    input.setCustomValidity("");
    return true;
  }

  if (!options.includes(value)) {
    input.setCustomValidity(value === "" ? "Choose a card." : `"${value}" isn't a valid card for this row - pick one from the list.`);
    return false;
  }

  input.setCustomValidity("");
  return true;
}

function getRowDay(row) {
  const day = Number(row.querySelector(".day-target-day").value);
  return Number.isInteger(day) ? day : null;
}

// A row toggled "any day" (see addDayTargetRow) only needs its card obtained by day 15, order/day
// unspecified - see SeedSearchRequest.FlexibleTargets. Its day INPUT is ignored/cleared while
// this is checked.
function isRowFlexible(row) {
  return row.querySelector(".day-target-any-day")?.checked ?? false;
}

// "card" (default) = an exact card name via the free-text/datalist input; "class" = any card of a
// dish category via the select populated by setClassSelectOptions - see addDayTargetRow's mode
// toggle. Mutually exclusive by construction (only one of the two controls is ever enabled/visible
// per row), matching DayTargetDto's CardName/DishType shape.
function getRowMode(row) {
  return row.querySelector(".day-target-mode")?.value ?? "card";
}

// The day value that actually governs matching/refresh for this row - null for a flexible row
// (always) or a fixed row that hasn't been given a valid day yet.
function getEffectiveDay(row) {
  return isRowFlexible(row) ? null : getRowDay(row);
}

function getAllDayTargetRows() {
  return [...dayTargetRowsEl.querySelectorAll(".dynamic-row")];
}

function getAllFranchiseRows() {
  return [...franchiseCardRowsEl.querySelectorAll(".dynamic-row")];
}

// Client-side mirror of UnlockPackEvaluator.HasBlockedByConflict - flags a pair of chosen
// day-target cards (fixed or flexible, same as the server's target set) that can never coexist
// because owning one permanently prevents the other from ever being offered again. Purely a UX
// nicety (immediate feedback instead of waiting on a submit round-trip); StartSearch's own check
// is the authoritative backstop for non-JS callers. Re-validates every day-target row's base
// validity first (via validateCardInput) so a resolved conflict doesn't leave a stale message
// behind, then overlays the conflict message on top where one still applies.
function refreshBlockedByValidation() {
  // Class-mode rows are excluded here entirely - there's no single unlock to look up (any card of
  // the class could satisfy it), so they can't participate in a pairwise BlockedBy check. See
  // TargetMatcher.cs / UnlockPackEvaluator.HasBlockedByConflict's scope on the server side.
  const entries = getAllDayTargetRows()
    .filter((row) => getRowMode(row) === "card")
    .map((row) => {
      const input = row.querySelector(".day-target-card");
      return { input, unlock: unlocksForSetting.find((u) => u.name === input.value) };
    });

  for (const { input } of entries) {
    validateCardInput(input);
  }

  for (const a of entries) {
    if (!a.unlock) continue;
    for (const b of entries) {
      if (a === b || !b.unlock) continue;
      if (a.unlock.blockedByIds.includes(b.unlock.id)) {
        a.input.setCustomValidity(`"${a.unlock.name}" and "${b.unlock.name}" can never coexist (BlockedBy) - remove one of them.`);
      }
    }
  }
}

function getFranchiseCardNames() {
  return getAllFranchiseRows().map((row) => row.querySelector(".franchise-card").value).filter(Boolean);
}

function getDayTargetCardNames() {
  return getAllDayTargetRows().map((row) => row.querySelector(".day-target-card").value).filter(Boolean);
}

function addDayTargetRow() {
  const row = document.createElement("div");
  row.className = "dynamic-row";

  const dayInput = document.createElement("input");
  dayInput.type = "number";
  dayInput.min = "0"; // 0 = the prep day before day 1 (santa-workshop's Christmas Special Card)
  dayInput.step = "1";
  dayInput.placeholder = "Day";
  dayInput.className = "day-target-day";
  dayInput.required = true;

  // "Specific card" (default) vs "Any of class" - swaps which of cardInput/classSelect below is
  // the row's active target control. A separate explicit toggle rather than overloading cardInput
  // with sentinel values like "Any Side", so DayTargetDto's CardName/DishType stay a real
  // structural choice instead of string-parsing a magic value.
  const modeSelect = document.createElement("select");
  modeSelect.className = "day-target-mode";
  const cardModeOption = document.createElement("option");
  cardModeOption.value = "card";
  cardModeOption.textContent = "Specific card";
  const classModeOption = document.createElement("option");
  classModeOption.value = "class";
  classModeOption.textContent = "Any of class";
  modeSelect.appendChild(cardModeOption);
  modeSelect.appendChild(classModeOption);

  // Starts empty and disabled - only populated (via /api/search/possible-cards) once this row has
  // a valid day, and only with cards actually reachable that day given the dish/heat/earlier picks.
  const { input: cardInput, datalist: cardDatalist } = createCardInput("day-target-card");
  cardInput.disabled = true;
  cardInput.required = true;

  // Class mode's equivalent of cardInput - a real <select> (not a datalist-backed input, since the
  // option set is small and closed) populated the same way, only with reachable dish classes
  // instead of reachable card names. Starts hidden/disabled/not-required to match the default
  // "card" mode - see the modeSelect change handler below for how these two controls trade places.
  const classSelect = document.createElement("select");
  classSelect.className = "day-target-class";
  classSelect.hidden = true;
  classSelect.disabled = true;
  classSelect.required = true;
  classSelect.addEventListener("blur", () => validateClassInput(classSelect));
  classSelect.addEventListener("change", () => {
    validateClassInput(classSelect);
    refreshBlockedByValidation();
  });

  modeSelect.addEventListener("change", () => {
    const cardMode = modeSelect.value === "card";
    cardInput.hidden = !cardMode;
    cardInput.required = cardMode;
    classSelect.hidden = cardMode;
    classSelect.required = !cardMode;
    if (cardMode) {
      classSelect.value = "";
      classSelect.setCustomValidity("");
    } else {
      cardInput.value = "";
      cardInput.setCustomValidity("");
    }

    // A mode switch changes what this row actually needs (a specific card's reachability vs a
    // class's), AND what it contributes as an "owned prior" to every later row/the franchise
    // dropdown (a class-mode row contributes nothing there - see refreshDayTargetRow/
    // getDayTargetCardNames) - refresh this row and everything after it, same threshold policy
    // cardInput's own change handler below uses for a fixed row.
    if (isRowFlexible(row)) {
      scheduleAllDayTargetRefresh();
    } else {
      scheduleAffectedDayTargetRefresh(getRowDay(row) ?? 0);
    }
    scheduleAllFranchiseRefresh();
  });

  // Per-card toggle (not a global mode): this row's card just needs to be obtained by day 15, in
  // any order relative to the rest of the list - mixed freely with pinned-day rows in one search.
  const anyDayLabel = document.createElement("label");
  anyDayLabel.className = "any-day-toggle";
  const anyDayCheckbox = document.createElement("input");
  anyDayCheckbox.type = "checkbox";
  anyDayCheckbox.className = "day-target-any-day";
  anyDayLabel.appendChild(anyDayCheckbox);
  anyDayLabel.appendChild(document.createTextNode("Any day (by day 15)"));

  const removeButton = document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "link-button";
  removeButton.textContent = "Remove";
  removeButton.addEventListener("click", () => {
    // A flexible row being removed can't be scoped to a threshold (it isn't tied to a day), so
    // fall back to refreshing every remaining row.
    const refreshThreshold = isRowFlexible(row) ? 0 : (getEffectiveDay(row) ?? 1);
    row.remove();
    scheduleAffectedDayTargetRefresh(refreshThreshold);
    scheduleAllFranchiseRefresh(); // removing a picked card can free up the Theme/tier caps
  });

  anyDayCheckbox.addEventListener("change", () => {
    const flexible = anyDayCheckbox.checked;
    dayInput.disabled = flexible;
    dayInput.required = !flexible;
    dayInput.hidden = flexible;
    if (flexible) {
      dayInput.value = "";
      dayInput.dataset.prevDay = "";
    }
    // Toggling changes both what this row needs (day vs. no day) and what counts as "prior" for
    // every other row, so the simplest correct thing is to refresh the whole section rather than
    // try to compute a narrower threshold.
    scheduleAllDayTargetRefresh();
    scheduleAllFranchiseRefresh();
  });

  // Editing this row's day only changes what's reachable for rows whose day is >= the smaller of
  // the old/new day (this row included, since which OTHER rows count as "prior" for it may have
  // changed too) - rows entirely before that threshold are unaffected.
  dayInput.addEventListener("input", () => {
    const previousDay = dayInput.dataset.prevDay ? Number(dayInput.dataset.prevDay) : null;
    const newDay = getRowDay(row);
    const threshold = previousDay !== null && newDay !== null ? Math.min(previousDay, newDay) : (newDay ?? 1);
    dayInput.dataset.prevDay = newDay === null ? "" : String(newDay);
    scheduleAffectedDayTargetRefresh(threshold);
  });

  // Changing this row's card only changes what's reachable for rows with a LATER day (or every
  // row, if this one is flexible - it could end up owned at any point) - and can change the
  // Theme/franchise-tier caps, so franchise rows need a recheck too.
  cardInput.addEventListener("change", () => {
    validateCardInput(cardInput);
    refreshBlockedByValidation();
    if (isRowFlexible(row)) {
      scheduleAllDayTargetRefresh();
    } else {
      const day = getRowDay(row) ?? 0;
      scheduleAffectedDayTargetRefresh(day + 1);
    }
    scheduleAllFranchiseRefresh();
  });

  row.appendChild(dayInput);
  row.appendChild(anyDayLabel);
  row.appendChild(modeSelect);
  row.appendChild(cardInput);
  row.appendChild(cardDatalist);
  row.appendChild(classSelect);
  row.appendChild(removeButton);
  dayTargetRowsEl.appendChild(row);
  scheduleRowRefresh(row);
}

function addFranchiseCardRow() {
  const row = document.createElement("div");
  row.className = "dynamic-row";

  const { input: cardInput, datalist: cardDatalist } = createCardInput("franchise-card");

  const removeButton = document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "link-button";
  removeButton.textContent = "Remove";
  removeButton.addEventListener("click", () => {
    row.remove();
    scheduleAffectedDayTargetRefresh(1); // a franchise card is owned from day 0 - affects every row
    scheduleAllFranchiseRefresh();
  });

  cardInput.addEventListener("change", () => {
    validateCardInput(cardInput);
    scheduleAffectedDayTargetRefresh(1);
    scheduleAllFranchiseRefresh();
  });

  row.appendChild(cardInput);
  row.appendChild(cardDatalist);
  row.appendChild(removeButton);
  franchiseCardRowsEl.appendChild(row);
  scheduleFranchiseRowRefresh(row);
}

addDayTargetButton.addEventListener("click", () => {
  addDayTargetRow();
  setDayTargetsEnabled(Boolean(dishSelect.value));
});

// A fresh page load always seeds one empty day-target row (see loadInit) - its day/card inputs are
// `required` so a genuine card-target search can't be submitted half-filled-in. But that same
// `required` blocks the native submit event entirely (before any JS validation runs) for an
// OT-Friendly-only (or map-size-only) search, where the user never touches that row at all. Checking
// OT Friendly or a map size relaxes `required` on any row that's still completely empty at that
// moment - a row the user has actually started filling in keeps being validated normally either way,
// since a non-empty field satisfies `required` regardless of its boolean value.
function relaxEmptyDayTargetRows() {
  for (const row of getAllDayTargetRows()) {
    const dayInput = row.querySelector(".day-target-day");
    const cardInput = row.querySelector(".day-target-card");
    const classSelect = row.querySelector(".day-target-class");
    if (!dayInput.value && !cardInput.value && !classSelect.value) {
      dayInput.required = false;
      cardInput.required = false;
      classSelect.required = false;
      // validateCardInput/validateClassInput may have already run while still required, leaving a
      // stale setCustomValidity message - required alone doesn't clear that, re-run it now so an
      // untouched row's active field is genuinely valid, not just not-required.
      validateCardInput(cardInput);
      validateClassInput(classSelect);
    }
  }
}

otFriendlyInput.addEventListener("change", () => {
  if (otFriendlyInput.checked) {
    relaxEmptyDayTargetRows();
  }
});

for (const checkbox of mapSizeCheckboxes) {
  checkbox.addEventListener("change", () => {
    if (checkbox.checked) {
      relaxEmptyDayTargetRows();
    }
  });
}

achievementPresetSelect.addEventListener("change", () => {
  if (achievementPresetSelect.value) {
    relaxEmptyDayTargetRows();
  }

  const option = achievementPresetSelect.selectedOptions[0];
  const explanationText = option?.dataset.explanation || "Select a preset above to see what it checks.";
  achievementPresetExplanation.innerHTML = "";
  const p = document.createElement("p");
  p.textContent = explanationText;
  achievementPresetExplanation.appendChild(p);
});

// Runs the permission+subscribe sequence right on the checkbox's own change event (not deferred to
// form submit) specifically to stay inside the same user-gesture window Notification.requestPermission
// relies on in some browsers. Un-checking (or any failure below) clears currentPushSubscription, so
// buildSearchPayload's notifyOnCompletion correctly collapses back to false - a checked box with no
// working subscription behind it must never claim notify-mode (see StartSearch's own
// IsConfigured-and-subscribed gate; this is just the client-side mirror of that same rule).
notifyOnCompletionInput?.addEventListener("change", async () => {
  if (!notifyOnCompletionInput.checked) {
    currentPushSubscription = null;
    notifyPermissionWarning.hidden = true;
    return;
  }

  notifyPermissionWarning.hidden = true;
  try {
    const permission = await Notification.requestPermission();
    if (permission !== "granted") {
      notifyOnCompletionInput.checked = false;
      currentPushSubscription = null;
      notifyPermissionWarning.textContent = "Notification permission denied - this search will run normally and stop if you close this tab.";
      notifyPermissionWarning.hidden = false;
      return;
    }

    const registration = await navigator.serviceWorker.ready;
    currentPushSubscription = await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: urlBase64ToUint8Array(vapidPublicKey),
    });
  } catch {
    notifyOnCompletionInput.checked = false;
    currentPushSubscription = null;
    notifyPermissionWarning.textContent = "Couldn't set up notifications in this browser - this search will run normally and stop if you close this tab.";
    notifyPermissionWarning.hidden = false;
  }
});

addFranchiseCardButton.addEventListener("click", () => {
  addFranchiseCardRow();
  scheduleAffectedDayTargetRefresh(1); // a newly-owned franchise card can affect any day's possible cards
});

function setDayTargetsEnabled(enabled) {
  addDayTargetButton.disabled = !enabled;
  for (const el of dayTargetRowsEl.querySelectorAll("input, button")) {
    el.disabled = !enabled;
  }
}

function scheduleRowRefresh(row) {
  const state = getRowState(row);
  clearTimeout(state.timer);
  state.timer = setTimeout(() => refreshDayTargetRow(row), 300);
}

function scheduleFranchiseRowRefresh(row) {
  const state = getRowState(row);
  clearTimeout(state.timer);
  state.timer = setTimeout(() => refreshFranchiseRow(row), 300);
}

// Only reschedules the rows actually affected (day >= threshold, or not yet given a day at all),
// each on its own debounce timer - not a single shared timer that rebuilds every row on any edit
// anywhere in the form.
function scheduleAffectedDayTargetRefresh(threshold) {
  for (const row of getAllDayTargetRows()) {
    const day = getEffectiveDay(row);
    if (day === null || day >= threshold) {
      scheduleRowRefresh(row);
    }
  }
}

function scheduleAllDayTargetRefresh() {
  scheduleAffectedDayTargetRefresh(1);
}

function scheduleAllFranchiseRefresh() {
  for (const row of getAllFranchiseRows()) {
    scheduleFranchiseRowRefresh(row);
  }
}

// Returns both reachable card names (for the "Specific card" datalist) and reachable dish classes
// (for the "Any of class" select) in one round-trip - both are projections of the same server-side
// possibleIds set, so a second independent endpoint/fetch per row would just add a duplicate
// debounce/generation race for no benefit. See PossibleCardsResponse/SearchEndpoints.GetPossibleCards.
async function fetchPossibleCardsAndClasses(day, priorTargets, franchiseCardNames) {
  const checkedSubOption = subOptionField.querySelector("input[name='sub-option']:checked");
  try {
    const response = await fetch("/api/search/possible-cards", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        setting: settingSelect.value,
        dishName: dishSelect.value,
        subOptionChoice: subOptionField.hidden ? null : Number(checkedSubOption?.value ?? 1),
        heat: Number(heatSelect.value),
        tier: Number(tierInput.value),
        franchiseCardNames,
        priorTargets,
        day,
        cardPack: cardPackSelect.value || null,
      }),
    });
    if (!response.ok) {
      return { cardNames: [], classes: [] };
    }
    const body = await response.json();
    return { cardNames: body.cardNames, classes: body.classes };
  } catch {
    return { cardNames: [], classes: [] };
  }
}

async function fetchFranchiseCandidates(otherSelectedCardNames) {
  const checkedSubOption = subOptionField.querySelector("input[name='sub-option']:checked");
  try {
    const response = await fetch("/api/search/franchise-candidates", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        setting: settingSelect.value,
        dishName: dishSelect.value,
        subOptionChoice: subOptionField.hidden ? null : Number(checkedSubOption?.value ?? 1),
        heat: Number(heatSelect.value),
        tier: Number(tierInput.value),
        otherSelectedCardNames,
        cardPack: cardPackSelect.value || null,
      }),
    });
    if (!response.ok) {
      return [];
    }
    const body = await response.json();
    return body.cardNames;
  } catch {
    return [];
  }
}

async function refreshDayTargetRow(row) {
  const mode = getRowMode(row);
  const cardInput = row.querySelector(".day-target-card");
  const datalist = document.getElementById(cardInput.getAttribute("list"));
  const classSelect = row.querySelector(".day-target-class");
  const activeControl = mode === "class" ? classSelect : cardInput;
  const inactiveControl = mode === "class" ? cardInput : classSelect;
  const flexible = isRowFlexible(row);

  // The inactive control (whichever of card/class this row isn't currently using) is never a
  // validation candidate and never populated - always disabled, regardless of dish/day state. This
  // is the *inactive* side being disabled, not the session-8 "disabling the active field breaks
  // native validation" bug (see cardInput's own comment below) - it's genuinely not this row's target.
  inactiveControl.disabled = true;

  if (!dishSelect.value) {
    // Nothing to type/select against yet, but this doesn't hide anything from validation - the
    // whole day-targets section is disabled in this state via setDayTargetsEnabled anyway, and the
    // form is already correctly blocked by dish-select's own separate `required` attribute regardless.
    activeControl.disabled = true;
    if (mode === "class") {
      setClassSelectOptions(classSelect, []);
    } else {
      setDatalistOptions(datalist, []);
    }
    return;
  }

  // Never disable the ACTIVE control once a dish is chosen, for any reason (invalid/empty day,
  // zero candidates, etc.) - a disabled field is invisible to native form validation entirely
  // (setCustomValidity/reportValidity/required are all no-ops on it), which was letting the form
  // submit despite an incomplete/invalid row in exactly the state that should have blocked it.
  // validateCardInput/validateClassInput's :invalid styling communicates the problem instead, and
  // stays effective.
  activeControl.disabled = false;

  // A flexible row has no day to validate - it's always queryable once a dish is chosen.
  const day = flexible ? null : getRowDay(row);
  if (!flexible && (day === null || day < 0)) {
    if (mode === "class") {
      setClassSelectOptions(classSelect, []);
      validateClassInput(classSelect);
    } else {
      setDatalistOptions(datalist, []);
      validateCardInput(cardInput);
    }
    return;
  }

  const state = getRowState(row);
  const myGeneration = ++state.generation;

  // A flexible prior is always included (it could end up owned at any point); a fixed prior is
  // only included if it's strictly before this row's own day, OR this row itself is flexible (no
  // day to be "before" - conservatively treat every fixed prior as possibly-already-owned too). A
  // class-mode prior contributes nothing (empty cardName here, filtered out below) - we don't know
  // which concrete card it'll resolve to, so it can't sharpen a later row's reachability, mirroring
  // GetPossibleCards' own PriorTargets handling server-side.
  const priorTargets = getAllDayTargetRows()
    .filter((other) => other !== row)
    .map((other) => ({
      day: getEffectiveDay(other),
      cardName: getRowMode(other) === "class" ? "" : other.querySelector(".day-target-card").value,
      isFlexible: isRowFlexible(other),
    }))
    .filter((t) => t.cardName && (t.isFlexible || (t.day !== null && (day === null || t.day < day))))
    .map((t) => ({ day: t.day, cardName: t.cardName }));

  const { cardNames, classes } = await fetchPossibleCardsAndClasses(day, priorTargets, getFranchiseCardNames());

  if (state.generation !== myGeneration) {
    return; // a newer request for this row superseded this one - drop the stale result
  }

  if (mode === "class") {
    setClassSelectOptions(classSelect, classes);
    validateClassInput(classSelect);
  } else {
    setDatalistOptions(datalist, cardNames);
    validateCardInput(cardInput);
  }

  // This async refresh's own validate call above can overwrite a BlockedBy message
  // refreshBlockedByValidation previously set on this same input (e.g. a flexible row's change
  // event re-refreshes every other row on a debounce) - reapply it so the more specific message
  // survives instead of silently reverting to the generic "not a valid card" one.
  refreshBlockedByValidation();
}

async function refreshFranchiseRow(row) {
  if (!dishSelect.value) {
    return;
  }

  const cardInput = row.querySelector(".franchise-card");
  const datalist = document.getElementById(cardInput.getAttribute("list"));
  const state = getRowState(row);
  const myGeneration = ++state.generation;

  const otherSelectedCardNames = [
    ...getAllFranchiseRows().filter((other) => other !== row).map((other) => other.querySelector(".franchise-card").value),
    ...getDayTargetCardNames(),
  ].filter(Boolean);

  const cardNames = await fetchFranchiseCandidates(otherSelectedCardNames);

  if (state.generation !== myGeneration) {
    return;
  }

  setDatalistOptions(datalist, cardNames);
  validateCardInput(cardInput);
}

function updateSubOptionUi() {
  const dish = unlocksForSetting.find((u) => u.name === dishSelect.value);
  if (dish && dish.hasSubOptions) {
    subOption1Label.textContent = dish.optionCard1Name ?? "Option 1";
    subOption2Label.textContent = dish.optionCard2Name ?? "Option 2";
    subOptionField.hidden = false;
    const firstRadio = subOptionField.querySelector('input[value="1"]');
    if (firstRadio) firstRadio.checked = true;
  } else {
    subOptionField.hidden = true;
  }
}

async function loadUnlocksForSetting(setting) {
  const response = await fetch(`/api/search/unlocks?setting=${encodeURIComponent(setting)}`);
  const unlocks = await response.json();
  unlocksForSetting = unlocks;

  dishSelect.innerHTML = "";
  for (const unlock of unlocks.filter((u) => u.isStartingDish)) {
    const option = document.createElement("option");
    option.value = unlock.name;
    option.textContent = unlock.name;
    dishSelect.appendChild(option);
  }

  onDishChanged();
}

// Single entry point for anything that can change which cards are reachable/relevant for the WHOLE
// form: dish, heat, tier. Updates the sub-option UI, the day-targets section's enabled state (per
// user request: unusable before a dish is chosen), and re-runs every row's possibility query - this
// is the one legitimate "refresh everything" case, since a dish/heat/tier change invalidates every
// row at once.
function onDishChanged() {
  updateSubOptionUi();
  setDayTargetsEnabled(Boolean(dishSelect.value));
  scheduleAllFranchiseRefresh();
  scheduleAllDayTargetRefresh();
}

// Not setting-dependent (heat/chill cards are the same across every restaurant setting), so this
// is loaded once, not refetched when settingSelect changes.
async function loadHeatLevels() {
  const response = await fetch("/api/search/heat-levels");
  const levels = await response.json();

  heatSelect.innerHTML = "";
  let noneInserted = false;
  const insertNone = () => {
    const option = document.createElement("option");
    option.value = "0";
    option.textContent = "0 — None";
    heatSelect.appendChild(option);
    noneInserted = true;
  };

  // levels is sorted ascending by signed level (Chill 3..1, then Heat 1..15) - insert "None"
  // where 0 would naturally sit in that order rather than always at the front.
  for (const level of levels) {
    if (!noneInserted && level.level >= 0) {
      insertNone();
    }
    const option = document.createElement("option");
    option.value = String(level.level);
    const kind = level.level > 0 ? "Heat" : "Chill";
    option.textContent = `${kind} ${Math.abs(level.level)} — ${level.name}`;
    heatSelect.appendChild(option);
  }
  if (!noneInserted) {
    insertNone();
  }
  heatSelect.value = "0";
}

// Not setting-scoped - a card schedule is a selectable pack independent of restaurant setting (see
// GameDataRepository.ListCardSchedules's doc comment), so this is loaded once like loadHeatLevels.
async function loadCardPacks() {
  const response = await fetch("/api/search/card-packs");
  const packs = await response.json();

  cardPackSelect.innerHTML = "";
  const defaultOption = document.createElement("option");
  defaultOption.value = "";
  defaultOption.textContent = "Normal (use the restaurant setting's own schedule)";
  cardPackSelect.appendChild(defaultOption);

  for (const pack of packs) {
    if (pack.slug === "normal") {
      continue; // already covered by the default option above
    }
    const option = document.createElement("option");
    option.value = pack.slug;
    option.textContent = pack.displayName;
    cardPackSelect.appendChild(option);
  }
}

// Static presets (not setting-scoped) - loaded once like loadHeatLevels/loadCardPacks. Each
// option stashes its explanation text in a data attribute so the "What does this search for?"
// details block can show it without a second round-trip when the selection changes.
async function loadAchievementPresets() {
  const response = await fetch("/api/search/achievement-presets");
  const presets = await response.json();

  for (const preset of presets) {
    const option = document.createElement("option");
    option.value = preset.slug;
    option.textContent = preset.displayName;
    option.dataset.explanation = preset.explanation;
    achievementPresetSelect.appendChild(option);
  }
}

dishSelect.addEventListener("change", onDishChanged);
heatSelect.addEventListener("change", onDishChanged);
tierInput.addEventListener("input", onDishChanged);
cardPackSelect.addEventListener("change", onDishChanged);
// Only the Normal setting draws its layout from the shared size pool - every other setting forces
// its own bespoke layout, where "size" isn't a meaningful concept at all (see
// Engine.Layouts.LayoutSeed.TryGetSize). Same hardcoded `!== "normal"` gating shared.js's
// updateCardPackLock already uses for the same reason, not a new generic mechanism.
function updateMapSizeLock() {
  const locked = settingSelect.value !== "normal";
  mapSizeFieldGroup.classList.toggle("disabled", locked);
  for (const checkbox of mapSizeCheckboxes) {
    checkbox.disabled = locked;
    if (locked) {
      checkbox.checked = false;
    }
  }
}

settingSelect.addEventListener("change", () => {
  updateCardPackLock(settingSelect, cardPackSelect);
  updateMapSizeLock();
  loadUnlocksForSetting(settingSelect.value).catch(() => setStatus("Failed to load cards for this setting.", true));
});

// Feature-detects Push support and checks the server actually has WebPush:* configured - only
// reveals notifyField if both are true, and eagerly registers the service worker (rather than
// waiting for the checkbox) so navigator.serviceWorker.ready resolves instantly later instead of
// racing a fresh install. A thrown SecurityError (insecure non-localhost origin) is treated the
// same as "unsupported" - notifyField just stays hidden, no warning needed since the user never
// saw the option.
async function setUpPushNotifications() {
  pushSupported = "serviceWorker" in navigator && "PushManager" in window;
  if (!pushSupported) {
    return;
  }

  try {
    const response = await fetch("/api/push/public-key");
    const body = await response.json();
    pushConfigured = Boolean(body.configured);
    vapidPublicKey = body.publicKey;
  } catch {
    pushConfigured = false;
  }

  if (!pushConfigured) {
    return;
  }

  try {
    await navigator.serviceWorker.register("/sw.js");
    notifyField.hidden = false;
  } catch {
    // Insecure context or some other registration failure - stay hidden, same as "unsupported".
  }
}

async function loadInit() {
  const settings = await fetchSettings();
  populateSettingSelect(settingSelect, settings);
  await loadUnlocksForSetting(settingSelect.value);
  await loadHeatLevels();
  await loadCardPacks();
  await loadAchievementPresets();
  updateCardPackLock(settingSelect, cardPackSelect);
  updateMapSizeLock();
  addDayTargetRow();
  onDishChanged();
  setUpPushNotifications().catch(() => {});
  renderRecentSearches();

  // Saved Searches use a distinct ?saved=<hash> query param, not the #hash fragment /search/
  // cached links use below - they point at a different server-side table (ICacheStore<
  // SavedSearchResult>, 7-day expiry) that GET /api/search/link/{hash} (loadSearchLink's endpoint)
  // never checks, so the two forms can't share a URL shape.
  const savedHash = new URL(location.href).searchParams.get("saved");
  if (savedHash) {
    await loadSavedSearch(savedHash);
    return;
  }

  const hash = location.hash.slice(1);
  if (hash) {
    await loadSearchLink(hash);
  }
}

// Prefills the whole form from a previously-run search's request payload (same shape as
// currentSearchPayload) - used both by loadSearchLink below. Reuses the row-building helpers the
// form already uses (addDayTargetRow/addFranchiseCardRow) rather than hand-rolling row markup here.
async function prefillFormFromPayload(request) {
  settingSelect.value = request.setting;
  await loadUnlocksForSetting(request.setting); // rebuilds dishSelect options, calls onDishChanged()

  dishSelect.value = request.dishName;
  updateSubOptionUi();
  if (request.subOptionChoice) {
    const radio = subOptionField.querySelector(`input[value="${request.subOptionChoice}"]`);
    if (radio) radio.checked = true;
  }
  heatSelect.value = String(request.heat);
  tierInput.value = String(request.tier);
  updateCardPackLock(settingSelect, cardPackSelect);
  if (!cardPackSelect.disabled) {
    cardPackSelect.value = request.cardPack ?? "";
  }
  alphabetInput.value = request.alphabet ?? alphabetInput.value;
  lengthInput.value = String(request.length);
  limitInput.value = request.limit != null ? String(request.limit) : "";
  maxResultsInput.value = request.maxResults != null ? String(request.maxResults) : "500";
  stopAfterFirstMatchInput.checked = Boolean(request.stopAfterFirstMatch);
  // Inert - just reflects the prior request's choice, no subscribe side-effect. A genuinely NEW
  // notify-enabled search still requires the user to actually check this box themselves (a fresh
  // user gesture is needed for Notification.requestPermission/pushManager.subscribe anyway).
  if (notifyOnCompletionInput) {
    notifyOnCompletionInput.checked = Boolean(request.notifyOnCompletion);
  }
  otFriendlyInput.checked = Boolean(request.otFriendly);
  allowRerollInput.checked = Boolean(request.allowRerollPerDay);
  achievementPresetSelect.value = request.achievementPreset ?? "";
  achievementPresetSelect.dispatchEvent(new Event("change"));
  updateMapSizeLock();
  if (!mapSizeFieldGroup.classList.contains("disabled")) {
    const requestedSizes = new Set(request.mapSizes ?? []);
    for (const checkbox of mapSizeCheckboxes) {
      checkbox.checked = requestedSizes.has(checkbox.value);
    }
  }
  excludeSeedsInput.value = (request.excludedSeeds ?? []).join(", ");

  franchiseCardRowsEl.innerHTML = "";
  for (const name of request.franchiseCardNames ?? []) {
    addFranchiseCardRow();
    const rows = getAllFranchiseRows();
    rows[rows.length - 1].querySelector(".franchise-card").value = name;
  }

  dayTargetRowsEl.innerHTML = "";
  for (const target of request.dayTargets ?? []) {
    addDayTargetRow();
    const row = getAllDayTargetRows().at(-1);
    if (target.day === null || target.day === undefined) {
      const anyDayCheckbox = row.querySelector(".day-target-any-day");
      anyDayCheckbox.checked = true;
      anyDayCheckbox.dispatchEvent(new Event("change"));
    } else {
      row.querySelector(".day-target-day").value = String(target.day);
    }

    if (target.dishType !== null && target.dishType !== undefined) {
      const modeSelect = row.querySelector(".day-target-mode");
      modeSelect.value = "class";
      modeSelect.dispatchEvent(new Event("change"));
      // Options don't exist yet (populated async below via onDishChanged's refresh cascade) - stash
      // the value for setClassSelectOptions to apply once they do, same pattern as a <select> can't
      // otherwise support (unlike cardInput's free-text/datalist pair, which has no such ordering
      // hazard - see createCardInput's doc comment).
      row.querySelector(".day-target-class").dataset.pendingClassValue = String(target.dishType);
    } else {
      row.querySelector(".day-target-card").value = target.cardName;
    }
  }

  // Re-runs possibility queries for every row now that the setup + rows exist - this is what
  // actually enables the (initially-disabled) card inputs and populates their datalists with the
  // real candidates, including the prefilled names above, so validateCardInput accepts them.
  onDishChanged();
}

// Resolves a /search/cached#hash (or a notify-enabled job's own eagerly-minted link, same URL
// shape) via GET /api/search/link/{hash} - which checks the permanent cache first, then falls
// back to a still-running/just-finished in-memory job. Branches on the response's "kind":
// "cached" renders the same permanent-snapshot view this used to always render (hitting
// /api/search/cached/{hash} directly); "live" resumes normal polling against the still-running
// job, exactly as if the user had just submitted it themselves.
async function loadSearchLink(hash) {
  try {
    const response = await fetch(`/api/search/link/${encodeURIComponent(hash)}`);
    if (!response.ok) {
      setStatus("This shared search result doesn't exist.", true);
      return;
    }

    const body = await response.json();

    if (body.kind === "live") {
      await prefillFormFromPayload(body.request);
      currentSearchSetting = body.request.setting;
      currentSearchPayload = body.request;
      currentJobId = body.jobId;
      currentShareHash = hash;
      resetPreviewCache();
      jobEl.hidden = false;
      matchesList.innerHTML = "";
      pollHandle = setInterval(() => pollJob(currentJobId), 1000);
      await pollJob(currentJobId);
      return;
    }

    const cached = body.cached;
    await prefillFormFromPayload(cached.request);

    currentSearchSetting = cached.request.setting;
    currentSearchPayload = cached.request;
    currentJobId = null; // no live job behind this view - Cancel stays hidden
    currentShareHash = hash; // Copy Link (if ever shown again) should reuse this same link
    resetPreviewCache();

    renderJobStatus({
      status: "Completed",
      checkedCount: cached.checkedCount,
      totalToCheck: cached.totalToCheck,
      seedsPerSecond: 0,
      elapsedSeconds: 0,
      matches: cached.matches,
      error: null,
      matchCapReached: cached.matchCapReached,
      timedOut: cached.timedOut,
    });
  } catch {
    setStatus("Failed to reach the server.", true);
  }
}

// Resolves a Saved Search (?saved=<hash>, see favorites.js's saveSearch/drawer): tries the live
// server copy first (GET /api/search/saved/{hash}), upgrading the localStorage entry's snapshot on
// success so it stays viewable after the server-side row expires (7 days). On a 404 - expected
// once that happens - falls back to the one-shot local-snapshot handoff the drawer stashed before
// navigating here (see favorites.js's stashHandoff), if one was ever actually recorded.
async function loadSavedSearch(hash) {
  try {
    const response = await fetch(`/api/search/saved/${encodeURIComponent(hash)}`);
    if (response.ok) {
      const saved = await response.json();
      window.PlateUpFavorites?.upgradeSavedSearchSnapshot(hash, saved);
      await renderSavedSearchView(saved, false);
      return;
    }
  } catch {
    // fall through to the local snapshot below
  }

  const snapshot = window.PlateUpFavorites?.consumeHandoff(`pendingSavedSearchSnapshot:${hash}`);
  if (snapshot) {
    await renderSavedSearchView(snapshot, true);
  } else {
    setStatus(
      "This saved search has expired (saved searches are kept 7 days) and no local snapshot from " +
        "an earlier view was recorded for it.",
      true
    );
  }
}

async function renderSavedSearchView(data, isStale) {
  await prefillFormFromPayload(data.request);
  currentSearchSetting = data.request.setting;
  currentSearchPayload = data.request;
  currentJobId = null; // no live job behind this view - Cancel and Save-via-jobId stay unavailable
  currentShareHash = null; // this hash belongs to the separate Saved-Searches table, not Copy Link's
  resetPreviewCache();

  renderJobStatus({
    status: "Completed",
    checkedCount: data.checkedCount,
    totalToCheck: data.totalToCheck,
    seedsPerSecond: 0,
    elapsedSeconds: 0,
    matches: data.matches,
    error: null,
    matchCapReached: data.matchCapReached,
    timedOut: data.timedOut,
  });

  if (isStale) {
    setStatus(
      "⚠ Showing a local snapshot from when you last viewed this - the server copy has expired " +
        "(saved searches are kept 7 days).",
      true
    );
  }
}

function stopPolling() {
  if (pollHandle !== null) {
    clearInterval(pollHandle);
    pollHandle = null;
  }
}

function renderJobStatus(job) {
  jobEl.hidden = false;
  currentMatches = job.matches;
  // If the preview dialog is open while a search is still finding matches, keep its "N / M"
  // counter and the prev/next disabled states in sync with the growing list.
  if (previewDialog.open) {
    updatePreviewNav();
  }

  const percent = job.totalToCheck > 0 ? Math.min(100, (job.checkedCount / job.totalToCheck) * 100) : 0;

  if (job.status === "Queued") {
    jobStatusText.textContent = "Queued - waiting for a free search slot (other visitors' searches are running)...";
    progressBar.hidden = true;
    jobStats.textContent = "";
    cancelButton.hidden = false;
  } else if (job.status === "Running") {
    jobStatusText.textContent = "Running...";
    progressBar.hidden = false;
    progressBarFill.style.width = `${percent.toFixed(1)}%`;
    jobStats.textContent =
      `${job.checkedCount.toLocaleString()} / ${job.totalToCheck.toLocaleString()} seeds checked ` +
      `(${job.seedsPerSecond.toFixed(0)}/sec, ${job.elapsedSeconds.toFixed(0)}s elapsed)`;
    cancelButton.hidden = false;
  } else {
    progressBar.hidden = job.status !== "Completed" && job.status !== "Cancelled";
    if (!progressBar.hidden) {
      progressBarFill.style.width = `${percent.toFixed(1)}%`;
    }
    cancelButton.hidden = true;

    if (job.status === "Completed" && job.timedOut) {
      jobStatusText.textContent =
        `Stopped after the 1-hour time limit - kept ${job.matches.length} match(es) found so far. ` +
        "The search space wasn't fully checked; narrow it (shorter length, smaller alphabet, more " +
        "specific targets) and search again to cover the rest.";
    } else if (job.status === "Completed" && job.matchCapReached) {
      jobStatusText.textContent =
        `Stopped after finding ${job.matches.length} match(es) - the search hit your "Max results" ` +
        "limit (or 1, with \"Stop after first match\"). Don't like these? Raise Max results (up to " +
        "500), adjust the seed length/alphabet in Advanced options to search a different space, or " +
        "exclude these seeds and search again to find more.";
    } else if (job.status === "Completed") {
      jobStatusText.textContent = `Done - checked ${job.checkedCount.toLocaleString()} seed(s), found ${job.matches.length} match(es).`;
    } else if (job.status === "Cancelled") {
      jobStatusText.textContent = `Cancelled after checking ${job.checkedCount.toLocaleString()} seed(s) - ${job.matches.length} match(es) found so far.`;
    } else if (job.status === "Error") {
      jobStatusText.textContent = `Search failed: ${job.error ?? "unknown error"}`;
    }
    jobStats.textContent = job.elapsedSeconds
      ? `${job.seedsPerSecond.toFixed(0)} seeds/sec, ${job.elapsedSeconds.toFixed(0)}s total`
      : "";
  }

  copyMatchesButton.hidden = job.matches.length === 0;
  // A page reached via /search/cached#hash (no live job behind it - see loadSearchLink) never
  // shows this, regardless of status. For a live job: a notify-enabled job already has its
  // ShareHash from the moment it started (see the submit handler), so the link is offered
  // immediately rather than waiting for Completed like a regular job's lazy mint-on-click does.
  copyLinkButton.hidden = !currentJobId || (!currentShareHash && job.status !== "Completed");
  if (saveSearchButton) {
    saveSearchButton.hidden = job.matches.length === 0 || job.status !== "Completed";
  }

  // Cap how many match badges get rendered as DOM nodes - some target cards are common enough
  // that a search can turn up thousands of matches, and building/reflowing that many elements on
  // every ~1s poll tick is exactly the kind of client-side cost this page is meant to avoid (the
  // search itself already runs server-side for the same reason - see the page intro).
  const MAX_RENDERED_MATCHES = 500;
  matchesList.innerHTML = "";
  for (const match of job.matches.slice(0, MAX_RENDERED_MATCHES)) {
    const li = document.createElement("li");
    li.append(match.seed);
    if (match.otFriendly) {
      const badge = document.createElement("span");
      badge.className = "ot-badge";
      badge.textContent = "OT";
      badge.title = "OT Friendly - Research Desk offered days 1, 2, and 4";
      li.append(" ", badge);
    }
    if (match.mapSize) {
      const badge = document.createElement("span");
      badge.className = "size-badge";
      badge.textContent = match.mapSize;
      badge.title = `Resolved layout size: ${match.mapSize}`;
      li.append(" ", badge);
    }
    if (match.equivalentCount > 0) {
      const badge = document.createElement("span");
      badge.className = "equiv-badge";
      badge.textContent = `+${match.equivalentCount} ≡`;
      badge.title = `${match.equivalentCount} other seed string(s) produce this exact game (same seed hash) - click to see them`;
      li.append(" ", badge);
    }
    const favoriteButton = document.createElement("button");
    favoriteButton.type = "button";
    favoriteButton.className = "favorite-seed-btn";
    favoriteButton.title = "Save this seed to Favorites";
    favoriteButton.textContent = "☆";
    favoriteButton.addEventListener("click", (event) => {
      event.stopPropagation();
      saveMatchAsFavorite(match.seed);
    });
    li.append(" ", favoriteButton);

    li.tabIndex = 0;
    li.addEventListener("click", () => openPreviewDialog(match.seed, match.otFriendly));
    li.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        openPreviewDialog(match.seed, match.otFriendly);
      }
    });
    matchesList.appendChild(li);
  }
  if (job.matches.length > MAX_RENDERED_MATCHES) {
    const li = document.createElement("li");
    li.textContent = `+${(job.matches.length - MAX_RENDERED_MATCHES).toLocaleString()} more`;
    matchesList.appendChild(li);
  }
}

async function pollJob(jobId) {
  try {
    const response = await fetch(`/api/search/${encodeURIComponent(jobId)}`);
    if (!response.ok) {
      stopPolling();
      setStatus("Lost track of the search job.", true);
      return;
    }

    const job = await response.json();
    renderJobStatus(job);

    if (job.status !== "Queued" && job.status !== "Running") {
      stopPolling();
    }
  } catch {
    stopPolling();
    setStatus("Failed to reach the server while polling the search job.", true);
  }
}

cancelButton.addEventListener("click", async () => {
  if (!currentJobId) return;
  try {
    await fetch(`/api/search/${encodeURIComponent(currentJobId)}/cancel`, { method: "POST" });
  } catch {
    setStatus("Failed to cancel the search.", true);
  }
});

copyMatchesButton?.addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(currentMatches.map((m) => m.seed).join("\n"));
    setStatus(`Copied ${currentMatches.length} seed(s) to the clipboard.`, false);
  } catch {
    setStatus("Failed to copy - your browser may not allow clipboard access here.", true);
  }
});

// Lazy by design: nothing is written to the cache DB until this is clicked - a completed search
// that nobody shares never touches /api/search/{jobId}/share at all. Also idempotent per job:
// currentShareHash memoizes the first click's result so repeated clicks (or an accidental
// double-click) just re-copy the same link instead of minting a fresh cache row every time.
copyLinkButton?.addEventListener("click", async () => {
  if (!currentJobId || !currentSearchPayload) return;
  try {
    if (!currentShareHash) {
      const response = await fetch(`/api/search/${encodeURIComponent(currentJobId)}/share`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(currentSearchPayload),
      });
      const body = await response.json();
      if (!response.ok) {
        setStatus(body.error ?? "Failed to create a shareable link.", true);
        return;
      }
      currentShareHash = body.hash;
      attachHashToRecentSearch(currentJobId, currentShareHash);
    }
    const url = `${location.origin}/search/cached#${currentShareHash}`;
    await navigator.clipboard.writeText(url);
    setStatus(`Shareable link copied: ${url}`, false);
  } catch {
    setStatus("Failed to reach the server.", true);
  }
});

// Personal "save this search" action - distinct from Copy Link above, which writes a permanent,
// unnamed snapshot. This always mints its own hash into the separate, 7-day-expiring Saved-
// Searches store (see SearchContracts.cs's SavedSearchResult), regardless of whether Copy Link
// has already been clicked for this job. Works both for a live job (has currentJobId) and a view
// reached via an already-cached/shared link (currentJobId is null but currentShareHash is set).
saveSearchButton?.addEventListener("click", async () => {
  if (!currentSearchPayload || currentMatches.length === 0) return;
  try {
    let hash;
    if (currentJobId) {
      const response = await fetch(`/api/search/${encodeURIComponent(currentJobId)}/save`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(currentSearchPayload),
      });
      const body = await response.json();
      if (!response.ok) {
        setStatus(body.error ?? "Failed to save this search.", true);
        return;
      }
      hash = body.hash;
    } else if (currentShareHash) {
      const response = await fetch(`/api/search/save-from-cached/${encodeURIComponent(currentShareHash)}`, {
        method: "POST",
      });
      const body = await response.json();
      if (!response.ok) {
        setStatus(body.error ?? "Failed to save this search.", true);
        return;
      }
      hash = body.hash;
    } else {
      setStatus("This search can't be saved yet - no shareable link exists for it.", true);
      return;
    }

    window.PlateUpFavorites?.saveSearch({
      hash,
      request: currentSearchPayload,
      matchCount: currentMatches.length,
    });
  } catch {
    setStatus("Failed to reach the server.", true);
  }
});

function parseExcludedSeeds() {
  return (excludeSeedsInput?.value ?? "")
    .split(/[\s,]+/)
    .map((s) => s.trim())
    .filter(Boolean);
}

// Builds the SearchStartRequest-shaped object straight from the DOM - shared by the submit handler
// and the Export Settings button, so there's exactly one place that knows this shape. A row with
// neither a day nor a card entered (the always-present default row from loadInit, possibly left
// untouched for an OT-Friendly-only search) isn't a real target - skip it rather than sending an
// empty cardName the server would reject as an unknown card. Checks the raw .value string, not
// getRowDay (Number("") is 0, a valid day, so that alone can't tell "empty" apart from "day 0" -
// see the Christmas/February day-0 mechanic elsewhere in this file).
function buildSearchPayload() {
  const dayTargets = getAllDayTargetRows()
    .filter((row) => {
      const hasValue = getRowMode(row) === "class"
        ? row.querySelector(".day-target-class").value
        : row.querySelector(".day-target-card").value;
      return row.querySelector(".day-target-day").value || hasValue;
    })
    .map((row) => {
      const mode = getRowMode(row);
      return {
        day: isRowFlexible(row) ? null : Number(row.querySelector(".day-target-day").value),
        cardName: mode === "class" ? null : row.querySelector(".day-target-card").value,
        dishType: mode === "class" ? Number(row.querySelector(".day-target-class").value) : null,
      };
    });

  const checkedSubOption = subOptionField.querySelector("input[name='sub-option']:checked");

  return {
    setting: settingSelect.value,
    dishName: dishSelect.value,
    subOptionChoice: subOptionField.hidden ? null : Number(checkedSubOption?.value ?? 1),
    heat: Number(heatSelect.value),
    tier: Number(tierInput.value),
    franchiseCardNames: getFranchiseCardNames(),
    dayTargets,
    alphabet: alphabetInput.value,
    length: Number(lengthInput.value),
    limit: limitInput.value ? Number(limitInput.value) : null,
    maxResults: maxResultsInput.value ? Number(maxResultsInput.value) : null,
    stopAfterFirstMatch: stopAfterFirstMatchInput.checked,
    excludedSeeds: parseExcludedSeeds(),
    cardPack: cardPackSelect.value || null,
    otFriendly: otFriendlyInput.checked,
    allowRerollPerDay: allowRerollInput.checked,
    mapSizes: mapSizeCheckboxes.filter((cb) => cb.checked).map((cb) => cb.value),
    achievementPreset: achievementPresetSelect.value || null,
    // Collapses to false if the box is checked but subscribing never actually succeeded (denied
    // permission, unsupported browser, etc.) - correctly falls back to today's fully
    // heartbeat-gated behavior with no server-side change needed, since the server never sees
    // this as true (see SearchJobManager.StartSearch's own IsConfigured-and-subscribed gate).
    notifyOnCompletion: Boolean(notifyOnCompletionInput?.checked && currentPushSubscription),
  };
}

// Restores the whole form from an imported/pasted settings object, then clears any stale live-job
// state left over from before the import (same reset a fresh search's submit performs) - an
// imported setup shouldn't leave an old job's results/preview cache on screen mismatched against it.
async function importSearchSettings(payload) {
  if (!payload || typeof payload !== "object" || !payload.setting || !payload.dishName) {
    throw new Error("Missing setting/dish in imported settings.");
  }
  await prefillFormFromPayload(payload);

  stopPolling();
  currentSearchSetting = null;
  currentSearchPayload = null;
  currentJobId = null;
  currentShareHash = null;
  resetPreviewCache();
  jobEl.hidden = true;
  matchesList.innerHTML = "";
  setStatus("Settings imported.", false);
}

// Native constraint validation runs BEFORE the "submit" event even fires, and blocks it entirely
// when a field is invalid - the submit handler below never runs in that case, which is exactly why
// an invalid card field used to "refuse to run with no visible error": the browser's own validation
// bubble is easy to miss and there was no other feedback. "invalid" events fire per-field (capture
// phase, since they don't bubble) whenever that blocked attempt happens, regardless of whether the
// submit handler itself ever runs - use that as the actually-reliable place to surface a status
// message rather than the submit handler's (post-native-validation) unreachable code path.
form.addEventListener(
  "invalid",
  (event) => {
    event.preventDefault(); // suppress the native bubble - our status message is the visible error now
    const invalidCount = [...form.querySelectorAll(":invalid")].length;
    setStatus(
      invalidCount === 1
        ? "One field is invalid - check the highlighted field."
        : `${invalidCount} fields are invalid - check the highlighted fields.`,
      true
    );
  },
  true
);

form.addEventListener("submit", async (event) => {
  event.preventDefault();

  // pollHandle is only non-null while the previous job is still Queued/Running (renderJobStatus's
  // caller stops polling as soon as a terminal status is seen) - if it's still set here, the
  // previous job would otherwise keep running server-side until the heartbeat-timeout sweep
  // reaps it, even though this tab never stopped watching. Cancel it explicitly instead of
  // leaving that to the timeout, since this is a real, deterministic "the user is done with it"
  // signal (not a DOM-lifecycle heuristic).
  if (pollHandle !== null && currentJobId) {
    fetch(`/api/search/${encodeURIComponent(currentJobId)}/cancel`, { method: "POST" }).catch(() => {});
  }
  stopPolling();
  setStatus("", false);

  const dayTargetInputs = getAllDayTargetRows().map((row) =>
    row.querySelector(getRowMode(row) === "class" ? ".day-target-class" : ".day-target-card")
  );
  const franchiseInputs = getAllFranchiseRows().map((row) => row.querySelector(".franchise-card"));

  // Re-validate every card/class field right before submit (not just on blur/change) - blocks
  // submit with the browser's native validation UI if any typed card name or chosen class isn't
  // currently valid for its row. Also set an explicit status message, not just the native bubble -
  // a native validation bubble only ever points at ONE field and is easy to miss/dismiss, which was
  // leaving submit silently refusing to do anything with no visible explanation.
  for (const input of franchiseInputs) {
    validateCardInput(input);
  }
  for (const row of getAllDayTargetRows()) {
    if (getRowMode(row) === "class") {
      validateClassInput(row.querySelector(".day-target-class"));
    }
  }
  refreshBlockedByValidation(); // validates every card-mode day-target input, then overlays any BlockedBy conflict

  let allValid = true;
  let invalidCount = 0;
  for (const input of [...dayTargetInputs, ...franchiseInputs]) {
    if (!input.checkValidity()) {
      allValid = false;
      invalidCount++;
    }
  }
  if (!allValid) {
    setStatus(
      invalidCount === 1
        ? "One card field is invalid for its day - check the highlighted field."
        : `${invalidCount} card fields are invalid for their day - check the highlighted fields.`,
      true
    );
    form.reportValidity();
    return;
  }

  const payload = buildSearchPayload();

  if (payload.dayTargets.length === 0 && !payload.otFriendly && payload.mapSizes.length === 0 && !payload.achievementPreset) {
    setStatus("Add at least one day target (or check OT Friendly / a map size / an achievement preset).", true);
    return;
  }

  try {
    const response = await fetch("/api/search", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    const body = await response.json();
    if (!response.ok) {
      setStatus(body.error ?? "Something went wrong.", true);
      return;
    }

    // Captured now (not read live from settingSelect.value/the form on click) so that clicking a
    // match still previews the right setting/setup even if the user edits the form while this
    // job's results are still on screen.
    currentSearchSetting = payload.setting;
    currentSearchPayload = payload;
    currentJobId = body.jobId;
    // Non-null only for a notify-enabled job (eagerly minted server-side - see
    // SearchJobManager.StartSearch) - a regular job still gets null here and mints its hash lazily
    // on the first Copy Link click, same as before.
    currentShareHash = body.shareHash ?? null;
    resetPreviewCache(); // a new search - prior seeds' cached previews no longer belong to it
    jobEl.hidden = false;
    matchesList.innerHTML = "";
    recordRecentSearch(currentJobId, currentShareHash, payload);

    if (payload.notifyOnCompletion && currentPushSubscription) {
      // PushSubscription.toJSON() nests endpoint/keys.{p256dh,auth} - flatten to match the
      // server's PushSubscriptionDto(Endpoint, P256dh, Auth) shape.
      const subscriptionJson = currentPushSubscription.toJSON();
      fetch(`/api/search/${encodeURIComponent(currentJobId)}/push-subscription`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          endpoint: subscriptionJson.endpoint,
          p256dh: subscriptionJson.keys.p256dh,
          auth: subscriptionJson.keys.auth,
        }),
      }).catch(() => {});
    }

    pollHandle = setInterval(() => pollJob(currentJobId), 1000);
    await pollJob(currentJobId);
  } catch {
    setStatus("Failed to reach the server.", true);
  }
});

// Click-to-preview dialog: reuses the existing layout-preview endpoints (/api/layout/.../
// /preview/....png) already built for the Seed Previewer page - no new backend beyond exposing
// tableApplianceId on LayoutResponse. The image is only ever fetched here, on an explicit open -
// never prefetched for the whole match list, which matters with up to 500 badges on screen.
function openPreviewDialog(seed, otFriendly) {
  if (!currentSearchSetting) {
    return;
  }

  currentPreviewIndex = currentMatches.findIndex((m) => m.seed === seed);
  if (!previewDialog.open) {
    previewDialog.showModal(); // showModal() throws if already open (e.g. a double-click)
  }
  updatePreviewNav();
  populatePreviewDialog(seed, otFriendly);
}

// Steps to a different match without reopening the dialog (showModal() throws on an already-open
// <dialog>). Bounds-checked; ends are also enforced by updatePreviewNav disabling the buttons.
function showPreviewAt(index) {
  if (index < 0 || index >= currentMatches.length) {
    return;
  }
  currentPreviewIndex = index;
  const match = currentMatches[index];
  updatePreviewNav();
  populatePreviewDialog(match.seed, match.otFriendly);
}

function updatePreviewNav() {
  const total = currentMatches.length;
  previewDialogPosition.textContent = total ? `${currentPreviewIndex + 1} / ${total}` : "";
  previewDialogPrev.disabled = currentPreviewIndex <= 0;
  previewDialogNext.disabled = currentPreviewIndex < 0 || currentPreviewIndex >= total - 1;
}

// Fills the (already-open) dialog for one seed. Never calls showModal - that's openPreviewDialog's
// job. Guarded by previewRequestToken so a slow fetch/PNG-render from an earlier selection can't
// land its results on top of a newer one when the user arrows through quickly. On a cache hit the
// whole thing renders synchronously - no "Loading...", no re-fetch, no image/path flicker.
async function populatePreviewDialog(seed, otFriendly) {
  const myToken = ++previewRequestToken;
  const cached = previewCache.get(seed);

  if (cached) {
    touchPreviewLru(seed);
    renderPreviewEntry(cached);
    if (cached.paths) {
      renderPathTabs(seed, cached.paths);
    } else {
      cached.pathsPromise?.then(() => {
        if (myToken === previewRequestToken && cached.paths) {
          renderPathTabs(seed, cached.paths);
        }
      });
    }
    prefetchNeighbours(currentPreviewIndex);
    return;
  }

  // Cache miss - show the loading skeleton (image hidden by CSS #preview-dialog-image:not([src])).
  previewDialogTitle.textContent = `Seed "${seed}"`;
  previewDialogStatus.textContent = "Loading...";
  previewDialogStatus.classList.remove("error");
  previewDialogImage.removeAttribute("src");
  previewDialogImage.removeAttribute("alt");
  previewDialogImageLink.removeAttribute("href");
  previewDialogStats.textContent = "";
  previewDialogEquivalents.hidden = true;
  previewDialogEquivalents.textContent = "";
  previewDialogPathTabs.innerHTML = "";
  previewDialogPathTabs.hidden = true;
  previewDialogPath.innerHTML = "";
  previewDialogExplorerLink.hidden = true;

  const entry = await resolvePreviewEntry(seed, otFriendly);
  if (myToken !== previewRequestToken) {
    return;
  }
  renderPreviewEntry(entry);
  if (entry.paths) {
    renderPathTabs(seed, entry.paths);
  } else {
    entry.pathsPromise?.then(() => {
      if (myToken === previewRequestToken && entry.paths) {
        renderPathTabs(seed, entry.paths);
      }
    });
  }
  prefetchNeighbours(currentPreviewIndex);
}

// Fetches + preloads everything for one seed and stores it in previewCache. No dialog DOM side
// effects, so it's safe to call speculatively (neighbour prefetch). Concurrent calls for the same
// seed share one promise. The card-paths fetch runs detached as entry.pathsPromise so this resolves
// as soon as the image is ready (preserving the "image first, paths fill in" feel).
function resolvePreviewEntry(seed, otFriendly) {
  const hit = previewCache.get(seed);
  if (hit) {
    return Promise.resolve(hit);
  }
  const pending = previewInFlight.get(seed);
  if (pending) {
    return pending;
  }

  // Snapshot - a new search could start (and bump the generation) mid-resolve.
  const setting = currentSearchSetting;
  const payload = currentSearchPayload;
  const gen = previewCacheGeneration;

  const promise = (async () => {
    const entry = {
      seed, otFriendly, pngUrl: null, alt: "", imageHref: null, statsText: "",
      error: null, img: null, paths: null, pathsPromise: null,
    };
    try {
      const response = await fetch(`/api/layout/${encodeURIComponent(setting)}/${encodeURIComponent(seed)}`);
      const layout = await response.json();
      if (!response.ok) {
        entry.error = layout.error ?? "Failed to load preview.";
      } else {
        entry.pngUrl = `/preview/${encodeURIComponent(setting)}/${encodeURIComponent(seed)}.png`;
        entry.imageHref = `/preview/${encodeURIComponent(setting)}/${encodeURIComponent(seed)}`;
        entry.alt = `${layout.settingName} — seed "${layout.seed}"`;
        const tableCount = layout.appliances.filter((a) => a.applianceId === layout.tableApplianceId).length;
        entry.statsText = `${layout.width}×${layout.height} tiles, ${tableCount} dining table(s)`;
        if (otFriendly) {
          entry.statsText += " · OT Friendly ✓";
        }
        // Preloaded (and retained on the entry) so the decoded PNG stays warm - a later revisit
        // sets the visible <img> src from browser + memory cache with no load flash.
        entry.img = await preloadImage(entry.pngUrl);
        entry.pathsPromise = fetchPreviewPaths(seed, payload);
        entry.pathsPromise.then((paths) => { entry.paths = paths; });
      }
    } catch {
      entry.error = "Failed to reach the server.";
    } finally {
      previewInFlight.delete(seed);
    }
    if (gen === previewCacheGeneration) {
      storePreviewEntry(seed, entry);
    }
    return entry;
  })();

  previewInFlight.set(seed, promise);
  return promise;
}

// Shows the full card pick for every drawing day up to the last target, not just the days the
// user explicitly targeted - schedules like "turbo" draw a card every day, so without this a
// sparse-target search (e.g. day 1 and day 10 only) left days 2-9 with no way to know what to
// pick. There's often more than one valid way to fill those in-between days (either offered card
// on a non-target day can be fine), so the server returns up to a few alternate complete paths
// (see SeedSearcher.ResolveCardPaths) - rendered as tabs by renderPathTabs when there's more than one.
async function fetchPreviewPaths(seed, payload) {
  if (!payload) {
    return null;
  }
  try {
    const response = await fetch("/api/search/path", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...payload, seed }),
    });
    if (!response.ok) {
      return null;
    }
    const { paths } = await response.json();
    return paths;
  } catch {
    // Best-effort - the layout preview is the primary content of the dialog, so a failed path
    // fetch just leaves the list empty rather than surfacing a second error state.
    return null;
  }
}

function preloadImage(url) {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => resolve(img); // caller checks img.naturalWidth === 0 for failure
    img.src = url;
  });
}

// Renders the "N other seed strings produce this identical game" line for the currently-shown seed.
// The data lives on the match DTO (currentMatches), not the preview cache entry, since it comes from
// the search result, not the layout endpoint. currentMatches is set for both live and cached-result
// views, so this works in both.
function renderEquivalents(seed) {
  const match = currentMatches.find((m) => m.seed === seed);
  const sample = match?.equivalentSeeds ?? [];
  const count = match?.equivalentCount ?? 0;
  if (!count) {
    previewDialogEquivalents.hidden = true;
    previewDialogEquivalents.textContent = "";
    return;
  }

  previewDialogEquivalents.hidden = false;
  previewDialogEquivalents.innerHTML = "";
  previewDialogEquivalents.append(
    `Identical game (same seed hash) - also produced by ${count} other seed string${count === 1 ? "" : "s"}: `
  );
  sample.forEach((s, i) => {
    const code = document.createElement("code");
    code.textContent = s;
    previewDialogEquivalents.append(code);
    if (i < sample.length - 1) {
      previewDialogEquivalents.append(", ");
    }
  });
  if (count > sample.length) {
    previewDialogEquivalents.append(`, +${count - sample.length} more`);
  }
}

// Pure DOM write for one resolved entry - no awaits, safe to call synchronously on a cache hit.
function renderPreviewEntry(entry) {
  previewDialogTitle.textContent = `Seed "${entry.seed}"`;
  previewDialogStatus.classList.remove("error");
  previewDialogPathTabs.innerHTML = "";
  previewDialogPathTabs.hidden = true;
  previewDialogPath.innerHTML = "";
  previewDialogExplorerLink.hidden = true;
  renderEquivalents(entry.seed);

  if (entry.error) {
    previewDialogImage.removeAttribute("src");
    previewDialogImage.removeAttribute("alt");
    previewDialogImageLink.removeAttribute("href");
    previewDialogStats.textContent = "";
    previewDialogStatus.textContent = entry.error;
    previewDialogStatus.classList.add("error");
    return;
  }

  const imageFailed = entry.img && entry.img.naturalWidth === 0;
  previewDialogStatus.textContent = imageFailed ? "Failed to load the preview image." : "";
  previewDialogStatus.classList.toggle("error", Boolean(imageFailed));
  // Assign src directly (no removeAttribute first) - the PNG is browser + memory cached, so it
  // paints immediately with no display:none flash when paging back to a seen match.
  previewDialogImage.src = entry.pngUrl;
  previewDialogImage.alt = entry.alt;
  previewDialogImageLink.href = entry.imageHref;
  previewDialogStats.textContent = entry.statsText;
}

function storePreviewEntry(seed, entry) {
  previewCache.delete(seed);
  previewCache.set(seed, entry);
  while (previewCache.size > PREVIEW_CACHE_MAX) {
    previewCache.delete(previewCache.keys().next().value); // evict least-recently-used
  }
}

function touchPreviewLru(seed) {
  const entry = previewCache.get(seed);
  if (entry) {
    previewCache.delete(seed);
    previewCache.set(seed, entry);
  }
}

// Warms the match on each side of the current one so the first arrow-press that way is instant too.
function prefetchNeighbours(index) {
  for (const i of [index - 1, index + 1]) {
    if (i < 0 || i >= currentMatches.length) {
      continue;
    }
    const match = currentMatches[i];
    if (previewCache.has(match.seed) || previewInFlight.has(match.seed)) {
      continue;
    }
    resolvePreviewEntry(match.seed, match.otFriendly).catch(() => {}); // fire-and-forget
  }
}

function resetPreviewCache() {
  previewCache.clear();
  previewInFlight.clear();
  previewCacheGeneration++;
}

function renderPathTabs(seed, paths) {
  previewDialogPathTabs.innerHTML = "";
  previewDialogPathTabs.hidden = paths.length <= 1;

  paths.forEach((path, index) => {
    const button = document.createElement("button");
    button.type = "button";
    button.role = "tab";
    button.textContent = `Path ${index + 1}`;
    button.setAttribute("aria-selected", index === 0 ? "true" : "false");
    button.addEventListener("click", () => {
      for (const tab of previewDialogPathTabs.children) {
        tab.setAttribute("aria-selected", "false");
      }
      button.setAttribute("aria-selected", "true");
      renderPathDays(seed, path);
    });
    previewDialogPathTabs.appendChild(button);
  });

  renderPathDays(seed, paths[0] ?? { days: [] });
}

function renderPathDays(seed, path) {
  previewDialogPath.innerHTML = "";
  for (const { day, cardName, rerolled } of path.days) {
    const li = document.createElement("li");
    li.textContent = rerolled ? `Day ${day}: 🔄 reroll → ${cardName}` : `Day ${day}: ${cardName}`;
    previewDialogPath.appendChild(li);
  }
  wireExplorerLink(seed, path);
}

// Lazy by design: no /api/explorer/preload write happens until a button that needs it is
// actually clicked - switching between Path 1/2/3 tabs alone never touches the cache DB. Also
// idempotent per path: the hash is memoized on the path object itself (path._explorerHash) once
// created, so clicking a button again for the same path (e.g. after opening the first tab, or
// clicking both Explorer-link and Save-Favorite) reuses it instead of minting a new cache row.
async function ensureExplorerHash(path) {
  if (path._explorerHash) {
    return path._explorerHash;
  }

  const steps = path.days
    .map(({ day, cardName, rerolled }) => {
      const unlock = unlocksForSetting.find((u) => u.name === cardName);
      return unlock ? { day, cardId: unlock.id, rerolled: Boolean(rerolled) } : null;
    })
    .filter(Boolean);

  const preload = {
    dishName: currentSearchPayload.dishName,
    subOptionChoice: currentSearchPayload.subOptionChoice,
    heat: currentSearchPayload.heat,
    tier: currentSearchPayload.tier,
    franchiseCardNames: currentSearchPayload.franchiseCardNames,
    cardPack: currentSearchPayload.cardPack,
    path: steps,
  };

  const response = await fetch("/api/explorer/preload", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(preload),
  });
  const body = await response.json();
  if (!response.ok) {
    setStatus(body.error ?? "Failed to open this path in the Seed Explorer.", true);
    return null;
  }
  path._explorerHash = body.hash;
  return path._explorerHash;
}

function wireExplorerLink(seed, path) {
  if (!currentSearchPayload || path.days.length === 0) {
    previewDialogExplorerLink.hidden = true;
    if (previewDialogSaveFavorite) previewDialogSaveFavorite.hidden = true;
    return;
  }

  previewDialogExplorerLink.hidden = false;
  previewDialogExplorerLink.onclick = async () => {
    try {
      const hash = await ensureExplorerHash(path);
      if (!hash) return;
      const url = `/explorer/${encodeURIComponent(currentSearchSetting)}/${encodeURIComponent(seed)}/${hash}`;
      window.PlateUpFavorites?.stashOriginatingSearch(hash, currentSearchPayload);
      window.open(url, "_blank", "noopener");
    } catch {
      setStatus("Failed to reach the server.", true);
    }
  };

  if (previewDialogSaveFavorite) {
    previewDialogSaveFavorite.hidden = false;
    previewDialogSaveFavorite.onclick = async () => {
      try {
        const hash = await ensureExplorerHash(path);
        if (!hash) return;
        saveSeedAsFavorite(seed, hash);
      } catch {
        setStatus("Failed to reach the server.", true);
      }
    };
  }
}

// Shared by both the preview dialog's "Save this seed" button (already has a resolved path/hash)
// and the per-match ☆ pill (needs to resolve a path first - see saveMatchAsFavorite below).
function saveSeedAsFavorite(seed, explorerHash) {
  window.PlateUpFavorites?.saveFavoriteSeed({
    setting: currentSearchSetting,
    seed,
    explorerHash: explorerHash ?? null,
    dishName: currentSearchPayload.dishName,
    subOptionChoice: currentSearchPayload.subOptionChoice,
    heat: currentSearchPayload.heat,
    tier: currentSearchPayload.tier,
    franchiseCardNames: currentSearchPayload.franchiseCardNames,
    cardPack: currentSearchPayload.cardPack,
    originatingSearch: currentSearchPayload,
  });
}

// Per-match ☆ pill handler - resolves this seed's card path (same endpoint the preview dialog
// uses) before saving, so the favorite can still reopen Explorer at the right day-by-day state
// even if the dialog itself was never opened for this match.
async function saveMatchAsFavorite(seed) {
  if (!currentSearchPayload) return;
  try {
    const paths = await fetchPreviewPaths(seed, currentSearchPayload);
    const path = paths?.[0] ?? { days: [] };
    const hash = path.days.length > 0 ? await ensureExplorerHash(path) : null;
    saveSeedAsFavorite(seed, hash);
  } catch {
    setStatus("Failed to reach the server.", true);
  }
}

function closePreviewDialog() {
  previewDialog.close();
  // Clears the visible <img> so a large decoded bitmap isn't retained by the on-screen element
  // between opens. The previewCache entry (incl. its retained Image + card paths) survives, so
  // reopening the same seed within this search still renders instantly from cache.
  previewDialogImage.removeAttribute("src");
  previewDialogImageLink.removeAttribute("href");
}

previewDialogClose.addEventListener("click", closePreviewDialog);
previewDialog.addEventListener("click", (event) => {
  // A click that lands on the <dialog> element itself (not any of its content) is a backdrop
  // click - dialog's own box always fills the content area, so this only fires outside it.
  if (event.target === previewDialog) {
    closePreviewDialog();
  }
});
previewDialog.addEventListener("close", () => {
  previewDialogImage.removeAttribute("src");
  previewDialogImageLink.removeAttribute("href");
});

// Prev/next navigation between matches - both clickable arrows and Left/Right arrow keys. The
// keydown listener is on the dialog (not document) so it's only live while the modal is open;
// native Escape-to-close is unaffected.
previewDialogPrev.addEventListener("click", () => showPreviewAt(currentPreviewIndex - 1));
previewDialogNext.addEventListener("click", () => showPreviewAt(currentPreviewIndex + 1));
previewDialog.addEventListener("keydown", (event) => {
  if (event.key === "ArrowLeft") {
    event.preventDefault();
    showPreviewAt(currentPreviewIndex - 1);
  } else if (event.key === "ArrowRight") {
    event.preventDefault();
    showPreviewAt(currentPreviewIndex + 1);
  }
});

setupSettingsIoDialog({
  exportButton: document.getElementById("export-settings"),
  importButton: document.getElementById("import-settings"),
  dialog: document.getElementById("settings-io-dialog"),
  title: document.getElementById("settings-io-title"),
  status: document.getElementById("settings-io-status"),
  textarea: document.getElementById("settings-io-textarea"),
  copyButton: document.getElementById("settings-io-copy"),
  applyButton: document.getElementById("settings-io-apply"),
  closeButton: document.getElementById("settings-io-close"),
  buildExport: buildSearchPayload,
  applyImport: importSearchSettings,
});

loadInit().catch(() => setStatus("Failed to load restaurant settings.", true));
