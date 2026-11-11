"use strict";

const field = selector => document.querySelector(selector);
const viewport = field("#viewport");
const stage = field("#stage");
const status = field("#full-status");
let previousVolumes;
let viewportSize = "";
let inventory = "";
let boardScale = 1;
let layoutFrame = 0;
let previousAccent;

function point(fraction, radius, centerY = 220) {
    const angle = (150 + fraction * 240) * Math.PI / 180;
    return [240 + Math.cos(angle) * radius, centerY + Math.sin(angle) * radius];
}

function arc(fraction, radius = 190, centerY = 220) {
    return Array.from({ length: 81 }, (_, i) => (i ? "L" : "M") +
        point(fraction * i / 80, radius, centerY).map(v => v.toFixed(2)).join(" ")).join("");
}

function svgElement(name, attributes) {
    const element = document.createElementNS("http://www.w3.org/2000/svg", name);
    for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, value);
    return element;
}

field(".dial-track").setAttribute("d", arc(1));
field(".frequency-track").setAttribute("d", arc(1, 55, 282));
for (let i = 0; i <= 20; i++) {
    const outer = point(i / 20, 204);
    const inner = point(i / 20, i % 4 === 0 ? 180 : 192);
    field(".dial-ticks").append(svgElement("line", { x1: inner[0], y1: inner[1], x2: outer[0], y2: outer[1], "stroke-width": i % 4 === 0 ? 2 : 1 }));
    if (i % 4 === 0) {
        const p = point(i / 20, 157);
        const label = svgElement("text", { x: p[0], y: p[1] });
        label.textContent = String(i * 5);
        field(".dial-labels").append(label);
    }
}
for (let i = 0; i < 20; i++) field(".fuel-segments").append(document.createElement("i"));

function sizeBoard(width, height) {
    stage.style.setProperty("--board-width", `${width / boardScale}px`);
    stage.style.setProperty("--board-height", `${height / boardScale}px`);
    stage.style.setProperty("--board-scale", boardScale);
}

function fit() {
    layoutFrame = 0;
    const width = viewport.clientWidth;
    const height = viewport.clientHeight;
    if (!width || !height) return;

    const size = `${width}/${height}/${inventory}`;
    if (size !== viewportSize) {
        viewportSize = size;
        const portrait = width / height < 0.88;
        stage.classList.toggle("portrait", portrait);
        boardScale = Math.min(1, width / (portrait ? 560 : 900), height / (portrait ? 900 : 680));
        sizeBoard(width, height);
    }

    if (stage.offsetHeight * boardScale > height + 1) {
        boardScale = height / stage.offsetHeight;
        sizeBoard(width, height);
    }
}

function queueLayout() {
    if (!layoutFrame) layoutFrame = requestAnimationFrame(fit);
}

const observer = new ResizeObserver(queueLayout);
observer.observe(viewport);
observer.observe(stage);
queueLayout();

function line(points, key, maximum) {
    let result = "";
    let run = false;
    for (const [index, point] of points.entries()) {
        const value = point[key];
        if (value == null) { run = false; continue; }
        const x = (1 + (60 - points.length + index) * 598 / 59).toFixed(2);
        const y = (59 - Math.max(0, Math.min(1, value / Math.max(1, maximum))) * 58).toFixed(2);
        result += `${run ? "L" : "M"}${x} ${y}`;
        run = true;
    }
    return result;
}

function trend(element, metric) {
    for (const [className, key, maximum] of [
        ["primary", "primary", metric.maximum],
        ["secondary", "secondary", metric.secondaryMaximum ?? metric.maximum],
        ["tertiary", "tertiary", metric.secondaryMaximum ?? metric.maximum]
    ]) element.querySelector(`.trend .${className}`)?.setAttribute("d", line(metric.points, key, maximum));
}

function segments(container, value) {
    for (const [i, segment] of Array.from(container.children).entries())
        segment.classList.toggle("active", value != null && i < Math.ceil(value / 5));
}

function reconcile(container, metrics, templateId) {
    if (container.children.length === metrics.length && metrics.every((metric, i) => container.children[i].dataset.id === metric.id)) return;
    const template = field(templateId);
    container.replaceChildren(...metrics.map(metric => {
        const item = template.content.firstElementChild.cloneNode(true);
        item.dataset.id = metric.id;
        const ruler = item.querySelector(".load-ruler");
        if (ruler) for (let i = 0; i < 20; i++) ruler.append(document.createElement("i"));
        return item;
    }));
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

function applyAccent(accent) {
    if (!accent || previousAccent === accent.base + accent.dark + accent.light) return;
    previousAccent = accent.base + accent.dark + accent.light;
    const root = document.documentElement.style;
    const isHex = value => typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value);
    if (isHex(accent.dark)) root.setProperty("--windows-accent-dark", accent.dark);
    if (isHex(accent.light)) root.setProperty("--windows-accent-light", accent.light);
    if (isHex(accent.base)) root.setProperty("--windows-accent-rgb",
        accent.base.slice(1).match(/../g).map(value => Number.parseInt(value, 16)).join(" "));
}

