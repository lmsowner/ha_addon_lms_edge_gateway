(() => {
    const storageKey = "lms-edge-gateway-theme";
    const root = document.documentElement;
    const media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;

    const hasStoredTheme = () => {
        try {
            const value = localStorage.getItem(storageKey);
            return value === "dark" || value === "light";
        } catch {
            return false;
        }
    };

    const readTheme = () => {
        try {
            const value = localStorage.getItem(storageKey);
            if (value === "dark" || value === "light") {
                return value;
            }
        } catch {
        }

        return media?.matches ? "dark" : "light";
    };

    const saveTheme = theme => {
        try {
            localStorage.setItem(storageKey, theme);
        } catch {
        }
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

    document.addEventListener("click", event => {
        const button = event.target.closest("[data-theme-toggle]");
        if (!button) {
            return;
        }

        const nextTheme = root.dataset.theme === "dark" ? "light" : "dark";
        saveTheme(nextTheme);
        applyTheme(nextTheme);
    });

    if (media?.addEventListener) {
        media.addEventListener("change", event => {
            if (!hasStoredTheme()) {
                applyTheme(event.matches ? "dark" : "light");
            }
        });
    }

    applyTheme(readTheme());
})();
