const watchers = new WeakMap();

function textControl(scope) {
    const control = scope?.querySelector("textarea");
    return control && !control.disabled ? control : null;
}

export function readTextStream(scope) {
    const control = textControl(scope);
    if (!control) return null;
    return DotNet.createJSStreamReference(new Blob([control.value], { type: "text/plain;charset=utf-8" }));
}

export function watchTemplateText(scope, receiver, generation) {
    if (!textControl(scope)) throw new Error("Поле шаблона недоступно.");
    watchers.get(scope)?.release();
    const state = { generation, revision: 0, composing: false };
    let active = true;
    let pending = false;
    let running = false;
    let notification;

    function schedule() {
        if (!active || !pending || state.composing || running || notification !== undefined) return;
        notification = setTimeout(async () => {
            notification = undefined;
            if (!active || state.composing || !pending) return;
            pending = false;
            running = true;
            try { await receiver.invokeMethodAsync("OnTemplateEditorChangedAsync", generation, state.revision); }
            catch { } // The circuit or this editor may already be gone.
            finally {
                running = false;
                schedule();
            }
        }, 40);
    }

    function change(event) {
        const control = textControl(scope);
        if (!control || event.target !== control || control.readOnly) return;
        updateLine(control);
        // The shared draft listener already captured the value at document capture.
        // This textarea has no Blazor input/change bindings; native editing remains untouched.
        state.revision++;
        pending = true;
        if (event.isComposing) state.composing = true;
        schedule();
    }

    function compositionStart(event) {
        if (event.target === textControl(scope))
            state.composing = true;
    }

    function compositionEnd(event) {
        if (event.target !== textControl(scope)) return;
        state.composing = false;
        schedule();
    }

    function updateLine(control) {
        const indicator = scope.querySelector("[data-template-line]");
        if (!indicator) return;
        let line = 1;
        for (let index = control.value.indexOf("\n"); index >= 0 && index < control.selectionStart;
            index = control.value.indexOf("\n", index + 1)) line++;
        indicator.textContent = `Строка ${line}`;
    }

    function updateSelection(event) {
        if (event.target === textControl(scope)) updateLine(event.target);
    }

    function clearLine(event) {
        if (event.target !== textControl(scope)) return;
        const indicator = scope.querySelector("[data-template-line]");
        if (indicator) indicator.textContent = "";
    }

    const lineEvents = ["keyup", "mouseup", "focus", "select"];
    for (const name of lineEvents) scope.addEventListener(name, updateSelection, true);
    scope.addEventListener("blur", clearLine, true);
    scope.addEventListener("input", change, true);
    scope.addEventListener("change", change, true);
    scope.addEventListener("compositionstart", compositionStart, true);
    scope.addEventListener("compositionend", compositionEnd, true);
    state.release = () => {
        active = false;
        state.unfreeze?.();
        clearTimeout(notification);
        scope.removeEventListener("input", change, true);
        scope.removeEventListener("change", change, true);
        scope.removeEventListener("compositionstart", compositionStart, true);
        scope.removeEventListener("compositionend", compositionEnd, true);
        for (const name of lineEvents) scope.removeEventListener(name, updateSelection, true);
        scope.removeEventListener("blur", clearLine, true);
        watchers.delete(scope);
    };
    state.resume = () => {
        pending = true;
        schedule();
    };
    watchers.set(scope, state);
}

export function unwatchTemplateText(scope, generation) {
    const state = watchers.get(scope);
    if (state?.generation === generation)
        state.release();
}

export function currentTemplateRevision(scope, generation) {
    const state = watchers.get(scope);
    return state?.generation === generation && !state.composing ? state.revision : null;
}

export function freezeTemplateInput(scope, generation, revision) {
    const state = watchers.get(scope);
    const control = textControl(scope);
    if (!control || control.readOnly || state?.unfreeze || currentTemplateRevision(scope, generation) !== revision) return false;
    control.readOnly = true;
    state.unfreeze = () => { control.readOnly = false; };
    return true;
}

export function releaseTemplateInput(scope, generation) {
    const state = watchers.get(scope);
    if (state?.generation !== generation || !state.unfreeze) return;
    state.unfreeze();
    state.unfreeze = null;
    state.resume();
}

export function readTemplateSnapshot(scope, generation) {
    const revision = currentTemplateRevision(scope, generation);
    if (revision === null) return null;
    const payload = readTextStream(scope);
    return payload ? { revision, payload } : null;
}

export function closeTemplateIfCurrent(scope, generation, revision) {
    if (currentTemplateRevision(scope, generation) !== revision) return false;
    window.clayModelEquipmentForm.close();
    return true;
}

export function findTemplateAttribute(scope, generation, type) {
    if (currentTemplateRevision(scope, generation) === null || !type) return false;
    const control = textControl(scope);
    if (!control) return false;

    const escaped = type.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    const pattern = new RegExp(escaped, "giu");
    pattern.lastIndex = control.selectionEnd;
    const match = pattern.exec(control.value) ?? pattern.exec(control.value);
    if (!match) return false;

    control.focus();
    control.setSelectionRange(match.index, match.index + match[0].length);
    return true;
}

export function insertTemplateAttribute(scope, generation, type) {
    if (currentTemplateRevision(scope, generation) === null || !type) return false;
    const control = textControl(scope);
    if (!control || control.readOnly) return false;

    const value = "?" + type;
    if (control.maxLength >= 0 && control.value.length + value.length > control.maxLength) return false;
    const position = control.selectionEnd;
    const scroll = control.scrollTop;
    control.setRangeText(value, position, position, "end");
    control.focus();
    control.scrollTop = scroll;
    control.dispatchEvent(new Event("input", { bubbles: true }));
    return true;
}
