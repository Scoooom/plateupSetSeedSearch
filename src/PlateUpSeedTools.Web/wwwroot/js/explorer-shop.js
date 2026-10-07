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
let shopRerollCount = 0;
let lastShopResponse = null;
let shopFetchGeneration = 0;

// Whether letters spawn inside the restaurant (vs. outside, near the front door) - mirrors the
// player's own live Pref.LettersSpawnInside game setting (a toggleable option, not seed-derived).
// Deliberately PERSISTS across day navigation (only resets on a full exploration reset) rather than
// resetting per day, since it's the player's actual game setting, not something that changes night
// to night. User-confirmed from real gameplay: this is the ONLY input that affects where a letter
// spawns - player position never matters, and a reroll never changes it (see
// Engine.Shop.ShopSpawnLocation's doc comment) - so this is one flat flag, not a per-round scenario.
let shopIsInside = true;

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
  shopRerollCount = 0;
  lastShopResponse = null;
  if (pathNode === null) {
    shopActionsByDay = []; // full exploration reset (resetExploration), not just a day change
    shopIsInside = true;
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
  loadAndRenderShopDay();
}

async function loadAndRenderShopDay() {
  if (lastShownDay === null || viewMode !== "blueprints") {
    return;
  }

  const myGeneration = ++shopFetchGeneration;
  shopDayHeadingEl.textContent = `Day ${lastShownDay}`;
  shopLocationSettingSelect.value = shopIsInside ? "inside" : "outside";
  setShopRoundsMessage(`Loading day ${lastShownDay}...`);

  const payload = {
    ...currentSetupPayload(),
    cardPath: pathToNode(lastShownPathNode),
    shopActions: pathToShopActions(),
    day: lastShownDay,
    rerollCount: shopRerollCount,
    isInside: shopIsInside,
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
    shopLocationResultEl.textContent = `Spawns: ${body.location}`;
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
  shopIsInside = shopLocationSettingSelect.value === "inside";
  loadAndRenderShopDay();
});

function renderShopRounds(body) {
  shopRoundsEl.innerHTML = "";
  for (const round of body.rounds) {
    shopRoundsEl.appendChild(renderShopRound(round));
  }
}

function renderShopRound(round) {
  const container = document.createElement("div");
  container.className = "shop-round";

  const heading = document.createElement("div");
  heading.className = "shop-round-heading";
  heading.textContent = round.rerollIndex === 0 ? "Initial offer" : `Reroll ${round.rerollIndex}`;
  container.appendChild(heading);

  const slotsEl = document.createElement("div");
  slotsEl.className = "shop-slots";
  for (const slot of round.slots) {
    slotsEl.appendChild(renderShopSlot(slot, round.rerollIndex));
  }
  container.appendChild(slotsEl);

  return container;
}

function renderShopSlot(slot, rerollIndex) {
  const el = document.createElement("div");
  el.className = "shop-slot";

  if (!slot.applianceName) {
    const empty = document.createElement("span");
    empty.textContent = "(nothing offered)";
    el.appendChild(empty);
    return el;
  }

  const img = document.createElement("img");
  img.src = `/images/appliances/${slugifyApplianceName(slot.applianceName)}.png`;
  img.alt = "";
  img.onerror = () => img.remove(); // most of the 451-appliance catalog has no icon - degrade to name-only, not a broken-image glyph
  el.appendChild(img);

  const name = document.createElement("span");
  name.textContent = slot.applianceName;
  el.appendChild(name);

  // "Buy" only on the initial offer - matches the real game (a slot about to be rerolled away isn't
  // purchasable), and keeps this simple rather than modeling "buy mid-reroll-sequence" edge cases.
  if (rerollIndex === 0) {
    const buyButton = document.createElement("button");
    buyButton.type = "button";
    buyButton.className = "link-button";
    buyButton.textContent = "Buy";
    buyButton.addEventListener("click", () => recordShopAction(slot.applianceId, false));
    el.appendChild(buyButton);
  }

  return el;
}

shopAddRerollButton.addEventListener("click", () => {
  shopRerollCount++;
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

    const tossButton = document.createElement("button");
    tossButton.type = "button";
    tossButton.className = "link-button";
    tossButton.textContent = "Toss";
    tossButton.addEventListener("click", () => recordShopAction(appliance.applianceId, true));
    pill.appendChild(tossButton);

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
  for (const el of shopRoundsEl.querySelectorAll(".shop-slot.matches-find")) {
    el.classList.remove("matches-find");
  }

  const query = shopFindInput.value.trim().toLowerCase();
  shopFindResultEl.innerHTML = "";
  if (!query || !lastShopResponse) {
    return;
  }

  for (const el of shopRoundsEl.querySelectorAll(".shop-slot")) {
    const name = el.querySelector("span")?.textContent ?? "";
    if (name.toLowerCase() === query) {
      el.classList.add("matches-find");
    }
  }

  const matchingRounds = lastShopResponse.rounds.filter((round) =>
    round.slots.some((slot) => slot.applianceName && slot.applianceName.toLowerCase() === query));

  if (matchingRounds.length > 0) {
    const descriptions = matchingRounds.map((round) =>
      round.rerollIndex === 0 ? "the initial offer" : `reroll ${round.rerollIndex}`);
    shopFindResultEl.textContent = `Found in: ${descriptions.join(", ")}. (Spawns: ${lastShopResponse.location.toLowerCase()})`;
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
    shopRerollCount += 5;
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
  if (query && shopRerollCount < 4 && lastShownDay !== null) {
    shopRerollCount = 4;
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
