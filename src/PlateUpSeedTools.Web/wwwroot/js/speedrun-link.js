// Builds a deep link into daemonus/@candigotchi's Speed Run Tools
// (https://daemonus.github.io/plateup-speed-run-tools/) for a given seed/setting/dish/heat/day.
// Their site documented accepting our own site slugs directly for `setting` (no translation
// table needed), and buckets any raw heat integer into its own Off/Chill/Heat2/Heat10 display -
// so every field here is a plain passthrough of this app's own values. `fcc` (their Turbo-only
// "Food Card Challenge" auto-pick display toggle) has no equivalent concept in this app and is
// never set.
function buildSpeedRunToolsUrl({ seed, settingSlug, cardPackSlug, dishName, heat, day }) {
  const base = "https://daemonus.github.io/plateup-speed-run-tools";
  const effectiveSetting = cardPackSlug && cardPackSlug !== "normal" ? cardPackSlug : settingSlug;
  const isTurbo = effectiveSetting === "turbo";
  const url = new URL(`${base}/${isTurbo ? "turbo" : "custom-seed"}`);
  if (seed) url.searchParams.set("seed", seed);
  if (!isTurbo && effectiveSetting) url.searchParams.set("setting", effectiveSetting);
  if (dishName) url.searchParams.set("dish", dishName);
  if (heat !== null && heat !== undefined && !Number.isNaN(heat)) {
    url.searchParams.set("heat", String(heat));
  }
  if (day !== null && day !== undefined && day >= 2 && day <= 15) {
    url.searchParams.set("day", String(day));
  }
  return url.toString();
}
