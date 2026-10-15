// Shared "Favorites" drawer: Favorite Seeds (Explorer navigation saves, reload the exact
// dish/heat/tier/card-path state) + Saved Searches (a personal, nicknamed, 7-day-retained copy of
// a completed search's results - distinct from "Copy Link", which is permanent/unnamed). Entirely
// localStorage-based - there's no account system in this app, so the list + nicknames live only in
// this browser, same as plateup:recentSearches in search.js. The server only ever stores
// anonymous, hash-keyed blobs (ExplorerPreload / SavedSearchResult), never a nickname.
//
// Loaded last on every page (index/preview/search/explorer.html) and injects its own trigger
// button + drawer markup at runtime, so none of those 4 hand-duplicated HTML files need a
// hand-duplicated copy of the drawer itself - only the one <script> tag each.

const FAVORITE_SEEDS_KEY = "plateup:favoriteSeeds";
const SAVED_SEARCHES_KEY = "plateup:savedSearches";
const MAX_FAVORITE_SEEDS = 200;
const MAX_SAVED_SEARCHES = 200;

function loadList(key) {
  try {
    const raw = localStorage.getItem(key);
    return raw ? JSON.parse(raw) : [];
  } catch {
    return [];
  }
}

function saveList(key, entries) {
  try {
    localStorage.setItem(key, JSON.stringify(entries));
  } catch {
    // localStorage unavailable (private browsing, storage full, ...) - the list just won't
    // persist across reloads; still works for the current page view.
  }
}

