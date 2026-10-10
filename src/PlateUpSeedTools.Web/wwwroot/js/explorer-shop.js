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
const shopDayPrevButton = document.getElementById("shop-day-prev");
const shopDayNextButton = document.getElementById("shop-day-next");
const shopLocationControlsEl = document.getElementById("shop-location-controls");
const shopLocationResultEl = document.getElementById("shop-location-result");
const shopRoundsEl = document.getElementById("shop-rounds");
const shopAddRerollButton = document.getElementById("shop-add-reroll");
const shopOwnedAppliancesEl = document.getElementById("shop-owned-appliances");
const shopConditionalStaplesEl = document.getElementById("shop-conditional-staples");
const shopFindForm = document.getElementById("shop-find-form");
const shopFindInput = document.getElementById("shop-find-input");
const shopFindDatalist = document.getElementById("shop-find-datalist");
const shopFindClearButton = document.getElementById("shop-find-clear-button");
const shopFindResultEl = document.getElementById("shop-find-result");

let viewMode = "cards";

// Which Cards-tree node/day the Blueprints view should simulate - kept in sync via notifyDayShown,
// called from explorer.js at the exact points its OWN day-panel changes (see that file's calls).
let lastShownPathNode = null;
let lastShownDay = null;

// Per-day-view ephemeral state (reset every time the shown day changes) - NOT part of the Cards
// tree, since reroll count is a live "how far have I looked" UI state, not a permanent decision.
// shopRerollScenarios[i] is round i+1's {held, isInside, someoneInside} input (see
// ShopRerollInputDto on the server) - array length IS the reroll count, no separate counter.
// CORRECTS an earlier, incomplete model: a reroll does NOT always keep the same position (see
// ShopSpawnLocationEvaluator.cs's doc comment on the project repo for the full mechanic) - held
// letters get a fresh placement reading whatever the setting (and, if Inside, player position) is at
// THAT moment, unheld letters keep the previous round's spot. Defaulted to {held: false, isInside:
// shopInitialIsInside, someoneInside: shopInitialSomeoneInside} when a new round is added, since
// "not held" is the common case (most rerolls happen with the letter sitting on the counter, not
// in-hand).
let shopRerollScenarios = [];
let lastShopResponse = null;
let shopFetchGeneration = 0;

// Whether letters spawn inside the restaurant (vs. outside, near the front door) at the moment THIS
// day's shop first opens (round 0 only) - mirrors the player's own live Pref.LettersSpawnInside game
// setting (a toggleable option, not seed-derived). Deliberately PERSISTS across day navigation (only
// resets on a full exploration reset) rather than resetting per day, since it's the player's actual
// game setting, not something that changes night to night on its own.
let shopInitialIsInside = true;

// Whether at least one player was standing on an inside tile at that exact moment - only consulted
// when shopInitialIsInside is true (see ShopSpawnLocationEvaluator.Evaluate's second parameter).
// Defaults to false (see ExplorerShopDayRequest.InitialSomeoneInside's doc comment on the server for
// why false, not true, is the non-breaking default) and persists the same way shopInitialIsInside does.
let shopInitialSomeoneInside = false;

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

