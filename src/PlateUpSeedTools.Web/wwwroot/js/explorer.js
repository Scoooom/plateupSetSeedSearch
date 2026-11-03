const settingSelect = document.getElementById("setting-select");
const seedInput = document.getElementById("seed-input");
const dishSelect = document.getElementById("dish-select");
const subOptionField = document.getElementById("sub-option-field");
const subOption1Label = document.getElementById("sub-option-1-label");
const subOption2Label = document.getElementById("sub-option-2-label");
const heatSelect = document.getElementById("heat-select");
const cardPackSelect = document.getElementById("card-pack-select");
const tierInput = document.getElementById("tier-input");
const franchiseCardRowsEl = document.getElementById("franchise-card-rows");
const addFranchiseCardButton = document.getElementById("add-franchise-card");
const form = document.getElementById("explorer-form");
const statusEl = document.getElementById("status");
const explorerResultEl = document.getElementById("explorer-result");
const treeViewEl = document.getElementById("tree-view");
const dayPanelEl = document.getElementById("day-panel");
const deleteBranchButton = document.getElementById("delete-branch");
const copyLinkButton = document.getElementById("copy-link");
const saveFavoriteButton = document.getElementById("save-favorite");
const speedrunLinkEl = document.getElementById("speedrun-link");
const layoutPanelLink = document.getElementById("layout-panel-link");
const layoutPanelImage = document.getElementById("layout-panel-image");
const layoutPanelStats = document.getElementById("layout-panel-stats");

// VALID_SEED/validateSeed/fetchSettings/populateSettingSelect/createCardInput/setDatalistOptions/
// datalistFor come from shared.js.

let unlocksForSetting = [];

// The explored tree: each node is one committed decision (a card accepted, or an explicit skip) at
// a given day. rootNode is a virtual "before day 1" node. Going back to an earlier node and picking
// a different option creates a SIBLING node rather than overwriting - both branches stay reachable
// via the tree view, per design. Nothing beyond nodes the user has actually visited is ever
// computed - no precomputed full tree.
let rootNode = null;
let currentNode = null;
// The search query that led here, if this page was opened via a Search match's "Open this path in
// Seed Explorer" link - recovered once (sessionStorage, one-shot) in loadInit below, so a favorite
// saved from THIS page can still retain it (see favorites.js's stashOriginatingSearch).
let pendingOriginatingSearch = null;

// day: -1 so the first fetched frontier (day + 1) is day 0, the prep day before the first real
// night - only relevant for packs with a Day=0 UnlockConditionOnce (santa-workshop's Christmas
// Special Card) but harmless otherwise, same as the day-0 floor added engine/API-side.
function makeRootNode() {
  return { id: "root", day: -1, cardId: null, label: null, sourcePackName: null, rerolled: false, parent: null, children: new Map() };
}

function setStatus(message, isError) {
  statusEl.textContent = message;
  statusEl.classList.toggle("error", Boolean(isError));
}

// --- Setup form (setting/dish/sub-option/heat/franchise carryover) - same shape as search.js's
// equivalent fields. createCardInput/setDatalistOptions now live in shared.js (promoted there so
// both pages' card inputs get the same iOS-keyboard-safe suggestion dropdown - see
// createCardInput's comment in shared.js). ---

// Populated by refreshFranchiseRow, not synchronously - a franchise-carryover candidate list needs
// the copy-expanded ("Variety (copy 1)") names from /api/search/franchise-candidates, not the raw
// unlocksForSetting list, which has no copy-numbering at all (see fetchFranchiseCandidates/
// refreshFranchiseRow below).

function getFranchiseCardNames() {
  return [...franchiseCardRowsEl.querySelectorAll(".dynamic-row")]
    .map((row) => row.querySelector(".franchise-card").value)
    .filter(Boolean);
}

function getAllFranchiseRows() {
  return [...franchiseCardRowsEl.querySelectorAll(".dynamic-row")];
}

// Mirrors search.js's fetchFranchiseCandidates - see that file's comment on FranchiseCandidatesResponse
// for why this list is copy-expanded (repeatable cards like Variety get one "(copy N)" option per
// still-reachable copy) and filtered to internally-consistent picks (Requires/BlockedBy/duplicate/tier).
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

