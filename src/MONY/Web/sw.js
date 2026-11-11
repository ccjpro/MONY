/* The server supplies cacheName from the application version. */
const offlinePage = "/offline";
const staticAssets = new Set([
    "/overview-v5.css", "/overview-v4.js", "/locale-v4.js", "/alerts-v2.js", "/pwa.js", "/manifest.webmanifest",
    "/brand.svg", "/favicon.ico", "/favicon-32.png", "/apple-touch-icon-v2.png", "/pwa-192.png", "/pwa-512.png"
]);
const pages = new Set(["/", "/full", "/settings", "/login", offlinePage]);

self.addEventListener("install", event => {
    event.waitUntil((async () => {
        const cache = await caches.open(cacheName);
        await cache.addAll([...staticAssets, offlinePage].map(path => new Request(path, { cache: "reload", redirect: "error" })));
        await self.skipWaiting();
    })());
});

self.addEventListener("activate", event => {
    event.waitUntil((async () => {
        const keys = await caches.keys();
        await Promise.all(keys.filter(key => key.startsWith("mony-shell-") && key !== cacheName)
            .map(key => caches.delete(key)));
        await self.clients.claim();
    })());
});

async function navigate(request) {
    try {
        const response = await fetch(request);
        if (![502, 503, 504].includes(response.status) && !(response.status >= 520 && response.status <= 527))
            return response;
        return (await (await caches.open(cacheName)).match(offlinePage)) ?? response;
    } catch {
        return (await (await caches.open(cacheName)).match(offlinePage)) ?? Response.error();
    }
}

async function asset(request) {
    const cache = await caches.open(cacheName);
    return (await cache.match(new URL(request.url).pathname)) ?? fetch(request);
}

self.addEventListener("fetch", event => {
    const { request } = event;
    const url = new URL(request.url);
    if (request.method !== "GET" || url.origin !== self.location.origin) return;
    if (request.mode === "navigate" && pages.has(url.pathname))
        event.respondWith(navigate(request));
    else if (staticAssets.has(url.pathname))
        event.respondWith(asset(request));
});