function render(data) {
    applyAccent(data.accent);
    i18n.setPreference(data.language);
    document.documentElement.dataset.theme = data.webTheme;
    const message = data.targetId && data.targetId !== "local" && !data.hasSample ? data.status : "";
    status.textContent = message;
    status.hidden = !message;
    stage.classList.toggle("waiting", Boolean(message));
    const signature = document.documentElement.lang + "/" + data.metrics.map(metric => metric.id).join("|") + "/" + (data.volumes?.length ?? 0);
    if (inventory !== signature) {
        inventory = signature;
        queueLayout();
    }
    field("#version").textContent = data.version;
    for (const id of ["cpu", "memory"]) {
        const metric = data.metrics.find(item => item.id === id);
        if (!metric) continue;
        const element = field(`#${id}`);
        element.querySelector(".device-detail").textContent = metric.detail;
        trend(element, metric);
        const latest = metric.points.at(-1)?.primary;
        if (id === "cpu") {
            element.querySelector(".hero-value").textContent = metric.value.replace("%", "");
            element.querySelector(".dial-unit").hidden = latest == null;
            const frequency = data.cpuFrequencyMHz > 0 ? data.cpuFrequencyMHz / 1000 : null;
            field("#cpu-frequency").textContent = frequency === null ? "—" : frequency.toFixed(2) + " GHz";
            field(".frequency-progress").setAttribute("d", frequency === null ? "" :
                arc(frequency / Math.max(6, Math.ceil(frequency)), 55, 282));
            const fraction = Math.max(0, Math.min(1, (latest ?? 0) / 100));
            element.querySelector(".dial-progress").setAttribute("d", latest == null ? "" : arc(fraction));
            const cursor = element.querySelector(".dial-cursor");
            cursor.style.visibility = latest == null ? "hidden" : "visible";
            const start = point(fraction, 175), end = point(fraction, 214);
            for (const [name, v] of Object.entries({ x1: start[0], y1: start[1], x2: end[0], y2: end[1] })) cursor.setAttribute(name, v);
            const telemetry = element.querySelector(".cpu-telemetry");
            telemetry.replaceChildren(...(metric.telemetry ?? "").split(" · ").map(text => {
                const span = document.createElement("span"); span.textContent = text; return span;
            }));
        } else {
            element.querySelector(".memory-value").textContent = metric.value;
            segments(element.querySelector(".fuel-segments"), latest);
        }
    }
    const gpus = data.metrics.filter(metric => metric.id !== "cpu" && metric.id !== "memory" && metric.id !== "network" && !metric.id.startsWith("disk-"));
    const disks = data.metrics.filter(metric => metric.id.startsWith("disk-"));
    for (const [kind, metrics] of [["gpu", gpus], ["disk", disks]]) {
        const list = field(`#${kind}-list`);
        reconcile(list, metrics, `#${kind}-template`);
        field(`#${kind}-count`).textContent = i18n.count(metrics.length);
        field(`#${kind}-empty`).hidden = metrics.length > 0;
        for (const [index, metric] of metrics.entries()) {
            const element = list.children[index];
            element.querySelector("h3").textContent = metric.title;
            renderLabels(element.querySelector(".reading"), metric.value, metric.valueLabels);
            element.querySelector(".device-detail").textContent = metric.detail;
            renderLabels(element.querySelector(".telemetry"), metric.telemetry ?? "", metric.telemetryLabels);
            const ruler = element.querySelector(".load-ruler");
            if (ruler) segments(ruler, metric.points.at(-1)?.primary);
            trend(element, metric);
        }
    }
    const network = data.metrics.find(metric => metric.id === "network");
    if (network) {
        const element = field("#network");
        element.querySelector(".download").textContent = network.valueLabels[0].text;
        element.querySelector(".upload").textContent = network.valueLabels[2].text;
        element.querySelector(".device-detail").textContent = network.detail;
        trend(element, network);
    }
    const volumes = data.volumes ?? [];
    const serialized = JSON.stringify([document.documentElement.lang, volumes]);
    if (serialized !== previousVolumes) {
        previousVolumes = serialized;
        field("#volume-count").textContent = i18n.count(volumes.length);
        field("#volumes-empty").hidden = volumes.length > 0;
        field("#volume-list").replaceChildren(...volumes.map(volume => {
            const row = field("#volume-template").content.firstElementChild.cloneNode(true);
            const name = row.querySelector("h3");
            name.title = name.textContent = `${volume.letter} · ${volume.name}`;
            row.querySelector(".volume-capacity").textContent = volume.totalCapacity;
            row.querySelector(".volume-usage").textContent = volume.usage ?? "";
            row.querySelector("progress").value = volume.usedPercent;
            row.querySelector("progress").setAttribute("aria-label", `${volume.letter} ${i18n.t("已用容量")}`);
            return row;
        }));
    }
}

async function refresh() {
    try {
        const signal = typeof AbortSignal.timeout === "function" ? AbortSignal.timeout(5000) : undefined;
        const response = await fetch(i18n.query("/api/dashboard"), { signal });
        if (response.status === 401) location.replace("/login?next=%2Ffull");
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        render(await response.json());
    } catch {
        status.textContent = i18n.t("连接中断 · 正在重试");
        status.hidden = false;
        stage.classList.add("waiting");
    } finally {
        setTimeout(refresh, 1000);
    }
}

field("#exit-full").addEventListener("click", async event => {
    if (!document.fullscreenElement || !document.exitFullscreen) return;
    event.preventDefault();
    try { await document.exitFullscreen(); } catch { /* The overview can still be opened. */ }
    location.assign("/");
});
refresh();
