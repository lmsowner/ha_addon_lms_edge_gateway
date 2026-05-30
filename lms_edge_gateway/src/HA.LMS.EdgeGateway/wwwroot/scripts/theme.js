(() => {
    const storageKey = "lms-edge-gateway-theme";
    const cookieName = "lms_edge_gateway_theme";
    const root = document.documentElement;
    const media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    let blazorListenerAttached = false;

    const normalizeTheme = value => value === "dark" || value === "light" ? value : null;

    const readCookieTheme = () => normalizeTheme(
        document.cookie
            .split(";")
            .map(value => value.trim())
            .find(value => value.startsWith(`${cookieName}=`))
            ?.split("=")[1]);

    const hasStoredTheme = () => Boolean(readStoredTheme());

    const readStoredTheme = () => {
        try {
            return normalizeTheme(localStorage.getItem(storageKey)) || readCookieTheme();
        } catch {
            return readCookieTheme();
        }
    };

    const writeCookieTheme = theme => {
        document.cookie = `${cookieName}=${theme}; Max-Age=31536000; Path=/; SameSite=Lax`;
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

    const readTheme = () => {
        return readStoredTheme() || readHomeAssistantTheme() || (media?.matches ? "dark" : "light");
    };

    const saveTheme = theme => {
        try {
            localStorage.setItem(storageKey, theme);
        } catch {
        }

        writeCookieTheme(theme);
    };

    const applyTheme = theme => {
        root.dataset.theme = theme;
        root.style.colorScheme = theme;

        const isDark = theme === "dark";
        for (const button of document.querySelectorAll("[data-theme-toggle]")) {
            const label = isDark ? "Switch to light mode" : "Switch to dark mode";
            button.title = label;
            button.setAttribute("aria-label", label);
            button.setAttribute("aria-pressed", isDark ? "true" : "false");
        }
    };

    const syncTheme = () => applyTheme(readTheme());

    document.addEventListener("click", event => {
        const button = event.target.closest("[data-theme-toggle]");
        if (!button) {
            return;
        }

        const nextTheme = root.dataset.theme === "dark" ? "light" : "dark";
        saveTheme(nextTheme);
        applyTheme(nextTheme);
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
        attachBlazorEnhancedNavigation();
    });
    window.setTimeout(attachBlazorEnhancedNavigation, 0);
    window.setTimeout(syncTheme, 0);

    if (media?.addEventListener) {
        media.addEventListener("change", event => {
            if (!hasStoredTheme()) {
                applyTheme(readHomeAssistantTheme() || (event.matches ? "dark" : "light"));
            }
        });
    }

    window.setInterval(() => {
        if (!hasStoredTheme()) {
            syncTheme();
        }
    }, 3000);

    syncTheme();
})();