// Sets the Blueprints view's own day position. Called from exactly two places now: explorer.js's
// resetExploration() with (null, null) on a genuine full reset, and Blueprints' own navigation
// (walkShopViewToDay, the Previous-day click handler below) - NOT from Cards-view navigation
// (renderFrontier/tree-node clicks no longer call this at all - see renderFrontier's doc comment for
// the real bug this fixed: Cards' own frontier-sync call was clobbering Blueprints' independent Day-1
// default the instant it fired). pathNode is the Cards-tree node whose pathToNode() gives every
// committed card decision strictly before `day` (the exact shape ExplorerShopDayRequest.cardPath
// needs). day === null means "nothing to show" (a full reset only, now - day 15 is reachable but
// Next is simply disabled there, not routed through this null-day path anymore).
function notifyDayShown(pathNode, day) {
  lastShownPathNode = pathNode;
  lastShownDay = day;
  shopRerollScenarios = [];
  lastShopResponse = null;
  if (pathNode === null) {
    shopActionsByDay = []; // full exploration reset (resetExploration), not just a day change
    shopInitialIsInside = true;
    shopInitialSomeoneInside = false;
  }

  // Seed the default reroll scenarios regardless of which view is currently active - the state is
  // shared, and setViewMode's own loadAndRenderShopDay() call (when the user switches INTO
  // Blueprints later, after already having navigated days in Cards view) must find this already
  // populated, not empty.
  //
  // Real bug fixed here: this used to always seed exactly 3 rounds, even when shopActionsByDay
  // already has a recorded action at a LATER round for this day (e.g. the user expanded past round
  // 3 with "Add another reroll", bought something at round 6, then navigated away and back). Since
  // shopRerollScenarios.length IS the reroll count sent to the server (rerolls: [...]), collapsing
  // back to 3 made the next request's round-by-round loop never reach round 6 at all - the buy was
  // silently dropped from server-side kitchen state while shopActionsByDay (and therefore the
  // rendered "Bought" badge, once rounds re-expand) still claimed it happened. Seed at least enough
  // rounds to cover every already-recorded action for this exact day, not a flat 3.
  if (day !== null) {
    const recordedRoundsForDay = shopActionsByDay.filter((a) => a.day === day).map((a) => a.round);
    const minRounds = Math.max(3, ...recordedRoundsForDay, 0);
    addShopRerollRounds(minRounds); // show Initial offer + at least `minRounds` rerolls
  }

  updateShopDayNavButtons();

  if (viewMode !== "blueprints") {
    return;
  }

  if (day === null) {
    shopDayHeadingEl.textContent = "Day";
    setShopRoundsMessage(lastShownPathNode === null ? "Nothing explored yet." : "Reached day 15 - this is as far as the Explorer goes.");
    shopOwnedAppliancesEl.innerHTML = "";
    shopConditionalStaplesEl.innerHTML = "";
    shopLocationResultEl.textContent = "";
    shopFindResultEl.innerHTML = "";
    return;
  }

  loadAndRenderShopDay();
}

// Lower bound is 0 only when Jasmine is enabled (Day 0 has no shop at all otherwise - see
// ExplorerEndpoints.SimulateShopDay's gating); upper bound 15 matches the server's own day-range
// validation. Disabling both while nothing is explored yet (lastShownDay === null) falls out of the
// same comparisons without a separate check.
function updateShopDayNavButtons() {
  const lowerBound = jasmineCheckbox.checked ? 0 : 1;
  shopDayPrevButton.disabled = lastShownDay === null || lastShownDay <= lowerBound;
  shopDayNextButton.disabled = lastShownDay === null || lastShownDay >= 15;
}

shopDayPrevButton.addEventListener("click", () => {
  // lastShownPathNode already IS the previous day's own node - every child of a given parent is
  // necessarily for parent.day + 1, so lastShownPathNode.day === lastShownDay - 1 always. No walk,
  // no fetch of a new tree node, just move the pointer back (see walkShopViewToDay's doc comment in
  // explorer.js for why this is safe/cheap).
  if (shopDayPrevButton.disabled || lastShownPathNode === null) {
    return;
  }
  notifyDayShown(lastShownPathNode.parent, lastShownPathNode.day);
});

shopDayNextButton.addEventListener("click", () => {
  if (shopDayNextButton.disabled) {
    return;
  }
  walkShopViewToDay(lastShownDay + 1);
});

function setShopRoundsMessage(message) {
  shopRoundsEl.innerHTML = "";
  const p = document.createElement("p");
  p.textContent = message;
  shopRoundsEl.appendChild(p);
}

