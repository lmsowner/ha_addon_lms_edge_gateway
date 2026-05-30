(() => {
    const themeVariables = [
        "--primary-color",
        "--accent-color",
        "--primary-background-color",
        "--secondary-background-color",
        "--card-background-color",
        "--ha-card-background",
        "--primary-text-color",
        "--secondary-text-color",
        "--disabled-text-color",
        "--divider-color",
        "--error-color",
        "--warning-color",
        "--success-color",
        "--state-icon-color",
        "--app-header-background-color",
        "--sidebar-background-color",
        "--ha-card-border-radius",
        "--ha-card-box-shadow",
        "--mdc-theme-primary",
        "--mdc-theme-secondary",
        "--text-primary-color"
    ];

    const target = document.documentElement;

    function getParentDocument() {
        try {
            return window.parent && window.parent !== window ? window.parent.document : null;
        } catch {
            return null;
        }
    }

    function firstValue(styles, name) {
        for (const style of styles) {
            const value = style.getPropertyValue(name).trim();
            if (value) {
                return value;
            }
        }

        return "";
    }

    function colorToRgbTriplet(value) {
        if (!value) {
            return "";
        }

        const canvas = document.createElement("canvas");
        const context = canvas.getContext("2d");
        if (!context) {
            return "";
        }

        context.fillStyle = "#000000";
        context.fillStyle = value;
        const normalized = context.fillStyle;
        const hex = /^#([0-9a-f]{6})$/i.exec(normalized);
        if (hex) {
            const number = Number.parseInt(hex[1], 16);
            return `${(number >> 16) & 255}, ${(number >> 8) & 255}, ${number & 255}`;
        }

        const rgb = /^rgba?\(([^)]+)\)$/i.exec(normalized);
        if (!rgb) {
            return "";
        }

        return rgb[1].split(",").slice(0, 3).map((part) => Number.parseInt(part.trim(), 10)).join(", ");
    }

    function syncTheme() {
        const parentDocument = getParentDocument();
        if (!parentDocument) {
            target.dataset.haThemeSource = "local";
            return;
        }

        const sourceElements = [parentDocument.documentElement, parentDocument.body].filter(Boolean);
        const sourceStyles = sourceElements.map((element) => parentDocument.defaultView.getComputedStyle(element));
        for (const name of themeVariables) {
            const value = firstValue(sourceStyles, name);
            if (value) {
                target.style.setProperty(name, value);
            }
        }

        const colorScheme = firstValue(sourceStyles, "color-scheme");
        const scheme = colorScheme.includes("dark") ? "dark" : colorScheme.includes("light") ? "light" : "";
        if (scheme) {
            target.style.setProperty("--lms-ha-color-scheme", scheme);
            target.style.colorScheme = scheme;
        }

        const primaryRgb = colorToRgbTriplet(firstValue(sourceStyles, "--primary-color"));
        if (primaryRgb) {
            target.style.setProperty("--lms-ha-primary-rgb", primaryRgb);
        }

        target.dataset.haThemeSource = "parent";
    }

    function observeParentTheme() {
        const parentDocument = getParentDocument();
        if (!parentDocument) {
            return;
        }

        const observer = new MutationObserver(syncTheme);
        for (const element of [parentDocument.documentElement, parentDocument.body].filter(Boolean)) {
            observer.observe(element, {
                attributes: true,
                attributeFilter: ["class", "style", "data-theme", "dark"]
            });
        }
    }

    syncTheme();
    window.addEventListener("load", () => {
        syncTheme();
        observeParentTheme();
    });
    window.addEventListener("focus", syncTheme);
})();