// Generation counter stashed directly on the input (Explorer has no shared per-row state map like
// search.js's getRowState) - guards against a slower earlier fetch overwriting a newer one.
async function refreshFranchiseRow(row) {
  if (!dishSelect.value) {
    return;
  }

  const input = row.querySelector(".franchise-card");
  const datalist = datalistFor(input);
  const myGeneration = (input._generation = (input._generation ?? 0) + 1);

  const otherSelectedCardNames = getAllFranchiseRows()
    .filter((other) => other !== row)
    .map((other) => other.querySelector(".franchise-card").value)
    .filter(Boolean);

  const cardNames = await fetchFranchiseCandidates(otherSelectedCardNames);

  if (input._generation !== myGeneration) {
    return; // a newer request for this row superseded this one - drop the stale result
  }

  setDatalistOptions(datalist, cardNames);
}

function refreshAllFranchiseRows() {
  for (const row of getAllFranchiseRows()) {
    refreshFranchiseRow(row);
  }
}

function addFranchiseCardRow() {
  const row = document.createElement("div");
  row.className = "dynamic-row";

  const { input, datalist, wrapper } = createCardInput("franchise-card", "explorer-franchise-card");
  input.addEventListener("change", () => {
    resetExploration();
    refreshAllFranchiseRows();
  });

  const removeButton = document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "link-button";
  removeButton.textContent = "Remove";
  removeButton.addEventListener("click", () => {
    row.remove();
    resetExploration();
    refreshAllFranchiseRows();
  });

  row.appendChild(wrapper);
  row.appendChild(datalist);
  row.appendChild(removeButton);
  franchiseCardRowsEl.appendChild(row);
  refreshFranchiseRow(row);
}

addFranchiseCardButton.addEventListener("click", addFranchiseCardRow);

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

  refreshAllFranchiseRows();

  updateSubOptionUi();
  resetExploration();
}

// Not setting-scoped - a card schedule is a selectable pack independent of restaurant setting (see
// GameDataRepository.ListCardSchedules's doc comment), loaded once like loadHeatLevels.
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
      continue;
    }
    const option = document.createElement("option");
    option.value = pack.slug;
    option.textContent = pack.displayName;
    cardPackSelect.appendChild(option);
  }
}

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

dishSelect.addEventListener("change", () => {
  updateSubOptionUi();
  resetExploration();
  refreshAllFranchiseRows();
});
heatSelect.addEventListener("change", () => {
  resetExploration();
  refreshAllFranchiseRows();
});
cardPackSelect.addEventListener("change", () => {
  resetExploration();
  refreshAllFranchiseRows();
});
tierInput.addEventListener("input", () => {
  resetExploration();
  refreshAllFranchiseRows();
});
settingSelect.addEventListener("change", () => {
  updateCardPackLock(settingSelect, cardPackSelect);
  loadUnlocksForSetting(settingSelect.value).catch(() => setStatus("Failed to load cards for this setting.", true));
});
seedInput.addEventListener("input", resetExploration);

// --- Exploration state/tree ---

// Any setup-field change invalidates whatever's been explored so far (a stale path replayed under
// a different setup could desync from what the server would actually say) - resetting just hides
// the result panel again until the user re-submits the form.
function resetExploration() {
  rootNode = null;
  currentNode = null;
  explorerResultEl.hidden = true;
  treeViewEl.innerHTML = "";
  dayPanelEl.innerHTML = "";
  layoutPanelImage.removeAttribute("src");
  layoutPanelLink.removeAttribute("href");
  layoutPanelStats.textContent = "";
}

// Layout (room shapes/appliance placement) depends only on setting+seed, never on the explored
// path/dish/heat/cards - reuses the same server-rendered PNG the Seed Previewer page shows
// (Rendering/LayoutImageRenderer.cs) rather than a second client-side renderer. Generation counter
// guards against a slow-to-load earlier request overwriting a later one if the user resubmits
// quickly (same pattern as search.js's per-row refresh state).
let layoutPreviewGeneration = 0;

