export async function refreshMetadataFrame(frame, expectedUrl) {
    const child = frame?.contentWindow;
    if (!frame?.isConnected || child?.location.href !== expectedUrl || typeof child.refresh !== "function")
        return false;

    await child.refresh();
    return true;
}
