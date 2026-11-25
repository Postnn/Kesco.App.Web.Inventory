export function focusControl(container, selector) {
    const control = container?.querySelector(selector);
    if (control && !control.disabled && control.getClientRects().length > 0)
        control.focus();
}