function pathToShopActions() {
  // Prior days: unchanged, day-level only - by the time a LATER day is requested, that whole day's
  // rounds are moot, only the final kitchen-state effect matters (see ShopActionDto's doc comment).
  const priorDay = shopActionsByDay
    .filter((a) => a.day < lastShownDay)
    .map((a) => ({ day: a.day, applianceId: a.applianceId, isToss: a.isToss, isManual: !!a.isManual }));
  // Today: round (and, for a buy, slotIndex) are required - see ShopActionDto's doc comment for why.
  // A manual ownership toggle never carries round/slotIndex, even for today.
  const today = shopActionsByDay
    .filter((a) => a.day === lastShownDay)
    .map((a) => ({ day: a.day, applianceId: a.applianceId, isToss: a.isToss, round: a.round, slotIndex: a.slotIndex, isManual: !!a.isManual }));
  return [...priorDay, ...today];
}

// round is which round (0 = initial offer) this action happened during; slotIndex is REQUIRED for a
// Buy (null for a Toss - see ShopActionDto's doc comment on the server for why).
function recordShopAction(applianceId, isToss, round, slotIndex = null) {
  if (lastShownDay === null) {
    return;
  }
  shopActionsByDay.push({ day: lastShownDay, round, applianceId, isToss, slotIndex, isManual: false });
  reRenderShopPanelsOptimisticallyThenRefetch();
}

// Click-to-toggle purchase state for a shop-grid slot cell (requests 1+5: Buy becomes a two-way
// toggle, not a one-way action). If this exact (round, slotIndex) cell was already bought this
// session, splice that action back out - un-doing it needs no server-side support, since the whole
// day is always re-simulated from shopActionsByDay from scratch (see this file's header comment);
// removing the entry just means ShopDayRoundState.RemoveSlot never gets called for it, so the slot
// naturally reappears in later rounds exactly as if it had never been bought.
function toggleShopBuyAction(applianceId, round, slotIndex) {
  if (lastShownDay === null) {
    return;
  }
  const existing = todaysShopBuyAction(round, slotIndex);
  if (existing) {
    shopActionsByDay.splice(shopActionsByDay.indexOf(existing), 1);
  } else {
    shopActionsByDay.push({ day: lastShownDay, round, applianceId, isToss: false, slotIndex, isManual: false });
  }
  reRenderShopPanelsOptimisticallyThenRefetch();
}

// Always-append toggle for the "appliances you might already own" panel (request 2): marks/un-marks
// ownership directly via a manual action (IsManual: true on the wire - see ShopActionDto's doc
// comment), bypassing the shop-offer round/slot machinery entirely. Deliberately doesn't try to
// splice/undo like toggleShopBuyAction above - repeated toggles just accumulate place/toss pairs that
// replay correctly in recorded order, which is simpler and correct even when the appliance was
// already owned via something other than this panel (a required layout item, or a real shop buy).
function recordManualOwnershipAction(applianceId, markOwned) {
  if (lastShownDay === null) {
    return;
  }
  shopActionsByDay.push({ day: lastShownDay, round: null, applianceId, isToss: !markOwned, slotIndex: null, isManual: true });
  reRenderShopPanelsOptimisticallyThenRefetch();
}

// A same-day Buy/Toss/manual-toggle now genuinely changes THIS day's own later rounds (buying
// permanently removes that slot from every round after `round` - see ShopDayRoundState's doc comment
// on the server for the real mechanic this models; a toss or manual toggle can too, by re-evaluating
// staple eligibility for later rounds' candidate pool). So a real re-fetch is required, not just a
// local re-render. Still do an optimistic local re-render first (from whatever's already loaded) so
// the clicked control's new state appears instantly, not just after the round-trip.
function reRenderShopPanelsOptimisticallyThenRefetch() {
  if (lastShopResponse) {
    renderShopRounds(lastShopResponse);
    renderShopOwnedAppliances(lastShopResponse);
    renderConditionalStapleAppliances(lastShopResponse);
  }
  loadAndRenderShopDay();
}

