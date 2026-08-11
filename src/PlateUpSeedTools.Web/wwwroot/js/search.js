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
const form = document.getElementById("search-form");
const statusEl = document.getElementById("status");
const jobEl = document.getElementById("job");
const jobStatusText = document.getElementById("job-status-text");
const cancelButton = document.getElementById("cancel-search");
const progressBar = document.getElementById("progress-bar");
const progressBarFill = document.getElementById("progress-bar-fill");
const jobStats = document.getElementById("job-stats");
const matchesList = document.getElementById("matches-list");

// VALID_SEED/fetchSettings/populateSettingSelect come from shared.js.

let unlocksForSetting = [];
let pollHandle = null;
let currentJobId = null;

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

  const cardSelect = createCardSelect();
  cardSelect.className = "day-target-card";

  const removeButton = document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "link-button";
  removeButton.textContent = "Remove";
  removeButton.addEventListener("click", () => row.remove());

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

addDayTargetButton.addEventListener("click", addDayTargetRow);
addFranchiseCardButton.addEventListener("click", addFranchiseCardRow);

function refreshCardSelectOptions() {
  const selects = document.querySelectorAll(".day-target-card, .franchise-card");
  for (const select of selects) {
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

  updateSubOptionUi();
  refreshCardSelectOptions();
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

dishSelect.addEventListener("change", updateSubOptionUi);
settingSelect.addEventListener("change", () => {
  loadUnlocksForSetting(settingSelect.value).catch(() => setStatus("Failed to load cards for this setting.", true));
});

async function loadInit() {
  const settings = await fetchSettings();
  populateSettingSelect(settingSelect, settings);
  await loadUnlocksForSetting(settingSelect.value);
  await loadHeatLevels();
  addDayTargetRow();
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

    if (job.status === "Completed") {
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

    currentJobId = body.jobId;
    jobEl.hidden = false;
    matchesList.innerHTML = "";
    pollHandle = setInterval(() => pollJob(currentJobId), 1000);
    await pollJob(currentJobId);
  } catch {
    setStatus("Failed to reach the server.", true);
  }
});

loadInit().catch(() => setStatus("Failed to load restaurant settings.", true));
