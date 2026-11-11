"use strict";

const form = document.querySelector("#login-form");
const input = document.querySelector("#password");
const message = document.querySelector("#login-message");
const submit = document.querySelector("#login-submit");
const next = new URLSearchParams(location.search).get("next");
const destination = location.pathname.toLowerCase().replace(/\/$/, "") === "/settings" ? "/settings" :
    ["/", "/settings", "/full", "/api/data", "/api/dashboard"].includes(next) ? next : "/";

if (destination === "/settings") {
    document.querySelector("h1").textContent = i18n.t("访问设置");
    document.querySelector("main p").textContent = i18n.t("输入网页设置密码。登录后 1 天内免重复验证。");
}

form.addEventListener("submit", async event => {
    event.preventDefault();
    if (submit.disabled) return;
    submit.disabled = true;
    message.textContent = "";
    try {
        const response = await fetch("/api/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ password: input.value })
        });
        if (response.ok) {
            input.value = "";
            location.replace(destination);
            return;
        }
        message.textContent = response.status === 401 ? i18n.t("密码不正确。") : i18n.t("暂时无法登录，请重试。");
    } catch {
        message.textContent = i18n.t("连接中断，请重试。");
    }
    submit.disabled = false;
    input.focus();
});