async function loadLayoutPreview() {
  const setting = settingSelect.value;
  const seed = seedInput.value;
  const myGeneration = ++layoutPreviewGeneration;

  const pngUrl = `/preview/${encodeURIComponent(setting)}/${encodeURIComponent(seed)}.png`;
  const pageUrl = `/preview/${encodeURIComponent(setting)}/${encodeURIComponent(seed)}`;
  layoutPanelLink.href = pageUrl;
  layoutPanelStats.textContent = "Loading...";

  try {
    const response = await fetch(`/api/layout/${encodeURIComponent(setting)}/${encodeURIComponent(seed)}`);
    const layout = await response.json();
    if (myGeneration !== layoutPreviewGeneration) {
      return; // superseded by a newer request
    }

    if (!response.ok) {
      layoutPanelStats.textContent = layout.error ?? "Failed to load layout.";
      return;
    }

    // Preload off-DOM so the visible <img> only ever receives a fully-decoded image, same
    // flicker-avoidance approach as search.js's click-to-preview dialog.
    await new Promise((resolve) => {
      const preloadImg = new Image();
      preloadImg.onload = () => {
        if (myGeneration === layoutPreviewGeneration) {
          layoutPanelImage.src = pngUrl;
          layoutPanelImage.alt = `${layout.settingName} — seed "${layout.seed}"`;
        }
        resolve();
      };
      preloadImg.onerror = () => resolve();
      preloadImg.src = pngUrl;
    });

    if (myGeneration !== layoutPreviewGeneration) {
      return;
    }

    const tableCount = layout.appliances.filter((a) => a.applianceId === layout.tableApplianceId).length;
    const sizeSuffix = layout.mapSizeName ? `, ${layout.mapSizeName} layout` : "";
    layoutPanelStats.textContent = `${layout.width}×${layout.height} tiles, ${tableCount} dining table(s)${sizeSuffix}`;
  } catch {
    if (myGeneration === layoutPreviewGeneration) {
      layoutPanelStats.textContent = "Failed to reach the server.";
    }
  }
}

function pathToNode(node) {
  const entries = [];
  for (let n = node; n && n.parent; n = n.parent) {
    if (n.cardId !== null) {
      entries.push({ day: n.day, cardId: n.cardId, rerolled: n.rerolled });
    }
  }
  entries.reverse();
  return entries;
}

function currentSetupPayload() {
  const checkedSubOption = subOptionField.querySelector("input[name='sub-option']:checked");
  return {
    setting: settingSelect.value,
    seed: seedInput.value,
    dishName: dishSelect.value,
    subOptionChoice: subOptionField.hidden ? null : Number(checkedSubOption?.value ?? 1),
    heat: Number(heatSelect.value),
    tier: Number(tierInput.value),
    franchiseCardNames: getFranchiseCardNames(),
    cardPack: cardPackSelect.value || null,
  };
}

async function fetchExplorerDay(node, day, reroll = false) {
  try {
    const response = await fetch("/api/explorer/day", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...currentSetupPayload(), path: pathToNode(node), day, reroll }),
    });
    const body = await response.json();
    if (!response.ok) {
      setStatus(body.error ?? "Failed to load that day's offer.", true);
      return null;
    }
    return body;
  } catch {
    setStatus("Failed to reach the server.", true);
    return null;
  }
}

function nodeLabel(node) {
  return `Day ${node.day}: ${node.rerolled ? "🔄 " : ""}${node.label}`;
}

