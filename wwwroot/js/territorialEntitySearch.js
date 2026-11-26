export async function refreshMetadataFrame(frame, expectedUrl) {
    const child = frame?.contentWindow;
    if (!frame?.isConnected || child?.location.href !== expectedUrl || typeof child.refresh !== "function")
        return false;

    await child.refresh();
    return true;
}
export function clearMoveMarker(key, expected) {
    const marker = sessionStorage.getItem(key);
    if (marker === null) return true;
    if (marker !== expected) return false;
    sessionStorage.removeItem(key);
    return true;
}
