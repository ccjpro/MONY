"use strict";

const metrics = document.querySelector("#metrics");
const template = document.querySelector("#card");
const pause = document.querySelector("#pause");
const status = document.querySelector("#status");
const sampleTime = document.querySelector("#sample-time");
const statusLine = status.parentElement;
const identity = document.querySelector("#device-identity");
const partitionCard = document.querySelector(".volumes");
const loading = document.querySelector("#loading-metrics");
const overview = document.querySelector(".dashboard");
const heading = overview.querySelector(".heading");
const footer = overview.querySelector("footer");

const volumeRows = document.querySelector("#volume-rows");
const volumeTemplate = document.querySelector("#volume-row");
const volumesEmpty = document.querySelector("#volumes-empty");
const paths = {
    cpu: "M7 7h10v10H7zM9 2v3m6-3v3M9 19v3m6-3v3M2 9h3m-3 6h3m14-6h3m-3 6h3M10 10h4v4h-4z",
    memory: "M3 7h18v10H3zM6 10v3m4-3v3m4-3v3m4-3v3M6 17v2m4-2v2m4-2v2m4-2v2",
    gpu: "M3 5h18v12H3zM8 21h8m-4-4v4m-4-9h3l1-3 2 5 1-2h1",
    disk: "M3 6h18v12H3zM3 13h18m-5 2h2",
    network: "M8 3h8v5H8zM3 16h6v5H3zm12 0h6v5h-6zM12 8v4m-6 4v-4h12v4"
};
let previousVolumes;

let snapshot;
let online = false;
let changingView = false;
let previousTargets;
let revision = 0;

function fitCardRows() {
    const padding = getComputedStyle(overview);
    const reserved = heading.getBoundingClientRect().height + footer.getBoundingClientRect().height +
        parseFloat(getComputedStyle(heading).marginBottom) + parseFloat(getComputedStyle(footer).marginTop) +
        parseFloat(padding.paddingTop) + parseFloat(padding.paddingBottom);
    overview.style.setProperty("--overview-chrome", `${reserved}px`);
}

const overviewObserver = new ResizeObserver(fitCardRows);
overviewObserver.observe(heading);
overviewObserver.observe(footer);
fitCardRows();

function showLoading(active) {
    loading.hidden = !active;
    metrics.hidden = active;
}

function line(points, key, maximum, fill = false) {
    const width = 600;
    const bottom = 59;
    const step = (width - 2) / 59;
    let path = "";
    let endX = null;

    function endRun() {
        if (fill && endX !== null) path += `L${endX} ${bottom}Z`;
        endX = null;
    }

    for (const [index, point] of points.entries()) {
        const value = point[key];
        if (value == null) {
            endRun();
            continue;
        }

        const x = (1 + (60 - points.length + index) * step).toFixed(2);
        const y = (bottom - Math.max(0, Math.min(1, value / maximum)) * (bottom - 1)).toFixed(2);
        if (endX === null) {
            path += fill ? `M${x} ${bottom}L${x} ${y}` : `M${x} ${y}`;
        } else {
            path += `L${x} ${y}`;
        }
        endX = x;
    }
    endRun();
    return path;
}

function renderLabels(element, text, labels) {
    if (!labels) {
        element.textContent = text;
        return;
    }
    element.replaceChildren(...labels.map(label => {
        if (!label.series) return document.createTextNode(label.text);
        const span = document.createElement("span");
        span.className = `series-${label.series}`;
        span.textContent = label.text;
        return span;
    }));
}