// Returns today's own already-recorded BUY for this exact (round, slotIndex), if any - scoped to a
// specific cell, not just an appliance ID, since once Buy exists on every round the SAME appliance
// name can legitimately appear in multiple different (round, slot) cells the same day (e.g. Sink in
// round 2's slot 0 AND independently again in round 5's slot 3) - an appliance-only lookup would
// incorrectly badge every matching-name cell as bought.
function todaysShopBuyAction(round, slotIndex) {
  return shopActionsByDay.find((a) => a.day === lastShownDay && !a.isToss && a.round === round && a.slotIndex === slotIndex);
}

// Returns today's own already-recorded TOSS for this appliance, if any - Toss targets an OWNED
// appliance by ID (not a shop slot), so appliance-ID lookup is correct here, unlike Buy above.
function todaysShopTossAction(applianceId) {
  return shopActionsByDay.find((a) => a.day === lastShownDay && a.isToss && a.applianceId === applianceId);
}

async function loadAndRenderShopDay() {
  if (lastShownDay === null || viewMode !== "blueprints") {
    return;
  }

  const myGeneration = ++shopFetchGeneration;
  shopDayHeadingEl.textContent = `Day ${lastShownDay}`;
  renderShopLocationControls();
  setShopRoundsMessage(`Loading day ${lastShownDay}...`);

  const payload = {
    ...currentSetupPayload(),
    cardPath: pathToNode(lastShownPathNode),
    shopActions: pathToShopActions(),
    day: lastShownDay,
    initialIsInside: shopInitialIsInside,
    initialSomeoneInside: shopInitialSomeoneInside,
    rerolls: shopRerollScenarios.map((s) => ({ held: s.held, isInside: s.isInside, someoneInside: s.someoneInside })),
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
      shopConditionalStaplesEl.innerHTML = "";
      shopLocationResultEl.textContent = "";
      return;
    }

    lastShopResponse = body;
    shopLocationResultEl.textContent = body.rounds.length > 0 ? `Spawns: ${body.rounds[0].location}` : "";
    if (lastShownDay === 0 && body.rounds.length === 0) {
      // See ExplorerEndpoints.SimulateShopDay's day-0 gating - this isn't an error, day 0 just has
      // no shop without Jasmine. Explain it rather than leaving a silently blank table.
      setShopRoundsMessage("Day 0 has no shop for this run - enable the Jasmine checkbox above to model its prep-day shop.");
    } else {
      renderShopRounds(body);
    }
    renderShopOwnedAppliances(body);
    renderConditionalStapleAppliances(body);
    refreshShopFindDatalist();
    applyFindHighlighting();
  } catch {
    if (myGeneration !== shopFetchGeneration) {
      return;
    }
    setShopRoundsMessage("Failed to reach the server.");
  }
}

// Small (2-option) mutually-exclusive toggle - replaces a <select> where a dropdown would hide the
// options behind an extra click (research: textbook UI guidance treats a <select> as justified at
// ~5+ options or under real space pressure; for 2-3 options, exposing every choice as always-visible
// buttons is the standard replacement). Same pill-button visual language as .view-mode-toggle/
// .view-mode-button (the Cards/Blueprints switch), sized for compact/inline use instead - see
// .toggle-button-* in theme.css. Flips its own pressed state synchronously on click (not a native
// form control, so this needs doing by hand) before calling onChange, so the control itself reacts
// immediately rather than waiting on the day re-fetch to resolve.
function renderToggleButtonGroup(groupLabel, options, currentValue, onChange) {
  const wrapper = document.createElement("div");
  wrapper.className = "toggle-button-wrapper";

  const labelEl = document.createElement("span");
  labelEl.className = "toggle-button-label";
  labelEl.textContent = groupLabel;
  wrapper.appendChild(labelEl);

  const group = document.createElement("div");
  group.className = "toggle-button-group";
  group.setAttribute("role", "radiogroup");
  group.setAttribute("aria-label", groupLabel);

  const entries = options.map(([value, text]) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "toggle-button";
    button.textContent = text;
    button.setAttribute("role", "radio");
    return { button, value };
  });

  function setPressed(selected) {
    for (const { button, value } of entries) {
      const pressed = value === selected;
      button.classList.toggle("active", pressed);
      button.setAttribute("aria-checked", String(pressed));
    }
  }
  setPressed(currentValue);

  for (const { button, value } of entries) {
    button.addEventListener("click", () => {
      setPressed(value);
      onChange(value);
    });
    group.appendChild(button);
  }

  wrapper.appendChild(group);
  return wrapper;
}