function renderTree() {
  deleteBranchButton.disabled = !currentNode || !currentNode.parent;
  copyLinkButton.disabled = !currentNode || !currentNode.parent;
  if (saveFavoriteButton) saveFavoriteButton.disabled = !currentNode || !currentNode.parent;

  const setupPayload = currentSetupPayload();
  speedrunLinkEl.href = buildSpeedRunToolsUrl({
    seed: setupPayload.seed,
    settingSlug: setupPayload.setting,
    cardPackSlug: setupPayload.cardPack,
    dishName: setupPayload.dishName,
    heat: setupPayload.heat,
    day: currentNode ? currentNode.day : null,
  }) + "&tab=blueprints";

  treeViewEl.innerHTML = "";
  if (!rootNode) {
    treeViewEl.textContent = "Nothing explored yet.";
    return;
  }

  const rootList = document.createElement("ul");
  rootList.className = "tree-list";
  appendVisibleChildren(rootNode, rootList);

  if (rootList.children.length === 0) {
    treeViewEl.textContent = "Nothing explored yet.";
    return;
  }

  treeViewEl.appendChild(rootList);
}

// Skipped days (node.cardId === null) are still tracked as real tree nodes internally - back-
// navigation/branching needs them - but per user feedback they're just noise in the tree view (a
// long run of empty days shouldn't produce a matching run of "Day N: skipped" lines). Walk straight
// through a skip node and attach ITS children to the nearest visible ancestor's list instead of
// rendering the skip node itself.
function appendVisibleChildren(node, parentList) {
  for (const child of node.children.values()) {
    if (child.cardId === null) {
      appendVisibleChildren(child, parentList);
      continue;
    }
    parentList.appendChild(renderTreeNode(child));
  }
}

function renderTreeNode(node) {
  const li = document.createElement("li");
  const button = document.createElement("button");
  button.type = "button";
  button.className = "link-button tree-node-button";
  button.textContent = nodeLabel(node);
  if (node === currentNode) {
    button.classList.add("active");
  }
  // Clicking a node re-shows THAT node's own day's offer (which card was picked among what was
  // offered), not what comes after it - walk forward from its PARENT (guaranteed to land back on
  // node.day, deterministically, same as when node was first created) rather than from node itself.
  // Highlight node itself as active, regardless of which node the walk internally passes through
  // (skip nodes) or ends on - previously this reused the walk's own internal pointer for both
  // purposes, which is what caused clicking a row to visibly land on/highlight a different one.
  button.addEventListener("click", () => viewNode(node.parent, node));
  li.appendChild(button);

  const childList = document.createElement("ul");
  childList.className = "tree-list";
  appendVisibleChildren(node, childList);
  if (childList.children.length > 0) {
    li.appendChild(childList);
  }

  return li;
}

// Bumped every time the user initiates a new navigation (viewNode) - lets an in-flight auto-skip
// loop (see renderFrontier) notice it's been superseded and stop, instead of a stale fetch
// clobbering whatever the user navigated to in the meantime.
let explorationGeneration = 0;

function getOrCreateChild(parentNode, day, option, sourcePackName, rerolled = false) {
  const key = option ? option.id : "skip";
  let child = parentNode.children.get(key);
  if (!child) {
    child = {
      id: `${parentNode.id}/${key}`,
      day,
      cardId: option ? option.id : null,
      label: option ? option.name : null,
      sourcePackName,
      rerolled: option ? rerolled : false, // a skip is never "rerolled" - nothing was picked
      parent: parentNode,
      children: new Map(),
    };
    parentNode.children.set(key, child);
  }
  return child;
}

// walkStart: the node whose next-day frontier should be fetched/shown (auto-skipping empty days).
// highlightNode: the node to mark "active" in the tree - the row the user actually clicked (or the
// newly-chosen node after a pick), NOT whatever internal node the auto-skip walk happens to stop at.
async function viewNode(walkStart, highlightNode) {
  const myGeneration = ++explorationGeneration;
  currentNode = highlightNode;
  renderTree();
  await renderFrontier(walkStart, myGeneration);
}

