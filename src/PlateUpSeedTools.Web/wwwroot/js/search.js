const settingSelect = document.getElementById("setting-select");
const dishSelect = document.getElementById("dish-select");
const subOptionField = document.getElementById("sub-option-field");
const subOption1Label = document.getElementById("sub-option-1-label");
const subOption2Label = document.getElementById("sub-option-2-label");
const heatSelect = document.getElementById("heat-select");
const tierInput = document.getElementById("tier-input");
const alphabetInput = document.getElementById("alphabet-input");
const lengthInput = document.getElementById("length-input");
const limitInput = document.getElementById("limit-input");
const dayTargetRowsEl = document.getElementById("day-target-rows");
const addDayTargetButton = document.getElementById("add-day-target");
const franchiseCardRowsEl = document.getElementById("franchise-card-rows");
const addFranchiseCardButton = document.getElementById("add-franchise-card");
const stopAfterFirstMatchInput = document.getElementById("stop-after-first-match");
const form = document.getElementById("search-form");
const statusEl = document.getElementById("status");
const jobEl = document.getElementById("job");
const jobStatusText = document.getElementById("job-status-text");
const cancelButton = document.getElementById("cancel-search");
const progressBar = document.getElementById("progress-bar");
const progressBarFill = document.getElementById("progress-bar-fill");
const jobStats = document.getElementById("job-stats");
const matchesList = document.getElementById("matches-list");
const previewDialog = document.getElementById("preview-dialog");
const previewDialogTitle = document.getElementById("preview-dialog-title");
const previewDialogClose = document.getElementById("preview-dialog-close");
const previewDialogStatus = document.getElementById("preview-dialog-status");
const previewDialogImage = document.getElementById("preview-dialog-image");
const previewDialogStats = document.getElementById("preview-dialog-stats");

// VALID_SEED/fetchSettings/populateSettingSelect come from shared.js.

let unlocksForSetting = [];
let pollHandle = null;
let currentJobId = null;
let currentSearchSetting = null;
let dayTargetRefreshTimer = null;

function setStatus(message, isError) {
  statusEl.textContent = message;
  statusEl.classList.toggle("error", Boolean(isError));
}

function createCardSelect() {
  const select = document.createElement("select");
  for (const unlock of unlocksForSetting) {
    const option = document.createElement("option");
    option.value = unlock.name;
    option.textContent = unlock.name;
    select.appendChild(option);
  }
  return select;
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
  // See refreshAllDayTargetCandidates.
  const cardSelect = document.createElement("select");
  cardSelect.className = "day-target-card";
  cardSelect.disabled = true;

  const removeButton = document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "link-button";
  removeButton.textContent = "Remove";
  removeButton.addEventListener("click", () => {
    row.remove();
    scheduleDayTargetRefresh();
  });

  dayInput.addEventListener("input", scheduleDayTargetRefresh);
  cardSelect.addEventListener("change", scheduleDayTargetRefresh);

  row.appendChild(dayInput);
  row.appendChild(cardSelect);
  row.appendChild(removeButton);
  dayTargetRowsEl.appendChild(row);
}

function addFranchiseCardRow() {
  const row = document.createElement("div");
  row.className = "dynamic-row";

  const cardSelect = createCardSelect();
  cardSelect.className = "franchise-card";

  const removeButton = document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "link-button";
  removeButton.textContent = "Remove";
  removeButton.addEventListener("click", () => row.remove());

  row.appendChild(cardSelect);
  row.appendChild(removeButton);
  franchiseCardRowsEl.appendChild(row);
}

addDayTargetButton.addEventListener("click", () => {
  addDayTargetRow();
  setDayTargetsEnabled(Boolean(dishSelect.value));
});
addFranchiseCardButton.addEventListener("click", () => {
  addFranchiseCardRow();
  scheduleDayTargetRefresh(); // a newly-owned franchise card can affect later days' possible cards
});

// Franchise-carryover cards aren't day-sequenced (they represent cards already owned from a prior
// run, not a night-by-night pick) - that select keeps using the full unfiltered per-setting list.
// Day-target card selects are populated separately by refreshAllDayTargetCandidates, since their
// options depend on which day the row is for and what's been picked for earlier days.
function refreshFranchiseCardSelectOptions() {
  for (const select of document.querySelectorAll(".franchise-card")) {
    const previousValue = select.value;
    select.innerHTML = "";
    for (const unlock of unlocksForSetting) {
      const option = document.createElement("option");
      option.value = unlock.name;
      option.textContent = unlock.name;
      select.appendChild(option);
    }
    if ([...select.options].some((o) => o.value === previousValue)) {
      select.value = previousValue;
    }
  }
}

// Disables/enables the entire day-targets section (per user request: it should be unusable until
// a starting dish is chosen, since "what's possible on day N" is meaningless without one). Card
// selects specifically are then re-narrowed by refreshAllDayTargetCandidates, which disables any
// that don't yet have a valid day even when the section overall is enabled.
function setDayTargetsEnabled(enabled) {
  addDayTargetButton.disabled = !enabled;
  for (const el of dayTargetRowsEl.querySelectorAll("input, select, button")) {
    el.disabled = !enabled;
  }
}

