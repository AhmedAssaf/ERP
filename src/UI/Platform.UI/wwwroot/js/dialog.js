// Dialog's fallback focus target (docs/08 section 8): when the control that opened a dialog is gone by the time it
// closes, focus goes to the page's main region (AppShell renders <main id="main" tabindex="-1">), or to the first main
// or h1 element on a page without the shell, instead of being left on the document body.

/** Focuses the page's main region; does nothing on a page that has none. */
export function focusMain() {
    const target = document.getElementById("main") ?? document.querySelector("main, h1");
    if (!target) {
        return;
    }

    if (!target.hasAttribute("tabindex")) {
        target.setAttribute("tabindex", "-1");
    }

    target.focus();
}
