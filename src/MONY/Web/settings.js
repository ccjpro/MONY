"use strict";

const form = document.querySelector("#settings-form");
const message = document.querySelector("#save-message");
const field = name => form.elements.namedItem(name);
const automatic = ["preferredIp", "desktopTheme", "webTheme", "minimizeTo", "closeTo", "startWithWindows",
    "language", "showSharedGpuMemory", "webEnabled", "webLan", "apiEnabled", "apiLan",
    "windowsNotifications", "webNotifications", "webMuteLocal", "webMuteIps"];
const alertNumbers = ["cpuAlertCelsius", "gpuAlertCelsius", "diskAlertPercent", "alertDelaySeconds"];
let current;
let pending = Promise.resolve();
let queued = 0;
let webDisabled = false;
let previousTargets;
const connectRemote = document.querySelector("#connect-remote");
const removeRemote = document.querySelector("#remove-remote");

function showEndpoint(kind, enabled, endpoint, url) {
    const link = document.querySelector(`#${kind}-url`);
    link.hidden = !url;
    link.textContent = url ?? "";
    if (url && endpoint.running) link.href = url;
    else link.removeAttribute("href");
    const configuredPort = current.options[kind + "Port"];
    const switching = endpoint.running && endpoint.port !== configuredPort && !endpoint.error;
    const status = document.querySelector(`#${kind}-status`);
    status.textContent = !enabled ? i18n.t("已关闭") : i18n.t(endpoint.error) ?? (switching ? i18n.t("正在切换端口") :
        endpoint.running ? endpoint.lan ? i18n.t("运行中 · 所有网络接口") : i18n.t("运行中 · 仅限此电脑") : i18n.t("正在启动"));
}

function showStatus(view) {
    i18n.setPreference(view.options.language);
    if (field("preferredIp").options.length) field("preferredIp").options[0].textContent = i18n.t("自动选择");
    const targets = view.remoteTargets ?? [];
    const serialized = JSON.stringify(targets);
    if (serialized !== previousTargets) {
        previousTargets = serialized;
        const select = field("remoteList");
        const selected = select.value;
        select.replaceChildren(...targets.map(target => {
            const option = new Option(target.label, target.id);
            option.title = target.address;
            return option;
        }));
        if (targets.some(target => target.id === selected)) select.value = selected;
        document.querySelector("#remote-list-row").hidden = targets.length === 0;
    }
    showEndpoint("web", view.options.webEnabled, view.web, view.webUrl);
    showEndpoint("api", view.options.apiEnabled, view.api, view.apiUrl);
    document.documentElement.dataset.theme = view.options.webTheme;
    document.querySelector("#lock-settings").disabled = !view.webPasswordSet;
    refreshNotificationPermission();
    for (const kind of ["web", "api"]) {
        const name = kind + "Password";
        field(name).placeholder = view[name + "Set"] ? i18n.t("已设置") : i18n.t("新密码");
        document.querySelector(`[data-clear="${name}"]`).disabled = !view[name + "Set"];
    }
}

function option(view, name) {
    return name === "startWithWindows" ? view.startWithWindows : view.options[name];
}

function setControl(control, value) {
    if (control.type === "checkbox") control.checked = value;
    else control.value = value;
}

function load(view) {
    current = view;
    document.querySelector("#app-version").textContent = view.version;
    const select = field("preferredIp");
    select.replaceChildren(new Option(i18n.t("自动选择"), ""));
    for (const address of view.addresses) {
        const item = new Option(address.ip, address.ip);
        item.title = address.adapter;
        select.add(item);
    }
    const saved = view.options.preferredIp;
    if (saved && !view.addresses.some(address => address.ip === saved)) select.add(new Option(saved, saved));
    for (const name of [...automatic, "computerName", "webPort", "apiPort", "webMutedAddresses", ...alertNumbers]) setControl(field(name), option(view, name));
    showStatus(view);
}

async function request(options, path = "/api/settings", readJson = true) {
    let response;
    for (let attempt = 0; ; attempt++) {
        const signal = typeof AbortSignal.timeout === "function" ? AbortSignal.timeout(5000) : undefined;
        try {
            response = await fetch(path, { ...options, signal });
            break;
        } catch (error) {
            if (path !== "/api/settings" || attempt === 4) throw error;
            // A Web listener rebind briefly closes the connection used by the next settings action.
            await new Promise(resolve => setTimeout(resolve, 250 * (attempt + 1)));
        }
    }
    if (response.status === 401) {
        location.replace("/settings");
        throw new Error(i18n.t("请重新登录。"));
    }
    if (!response.ok) {
        const error = await response.json().catch(() => null);
        throw new Error(i18n.t(error?.message) || i18n.t("设置请求失败。"));
    }
    return readJson ? response.json() : null;
}

function update(name, value) {
    if (!current || webDisabled) return;
    queued++;
    pending = pending.then(async () => {
        if (webDisabled) return;
        const control = field(name);
        const secret = name.endsWith("Password");
        try {
            current = await request({
                method: "PATCH",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name, value })
            });
            showStatus(current);
            if (secret && (value === "" || control.value === value)) control.value = "";
            if (name === "webEnabled" && !value) {
                webDisabled = true;
                message.textContent = i18n.t("Web 正在关闭。可在桌面重新启用。");
            } else {
                if ((name === "computerName" || name === "webMutedAddresses") && control.value === value)
                    control.value = current.options[name];
                if (name.endsWith("Port") && Number(control.value) === value)
                    control.value = String(value);
                message.textContent = i18n.t("已生效");
                if (name === "webPort" && current.options.webEnabled && location.protocol === "https:") {
                    message.textContent = i18n.t("端口已保存。请同步更新反代服务地址。");
                } else if (name === "webPort" && current.options.webEnabled && value !== Number(location.port || 80)) {
                    const url = new URL("/settings", location.href);
                    url.port = String(value);
                    const link = document.createElement("a");
                    link.href = url.href;
                    link.textContent = i18n.t("打开新端口的设置页");
                    message.replaceChildren(i18n.t("端口已保存。服务切换后，"), link);
                }
            }
        } catch (error) {
            message.textContent = error.message;
            if (!secret && (control.type === "checkbox" ? control.checked : control.value) === value)
                setControl(control, option(current, name));
        }
    }).finally(() => { queued--; });
}

