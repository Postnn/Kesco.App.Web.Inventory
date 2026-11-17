// ── Clayzor application helpers ────────────────────────────────────────────────

/**
 * Устанавливает фокус на DOM-элемент как только он становится видимым.
 * Используется вместо Task.Delay + FocusAsync в Blazor-компонентах, чтобы не
 * зависеть от конкретной длительности CSS-анимации (например, открытия диалога).
 *
 * Механизм: через requestAnimationFrame опрашивает элемент до тех пор, пока
 * offsetParent !== null (элемент виден и не скрыт через display:none),
 * после чего вызывает .focus().
 *
 * @param {HTMLElement} element — ссылка на DOM-элемент (ElementReference из Blazor)
 */
window.clayFocusElement = function (element) {
    if (!element) return;

    const tryFocus = () => {
        if (element.offsetParent !== null || element.offsetWidth > 0) {
            element.focus();
        } else {
            requestAnimationFrame(tryFocus);
        }
    };

    requestAnimationFrame(tryFocus);
};


// Обновление родительского списка и закрытие legacy-совместимого popup формы модели.
window.clayModelEquipmentForm = {
    refreshOpener() {
        try {
            if (!window.opener || window.opener.closed) return;
            if (typeof window.opener.refresh === "function") window.opener.refresh();
            else window.opener.location.reload();
        } catch (_) {
            // Родительское окно может быть в другом origin; его обновление необязательно.
        }
    },

    close() {
        window.close();
        setTimeout(() => {
            if (!window.closed && window.history.length > 1) window.history.back();
        }, 100);
    },

    browseFile(elementId) {
        document.getElementById(elementId)?.click();
    }
};