function render(data) {
    i18n.setPreference(data.language);
    snapshot = data;
    document.documentElement.dataset.theme = data.webTheme;
    const targets = data.targets?.length ? data.targets : [{ id: "local",
        label: [data.computerName, data.computerIp].filter(Boolean).join(" · ") || i18n.t("此电脑") }];
    const serializedTargets = JSON.stringify(targets);
    if (serializedTargets !== previousTargets) {
        previousTargets = serializedTargets;
        identity.replaceChildren(...targets.map(target => new Option(target.label, target.id)));
    }
    identity.value = data.targetId ?? "local";
    identity.title = identity.selectedOptions[0]?.textContent ?? "";
    document.querySelector("#target-label").textContent = identity.title;
    identity.disabled = targets.length < 2;
    const message = data.paused ? i18n.t("已暂停") : data.targetId && data.targetId !== "local" ? data.status : "";
    status.textContent = message;
    statusLine.hidden = !message;
    sampleTime.textContent = data.sampleTime;
    sampleTime.hidden = !data.sampleTime;
    pause.querySelector(".button-label").textContent = data.paused ? i18n.t("继续") : i18n.t("暂停");
    pause.querySelector(".icon path").setAttribute("d", data.paused ? "M8 5l11 7-11 7z" : "M9 5v14M15 5v14");
    pause.setAttribute("aria-label", data.paused ? i18n.t("继续监控") : i18n.t("暂停监控"));
    const hasSample = data.hasSample ?? Boolean(data.sampleTime);
    showLoading(!hasSample);
    if (!hasSample) return;

    let cards = Array.from(metrics.children).filter(card => card.dataset.id);
    const metricsChanged = cards.length !== data.metrics.length ||
        data.metrics.some((metric, index) => cards[index]?.dataset.id !== metric.id);
    if (metricsChanged) {
        metrics.replaceChildren(...data.metrics.map(metric => {
            const card = template.content.firstElementChild.cloneNode(true);
            card.dataset.id = metric.id;
            return card;
        }), partitionCard);
        cards = Array.from(metrics.children).filter(card => card.dataset.id);
    }
    for (const [index, metric] of data.metrics.entries()) {
        const card = cards[index];
        card.querySelector(".metric-title").textContent = metric.title;
        const icon = metric.id === "cpu" || metric.id === "memory" || metric.id === "network"
            ? metric.id : metric.id.startsWith("disk-") ? "disk" : "gpu";
        card.querySelector(".icon path").setAttribute("d", paths[icon]);
        renderLabels(card.querySelector(".value"), metric.value, metric.valueLabels);
        const detail = card.querySelector(".detail");
        detail.textContent = metric.detail;
        detail.title = metric.detail;
        const telemetry = card.querySelector(".telemetry");
        renderLabels(telemetry, metric.telemetry ?? "", metric.telemetryLabels);
        telemetry.hidden = metric.telemetry === null;
        card.querySelector(".area").setAttribute("d", line(metric.points, "primary", metric.maximum, true));
        card.querySelector(".primary").setAttribute("d", line(metric.points, "primary", metric.maximum));
        const secondaryMaximum = metric.secondaryMaximum ?? metric.maximum;
        card.querySelector(".secondary").setAttribute("d", line(metric.points, "secondary", secondaryMaximum));
        card.querySelector(".tertiary").setAttribute("d", line(metric.points, "tertiary", secondaryMaximum));
    }
    const volumes = data.volumes ?? [];
    const serialized = JSON.stringify([document.documentElement.lang, volumes]);
    if (serialized !== previousVolumes) {
        previousVolumes = serialized;
        document.querySelector("#volume-count").textContent = i18n.count(volumes.length);
        volumesEmpty.hidden = volumes.length > 0;
        volumeRows.replaceChildren(...volumes.map(volume => {
            const row = volumeTemplate.content.firstElementChild.cloneNode(true);
            const name = `${volume.letter} · ${volume.name}`;
            const nameLabel = row.querySelector(".volume-name");
            nameLabel.title = nameLabel.textContent = name;
            row.querySelector(".volume-capacity").textContent = volume.totalCapacity;
            row.querySelector(".volume-usage").textContent = volume.usage ?? "";
            const progress = row.querySelector("progress");
            progress.value = volume.usedPercent;
            progress.setAttribute("aria-label", name + " " + i18n.t("已用容量"));
            return row;
        }));
    }
}

function disconnect() {
    online = false;
    status.textContent = navigator.onLine ? i18n.t("连接中断 · 正在重试") : i18n.t("离线 · 等待连接");
    statusLine.hidden = false;
    sampleTime.hidden = identity.disabled = true;
    showLoading(true);
}

async function request(path, options) {
    const signal = typeof AbortSignal.timeout === "function" ? AbortSignal.timeout(4000) : undefined;
    const response = await fetch(i18n.query(path), { ...options, signal });
    if (response.status === 401) location.replace("/login");
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    return response.json();
}

async function refresh() {
    if (!changingView) {
        const id = ++revision;
        try {
            const data = await request("/api/dashboard");
            if (id === revision) {
                if (location.pathname !== "/" && location.pathname !== "/offline") {
                    location.reload();
                    return;
                }
                online = true;
                render(data);
            }
        } catch {
            if (id === revision) disconnect();
        } finally {
            pause.disabled = !online || changingView;
        }
    }
    setTimeout(refresh, 1000);
}

async function changeView(path, body) {
    if (!snapshot || changingView) return;
    changingView = true;
    revision++;
    pause.disabled = identity.disabled = true;
    try {
        render(await request(path, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body)
        }));
    } catch {
        disconnect();
    } finally {
        changingView = false;
        pause.disabled = !online;
    }
}

pause.addEventListener("click", () => changeView("/api/pause", { paused: !snapshot?.paused }));
identity.addEventListener("change", () => changeView("/api/target", { id: identity.value }));

window.addEventListener("offline", () => {
    revision++;
    disconnect();
    pause.disabled = true;
});
refresh();