// Renders the day-level spawn-setting controls into #shop-location-controls - the Inside/Outside
// toggle, plus (only when Inside is active) a second "someone standing inside?" toggle, since player
// position is irrelevant in Outside mode (see ShopSpawnLocationEvaluator's doc comment).
function renderShopLocationControls() {
  shopLocationControlsEl.innerHTML = "";
  shopLocationControlsEl.appendChild(renderToggleButtonGroup(
    "Initial offer - letters spawn:",
    [["outside", "Outside"], ["inside", "Inside"]],
    shopInitialIsInside ? "inside" : "outside",
    (value) => {
      shopInitialIsInside = value === "inside";
      renderShopLocationControls(); // rebuild so the someone-inside group appears/disappears immediately
      loadAndRenderShopDay();
    },
  ));

  if (shopInitialIsInside) {
    shopLocationControlsEl.appendChild(renderToggleButtonGroup(
      "Someone standing inside right then?",
      [["no", "No"], ["yes", "Yes"]],
      shopInitialSomeoneInside ? "yes" : "no",
      (value) => {
        shopInitialSomeoneInside = value === "yes";
        loadAndRenderShopDay();
      },
    ));
  }
}

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
  // Slot identity is ShopSlotDto.slotIndex, NOT list position - once a slot is bought it disappears
  // from every later round's list ENTIRELY (no placeholder entry - see ShopSlotDto's doc comment on
  // the server), so a round's Nth list entry does not reliably mean "slot N" once any round has fewer
  // entries than an earlier one. Row count/order comes from the FIRST round (the initial offer),
  // which always has the full, never-yet-shrunk set - every later round's row just looks up its own
  // slot by index, or renders as sold-out if that index is no longer present.
  const slotIndexes = body.rounds[0].slots.map((s) => s.slotIndex);
  for (const slotIndex of slotIndexes) {
    const row = document.createElement("tr");
    const rowHeader = document.createElement("th");
    rowHeader.scope = "row";
    rowHeader.textContent = `Slot ${slotIndex + 1}`;
    row.appendChild(rowHeader);
    for (const round of body.rounds) {
      const slot = round.slots.find((s) => s.slotIndex === slotIndex);
      row.appendChild(renderShopSlotCell(slot, round.rerollIndex, slotIndex));
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
    const scenario = shopRerollScenarios[scenarioIndex] ?? { held: false, isInside: shopInitialIsInside, someoneInside: shopInitialSomeoneInside };

    const controls = document.createElement("div");
    controls.className = "shop-table-held-control";

    // Re-renders this table from whatever's already loaded BEFORE the fetch resolves, so a reveal/
    // hide of a dependent control (Spawn appearing when Held is checked, Someone-inside appearing
    // when Spawn=Inside) happens immediately rather than waiting on a round-trip - same "don't make
    // the user wait to see their own click took effect" principle as recordShopAction's Buy/Toss
    // badges above.
    function updateScenario(patch) {
      shopRerollScenarios[scenarioIndex] = { ...shopRerollScenarios[scenarioIndex] ?? scenario, ...patch };
      if (lastShopResponse) {
        renderShopRounds(lastShopResponse);
      }
      loadAndRenderShopDay();
    }

    const heldLabel = document.createElement("label");
    const heldCheckbox = document.createElement("input");
    heldCheckbox.type = "checkbox";
    heldCheckbox.checked = scenario.held;
    heldCheckbox.addEventListener("change", () => updateScenario({ held: heldCheckbox.checked }));
    heldLabel.appendChild(heldCheckbox);
    heldLabel.append(" Held when rerolled?");
    controls.appendChild(heldLabel);

    if (scenario.held) {
      controls.appendChild(renderToggleButtonGroup(
        "Spawn:",
        [["outside", "Outside"], ["inside", "Inside"]],
        scenario.isInside ? "inside" : "outside",
        (value) => updateScenario({ isInside: value === "inside" }),
      ));

      if (scenario.isInside) {
        controls.appendChild(renderToggleButtonGroup(
          "Someone inside then?",
          [["no", "No"], ["yes", "Yes"]],
          scenario.someoneInside ? "yes" : "no",
          (value) => updateScenario({ someoneInside: value === "yes" }),
        ));
      }
    }

    th.appendChild(controls);
  }

  const location = document.createElement("div");
  location.className = "shop-table-location";
  location.textContent = `Spawns: ${round.location}`;
  th.appendChild(location);

  return th;
}

