// Blueprints view for the Card + Blueprint Explorer - a display mode on the SAME underlying day
// position explorer.js's Cards-tree navigation drives (see notifyDayShown below), not a second
// parallel tree. Globals from explorer.js (currentSetupPayload, pathToNode, dayPanelEl, heatSelect,
// etc.) are referenced directly - both files are plain classic <script> tags sharing one top-level
// lexical scope, not modules, matching this project's existing cross-file convention.

const viewModeCardsButton = document.getElementById("view-mode-cards");
const viewModeBlueprintsButton = document.getElementById("view-mode-blueprints");
const shopDayPanelEl = document.getElementById("shop-day-panel");
const heatInactiveNoteEl = document.getElementById("heat-inactive-note");
const shopDayHeadingEl = document.getElementById("shop-day-heading");
const shopLocationSettingSelect = document.getElementById("shop-location-setting");
const shopLocationResultEl = document.getElementById("shop-location-result");
const shopRoundsEl = document.getElementById("shop-rounds");
const shopAddRerollButton = document.getElementById("shop-add-reroll");
const shopOwnedAppliancesEl = document.getElementById("shop-owned-appliances");
const shopFindInput = document.getElementById("shop-find-input");
const shopFindDatalist = document.getElementById("shop-find-datalist");
const shopFindResultEl = document.getElementById("shop-find-result");

let viewMode = "cards";

// Which Cards-tree node/day the Blueprints view should simulate - kept in sync via notifyDayShown,
// called from explorer.js at the exact points its OWN day-panel changes (see that file's calls).
let lastShownPathNode = null;
let lastShownDay = null;

// Per-day-view ephemeral state (reset every time the shown day changes) - NOT part of the Cards
// tree, since reroll count is a live "how far have I looked" UI state, not a permanent decision.
// shopRerollScenarios[i] is round i+1's {held, isInside} input (see ShopRerollInputDto on the
// server) - array length IS the reroll count, no separate counter. CORRECTS an earlier, incomplete
// model: a reroll does NOT always keep the same position (see ShopSpawnLocationEvaluator.cs's doc
// comment on the project repo for the full mechanic) - held letters get a fresh placement reading
// whatever the setting is at THAT moment, unheld letters keep the previous round's spot. Defaulted
// to {held: false, isInside: shopInitialIsInside} when a new round is added, since "not held" is the
// common case (most rerolls happen with the letter sitting on the counter, not in-hand).
let shopRerollScenarios = [];
let lastShopResponse = null;
let shopFetchGeneration = 0;

// Whether letters spawn inside the restaurant (vs. outside, near the front door) at the moment THIS
// day's shop first opens (round 0 only) - mirrors the player's own live Pref.LettersSpawnInside game
// setting (a toggleable option, not seed-derived). Deliberately PERSISTS across day navigation (only
// resets on a full exploration reset) rather than resetting per day, since it's the player's actual
// game setting, not something that changes night to night on its own.
let shopInitialIsInside = true;

// Buy/Toss history - deliberately flat/global, NOT attached to Cards-tree nodes. The Cards tree can
// only create a node for a day once a card decision (pick/skip) is made for it, but a Blueprints
// action can happen on a day before any such decision exists yet - attaching to a synthesized "skip"
// node risked silently orphaning the action if a real card pick later created a DIFFERENT child for
// the same day (getOrCreateChild keys by option.id, not by day). A flat day-keyed list avoids that
// correctness trap entirely, at the cost of not being branch-aware across the Cards tree - acceptable
// for v1 (see the implementation plan's note that Buy/Toss is the first thing to simplify if needed).
let shopActionsByDay = [];

function slugifyApplianceName(name) {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
}

