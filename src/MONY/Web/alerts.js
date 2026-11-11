"use strict";

(() => {
    const storageKey = "mony-delivered-alerts";
    let inMemory = [];

    function seenIds() {
        const seen = new Set(inMemory);
        try {
            const stored = JSON.parse(localStorage.getItem(storageKey) ?? "[]");
            if (Array.isArray(stored)) for (const id of stored) seen.add(id);
        } catch { /* Use page-local delivery state when storage is unavailable. */ }
        return seen;
    }

    async function deliver(alert) {
        const seen = seenIds();
        if (seen.has(alert.id)) return;
        const options = { body: alert.message, icon: "/favicon-32.png", tag: "mony-" + alert.id };
        const worker = await navigator.serviceWorker?.getRegistration();
        if (worker) await worker.showNotification("MONY", options);
        else new Notification("MONY", options);
        seen.add(alert.id);
        inMemory = Array.from(seen).slice(-64);
        try { localStorage.setItem(storageKey, JSON.stringify(inMemory)); }
        catch { /* Retain page-local delivery state when storage is unavailable. */ }
    }

    async function poll() {
        try {
            if (window.isSecureContext && "Notification" in window && Notification.permission === "granted") {
                const response = await fetch(i18n.query("/api/alerts"), { cache: "no-store" });
                if (response.ok) {
                    for (const alert of await response.json()) {
                        if (navigator.locks) await navigator.locks.request("mony-alert-delivery", () => deliver(alert));
                        else await deliver(alert);
                    }
                }
            }
        } catch { /* The next poll retries transient network and notification failures. */ }
        finally { setTimeout(poll, 4000); }
    }

    poll();
})();
