"use strict";

const i18n = (() => {
    const root = document.documentElement;
    const reverse = new Map(Object.entries(translations).map(([key, value]) => [value, key]));
    const originalNodes = [];
    const originalAttributes = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode(node) {
            return node.parentElement?.closest("script,style") || !(node.nodeValue.trim() in translations)
                ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
        }
    });
    while (walker.nextNode()) originalNodes.push([walker.currentNode, walker.currentNode.nodeValue]);
    for (const element of root.querySelectorAll("[aria-label],[title],[placeholder]"))
        for (const name of ["aria-label", "title", "placeholder"]) {
            const value = element.getAttribute(name);
            if (value && value in translations) originalAttributes.push([element, name, value]);
        }
    let english;
    let remembered;
    function t(value) {
        if (typeof value !== "string") return value;
        if (!english) return reverse.get(value) ?? value;
        return translations[value] ?? value
            .replace(/^目标 API 返回 HTTP /, translations["目标 API 返回 HTTP "])
            .replace(/^无法监听 (\d+)，仍使用端口 (\d+)。$/, "Could not listen on port $1. Still using port $2.")
            .replace(/^无法监听 (\d+) 端口。请检查端口占用后重试。$/, "Could not listen on port $1. Check whether it is in use.");
    }
    function setPreference(preference = "system") {
        if (remembered !== preference) {
            remembered = preference;
            try { localStorage.setItem("mony-language", preference); } catch { /* Storage may be disabled. */ }
        }
        const useEnglish = preference === "en" || (preference !== "zh-CN" &&
            !/^(?:zh-(?:CN|SG)|zh-Hans(?:-[a-z0-9]+)*)$/i.test(navigator.language));
        if (english === useEnglish) return;
        english = useEnglish;
        root.lang = english ? "en" : "zh-CN";
        for (const [node, value] of originalNodes) node.nodeValue = value.replace(value.trim(), t(value.trim()));
        for (const [element, name, value] of originalAttributes) element.setAttribute(name, t(value));
        const notice = document.querySelector("#save-message");
        if (notice && !notice.children.length) notice.textContent = t(notice.textContent);
    }
    let preference = root.dataset.language;
    if (root.dataset.offline) {
        try { preference = localStorage.getItem("mony-language") ?? preference; }
        catch { /* Use the cached page's preference. */ }
    }
    setPreference(preference);
    const query = path => path + "?language=" + encodeURIComponent(navigator.language);
    return { t, setPreference, query, count: n => english ? String(n) : n + " 个" };
})();
