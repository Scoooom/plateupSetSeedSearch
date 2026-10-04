// Minimal push-only service worker for the "Notify on completion" search feature - see search.js.
// Deliberately does nothing else: no fetch handler, no offline caching, no manifest.json/PWA
// install behavior. Scope is "/" (the default for a worker registered from the site root), which
// covers both /search (where the notification was requested) and /search/cached#hash (where
// clicking it should land).

self.addEventListener("push", (event) => {
  let data = {};
  try {
    data = event.data ? event.data.json() : {};
  } catch {
    // Not valid JSON - fall through to the empty-object defaults below rather than failing the
    // whole push event over a malformed payload.
  }

  const title = data.title || "Seed search finished";
  const body = data.body || "Your search has finished.";
  const url = data.url || "/search";

  event.waitUntil(
    self.registration.showNotification(title, {
      body,
      data: { url },
      tag: data.jobId || undefined,
    })
  );
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const url = event.notification.data?.url || "/search";

  event.waitUntil(
    (async () => {
      const clientsList = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
      for (const client of clientsList) {
        if (new URL(client.url).pathname === new URL(url, self.location.origin).pathname && "focus" in client) {
          await client.focus();
          if ("navigate" in client) {
            await client.navigate(url);
          }
          return;
        }
      }
      await self.clients.openWindow(url);
    })()
  );
});
