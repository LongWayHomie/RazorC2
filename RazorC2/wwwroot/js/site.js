// wwwroot/js/site.js
(function () {
    'use strict';

    // --- Configuration ---
    const staleThresholdMinutes = 5;
    const liveTimeUpdateInterval = 1000; // Update every 1000ms (1 second)

    // --- DOM Elements ---
    const implantTableBody = document.getElementById('implant-table-body');
    const implantCountSpan = document.getElementById('implant-count');
    const logBox = document.getElementById('log-box');
    // Get NEW Tab/Pane elements
    const interactionTabBar = document.getElementById('interaction-tab-bar');
    const interactionPanesContainer = document.getElementById('interaction-panes-container');
    const terminalTargetSpan = document.getElementById('terminal-target-implant');
    // Command Form Elements
    const commandForm = document.getElementById('command-form');
    const hiddenSelectedImplantIdInput = document.getElementById('selectedImplantId');
    const terminalInputField = document.getElementById('terminal-input-field');
    const queueCommandBtn = document.getElementById('queue-command-btn');
    const commandStatusDiv = document.getElementById('command-status');
    // Context Menu Elements
    const contextMenu = document.getElementById('implant-context-menu');
    const ctxMenuTask = document.getElementById('ctx-menu-task');
    const ctxMenuKill = document.getElementById('ctx-menu-kill');
    const ctxMenuRemove = document.getElementById('ctx-menu-remove');
    let contextMenuTargetImplantId = null; // Store ID for menu actions
    let implantTableSortable = null;
    const IMPLANT_ORDER_KEY = 'implantOrder'; // Key for localStorage
    let signalRConnectionInitialized = false;

    // --- State ---
    let selectedImplantRow = null;
    let currentImplantId = null;
    let openTabs = new Set(); // Keep track of open implant IDs
    let activeTabId = null; // Keep track of the active implant ID
    const TAB_STATE_KEY = 'interactionTabState'; // Key for sessionStorage
    let liveTimeUpdateTimerId = null; // Variable to hold the interval ID
    // --- Add state flags for dragging ---
    let isDragging = false;          // Flag to indicate if a drag is active
    let pendingImplantData = null; // Store latest data received during drag
    // --- Popup State ---
    let checkinPopupTimeoutId = null; // To store the setTimeout ID for auto-hide

    // --- SignalR Connection Management ---
    let connection = null; // Holds the single connection instance
    let connectionStarting = false; // Flag to prevent race conditions during the async start()
    let signalREventHandlersAttached = false; // Flag to ensure handlers are attached only ONCE per connection object

    // Function to define ALL SignalR client-side event handlers
    // This should ONLY be called ONCE when a NEW connection object is created.
    function setupSignalREventHandlers() {
        if (!connection || signalREventHandlersAttached) {
            // Should not happen if logic in startSignalRConnection is correct, but safety check
            if (signalREventHandlersAttached) console.warn("SignalR: Attempted to attach event handlers multiple times.");
            else console.error("SignalR: setupSignalREventHandlers called with no connection object.");
            return;
        }
        console.log("SignalR: Attaching event handlers...");

        connection.on("NewImplantCheckin", (implantData) => {
            console.log("SignalR: Received NewImplantCheckin", implantData);
            // Ensure implantData is valid before showing popup
            if (implantData && implantData.hostname && implantData.remoteAddress && implantData.checkinTime && implantData.username) { // Check username exists too
                showCheckinPopup(implantData);
            } else {
                console.warn("Received incomplete implant data for check-in popup:", implantData);
            }
        });

        // Store last log message details for client-side deduplication
        let lastLogMessage = null;
        let lastLogTimestamp = null;

        // Handler for receiving implant list updates
        connection.on("UpdateImplantList", (implants) => {
            if (implantTableBody) {
                renderImplantTable(implants);
            }
        });

        // Appends a single new log entry - with deduplication
        connection.on("AppendLogEntry", (logEntry) => {
            if (logBox) { 
                const isPlaceholder = logBox.innerHTML.includes('text-muted');
                if (isPlaceholder) logBox.innerHTML = '';

                // Basic client-side deduplication (useful for rapid identical messages)
                if (logEntry && lastLogMessage === logEntry.message && lastLogTimestamp === logEntry.timestamp) {
                    return;
                }

                // Store this message for deduplication
                if (logEntry) {
                    lastLogMessage = logEntry.message;
                    lastLogTimestamp = logEntry.timestamp;
                    appendLogEntry(logEntry); // appendLogEntry has internal check for logBox now
                }
            }
        });

        // Handler for Initial Log Load (Check if logBox exists)
        connection.on("InitialLogView", (initialLogs) => {
            if (logBox) { // <-- CHECK ADDED
                console.log("SignalR: Received initial log view state");
                renderLogs(initialLogs); // renderLogs has internal check for logBox now
            }
        });

        // Handles updates for individual command tasks (Check if related elements exist)
        connection.on("CommandTaskUpdated", (update) => {
            // console.log("SignalR: Received command task update", update); // Noisy
            // Check if the update is for the currently ACTIVE interaction tab AND panes container exists
            if (interactionPanesContainer && update && update.implantId === activeTabId) { // <-- CHECK ADDED
                appendOrUpdateTerminalTask(update.task); // appendOrUpdateTerminalTask checks for target pane
            }
        });

        // --- Connection Lifecycle Handlers ---
        connection.onreconnecting((error) => {
            console.warn("SignalR: Connection lost. Attempting to reconnect...", error ? error.message : "No error details");
            setConnectionStatus("Reconnecting...");
            connectionStarting = false;
            signalREventHandlersAttached = false;
        });

        connection.onreconnected((connectionId) => {
            console.info(`SignalR: Connection reestablished. ConnectionId: ${connectionId}`);
            setConnectionStatus("Connected");
            connectionStarting = false;
            signalREventHandlersAttached = true;

            // Restore active tab's history after reconnection if needed (check elements exist)
            if (activeTabId && interactionPanesContainer) { 
                console.log(`[onreconnected] Reconnected, ensuring history for active tab: ${activeTabId}`);
                fetchCommandHistory(activeTabId).then(history => {
                    renderTerminal(activeTabId, history);
                }).catch(err => {
                    console.error(`Error fetching history after reconnect:`, err);
                });
            } else {
                console.log("[onreconnected] Reconnected, but no tab was active or pane container missing.");
            }
        });

        connection.onclose((error) => {
            console.error(`SignalR: Connection closed. Automatic Reconnect failed or connection stopped. Error:`, error ? error.message : "No error details");
            setConnectionStatus(`Disconnected: ${error ? error.message : 'Closed'}`);
            connection = null;
            connectionStarting = false;
            signalREventHandlersAttached = false;
            // setTimeout(startSignalRConnection, 10000); // Example retry
        });

        // Mark handlers as attached for this connection instance
        signalREventHandlersAttached = true;
        console.log("SignalR: Event handlers attached.");
    }

    // Function to start the SignalR connection if not already connected/connecting
    async function startSignalRConnection() {
        // Prevent starting if already starting OR if connection exists and is not Disconnected/null
        if (connectionStarting || (connection && connection.state !== signalR.HubConnectionState.Disconnected)) {
            console.log(`SignalR: Start request ignored. State: ${connection ? connection.state : 'null'}, Starting: ${connectionStarting}`);
            return connection && connection.state === signalR.HubConnectionState.Connected;
        }

        connectionStarting = true;
        setConnectionStatus("Connecting...");
        console.log("SignalR: Attempting to establish connection...");

        try {
            connection = new signalR.HubConnectionBuilder()
                .withUrl("/dashboardHub")
                .configureLogging(signalR.LogLevel.Warning)
                .withAutomaticReconnect()
                .build();

            signalREventHandlersAttached = false;
            setupSignalREventHandlers();

            await connection.start();
            console.log("SignalR: Connection established successfully.");
            setConnectionStatus("Connected");
            connectionStarting = false;

            // --- Initial Data Fetch / State Restoration (Conditional) ---
            if (activeTabId && interactionPanesContainer) { // <-- CHECK ADDED
                console.log(`[startSignalRConnection] Connection successful, ensuring history for restored active tab: ${activeTabId}`);
                const pane = document.getElementById(`pane-${activeTabId}`);
                if (pane && pane.innerHTML.includes('initial-pane-message')) {
                    fetchCommandHistory(activeTabId).then(history => {
                        renderTerminal(activeTabId, history);
                    }).catch(err => {
                        console.error(`Error fetching initial history for restored tab:`, err);
                    });
                } else {
                    console.log(`[startSignalRConnection] Pane for ${activeTabId} already populated or doesn't exist.`);
                }
            } else if (!activeTabId && interactionPanesContainer) { // Check added for default message scenario
                console.log("[startSignalRConnection] Connection successful, no tab was initially active.");
                renderTerminal(null, []); // Ensure default message shows IF panes container exists
            } else {
                console.log("[startSignalRConnection] Connection successful, but not on Index page or no active tab.");
            }

            return true;

        } catch (err) {
            console.error("SignalR: Connection failed to start:", err);
            setConnectionStatus(`Connection Failed: ${err.message}`);
            connection = null;
            connectionStarting = false;
            signalREventHandlersAttached = false;
            // setTimeout(startSignalRConnection, 5000); // Example retry
            return false;
        }
    }

    // --- Initialization ---
    document.addEventListener('DOMContentLoaded', () => {
        console.log("[DOMContentLoaded] Page loaded. Initializing SignalR and page-specific UI.");
        setConnectionStatus("Initializing...");

        // --- Initialize INDEX PAGE specific things ONLY if needed elements exist ---
        // Check for a combination of key Index-only elements
        if (implantTableBody && interactionTabBar && interactionPanesContainer && commandForm) {
            console.log("[DOMContentLoaded] Index page elements found. Initializing tabs, terminal, table, listeners.");

            loadTabState(); // Load tab state ONLY on Index page
            renderInitialTabsAndPanes(); // Render tabs/panes ONLY on Index page

            // *** Initialize SortableJS ***
            initSortable();

            if (activeTabId) {
                console.log("[DOMContentLoaded] Restored activeTabId from session:", activeTabId);
                if (hiddenSelectedImplantIdInput) hiddenSelectedImplantIdInput.value = activeTabId;
                updateTerminalHeader(activeTabId);
                updateCommandInputState();
            } else {
                console.log("[DOMContentLoaded] No active tab ID restored from session.");
                updateTerminalHeader(null);
                updateCommandInputState();
                if (openTabs.size === 0) renderTerminal(null, []); // Render default only if no tabs restored
            }

            // Setup Index-specific event listeners
            commandForm.addEventListener('submit', handleCommandSubmit);
            if (terminalInputField) terminalInputField.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' && queueCommandBtn && !queueCommandBtn.disabled) { // Check button too
                    e.preventDefault();
                    handleCommandSubmit(e);
                }
            });
            implantTableBody.addEventListener('click', handleTableClick);
            implantTableBody.addEventListener('contextmenu', handleRightClick);

            if (contextMenu) { // Context menu listeners only if menu exists
                document.addEventListener('click', hideContextMenu);
                if (ctxMenuTask) ctxMenuTask.addEventListener('click', handleContextMenuTask);
                if (ctxMenuKill) ctxMenuKill.addEventListener('click', handleContextMenuKill);
                if (ctxMenuRemove) ctxMenuRemove.addEventListener('click', handleContextMenuRemove);
            }

            updateCommandInputState(); // Set initial state

            // Set initial UI placeholders for Index page table
            if (implantTableBody && !implantTableBody.hasChildNodes()) {
                implantTableBody.innerHTML = '<tr><td colspan="7" class="text-center p-3 text-muted">Waiting for connection...</td></tr>';
            }
            startLiveTimeUpdates();
        } else {
            console.log("[DOMContentLoaded] Not on Index page (required elements missing). Skipping Index-specific UI init.");
            // Reset index-specific state variables if not on index page to prevent issues
            currentImplantId = null;
            activeTabId = null;
            openTabs = new Set();
            // No need to load or save tab state if not on index page
        }

        // Setup Log Box placeholder (IF it exists on the current page)
        if (logBox && !logBox.hasChildNodes()) {
            logBox.innerHTML = '<div class="text-muted p-3 text-center">Waiting for connection...</div>';
        }

        // --- START SIGNALR (GLOBAL) ---
        startSignalRConnection().then(connected => {
            console.log("[DOMContentLoaded] Initial SignalR start attempt finished.");
            if (!connected) {
                setConnectionStatus("Connection Failed");
                // If on Index page and connection failed, ensure default message is shown
                if (!activeTabId && interactionPanesContainer) renderTerminal(null, []);
            }
        }).catch(err => {
            console.error("[DOMContentLoaded] Error during initial SignalR start sequence:", err);
            setConnectionStatus("Connection Error");
            if (!activeTabId && interactionPanesContainer) renderTerminal(null, []);
        });
    });

    // --- Restore SortableJS Functions ---
    function initSortable() {
        if (implantTableBody) { // Check exists
            // Prevent re-initializing if it already exists
            if (implantTableSortable) {
                implantTableSortable.destroy();
            }
            implantTableSortable = new Sortable(implantTableBody, {
                animation: 150,
                ghostClass: 'sortable-ghost',
                handle: 'tr', // Allow dragging entire row
                // *** ADD onStart and Modify onEnd ***
                onStart: function (evt) {
                    isDragging = true; // Set flag when drag starts
                    console.log("SortableJS: Drag Start");
                },
                onEnd: function (evt) {
                    console.log("SortableJS: Drag End. Saving order...");
                    isDragging = false; // Unset flag when drag finishes
                    saveImplantOrder(); // Save the new order

                    // *** Check if an update arrived during the drag ***
                    if (pendingImplantData) {
                        console.log("SortableJS: Pending implant data found, rendering deferred update.");
                        renderImplantTable(pendingImplantData); // Render the stored data
                        pendingImplantData = null; // Clear the pending data
                    } else {
                        console.log("SortableJS: No pending implant data after drag.");
                        // Optional: You *could* re-render here even if no data was pending,
                        // just to ensure absolute consistency, but might cause flicker.
                        // Re-rendering only if data arrived during drag is usually better UX.
                    }
                },
            });
            console.log("SortableJS initialized.");
        } else {
            console.warn("SortableJS: Implant table body not found, cannot initialize.");
        }
    }

    function getStoredImplantOrder() {
        // Doesn't rely on DOM elements, safe
        try {
            const storedOrderJson = localStorage.getItem(IMPLANT_ORDER_KEY);
            const order = storedOrderJson ? JSON.parse(storedOrderJson) : [];
            // console.log("SortableJS: Retrieved order from localStorage.", order);
            return order;
        } catch (e) {
            console.error("SortableJS: Failed to get/parse implant order from localStorage:", e);
            return [];
        }
    }


    function saveImplantOrder() {
        if (!implantTableBody) return; // Check exists
        const rows = implantTableBody.querySelectorAll('tr[data-implant-id]');
        const currentOrder = Array.from(rows).map(row => row.getAttribute('data-implant-id'));
        try {
            localStorage.setItem(IMPLANT_ORDER_KEY, JSON.stringify(currentOrder));
            console.log("SortableJS: Implant order saved to localStorage.", currentOrder);
        } catch (e) {
            console.error("SortableJS: Failed to save implant order to localStorage:", e);
        }
    }

    // --- Live Time Update Functions ---
    // Function to start the periodic update

    function startLiveTimeUpdates() {
        // Clear any existing timer just in case
        if (liveTimeUpdateTimerId) {
            clearInterval(liveTimeUpdateTimerId);
        }
        console.log("[Live Time] Starting live updates for 'Last Seen' column.");
        // Run immediately once
        updateAllRelativeTimesDisplay();
        // Then run every second
        liveTimeUpdateTimerId = setInterval(updateAllRelativeTimesDisplay, liveTimeUpdateInterval);
    }

    // Function called by the interval to update all visible time cells
    // Function called BY the interval timer
    function updateAllRelativeTimesDisplay() {
        // Ensure the table body exists before trying to query cells
        if (!implantTableBody) {
            if (liveTimeUpdateTimerId) {
                console.warn("[Live Time] Implant table not found, stopping updates.");
                clearInterval(liveTimeUpdateTimerId); // Stop the timer if table vanishes
                liveTimeUpdateTimerId = null;
            }
            return;
        }

        const timeCells = implantTableBody.querySelectorAll('.last-seen-cell');
        // console.log(`[Live Time] Tick: Updating ${timeCells.length} cells...`); // Log for debugging timer ticks

        timeCells.forEach(cell => {
            const row = cell.closest('tr');
            if (!row) return;
            const lastSeenTimestamp = row.getAttribute('data-lastseen');
            if (!lastSeenTimestamp) return;

            try {
                const lastSeenDate = new Date(lastSeenTimestamp);
                if (isNaN(lastSeenDate)) return;

                const relativeTime = formatRelativeTime(lastSeenDate); // Calculate current relative time

                if (cell.textContent !== relativeTime) { // Update only if changed
                    cell.textContent = relativeTime;
                }
            } catch (e) {
                console.error("[Live Time] Error updating relative time for cell:", cell, e);
            }
        });
    }

    // --- Tab State Management (localStorage/sessionStorage) ---
    function saveTabState() {
        if (!interactionTabBar) return;
        try {
            const state = {
                openTabs: Array.from(openTabs),
                activeTabId: activeTabId
            };
            sessionStorage.setItem(TAB_STATE_KEY, JSON.stringify(state));
            console.log("Saved tab state:", state);
        } catch (e) { console.error("Failed to save tab state:", e); }
    }

    function loadTabState() {
        // Only run if on Index page
        if (!interactionTabBar) return;
        try {
            const storedState = sessionStorage.getItem(TAB_STATE_KEY);
            if (storedState) {
                const state = JSON.parse(storedState);
                openTabs = new Set(state.openTabs || []);
                activeTabId = state.activeTabId || null;
                console.log("Loaded tab state:", state);
            } else {
                openTabs = new Set();
                activeTabId = null;
                console.log("No tab state found in session storage.");
            }
        } catch (e) {
            console.error("Failed to load/parse tab state:", e);
            openTabs = new Set();
            activeTabId = null;
        }
    }

    function renderInitialTabsAndPanes() {
        // Check added at the start
        if (!interactionTabBar || !interactionPanesContainer) {
            console.warn("[renderInitialTabsAndPanes] Skipping execution, tab/pane containers not found.");
            return;
        }

        interactionTabBar.innerHTML = ''; // Clear placeholder
        interactionPanesContainer.innerHTML = ''; // Clear placeholder

        if (openTabs.size === 0) {
            interactionTabBar.innerHTML = '<li class="nav-item text-muted ms-2" id="no-tabs-message"><small>No active interactions</small></li>';
            updateTerminalHeader(null); // Update header (already checks element)
            return;
        }

        // Render tabs
        openTabs.forEach(implantId => {
            renderTab(implantId); // renderTab checks interactionTabBar
            renderPanePlaceholder(implantId); // renderPanePlaceholder checks interactionPanesContainer
        });

        // Activate the stored active tab (or the first one if active is invalid)
        if (activeTabId && openTabs.has(activeTabId)) {
            activateTab(activeTabId, false); // Activate without fetching history yet (activateTab checks elements)
        } else if (openTabs.size > 0) {
            // Activate the first tab in the set as fallback
            activateTab(openTabs.values().next().value, false);
        } else {
            activateTab(null, false); // No tabs active
        }
    }


    // --- Tab UI Rendering & Handling ---

    function renderTab(implantId) {
        if (!interactionTabBar || !implantId) return; // Check added

        const noTabsMsg = document.getElementById('no-tabs-message');
        if (noTabsMsg) noTabsMsg.remove();

        const shortId = getShortId(implantId);
        const tabId = `tab-${implantId}`;

        if (document.getElementById(tabId)) return; // Prevent duplicate tabs

        const li = document.createElement('li');
        li.className = 'nav-item';

        const a = document.createElement('a');
        a.className = 'nav-link';
        a.id = tabId;
        a.setAttribute('data-implant-id', implantId);
        a.setAttribute('role', 'tab');
        a.textContent = shortId;

        const closeBtn = document.createElement('button');
        closeBtn.className = 'tab-close-btn ms-2';
        closeBtn.innerHTML = '×';
        closeBtn.setAttribute('aria-label', 'Close tab');
        closeBtn.title = `Close interaction with ${shortId}`;
        closeBtn.onclick = (event) => {
            event.stopPropagation();
            closeTab(implantId); // closeTab checks elements
        };

        a.appendChild(closeBtn);
        a.onclick = () => activateTab(implantId, true); // activateTab checks elements

        li.appendChild(a);
        interactionTabBar.appendChild(li);
    }

    function renderPanePlaceholder(implantId) {
        if (!interactionPanesContainer || !implantId) return; // Check added

        const noPanesMsg = document.getElementById('no-panes-message');
        if (noPanesMsg) noPanesMsg.remove();

        const paneId = `pane-${implantId}`;
        if (document.getElementById(paneId)) return;

        const paneDiv = document.createElement('div');
        paneDiv.className = 'interaction-pane';
        paneDiv.id = paneId;
        paneDiv.style.display = 'none';
        paneDiv.innerHTML = `<div class="text-muted p-3 text-center initial-pane-message"><i class="fas fa-spinner fa-spin me-2"></i>Loading history for ${getShortId(implantId)}</div>`;

        interactionPanesContainer.appendChild(paneDiv);
    }


    function activateTab(implantId, fetchHistoryIfNeeded = true) {
        // Check elements are present before proceeding
        if (!interactionTabBar || !interactionPanesContainer) {
            console.warn("[activateTab] Skipping, required tab/pane elements missing.");
            // Reset state if called when not on Index page
            activeTabId = null;
            currentImplantId = null;
            if (hiddenSelectedImplantIdInput) hiddenSelectedImplantIdInput.value = '';
            updateCommandInputState(); // Update based on lack of active tab
            return;
        }

        console.log(`Activating tab: ${implantId}`);
        activeTabId = implantId;
        currentImplantId = implantId;
        if (hiddenSelectedImplantIdInput) hiddenSelectedImplantIdInput.value = implantId || ''; // Check added

        // Update Tab UI
        interactionTabBar.querySelectorAll('.nav-link').forEach(tab => {
            tab.classList.toggle('active', tab.getAttribute('data-implant-id') === implantId);
        });

        // Update Pane UI
        interactionPanesContainer.querySelectorAll('.interaction-pane').forEach(pane => {
            const isActive = pane.id === `pane-${implantId}`;
            pane.style.display = isActive ? 'block' : 'none';
            pane.classList.toggle('active', isActive);
        });

        updateTerminalHeader(implantId); // Already checks element
        updateCommandInputState(); // Already checks elements

        saveTabState(); // Checks interactionTabBar internally

        if (implantId && fetchHistoryIfNeeded) {
            const pane = document.getElementById(`pane-${implantId}`);
            if (pane && pane.innerHTML.includes('Loading history')) { // Check pane exists too
                fetchCommandHistory(implantId).then(history => {
                    renderTerminal(implantId, history); // renderTerminal checks elements
                }).catch(err => { console.error(`Error fetching history for ${implantId}:`, err); });
            }
        } else if (!implantId) {
            renderTerminal(null, []); // renderTerminal checks elements
            updateTerminalHeader(null); // Already checks element
        }
        updateCommandInputState(); // Redundant? Ensure state is correct
    }


    function closeTab(implantId) {
        // Check elements are present before proceeding
        if (!interactionTabBar || !interactionPanesContainer) {
            console.warn("[closeTab] Skipping, required tab/pane elements missing.");
            return;
        }
        console.log(`Closing tab: ${implantId}`);
        if (!openTabs.has(implantId)) return;

        openTabs.delete(implantId);

        const tabElement = document.getElementById(`tab-${implantId}`);
        if (tabElement) tabElement.closest('.nav-item').remove();

        const paneElement = document.getElementById(`pane-${implantId}`);
        if (paneElement) paneElement.remove();

        if (activeTabId === implantId) {
            activeTabId = null;
            currentImplantId = null;
            if (hiddenSelectedImplantIdInput) hiddenSelectedImplantIdInput.value = ''; // Check added
            if (openTabs.size > 0) {
                activateTab(Array.from(openTabs)[openTabs.size - 1], false);
            } else {
                activateTab(null, false);
                interactionTabBar.innerHTML = '<li class="nav-item text-muted ms-2" id="no-tabs-message"><small>No active interactions</small></li>';
            }
        }

        saveTabState();
        updateCommandInputState();
    }

    function openOrActivateTab(implantId) {
        // Check elements before proceeding
        if (!interactionTabBar || !interactionPanesContainer) {
            console.warn("[openOrActivateTab] Skipping, required tab/pane elements missing.");
            return;
        }
        if (!implantId) return;

        console.log(`Opening or activating tab for ${implantId}`);
        if (openTabs.has(implantId)) {
            activateTab(implantId, true);
        } else {
            openTabs.add(implantId);
            renderTab(implantId);
            renderPanePlaceholder(implantId);
            activateTab(implantId, true);
        }
        saveTabState();
    }


    // NEW Helper to update command input enable/disable state
    function updateCommandInputState() {
        const enabled = !!activeTabId; // Enable if any tab is active AND on Index page implicitly
        // Check elements before modifying
        if (terminalInputField) terminalInputField.disabled = !enabled;
        if (queueCommandBtn) queueCommandBtn.disabled = !enabled;
        if (terminalInputField) { // Check again for placeholder
            terminalInputField.placeholder = enabled ? "Enter command..." : "Select implant tab to interact...";
        }
    }

    // NEW Helper to update terminal header
    function updateTerminalHeader(implantId) {
        if (terminalTargetSpan) { // Check exists
            terminalTargetSpan.textContent = implantId ? getShortId(implantId) : 'None Selected';
        }
    }

    function showCheckinPopup(implantData) {
        if (checkinPopupTimeoutId) {
            clearTimeout(checkinPopupTimeoutId);
            checkinPopupTimeoutId = null;
            const existingPopup = document.getElementById('checkin-popup');
            if (existingPopup) {
                existingPopup.remove();
            }
        }

        const popup = document.createElement('div');
        popup.id = 'checkin-popup';
        popup.className = 'checkin-popup shadow';
        popup.setAttribute('role', 'alert');

        const checkinTime = new Date(implantData.checkinTime).toLocaleTimeString('en-GB', { hour12: false });

        popup.innerHTML = `
            <div class="popup-header">
                <i class="fas fa-check-circle text-success me-2"></i>
                <strong>New Check-in!</strong>
                <button type="button" class="btn-close btn-close-white" aria-label="Close"></button>
            </div>
            <div class="popup-body">
                <div><strong>Host:</strong> ${escapeHtml(implantData.hostname)}</div>
                <div><strong>User:</strong> ${escapeHtml(implantData.username || 'N/A')}</div>
                <div><strong>From:</strong> ${escapeHtml(implantData.remoteAddress)}</div>
                <div><strong>Time:</strong> ${checkinTime}</div>
            </div>
        `;

        document.body.appendChild(popup);

        checkinPopupTimeoutId = setTimeout(() => {
            popup.style.opacity = '0';
            setTimeout(() => {
                if (document.body.contains(popup)) {
                    popup.remove();
                }
                checkinPopupTimeoutId = null;
            }, 500);
        }, 15000);

        const closeButton = popup.querySelector('.btn-close');
        if (closeButton) {
            closeButton.onclick = () => {
                clearTimeout(checkinPopupTimeoutId);
                checkinPopupTimeoutId = null;
                popup.remove();
            };
        }

        setTimeout(() => {
            popup.style.opacity = '1';
            popup.style.transform = 'translateX(0)';
        }, 10);
    }


    // --- Context Menu Logic (Index Page Specific) ---
    function handleRightClick(event) {
        // Assumes this is only called if implantTableBody listener is attached
        const targetRow = event.target.closest('tr');
        if (!targetRow) return;
        const implantId = targetRow.getAttribute('data-implant-id');
        if (!implantId) return;
        event.preventDefault();
        contextMenuTargetImplantId = implantId;
        showContextMenu(event.clientX, event.clientY); // showContextMenu checks for contextMenu element
    }

    function showContextMenu(x, y) {
        if (!contextMenu) return; // Check exists
        const menuWidth = contextMenu.offsetWidth;
        const menuHeight = contextMenu.offsetHeight;
        const winWidth = window.innerWidth;
        const winHeight = window.innerHeight;
        x = (x + menuWidth > winWidth) ? winWidth - menuWidth - 5 : x;
        y = (y + menuHeight > winHeight) ? winHeight - menuHeight - 5 : y;
        contextMenu.style.left = `${x}px`;
        contextMenu.style.top = `${y}px`;
        contextMenu.style.display = 'block';
    }


    function hideContextMenu() {
        if (contextMenu) contextMenu.style.display = 'none'; // Check exists
        contextMenuTargetImplantId = null;
    }

    // --- Context Menu Action Handlers ---
    function handleContextMenuTask(event) {
        if (contextMenuTargetImplantId) {
            openOrActivateTab(contextMenuTargetImplantId); // openOrActivateTab checks elements
            hideContextMenu(); // Checks element
        }
    }

    function handleContextMenuKill(event) {
        if (!commandForm) return; // Check needed element
        if (contextMenuTargetImplantId) {
            if (confirm(`Are you sure you want to send the KILL command to implant ${contextMenuTargetImplantId}?`)) {
                console.log(`Sending KILL command to ${contextMenuTargetImplantId}`);
                queueKillCommand(contextMenuTargetImplantId); // queueKillCommand checks element
            }
            hideContextMenu(); // Checks element
        }
    }

    async function handleContextMenuRemove(event) {
        if (contextMenuTargetImplantId) {
            if (confirm(`Are you sure you want to REMOVE implant ${contextMenuTargetImplantId} from the UI?\n(The implant process may still be running).`)) {
                console.log(`Removing implant ${contextMenuTargetImplantId} from UI`);
                await removeImplantFromUI(contextMenuTargetImplantId); // removeImplantFromUI checks elements if needed
            }
            hideContextMenu(); // Checks element
        }
    }

    // Queues the special 'implant_exit' command
    async function queueKillCommand(implantId) {
        if (!commandForm) return; // Check exists
        const commandText = "implant_exit";
        console.log(`Queueing command: ${commandText} for ${implantId}`);
        try {
            const response = await fetch(commandForm.action, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    SelectedImplantId: implantId,
                    CommandText: commandText
                })
            });
            if (response.ok) {
                updateCommandStatus(`Kill command queued for ${implantId}.`, true); // Checks element
            } else {
                const result = await response.json();
                updateCommandStatus(`Failed to queue kill command: ${result.message || response.status}`, false); // Checks element
            }
        } catch (error) {
            console.error('Error queueing kill command:', error);
            updateCommandStatus('Network error trying to queue kill command.', false); // Checks element
        }
    }

    // Calls the API to remove implant from server list
    async function removeImplantFromUI(implantId) {
        console.log(`Sending DELETE request for ${implantId}`);
        try {
            const response = await fetch(`/api/ui/implants/${implantId}`, {
                method: 'DELETE'
            });

            if (response.ok) {
                console.log(`Successfully requested removal of ${implantId}`);
                updateCommandStatus(`Implant ${implantId} removed from UI.`, true); // Checks element
                // Check elements before calling selectImplant
                if (implantId === currentImplantId && (interactionTabBar || interactionPanesContainer)) {
                    selectImplant(null); // selectImplant checks elements
                }
            } else {
                const result = await response.json();
                console.error(`Failed to remove implant ${implantId}: ${response.status}`);
                updateCommandStatus(`Failed to remove implant: ${result.message || response.status}`, false); // Checks element
            }
        } catch (error) {
            console.error('Error removing implant:', error);
            updateCommandStatus('Network error trying to remove implant.', false); // Checks element
        }
    }

    // REVISED DEBUG: Creates or replaces the complete task block using data from SignalR
    function appendOrUpdateTerminalTask(task) {
        const funcName = "[appendOrUpdateTerminalTask]"; // For easier log filtering

        // --- Initial Checks ---
        if (!task || !task.commandId) { // Check if task object itself is valid
            return;
        }
        
        const targetPane = document.getElementById(`pane-${activeTabId}`);
        if (!targetPane) {
            console.error(`${funcName} Cannot find active pane: pane-${activeTabId}`);
            return;
        }
        // ---------------------

        console.log(`${funcName} Processing update for ACTIVE task ${task.commandId}. Status: ${task.status}, Result: ${task.result ? "'" + task.result.substring(0, 50) + "...'" : "(null)"}`);

        const taskBlockId = `task-${task.commandId}`;
        const completeTaskHtml = formatTerminalTaskBlock(task); // Generate final HTML first

        // Log the generated HTML specifically when completed/error
        if (task.status === 3 || task.status === 4) {
            console.log(`${funcName} Generated COMPLETE/ERROR HTML for ${taskBlockId}:\n`, completeTaskHtml);
        } else if (task.status === 1) {
            console.log(`${funcName} Generated ISSUED HTML for ${taskBlockId}:\n`, completeTaskHtml);
        }

        let existingBlockById = targetPane.querySelector(`#${taskBlockId}`); // Search within target pane

        if (existingBlockById) {
            console.log(`${funcName} Found existing block #${taskBlockId}. Replacing its outerHTML.`);
            try {
                existingBlockById.outerHTML = completeTaskHtml;
                // Verify replacement happened
                let newBlock = document.getElementById(taskBlockId); // Re-select by ID
                if (newBlock && newBlock.innerHTML === completeTaskHtml.substring(completeTaskHtml.indexOf('>') + 1, completeTaskHtml.lastIndexOf('<'))) { // Basic content check
                    console.log(`${funcName} Replacement of #${taskBlockId} seems successful.`);
                } else {
                    console.error(`${funcName} Replacement of #${taskBlockId} FAILED or content mismatch. New block:`, newBlock);
                }
            } catch (replaceError) {
                console.error(`${funcName} Error during outerHTML replacement for #${taskBlockId}:`, replaceError);
            }
        } else {
            // Fallback: If the specific block ID isn't found, try replacing the last pending echo
            let lastPendingEcho = targetPane.querySelector('.terminal-task-block.pending-echo:last-of-type');
            if (lastPendingEcho) {
                console.warn(`${funcName} Block #${taskBlockId} not found, but found pending echo. Replacing pending echo.`);
                try {
                    lastPendingEcho.outerHTML = completeTaskHtml;
                    let newBlock = document.getElementById(taskBlockId); // Check if ID was added
                    if (newBlock) {
                        console.log(`${funcName} Pending echo replaced successfully with #${taskBlockId}.`);
                    } else {
                        console.error(`${funcName} Pending echo replacement FAILED. Block with ID ${taskBlockId} not found after replacement.`);
                    }
                } catch (replaceError) {
                    console.error(`${funcName} Error during pending echo outerHTML replacement:`, replaceError);
                }
            } else {
                // If no block with the ID exists AND no pending echo is found, just append.
                console.warn(`${funcName} No existing block or pending echo found for #${taskBlockId}. Appending.`);
                targetPane.insertAdjacentHTML('beforeend', completeTaskHtml);
            }
        }

        // Scroll to bottom
        setTimeout(() => {
            if (targetPane) {
                const isNearBottom = targetPane.scrollHeight - targetPane.clientHeight <= targetPane.scrollTop + 30; // Check if already near bottom
                if (isNearBottom) { // Only force scroll if user is already near the end
                    targetPane.scrollTop = targetPane.scrollHeight;
                    console.log(`${funcName} Scrolled pane-${activeTabId} to bottom.`);
                } else {
                    console.log(`${funcName} User scrolled up in pane-${activeTabId}, not auto-scrolling.`);
                }
            }
        }, 50); // Increased timeout slightly
    }

    // --- Fetch Command History On Demand ---
    async function fetchCommandHistory(implantId) {
        if (!implantId) return [];
        try {
            const response = await fetch(`/api/ui/implants/${implantId}/history`);
            if (!response.ok) {
                if (response.status === 404) {
                    console.log(`History fetch: Implant ${implantId} not found (or has no history).`);
                    return [];
                }
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            const historyData = await response.json();
            console.log(`[fetchCommandHistory] Received history data for ${implantId}:`, historyData);
            return historyData || [];
        } catch (error) {
            console.error(`Error fetching command history for ${implantId}:`, error);
            return [];
        }
    }

    // --- Rendering ---
    function renderImplantTable(implantsDataFromSignalR) {
        // if dragging is on, stop the render to prevent duplicates
        if (isDragging) {
            console.log("[renderImplantTable] Drag in progress. Storing data and deferring render.");
            pendingImplantData = implantsDataFromSignalR;
            return;
        }
        // If not dragging, proceed as normal
        //console.log("[renderImplantTable] Rendering table based on manual order."); //noisy
        pendingImplantData = null; // Clear any old pending data

        if (!implantTableBody || !implantCountSpan) return;
        implantCountSpan.textContent = implantsDataFromSignalR.length;

        let tableHtml = '';
        let selectedImplantStillExists = false;
        const now = new Date(); // Get current time once for staleness checks

        if (!implantsDataFromSignalR || implantsDataFromSignalR.length === 0) {
            implantTableBody.innerHTML = '<tr><td colspan="7" class="text-center p-5 text-muted">No implants connected.</td></tr>';
            if (currentImplantId) { selectImplant(null); }
            return;
        }

        // --- REVISED SORTING LOGIC (Manual Order Only) ---

        // 1. Get data and manual order
        const currentImplantsMap = new Map(implantsDataFromSignalR.map(i => [i.id, i]));
        const storedOrder = getStoredImplantOrder();

        // 2. Create final list based ONLY on stored order, adding new ones at the end
        let orderedImplants = []; // This will be the final render order
        const processedIds = new Set();

        // Add implants based on stored order first
        storedOrder.forEach(id => {
            if (currentImplantsMap.has(id)) {
                orderedImplants.push(currentImplantsMap.get(id));
                processedIds.add(id);
            }
            // If an ID in storedOrder doesn't exist in current data, it's simply skipped
        });

        // Add any implants from current data not in the stored order (new implants)
        implantsDataFromSignalR.forEach(implant => {
            if (!processedIds.has(implant.id)) {
                //console.log(`[renderImplantTable] New implant ${getShortId(implant.id)} not in stored order, appending.`); //noisy
                orderedImplants.push(implant);
            }
        });

        // --- END REVISED SORTING LOGIC ---

        // --- Render logic using the 'orderedImplants' list (manual order) ---
        orderedImplants.forEach(implant => {
            const lastSeenDate = new Date(implant.lastSeen);
            // *** Calculate staleness HERE, only for applying CSS class ***
            const timeSinceSeen = (now - lastSeenDate) / (1000 * 60);
            const isStale = timeSinceSeen > staleThresholdMinutes;
            const rowClass = isStale ? 'implant-stale' : ''; // Apply class based on calculation
            // *** End staleness calculation for CSS ***

            const isActiveSelection = implant.id === currentImplantId;

            if (implant.id === currentImplantId) selectedImplantStillExists = true;

            tableHtml += `
                <tr class="${rowClass} ${isActiveSelection ? 'table-active' : ''}" data-implant-id="${implant.id}" data-lastseen="${implant.lastSeen}" data-isstale="${isStale}">
                   <td class="implant-id" title="${implant.id}">${getShortId(implant.id)}</td>
                   <td>${implant.hostname || 'N/A'}</td>
                   <td>${implant.remoteAddress || 'N/A'}</td>
                   <td>${implant.username || 'N/A'}</td>
                   <td class="process-name" title="${implant.processName || ''}">${implant.processName || 'N/A'}</td>
                   <td>${implant.processId || 'N/A'}</td>
                   <td class="last-seen-cell" title="${lastSeenDate.toISOString()}">Calculating...</td>
                </tr>`;
        });

        implantTableBody.innerHTML = tableHtml;

        updateAllRelativeTimesDisplay(); // Update times immediately

        // Handle selection
        if (!selectedImplantStillExists && currentImplantId) {
            selectImplant(null);
        } else if (selectedImplantStillExists) {
            selectedImplantRow = implantTableBody.querySelector(`tr[data-implant-id="${currentImplantId}"]`);
        } else {
            selectedImplantRow = null;
        }
    }


    function renderLogs(logs) {
        if (!logBox) return;
        if (!logs || logs.length === 0) {
            logBox.innerHTML = '<div class="text-muted p-3 text-center">No log messages yet.</div>';
            return;
        }
        let logHtml = '';
        // Sort logs by timestamp in ascending order (oldest first)
        logs.sort((a, b) => new Date(a.timestamp) - new Date(b.timestamp));
        // Render all logs received initially
        logs.forEach(logEntry => {
            logHtml += formatLogEntry(logEntry);
        });
        logBox.innerHTML = logHtml;
        // Scroll to bottom
        logBox.scrollTop = logBox.scrollHeight;
    }

    // NEW: Appends a single log entry
    function appendLogEntry(logEntry) {
        if (!logBox || !logEntry) return;
        const logHtml = formatLogEntry(logEntry);
        const isScrolledUp = logBox.scrollTop < (logBox.scrollHeight - logBox.clientHeight - 50);
        logBox.insertAdjacentHTML('beforeend', logHtml);

        // If we were near the bottom, scroll all the way down AFTER adding
        if (!isScrolledUp) {
            setTimeout(() => {
                logBox.scrollTop = logBox.scrollHeight;
            }, 0);
        }
    }

    // NEW: Helper to format a single log entry
    function formatLogEntry(logEntry) {
        const timestamp = new Date(logEntry.timestamp).toLocaleTimeString('en-GB', { hour12: false });
        const message = escapeHtml(logEntry.message);
        return `<div><span class="log-timestamp">[${timestamp}]</span> ${message}</div>`;
    }

    // REVISED renderTerminal function
    function renderTerminal(implantId, history) {
        // Check elements before proceeding
        let targetPane = implantId ? document.getElementById(`pane-${implantId}`) : null;

        // 1. Determine the target pane to render into
        if (implantId) {
            targetPane = document.getElementById(`pane-${implantId}`);
            if (!targetPane) {
                console.error(`[renderTerminal] Cannot find target pane element for ID: pane-${implantId}`);
                return; // Cannot render if pane doesn't exist
            }
        } else {
            // Case: No implant selected. Show default message in the main container
            // ONLY if there are absolutely no tabs open.
            if (openTabs.size === 0 && interactionPanesContainer) {
                interactionPanesContainer.innerHTML = `<div class="text-muted p-5 text-center" id="no-panes-message">Select an implant via the context menu to start interaction.</div>`;
            }
            // If tabs are open but none active, don't change anything here, activateTab handles it.
            return;
        }

        // --- We now have a valid targetPane for a specific implantId ---

        // 2. Handle history states (Empty vs Populated)
        let terminalHtml = ''; // Initialize empty HTML string

        if (!history || history.length === 0) {
            // State: Implant selected, but NO history. Show "Ready..." message.
            console.log(`[renderTerminal] No history for ${getShortId(implantId)}, showing blank page`);
            // Set content immediately for empty state
            targetPane.innerHTML = '';
            targetPane.scrollTop = 0; // Reset scroll
            return; // Stop further processing
        } else {
            // State: History exists, format it.
            console.log(`[renderTerminal] Rendering ${history.length} history items for ${getShortId(implantId)}`);
            try {
                history.forEach(task => {
                    if (task && task.commandId) {
                        terminalHtml += formatTerminalTaskBlock(task);
                    } else {
                        console.warn("[renderTerminal] Skipping invalid item in history array:", task);
                    }
                });
                // Set content after successful formatting
                targetPane.innerHTML = terminalHtml;
            } catch (formatError) {
                // Handle potential errors during formatting
                console.error("[renderTerminal] Error during history formatting:", formatError);
                targetPane.innerHTML = `<div class="error-line p-3">Error rendering command history. Check console.</div>`;
                return; // Stop execution on error
            }
        }

        // 3. Scroll to the bottom (only if history was rendered)
        setTimeout(() => {
            if (targetPane) { // Check if element still exists
                targetPane.scrollTop = targetPane.scrollHeight;
            }
        }, 0);
    }

    // REVISED: Main formatter - Adds a wrapper div with ID for targeting updates
    function formatTerminalTaskBlock(task) {
        const timeFormat = { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' };
        const cmdTime = task.issuedAt ? new Date(task.issuedAt).toLocaleTimeString('en-GB', timeFormat) : '??:??:??';
        const resTime = task.completedAt ? new Date(task.completedAt).toLocaleTimeString('en-GB', timeFormat) : cmdTime;

        let blockContent = ''; // Content inside the wrapper div

        const implantIdShort = getShortId(currentImplantId);
        const escapedCommand = escapeHtml(task.commandText || '');

        // Line 1: Tasking Info
        blockContent += `<div class="terminal-line terminal-task-line">` + // Added common terminal-line class
            `<span class="terminal-timestamp">[${cmdTime}]</span> ` +
            `<span class="prefix-tasked">[*]</span> ` + // Yellow prefix
            `Tasked ${implantIdShort} with "${escapedCommand}"` +
            `</div>`;

        // Line 2: Response Info (add prefix span)
        if (task.status === 3 || task.status === 4) { // 3 = Completed, 4 = Error
            let resultText = task.result ? escapeHtml(task.result).replace(/\n/g, '\n  ') : '(No output)'; // Keep indent for multiline
            const lineClass = 'result-line'; // Use the same structural class
            const prefixClass = task.status === 4 ? 'prefix-error' : 'prefix-response';
            const prefixText = task.status === 4 ? '[-]' : '[+]';

            blockContent += `<div class="terminal-line ${lineClass}">` + // Apply neutral line class
                `<span class="terminal-timestamp">[${resTime}]</span> ` +
                `<span class="${prefixClass}">${prefixText}</span> ` + // Prefix span gets the error color if needed
                `Response ${implantIdShort}: ${resultText}` + // This text will NOT be red now
                `</div>`;
        } else if (task.status === 1) { // 1 = Issued
            blockContent += ''
        }

        // Wrap the content in a div with a unique ID based on the command ID
        return `<div id="task-${task.commandId}" class="terminal-task-block">${blockContent}</div>`;
    }

    // --- Event Handlers & Logic ---
    function handleTableClick(event) {
        // Only attached if implantTableBody exists
        const taskButton = event.target.closest('.task-btn');
        if (taskButton) {
            const implantId = taskButton.getAttribute('data-implant-id');
            selectImplant(implantId); // selectImplant checks elements
        }
        event.stopPropagation();
    }

    async function selectImplant(implantId) {
        if (currentImplantId === implantId) return;

        currentImplantId = implantId;
        hiddenSelectedImplantIdInput.value = implantId || '';

        // Store the selected implant ID in session storage
        // *** FIX: Store/Remove selected ID in sessionStorage ***
        if (implantId) {
            try {
                sessionStorage.setItem('selectedImplantId', implantId);
            } catch (e) {
                console.error("Failed to set sessionStorage item:", e); // Handle potential storage errors (e.g., quota exceeded)
            }
        } else {
            try {
                sessionStorage.removeItem('selectedImplantId');
            } catch (e) {
                console.error("Failed to remove sessionStorage item:", e);
            }
        }

        // Update table highlighting
        if (selectedImplantRow) {
            selectedImplantRow.classList.remove('table-active', 'text-white');
            selectedImplantRow = null;
        }
        if (implantId) {
            const newSelectedRow = implantTableBody.querySelector(`tr[data-implant-id="${implantId}"]`);
            if (newSelectedRow) {
                newSelectedRow.classList.add('table-active', 'text-white');
                selectedImplantRow = newSelectedRow;
            }
        }

        // Update terminal header and input state
        if (terminalTargetSpan) {
            terminalTargetSpan.textContent = implantId ? getShortId(implantId) : 'None Selected'; // Show full ID
        }
        if (terminalInputField) {
            terminalInputField.disabled = !implantId;
            terminalInputField.value = '';
        }
        if (queueCommandBtn) {
            queueCommandBtn.disabled = !implantId;
        }

        // Fetch and Render History for the selected implant
        let history = [];
        if (implantId) {
            // Fetch history and render when complete
            fetchCommandHistory(implantId).then(history => {
                renderTerminal(implantId, history);
            }).catch(err => { // Add catch for fetch history errors
                console.error(`Error fetching history for ${implantId}:`, err);
                renderTerminal(implantId, []); // Show "Ready..." or empty state on error
            });
        } else {
            // If no implant ID, clear the terminal immediately
            renderTerminal(null, []); // Render the "Select implant..." message
        }

        // Final UI adjustments
        if (implantId) { terminalInputField.focus(); }
        updateCommandStatus(''); // Clear any previous command status
    }

    async function handleCommandSubmit(event) {
        event.preventDefault();
        if (!currentImplantId) return;

        const commandText = terminalInputField.value.trim();
        if (!commandText) return;

        // *** GET CURRENT ACTIVE PANE ***
        const activePane = document.getElementById(`pane-${activeTabId}`);
        if (!activePane) {
            console.error("Cannot submit command, active pane not found!");
            updateCommandStatus("Error: No active interaction tab found.", false);
            return;
        }
        // ******************************

        console.log(`[handleCommandSubmit] Initiated. Implant: ${currentImplantId}, Command: ${commandText}`); // Log start

        setCommandFormState(true);
        updateCommandStatus('Sending...', null);
        terminalInputField.value = '';

        // --- IMMEDIATE LOCAL ECHO ---
        const echoTimeFormat = { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' };
        const echoTime = new Date().toLocaleTimeString('en-GB', echoTimeFormat);
        const implantIdShort = getShortId(currentImplantId);
        const escapedCommand = escapeHtml(commandText);

        const echoHtml = `<div class="terminal-task-block pending-echo">` + // Use marker class
            `<div class="terminal-line terminal-task-line">` +
            `<span class="terminal-timestamp">[${echoTime}]</span> ` +
            `<span class="prefix-tasked">[*]</span> ` +
            `Tasked ${implantIdShort} with "${escapedCommand}"` +
            `</div>` +
            `</div>`;

        activePane.insertAdjacentHTML('beforeend', echoHtml); // Append echo to active pane
        setTimeout(() => { if (activePane) activePane.scrollTop = activePane.scrollHeight; }, 0);
        // -----------------------------

        console.log("[handleCommandSubmit] Echo appended. Preparing fetch...");

        try {
            const urlToPost = '/'; // Post to the root (IndexModel.OnPost)
            console.log(`[handleCommandSubmit] Fetching POST to: ${urlToPost}`);

            const response = await fetch(urlToPost, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    // Add Antiforgery header here if needed
                },
                body: JSON.stringify({
                    SelectedImplantId: currentImplantId,
                    CommandText: commandText
                })
            });

            console.log(`[handleCommandSubmit] Fetch response status: ${response.status}`);

            if (response.ok) {
                updateCommandStatus('Command queued successfully.', true);
            } else {
                const errorMsg = await getResponseError(response);
                const errorHtml = `<div class="terminal-line error-line"> [-] Queueing Failed: ${escapeHtml(errorMsg)} </div>`;
                
                activePane.insertAdjacentHTML('beforeend', errorHtml); // Append error to active pane
                setTimeout(() => { if (activePane) activePane.scrollTop = activePane.scrollHeight; }, 0);
            }

        } catch (error) {
            const networkErrorMsg = getNetworkErrorMsg(error);
            const errorHtml = `<div ...> ... [-] ${escapeHtml(networkErrorMsg)}</div>`;
            activePane.insertAdjacentHTML('beforeend', errorHtml); // Append error to active pane
            setTimeout(() => { if (activePane) activePane.scrollTop = activePane.scrollHeight; }, 0);
        } finally {
            console.log("[handleCommandSubmit] Fetch finished (finally block).");
            setCommandFormState(false);
            if (terminalInputField && !terminalInputField.disabled) {
                terminalInputField.focus();
            }
        }
    }

    async function getResponseError(response) {
        let errorMsg = `HTTP Error ${response.status}`;
        try {
            const result = await response.json();
            errorMsg = result.message || errorMsg;
        } catch (parseError) {
            errorMsg = response.statusText || errorMsg;
        }
        return errorMsg;
    }

    function getNetworkErrorMsg(error) {
        console.error('[handleCommandSubmit] Fetch CATCH block error:', error);
        let networkErrorMsg = 'Network Error submitting command.';
        if (error instanceof TypeError) {
            networkErrorMsg = `Network/Fetch Error: ${error.message || 'Could not send request.'}`;
        } else if (error.message) {
            networkErrorMsg += ` (${error.message})`;
        }
        return networkErrorMsg;
    }

    function setCommandFormState(isSubmitting) {
        if (queueCommandBtn) queueCommandBtn.disabled = isSubmitting || !currentImplantId;
        if (terminalInputField) terminalInputField.disabled = isSubmitting || !currentImplantId;

        const icon = queueCommandBtn ? queueCommandBtn.querySelector('i') : null;
        if (icon) {
            icon.classList.toggle('fa-paper-plane', !isSubmitting);
            icon.classList.toggle('fa-spinner', isSubmitting);
            icon.classList.toggle('fa-spin', isSubmitting);
        }
    }

    function updateCommandStatus(message, isSuccess, duration = 4000) {
        if (!commandStatusDiv) return;
        commandStatusDiv.textContent = message;
        // Define CSS classes for styling the status (e.g., success, error, info)
        commandStatusDiv.className = 'terminal-status text-small'; // Reset base class
        if (message) {
            const statusClass = isSuccess === true ? 'text-success' : (isSuccess === false ? 'text-danger' : 'text-info'); // Example using text color classes
            commandStatusDiv.classList.add(statusClass);
        }

        if (isSuccess !== null && message) {
            setTimeout(() => {
                if (commandStatusDiv.textContent === message) {
                    commandStatusDiv.textContent = '';
                    commandStatusDiv.className = 'terminal-status text-small';
                }
            }, duration);
        }
    }

    // --- Utility Functions ---
    // NEW: Helper to get shortened ID for display
    function getShortId(fullId) {
        if (!fullId) return '???'; // Handle null/empty case
        // Return only the first 8 characters (or fewer if ID is shorter)
        return fullId.substring(0, 8); // <-- Removed appending '...'
    }


    function formatRelativeTime(date) {
        // Keep your existing accurate relative time logic here
        if (!(date instanceof Date)) { date = new Date(date); }
        if (isNaN(date)) return 'Invalid date';

        const now = new Date();
        const diffSeconds = Math.round((now - date) / 1000);

        if (diffSeconds < 1) return '0s';
        if (diffSeconds < 60) return `${diffSeconds}s`;
        const diffMinutes = Math.round(diffSeconds / 60);
        if (diffMinutes < 60) return `${diffMinutes}m`;
        const diffHours = Math.round(diffMinutes / 60);
        if (diffHours < 24) return `${diffHours}h`;
        const diffDays = Math.round(diffHours / 24);
        if (diffDays <= 3) return `${diffDays}d`;
        return date.toLocaleDateString([], { month: 'short', day: 'numeric' });
    }

    function escapeHtml(unsafe) {
        if (!unsafe) return '';
        return unsafe
            .replace(/&/g, "&")
            .replace(/</g, "<")
            .replace(/>/g, ">")
            .replace(/"/g, "\"")
            .replace(/'/g, "'");
    }

    function setConnectionStatus(status) {
        console.log(`UI Status: ${status}`); // Keep console log for debugging
        const statusSpan = document.getElementById('connection-status-span');
        if (!statusSpan) {
            console.error("Error: Footer status span 'connection-status-span' not found!");
            return;
        }
        if (statusSpan) {
            let iconClass = 'fas fa-question-circle text-muted'; // Default icon/color
            let statusText = status;
            let textColorClass = 'text-muted';

            if (status.startsWith('Connected')) {
                iconClass = 'fas fa-check-circle';
                textColorClass = 'text-success';
                statusText = "Connected"; // Use concise text
            } else if (status.startsWith('Reconnecting')) {
                iconClass = 'fas fa-sync fa-spin'; // Spinning icon
                textColorClass = 'text-warning';
                statusText = "Reconnecting...";
            } else if (status.startsWith('Disconnected') || status.startsWith('Connection Failed')) {
                iconClass = 'fas fa-times-circle';
                textColorClass = 'text-danger';
                statusText = "Disconnected"; // Use concise text
            } else if (status.startsWith('Connecting')) {
                iconClass = 'fas fa-spinner fa-spin'; // Spinning icon
                textColorClass = 'text-info';
                statusText = "Connecting...";
            } else {
                // Keep the default icon/color for unknown statuses
                statusText = "Status: Unknown";
            }

            // Use innerHTML to include the icon and apply text color class
            statusSpan.innerHTML = `<i class="${iconClass} ${textColorClass} me-1"></i><span class="${textColorClass}">${statusText}</span>`;
        }
    }

})(); // IIFE