export function scrollJournal(journal) {
    if (!journal?.isConnected) return;
    const viewport = journal.closest(".clay-entity-form-content") ?? journal;
    viewport.scrollTop = viewport.scrollHeight;
}