// Called from explorer.js whenever its OWN shown day changes (new frontier day loaded, a tree node
// clicked, or a full reset) - pathNode is the Cards-tree node whose pathToNode() gives every
// committed card decision strictly before `day` (the exact shape ExplorerShopDayRequest.cardPath
// needs). day === null means "nothing to show" (day 15 reached, or a full reset).
function notifyDayShown(pathNode, day) {
  lastShownPathNode = pathNode;
  lastShownDay = day;
  shopRerollScenarios = [];
  lastShopResponse = null;
  if (pathNode === null) {
    shopActionsByDay = []; // full exploration reset (resetExploration), not just a day change
    shopInitialIsInside = true;
  }

  if (viewMode !== "blueprints") {
    return;
  }

  if (day === null) {
    shopDayHeadingEl.textContent = "Day";
    setShopRoundsMessage(lastShownPathNode === null ? "Nothing explored yet." : "Reached day 15 - this is as far as the Explorer goes.");
    shopOwnedAppliancesEl.innerHTML = "";
    shopLocationResultEl.textContent = "";
    shopFindResultEl.innerHTML = "";
    return;
  }

  loadAndRenderShopDay();
}

function setShopRoundsMessage(message) {
  shopRoundsEl.innerHTML = "";
  const p = document.createElement("p");
  p.textContent = message;
  shopRoundsEl.appendChild(p);
}

function pathToShopActions() {
  return shopActionsByDay.filter((a) => a.day < lastShownDay).map((a) => ({ day: a.day, applianceId: a.applianceId, isToss: a.isToss }));
}

function recordShopAction(applianceId, isToss) {
  if (lastShownDay === null) {
    return;
  }
  shopActionsByDay.push({ day: lastShownDay, applianceId, isToss });
  // The server's response reflects kitchen state HEADING INTO this day (before today's actions -
  // see ExplorerShopDayResponse's doc comment), by design: a same-day action only actually takes
  // effect starting the NEXT day's fetch (pathToShopActions filters to `a.day < lastShownDay`), so
  // re-fetching alone would re-render this exact day identically and look like nothing happened.
  // Re-render from the ALREADY-loaded response instead, so today's own action-badges above (see
  // todaysShopAction) update immediately without a round-trip.
  if (lastShopResponse) {
    renderShopRounds(lastShopResponse);
    renderShopOwnedAppliances(lastShopResponse);
  }
}

// Returns today's own already-recorded action for this appliance, if any - used to render an
// immediate "Bought"/"Tossed" confirmation on the current day's own view, since the server's
// response for THIS day never reflects actions recorded for THIS SAME day (see recordShopAction).
function todaysShopAction(applianceId) {
  return shopActionsByDay.find((a) => a.day === lastShownDay && a.applianceId === applianceId);
}

