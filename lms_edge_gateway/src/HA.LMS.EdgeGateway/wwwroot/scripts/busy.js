(() => {
    const root = document.documentElement;
    let navigationTimer = 0;

    const setNavigationBusy = element => {
        clearTimeout(navigationTimer);
        root.dataset.uiBusy = "navigation";
        element?.classList.add("tab-trigger-pending");
        element?.setAttribute("aria-busy", "true");
        navigationTimer = window.setTimeout(clearNavigationBusy, 15000);
    };

    const clearNavigationBusy = () => {
        clearTimeout(navigationTimer);
        if (root.dataset.uiBusy === "navigation") {
            delete root.dataset.uiBusy;
        }

        for (const element of document.querySelectorAll(".tab-trigger-pending")) {
            element.classList.remove("tab-trigger-pending");
            element.removeAttribute("aria-busy");
        }
    };

    const markButtonPending = button => {
        if (!button || button.disabled || button.classList.contains("lms-button-busy")) {
            return;
        }

        button.classList.add("lms-button-pending");
        button.setAttribute("aria-busy", "true");
        window.setTimeout(() => {
            button.classList.remove("lms-button-pending");
            if (!button.classList.contains("lms-button-busy")) {
                button.removeAttribute("aria-busy");
            }
        }, 1200);
    };

    const markLocalTabPending = tab => {
        if (!tab || tab.classList.contains("active")) {
            return;
        }

        tab.classList.add("tab-trigger-pending");
        tab.setAttribute("aria-busy", "true");
        window.setTimeout(() => {
            tab.classList.remove("tab-trigger-pending");
            tab.removeAttribute("aria-busy");
        }, 700);
    };

    document.addEventListener("click", event => {
        const tab = event.target.closest(".tab-trigger[href]");
        if (tab &&
            !tab.classList.contains("active") &&
            !event.defaultPrevented &&
            event.button === 0 &&
            !event.metaKey &&
            !event.ctrlKey &&
            !event.shiftKey &&
            !event.altKey &&
            tab.target !== "_blank") {
            setNavigationBusy(tab);
        }
        else {
            markLocalTabPending(event.target.closest(".tab-trigger:not([href])"));
        }

        const button = event.target.closest(".lms-button");
        if (button) {
            markButtonPending(button);
        }
    }, true);

    document.addEventListener("enhancedload", clearNavigationBusy);
    window.addEventListener("pageshow", clearNavigationBusy);
    window.addEventListener("popstate", clearNavigationBusy);

    const attachBlazorEnhancedNavigation = () => {
        if (!window.Blazor?.addEventListener) {
            return;
        }

        window.Blazor.addEventListener("enhancedload", clearNavigationBusy);
    };

    window.addEventListener("load", attachBlazorEnhancedNavigation);
    window.setTimeout(attachBlazorEnhancedNavigation, 0);
})();
