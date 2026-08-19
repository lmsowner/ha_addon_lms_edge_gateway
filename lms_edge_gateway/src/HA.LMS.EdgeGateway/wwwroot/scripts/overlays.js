(() => {
    const pad = 10;

    const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

    const boundsFor = element => {
        const dialog = element.closest(".edge-modal-panel") || element.closest('[role="dialog"]');
        if (dialog) {
            return dialog.getBoundingClientRect();
        }

        return new DOMRect(pad, pad, window.innerWidth - pad * 2, window.innerHeight - pad * 2);
    };

    const placeFieldInfo = (root, event) => {
        const trigger = root.querySelector(".lms-field-info-trigger");
        const tip = root.querySelector(".lms-field-info-tip");
        if (!trigger || !tip) {
            return;
        }

        const bounds = boundsFor(root);
        const icon = trigger.getBoundingClientRect();
        const width = clamp(bounds.width - pad * 2, 0, 320);
        const maxHeight = Math.max(96, bounds.height - pad * 2);

        tip.style.position = "fixed";
        tip.style.zIndex = "240";
        tip.style.width = `${width}px`;
        tip.style.maxWidth = `${width}px`;
        tip.style.minWidth = "0";
        tip.style.maxHeight = `${maxHeight}px`;
        tip.style.right = "auto";
        tip.style.bottom = "auto";
        tip.style.left = `${icon.left}px`;
        tip.style.top = `${icon.bottom + 8}px`;

        const tipHeight = tip.getBoundingClientRect().height;
        const spaceAbove = icon.top - bounds.top - pad;
        const spaceBelow = bounds.bottom - icon.bottom - pad;
        const placeAbove = spaceAbove >= tipHeight || spaceAbove > spaceBelow;

        let top = placeAbove ? icon.top - tipHeight - 8 : icon.bottom + 8;
        let left = event && Number.isFinite(event.clientX) ? event.clientX - 28 : icon.left;

        const minLeft = bounds.left + pad;
        const maxLeft = bounds.right - pad - width;
        left = maxLeft >= minLeft ? clamp(left, minLeft, maxLeft) : minLeft;

        const minTop = bounds.top + pad;
        const maxTop = bounds.bottom - pad - Math.min(tipHeight, maxHeight);
        top = maxTop >= minTop ? clamp(top, minTop, maxTop) : minTop;

        tip.style.left = `${Math.round(left)}px`;
        tip.style.top = `${Math.round(top)}px`;
        tip.classList.add("is-placed");
    };

    const placeMultiSelect = root => {
        const menu = root.querySelector(".lms-multi-select-options");
        const control = root.querySelector(".lms-multi-select-control");
        if (!menu || !control) {
            return;
        }

        const bounds = boundsFor(root);
        const rect = control.getBoundingClientRect();
        const width = clamp(rect.width, 0, bounds.width - pad * 2);
        const maxHeight = Math.max(96, bounds.height - pad * 2);

        menu.style.position = "fixed";
        menu.style.zIndex = "241";
        menu.style.width = `${width}px`;
        menu.style.maxWidth = `${width}px`;
        menu.style.minWidth = "0";
        menu.style.maxHeight = `${Math.min(288, maxHeight)}px`;
        menu.style.right = "auto";
        menu.style.left = `${rect.left}px`;
        menu.style.top = `${rect.bottom + 6}px`;

        const menuHeight = menu.getBoundingClientRect().height;
        const spaceBelow = bounds.bottom - rect.bottom - pad;
        const placeAbove = spaceBelow < menuHeight && rect.top - bounds.top - pad > spaceBelow;

        let top = placeAbove ? rect.top - menuHeight - 6 : rect.bottom + 6;
        let left = rect.left;

        const minLeft = bounds.left + pad;
        const maxLeft = bounds.right - pad - width;
        left = maxLeft >= minLeft ? clamp(left, minLeft, maxLeft) : minLeft;

        const minTop = bounds.top + pad;
        const maxTop = bounds.bottom - pad - Math.min(menuHeight, maxHeight);
        top = maxTop >= minTop ? clamp(top, minTop, maxTop) : minTop;

        menu.style.left = `${Math.round(left)}px`;
        menu.style.top = `${Math.round(top)}px`;
    };

    const placeHovered = event => {
        document.querySelectorAll(".lms-field-info:hover, .lms-field-info:focus-within").forEach(root => {
            placeFieldInfo(root, event);
        });
        document.querySelectorAll(".lms-multi-select.open").forEach(placeMultiSelect);
    };

    document.addEventListener("pointerover", event => {
        const root = event.target?.closest?.(".lms-field-info");
        if (root) {
            placeFieldInfo(root, event);
        }
    }, true);

    document.addEventListener("focusin", event => {
        const info = event.target?.closest?.(".lms-field-info");
        if (info) {
            placeFieldInfo(info, event);
        }

        const select = event.target?.closest?.(".lms-multi-select.open");
        if (select) {
            placeMultiSelect(select);
        }
    }, true);

    document.addEventListener("click", () => {
        window.requestAnimationFrame(() => {
            document.querySelectorAll(".lms-multi-select.open").forEach(placeMultiSelect);
        });
    }, true);

    document.addEventListener("scroll", placeHovered, true);
    window.addEventListener("resize", placeHovered);
})();
