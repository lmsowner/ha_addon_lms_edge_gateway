(() => {
    const modeStorageKey = "lms-edge-gateway-theme-mode";
    const legacyThemeStorageKey = "lms-edge-gateway-theme";
    const modeCookieName = "lms_edge_gateway_theme_mode";
    const legacyThemeCookieName = "lms_edge_gateway_theme";
    const root = document.documentElement;
    const media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    let blazorListenerAttached = false;
    let parentThemeObserverAttached = false;

    const normalizeTheme = value => value === "dark" || value === "light" ? value : null;
    const normalizeMode = value => value === "auto" || value === "dark" || value === "light" ? value : null;

    const readCookieValue = name => document.cookie
        .split(";")
        .map(value => value.trim())
        .find(value => value.startsWith(`${name}=`))
        ?.split("=")[1];

    const readCookieMode = () => normalizeMode(readCookieValue(modeCookieName));

    const writeCookie = (name, value, maxAge = 31536000) => {
        document.cookie = `${name}=${value}; Max-Age=${maxAge}; Path=/; SameSite=Lax`;
    };

    const clearLegacyTheme = () => {
        try {
            localStorage.removeItem(legacyThemeStorageKey);
        } catch {
        }

        writeCookie(legacyThemeCookieName, "", 0);
    };

    const readThemeMode = () => {
        try {
            return normalizeMode(localStorage.getItem(modeStorageKey)) || readCookieMode() || "auto";
        } catch {
            return readCookieMode() || "auto";
        }
    };

    const saveThemeMode = mode => {
        try {
            localStorage.setItem(modeStorageKey, mode);
        } catch {
        }

        writeCookie(modeCookieName, mode);
        clearLegacyTheme();
    };

    const parseRgb = value => {
        const match = value?.match(/rgba?\(\s*(\d+)[,\s]+(\d+)[,\s]+(\d+)(?:[,\s/]+([0-9.]+))?/i);
        if (match?.[4] === "0" || match?.[4] === "0.0") {
            return null;
        }

        return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : null;
    };

    const isDarkColor = value => {
        const rgb = parseRgb(value);
        return rgb ? (0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]) / 255 < 0.48 : false;
    };

    const readElementTheme = element => {
        if (!element) {
            return null;
        }

        const values = [
            element.dataset?.theme,
            element.dataset?.colorScheme,
            element.getAttribute?.("data-theme"),
            element.getAttribute?.("data-color-scheme"),
            element.getAttribute?.("theme")
        ].map(normalizeTheme);
        const direct = values.find(Boolean);
        if (direct) {
            return direct;
        }

        const className = String(element.className || "").toLowerCase();
        if (/\b(dark|dark-mode|theme-dark|ha-dark)\b/.test(className)) {
            return "dark";
        }

        if (/\b(light|light-mode|theme-light|ha-light)\b/.test(className)) {
            return "light";
        }

        return null;
    };

    const readHomeAssistantTheme = () => {
        try {
            if (!window.parent || window.parent === window || !window.parent.document) {
                return null;
            }

            const parentDocument = window.parent.document;
            const parentRoot = parentDocument.documentElement;
            const parentBody = parentDocument.body;
            const explicit = readElementTheme(parentRoot) || readElementTheme(parentBody);
            if (explicit) {
                return explicit;
            }

            const rootStyle = window.parent.getComputedStyle(parentRoot);
            const bodyStyle = parentBody ? window.parent.getComputedStyle(parentBody) : null;
            const colorScheme = `${rootStyle.colorScheme || ""} ${bodyStyle?.colorScheme || ""}`.toLowerCase();
            if (colorScheme.includes("dark")) {
                return "dark";
            }

            if (colorScheme.includes("light")) {
                return "light";
            }

            const background =
                rootStyle.getPropertyValue("--primary-background-color") ||
                rootStyle.getPropertyValue("--card-background-color") ||
                rootStyle.getPropertyValue("--ha-card-background") ||
                bodyStyle?.backgroundColor ||
                rootStyle.backgroundColor;
            return isDarkColor(background) ? "dark" : "light";
        } catch {
            return null;
        }
    };

    const resolveAutoTheme = () => readHomeAssistantTheme() || (media?.matches ? "dark" : "light");

    const readTheme = () => {
        const mode = readThemeMode();
        return mode === "auto" ? resolveAutoTheme() : mode;
    };

    const nextThemeMode = () => {
        const mode = readThemeMode();
        if (mode === "auto") {
            return readTheme() === "dark" ? "light" : "dark";
        }

        return mode === "dark" ? "light" : "auto";
    };

    const applyTheme = (theme, mode = readThemeMode()) => {
        root.dataset.theme = theme;
        root.dataset.themeMode = mode;
        root.style.colorScheme = theme;

        for (const button of document.querySelectorAll("[data-theme-toggle]")) {
            const label = mode === "auto"
                ? `Following Home Assistant theme (${theme}). Click to force ${theme === "dark" ? "light" : "dark"} mode.`
                : `${mode === "dark" ? "Dark" : "Light"} mode forced. Click to ${mode === "dark" ? "force light mode" : "follow Home Assistant theme"}.`;
            button.title = label;
            button.setAttribute("aria-label", label);
            button.setAttribute("aria-pressed", mode === "auto" ? "mixed" : theme === "dark" ? "true" : "false");
            button.dataset.themeMode = mode;
        }
    };

    const syncTheme = () => applyTheme(readTheme(), readThemeMode());

    const attachParentThemeObserver = () => {
        if (parentThemeObserverAttached || !window.MutationObserver) {
            return;
        }

        try {
            if (!window.parent || window.parent === window || !window.parent.document) {
                return;
            }

            const parentDocument = window.parent.document;
            const observedElements = [parentDocument.documentElement, parentDocument.body].filter(Boolean);
            if (observedElements.length === 0) {
                return;
            }

            const observer = new MutationObserver(() => {
                if (readThemeMode() === "auto") {
                    syncTheme();
                }
            });
            for (const element of observedElements) {
                observer.observe(element, {
                    attributes: true,
                    attributeFilter: ["class", "style", "data-theme", "data-color-scheme", "theme"]
                });
            }

            window.addEventListener("pagehide", () => observer.disconnect(), { once: true });
            parentThemeObserverAttached = true;
        } catch {
        }
    };

    document.addEventListener("click", event => {
        const button = event.target.closest("[data-theme-toggle]");
        if (!button) {
            return;
        }

        saveThemeMode(event.shiftKey ? "auto" : nextThemeMode());
        syncTheme();
    });

    document.addEventListener("DOMContentLoaded", syncTheme);
    document.addEventListener("enhancedload", syncTheme);
    window.addEventListener("pageshow", syncTheme);

    const attachBlazorEnhancedNavigation = () => {
        if (blazorListenerAttached || !window.Blazor?.addEventListener) {
            return;
        }

        window.Blazor.addEventListener("enhancedload", syncTheme);
        blazorListenerAttached = true;
    };

    window.addEventListener("load", () => {
        syncTheme();
        attachParentThemeObserver();
        attachBlazorEnhancedNavigation();
    });
    window.setTimeout(attachParentThemeObserver, 0);
    window.setTimeout(attachBlazorEnhancedNavigation, 0);
    window.setTimeout(syncTheme, 0);

    if (media?.addEventListener) {
        media.addEventListener("change", () => {
            if (readThemeMode() === "auto") {
                syncTheme();
            }
        });
    }

    window.setInterval(() => {
        if (readThemeMode() === "auto") {
            syncTheme();
        }
    }, 3000);

    clearLegacyTheme();
    syncTheme();
})();