function makeId() {
  return `${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
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

// --- One-shot handoffs - NOT a durable list like the two localStorage keys above, but still
// backed by localStorage rather than sessionStorage. Used for e.g. carrying a Search query across
// a page navigation into the Explorer tab opened from it, or carrying a locally-cached snapshot
// into the Search page for a Saved Search whose server-side copy has expired.
//
// sessionStorage would be the more obviously-scoped choice (auto-clears per tab, no manual
// cleanup) - deliberately NOT used: the Explorer-link handoff's real delivery mechanism is
// `window.open(url, "_blank", "noopener")` (see wireExplorerLink in search.js), and `noopener`
// means the new tab is never established as "auxiliary" to its opener at all - per spec, session
// storage is only cloned into a new top-level browsing context when it IS auxiliary, so a
// `noopener`-opened tab's sessionStorage is simply empty, confirmed empirically in this browser.
// `noopener` is a deliberate existing choice elsewhere in this app (not something to remove just
// to make this one feature work), so this uses localStorage instead, which is scoped per-origin
// rather than per-browsing-context and is unaffected by the opener relationship. Still a one-shot:
// consumeHandoff removes the key immediately on read, same semantics as before, just reachable
// from a page that has no opener link to where it was stashed.

function stashHandoff(key, data) {
  try {
    localStorage.setItem(`plateup:${key}`, JSON.stringify(data));
  } catch {
    // best-effort
  }
}

function consumeHandoff(key) {
  try {
    const fullKey = `plateup:${key}`;
    const raw = localStorage.getItem(fullKey);
    if (raw === null) return null;
    localStorage.removeItem(fullKey);
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function stashOriginatingSearch(explorerHash, request) {
  stashHandoff(`pendingExplorerOrigin:${explorerHash}`, request);
}

function consumeOriginatingSearch(explorerHash) {
  return consumeHandoff(`pendingExplorerOrigin:${explorerHash}`);
}

// --- Drawer DOM, injected at runtime (see file header) ---

let drawerEl = null;
let overlayEl = null;
let favoriteSeedsListEl = null;
let savedSearchesListEl = null;

function buildDrawer() {
  overlayEl = document.createElement("div");
  overlayEl.className = "favorites-drawer-overlay";
  overlayEl.addEventListener("click", closeDrawer);

  drawerEl = document.createElement("aside");
  drawerEl.className = "favorites-drawer";
  drawerEl.innerHTML = `
    <div class="favorites-drawer-header">
      <strong>Favorites</strong>
      <button type="button" class="link-button favorites-drawer-close">Close</button>
    </div>
    <section class="favorites-section">
      <h3>Favorite Seeds</h3>
      <ul class="favorites-list favorite-seeds-list"></ul>
      <p class="favorites-empty-note favorite-seeds-empty" hidden>No favorite seeds saved yet.</p>
    </section>
    <section class="favorites-section">
      <h3>Saved Searches</h3>
      <p class="favorites-section-note">Kept for 7 days on the server; still viewable afterward if you opened it at least once before then.</p>
      <ul class="favorites-list saved-searches-list"></ul>
      <p class="favorites-empty-note saved-searches-empty" hidden>No saved searches yet.</p>
    </section>
  `;

  favoriteSeedsListEl = drawerEl.querySelector(".favorite-seeds-list");
  savedSearchesListEl = drawerEl.querySelector(".saved-searches-list");
  drawerEl.querySelector(".favorites-drawer-close").addEventListener("click", closeDrawer);

  document.body.appendChild(overlayEl);
  document.body.appendChild(drawerEl);
}

function buildTrigger() {
  // Appended inside #main-nav (not as a sibling of it) so it gets the same pill styling as the
  // other nav links and collapses into the mobile hamburger menu along with them, instead of
  // needing its own responsive handling.
  const nav = document.getElementById("main-nav");
  if (!nav) return;
  const trigger = document.createElement("button");
  trigger.type = "button";
  trigger.className = "favorites-trigger";
  trigger.textContent = "☆ Favorites";
  trigger.addEventListener("click", openDrawer);
  nav.appendChild(trigger);
}

function openDrawer() {
  renderFavoriteSeeds();
  renderSavedSearches();
  overlayEl.classList.add("open");
  drawerEl.classList.add("open");
}

function closeDrawer() {
  overlayEl.classList.remove("open");
  drawerEl.classList.remove("open");
}

// --- Shared row renderer: a nicknamed button (opens the entry) + rename/remove actions, modeled
// on search.js's renderRecentSearches list-item shape.

function renderEntryRow({ nickname, subtitle, onOpen, onRename, onRemove }) {
  const li = document.createElement("li");

  const openButton = document.createElement("button");
  openButton.type = "button";
  openButton.className = "link-button favorites-open-btn";
  openButton.title = subtitle;
  openButton.textContent = nickname;
  openButton.addEventListener("click", onOpen);
  li.appendChild(openButton);

  const actions = document.createElement("span");
  actions.className = "favorites-row-actions";

  const renameButton = document.createElement("button");
  renameButton.type = "button";
  renameButton.className = "link-button";
  renameButton.textContent = "Rename";
  renameButton.addEventListener("click", () => {
    const next = window.prompt("Rename:", nickname);
    if (next !== null && next.trim()) {
      onRename(next.trim());
    }
  });

  const removeButton = document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "link-button";
  removeButton.textContent = "Remove";
  removeButton.addEventListener("click", onRemove);

  actions.append(renameButton, removeButton);
  li.appendChild(actions);
  return li;
}

// --- Favorite Seeds ---

function loadFavoriteSeeds() {
  return loadList(FAVORITE_SEEDS_KEY);
}

function saveFavoriteSeeds(entries) {
  saveList(FAVORITE_SEEDS_KEY, entries);
}

function favoriteSeedUrl(entry) {
  const base = `/explorer/${encodeURIComponent(entry.setting)}/${encodeURIComponent(entry.seed)}`;
  return entry.explorerHash ? `${base}/${entry.explorerHash}` : base;
}

function favoriteSeedSubtitle(entry) {
  const bits = [entry.setting];
  if (entry.dishName) bits.push(entry.dishName);
  if (entry.heat !== null && entry.heat !== undefined) bits.push(`Heat ${entry.heat}`);
  bits.push(formatRelativeTime(entry.savedAt));
  return `${entry.seed} · ${bits.join(" · ")}`;
}

function renderFavoriteSeeds() {
  const entries = loadFavoriteSeeds();
  favoriteSeedsListEl.innerHTML = "";
  drawerEl.querySelector(".favorite-seeds-empty").hidden = entries.length > 0;

  for (const entry of entries) {
    favoriteSeedsListEl.appendChild(
      renderEntryRow({
        nickname: entry.nickname,
        subtitle: favoriteSeedSubtitle(entry),
        onOpen: () => {
          window.location.href = favoriteSeedUrl(entry);
        },
        onRename: (nickname) => {
          entry.nickname = nickname;
          saveFavoriteSeeds(entries);
          renderFavoriteSeeds();
        },
        onRemove: () => {
          saveFavoriteSeeds(entries.filter((e) => e.id !== entry.id));
          renderFavoriteSeeds();
        },
      })
    );
  }
}

// entry: { setting, seed, explorerHash, dishName, subOptionChoice, heat, tier, franchiseCardNames,
// cardPack, originatingSearch } - explorerHash/dishName/etc may be null (a plain Preview-page save
// with no dish/card-path concept); originatingSearch may be null (favorited without a Search match
// involved). Prompts for a nickname; cancelling the prompt (Escape, or the dialog's Cancel button)
// aborts the save entirely rather than saving with a blank name.
function saveFavoriteSeed(entry) {
  const defaultNickname = entry.dishName ? `${entry.dishName} · ${entry.seed}` : entry.seed;
  const nickname = window.prompt("Nickname this favorite seed:", defaultNickname);
  if (nickname === null) return;

  const entries = loadFavoriteSeeds();
  entries.unshift({
    id: makeId(),
    nickname: nickname.trim() || defaultNickname,
    savedAt: new Date().toISOString(),
    setting: entry.setting,
    seed: entry.seed,
    explorerHash: entry.explorerHash ?? null,
    dishName: entry.dishName ?? null,
    subOptionChoice: entry.subOptionChoice ?? null,
    heat: entry.heat ?? null,
    tier: entry.tier ?? null,
    franchiseCardNames: entry.franchiseCardNames ?? null,
    cardPack: entry.cardPack ?? null,
    originatingSearch: entry.originatingSearch ?? null,
  });
  saveFavoriteSeeds(entries.slice(0, MAX_FAVORITE_SEEDS));
  openDrawer();
}

// --- Saved Searches ---

function loadSavedSearches() {
  return loadList(SAVED_SEARCHES_KEY);
}

function saveSavedSearches(entries) {
  saveList(SAVED_SEARCHES_KEY, entries);
}

function renderSavedSearches() {
  const entries = loadSavedSearches();
  savedSearchesListEl.innerHTML = "";
  drawerEl.querySelector(".saved-searches-empty").hidden = entries.length > 0;

  for (const entry of entries) {
    const subtitle = `${entry.request?.dishName ?? "?"} / ${entry.request?.setting ?? "?"} · ${entry.matchCount} match(es) · ${formatRelativeTime(entry.savedAt)}${entry.localSnapshot ? " · local snapshot saved" : ""}`;
    savedSearchesListEl.appendChild(
      renderEntryRow({
        nickname: entry.nickname,
        subtitle,
        onOpen: () => {
          // Hand the local snapshot (if any) to the Search page via a one-shot handoff, in case
          // the server-side copy has already expired by the time it loads there. localSnapshot
          // itself doesn't carry `request` (that lives on this entry, not duplicated into the
          // snapshot at upgrade time) - merge it back in so the Search page's render path, which
          // expects the same shape GET /api/search/saved/{hash} returns, gets a complete object.
          if (entry.localSnapshot) {
            stashHandoff(`pendingSavedSearchSnapshot:${entry.hash}`, {
              ...entry.localSnapshot,
              request: entry.request,
            });
          }
          window.location.href = `/search?saved=${encodeURIComponent(entry.hash)}`;
        },
        onRename: (nickname) => {
          entry.nickname = nickname;
          saveSavedSearches(entries);
          renderSavedSearches();
        },
        onRemove: () => {
          saveSavedSearches(entries.filter((e) => e.id !== entry.id));
          renderSavedSearches();
        },
      })
    );
  }
}

// entry: { hash, request, matchCount } - hash comes from POST /api/search/{jobId}/save or
// /api/search/save-from-cached/{hash}. localSnapshot starts null; it's only populated the first
// time this search is actually viewed again while still live server-side (see
// upgradeSavedSearchSnapshot), which is what lets it still render (marked stale) after the
// server-side 7-day copy expires.
function saveSearch(entry) {
  const defaultNickname = `${entry.request?.dishName ?? "Search"} / ${entry.request?.setting ?? ""}`.trim();
  const nickname = window.prompt("Nickname this saved search:", defaultNickname);
  if (nickname === null) return;

  const entries = loadSavedSearches();
  entries.unshift({
    id: makeId(),
    nickname: nickname.trim() || defaultNickname,
    savedAt: new Date().toISOString(),
    hash: entry.hash,
    request: entry.request,
    matchCount: entry.matchCount,
    localSnapshot: null,
  });
  saveSavedSearches(entries.slice(0, MAX_SAVED_SEARCHES));
  openDrawer();
}

// Called by search.js once it successfully fetches GET /api/search/saved/{hash} while it's still
// live server-side - this is the "if you've viewed it before it expired" upgrade that lets the
// saved entry keep showing something after the server-side row is swept.
function upgradeSavedSearchSnapshot(hash, body) {
  const entries = loadSavedSearches();
  const entry = entries.find((e) => e.hash === hash);
  if (!entry) return;
  entry.localSnapshot = {
    cachedAt: new Date().toISOString(),
    matches: body.matches,
    totalToCheck: body.totalToCheck,
    checkedCount: body.checkedCount,
    matchCapReached: body.matchCapReached,
    timedOut: body.timedOut,
  };
  saveSavedSearches(entries);
}

// --- Init ---

buildDrawer();
buildTrigger();

window.PlateUpFavorites = {
  saveFavoriteSeed,
  saveSearch,
  stashOriginatingSearch,
  consumeOriginatingSearch,
  stashHandoff,
  consumeHandoff,
  upgradeSavedSearchSnapshot,
};
