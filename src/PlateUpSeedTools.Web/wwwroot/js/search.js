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
const dayTargetRowsEl = document.getElementById("day-target-rows");
const addDayTargetButton = document.getElementById("add-day-target");
const franchiseCardRowsEl = document.getElementById("franchise-card-rows");
const addFranchiseCardButton = document.getElementById("add-franchise-card");
const stopAfterFirstMatchInput = document.getElementById("stop-after-first-match");
const excludeSeedsInput = document.getElementById("exclude-seeds-input");
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
const previewDialog = document.getElementById("preview-dialog");
const previewDialogTitle = document.getElementById("preview-dialog-title");
const previewDialogClose = document.getElementById("preview-dialog-close");
const previewDialogStatus = document.getElementById("preview-dialog-status");
const previewDialogImageLink = document.getElementById("preview-dialog-image-link");
const previewDialogImage = document.getElementById("preview-dialog-image");
const previewDialogStats = document.getElementById("preview-dialog-stats");
const previewDialogPathTabs = document.getElementById("preview-dialog-path-tabs");
const previewDialogPath = document.getElementById("preview-dialog-path");
const previewDialogExplorerLink = document.getElementById("preview-dialog-explorer-link");

// VALID_SEED/fetchSettings/populateSettingSelect come from shared.js.

let unlocksForSetting = [];
let pollHandle = null;
let currentJobId = null;
let currentSearchSetting = null;
let currentSearchPayload = null;
let currentMatches = [];
let datalistCounter = 0;

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

function getAllDayTargetRows() {
  return [...dayTargetRowsEl.querySelectorAll(".dynamic-row")];
}

