export function readText(scope) {
    const control = scope?.querySelector("textarea");
    return control && !control.disabled ? control.value : null;
}