// Advances from `walkStart` day-by-day, silently auto-skipping any day that offers nothing (per
// user feedback: an empty day is just noise, not a real decision point) until it finds a day with
// real offers, or runs out of days. Each auto-skipped day still becomes a real tree node (so back-
// navigation/branching stays correct) - it's just not shown as an interactive step. Does NOT touch
// `currentNode`/tree highlighting - that's fixed once by the caller (viewNode) and must stay put
// regardless of which internal node this walk passes through or stops on.
async function renderFrontier(walkStart, myGeneration) {
  let current = walkStart;

  while (true) {
    const nextDay = current.day + 1;

    if (myGeneration === explorationGeneration) {
      dayPanelEl.innerHTML = "";
      const loading = document.createElement("p");
      loading.textContent = `Loading day ${nextDay}...`;
      dayPanelEl.appendChild(loading);
    }

    const response = await fetchExplorerDay(current, nextDay);
    if (myGeneration !== explorationGeneration || !response) {
      return; // stale (user navigated elsewhere while this was in flight) or failed
    }

    if (response.options.length === 0) {
      current = getOrCreateChild(current, nextDay, null, null);
      continue; // don't render this day at all - just move on to the next one
    }

    renderDayPanel(current, nextDay, response);
    return;
  }
}

function renderDayPanel(node, day, response, rerollView = false) {
  dayPanelEl.innerHTML = "";

  const heading = document.createElement("h2");
  heading.textContent = `Day ${day}`;
  dayPanelEl.appendChild(heading);

  if (response.sourcePackName) {
    const tag = document.createElement("p");
    tag.className = "pack-tag";
    tag.textContent = response.sourcePackName;
    dayPanelEl.appendChild(tag);
  }

  // Every ground-truth capture this project has ever done stops at day 15 (the last pinned
  // schedule event, the Franchise Cards Pack) - day 16+ is the day<=15 formula extrapolated
  // forward, not a verified overtime-day mechanic. Surface that once per overtime day, not just
  // in docs, since it materially changes how much to trust the result.
  if (day > 15) {
    const warning = document.createElement("p");
    warning.className = "field-note overtime-warning";
    warning.textContent =
      "Day 16+ is extrapolated from the day ≤15 schedule - not verified against real overtime-day gameplay.";
    dayPanelEl.appendChild(warning);
  }

  if (rerollView) {
    const note = document.createElement("p");
    note.className = "pack-tag";
    note.textContent = "🔄 Viewing the reroll offer (Bay Leaves)";
    dayPanelEl.appendChild(note);
  }

  const optionsEl = document.createElement("div");
  optionsEl.className = "day-options";
  for (const option of response.options) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "day-option-button";
    button.textContent = option.name;
    if (node.children.has(option.id)) {
      button.classList.add("already-explored");
    }
    button.addEventListener("click", () => choose(node, day, option, response.sourcePackName, rerollView));
    optionsEl.appendChild(button);
  }
  dayPanelEl.appendChild(optionsEl);

  // Models the Bay Leaves plant's 1-reroll-per-night mechanic - same skipChoices: 1 re-index into
  // the same pre-shuffled pool as the search feature's "Allow 1 reroll per day" flag, just viewed
  // interactively here instead of brute-forced. Toggles the CURRENT day's view only - never chains
  // (no Sage lifetime-cap rerolling-the-reroll).
  const rerollButton = document.createElement("button");
  rerollButton.type = "button";
  rerollButton.className = "link-button";
  rerollButton.textContent = rerollView ? "↩ Back to original offer" : "🔄 Reroll (Bay Leaves)";
  rerollButton.addEventListener("click", () => toggleReroll(node, day, !rerollView));
  // Reroll temporarily disabled - known-buggy skipChoices mechanic, see SearchEndpoints.
  // Un-hide (and drop the early return in toggleReroll) to re-enable.
  rerollButton.hidden = true;
  dayPanelEl.appendChild(rerollButton);

  const skipButton = document.createElement("button");
  skipButton.type = "button";
  skipButton.className = "link-button";
  skipButton.textContent = node.children.has("skip") ? "Skip (already explored)" : "Skip this offer";
  skipButton.addEventListener("click", () => choose(node, day, null, null, false));
  dayPanelEl.appendChild(skipButton);
}

async function toggleReroll(node, day, reroll) {
  // Reroll temporarily disabled - known-buggy skipChoices mechanic, see SearchEndpoints. The
  // reroll button is hidden (renderDayPanel), so nothing reaches this; kept as a defensive no-op.
  void node;
  void day;
  void reroll;
}