async function loadAndRenderShopDay() {
  if (lastShownDay === null || viewMode !== "blueprints") {
    return;
  }

  const myGeneration = ++shopFetchGeneration;
  shopDayHeadingEl.textContent = `Day ${lastShownDay}`;
  shopLocationSettingSelect.value = shopInitialIsInside ? "inside" : "outside";
  setShopRoundsMessage(`Loading day ${lastShownDay}...`);

  const payload = {
    ...currentSetupPayload(),
    cardPath: pathToNode(lastShownPathNode),
    shopActions: pathToShopActions(),
    day: lastShownDay,
    initialIsInside: shopInitialIsInside,
    rerolls: shopRerollScenarios.map((s) => ({ held: s.held, isInside: s.isInside })),
  };

  try {
    const response = await fetch("/api/explorer/shop-day", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    const body = await response.json();
    if (myGeneration !== shopFetchGeneration) {
      return; // superseded by a newer request
    }

    if (!response.ok) {
      setShopRoundsMessage(body.error ?? "Failed to load that day's shop.");
      shopOwnedAppliancesEl.innerHTML = "";
      shopLocationResultEl.textContent = "";
      return;
    }

    lastShopResponse = body;
    shopLocationResultEl.textContent = body.rounds.length > 0 ? `Spawns: ${body.rounds[0].location}` : "";
    renderShopRounds(body);
    renderShopOwnedAppliances(body);
    refreshShopFindDatalist();
    applyFindHighlighting();
  } catch {
    if (myGeneration !== shopFetchGeneration) {
      return;
    }
    setShopRoundsMessage("Failed to reach the server.");
  }
}

shopLocationSettingSelect.addEventListener("change", () => {
  shopInitialIsInside = shopLocationSettingSelect.value === "inside";
  loadAndRenderShopDay();
});

// Renders the day's offers as one table - rounds (initial offer, then each reroll) as columns,
// slots as rows - replacing an earlier one-box-per-round vertical stack that read as a wall of
// disconnected cards rather than a scannable grid. Each column header carries that round's own
// location controls (see renderShopRoundHeaderCell) so the per-round "held when rerolled?" state
// (see ShopRerollInputDto's doc comment on the server) stays visually attached to its column.
function renderShopRounds(body) {
  shopRoundsEl.innerHTML = "";
  if (body.rounds.length === 0) {
    return;
  }

  const table = document.createElement("table");
  table.className = "shop-offers-table";

  const thead = document.createElement("thead");
  const headRow = document.createElement("tr");
  headRow.appendChild(document.createElement("th"));
  for (const round of body.rounds) {
    headRow.appendChild(renderShopRoundHeaderCell(round));
  }
  thead.appendChild(headRow);
  table.appendChild(thead);

  const tbody = document.createElement("tbody");
  const slotCount = Math.max(...body.rounds.map((r) => r.slots.length));
  for (let slotIndex = 0; slotIndex < slotCount; slotIndex++) {
    const row = document.createElement("tr");
    const rowHeader = document.createElement("th");
    rowHeader.scope = "row";
    rowHeader.textContent = `Slot ${slotIndex + 1}`;
    row.appendChild(rowHeader);
    for (const round of body.rounds) {
      row.appendChild(renderShopSlotCell(round.slots[slotIndex], round.rerollIndex));
    }
    tbody.appendChild(row);
  }
  table.appendChild(tbody);

  shopRoundsEl.appendChild(table);
}

// scenarioIndex is round.rerollIndex - 1 (shopRerollScenarios has no entry for round 0, which uses
// the day-level shopInitialIsInside control instead).
function renderShopRoundHeaderCell(round) {
  const th = document.createElement("th");
  th.scope = "col";

  const title = document.createElement("div");
  title.className = "shop-table-round-title";
  title.textContent = round.rerollIndex === 0 ? "Initial offer" : `Reroll ${round.rerollIndex}`;
  th.appendChild(title);

  if (round.rerollIndex > 0) {
    const scenarioIndex = round.rerollIndex - 1;
    const scenario = shopRerollScenarios[scenarioIndex] ?? { held: false, isInside: shopInitialIsInside };

    const controls = document.createElement("div");
    controls.className = "shop-table-held-control";

    const heldLabel = document.createElement("label");
    const heldCheckbox = document.createElement("input");
    heldCheckbox.type = "checkbox";
    heldCheckbox.checked = scenario.held;
    heldCheckbox.addEventListener("change", () => {
      shopRerollScenarios[scenarioIndex] = { ...scenario, held: heldCheckbox.checked };
      loadAndRenderShopDay();
    });
    heldLabel.appendChild(heldCheckbox);
    heldLabel.append(" Held when rerolled?");
    controls.appendChild(heldLabel);

    if (scenario.held) {
      const settingLabel = document.createElement("label");
      settingLabel.append("Spawn: ");
      const select = document.createElement("select");
      for (const [value, text] of [["inside", "Inside"], ["outside", "Outside"]]) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = text;
        select.appendChild(option);
      }
      select.value = scenario.isInside ? "inside" : "outside";
      select.addEventListener("change", () => {
        shopRerollScenarios[scenarioIndex] = { ...scenario, isInside: select.value === "inside" };
        loadAndRenderShopDay();
      });
      settingLabel.appendChild(select);
      controls.appendChild(settingLabel);
    }

    th.appendChild(controls);
  }

  const location = document.createElement("div");
  location.className = "shop-table-location";
  location.textContent = `Spawns: ${round.location}`;
  th.appendChild(location);

  return th;
}