function scheduleDayTargetRefresh() {
  clearTimeout(dayTargetRefreshTimer);
  dayTargetRefreshTimer = setTimeout(refreshAllDayTargetCandidates, 300);
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

// Recomputes every day-target row's candidate list from scratch based on the current dish/heat/
// tier/franchise-cards and every *other* row's day+card. Recomputing all rows on any single change
// (rather than tracking a precise per-row dependency graph) is simplest and cheap given day-target
// lists are always small - picking a card for an earlier day can affect later rows (e.g. Medium
// Groups -> Large Groups becomes reachable), so any row could in principle depend on any other.
async function refreshAllDayTargetCandidates() {
  if (!dishSelect.value) {
    return;
  }

  const rows = [...dayTargetRowsEl.querySelectorAll(".dynamic-row")].map((row) => ({
    dayInput: row.querySelector(".day-target-day"),
    cardSelect: row.querySelector(".day-target-card"),
  }));

  const franchiseCardNames = [...franchiseCardRowsEl.querySelectorAll(".dynamic-row")].map(
    (row) => row.querySelector(".franchise-card").value
  );

  await Promise.all(
    rows.map(async ({ dayInput, cardSelect }) => {
      const day = Number(dayInput.value);
      if (!dayInput.value || !Number.isInteger(day) || day < 1) {
        cardSelect.disabled = true;
        cardSelect.innerHTML = "";
        return;
      }

      const priorTargets = rows
        .filter((other) => other.dayInput !== dayInput)
        .map((other) => ({ day: Number(other.dayInput.value), cardName: other.cardSelect.value }))
        .filter((t) => Number.isInteger(t.day) && t.day < day && t.cardName);

      const cardNames = await fetchPossibleCardNames(day, priorTargets, franchiseCardNames);
      const previousValue = cardSelect.value;
      cardSelect.innerHTML = "";
      for (const name of cardNames) {
        const option = document.createElement("option");
        option.value = name;
        option.textContent = name;
        cardSelect.appendChild(option);
      }
      cardSelect.disabled = cardNames.length === 0;
      if (cardNames.includes(previousValue)) {
        cardSelect.value = previousValue;
      }
    })
  );
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

// Single entry point for anything that can change which cards are reachable/relevant: dish, heat,
// tier, or franchise-carryover-card selection. Updates the sub-option UI, the day-targets
// section's enabled state (per user request: unusable before a dish is chosen), the franchise-card
// dropdown's option list, and re-runs the day-target possibility query.
function onDishChanged() {
  updateSubOptionUi();
  setDayTargetsEnabled(Boolean(dishSelect.value));
  refreshFranchiseCardSelectOptions();
  refreshAllDayTargetCandidates();
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

dishSelect.addEventListener("change", onDishChanged);
heatSelect.addEventListener("change", () => refreshAllDayTargetCandidates());
tierInput.addEventListener("input", scheduleDayTargetRefresh);
settingSelect.addEventListener("change", () => {
  loadUnlocksForSetting(settingSelect.value).catch(() => setStatus("Failed to load cards for this setting.", true));
});

async function loadInit() {
  const settings = await fetchSettings();
  populateSettingSelect(settingSelect, settings);
  await loadUnlocksForSetting(settingSelect.value);
  await loadHeatLevels();
  addDayTargetRow();
  onDishChanged();
}

function stopPolling() {
  if (pollHandle !== null) {
    clearInterval(pollHandle);
    pollHandle = null;
  }
}

function renderJobStatus(job) {
  jobEl.hidden = false;

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
        "length/alphabet in Advanced options to search a different space.";
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

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  stopPolling();
  setStatus("", false);

  const dayTargets = [...dayTargetRowsEl.querySelectorAll(".dynamic-row")].map((row) => ({
    day: Number(row.querySelector(".day-target-day").value),
    cardName: row.querySelector(".day-target-card").value,
  }));

  if (dayTargets.length === 0) {
    setStatus("Add at least one day target.", true);
    return;
  }

  const franchiseCardNames = [...franchiseCardRowsEl.querySelectorAll(".dynamic-row")].map(
    (row) => row.querySelector(".franchise-card").value
  );

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

    // Captured now (not read live from settingSelect.value on click) so that clicking a match
    // still previews the right setting even if the user changes the dropdown while this job's
    // results are still on screen.
    currentSearchSetting = payload.setting;
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
  previewDialogStats.textContent = "";
  previewDialog.showModal();

  try {
    const response = await fetch(`/api/layout/${encodeURIComponent(currentSearchSetting)}/${encodeURIComponent(seed)}`);
    const layout = await response.json();
    if (!response.ok) {
      previewDialogStatus.textContent = layout.error ?? "Failed to load preview.";
      previewDialogStatus.classList.add("error");
      return;
    }

    previewDialogStatus.textContent = "";
    previewDialogImage.alt = `${layout.settingName} — seed "${layout.seed}"`;
    previewDialogImage.src = `/preview/${encodeURIComponent(currentSearchSetting)}/${encodeURIComponent(seed)}.png`;
    const tableCount = layout.appliances.filter((a) => a.applianceId === layout.tableApplianceId).length;
    previewDialogStats.textContent = `${layout.width}×${layout.height} tiles, ${tableCount} dining table(s)`;
  } catch {
    previewDialogStatus.textContent = "Failed to reach the server.";
    previewDialogStatus.classList.add("error");
  }
}

function closePreviewDialog() {
  previewDialog.close();
  // Clears the image so a stale/large decoded image isn't held onto between opens, and so
  // reopening the same seed re-fetches rather than silently reusing browser image-decode state.
  previewDialogImage.removeAttribute("src");
}

previewDialogClose.addEventListener("click", closePreviewDialog);
previewDialog.addEventListener("click", (event) => {
  // A click that lands on the <dialog> element itself (not any of its content) is a backdrop
  // click - dialog's own box always fills the content area, so this only fires outside it.
  if (event.target === previewDialog) {
    closePreviewDialog();
  }
});
previewDialog.addEventListener("close", () => previewDialogImage.removeAttribute("src"));

loadInit().catch(() => setStatus("Failed to load restaurant settings.", true));