async function choose(parentNode, day, option, sourcePackName, rerolled = false) {
  const child = getOrCreateChild(parentNode, day, option, sourcePackName, rerolled);
  await viewNode(child, child);
}

// Prunes the currently-selected tree node (and everything under it) entirely - "mostly just for
// clearing the sidebar" per the user's own framing, not a gameplay action. Since offers are
// deterministic (same seed -> same result), this doesn't hide anything permanently: navigating back
// to the same point naturally re-derives the identical offer via the normal walk/fetch, it's just no
// longer cluttering the tree until re-explored.
function deleteCurrentBranch() {
  if (!currentNode || !currentNode.parent) {
    return; // nothing selected yet, or already at the synthetic root - nothing to delete
  }

  const parent = currentNode.parent;
  for (const [key, child] of parent.children) {
    if (child === currentNode) {
      parent.children.delete(key);
      break;
    }
  }

  viewNode(parent, parent);
}

deleteBranchButton.addEventListener("click", deleteCurrentBranch);

// Lazy by design: no /api/explorer/preload write happens until Copy Link or Save to Favorites is
// actually clicked - exploring a tree and never sharing/saving it never touches the cache DB.
// Idempotent per node: the hash is memoized on the tree node itself (node.shareHash) the first
// time either button mints one, so re-clicking while still on the same node (no re-navigation)
// reuses it instead of minting a new cache row every click.
async function ensureShareHash(node) {
  if (node.shareHash) {
    return node.shareHash;
  }

  const preload = { ...currentSetupPayload(), path: pathToNode(node) };
  delete preload.setting; // setting/seed travel as URL path segments, not part of the stored preload
  delete preload.seed;

  const response = await fetch("/api/explorer/preload", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(preload),
  });
  const body = await response.json();
  if (!response.ok) {
    setStatus(body.error ?? "Failed to create a shareable link.", true);
    return null;
  }
  node.shareHash = body.hash;
  return node.shareHash;
}

copyLinkButton.addEventListener("click", async () => {
  if (!currentNode || !currentNode.parent) return;

  try {
    const hash = await ensureShareHash(currentNode);
    if (!hash) return;

    const url = `${location.origin}/explorer/${encodeURIComponent(settingSelect.value)}/${encodeURIComponent(seedInput.value)}/${hash}`;
    await navigator.clipboard.writeText(url);
    setStatus(`Shareable link copied: ${url}`, false);
  } catch {
    setStatus("Failed to reach the server.", true);
  }
});

saveFavoriteButton?.addEventListener("click", async () => {
  if (!currentNode || !currentNode.parent) return;

  try {
    const hash = await ensureShareHash(currentNode);
    if (!hash) return;

    window.PlateUpFavorites?.saveFavoriteSeed({
      ...currentSetupPayload(),
      explorerHash: hash,
      originatingSearch: pendingOriginatingSearch,
    });
  } catch {
    setStatus("Failed to reach the server.", true);
  }
});

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  setStatus("", false);

  const seedError = validateSeed(seedInput.value);
  if (seedError) {
    setStatus(seedError, true);
    return;
  }

  if (!dishSelect.value) {
    setStatus("Choose a starting dish.", true);
    return;
  }

  rootNode = makeRootNode();
  explorerResultEl.hidden = false;
  loadLayoutPreview().catch(() => {});
  await viewNode(rootNode, null);
});

// Hotlinking: /explorer/<setting>/<seed> prefills just those two fields (matching /preview's
// pattern) - dish/heat/tier/franchise cards and the explored tree normally start blank, since a
// mid-exploration path isn't meaningfully URL-shaped BY ITSELF. A fourth segment,
// /explorer/<setting>/<seed>/<hash>, is the exception: a short opaque token (never the path/setup
// data itself - see ExplorerPreload's doc comment) that resolves via GET /api/explorer/preload/
// {hash} to a full setup + path, which applyPreload below replays.
function paramsFromPath() {
  const segments = window.location.pathname.split("/").filter(Boolean);
  return { setting: segments[1] ?? null, seed: segments[2] ?? null, hash: segments[3] ?? null };
}