for (const name of automatic) {
    const control = field(name);
    control.addEventListener("change", () => update(name, control.type === "checkbox" ? control.checked : control.value));
}

form.addEventListener("submit", event => event.preventDefault());
for (const button of form.querySelectorAll("[data-confirm]")) {
    button.addEventListener("click", () => {
        const name = button.dataset.confirm;
        const value = field(name).value;
        if (name.endsWith("Password") && value.length === 0) {
            message.textContent = i18n.t("请输入新密码，或使用旁边的清除按钮。");
        } else if (alertNumbers.includes(name)) {
            const number = /^[0-9]+$/.test(value.trim()) ? Number(value) : NaN;
            if (!Number.isSafeInteger(number)) message.textContent = i18n.t("告警阈值和持续时间必须是整数。");
            else update(name, number);
        } else if (name.endsWith("Port")) {
            const port = /^[0-9]+$/.test(value.trim()) ? Number(value) : 0;
            if (!Number.isInteger(port) || port < 1 || port > 65535) message.textContent = i18n.t("端口必须是 1 至 65535 的整数。");
            else update(name, port);
        } else {
            update(name, value);
        }
    });
}
for (const button of form.querySelectorAll("[data-clear]"))
    button.addEventListener("click", () => update(button.dataset.clear, ""));

function changeRemote(path, options, completed) {
    if (!current || webDisabled) return;
    connectRemote.disabled = removeRemote.disabled = true;
    queued++;
    pending = pending.then(async () => {
        if (webDisabled) return;
        try {
            current = await request(options, path);
            showStatus(current);
            completed();
        } catch (error) { message.textContent = error.message; }
    }).finally(() => {
        queued--;
        connectRemote.disabled = removeRemote.disabled = false;
    });
}

connectRemote.addEventListener("click", () => {
    const address = field("remoteAddress").value;
    const port = Number(field("remotePort").value.trim());
    if (!Number.isInteger(port) || port < 1 || port > 65535) {
        message.textContent = i18n.t("端口必须是 1 至 65535 的整数。");
        return;
    }
    const password = field("remotePassword").value;
    changeRemote("/api/targets", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ip: address, port, password })
    }, () => {
        if (field("remoteAddress").value === address) field("remoteAddress").value = "";
        if (field("remotePassword").value === password) field("remotePassword").value = "";
        message.textContent = i18n.t("连接成功，可在性能概览切换目标。");
    });
});

removeRemote.addEventListener("click", () => {
    const id = field("remoteList").value;
    if (id) changeRemote(`/api/targets/${encodeURIComponent(id)}`, { method: "DELETE" },
        () => { message.textContent = i18n.t("已移除连接。"); });
});

function refreshNotificationPermission() {
    const button = document.querySelector("#allow-notifications");
    const status = document.querySelector("#notification-permission-status");
    if (!window.isSecureContext || !("Notification" in window)) {
        button.disabled = true;
        status.textContent = i18n.t("浏览器通知需要支持通知的浏览器和安全访问地址。");
        return;
    }
    button.disabled = Notification.permission !== "default";
    status.textContent = i18n.t(Notification.permission === "granted" ? "此浏览器已授权通知。" :
        Notification.permission === "denied" ? "通知已被拒绝，可在浏览器站点设置中修改。" : "请为此浏览器授权通知。");
}

document.querySelector("#allow-notifications").addEventListener("click", async () => {
    try {
        await Notification.requestPermission();
        refreshNotificationPermission();
    } catch {
        message.textContent = i18n.t("无法请求浏览器通知权限。");
    }
});

document.querySelector("#lock-settings").addEventListener("click", () => {
    pending = pending.then(async () => {
        try {
            await request({ method: "POST" }, "/api/settings/lock", false);
            location.replace("/settings");
        } catch (error) { message.textContent = error.message; }
    });
});

async function open() {
    const controls = Array.from(form.elements);
    controls.forEach(control => { control.disabled = true; });
    try {
        const view = await request();
        controls.forEach(control => { control.disabled = false; });
        load(view);
        message.textContent = i18n.t(view.loadError) ?? "";
    } catch (error) {
        message.textContent = error.message;
    }
}

setInterval(async () => {
    if (!current || queued || webDisabled) return;
    const before = current;
    try {
        const view = await request();
        if (!queued && !webDisabled && current === before) {
            current = view;
            const ip = view.options.preferredIp;
            if (ip && !Array.from(field("preferredIp").options).some(item => item.value === ip))
                field("preferredIp").add(new Option(ip, ip));
            for (const name of automatic) setControl(field(name), option(view, name));
            showStatus(view);
        }
    } catch { /* Rebinding a local listener can briefly interrupt the page. */ }
}, 1500);
window.addEventListener("pageshow", event => { if (event.persisted) open(); });
open();
