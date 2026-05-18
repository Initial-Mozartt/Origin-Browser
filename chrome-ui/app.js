const tabsBar = document.getElementById('tabs-bar');
const addressBar = document.querySelector('#addressBarWrapper input');
const btnBack = document.getElementById('btnBack');
const btnForward = document.getElementById('btnForward');
const btnReload = document.getElementById('btnReload');
const btnHome = document.getElementById('btnHome');
const btnNewTab = document.getElementById('btnNewTab');

let currentState = { tabs: [], url: '' };

function init() {
    // Ensure we are inside WebView2; fail gracefully if opened in a normal browser
    if (!window.chrome?.webview) {
        console.error('Origin Chrome UI must run inside WebView2');
        return;
    }

    // Listen for state updates pushed from the C# backend
    window.chrome.webview.addEventListener('message', onMessage);

    // Toolbar buttons
    btnBack.addEventListener('click', () => post('goBack'));
    btnForward.addEventListener('click', () => post('goForward'));
    btnReload.addEventListener('click', () => post('reload'));
    btnHome.addEventListener('click', () => post('home'));
    btnNewTab.addEventListener('click', () => post('newTab'));

    // Address bar: Enter to navigate
    addressBar.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            post('navigate', { url: addressBar.value });
        }
    });
    // Keyboard shortcuts when focus is inside the chrome UI (address bar, buttons, etc.)
    document.addEventListener('keydown', (e) => {
    if (e.ctrlKey) {
        if (e.key === 'Tab' && !e.shiftKey) {
            e.preventDefault();
            post('nextTab');
            return;
        }
        if (e.key === 'Tab' && e.shiftKey) {
            e.preventDefault();
            post('prevTab');
            return;
        }
    }
    if (!e.ctrlKey) return;
    switch (e.key.toLowerCase()) {
        case 't': e.preventDefault(); post('newTab'); break;
        case 'w': e.preventDefault(); post('closeTab'); break;
        case 'l': e.preventDefault(); post('focusAddressBar'); break;
        case 'r': e.preventDefault(); post('reload'); break;
        case 'h': e.preventDefault(); post('home'); break;
        case 'arrowleft': e.preventDefault(); post('goBack'); break;
        case 'arrowright': e.preventDefault(); post('goForward'); break;
        case '[': e.preventDefault(); post('goBack'); break;
        case ']': e.preventDefault(); post('goForward'); break;
    }
});
}

// Helper: send JSON message to C# backend
function post(type, data = {}) {
    window.chrome.webview.postMessage({ type, ...data });
}

// Handle state broadcast from C#
function onMessage(event) {
    const data = event.data;
    if (data.type === 'state') {
        currentState = data;
        renderTabs(data.tabs);
        renderToolbar(data);
    }
}

function renderTabs(tabs) {
    tabsBar.innerHTML = '';
    tabs.forEach(tab => {
        const el = document.createElement('div');
        el.className = 'tab' + (tab.active ? ' active' : '');
        el.innerHTML = `
            <span class="tab-title">${escapeHtml(tab.title)}</span>
            <button class="tab-close" title="Close tab">×</button>
        `;

        // Click tab body to switch; click × to close
        el.addEventListener('click', (e) => {
            if (e.target.classList.contains('tab-close')) {
                e.stopPropagation();
                post('closeTab', { tabId: tab.id });
            } else {
                post('switchTab', { tabId: tab.id });
            }
        });

        // Middle-click anywhere on a tab to close it
        el.addEventListener('mousedown', (e) => {
            if (e.button === 1) { // middle mouse button
                e.preventDefault();
                post('closeTab', { tabId: tab.id });
            }
        });

        tabsBar.appendChild(el);
    });
}

function renderToolbar(state) {
    addressBar.value = state.url || '';
    btnBack.disabled = !state.canGoBack;
    btnForward.disabled = !state.canGoForward;
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

init();