// Prefills the setup fields from a resolved ExplorerPreload, then replays its path day-by-day
// against the live server (same fetchExplorerDay/getOrCreateChild primitives choose() uses) rather
// than synthesizing tree nodes client-side - consistent with this page's "server is the source of
// truth" design. Stops early (keeping whatever replayed successfully) if a step's card turns out
// not to be offered, rather than hard-failing the whole page.
async function applyPreload(preload) {
  dishSelect.value = preload.dishName;
  updateSubOptionUi();
  if (preload.subOptionChoice) {
    const radio = subOptionField.querySelector(`input[value="${preload.subOptionChoice}"]`);
    if (radio) radio.checked = true;
  }
  heatSelect.value = String(preload.heat);
  tierInput.value = String(preload.tier);
  updateCardPackLock(settingSelect, cardPackSelect);
  if (!cardPackSelect.disabled) {
    cardPackSelect.value = preload.cardPack ?? "";
  }

  franchiseCardRowsEl.innerHTML = "";
  for (const name of preload.franchiseCardNames ?? []) {
    addFranchiseCardRow();
    const rows = franchiseCardRowsEl.querySelectorAll(".dynamic-row");
    rows[rows.length - 1].querySelector(".franchise-card").value = name;
  }

  rootNode = makeRootNode();
  explorerResultEl.hidden = false;
  loadLayoutPreview().catch(() => {});

  let node = rootNode;
  for (const step of preload.path ?? []) {
    const rerolled = Boolean(step.rerolled);
    const response = await fetchExplorerDay(node, step.day, rerolled);
    if (!response) break;
    const option = response.options.find((o) => o.id === step.cardId);
    if (!option) break;
    node = getOrCreateChild(node, step.day, option, response.sourcePackName, rerolled);
  }

  await viewNode(node, node);
}

// Same object Copy Link builds (see copyLinkButton's handler above), just kept self-contained
// (setting/seed included) instead of stripped for URL-path travel - the exported blob has to carry
// everything on its own.
function buildExplorerExportPayload() {
  return { ...currentSetupPayload(), path: pathToNode(currentNode) };
}

// Restores setting/seed/setup from an imported/pasted blob, then replays its path - the same
// three-call sequence loadInit uses for the hash-based Copy Link flow, just triggered by a pasted
// string instead of a URL segment.
async function importExplorerSettings(preload) {
  if (!preload || typeof preload !== "object" || !preload.setting || !preload.seed) {
    throw new Error("Missing setting/seed in imported settings.");
  }

  settingSelect.value = preload.setting;
  seedInput.value = preload.seed;
  updateCardPackLock(settingSelect, cardPackSelect);
  await loadUnlocksForSetting(settingSelect.value);
  await applyPreload(preload); // sets its own status on a failed replay step
}

async function loadInit() {
  const settings = await fetchSettings();
  const { setting: settingFromUrl, seed: seedFromUrl, hash } = paramsFromPath();
  populateSettingSelect(settingSelect, settings, settingFromUrl);
  if (seedFromUrl && !validateSeed(seedFromUrl)) {
    seedInput.value = seedFromUrl;
  }
  await loadUnlocksForSetting(settingSelect.value);
  await loadHeatLevels();
  await loadCardPacks();
  updateCardPackLock(settingSelect, cardPackSelect);

  if (hash) {
    try {
      const response = await fetch(`/api/explorer/preload/${encodeURIComponent(hash)}`);
      if (!response.ok) {
        setStatus("This shared Explorer link doesn't exist.", true);
        return;
      }
      await applyPreload(await response.json());
      // One-shot handoff from a Search match's "Open this path in Seed Explorer" link, if that's
      // how this page was reached - see favorites.js's stashOriginatingSearch/consumeOriginatingSearch.
      pendingOriginatingSearch = window.PlateUpFavorites?.consumeOriginatingSearch(hash) ?? null;
    } catch {
      setStatus("Failed to reach the server.", true);
    }
  }
}

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
  buildExport: buildExplorerExportPayload,
  applyImport: importExplorerSettings,
});

loadInit().catch(() => setStatus("Failed to load restaurant settings.", true));