function renderShopSlotCell(slot, rerollIndex) {
  const td = document.createElement("td");

  if (!slot || !slot.applianceName) {
    td.className = "shop-table-cell-empty";
    td.textContent = "-";
    return td;
  }

  td.dataset.applianceName = slot.applianceName;

  const cell = document.createElement("div");
  cell.className = "shop-table-cell";

  const img = document.createElement("img");
  img.src = `/images/appliances/${slugifyApplianceName(slot.applianceName)}.png`;
  img.alt = "";
  img.onerror = () => img.remove(); // most of the 451-appliance catalog has no icon - degrade to name-only, not a broken-image glyph
  cell.appendChild(img);

  const name = document.createElement("span");
  name.textContent = slot.applianceName;
  cell.appendChild(name);

  // "Buy" only on the initial offer - matches the real game (a slot about to be rerolled away isn't
  // purchasable), and keeps this simple rather than modeling "buy mid-reroll-sequence" edge cases.
  if (rerollIndex === 0) {
    const action = todaysShopAction(slot.applianceId);
    if (action && !action.isToss) {
      const badge = document.createElement("span");
      badge.className = "shop-action-badge";
      badge.textContent = "✓ Bought";
      cell.appendChild(badge);
    } else {
      const buyButton = document.createElement("button");
      buyButton.type = "button";
      buyButton.className = "link-button";
      buyButton.textContent = "Buy";
      buyButton.addEventListener("click", () => recordShopAction(slot.applianceId, false));
      cell.appendChild(buyButton);
    }
  }

  td.appendChild(cell);
  return td;
}

function addShopRerollRounds(count) {
  for (let i = 0; i < count; i++) {
    shopRerollScenarios.push({ held: false, isInside: shopInitialIsInside });
  }
}

shopAddRerollButton.addEventListener("click", () => {
  addShopRerollRounds(1);
  loadAndRenderShopDay();
});

function renderShopOwnedAppliances(body) {
  shopOwnedAppliancesEl.innerHTML = "";
  if (body.placedAppliances.length === 0) {
    const p = document.createElement("p");
    p.textContent = "Nothing placed yet.";
    shopOwnedAppliancesEl.appendChild(p);
    return;
  }

  for (const appliance of body.placedAppliances) {
    const pill = document.createElement("div");
    pill.className = "shop-slot";

    const img = document.createElement("img");
    img.src = `/images/appliances/${slugifyApplianceName(appliance.applianceName)}.png`;
    img.alt = "";
    img.onerror = () => img.remove();
    pill.appendChild(img);

    const name = document.createElement("span");
    name.textContent = appliance.applianceName;
    pill.appendChild(name);

    // See todaysShopAction's doc comment - a toss recorded for TODAY never disappears from this
    // list until the NEXT day's fetch (the server's placedAppliances is a pre-today snapshot by
    // design), so without this check clicking Toss looked like nothing happened.
    const action = todaysShopAction(appliance.applianceId);
    if (action && action.isToss) {
      pill.classList.add("shop-slot-tossed");
      const badge = document.createElement("span");
      badge.className = "shop-action-badge";
      badge.textContent = "✓ Tossed";
      pill.appendChild(badge);
    } else {
      const tossButton = document.createElement("button");
      tossButton.type = "button";
      tossButton.className = "link-button";
      tossButton.textContent = "Toss";
      tossButton.addEventListener("click", () => recordShopAction(appliance.applianceId, true));
      pill.appendChild(tossButton);
    }

    shopOwnedAppliancesEl.appendChild(pill);
  }
}