function getAllFranchiseRows() {
  return [...franchiseCardRowsEl.querySelectorAll(".dynamic-row")];
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
  dayInput.min = "1";
  dayInput.step = "1";
  dayInput.placeholder = "Day";
  dayInput.className = "day-target-day";
  dayInput.required = true;

  // Starts empty and disabled - only populated (via /api/search/possible-cards) once this row has
  // a valid day, and only with cards actually reachable that day given the dish/heat/earlier picks.
  const { input: cardInput, datalist: cardDatalist } = createCardInput("day-target-card");
  cardInput.disabled = true;
  cardInput.required = true;

  const removeButton = document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "link-button";
  removeButton.textContent = "Remove";
  removeButton.addEventListener("click", () => {
    const removedDay = getRowDay(row) ?? 1;
    row.remove();
    scheduleAffectedDayTargetRefresh(removedDay);
    scheduleAllFranchiseRefresh(); // removing a picked card can free up the Theme/tier caps
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

  // Changing this row's card only changes what's reachable for rows with a LATER day - and can
  // change the Theme/franchise-tier caps, so franchise rows need a recheck too.
  cardInput.addEventListener("change", () => {
    validateCardInput(cardInput);
    const day = getRowDay(row) ?? 0;
    scheduleAffectedDayTargetRefresh(day + 1);
    scheduleAllFranchiseRefresh();
  });

  row.appendChild(dayInput);
  row.appendChild(cardInput);
  row.appendChild(cardDatalist);
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
    const day = getRowDay(row);
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

async function fetchPossibleCardNames(day, priorTargets, franchiseCardNames) {
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
      return [];
    }
    const body = await response.json();
    return body.cardNames;
  } catch {
    return [];
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
  const dayInput = row.querySelector(".day-target-day");
  const cardInput = row.querySelector(".day-target-card");
  const datalist = document.getElementById(cardInput.getAttribute("list"));

  if (!dishSelect.value) {
    // Nothing to type against yet, but this doesn't hide anything from validation - the whole
    // day-targets section is disabled in this state via setDayTargetsEnabled anyway, and the form
    // is already correctly blocked by dish-select's own separate `required` attribute regardless.
    cardInput.disabled = true;
    setDatalistOptions(datalist, []);
    return;
  }

  // Never disable this field once a dish is chosen, for any reason (invalid/empty day, zero
  // candidates, etc.) - a disabled field is invisible to native form validation entirely
  // (setCustomValidity/reportValidity/required are all no-ops on it), which was letting the form
  // submit despite an incomplete/invalid row in exactly the state that should have blocked it.
  // validateCardInput's :invalid styling communicates the problem instead, and stays effective.
  cardInput.disabled = false;

  const day = getRowDay(row);
  if (day === null || day < 1) {
    setDatalistOptions(datalist, []);
    validateCardInput(cardInput);
    return;
  }

  const state = getRowState(row);
  const myGeneration = ++state.generation;

  const priorTargets = getAllDayTargetRows()
    .filter((other) => other !== row)
    .map((other) => ({ day: getRowDay(other), cardName: other.querySelector(".day-target-card").value }))
    .filter((t) => Number.isInteger(t.day) && t.day < day && t.cardName);

  const cardNames = await fetchPossibleCardNames(day, priorTargets, getFranchiseCardNames());

  if (state.generation !== myGeneration) {
    return; // a newer request for this row superseded this one - drop the stale result
  }

  setDatalistOptions(datalist, cardNames);
  validateCardInput(cardInput);
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

dishSelect.addEventListener("change", onDishChanged);
heatSelect.addEventListener("change", onDishChanged);
tierInput.addEventListener("input", onDishChanged);
cardPackSelect.addEventListener("change", onDishChanged);
settingSelect.addEventListener("change", () => {
  loadUnlocksForSetting(settingSelect.value).catch(() => setStatus("Failed to load cards for this setting.", true));
});

async function loadInit() {
  const settings = await fetchSettings();
  populateSettingSelect(settingSelect, settings);
  await loadUnlocksForSetting(settingSelect.value);
  await loadHeatLevels();
  await loadCardPacks();
  addDayTargetRow();
  onDishChanged();

  const hash = location.hash.slice(1);
  if (hash) {
    await loadCachedResult(hash);
  }
}

// Prefills the whole form from a previously-run search's request payload (same shape as
// currentSearchPayload) - used both by loadCachedResult below. Reuses the row-building helpers the
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
  cardPackSelect.value = request.cardPack ?? "";
  alphabetInput.value = request.alphabet ?? alphabetInput.value;
  lengthInput.value = String(request.length);
  limitInput.value = request.limit != null ? String(request.limit) : "";
  stopAfterFirstMatchInput.checked = Boolean(request.stopAfterFirstMatch);
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
    row.querySelector(".day-target-day").value = String(target.day);
    row.querySelector(".day-target-card").value = target.cardName;
  }

  // Re-runs possibility queries for every row now that the setup + rows exist - this is what
  // actually enables the (initially-disabled) card inputs and populates their datalists with the
  // real candidates, including the prefilled names above, so validateCardInput accepts them.
  onDishChanged();
}

async function loadCachedResult(hash) {
  try {
    const response = await fetch(`/api/search/cached/${encodeURIComponent(hash)}`);
    if (!response.ok) {
      setStatus("This shared search result has expired or doesn't exist.", true);
      return;
    }

    const cached = await response.json();
    await prefillFormFromPayload(cached.request);

    currentSearchSetting = cached.request.setting;
    currentSearchPayload = cached.request;
    currentJobId = null; // no live job behind this view - Cancel/Copy Link stay hidden

    renderJobStatus({
      status: "Completed",
      checkedCount: cached.checkedCount,
      totalToCheck: cached.totalToCheck,
      seedsPerSecond: 0,
      elapsedSeconds: 0,
      matches: cached.matches,
      error: null,
      matchCapReached: cached.matchCapReached,
    });
  } catch {
    setStatus("Failed to reach the server.", true);
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

    if (job.status === "Completed" && job.matchCapReached) {
      jobStatusText.textContent =
        `Stopped after finding ${job.matches.length} match(es) - every search on this site stops at ` +
        "500 results (or 1, with \"Stop after first match\"). Don't like these? Adjust the seed " +
        "length/alphabet in Advanced options to search a different space, or exclude these seeds " +
        "and search again to find more.";
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
  // Only offered for a live, just-run search (currentJobId set) - a page reached via
  // /search/cached#hash has no live job behind it (see loadCachedResult), so there's nothing new
  // to share beyond the link the user is already looking at.
  copyLinkButton.hidden = job.status !== "Completed" || !currentJobId;

  // Cap how many match badges get rendered as DOM nodes - some target cards are common enough
  // that a search can turn up thousands of matches, and building/reflowing that many elements on
  // every ~1s poll tick is exactly the kind of client-side cost this page is meant to avoid (the
  // search itself already runs server-side for the same reason - see the page intro).
  const MAX_RENDERED_MATCHES = 500;
  matchesList.innerHTML = "";
  for (const seed of job.matches.slice(0, MAX_RENDERED_MATCHES)) {
    const li = document.createElement("li");
    li.textContent = seed;
    li.tabIndex = 0;
    li.addEventListener("click", () => openPreviewDialog(seed));
    li.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        openPreviewDialog(seed);
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
    await navigator.clipboard.writeText(currentMatches.join("\n"));
    setStatus(`Copied ${currentMatches.length} seed(s) to the clipboard.`, false);
  } catch {
    setStatus("Failed to copy - your browser may not allow clipboard access here.", true);
  }
});

// Lazy by design: nothing is written to the cache DB until this is clicked - a completed search
// that nobody shares never touches /api/search/{jobId}/share at all.
copyLinkButton?.addEventListener("click", async () => {
  if (!currentJobId || !currentSearchPayload) return;
  try {
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
    const url = `${location.origin}/search/cached#${body.hash}`;
    await navigator.clipboard.writeText(url);
    setStatus(`Shareable link copied (expires in ~24h): ${url}`, false);
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

  const dayTargetInputs = getAllDayTargetRows().map((row) => row.querySelector(".day-target-card"));
  const franchiseInputs = getAllFranchiseRows().map((row) => row.querySelector(".franchise-card"));

  // Re-validate every card field right before submit (not just on blur) - blocks submit with the
  // browser's native validation UI if any typed card name isn't currently valid for its row. Also
  // set an explicit status message, not just the native bubble - a native validation bubble only
  // ever points at ONE field and is easy to miss/dismiss, which was leaving submit silently
  // refusing to do anything with no visible explanation.
  let allValid = true;
  let invalidCount = 0;
  for (const input of [...dayTargetInputs, ...franchiseInputs]) {
    if (!validateCardInput(input)) {
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

  const dayTargets = getAllDayTargetRows().map((row) => ({
    day: Number(row.querySelector(".day-target-day").value),
    cardName: row.querySelector(".day-target-card").value,
  }));

  if (dayTargets.length === 0) {
    setStatus("Add at least one day target.", true);
    return;
  }

  const franchiseCardNames = getFranchiseCardNames();
  const checkedSubOption = subOptionField.querySelector("input[name='sub-option']:checked");

  const payload = {
    setting: settingSelect.value,
    dishName: dishSelect.value,
    subOptionChoice: subOptionField.hidden ? null : Number(checkedSubOption?.value ?? 1),
    heat: Number(heatSelect.value),
    tier: Number(tierInput.value),
    franchiseCardNames,
    dayTargets,
    alphabet: alphabetInput.value,
    length: Number(lengthInput.value),
    limit: limitInput.value ? Number(limitInput.value) : null,
    stopAfterFirstMatch: stopAfterFirstMatchInput.checked,
    excludedSeeds: parseExcludedSeeds(),
    cardPack: cardPackSelect.value || null,
  };

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
    jobEl.hidden = false;
    matchesList.innerHTML = "";
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
async function openPreviewDialog(seed) {
  if (!currentSearchSetting) {
    return;
  }

  previewDialogTitle.textContent = `Seed "${seed}"`;
  previewDialogStatus.textContent = "Loading...";
  previewDialogStatus.classList.remove("error");
  previewDialogImage.removeAttribute("src");
  previewDialogImageLink.removeAttribute("href");
  previewDialogStats.textContent = "";
  previewDialogPathTabs.innerHTML = "";
  previewDialogPathTabs.hidden = true;
  previewDialogPath.innerHTML = "";
  previewDialogExplorerLink.hidden = true;
  previewDialog.showModal();

  try {
    const response = await fetch(`/api/layout/${encodeURIComponent(currentSearchSetting)}/${encodeURIComponent(seed)}`);
    const layout = await response.json();
    if (!response.ok) {
      previewDialogStatus.textContent = layout.error ?? "Failed to load preview.";
      previewDialogStatus.classList.add("error");
      return;
    }

    const pngUrl = `/preview/${encodeURIComponent(currentSearchSetting)}/${encodeURIComponent(seed)}.png`;
    const alt = `${layout.settingName} — seed "${layout.seed}"`;

    // Preload off-DOM so the visible <img> only ever receives a fully-decoded image - assigning
    // src directly (the old approach) let the browser show the element mid-load, which is what
    // produced a brief broken/empty-icon flash before the PNG finished loading.
    await new Promise((resolve) => {
      const preloadImg = new Image();
      preloadImg.onload = () => {
        previewDialogImage.src = pngUrl;
        previewDialogImage.alt = alt;
        resolve();
      };
      preloadImg.onerror = () => {
        previewDialogStatus.textContent = "Failed to load the preview image.";
        previewDialogStatus.classList.add("error");
        resolve();
      };
      preloadImg.src = pngUrl;
    });

    previewDialogImageLink.href = `/preview/${encodeURIComponent(currentSearchSetting)}/${encodeURIComponent(seed)}`;
    previewDialogStatus.textContent = "";
    const tableCount = layout.appliances.filter((a) => a.applianceId === layout.tableApplianceId).length;
    previewDialogStats.textContent = `${layout.width}×${layout.height} tiles, ${tableCount} dining table(s)`;
  } catch {
    previewDialogStatus.textContent = "Failed to reach the server.";
    previewDialogStatus.classList.add("error");
    return;
  }

  await loadPreviewDialogPath(seed);
}

// Shows the full card pick for every drawing day up to the last target, not just the days the
// user explicitly targeted - schedules like "turbo" draw a card every day, so without this a
// sparse-target search (e.g. day 1 and day 10 only) left days 2-9 with no way to know what to
// pick. There's often more than one valid way to fill those in-between days (either offered card
// on a non-target day can be fine), so the server returns up to a few alternate complete paths
// (see SeedSearcher.ResolveCardPaths) - rendered here as tabs when there's more than one.
async function loadPreviewDialogPath(seed) {
  if (!currentSearchPayload) {
    return;
  }

  try {
    const response = await fetch("/api/search/path", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...currentSearchPayload, seed }),
    });
    if (!response.ok) {
      return;
    }

    const { paths } = await response.json();
    renderPathTabs(seed, paths);
  } catch {
    // Best-effort - the layout preview above is the primary content of this dialog, so a failed
    // path fetch just leaves the list empty rather than surfacing a second error state.
  }
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
  for (const { day, cardName } of path.days) {
    const li = document.createElement("li");
    li.textContent = `Day ${day}: ${cardName}`;
    previewDialogPath.appendChild(li);
  }
  wireExplorerLink(seed, path);
}

// Lazy by design: no /api/explorer/preload write happens until this button is actually clicked -
// switching between Path 1/2/3 tabs alone never touches the cache DB.
function wireExplorerLink(seed, path) {
  if (!currentSearchPayload || path.days.length === 0) {
    previewDialogExplorerLink.hidden = true;
    return;
  }

  previewDialogExplorerLink.hidden = false;
  previewDialogExplorerLink.onclick = async () => {
    const steps = path.days
      .map(({ day, cardName }) => {
        const unlock = unlocksForSetting.find((u) => u.name === cardName);
        return unlock ? { day, cardId: unlock.id } : null;
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

    try {
      const response = await fetch("/api/explorer/preload", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(preload),
      });
      const body = await response.json();
      if (!response.ok) {
        setStatus(body.error ?? "Failed to open this path in the Seed Explorer.", true);
        return;
      }
      const url = `/explorer/${encodeURIComponent(currentSearchSetting)}/${encodeURIComponent(seed)}/${body.hash}`;
      window.open(url, "_blank", "noopener");
    } catch {
      setStatus("Failed to reach the server.", true);
    }
  };
}

function closePreviewDialog() {
  previewDialog.close();
  // Clears the image so a stale/large decoded image isn't held onto between opens, and so
  // reopening the same seed re-fetches rather than silently reusing browser image-decode state.
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

loadInit().catch(() => setStatus("Failed to load restaurant settings.", true));
