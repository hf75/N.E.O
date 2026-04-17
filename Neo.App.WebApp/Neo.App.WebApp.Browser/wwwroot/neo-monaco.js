// Monaco editor wrapper — overlays an absolutely-positioned <div> on top of
// the Avalonia WASM canvas. Shown/hidden by the C# side, sized once and kept
// in sync via ResizeObserver. CDN-loaded to keep the Neo.WebApp bundle small.

const MONACO_BASE = 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.0/min/vs';
let _editor = null;
let _overlay = null;
let _loadPromise = null;
let _ro = null;

function ensureOverlay() {
    if (_overlay) return _overlay;
    _overlay = document.createElement('div');
    _overlay.id = 'neo-monaco-overlay';
    Object.assign(_overlay.style, {
        position: 'fixed',
        display: 'none',
        background: '#1e1e1e',
        border: '1px solid #444',
        zIndex: '10000',
        left: '0px', top: '0px', width: '0px', height: '0px',
    });
    document.body.appendChild(_overlay);
    return _overlay;
}

function loadMonaco() {
    if (_loadPromise) return _loadPromise;
    _loadPromise = new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = `${MONACO_BASE}/loader.js`;
        s.onload = () => {
            window.require.config({ paths: { vs: MONACO_BASE } });
            window.require(['vs/editor/editor.main'], () => resolve(window.monaco));
        };
        s.onerror = reject;
        document.head.appendChild(s);
    });
    return _loadPromise;
}

export async function neo_monaco_show(x, y, w, h, initialCode) {
    const overlay = ensureOverlay();
    overlay.style.left = x + 'px';
    overlay.style.top = y + 'px';
    overlay.style.width = w + 'px';
    overlay.style.height = h + 'px';
    overlay.style.display = 'block';

    const monaco = await loadMonaco();
    if (!_editor) {
        _editor = monaco.editor.create(overlay, {
            value: initialCode || '',
            language: 'csharp',
            theme: 'vs-dark',
            automaticLayout: true,
            minimap: { enabled: false },
            fontSize: 13,
        });
    } else if (initialCode != null && _editor.getValue() !== initialCode) {
        _editor.setValue(initialCode);
    }
    return true;
}

export function neo_monaco_reposition(x, y, w, h) {
    if (!_overlay) return;
    _overlay.style.left = x + 'px';
    _overlay.style.top = y + 'px';
    _overlay.style.width = w + 'px';
    _overlay.style.height = h + 'px';
    if (_editor) _editor.layout();
}

export function neo_monaco_hide() {
    if (_overlay) _overlay.style.display = 'none';
}

export function neo_monaco_set_code(code) {
    if (_editor) _editor.setValue(code || '');
}

export function neo_monaco_get_code() {
    return _editor ? _editor.getValue() : '';
}

export function neo_monaco_dispose() {
    if (_editor) { _editor.dispose(); _editor = null; }
    if (_overlay) { _overlay.remove(); _overlay = null; }
    if (_ro) { _ro.disconnect(); _ro = null; }
}