function renderShopSlotCell(slot, rerollIndex, slotIndex) {
  const td = document.createElement("td");

  if (!slot) {
    // Distinct from the "nothing offered" empty case below - this slot was BOUGHT in an earlier
    // round THIS day and, per the real game's mechanic (buying permanently removes that slot, no
    // backfill - see ShopDayRoundState's doc comment on the server), it simply no longer exists from
    // this round onward. Not the same concept as "RNG produced nothing for this slot" - don't reuse
    // shop-table-cell-empty's styling/text for it.
    td.className = "shop-table-cell-closed";
    td.textContent = "— sold —";
    return td;
  }

  if (!slot.applianceName) {
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

  // Buy is now available on EVERY round, not just the initial offer - the real game lets you buy
  // from whatever letters are CURRENTLY on display, right up until a later reroll destroys them (see
  // ShopDayRoundState's doc comment on the server for the full mechanic this corrects - an earlier
  // version of this code incorrectly restricted Buy to round 0 only).
  //
  // Click-to-toggle purchase state (requests 1+5): one button, flips between "Buy" and "✓ Bought" -
  // clicking either state calls the same toggle, which un-does a purchase on a second click rather
  // than only ever recording a one-way action.
  const action = todaysShopBuyAction(rerollIndex, slotIndex);
  const buyButton = document.createElement("button");
  buyButton.type = "button";
  buyButton.className = action ? "link-button shop-action-badge" : "link-button";
  buyButton.textContent = action ? "✓ Bought" : "Buy";
  buyButton.title = action ? "Click to undo this purchase" : "Click to mark as purchased";
  buyButton.addEventListener("click", () => toggleShopBuyAction(slot.applianceId, rerollIndex, slotIndex));
  cell.appendChild(buyButton);

  td.appendChild(cell);
  return td;
}

function addShopRerollRounds(count) {
  for (let i = 0; i < count; i++) {
    shopRerollScenarios.push({ held: false, isInside: shopInitialIsInside, someoneInside: shopInitialSomeoneInside });
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

    // See todaysShopTossAction's doc comment - a toss recorded for TODAY never disappears from this
    // list until the NEXT day's fetch (the server's placedAppliances is a pre-today snapshot by
    // design), so without this check clicking Toss looked like nothing happened.
    const action = todaysShopTossAction(appliance.applianceId);
    if (action) {
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
      // This panel isn't rendered per-round, so there's no natural "which round was I looking at"
      // anchor for a Toss click - record it against the most-recently-loaded round. An approximation
      // (a toss conceptually happens "right now", and "now" isn't well-defined in a UI previewing
      // many future rounds at once simultaneously) but a reasonable one: staple re-eligibility from a
      // toss can only ever matter for a FUTURE day's round 0 anyway (every round after THIS day's own
      // round 0 is a reroll, and a reroll can never produce a staple at all regardless of ownership -
      // see ShopDaySimulator's reroll-tag doc comment on the server), so which round within today a
      // toss is anchored to has no observable effect on today's own output.
      tossButton.addEventListener("click", () => recordShopAction(appliance.applianceId, true, (lastShopResponse?.rounds.length ?? 1) - 1));
      pill.appendChild(tossButton);
    }

    shopOwnedAppliancesEl.appendChild(pill);
  }
}

// Request 2: "appliances you might already own" - a standalone panel for appliances whose shop
// eligibility is ownership-gated (ShopApplianceData.StapleWhenMissing, see
// ConditionalStapleApplianceDto's doc comment on the server), with a toggle button per appliance that
// sets ownership directly via recordManualOwnershipAction - deliberately decoupled from the shop grid's
// Buy flow above, so ownership can be simulated without needing the shop RNG to actually offer it.
function renderConditionalStapleAppliances(body) {
  shopConditionalStaplesEl.innerHTML = "";
  if (body.conditionalStapleAppliances.length === 0) {
    const p = document.createElement("p");
    p.textContent = "No ownership-gated appliances found.";
    shopConditionalStaplesEl.appendChild(p);
    return;
  }

  for (const appliance of body.conditionalStapleAppliances) {
    const pill = document.createElement("div");
    pill.className = "shop-slot";
    if (appliance.isOwned) {
      pill.classList.add("shop-slot-owned");
    }

    const img = document.createElement("img");
    img.src = `/images/appliances/${slugifyApplianceName(appliance.applianceName)}.png`;
    img.alt = "";
    img.onerror = () => img.remove();
    pill.appendChild(img);

    const name = document.createElement("span");
    name.textContent = appliance.applianceName;
    pill.appendChild(name);

    const toggleButton = document.createElement("button");
    toggleButton.type = "button";
    toggleButton.className = appliance.isOwned ? "link-button shop-action-badge" : "link-button";
    toggleButton.textContent = appliance.isOwned ? "✓ Owned" : "Mark as owned";
    toggleButton.title = appliance.isOwned ? "Click to un-mark as owned" : "Click to mark as owned";
    toggleButton.addEventListener("click", () => recordManualOwnershipAction(appliance.applianceId, !appliance.isOwned));
    pill.appendChild(toggleButton);

    shopConditionalStaplesEl.appendChild(pill);
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
// more rounds than are currently loaded) is the Search form's submit handler's job below.
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

// Request 4: explicit Search control, not auto-search-on-keystroke - typing alone no longer triggers
// a (sometimes network-bound) search, so old results stay visible/stable until the user deliberately
// re-searches or clears, and nothing resets mid-type.
shopFindForm.addEventListener("submit", (event) => {
  event.preventDefault();
  const query = shopFindInput.value.trim();
  // First real search this day-view: reuse the reroll machinery to ensure a useful default depth
  // (5 rounds: the initial offer + 4 rerolls) rather than only searching whatever happens to already
  // be on screen - lazy, same "only compute rounds the user actually asks to see" spirit as the
  // Explorer's existing tree-node convention.
  if (query && shopRerollScenarios.length < 4 && lastShownDay !== null) {
    addShopRerollRounds(4 - shopRerollScenarios.length);
    loadAndRenderShopDay();
  } else {
    applyFindHighlighting();
  }
});

shopFindClearButton.addEventListener("click", () => {
  shopFindInput.value = "";
  applyFindHighlighting(); // clears matches-find highlighting + shopFindResultEl, same as an empty query
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