function refreshShopFindDatalist() {
  shopFindDatalist.innerHTML = "";
  if (!lastShopResponse) {
    return;
  }

  const names = new Set();
  for (const round of lastShopResponse.rounds) {
    for (const slot of round.slots) {
      if (slot.applianceName) {
        names.add(slot.applianceName);
      }
    }
  }

  for (const name of [...names].sort()) {
    const option = document.createElement("option");
    option.value = name;
    shopFindDatalist.appendChild(option);
  }
}

// Pure re-render against whatever's already loaded - never fetches. Fetching (when a search needs
// more rounds than are currently loaded) is the input handler's job below.
function applyFindHighlighting() {
  for (const el of shopRoundsEl.querySelectorAll("td.matches-find")) {
    el.classList.remove("matches-find");
  }

  const query = shopFindInput.value.trim().toLowerCase();
  shopFindResultEl.innerHTML = "";
  if (!query || !lastShopResponse) {
    return;
  }

  for (const el of shopRoundsEl.querySelectorAll("td[data-appliance-name]")) {
    if (el.dataset.applianceName.toLowerCase() === query) {
      el.classList.add("matches-find");
    }
  }

  const matchingRounds = lastShopResponse.rounds.filter((round) =>
    round.slots.some((slot) => slot.applianceName && slot.applianceName.toLowerCase() === query));

  if (matchingRounds.length > 0) {
    const descriptions = matchingRounds.map((round) =>
      (round.rerollIndex === 0 ? "the initial offer" : `reroll ${round.rerollIndex}`) + ` (spawns: ${round.location.toLowerCase()})`);
    shopFindResultEl.textContent = `Found in: ${descriptions.join(", ")}.`;
    return;
  }

  const notFound = document.createElement("span");
  notFound.textContent = `Not found in the first ${lastShopResponse.rounds.length} round(s) shown. `;
  shopFindResultEl.appendChild(notFound);

  const more = document.createElement("button");
  more.type = "button";
  more.className = "link-button";
  more.textContent = "Search 5 more rerolls";
  more.addEventListener("click", () => {
    addShopRerollRounds(5);
    loadAndRenderShopDay();
  });
  shopFindResultEl.appendChild(more);
}

shopFindInput.addEventListener("input", () => {
  const query = shopFindInput.value.trim();
  // First real search this day-view: reuse the reroll machinery to ensure a useful default depth
  // (5 rounds: the initial offer + 4 rerolls) rather than only searching whatever happens to already
  // be on screen - lazy, same "only compute rounds the user actually asks to see" spirit as the
  // Explorer's existing tree-node convention, just triggered by typing instead of a button click.
  if (query && shopRerollScenarios.length < 4 && lastShownDay !== null) {
    addShopRerollRounds(4 - shopRerollScenarios.length);
    loadAndRenderShopDay();
  } else {
    applyFindHighlighting();
  }
});

function setViewMode(mode) {
  viewMode = mode;
  viewModeCardsButton.classList.toggle("active", mode === "cards");
  viewModeCardsButton.setAttribute("aria-selected", String(mode === "cards"));
  viewModeBlueprintsButton.classList.toggle("active", mode === "blueprints");
  viewModeBlueprintsButton.setAttribute("aria-selected", String(mode === "blueprints"));

  dayPanelEl.hidden = mode !== "cards";
  shopDayPanelEl.hidden = mode !== "blueprints";

  // Heat has zero effect on shop offers (confirmed from the real game's DifficultyHelpers - see
  // docs/game-domain-knowledge.md) - visibly inert rather than silently ignored.
  heatSelect.disabled = mode === "blueprints";
  heatInactiveNoteEl.hidden = mode !== "blueprints";

  if (mode === "blueprints" && lastShownDay !== null && !lastShopResponse) {
    loadAndRenderShopDay();
  }
}

viewModeCardsButton.addEventListener("click", () => setViewMode("cards"));
viewModeBlueprintsButton.addEventListener("click", () => setViewMode("blueprints"));
