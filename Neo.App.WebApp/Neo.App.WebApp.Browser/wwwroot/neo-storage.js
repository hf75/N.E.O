// IndexedDB helpers exposed to .NET via JSImport.
// The database has a single object store "sessions" keyed by name.

const DB_NAME = 'neo-webapp';
const STORE = 'sessions';
let _dbPromise = null;

function openDb() {
    if (_dbPromise) return _dbPromise;
    _dbPromise = new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, 1);
        req.onupgradeneeded = () => {
            const db = req.result;
            if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE, { keyPath: 'name' });
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
    return _dbPromise;
}

export async function neo_storage_list() {
    const db = await openDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readonly');
        const req = tx.objectStore(STORE).getAllKeys();
        req.onsuccess = () => resolve(JSON.stringify(req.result));
        req.onerror = () => reject(req.error);
    });
}

export async function neo_storage_get(name) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readonly');
        const req = tx.objectStore(STORE).get(name);
        req.onsuccess = () => {
            const v = req.result;
            resolve(v ? (v.json ?? JSON.stringify(v)) : '');
        };
        req.onerror = () => reject(req.error);
    });
}

export async function neo_storage_put(name, json) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readwrite');
        const req = tx.objectStore(STORE).put({ name, json });
        req.onsuccess = () => resolve();
        req.onerror = () => reject(req.error);
    });
}

export async function neo_storage_delete(name) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readwrite');
        const req = tx.objectStore(STORE).delete(name);
        req.onsuccess = () => resolve();
        req.onerror = () => reject(req.error);
    });
}

// File download helper used by the UI to let the user export a .neo session.
export function neo_trigger_download(filename, text) {
    const blob = new Blob([text], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename + '.neo';
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
