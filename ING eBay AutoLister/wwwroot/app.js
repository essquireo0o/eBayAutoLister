(() => {
  let nlImageBase64 = '';
  let nlMimeType = 'image/jpeg';
  let ebayToken = '';
  let activeOfferId = '';
  let activeListingId = '';
  let activeSku = '';
  let activeListingStatus = '';
  let pendingDraftPayload = null;
  let cachedListings = [];
  let cachedPolicies = null; // { fulfillmentPolicies, paymentPolicies, returnPolicies }
  let viewMode = 'cards';
  let isConnected = false;

  document.addEventListener('DOMContentLoaded', init);

  async function guardedFetch(url, opts) {
    return fetch(url, opts);
  }

  // ── Reliability layer ─────────────────────────────────────────────────────
  //
  // One way to call the app, one way to describe a failure, and one place that decides whether a
  // Retry button is honest. Before this, each caller wrote its own `fetch(...).then(r => r.json())`
  // and its own `catch (err) { show(err.message) }`, so a rate limit, an expired eBay token and a
  // 500 HTML error page all reached the seller as the same red line of unusable text — and the
  // AI paths, which are the slowest and most expensive to redo, had the least helpful messages.

  // A generated listing can legitimately take minutes at high thinking effort, so the ceiling is
  // generous. It exists so a request that will never come back stops looking like one that is still
  // working — an unbounded spinner is the failure sellers read as "this app is broken".
  const AI_TIMEOUT_MS = 5 * 60 * 1000;
  const PUBLISH_TIMEOUT_MS = 4 * 60 * 1000;
  const QUICK_TIMEOUT_MS = 60 * 1000;

  // Always resolves. Never throws, never rejects — the caller branches on `ok`.
  async function callApi(url, { method = 'GET', body = null, timeoutMs = QUICK_TIMEOUT_MS } = {}) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const res = await fetch(url, {
        method,
        signal: controller.signal,
        headers: body ? { 'Content-Type': 'application/json' } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });

      const text = await res.text();
      let data = null;
      try { data = text ? JSON.parse(text) : null; } catch { data = null; }

      if (res.ok && data !== null) return { ok: true, data, failure: null };
      if (res.ok) return { ok: true, data: {}, failure: null };

      // The server's own classification, when it gave one.
      if (data && data.failure) return { ok: false, data, failure: data.failure };

      // A body that isn't our envelope: an older endpoint, or ASP.NET's HTML error page. Both are
      // reported as an app-side fault rather than pasted at the seller as-is.
      return {
        ok: false,
        data,
        failure: {
          kind: 'Unknown',
          headline: 'The app returned an error',
          whatHappened: `The request came back as HTTP ${res.status}${res.statusText ? ` (${res.statusText})` : ''}.`,
          whatToDo: 'Try again. If it keeps happening, open Logs and send the detail below.',
          retryable: true,
          fixAction: 'open-logs',
          workPreserved: true,
          technical: looksLikeHtml(text) ? 'The app sent back an error page instead of data.' : (data?.error || text || '').slice(0, 600),
        },
      };
    } catch (err) {
      const aborted = err.name === 'AbortError';
      return {
        ok: false,
        data: null,
        failure: aborted
          ? {
              kind: 'Timeout',
              headline: 'That took too long and was stopped',
              whatHappened: `No answer came back within ${Math.round(timeoutMs / 60000)} minutes, so the request was cancelled.`,
              whatToDo: 'Try again. Everything you filled in is still here.',
              retryable: true,
              workPreserved: true,
              technical: `Client timeout after ${timeoutMs} ms.`,
            }
          : {
              kind: 'Network',
              headline: 'Could not reach the app',
              whatHappened: `The connection to ING AutoLister failed (${err.message}).`,
              whatToDo: 'Check the app is still running, then try again. Your work is kept.',
              retryable: true,
              workPreserved: true,
              technical: String(err.message || err),
            },
      };
    } finally {
      clearTimeout(timer);
    }
  }

  function looksLikeHtml(text) {
    const head = (text || '').trimStart().slice(0, 80).toLowerCase();
    return head.startsWith('<!doctype') || head.startsWith('<html');
  }

  // Which button resolves this failure, if one can.
  const FIX_ACTIONS = {
    'ai-key':        { label: 'Open Settings',   run: () => handleNav('settings') },
    'connect-ebay':  { label: 'Log into eBay',   run: () => startEbayLogin() },
    'ebay-policies': { label: 'Open Settings',   run: () => handleNav('settings') },
    'open-logs':     { label: 'Open Logs',       run: () => handleNav('logs') },
  };

  // The eBay sign-in button lives in more than one place depending on which screen is open, so the
  // fix action finds whichever one is present rather than assuming a single id.
  function startEbayLogin() {
    const btn = $('btn-connect');
    if (btn) { btn.click(); return; }
    handleNav('settings');
  }

  // One failure, rendered the same way everywhere: what happened, what to do about it, the button
  // that does it, and the raw text kept as evidence rather than as the headline.
  function renderFailure(container, failure, options = {}) {
    const el = typeof container === 'string' ? $(container) : container;
    if (!el || !failure) return;

    const fix = FIX_ACTIONS[failure.fixAction];
    const buttons = [];
    // Only offered when the server (or the transport) says another attempt could genuinely differ.
    // A Retry button on a rejected API key teaches sellers to click Retry on everything.
    if (failure.retryable && options.onRetry) buttons.push('<button type="button" class="btn btn-primary small" data-fp-retry>Try again</button>');
    if (fix) buttons.push(`<button type="button" class="btn btn-secondary small" data-fp-fix>${esc(fix.label)}</button>`);
    (options.extraButtons || []).forEach((b, i) =>
      buttons.push(`<button type="button" class="btn btn-secondary small" data-fp-extra="${i}">${esc(b.label)}</button>`));

    const attempts = failure.attempts > 1
      ? `<p class="failure-attempts">Tried ${failure.attempts} times before giving up.</p>` : '';

    const preserved = failure.workPreserved === false
      ? '' : '<p class="failure-preserved">Nothing you entered has been lost.</p>';

    el.innerHTML =
      `<div class="failure-head">${esc(failure.headline || 'Something went wrong')}</div>` +
      (failure.whatHappened ? `<p class="failure-what">${esc(failure.whatHappened)}</p>` : '') +
      (failure.whatToDo ? `<p class="failure-todo">${esc(failure.whatToDo)}</p>` : '') +
      preserved + attempts +
      (buttons.length ? `<div class="failure-actions">${buttons.join('')}</div>` : '') +
      (failure.technical
        ? `<details class="failure-detail"><summary>Technical detail</summary><pre>${esc(failure.technical)}</pre></details>`
        : '');

    el.classList.remove('hidden');
    el.querySelector('[data-fp-retry]')?.addEventListener('click', () => { hideFailure(el); options.onRetry(); });
    el.querySelector('[data-fp-fix]')?.addEventListener('click', () => fix.run());
    el.querySelectorAll('[data-fp-extra]').forEach(btn =>
      btn.addEventListener('click', () => options.extraButtons[Number(btn.dataset.fpExtra)].run()));

    // Bring it into view. Caught by measuring it in a real browser: the panel lives low in the
    // modal's scrollable body, so a seller who was scrolled anywhere else got a failure message
    // rendered 989px down a 950px viewport and behind the sticky footer — a perfect explanation of
    // what went wrong that they never saw, which is the same as no message at all.
    try {
      const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
      el.scrollIntoView({ block: 'center', behavior: reduced ? 'auto' : 'smooth' });
    } catch { el.scrollIntoView(); }
  }

  function hideFailure(container) {
    const el = typeof container === 'string' ? $(container) : container;
    if (!el) return;
    el.innerHTML = '';
    el.classList.add('hidden');
  }

  // ── Crash recovery: the listing in progress survives the tab ───────────────
  //
  // A Claude-written listing costs real API spend and a minute or two of waiting, and until now it
  // lived only in the DOM: one accidental Ctrl+W, one refresh, one crash, and it was gone with no
  // trace and no way back. Autosave puts it in the app's own database, so the work outlives the page.

  // Long enough that typing a description isn't 400 round trips; short enough that almost nothing is
  // lost if the tab dies mid-sentence.
  const AUTOSAVE_DEBOUNCE_MS = 2500;

  let workKey = null;
  let autosaveTimer = null;
  let lastAutosavePayload = '';

  function currentWorkKey() {
    if (!workKey) workKey = `wip-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
    return workKey;
  }

  function scheduleAutosave() {
    clearTimeout(autosaveTimer);
    autosaveTimer = setTimeout(() => { flushAutosave(false); }, AUTOSAVE_DEBOUNCE_MS);
  }

  // `useBeacon` is the page-unload path: fetch is cancelled when the document goes away, and this is
  // the one save that matters most — it is the save that makes the difference between recovering the
  // listing and losing it.
  function flushAutosave(useBeacon) {
    clearTimeout(autosaveTimer);
    let payload;
    try {
      payload = JSON.stringify(buildNlPayload());
    } catch { return; }

    // A no-op save is still a database write and a Prune pass. Skip when nothing changed.
    if (payload === lastAutosavePayload) return;

    const title = ($('nl-title')?.value || '').trim();
    // An empty form is not work worth recovering, and offering it back as "unfinished work" on every
    // launch would train sellers to dismiss the banner without reading it.
    if (!title && payload.length < 400) return;

    lastAutosavePayload = payload;
    const body = { key: currentWorkKey(), label: title || 'Untitled listing', payload, stage: 'editing' };

    if (useBeacon && navigator.sendBeacon) {
      try {
        navigator.sendBeacon('/api/work/autosave',
          new Blob([JSON.stringify(body)], { type: 'application/json' }));
        return;
      } catch { /* fall through to the normal call */ }
    }

    callApi('/api/work/autosave', { method: 'POST', body, timeoutMs: 15000 });
  }

  function bindAutosave() {
    const form = $('new-listing-overlay') || document;
    // Capture phase, and on both events: `input` covers typing, `change` covers selects and the
    // paste-and-blur case that never fires `input`.
    ['input', 'change'].forEach(evt =>
      form.addEventListener(evt, e => {
        if (!e.target?.id?.startsWith('nl-')) return;
        scheduleAutosave();
      }, true));

    // Both, deliberately: `pagehide` is the one that fires reliably on mobile and on tab discard,
    // `beforeunload` on a desktop close. A duplicate save is free; a missed one is the whole problem.
    window.addEventListener('beforeunload', () => flushAutosave(true));
    window.addEventListener('pagehide', () => flushAutosave(true));
  }

  async function loadRecoverableWork() {
    const banner = $('recovery-banner');
    if (!banner) return;

    const { ok, data } = await callApi('/api/work/recoverable', { timeoutMs: 15000 });
    const items = (ok && data?.items) || [];
    // Never announce recovery when there is nothing to recover.
    if (!items.length) { banner.classList.add('hidden'); banner.innerHTML = ''; return; }

    const rows = items.map((item, i) => {
      const when = item.updatedUtc ? new Date(item.updatedUtc).toLocaleString() : 'recently';
      const unknown = item.outcomeUnknown
        ? '<span class="recovery-flag">publish outcome unknown</span>' : '';
      const failed = item.lastError
        ? `<span class="recovery-error">${esc(item.lastError.slice(0, 160))}</span>` : '';
      return `<li class="recovery-row">
          <div class="recovery-meta">
            <span class="recovery-label">${esc(item.label || 'Untitled listing')}</span>
            <span class="recovery-when">last saved ${esc(when)}</span>
            ${unknown}${failed}
          </div>
          <div class="recovery-row-actions">
            <button type="button" class="btn btn-primary small" data-recover="${i}">Restore</button>
            ${item.outcomeUnknown ? `<button type="button" class="btn btn-secondary small" data-recover-check="${i}">Check eBay</button>` : ''}
            <button type="button" class="btn btn-ghost small" data-recover-discard="${i}">Discard</button>
          </div>
        </li>`;
    }).join('');

    banner.innerHTML =
      `<div class="recovery-head">${items.length === 1
          ? 'An unfinished listing was recovered'
          : `${items.length} unfinished listings were recovered`}</div>` +
      '<p class="recovery-sub">These were being written when the app last closed. Nothing was lost.</p>' +
      `<ul class="recovery-list">${rows}</ul>`;
    banner.classList.remove('hidden');

    banner.querySelectorAll('[data-recover]').forEach(btn =>
      btn.addEventListener('click', () => restoreWork(items[Number(btn.dataset.recover)])));
    banner.querySelectorAll('[data-recover-check]').forEach(btn =>
      btn.addEventListener('click', () => checkRecoveredPublish(items[Number(btn.dataset.recoverCheck)])));
    banner.querySelectorAll('[data-recover-discard]').forEach(btn =>
      btn.addEventListener('click', () => discardWork(items[Number(btn.dataset.recoverDiscard)])));
  }

  function restoreWork(item) {
    if (!item) return;
    let payload;
    try { payload = JSON.parse(item.payload || '{}'); }
    catch {
      addActivity('Could not restore that draft', 'The saved copy could not be read.');
      return;
    }

    // Adopt the recovered key so continuing to edit updates that same row rather than starting a
    // second one beside it — otherwise a restored draft would be offered back twice next launch.
    workKey = item.key;
    lastAutosavePayload = '';

    openNewListingModal();
    fillNlForm(payload);
    // Photos are URLs into the app's own generated-photos folder, so a recovered draft keeps them.
    if (Array.isArray(payload.imageUrls) && payload.imageUrls.length) {
      nlClearAllPhotoSlots();
      payload.imageUrls.filter(Boolean).forEach(url => nlAddPhotoRow(url));
    }
    addActivity('Draft restored', payload.title || item.label || 'Unfinished listing recovered');
    loadRecoverableWork();
  }

  async function checkRecoveredPublish(item) {
    if (!item) return;
    let payload = {};
    try { payload = JSON.parse(item.payload || '{}'); } catch { /* title fallback below */ }
    const title = payload.title || item.label || '';
    if (!title) return;

    addActivity('Checking eBay', `Looking for "${title}" among your live listings…`);
    const { ok, data, failure } = await callApi('/api/listing/check-published', {
      method: 'POST', body: { title, workKey: item.key }, timeoutMs: 90000,
    });

    if (!ok) {
      addActivity('Could not check eBay', failure?.whatHappened || 'The check did not complete.');
      return;
    }
    addActivity(data.found ? 'It is already live on eBay' : 'It never went live', data.message || '');
    if (data.found) loadListings('Listings refreshed after reconciling a publish');
    loadRecoverableWork();
  }

  async function discardWork(item) {
    if (!item) return;
    if (!confirm(`Discard "${item.label || 'this draft'}"? This cannot be undone.`)) return;
    await callApi('/api/work/discard', { method: 'POST', body: { key: item.key }, timeoutMs: 15000 });
    if (workKey === item.key) { workKey = null; lastAutosavePayload = ''; }
    loadRecoverableWork();
  }

  async function init() {
    initPhotoGrid();          // render 6 photo slots on page load, not just on modal open
    initPhotoEditorPaste();
    bindDashboard();
    bindSetup();
    bindNewListingModal();
    bindImageGenSetup();
    bindPgImggen();
    bindOpportunitySearch();
    bindSupplierAnalyzer();
    bindFacebookMarketplace();
    bindNegotiation();
    bindRollTheDice();
    bindPhotoLibrary();
    bindInventoryHealth();
    bindWatcherOffers();
    bindRescue();
    bindBudget();
    bindRelist();
    bindLotAnalyzer();
    bindPromoted();
    bindTrendRadar();
    bindWhereToSell();
    bindSniper();
    bindEarnings();
    bindPipeline();
    bindHomeButtons();
    bindForm();
    initEditDrawer();
    bindMarketResearch();
    bindCrossListing();
    bindTakeHome('nl');
    bindTakeHome('f');
    loadFeeProfile();
    bindAutosave();
    restoreListingViewMode();
    addActivity('ING Listing Engine™ ready', 'Official product of ING Mining LLC — all systems operational.');

    await checkSetupOnLoad();
    await checkLicenseStatus();
    await checkTrialStatus();

    try {
      const tokenStatus = await fetch('/api/ebay/token-status').then(r => r.json());
      updateAuthUI(!!tokenStatus.hasToken);
      if (tokenStatus.hasToken) {
        await loadListings('Connected account detected');
        loadPolicies(false); // background — populate dropdowns for next modal open
      } else await loadPlaceholderListings('Sample listings loaded');
    } catch {
      updateAuthUI(false);
      await loadPlaceholderListings('Sample listings loaded');
    }

    renderListings();
    updateStats();

    // Not awaited, and quiet on failure: the running total is the best thing on the dashboard when
    // there is one, and no reason to hold the page up when there isn't. The band stays hidden
    // unless real money comes back.
    loadEarnings(true);

    // Same posture: not awaited, quiet on failure. The board is only worth a front-page band
    // when real money is actually out, so a seller with no tracked deals never sees a $0 banner.
    loadPipeline(true);

    // Last, and not awaited: a listing recovered from a crash is the most valuable thing on this
    // page, but it must not delay the page loading — and if the check itself fails, the banner simply
    // stays hidden rather than holding up everything behind it.
    loadRecoverableWork();

    // Navigate to whatever section the URL hash specifies (supports reload + deep links)
    if (location.hash) handleNav(location.hash.slice(1));
  }

  function bindDashboard() {
    on('license-nag-dismiss',  'click', () => { $('license-nag')?.classList.add('hidden'); sessionStorage.setItem('licenseNagDismissed', '1'); });
    on('license-nag-settings', 'click', () => { $('license-nag')?.classList.add('hidden'); openSetupWithPolicies(null); });
    on('btn-import-listings', 'click', () => loadListings('Manual import requested'));
    on('btn-refresh-listings', 'click', () => loadListings('Listings refreshed'));
    on('btn-refresh-dashboard', 'click', () => loadListings('Dashboard refresh requested'));
    on('btn-refresh-logs', 'click', loadLogs);
    on('btn-new-ai-listing', 'click', showAiSection);
    on('btn-back-dashboard', 'click', showDashboard);
    on('global-search', 'input', renderListings);

    // The search box advertises Ctrl+K; this is what makes the label true.
    // Escape clears and re-renders, so a filtered grid is one key from whole.
    document.addEventListener('keydown', e => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        const box = $('global-search');
        box?.focus();
        box?.select();
        return;
      }
      if (e.key === 'Escape' && document.activeElement?.id === 'global-search') {
        const box = $('global-search');
        if (box && box.value) { box.value = ''; renderListings(); }
      }
    });

    on('btn-card-view', 'click', () => setViewMode('cards'));
    on('btn-table-view', 'click', () => setViewMode('table'));

    document.querySelectorAll('.nav-item').forEach(btn => {
      btn.addEventListener('click', () => {
        const page = btn.dataset.page || 'dashboard';
        // Setting the hash to what it already is fires no hashchange, so the click would do
        // nothing at all. Navigate directly in that case.
        if (location.hash.slice(1) === page) handleNav(page);
        else location.hash = page;
      });
    });

    window.addEventListener('hashchange', () => {
      handleNav(location.hash.slice(1) || 'dashboard');
    });
  }

  function handleNav(page) {
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === page));
    if (page !== 'ai') $('new-listing-overlay')?.classList.add('hidden');
    if (page !== 'opportunity') $('opportunity-section')?.classList.add('hidden');
    if (page !== 'photos') $('photo-library-section')?.classList.add('hidden');
    if (page !== 'inventory') $('inventory-section')?.classList.add('hidden');
    if (page !== 'offers') $('offers-section')?.classList.add('hidden');
    if (page !== 'rescue') $('rescue-section')?.classList.add('hidden');
    if (page !== 'budget') $('budget-section')?.classList.add('hidden');
    if (page !== 'relist') $('relist-section')?.classList.add('hidden');
    if (page !== 'lots') $('lots-section')?.classList.add('hidden');
    if (page !== 'promoted') $('promoted-section')?.classList.add('hidden');
    if (page !== 'trends') $('trends-section')?.classList.add('hidden');
    if (page !== 'wheretosell') $('wts-section')?.classList.add('hidden');
    if (page !== 'snipe') closeSnipeSection({ back: false });
    if (page !== 'earnings') $('earnings-section')?.classList.add('hidden');
    if (page !== 'pipeline') $('pipeline-section')?.classList.add('hidden');
    if (page === 'earnings') {
      showEarningsSection();
      return;
    }
    if (page === 'pipeline') {
      showPipelineSection();
      return;
    }
    if (page === 'ai') {
      showAiSection();
      return;
    }
    if (page === 'settings') {
      showSettingsSection();
      return;
    }
    if (page === 'logs') {
      showLogsSection();
      return;
    }
    if (page === 'license') {
      showLicenseSection();
      return;
    }
    if (page === 'opportunity') {
      showOpportunitySection();
      return;
    }
    if (page === 'photos') {
      showPhotoLibrarySection();
      return;
    }
    if (page === 'inventory') {
      showInventorySection();
      return;
    }
    if (page === 'offers') {
      showOffersSection();
      return;
    }
    if (page === 'rescue') {
      showRescueSection();
      return;
    }
    if (page === 'budget') {
      showBudgetSection();
      return;
    }
    if (page === 'relist') {
      showRelistSection();
      return;
    }
    if (page === 'lots') {
      showLotsSection();
      return;
    }
    if (page === 'promoted') {
      showPromotedSection();
      return;
    }
    if (page === 'trends') {
      showTrendsSection();
      return;
    }
    if (page === 'wheretosell') {
      showWhereToSellSection();
      return;
    }
    if (page === 'snipe') {
      showSnipeSection();
      return;
    }
    showDashboard();
    if (page === 'listings') $('listings-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    if (page === 'activity') $('activity-list')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }

  function showAiSection() {
    openNewListingModal();
  }

  const OVERLAY_SECTIONS = ['settings-section', 'logs-section', 'license-section', 'opportunity-section', 'photo-library-section', 'inventory-section', 'offers-section', 'rescue-section', 'budget-section', 'relist-section', 'lots-section', 'promoted-section', 'trends-section', 'wts-section', 'snipe-section', 'earnings-section', 'pipeline-section'];

  function hideOverlaySections() {
    OVERLAY_SECTIONS.forEach(id => $(id)?.classList.add('hidden'));
  }

  function showDashboard() {
    hideOverlaySections();
    // Closing an overlay has to clear the hash too, or the URL keeps claiming we're on a page
    // that is no longer open — which breaks the next click on that sidebar entry and makes a
    // reload land on a section the user already closed. replaceState fires no hashchange, so
    // this can't loop back into handleNav.
    if (location.hash && location.hash !== '#dashboard')
      history.replaceState(null, '', location.pathname + location.search);
    $('dashboard-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'dashboard'));
  }

  async function showSettingsSection() {
    hideOverlaySections();
    $('settings-section')?.classList.remove('hidden');
    $('settings-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'settings'));
    await loadSettingsStatus();
    await loadTerapeakStatus();
    await loadFacebookStatus();
  }

  const TERAPEAK_BANNERS = [
    ['pg-terapeak-status', 'pg-terapeak-connect', 'pg-terapeak-disconnect'],
    ['opp-terapeak-status', 'opp-terapeak-connect', 'opp-terapeak-disconnect'],
  ];

  // There is no auto-connect and no background scraping — unattended, continuous automated
  // access to Terapeak/Seller Hub is against eBay's User Agreement. Connecting is always a
  // person clicking the button below and logging into eBay themselves in the browser window
  // that opens (which is also the only way to clear a captcha/security challenge if eBay shows
  // one). Both Settings and the Opportunity Finder banner carry the same connect/disconnect
  // controls so a session can be (re)established from wherever the user notices it's needed.
  function paintTerapeakBanner(statusEl, connectBtn, disconnectBtn, data) {
    if (!statusEl) return;
    if (data.loginInProgress) {
      // The window is genuinely open whenever this shows; the common failure is that it opened
      // behind everything, so say where to look instead of leaving "connecting…" to look stuck.
      statusEl.textContent = 'Connecting to Terapeak — log into eBay in the browser window that opened. Don\'t see it? Alt+Tab, or check the taskbar for the login window.';
      connectBtn?.classList.remove('hidden');
      if (connectBtn) connectBtn.disabled = true;
      disconnectBtn?.classList.add('hidden');
    } else if (data.connected) {
      statusEl.textContent = '✓ Connected — sold-comp lookups will use real Terapeak data.';
      connectBtn?.classList.add('hidden');
      disconnectBtn?.classList.remove('hidden');
    } else {
      statusEl.textContent = data.lastError
        ? `Terapeak connect failed: ${data.lastError}`
        : 'Not connected — sold comps will show links only. Click Connect to log in.';
      connectBtn?.classList.remove('hidden');
      if (connectBtn) connectBtn.disabled = false;
      disconnectBtn?.classList.add('hidden');
    }
  }

  async function loadTerapeakStatus() {
    try {
      const data = await fetch('/api/terapeak/status').then(r => r.json());
      TERAPEAK_BANNERS.forEach(([statusId, connectId, disconnectId]) =>
        paintTerapeakBanner($(statusId), $(connectId), $(disconnectId), data));
    } catch (err) {
      TERAPEAK_BANNERS.forEach(([statusId]) => {
        const el = $(statusId);
        if (el) el.textContent = `Unable to check Terapeak status: ${err.message}`;
      });
    }
  }

  async function terapeakConnect(e) {
    const btn = e?.currentTarget || $('pg-terapeak-connect');
    try {
      btn.disabled = true;
      const data = await fetch('/api/terapeak/connect', { method: 'POST' }).then(r => r.json());
      TERAPEAK_BANNERS.forEach(([statusId]) => {
        const el = $(statusId);
        if (el) el.textContent = data.message || 'Opening browser…';
      });
      // Poll status every few seconds until the login window closes (saved or cancelled)
      const poll = setInterval(async () => {
        const s = await fetch('/api/terapeak/status').then(r => r.json()).catch(() => null);
        if (s && !s.loginInProgress) {
          clearInterval(poll);
          await loadTerapeakStatus();
        }
      }, 3000);
    } catch (err) {
      TERAPEAK_BANNERS.forEach(([statusId]) => {
        const el = $(statusId);
        if (el) el.textContent = `Connect failed: ${err.message}`;
      });
      btn.disabled = false;
    }
  }

  async function terapeakDisconnect() {
    await fetch('/api/terapeak/disconnect', { method: 'POST' }).catch(() => {});
    await loadTerapeakStatus();
  }

  // ── Facebook Marketplace (local sourcing) ────────────────────────────────
  // Same connect/status/disconnect shape as Terapeak above, for the same reason: no public
  // API, so the seller logs into their own account once in a real browser window and the
  // saved session is reused. Searching is always a click — nothing polls Facebook.
  const FACEBOOK_BANNERS = [
    ['pg-facebook-status', 'pg-facebook-connect', 'pg-facebook-disconnect'],
    ['fb-connect-status', 'fb-connect-btn', 'fb-disconnect-btn'],
  ];

  function paintFacebookBanner(statusEl, connectBtn, disconnectBtn, data) {
    if (!statusEl) return;
    if (data.loginInProgress) {
      statusEl.textContent = 'Connecting — log into Facebook in the browser window that opened. Don\'t see it? Alt+Tab, or check the taskbar for the login window.';
      connectBtn?.classList.remove('hidden');
      if (connectBtn) connectBtn.disabled = true;
      disconnectBtn?.classList.add('hidden');
    } else if (data.connected) {
      statusEl.textContent = '✓ Connected — local Marketplace search is ready.';
      connectBtn?.classList.add('hidden');
      disconnectBtn?.classList.remove('hidden');
    } else {
      statusEl.textContent = data.lastError
        ? `Facebook connect failed: ${data.lastError}`
        : 'Not connected — click Connect to log into your own Facebook account once.';
      connectBtn?.classList.remove('hidden');
      if (connectBtn) connectBtn.disabled = false;
      disconnectBtn?.classList.add('hidden');
    }
    // The search buttons are NOT tied to Facebook any more: Craigslist needs no login, so a
    // disconnected Facebook only removes one source. What gates the buttons is whether any
    // source is ticked at all — see refreshLocalSearchButtons.
    refreshLocalSearchButtons();
  }

  async function loadFacebookStatus() {
    try {
      const data = await fetch('/api/facebook/status').then(r => r.json());
      FACEBOOK_BANNERS.forEach(([statusId, connectId, disconnectId]) =>
        paintFacebookBanner($(statusId), $(connectId), $(disconnectId), data));
      // A Facebook connect/disconnect changes what the source list says about itself.
      loadLocalSources();
      return data;
    } catch (err) {
      FACEBOOK_BANNERS.forEach(([statusId]) => {
        const el = $(statusId);
        if (el) el.textContent = `Unable to check Facebook status: ${err.message}`;
      });
      return null;
    }
  }

  async function facebookConnect(e) {
    const btn = e?.currentTarget || $('pg-facebook-connect');
    try {
      if (btn) btn.disabled = true;
      const data = await fetch('/api/facebook/connect', { method: 'POST' }).then(r => r.json());
      FACEBOOK_BANNERS.forEach(([statusId]) => {
        const el = $(statusId);
        if (el) el.textContent = data.message || 'Opening browser…';
      });
      const poll = setInterval(async () => {
        const s = await fetch('/api/facebook/status').then(r => r.json()).catch(() => null);
        if (s && !s.loginInProgress) {
          clearInterval(poll);
          await loadFacebookStatus();
        }
      }, 3000);
    } catch (err) {
      FACEBOOK_BANNERS.forEach(([statusId]) => {
        const el = $(statusId);
        if (el) el.textContent = `Connect failed: ${err.message}`;
      });
      if (btn) btn.disabled = false;
    }
  }

  async function facebookDisconnect() {
    await fetch('/api/facebook/disconnect', { method: 'POST' }).catch(() => {});
    await loadFacebookStatus();
  }

  // ── Pluggable local supply sources ───────────────────────────────────────
  // The panel below searches whichever sites are ticked here, and the list is rendered from
  // /api/local/sources rather than hard-coded — a source added on the server shows up here on
  // its own. Craigslist is public (no login); Facebook needs its saved session.
  let localSources = [];

  function selectedSourceIds() {
    return [...document.querySelectorAll('#local-source-picker input[type=checkbox]')]
      .filter(cb => cb.checked).map(cb => cb.value);
  }

  // Gated on "is any site selected", not on any one site's connection: Craigslist alone is a
  // complete local search, so a disconnected Facebook must not disable the buttons.
  function refreshLocalSearchButtons() {
    const picked = selectedSourceIds();
    ['fb-search-btn', 'fb-arb-btn'].forEach(id => {
      const btn = $(id);
      if (btn) btn.disabled = picked.length === 0;
    });
    // The metro override only means anything to Craigslist.
    $('cl-site-row')?.classList.toggle('hidden', !picked.includes('craigslist'));
    if (picked.includes('craigslist')) loadCraigslistSites();
  }

  async function loadLocalSources() {
    const picker = $('local-source-picker');
    if (!picker) return;

    // The picker is the panel's front door — with nothing to tick there is nothing to search, so
    // a failure here offers the retry rather than leaving a sentence and a dead panel.
    const { data, error } = await localFetchJson('/api/local/sources', 20000);
    // An empty list means the server couldn't describe its sources — same dead panel as a failed
    // request, so it gets the same treatment rather than a picker with nothing in it.
    if (!Array.isArray(data) || !data.length) {
      picker.innerHTML = `${esc(`Couldn't load the list of places to search. ${error || ''}`.trim())} ` +
        '<button type="button" class="btn btn-secondary small local-sources-retry">Try again</button>';
      picker.querySelector('.local-sources-retry')?.addEventListener('click', loadLocalSources);
      return;
    }
    localSources = data;

    // A remembered choice wins; on a first run the default is everything that can answer right
    // now, which on a fresh install is Craigslist alone.
    const saved = (localStorage.getItem('localSources') || '').split(',').filter(Boolean);
    picker.innerHTML = localSources.map(s => {
      const checked = saved.length ? saved.includes(s.id) : s.available;
      const badge = !s.requiresConnection ? 'no login'
        : s.available ? 'connected' : 'needs login';
      return `<label class="local-source${s.available ? '' : ' local-source-off'}" title="${esc(s.note || '')}">
                <input type="checkbox" value="${esc(s.id)}"${checked ? ' checked' : ''} />
                <span class="local-source-name">${esc(s.label)}</span>
                <span class="local-source-badge">${badge}</span>
              </label>`;
    }).join('');

    picker.querySelectorAll('input[type=checkbox]').forEach(cb => cb.addEventListener('change', () => {
      localStorage.setItem('localSources', selectedSourceIds().join(','));
      refreshLocalSearchButtons();
    }));
    refreshLocalSearchButtons();
  }

  // Craigslist's metro list is ~230 entries and only matters if the auto-picked board is wrong,
  // so it's fetched once, on demand, rather than on every page load.
  async function loadCraigslistSites() {
    const sel = $('cl-site-select');
    if (!sel || sel.dataset.loaded) return;
    sel.dataset.loaded = '1';
    try {
      const sites = await fetch('/api/craigslist/sites').then(r => r.json());
      sel.insertAdjacentHTML('beforeend', sites
        .map(s => `<option value="${esc(s.id)}">${esc(s.label)}, ${esc(s.state)}</option>`).join(''));
      const saved = localStorage.getItem('clSite');
      if (saved) sel.value = saved;
    } catch {
      // Auto-resolution from the zip still works; only the manual override is unavailable.
      sel.dataset.loaded = '';
    }
  }

  function bindFacebookMarketplace() {
    on('pg-facebook-connect', 'click', facebookConnect);
    on('pg-facebook-disconnect', 'click', facebookDisconnect);
    on('fb-connect-btn', 'click', facebookConnect);
    on('fb-disconnect-btn', 'click', facebookDisconnect);
    on('fb-search-btn', 'click', runLocalSearch);
    on('fb-arb-btn', 'click', runLocalArbitrage);
    on('cl-site-select', 'change', e => localStorage.setItem('clSite', e.currentTarget.value));
    // Enter runs the ranked scan, not the plain list — the ranking is what the panel is for,
    // and the plain list is one click away.
    on('fb-query-input', 'keydown', e => { if (e.key === 'Enter') runLocalArbitrage(); });
    on('fb-zip-input', 'keydown', e => { if (e.key === 'Enter') runLocalArbitrage(); });
    on('fb-arb-sort', 'change', renderArbitrageRows);
    on('fb-arb-hide-losers', 'change', renderArbitrageRows);
    on('fb-arb-fast-only', 'change', renderArbitrageRows);
    // The seller's zip and radius don't change between searches — remembering them is the
    // difference between a two-field search and a one-field one.
    const zip = localStorage.getItem('fbZip');
    const radius = localStorage.getItem('fbRadius');
    if (zip && $('fb-zip-input')) $('fb-zip-input').value = zip;
    if (radius && $('fb-radius-select')) $('fb-radius-select').value = radius;
  }

  // The form values every local search shares, plus the selected sources. Craigslist's metro
  // override rides along and is ignored by every other source.
  function localSearchParams() {
    const query = $('fb-query-input')?.value.trim() || '';
    const zip = $('fb-zip-input')?.value.trim() || '';
    const radius = $('fb-radius-select')?.value || '40';
    const sources = selectedSourceIds();
    const site = $('cl-site-select')?.value || '';

    localStorage.setItem('fbZip', zip);
    localStorage.setItem('fbRadius', radius);

    const qs = `q=${encodeURIComponent(query)}&zip=${encodeURIComponent(zip)}&radius=${encodeURIComponent(radius)}` +
      `&sources=${encodeURIComponent(sources.join(','))}` +
      (site ? `&craigslistSite=${encodeURIComponent(site)}` : '');

    return { query, zip, radius, sources, qs };
  }

  function sourceLabelsFor(ids) {
    return ids.map(id => localSources.find(s => s.id === id)?.label || id).join(' + ');
  }

  // ── Never dead-ending the panel ──────────────────────────────────────────
  // The server guarantees a 200 with a valid body for every local search, however badly a site
  // behaves (see LocalSupplyGuard). This is the other half of that promise: the cases the server
  // can't reach — the app not running, the connection dropping mid-scan, a proxy returning HTML,
  // a response that never arrives — all have to end as a sentence on screen with a way forward.
  // `fetch(...).then(r => r.json())` ends every one of them as an unhandled rejection instead,
  // which is what left this panel showing a spinner and "Failed to fetch".
  //
  // Client-side ceilings, generous enough not to cut a real scan short: the point is that a
  // request which will never come back stops looking like one that is still working.
  const LOCAL_SEARCH_TIMEOUT_MS = 4 * 60 * 1000;      // Craigslist is seconds; Facebook loads a real page
  const LOCAL_ARBITRAGE_TIMEOUT_MS = 8 * 60 * 1000;   // + a sold-comp lookup per distinct product

  // The last search the seller ran, so a Try again button can repeat it without them retyping.
  let lastLocalRun = null;

  // Always resolves to { data, error } — never throws, never rejects.
  async function localFetchJson(url, timeoutMs) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const res = await fetch(url, { signal: controller.signal });
      const text = await res.text();

      let data = null;
      try { data = text ? JSON.parse(text) : null; } catch { data = null; }
      // A readable body wins even on a non-OK status: it carries the per-source detail, and
      // showing that beats showing the status code it arrived with.
      if (data && typeof data === 'object') return { data, error: null };

      if (!res.ok) {
        return { data: null, error: `The app answered HTTP ${res.status}${res.statusText ? ` (${res.statusText})` : ''}.` };
      }
      return { data: null, error: 'The app sent back a response this page couldn\'t read.' };
    } catch (err) {
      if (err.name === 'AbortError') {
        return {
          data: null,
          error: `No answer after ${Math.round(timeoutMs / 60000)} minutes, so the search was stopped. ` +
                 'Try again, or untick Facebook — Craigslist alone answers in seconds.',
        };
      }
      return { data: null, error: `Couldn't reach the app (${err.message}). Check it's still running, then try again.` };
    } finally {
      clearTimeout(timer);
    }
  }

  // The panel's one status line. Takes plain text — everything shown here can contain a message
  // from a site or an exception, so it is escaped rather than trusted — plus an optional Try again
  // button wired to whichever search was last run.
  function setLocalStatus(message, retry) {
    const el = $('fb-status');
    if (!el) return;
    if (!message) { el.innerHTML = ''; return; }

    el.innerHTML = esc(message) +
      (retry && lastLocalRun ? ' <button type="button" class="btn btn-secondary small local-retry-btn">Try again</button>' : '');
    el.querySelector('.local-retry-btn')?.addEventListener('click', () => lastLocalRun && lastLocalRun());
  }

  async function runLocalSearch() {
    const { query, zip, radius, sources, qs } = localSearchParams();
    const btn = $('fb-search-btn');

    lastLocalRun = runLocalSearch;

    if (!query) return setLocalStatus('Enter what you want to look for locally.');
    if (!sources.length) return setLocalStatus('Tick at least one place to search.');

    $('fb-results')?.classList.add('hidden');
    if (btn) btn.disabled = true;
    // Honest about the cost, which differs per source: Craigslist is one HTTPS request, Facebook
    // is a real browser loading a page and scrolling a grid.
    setLocalStatus(`Searching ${sourceLabelsFor(sources)} within ${radius} miles${zip ? ` of ${zip}` : ''}` +
      `${sources.includes('facebook') ? ' — Facebook opens a real page, so give it up to a minute…' : '…'}`);

    const { data, error } = await localFetchJson(`/api/local/search?${qs}`, LOCAL_SEARCH_TIMEOUT_MS);
    if (btn) btn.disabled = false;

    if (!data) {
      setLocalStatus(`Local search didn't complete. ${error}`, true);
      return;
    }
    renderLocalResults(data);
  }

  // Everything that isn't a usable result set, handled the same way for both the plain list and
  // the arbitrage ranking. With several sources these statuses only appear when NONE of them
  // answered — one disconnected site never blanks results another site returned.
  // Returns true when it handled (and therefore ended) the render.
  function handleLocalNonResult(data) {
    if (data.status === 'no_sources') {
      setLocalStatus('Tick at least one place to search.');
      return true;
    }
    if (data.status === 'not_connected') {
      setLocalStatus(data.error || 'Connect your Facebook account above, or tick Craigslist — it needs no login.');
      loadFacebookStatus();
      return true;
    }
    if (data.status === 'session_expired') {
      setLocalStatus(data.error || 'Your saved Facebook session expired — click Connect Facebook to log in again.');
      loadFacebookStatus();
      return true;
    }
    if (data.status !== 'ok') {
      // Retryable is a per-source judgement, so the whole-search button is offered whenever any
      // site said "come back shortly" — a blocked Craigslist is the common case here.
      const retryable = (data.sources || []).some(s => s.retryable);
      setLocalStatus(data.error || 'The local search didn\'t come back with anything usable.', retryable);
      return true;
    }
    return false;
  }

  // One site answering while another doesn't is the normal case, not a failure — but a bare count
  // silently presents one site's results as the whole local market. This says which is which.
  function partialLocalNote(sources) {
    const answered = (sources || []).filter(s => s.status === 'ok');
    const missing = (sources || []).filter(s => s.status !== 'ok');
    if (!answered.length || !missing.length) return '';

    const why = missing.map(s =>
      s.status === 'not_connected' ? `${s.label} needs connecting`
      : s.status === 'session_expired' ? `${s.label}'s session expired`
      : s.retryable ? `${s.label} is blocked right now`
      : `${s.label} couldn't be searched`).join(', ');

    return `Showing ${answered.map(s => s.label).join(' + ')} only — ${why}.`;
  }

  // What each site contributed, including the ones that couldn't answer and why — with several
  // sources in one table, "24 results" alone hides that a site failed silently. Each chip that
  // describes a fixable state carries the button that fixes it, so the seller never has to work
  // out from a sentence which of three things to click.
  function renderSourceOutcomes(sources) {
    const el = $('local-source-status');
    if (!el) return;
    if (!sources || !sources.length) { el.innerHTML = ''; return; }

    el.innerHTML = sources.map(s => {
      const ok = s.status === 'ok';
      const detail = ok && s.count
        ? `${s.count} result${s.count === 1 ? '' : 's'}${s.scopeLabel ? ` · ${esc(s.scopeLabel)}` : ''}`
        : ok ? 'no results'
        : s.status === 'not_connected' ? 'connect required'
        : s.status === 'session_expired' ? 'session expired'
        : s.retryable ? 'blocked — retry'
        : 'unavailable';
      // Connect is offered only for the source whose connect flow this page actually has. A future
      // session-based source gets its own button here, not Facebook's.
      const action = (s.status === 'not_connected' || s.status === 'session_expired') && s.id === 'facebook'
        ? '<button type="button" class="btn btn-secondary small local-chip-btn local-chip-connect">Connect</button>'
        : (!ok && s.retryable)
        ? '<button type="button" class="btn btn-secondary small local-chip-btn local-chip-retry">Retry</button>'
        : '';
      const link = s.searchUrl
        ? ` <a class="link-ext" href="${esc(s.searchUrl)}" target="_blank" rel="noopener">open ↗</a>` : '';
      return `<span class="local-source-chip${ok ? '' : ' local-source-chip-off'}">
                <strong>${esc(s.label)}</strong> ${detail}${link} ${action}
                ${s.error ? `<span class="local-source-chip-err">${esc(s.error)}</span>` : ''}
              </span>`;
    }).join('');

    el.querySelectorAll('.local-chip-connect').forEach(b => b.addEventListener('click', facebookConnect));
    el.querySelectorAll('.local-chip-retry').forEach(b =>
      b.addEventListener('click', () => lastLocalRun && lastLocalRun()));
  }

  function renderLocalResults(data) {
    const results = $('fb-results');
    const list = $('fb-list');
    const summary = $('fb-summary');
    if (!list || !results) return;

    renderSourceOutcomes(data.sources);
    if (handleLocalNonResult(data)) return;
    if (!data.count) {
      setLocalStatus(data.error
        ? `No local listings found — ${data.error}`
        : `No local listings found for "${data.query}" within ${data.radiusMiles} miles.`,
        (data.sources || []).some(s => s.retryable));
      return;
    }

    // Blank unless part of the search is missing, in which case the count above it needs the
    // caveat more than the panel needs a clean status line.
    setLocalStatus(partialLocalNote(data.sources));
    // Radius is echoed from the response, not the form: Facebook snaps it to one of its own
    // dropdown values, so this reports what was actually searched. Per-site links live in the
    // source chips above, since each site has its own results URL.
    summary.innerHTML =
      `<strong>${data.count}</strong> local listing${data.count === 1 ? '' : 's'} for "${esc(data.query)}" ` +
      `within ${data.radiusMiles} miles${data.zipCode ? ` of ${esc(data.zipCode)}` : ''} · ` +
      `asking ${money(data.min)}–${money(data.max)} · median ${money(data.median)}`;

    list.innerHTML = data.items.map(item => {
      const drop = item.originalPrice
        ? `<span class="fb-drop">was ${money(item.originalPrice)}</span>` : '';
      const meta = [
        item.location ? esc(item.location) : '',
        item.distanceMiles != null ? `${item.distanceMiles} mi away` : '',
        item.postedAgo ? esc(item.postedAgo) : '',
      ].filter(Boolean).join(' · ');
      return `
        <div class="fb-card" data-title="${esc(item.title)}" data-price="${item.price ?? 0}">
          ${item.imageUrl ? `<img class="fb-card-img" src="${esc(item.imageUrl)}" alt="" loading="lazy" referrerpolicy="no-referrer" />` : '<div class="fb-card-img fb-card-img-empty">📦</div>'}
          <div class="fb-card-body">
            <div class="fb-card-price">${item.isFree ? 'Free' : money(item.price)} ${drop}
              <span class="local-badge local-badge-${esc(item.source)}">${esc(item.sourceLabel || item.source)}</span>
            </div>
            <div class="fb-card-title">${esc(item.title)}</div>
            <div class="fb-card-meta">${meta}</div>
            <div class="fb-card-actions">
              <a class="btn btn-ghost small" href="${esc(item.url)}" target="_blank" rel="noopener">View listing ↗</a>
              <button class="btn btn-secondary small fb-comp-btn" type="button">Check eBay sold price</button>
            </div>
            <div class="fb-card-comp"></div>
          </div>
        </div>`;
    }).join('');

    // Sold-comp lookups are per-card and on demand — one click, one lookup. Checking every
    // result automatically would fire a scrape per tile off a single search.
    list.querySelectorAll('.fb-comp-btn').forEach(btn =>
      btn.addEventListener('click', () => facebookCheckComp(btn)));

    results.classList.remove('hidden');
  }

  // Prices the local ask against real eBay sold data using the existing /api/sold-comps
  // pipeline (Terapeak session first, then Marketplace Insights) — the whole point of a
  // local sourcing search is the spread between the two.
  async function facebookCheckComp(btn) {
    const card = btn.closest('.fb-card');
    const out = card?.querySelector('.fb-card-comp');
    const title = card?.dataset.title || '';
    if (!out || !title) return;

    btn.disabled = true;
    out.textContent = 'Checking eBay sold prices…';
    try {
      const data = await fetch(`/api/sold-comps?q=${encodeURIComponent(title)}`).then(r => r.json());
      const avg = data.average || data.median || 0;
      if (!avg) {
        out.innerHTML = `<span class="fb-comp-none">No sold comps found for this title. <a class="link-ext" href="${esc(data.fallbackUrl || '#')}" target="_blank" rel="noopener">Search eBay ↗</a></span>`;
        return;
      }
      // The numeric price off the card, not the rendered text — a price-drop card shows two
      // prices ("$450 was $700") and scraping the digits back out of that reads as $450,700.
      const localAsk = parseFloat(card.dataset.price) || 0;
      const spread = avg - localAsk;
      // Gross spread only — this is sold price minus local ask, before eBay fees and
      // shipping. The listing editor's profit panel is where the net number lives.
      const verdict = localAsk > 0
        ? `<span class="${spread > 0 ? 'fb-comp-good' : 'fb-comp-bad'}">${spread > 0 ? '+' : ''}${money(spread)} vs local ask (before fees &amp; shipping)</span>`
        : '';
      out.innerHTML = `<span class="fb-comp-val">eBay sold avg ${money(avg)}${data.count ? ` from ${data.count} comps` : ''}</span> ${verdict}`;
    } catch (err) {
      out.textContent = `Couldn't check sold prices: ${err.message}`;
    } finally {
      btn.disabled = false;
    }
  }

  // ── Local arbitrage ranking ──────────────────────────────────────────────
  // The whole local-sourcing feature in one table: every local listing priced against real
  // eBay sold comps, ranked by what's left after fees. Held here so the sort/filter controls
  // re-render from the same response instead of re-running a multi-minute scan.
  let arbitrageData = null;

  const ARB_VERDICTS = {
    goldmine: { label: '💎 Goldmine', cls: 'goldmine' },
    solid:    { label: '✅ Worth it', cls: 'solid' },
    thin:     { label: '⚠️ Thin', cls: 'thin' },
    pass:     { label: '✕ Pass', cls: 'pass' },
    no_data:  { label: '? No data', cls: 'nodata' },
  };

  // How long the money stays spent, in one cell. Server-side tiers (DaysToCashEstimator) so the
  // colour on the row and the sentence in the tooltip can't disagree.
  const SPEED_TIERS = {
    fast:       { cls: 'fast',  short: 'fast' },
    steady:     { cls: 'steady', short: 'steady' },
    slow:       { cls: 'slow',  short: 'slow' },
    dead_money: { cls: 'dead',  short: 'parked' },
    unknown:    { cls: 'unknown', short: '' },
  };

  // The buy side, on the row. Server-side verdicts (NegotiationAdvisor) so the badge, the number
  // and the drafted message can never tell three different stories.
  const NEG_VERDICTS = {
    buy_now:        { label: 'Take it', cls: 'buynow', hint: 'Already under your great-buy price — ask once, take it either way.' },
    negotiate:      { label: 'Haggle', cls: 'haggle', hint: 'Profitable at their ask. Everything you talk off is free money.' },
    must_negotiate: { label: 'Only at your price', cls: 'must', hint: 'Not worth their ask — this one only works if they come down.' },
    long_shot:      { label: 'Long shot', cls: 'longshot', hint: 'A polite offer barely clears the fees. One message, then let it go.' },
    walk:           { label: 'Walk', cls: 'walk', hint: 'No offer here both makes money and gets answered.' },
    no_data:        { label: '—', cls: 'nodata', hint: 'No sold history to negotiate against.' },
  };

  const NEG_TONES = {
    great: { cls: 'great', label: 'great buy' },
    good:  { cls: 'good',  label: 'worth doing' },
    thin:  { cls: 'thin',  label: 'thin' },
    loss:  { cls: 'loss',  label: 'you lose money' },
  };

  // "$1.85/day" — the profit spread over the wait. Sub-dollar rates keep their cents, because
  // that is exactly the range where two flips are being compared.
  function perDay(value) {
    if (value == null) return '';
    const abs = Math.abs(value);
    return `${value < 0 ? '-' : ''}$${abs < 10 ? abs.toFixed(2) : Math.round(abs).toLocaleString()}/day`;
  }

  // The days-to-cash cell shared by both boards: the wait, then what it earns per day of waiting.
  function daysToCashCell(row) {
    const tier = SPEED_TIERS[row.speedTier] || SPEED_TIERS.unknown;
    if (row.daysToCash == null) {
      return '<span class="fb-arb-muted" title="No dated sold history for this product, so there is no honest estimate of how long the money stays tied up.">—</span>';
    }
    const rate = row.profitPerDay != null && row.profitPerDay > 0
      ? `<span class="speed-rate">${perDay(row.profitPerDay)}</span>` : '';
    return `<span class="speed-days speed-${tier.cls}" title="${esc(row.speedNote || '')}">${row.daysToCash}d</span>${rate}`;
  }

  async function runLocalArbitrage() {
    const { query, zip, radius, sources, qs } = localSearchParams();
    const buttons = ['fb-search-btn', 'fb-arb-btn'].map($).filter(Boolean);

    lastLocalRun = runLocalArbitrage;

    if (!query) return setLocalStatus('Enter what you want to look for locally.');
    if (!sources.length) return setLocalStatus('Tick at least one place to search.');

    $('fb-results')?.classList.add('hidden');
    $('fb-arb-results')?.classList.add('hidden');
    buttons.forEach(b => { b.disabled = true; });
    // Honest about the cost: every selected site is searched, then one sold-comp lookup per
    // distinct product, then up to five Terapeak lookups. Minutes, not seconds.
    setLocalStatus(`Searching ${sourceLabelsFor(sources)} within ${radius} miles${zip ? ` of ${zip}` : ''}, ` +
      'then pricing every result against eBay sold data — this can take a couple of minutes…');

    // The scan comes back already ordered the way the seller last chose to look at it. Changing
    // the sort afterwards is still purely client-side — it must never re-run a multi-minute scan.
    const sort = $('fb-arb-sort')?.value || 'profit';
    const { data, error } = await localFetchJson(
      `/api/local/arbitrage?${qs}&sort=${encodeURIComponent(sort)}`, LOCAL_ARBITRAGE_TIMEOUT_MS);
    buttons.forEach(b => { b.disabled = false; });

    if (!data) {
      setLocalStatus(`The local scan didn't complete. ${error}`, true);
      return;
    }
    renderArbitrage(data);
  }

  function renderArbitrage(data) {
    const wrap = $('fb-arb-results');
    if (!wrap) return;

    renderSourceOutcomes(data.sources);
    if (handleLocalNonResult(data)) return;
    if (!data.localListingsFound) {
      setLocalStatus(data.error
        ? `No local listings found — ${data.error}`
        : `No local listings found for "${data.query}" within ${data.radiusMiles} miles.`,
        (data.sources || []).some(s => s.retryable));
      return;
    }
    if (!data.count) {
      // The sites answered and the listings are real — what's missing is the pricing half, and
      // dataWarning is where the server says why (no comps source, or a pricing pass that broke).
      setLocalStatus(`Found ${data.localListingsFound} local listing(s), but none could be priced. ` +
        (data.dataWarning || data.error || 'None of them had a price to work from.'), true);
      return;
    }

    arbitrageData = data;
    setLocalStatus(partialLocalNote(data.sources));

    // Which sites are actually in this ranking — a mixed table has to say so, and a site that
    // returned nothing shouldn't be implied to have been searched fruitfully.
    const searched = (data.sources || []).filter(s => s.status === 'ok' && s.count)
      .map(s => `${esc(s.label)} (${s.count})`).join(' + ');

    const scanned = [
      `<strong>${data.count}</strong> local listing${data.count === 1 ? '' : 's'} priced for "${esc(data.query)}"`,
      `within ${data.radiusMiles} miles${data.zipCode ? ` of ${esc(data.zipCode)}` : ''}`,
      searched ? `from ${searched}` : '',
      data.goldmineCount ? `<strong class="fb-arb-hit">${data.goldmineCount} goldmine${data.goldmineCount === 1 ? '' : 's'}</strong>` : 'no goldmines this time',
      // Capital that comes back inside three weeks is capital that can buy the next one — worth its
      // own headline next to the total, which says nothing about when any of it arrives.
      data.fastCashCount ? `<strong class="fb-arb-hit">${data.fastCashCount} that ${data.fastCashCount === 1 ? 'pays' : 'pay'} back inside 3 weeks</strong>` : '',
      `${money(data.totalPotentialProfit)} total profit if you bought every profitable one`,
      // The buy side, said plainly: this is the money that costs nothing to earn. Framed as a
      // ceiling, because nobody accepts every opening offer.
      data.negotiationUpside > 0
        ? `<strong class="fb-arb-hit">${money(data.negotiationUpside)} more</strong> if all ${data.negotiableCount} sellers took your opening offer`
        : '',
    ].filter(Boolean).join(' · ');
    $('fb-arb-summary').innerHTML =
      scanned +
      `<div class="fb-arb-sources">Priced ${data.productsPriced} distinct product${data.productsPriced === 1 ? '' : 's'} against sold comps` +
      `${data.terapeakScrapesUsed ? `, ${data.terapeakScrapesUsed} of them re-checked live on Terapeak` : ''}` +
      `${data.terapeakConnected ? '' : ' · Terapeak not connected — sold-comps database only'}.</div>`;

    const warn = $('fb-arb-warning');
    if (warn) {
      warn.textContent = data.dataWarning || '';
      warn.classList.toggle('hidden', !data.dataWarning);
    }

    renderArbitrageRows();
    wrap.classList.remove('hidden');
  }

  // Re-sorting and filtering are pure client-side views over the response already in hand —
  // changing the sort must never re-run the scan.
  function renderArbitrageRows() {
    const body = $('fb-arb-body');
    if (!body || !arbitrageData) return;

    const sort = $('fb-arb-sort')?.value || 'profit';
    const hideLosers = !!$('fb-arb-hide-losers')?.checked;
    const fastOnly = !!$('fb-arb-fast-only')?.checked;

    let rows = arbitrageData.items.slice();
    if (hideLosers) rows = rows.filter(r => r.netProfit > 0);
    // "Money back in 3 weeks" is the server's own fast tier, not a number re-derived here.
    if (fastOnly) rows = rows.filter(r => r.speedTier === 'fast');

    // Unpriced rows always sort last whatever the key — "we couldn't price this" isn't a zero.
    const nullsLast = (a, b, key) => (a[key] == null) - (b[key] == null) || null;
    // A free item's ROI is unbounded, not missing — it belongs at the top of an ROI sort, not
    // below a listing that loses money.
    const roiOf = r => (r.roiPercent != null ? r.roiPercent : r.netProfit > 0 && r.localAsk === 0 ? Infinity : null);
    const cmp = {
      profit: (a, b) => nullsLast(a, b, 'netProfit') ?? (b.netProfit - a.netProfit),
      // Two unbounded ROIs are equal, not NaN — Infinity - Infinity would corrupt the sort.
      roi: (a, b) => { const x = roiOf(a), y = roiOf(b); return (x == null) - (y == null) || (x === y ? 0 : y - x); },
      margin: (a, b) => nullsLast(a, b, 'marginPercent') ?? (b.marginPercent - a.marginPercent),
      distance: (a, b) => nullsLast(a, b, 'distanceMiles') ?? (a.distanceMiles - b.distanceMiles),
      ask: (a, b) => a.localAsk - b.localAsk,
      // The point of the whole feature: money that comes back soonest, and money that earns most
      // per day it is tied up. Rows that lose money stay below rows that make it in both — a fast
      // route to a loss is not a fast flip — and an unmeasured wait is never treated as instant.
      fastest: (a, b) => ((b.netProfit > 0) - (a.netProfit > 0))
        || (nullsLast(a, b, 'daysToCash') ?? ((a.daysToCash - b.daysToCash) || (b.netProfit - a.netProfit)))
        || 0,
      perday: (a, b) => ((b.netProfit > 0) - (a.netProfit > 0))
        || (nullsLast(a, b, 'profitPerDay') ?? (b.profitPerDay - a.profitPerDay))
        || 0,
    }[sort];
    rows.sort(cmp);

    const shown = $('fb-arb-shown');
    if (shown) {
      shown.textContent = rows.length === arbitrageData.items.length
        ? `${rows.length} shown`
        : `${rows.length} of ${arbitrageData.items.length} shown`;
    }

    body.innerHTML = rows.length
      ? rows.map(arbitrageRowHtml).join('')
      : `<tr><td colspan="14" class="fb-arb-empty">${fastOnly && arbitrageData.items.some(r => r.netProfit > 0)
          ? 'Nothing here turns your money around inside three weeks. Untick the filter to see the slower flips this search did find.'
          : 'Nothing here clears its fees. That is a real answer — this search has no local flip worth driving to.'}</td></tr>`;

    // Re-bound after every render: the table body is replaced wholesale by the sort and filter
    // controls, so listeners attached to the previous rows are gone with them.
    body.querySelectorAll('.fb-arb-neg-btn').forEach(btn =>
      btn.addEventListener('click', () => openNegotiation(btn.dataset.key)));
    body.querySelectorAll('.fb-arb-track-btn').forEach(btn =>
      btn.addEventListener('click', () => trackArbitrageRow(btn.dataset.key, btn)));
  }

  // Research is only worth what gets acted on. This is the one step between a ranked table the
  // seller closes and a deal they can still see next week — and it freezes the forecast as it
  // stands right now, which is the only way the pipeline can grade it against the outcome later.
  async function trackArbitrageRow(key, btn) {
    const row = (arbitrageData?.items || []).find(r => arbRowKey(r) === key);
    if (!row || !btn) return;

    const original = btn.textContent;
    btn.disabled = true;
    btn.textContent = 'Tracking…';

    try {
      const res = await fetch('/api/deals', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title: row.title,
          stage: 'sourced',
          source: row.source,
          sourceLabel: row.sourceLabel,
          sourceUrl: row.url,
          sourceItemId: row.itemId,
          askPrice: row.localAsk,
          maxBuyPrice: row.maxBuyPrice,
          projectedSalePrice: row.ebayExpectedSale,
          projectedNetProfit: row.netProfit,
          projectedRoiPercent: row.roiPercent,
          projectedDaysToCash: row.daysToCash,
          // What the forecast rested on, carried across verbatim. A projection with no stated
          // basis is impossible to argue with later, which makes it impossible to learn from.
          projectedBasis: [
            row.soldCompCount ? `${row.soldCompCount} sold comp${row.soldCompCount === 1 ? '' : 's'}` : '',
            row.confidenceLevel,
            row.speedLabel,
          ].filter(Boolean).join(' · '),
        }),
      });

      if (!res.ok) throw new Error(await res.text());
      pipeline = await res.json();
      renderDashboardPipeline();
      btn.textContent = '✓ Tracked';
      btn.classList.add('fb-arb-tracked');
      setLocalStatus(`Tracking "${row.title}" — it's on the Deal Pipeline board, with this forecast frozen against it.`);
      addActivity('Deal tracked', `${row.title} — ${money(row.netProfit || 0)} projected`);
    } catch (err) {
      btn.disabled = false;
      btn.textContent = original;
      setLocalStatus(`Couldn't track that deal: ${err.message}`);
    }
  }

  // Rows have no id of their own that survives across sources, so the key is source + the site's
  // own listing id, with the title as a last resort.
  function arbRowKey(row) {
    return `${row.source}::${row.itemId || row.url || row.title}`;
  }

  function arbitrageRowHtml(row, index) {
    const verdict = ARB_VERDICTS[row.verdict] || ARB_VERDICTS.no_data;
    const meta = [
      row.distanceMiles != null ? `${row.distanceMiles} mi` : '',
      row.location ? esc(row.location) : '',
      row.postedAgo ? esc(row.postedAgo) : '',
      row.originalPrice ? `was ${money(row.originalPrice)} — price dropped` : '',
    ].filter(Boolean).join(' · ');

    // A free item has no cost basis, so its ROI is unbounded rather than zero.
    const roi = row.roiPercent != null ? `${Math.round(row.roiPercent)}%`
      : row.netProfit > 0 && row.localAsk === 0 ? '∞' : '—';

    const evidence = row.ebayExpectedSale == null
      ? '<span class="fb-arb-muted">no sold history</span>'
      : [
          `${row.soldCompCount} sold comp${row.soldCompCount === 1 ? '' : 's'}`,
          row.terapeakCompCount ? `${row.terapeakCompCount} Terapeak` : '',
          row.confidenceLevel ? esc(row.confidenceLevel) : '',
          row.liquidityLevel ? esc(row.liquidityLevel) : '',
        ].filter(Boolean).join(' · ');

    // The comp lookup runs on the fullest title in the product group, which may not be this
    // row's own wording — say so rather than implying the match was against this exact tile.
    const pricedAs = row.pricedAs && row.pricedAs !== row.title
      ? ` title="Priced as: ${esc(row.pricedAs)}"` : '';

    return `
      <tr class="fb-arb-row fb-arb-row-${verdict.cls}">
        <td class="fb-arb-th-rank">${index + 1}</td>
        <td class="fb-arb-item">
          ${row.imageUrl ? `<img class="fb-arb-thumb" src="${esc(row.imageUrl)}" alt="" loading="lazy" referrerpolicy="no-referrer" />` : '<span class="fb-arb-thumb fb-arb-thumb-empty">📦</span>'}
          <span class="fb-arb-item-text">
            <a class="fb-arb-title" href="${esc(row.url)}" target="_blank" rel="noopener">${esc(row.title)} ↗</a>
            <span class="fb-arb-meta">${meta}</span>
            <span class="fb-verdict fb-verdict-${verdict.cls}">${verdict.label}</span>
            <span class="fb-arb-note">${esc(row.verdictNote)}</span>
          </span>
        </td>
        <td><span class="local-badge local-badge-${esc(row.source)}">${esc(row.sourceLabel || row.source)}</span></td>
        <td class="num">${row.localAsk > 0 ? money(row.localAsk) : 'Free'}</td>
        <td class="num"${pricedAs}>${row.ebayExpectedSale != null ? money(row.ebayExpectedSale) : '—'}</td>
        <td class="num fb-arb-cost">${row.estimatedFees != null ? `-${money(row.estimatedFees)}` : '—'}</td>
        <td class="num fb-arb-profit ${row.netProfit > 0 ? 'good' : row.netProfit != null ? 'bad' : ''}">${row.netProfit != null ? money(row.netProfit) : '—'}</td>
        <td class="num fb-arb-speed">${daysToCashCell(row)}</td>
        <td class="num">${roi}</td>
        <td class="num">${row.marginPercent != null ? `${Math.round(row.marginPercent)}%` : '—'}</td>
        <td class="num">${row.maxBuyPrice != null ? money(row.maxBuyPrice) : '—'}</td>
        <td class="num fb-arb-offer">${offerCell(row)}</td>
        <td class="fb-arb-evidence">${evidence}${row.disagreementMessage ? ` <span class="fb-arb-flag" title="${esc(row.disagreementMessage)}">⚠</span>` : ''}</td>
        <td class="fb-arb-track">${trackCell(row)}</td>
      </tr>`;
  }

  // The way out of a research table and into the pipeline. Offered on every row that could be
  // priced — including the ones that don't profit, because "I looked at this and walked" is worth
  // recording too — but never on a row with no sold history, which has no forecast to freeze.
  function trackCell(row) {
    if (row.ebayExpectedSale == null) {
      return '<span class="fb-arb-muted" title="Nothing sold-comp-backed to freeze against this one.">—</span>';
    }
    const tracked = isDealTracked(row.source, row.itemId);
    return tracked
      ? '<span class="fb-arb-tracked-flag" title="Already on the Deal Pipeline board.">✓ Tracked</span>'
      : `<button class="btn btn-secondary small fb-arb-track-btn" type="button" data-key="${esc(arbRowKey(row))}">＋ Track</button>`;
  }

  function isDealTracked(source, itemId) {
    if (!itemId || !pipeline?.deals) return false;
    return pipeline.deals.some(d => d.deal?.source === source && d.deal?.sourceItemId === itemId);
  }

  // The buy-side cell: what to open at, what saying it is worth, and the way into the drafts.
  // A row we'd walk away from gets the walk badge and no button — the most useful thing this
  // feature can do on a bad deal is fail to produce a message for it.
  function offerCell(row) {
    const plan = row.negotiation;
    if (!plan || plan.verdict === 'no_data') {
      return '<span class="fb-arb-muted" title="No sold history matched this, so there is no honest number to negotiate against.">—</span>';
    }

    const v = NEG_VERDICTS[plan.verdict] || NEG_VERDICTS.no_data;
    if (plan.openingOffer == null) {
      return `<span class="neg-badge neg-${v.cls}" title="${esc(plan.headline)}">${v.label}</span>`;
    }

    return `
      <span class="neg-offer" title="${esc(plan.headline)}">${money(plan.openingOffer)}</span>
      ${plan.upside > 0 ? `<span class="neg-upside">saves ${money(plan.upside)}</span>` : ''}
      <span class="neg-badge neg-${v.cls}">${v.label}</span>
      <button class="btn btn-secondary small fb-arb-neg-btn" type="button" data-key="${esc(arbRowKey(row))}">What to say</button>`;
  }

  // ── Buy-side negotiation ─────────────────────────────────────────────────
  // The other half of every deal on the board above. A dollar talked off the buy price is worth
  // more than a dollar added to the sale price — it arrives now, eBay takes none of it, and nothing
  // has to ship for it. This is where that dollar gets asked for.
  function bindNegotiation() {
    const close = () => $('neg-overlay')?.classList.add('hidden');
    on('neg-close', 'click', close);
    on('neg-close-x', 'click', close);
    $('neg-overlay')?.addEventListener('click', e => {
      if (e.target.id === 'neg-overlay') close();
    });
    document.addEventListener('keydown', e => {
      if (e.key === 'Escape' && !$('neg-overlay')?.classList.contains('hidden')) close();
    });
  }

  function openNegotiation(key) {
    const row = (arbitrageData?.items || []).find(r => arbRowKey(r) === key);
    const plan = row?.negotiation;
    if (!plan) return;

    const v = NEG_VERDICTS[plan.verdict] || NEG_VERDICTS.no_data;
    $('neg-title').textContent = row.title;
    const headline = $('neg-headline');
    headline.className = `neg-headline neg-headline-${v.cls}`;
    headline.textContent = plan.headline;

    // The four numbers, in the order they get used: open here, stop here, never past here.
    $('neg-numbers').innerHTML = [
      tile('Their ask', money(plan.askPrice), row.postedAgo ? `listed ${esc(row.postedAgo)}` : ''),
      plan.openingOffer != null
        ? tile('Open at', money(plan.openingOffer),
               plan.upside > 0 ? `${plan.openingDiscountPercent}% off — ${money(plan.upside)} straight to you` : 'their price', 'open')
        : '',
      plan.ceilingPrice != null
        ? tile('Stop at', money(plan.ceilingPrice),
               // When the ceiling IS their ask, "above this it stops being worth the drive" reads as
               // a warning about a price nobody is being asked to pay.
               plan.ceilingPrice >= plan.askPrice
                 ? 'their price is already inside your limit'
                 : 'above this it stops being worth the drive', 'stop') : '',
      tile('Break-even', money(plan.breakEvenPrice), 'pay this and you worked for free', 'stop'),
    ].filter(Boolean).join('');

    $('neg-signals').innerHTML = (plan.signals || []).length
      ? `<div class="neg-signals-head">Your leverage</div><ul>${
          plan.signals.map(s => `<li>${esc(s)}</li>`).join('')}</ul>`
      : '';

    // The counter-offer table: they name a number mid-conversation, and this says yes or no without
    // any arithmetic happening in a driveway.
    $('neg-ladder').innerHTML = (plan.ladder || []).length
      ? `<div class="neg-signals-head">If you end up paying…</div>
         <table class="inv-table neg-ladder-table">
           <thead><tr><th class="num">Price</th><th class="num">You keep</th><th class="num">ROI</th><th>What that is</th></tr></thead>
           <tbody>${plan.ladder.map(negRungHtml).join('')}</tbody>
         </table>`
      : '';

    $('neg-messages').innerHTML = (plan.messages || []).length
      ? `<div class="neg-signals-head">What to say</div>${plan.messages.map(negMessageHtml).join('')}`
      : '<p class="neg-nomessage">No message drafted for this one on purpose — there is no offer here that both makes you money and gets a reply. Sending one anyway is how a bad deal gets talked into.</p>';

    $('neg-evidence').textContent = plan.evidenceNote
      ? `${plan.evidenceNote} Read the draft before you send it and make it sound like you — this app never messages anyone on your behalf.`
      : '';

    $('neg-messages').querySelectorAll('.neg-copy').forEach(btn =>
      btn.addEventListener('click', () => negCopy(btn)));

    $('neg-overlay')?.classList.remove('hidden');
  }

  function tile(label, value, sub, cls) {
    return `<div class="neg-tile${cls ? ` neg-tile-${cls}` : ''}">
        <span class="neg-tile-label">${esc(label)}</span>
        <strong class="neg-tile-value">${value}</strong>
        ${sub ? `<span class="neg-tile-sub">${sub}</span>` : ''}
      </div>`;
  }

  function negRungHtml(rung) {
    const tone = NEG_TONES[rung.tone] || NEG_TONES.thin;
    const tags = [rung.isOpening ? 'your opener' : '', rung.isCeiling ? 'your ceiling' : '',
                  rung.isAsk ? 'their ask' : ''].filter(Boolean)
      .map(t => `<span class="neg-tag">${t}</span>`).join('');

    return `
      <tr class="neg-rung neg-rung-${tone.cls}">
        <td class="num"><strong>${moneyExact(rung.price)}</strong>${tags}</td>
        <td class="num neg-rung-net">${moneyExact(rung.netProfit)}</td>
        <td class="num">${rung.roiPercent != null ? `${Math.round(rung.roiPercent)}%` : '∞'}</td>
        <td><span class="neg-tone neg-tone-${tone.cls}">${tone.label}</span> <span class="neg-rung-label">${esc(rung.label)}</span></td>
      </tr>`;
  }

  // Editable on purpose. A message that reads like a form letter gets treated like one, and the
  // seller's own wording closes deals that a template doesn't.
  function negMessageHtml(msg) {
    const rows = Math.min(12, Math.max(4, (msg.text || '').split('\n').length + 2));
    return `
      <div class="neg-message">
        <div class="neg-message-head">
          <div>
            <strong>${esc(msg.label)}</strong>
            <span class="neg-message-when">${esc(msg.when)}</span>
          </div>
          <button class="btn btn-secondary small neg-copy" type="button" data-id="${esc(msg.id)}">Copy</button>
        </div>
        <textarea class="neg-message-text" rows="${rows}" spellcheck="true">${esc(msg.text)}</textarea>
      </div>`;
  }

  // Copies whatever is in the box now, not the original draft — an edited message is the one being
  // sent, so it is the one that has to end up on the clipboard.
  async function negCopy(btn) {
    const text = btn.closest('.neg-message')?.querySelector('.neg-message-text')?.value || '';
    const original = btn.textContent;
    try {
      await navigator.clipboard.writeText(text);
      btn.textContent = 'Copied ✓';
    } catch {
      btn.textContent = 'Select and copy';
    }
    setTimeout(() => { btn.textContent = original; }, 2000);
  }

  // ── Roll the Dice ────────────────────────────────────────────────────────
  // The board every other panel can't produce: the seller types nothing, the server sweeps whole
  // categories of sold history, and what comes back is what to buy, for how much, and where.
  //
  // A roll is minutes of server work across four external systems, so it borrows the local panel's
  // never-throw fetch contract (localFetchJson) — a roll that will never come back has to stop
  // looking like one that is still working.
  const DICE_TIMEOUT_MS = 10 * 60 * 1000;

  let diceData = null;
  let diceNextSeed = null;
  let diceRolling = false;

  const DICE_TIERS = {
    jackpot: { label: '🎰 Jackpot', cls: 'jackpot' },
    strong:  { label: '💰 Strong play', cls: 'strong' },
    target:  { label: '🎯 Target price', cls: 'target' },
    thin:    { label: '⚠️ Thin margin', cls: 'thin' },
    watch:   { label: '👀 Watch — thin data', cls: 'watch' },
    pass:    { label: '✕ Pass', cls: 'pass' },
  };

  function bindRollTheDice() {
    // The dashboard band: one click has to produce a board, not a form to fill in.
    on('btn-roll-dice', 'click', () => {
      showOpportunitySection();
      $('dice-results')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
      rollTheDice(null);
    });
    on('dice-roll-btn', 'click', () => rollTheDice(null));
    on('dice-again-btn', 'click', () => rollTheDice(diceNextSeed));
    on('dice-only-buyable', 'change', renderDiceBoard);
    on('dice-sort', 'change', renderDiceBoard);
    on('dice-fast-only', 'change', renderDiceBoard);
    on('dice-zip-input', 'keydown', e => { if (e.key === 'Enter') rollTheDice(null); });

    // Same remembered zip and radius the local scan uses — a seller types their zip code once.
    const zip = localStorage.getItem('fbZip');
    const radius = localStorage.getItem('fbRadius');
    if (zip && $('dice-zip-input')) $('dice-zip-input').value = zip;
    if (radius && $('dice-radius-select')) {
      const select = $('dice-radius-select');
      if ([...select.options].some(o => o.value === radius)) select.value = radius;
    }
  }

  function setDiceStatus(message, retry) {
    const el = $('dice-status');
    if (!el) return;
    if (!message) { el.innerHTML = ''; return; }
    el.innerHTML = esc(message) +
      (retry ? ' <button type="button" class="btn btn-secondary small dice-retry-btn">Try again</button>' : '');
    el.querySelector('.dice-retry-btn')?.addEventListener('click', () => rollTheDice(null));
  }

  // seed === null means "a fresh random roll"; passing the previous response's nextSeed is what
  // makes Roll again sweep categories this seller hasn't just seen.
  async function rollTheDice(seed) {
    if (diceRolling) return;

    const zip = $('dice-zip-input')?.value.trim() || '';
    const radius = $('dice-radius-select')?.value || '40';
    const niches = $('dice-niches-select')?.value || '4';
    if (zip) localStorage.setItem('fbZip', zip);
    localStorage.setItem('fbRadius', radius);

    // Whatever sites the seller ticked in Local Deals. Omitted when nothing is ticked (or the
    // picker hasn't loaded), which lets the server search everything reachable.
    const sources = selectedSourceIds();

    // "best" is the server's own ranking, which is the default — only the velocity sorts are worth
    // asking for, and the board can still be re-sorted client-side afterwards without re-rolling.
    const sort = $('dice-sort')?.value || 'best';

    const qs = `niches=${encodeURIComponent(niches)}&zip=${encodeURIComponent(zip)}` +
      `&radius=${encodeURIComponent(radius)}` +
      (sources.length ? `&sources=${encodeURIComponent(sources.join(','))}` : '') +
      (seed != null ? `&seed=${encodeURIComponent(seed)}` : '') +
      `&sort=${encodeURIComponent(sort)}`;

    const buttons = ['dice-roll-btn', 'dice-again-btn', 'btn-roll-dice'].map($).filter(Boolean);
    diceRolling = true;
    buttons.forEach(b => { b.disabled = true; });
    $('dice-results')?.classList.add('hidden');
    // Honest about the cost: a sweep, a real sold-comp lookup per product, then a supply search
    // per product. Minutes, not seconds — and Facebook loads a real browser page.
    setDiceStatus(`🎲 Rolling — sweeping ${niches} categories of eBay sold history, pricing the best ` +
      `products against real comps, then hunting for where to buy them` +
      `${zip ? ` near ${zip}` : ' on eBay'}. Give it a minute or two…`);

    const { data, error } = await localFetchJson(`/api/opportunities/roll-the-dice?${qs}`, DICE_TIMEOUT_MS);

    diceRolling = false;
    buttons.forEach(b => { b.disabled = false; });

    if (!data) {
      setDiceStatus(`The roll didn't complete. ${error}`, true);
      return;
    }
    renderDice(data);
  }

  function renderDice(data) {
    const wrap = $('dice-results');
    if (!wrap) return;

    diceNextSeed = data.nextSeed;
    $('dice-again-btn')?.classList.remove('hidden');

    if (data.status === 'error') {
      setDiceStatus(data.error || 'The roll came back with nothing usable.', true);
      return;
    }

    diceData = data;

    const swept = (data.niches || []).length;
    const summary = [
      data.jackpotCount
        ? `<strong class="fb-arb-hit">${data.jackpotCount} jackpot${data.jackpotCount === 1 ? '' : 's'}</strong>`
        : `<strong>${data.count}</strong> play${data.count === 1 ? '' : 's'}`,
      `across ${swept} categor${swept === 1 ? 'y' : 'ies'}`,
      data.fastCashCount ? `<strong class="fb-arb-hit">${data.fastCashCount} that ${data.fastCashCount === 1 ? 'pays' : 'pay'} back inside 3 weeks</strong>` : '',
      data.totalPotentialProfit > 0
        ? `${money(data.totalPotentialProfit)} of profit sitting in supply you can buy right now`
        : '',
    ].filter(Boolean).join(' · ');

    // The funnel, in full. What was thrown away matters as much as what survived: a short board is
    // the guards working, and this is where a seller can see that rather than assume the sweep
    // simply found nothing.
    $('dice-summary').innerHTML = summary +
      `<div class="fb-arb-sources">Mined ${data.compsScanned.toLocaleString()} sold comps → ` +
      `${data.productsConsidered} product${data.productsConsidered === 1 ? '' : 's'} worth a look → ` +
      `${data.productsPriced} priced against real comps → ${data.productsSourced} checked for supply` +
      `${data.terapeakScrapesUsed ? `, ${data.terapeakScrapesUsed} re-checked live on Terapeak` : ''}. ` +
      `${data.productsDropped ? `${data.productsDropped} dropped — too little sold history, or two reads of it that disagreed. ` : ''}` +
      `${data.supplyRejected ? `${data.supplyRejected} cheap listing${data.supplyRejected === 1 ? '' : 's'} rejected as parts or accessories rather than the product. ` : ''}` +
      `${data.terapeakConnected ? '' : 'Terapeak not connected — sold-comps database only. '}` +
      `Roll #${data.seed} · ${data.rollsToCoverEverything} rolls covers all ${data.nichesInUniverse} categories.</div>`;

    const warn = $('dice-warning');
    if (warn) {
      warn.textContent = data.dataWarning || '';
      warn.classList.toggle('hidden', !data.dataWarning);
    }

    // Which categories were dug, and what each one gave up — a sweep that reports "no plays"
    // without saying where it looked can't be trusted or repeated.
    const chips = $('dice-niche-chips');
    if (chips) {
      chips.innerHTML = (data.niches || []).map(n => {
        const detail = n.playsFound
          ? `${n.playsFound} play${n.playsFound === 1 ? '' : 's'}`
          : n.note ? esc(n.note)
          : `${n.productsFound} product${n.productsFound === 1 ? '' : 's'}, none made the board`;
        return `<span class="dice-chip${n.playsFound ? ' dice-chip-hit' : ''}">
                  <strong>${esc(n.label)}</strong> ${detail}
                  <span class="dice-chip-probe">${(n.probes || []).map(esc).join(', ')}</span>
                </span>`;
      }).join('');
    }

    // Which sourcing channels actually answered — an empty supply list has to be distinguishable
    // from a site that was never searched.
    const searched = [
      data.ebaySupplySearched ? 'eBay Buy It Now' : '',
      ...(data.sources || []).map(s => s.status === 'ok'
        ? `${s.label} (${s.count})`
        : `${s.label} — ${s.status === 'not_connected' ? 'not connected'
            : s.status === 'session_expired' ? 'session expired' : 'unavailable'}`),
    ].filter(Boolean);
    setDiceStatus(searched.length
      ? `Supply checked on: ${searched.join(' · ')}.`
      : 'No supply sites were searched — add a zip code to include local classifieds.');

    renderDiceBoard();
    wrap.classList.remove('hidden');
  }

  // Sorting and filtering are pure views over the response already in hand — neither may re-run a
  // roll, which is minutes of work across four systems.
  function renderDiceBoard() {
    const board = $('dice-board');
    if (!board || !diceData) return;

    const onlyBuyable = !!$('dice-only-buyable')?.checked;
    const fastOnly = !!$('dice-fast-only')?.checked;
    const sort = $('dice-sort')?.value || 'best';
    const all = diceData.plays || [];

    let rows = onlyBuyable ? all.filter(p => p.sources && p.sources.length) : all.slice();
    if (fastOnly) rows = rows.filter(p => p.speedTier === 'fast');

    // "best" is the order the server ranked them in — believability first — and is left alone.
    // The velocity sorts keep plays that can't be believed at the bottom, same rule the server uses.
    // The play's money: what the live buy nets, or what buying at the target would net.
    const cash = p => (p.netProfit != null ? p.netProfit : p.profitAtTarget) || 0;
    const weak = p => p.tier === 'pass' || p.tier === 'no_data';
    const diceCmp = {
      fastest: (a, b) => (weak(a) - weak(b))
        || ((a.daysToCash == null) - (b.daysToCash == null))
        || ((a.daysToCash - b.daysToCash) || (cash(b) - cash(a))) || 0,
      perday: (a, b) => (weak(a) - weak(b))
        || ((a.profitPerDay == null) - (b.profitPerDay == null))
        || ((b.profitPerDay - a.profitPerDay) || (cash(b) - cash(a))) || 0,
      profit: (a, b) => (weak(a) - weak(b)) || (cash(b) - cash(a)),
    }[sort];
    if (diceCmp) rows = rows.slice().sort(diceCmp);

    const shown = $('dice-shown');
    if (shown) {
      shown.textContent = rows.length === all.length
        ? `${rows.length} shown`
        : `${rows.length} of ${all.length} shown`;
    }

    board.innerHTML = rows.length
      ? rows.map(dicePlayHtml).join('')
      : all.length
        ? `<p class="opportunity-empty">${fastOnly
            ? 'Nothing on this board turns your money around inside three weeks. Untick that filter to see the slower plays — or roll again for different categories.'
            : 'Nothing on this board is for sale anywhere right now. The target prices above are still worth watching for — or roll again for different categories.'}</p>`
        : '<p class="opportunity-empty">This roll found no product with enough sold history to stand behind. That is a real answer, not an error — roll again and the sweep moves on to different categories.</p>';

    board.querySelectorAll('.dice-hunt-btn').forEach(btn =>
      btn.addEventListener('click', () => huntPlayLocally(btn.dataset.query || '')));
  }

  function dicePlayHtml(play, index) {
    const tier = DICE_TIERS[play.tier] || DICE_TIERS.watch;
    // A live buy shows what it actually nets; a target shows what buying at the target would net.
    const live = play.netProfit != null;
    const headline = live ? play.netProfit : play.profitAtTarget;
    const headlineNote = live
      ? `net after fees, buying at ${moneyExact(play.bestBuyPrice)}`
      : play.targetBuyPrice > 0
        ? `net if you buy it at ${moneyExact(play.targetBuyPrice)}`
        // No price clears the jackpot bar, so the honest headline is what's left at a cost of
        // nothing — quoting "buy it at $0.00" would read as a bug.
        : `net even if it were free — break even is ${moneyExact(play.maxBuyPrice)}`;

    const facts = [
      play.ebayExpectedSale != null ? `Sells for <strong>${money(play.ebayExpectedSale)}</strong>` : '',
      `Break even at <strong>${moneyExact(play.maxBuyPrice)}</strong>`,
      play.targetBuyPrice > 0 ? `Target buy <strong>${moneyExact(play.targetBuyPrice)}</strong>` : '',
      play.roiPercent != null ? `${Math.round(play.roiPercent)}% ROI` : '',
      // The wait, and what the money earns per day of it — the difference between a flip and a
      // shelf. Rendered as a badge so a "dead money" play can't read like a fast one.
      play.daysToCash != null
        ? `<span class="speed-badge speed-${(SPEED_TIERS[play.speedTier] || SPEED_TIERS.unknown).cls}" title="${esc(play.speedNote || '')}">` +
          `~${play.daysToCash}d to cash${play.profitPerDay > 0 ? ` · ${perDay(play.profitPerDay)}` : ''}</span>`
        : '',
    ].filter(Boolean).join(' · ');

    const evidence = [
      `${play.soldCompCount} sold comp${play.soldCompCount === 1 ? '' : 's'}`,
      play.terapeakCompCount ? `${play.terapeakCompCount} Terapeak` : '',
      play.confidenceLevel ? esc(play.confidenceLevel) : '',
      play.liquidityLevel ? esc(play.liquidityLevel) : '',
      play.estimatedMonthlySales > 0 ? `~${Math.round(play.estimatedMonthlySales)} sold/month` : '',
    ].filter(Boolean).join(' · ');

    const sources = (play.sources || []).map(src => {
      const good = src.netProfit != null && src.netProfit > 0;
      const meta = [
        src.distanceMiles != null ? `${src.distanceMiles} mi` : '',
        src.location ? esc(src.location) : '',
        src.postedAgo ? esc(src.postedAgo) : '',
      ].filter(Boolean).join(' · ');
      return `<div class="dice-source">
                <span class="local-badge local-badge-${esc(src.source)}">${esc(src.sourceLabel || src.source)}</span>
                <a class="dice-source-title" href="${esc(src.url)}" target="_blank" rel="noopener">${esc(src.title)} ↗</a>
                <span class="dice-source-buy">${moneyExact(src.buyPrice)}</span>
                <span class="dice-source-net ${good ? 'good' : 'bad'}">${src.netProfit != null ? `${good ? '+' : ''}${money(src.netProfit)} net` : '—'}</span>
                <span class="dice-source-meta">${meta}</span>
              </div>`;
    }).join('');

    // eBay's own sold-listing search for the same keyword: the seller can check the comps behind
    // the number themselves, with no API call and no trust required.
    const soldUrl = `https://www.ebay.com/sch/i.html?_nkw=${encodeURIComponent(play.searchQuery || play.product)}&LH_Sold=1&LH_Complete=1`;

    return `
      <article class="dice-play dice-play-${tier.cls}">
        <div class="dice-play-rank">${index + 1}</div>
        ${play.imageUrl
          ? `<img class="dice-play-thumb" src="${esc(play.imageUrl)}" alt="" loading="lazy" referrerpolicy="no-referrer" />`
          : '<div class="dice-play-thumb dice-play-thumb-empty">📦</div>'}
        <div class="dice-play-body">
          <div class="dice-play-head">
            <span class="dice-tier dice-tier-${tier.cls}">${tier.label}</span>
            <span class="dice-play-niche">${esc(play.nicheLabel)}</span>
          </div>
          <h4 class="dice-play-title">${esc(play.product)}</h4>
          <div class="dice-play-money">
            <span class="dice-play-profit ${headline > 0 ? 'good' : 'bad'}">${money(headline)}</span>
            <span class="dice-play-profit-note">${headlineNote}</span>
          </div>
          <div class="dice-play-facts">${facts}</div>
          <div class="dice-play-note">${esc(play.tierNote)}</div>
          <div class="dice-play-evidence">${evidence}${play.disagreementMessage ? ` <span class="fb-arb-flag" title="${esc(play.disagreementMessage)}">⚠</span>` : ''}</div>
          <div class="dice-play-where">${esc(play.whereToLook)}</div>
          ${sources ? `<div class="dice-sources">${sources}</div>` : ''}
          <div class="dice-play-actions">
            <button class="btn btn-secondary small dice-hunt-btn" type="button" data-query="${esc(play.searchQuery)}">📍 Hunt this locally</button>
            <a class="btn btn-ghost small" href="${esc(soldUrl)}" target="_blank" rel="noopener">See the sold comps ↗</a>
          </div>
        </div>
      </article>`;
  }

  // Hands the play straight to the local scan below — the roll says WHAT to buy, and Local Deals is
  // already the panel that finds every one of them near you and ranks them.
  function huntPlayLocally(query) {
    if (!query) return;
    const input = $('fb-query-input');
    if (input) input.value = query;
    const diceZip = $('dice-zip-input')?.value.trim() || '';
    if (diceZip && $('fb-zip-input')) $('fb-zip-input').value = diceZip;
    $('fb-arb-btn')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    runLocalArbitrage();
  }

  async function openSetupWithPolicies(status) {
    openSetup(status);
    if (isConnected) loadPolicies(false);
  }

  async function showLogsSection() {
    hideOverlaySections();
    $('logs-section')?.classList.remove('hidden');
    $('logs-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'logs'));
    await loadLogs();
  }

  async function showLicenseSection() {
    hideOverlaySections();
    $('license-section')?.classList.remove('hidden');
    $('license-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'license'));
    const status = await fetch('/api/license/status').then(r => r.json()).catch(() => null);
    if (status) updateLicenseUI(status);
  }

  // ── Opportunity Finder ───────────────────────────────────────────────────
  function showOpportunitySection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('opportunity-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'opportunity'));
    loadTerapeakStatus();
    loadFacebookStatus();
    // Also loaded on its own, not only off the back of the Facebook status call: Craigslist is
    // searchable whether or not that call succeeds.
    loadLocalSources();
    loadHighSellThrough();
    loadLowCompetition();
    loadPricingRecommendations();
    loadSeasonalDemand();
  }

  // ── Opportunity Finder insight cards ─────────────────────────────────────
  function renderInsightList(elId, items, rowFn, emptyMsg) {
    const el = $(elId);
    if (!el) return;
    el.innerHTML = items.length
      ? items.map(rowFn).join('')
      : `<p class="opportunity-empty">${emptyMsg}</p>`;
  }

  async function loadHighSellThrough() {
    try {
      const data = await fetch('/api/insights/high-sell-through').then(r => r.json());
      renderInsightList('insight-sell-through', data.items || [], it => `
        <div class="opportunity-insight-row">
          <span class="opportunity-insight-label">${esc(it.category)}</span>
          <span class="opportunity-insight-value good">${it.sellThroughPercent}%</span>
        </div>`,
        'Not enough priced categories yet — run some Opportunity Finder searches to build this up.');
    } catch { /* leave loading state — non-critical */ }
  }

  async function loadLowCompetition() {
    try {
      const data = await fetch('/api/insights/low-competition').then(r => r.json());
      renderInsightList('insight-low-competition', data.items || [], it => `
        <div class="opportunity-insight-row">
          <span class="opportunity-insight-label">${esc(it.category)}</span>
          <span class="opportunity-insight-value">${it.activeListings} listed · ${it.sellThroughPercent}% sell-through</span>
        </div>`,
        'Not enough priced categories yet — run some Opportunity Finder searches to build this up.');
    } catch { /* leave loading state — non-critical */ }
  }

  async function loadPricingRecommendations() {
    try {
      const data = await fetch('/api/insights/pricing-recommendations').then(r => r.json());
      renderInsightList('insight-pricing-recs', data.items || [], it => {
        const cls = it.deltaPercent > 0 ? 'good' : 'bad';
        const sign = it.deltaPercent > 0 ? '+' : '';
        return `
        <div class="opportunity-insight-row">
          <a href="${esc(it.listingUrl)}" target="_blank" rel="noopener">${esc(it.title)}</a>
          <span class="opportunity-insight-value ${cls}">$${it.currentPrice.toFixed(2)} → $${it.suggestedPrice.toFixed(2)} (${sign}${it.deltaPercent}%)</span>
        </div>`;
      }, 'No pricing gaps found yet in your active listings against cached market data.');
    } catch { /* leave loading state — non-critical */ }
  }

  async function loadSeasonalDemand() {
    try {
      const data = await fetch('/api/insights/seasonal-demand').then(r => r.json());
      const el = $('insight-seasonal');
      if (!el) return;
      const cur = data.current, next = data.upcoming;
      el.innerHTML = `
        <div class="opportunity-insight-row"><span class="opportunity-insight-label">${esc(cur.monthName)} (now)</span></div>
        <p class="opportunity-empty" style="margin:-4px 0 8px">${cur.categories.map(esc).join(', ')}</p>
        <div class="opportunity-insight-row"><span class="opportunity-insight-label">${esc(next.monthName)} (upcoming)</span></div>
        <p class="opportunity-empty" style="margin:-4px 0 0">${next.categories.map(esc).join(', ')}</p>`;
    } catch { /* leave loading state — non-critical */ }
  }

  function renderUnderpricedCard(items) {
    const top = items.filter(it => it.isUnderpriced).sort((a, b) => (b.profitPercent ?? 0) - (a.profitPercent ?? 0)).slice(0, 5);
    renderInsightList('insight-underpriced', top, it => `
      <div class="opportunity-insight-row">
        <a href="${esc(it.url)}" target="_blank" rel="noopener">${esc(it.title)}</a>
        <span class="opportunity-insight-value good">+${it.profitPercent}%</span>
      </div>`,
      'No underpriced auctions in this search — try a broader keyword.');
  }

  function closeOpportunitySection() {
    $('opportunity-section')?.classList.add('hidden');
    showDashboard();
  }

  // ── Inventory Health ─────────────────────────────────────────────────────
  // The one screen in this app that looks at inventory the seller ALREADY owns: every live eBay
  // listing priced against real sold comps, checked against what they paid, and given a price that
  // both moves it and clears cost. See InventoryHealthAnalyzer.cs for the judgement rules and
  // ScanInventoryHealthAsync in Program.cs for the orchestration.
  let invScan     = null;   // the last InventoryHealthResult, kept so filtering never re-scans
  let invSelected = new Set();   // listing IDs ticked for repricing
  let invCosts    = new Map();   // listingId -> CostBasisEntry, so an edit re-renders without a refetch

  function showInventorySection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('inventory-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'inventory'));
    loadCostBasis();
  }

  function closeInventorySection() {
    $('inventory-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindInventoryHealth() {
    on('inv-scan-btn', 'click', runInventoryScan);
    on('inv-close', 'click', closeInventorySection);
    on('inv-home', 'click', closeInventorySection);
    // Filtering is a pure view over the result already in hand — changing it must never re-run a
    // scan that costs eBay calls and comp lookups.
    on('inv-verdict-filter', 'change', renderInventoryRows);
    on('inv-select-all', 'change', toggleSelectAllReprice);
    on('inv-preview-btn', 'click', () => submitReprice(true));
    on('inv-apply-btn', 'click', openRepriceConfirm);
    on('inv-confirm-cancel', 'click', () => $('inv-confirm-overlay')?.classList.add('hidden'));
    on('inv-confirm-go', 'click', () => {
      $('inv-confirm-overlay')?.classList.add('hidden');
      submitReprice(false);
    });
  }

  async function loadCostBasis() {
    try {
      const entries = await fetch('/api/inventory/cost-basis').then(r => r.json());
      invCosts = new Map((entries || []).map(e => [e.listingId || e.sku, e]));
    } catch { /* cost basis is optional — a scan without it still works, it just can't check floors */ }
  }

  async function runInventoryScan() {
    const btn = $('inv-scan-btn');
    const minDays  = $('inv-min-days')?.value || '0';
    const maxItems = $('inv-max-items')?.value || '120';

    if (btn) { btn.disabled = true; btn.textContent = 'Scanning…'; }
    setInvStatus('Reading your live eBay listings…');
    $('inv-results').innerHTML = '<p class="opportunity-empty">Pricing each listing against sold comps — this takes a moment on a large inventory.</p>';

    try {
      const res  = await fetch(`/api/inventory/health?minDays=${minDays}&maxItems=${maxItems}`);
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'The scan failed.');
      invScan = data;
      invSelected.clear();
      renderInventoryScan();
    } catch (err) {
      invScan = null;
      $('inv-summary')?.classList.add('hidden');
      $('inv-bulk-bar')?.classList.add('hidden');
      $('inv-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The scan failed.')}</p>`;
      setInvStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '🔎 Scan My Listings'; }
    }
  }

  function renderInventoryScan() {
    const warn = $('inv-warning');

    if (invScan.status === 'ebay_unavailable') {
      $('inv-summary')?.classList.add('hidden');
      $('inv-bulk-bar')?.classList.add('hidden');
      warn?.classList.add('hidden');
      $('inv-results').innerHTML =
        '<p class="opportunity-empty">Your eBay account isn\'t connected, or the token has expired. Reconnect it in Settings, then scan again.</p>';
      setInvStatus('');
      return;
    }

    if (invScan.dataWarning) {
      warn.textContent = invScan.dataWarning;
      warn.classList.remove('hidden');
    } else {
      warn?.classList.add('hidden');
    }

    renderInventorySummary(invScan.summary || {});
    renderInventoryRows();

    const s = invScan.summary || {};
    setInvStatus(
      `${invScan.itemsAnalyzed} of ${invScan.activeListings} active listings, ` +
      `${invScan.productsPriced} distinct product${invScan.productsPriced === 1 ? '' : 's'} priced` +
      (invScan.terapeakScrapesUsed ? `, ${invScan.terapeakScrapesUsed} Terapeak lookup${invScan.terapeakScrapesUsed === 1 ? '' : 's'}` : '') +
      (s.unknownAgeCount ? ` · ${s.unknownAgeCount} with no start date reported by eBay` : ''));
  }

  // The portfolio numbers. Deliberately leads with capital rather than with a count: "18 stale
  // listings" is a statistic, "$6,240 sitting in listings older than 90 days" is a decision.
  function renderInventorySummary(s) {
    const el = $('inv-summary');
    if (!el) return;

    const tiles = [
      { label: 'Capital tied up', value: money(s.totalCapitalTiedUp),
        sub: s.withCostBasis === s.listingsAnalyzed
          ? 'At what you paid'
          : `${s.withCostBasis} of ${s.listingsAnalyzed} at cost; the rest at market value`, tone: '' },
      { label: `Stuck ${90}+ days`, value: money(s.staleCapital),
        sub: `${s.staleCount} listing${s.staleCount === 1 ? '' : 's'}${s.medianDaysListed != null ? ` · median age ${s.medianDaysListed}d` : ''}`,
        tone: s.staleCount > 0 ? 'warn' : '' },
      { label: 'Dead capital', value: money(s.deadCapital),
        sub: `${s.deadCapitalCount} over 180 days, no watchers, above market`, tone: s.deadCapitalCount > 0 ? 'bad' : '' },
      { label: 'Priced above market', value: money(s.totalAboveMarket),
        sub: `${s.overpricedCount} listing${s.overpricedCount === 1 ? '' : 's'} 15%+ over comps`, tone: s.overpricedCount > 0 ? 'warn' : '' },
      { label: 'Left on the table', value: money(s.moneyLeftOnTable),
        sub: `${s.underpricedCount} listed below market — per sale`, tone: s.underpricedCount > 0 ? 'good' : '' },
      { label: 'Ready to reprice', value: String(s.repriceCandidates || 0),
        sub: s.projectedNetIfRepricedSells
          ? `${money(s.projectedNetIfRepricedSells)} net if they then sell`
          : 'Record cost basis to see net profit', tone: s.repriceCandidates > 0 ? 'good' : '' },
    ];

    el.innerHTML = tiles.map(t => `
      <div class="inv-tile ${t.tone ? 'inv-tile-' + t.tone : ''}">
        <div class="inv-tile-label">${esc(t.label)}</div>
        <div class="inv-tile-value">${esc(t.value)}</div>
        <div class="inv-tile-sub">${esc(t.sub)}</div>
      </div>`).join('');
    el.classList.remove('hidden');
  }

  const INV_VERDICTS = {
    underwater:   { label: '🌊 Underwater',   cls: 'inv-v-underwater' },
    dead_capital: { label: '🪦 Dead capital', cls: 'inv-v-dead' },
    stale:        { label: '🕸️ Stale',        cls: 'inv-v-stale' },
    overpriced:   { label: '⬆️ Overpriced',   cls: 'inv-v-over' },
    underpriced:  { label: '⬇️ Underpriced',  cls: 'inv-v-under' },
    selling:      { label: '🔁 Selling',      cls: 'inv-v-selling' },
    priced_right: { label: '✓ Priced right',  cls: 'inv-v-ok' },
    fresh:        { label: '🌱 Fresh',        cls: 'inv-v-ok' },
    no_data:      { label: '? No data',       cls: 'inv-v-nodata' },
  };

  function invFilterMatches(item, filter) {
    switch (filter) {
      case 'action':      return item.hasRecommendation;
      case 'stale':       return item.verdict === 'stale' || item.verdict === 'dead_capital';
      case 'selling':     return item.verdict === 'selling';
      case 'overpriced':  return item.verdict === 'overpriced';
      case 'underpriced': return item.verdict === 'underpriced';
      case 'underwater':  return item.verdict === 'underwater';
      case 'no_data':     return item.verdict === 'no_data';
      default:            return true;
    }
  }

  function renderInventoryRows() {
    if (!invScan) return;
    const filter = $('inv-verdict-filter')?.value || 'all';
    const rows = (invScan.items || []).filter(i => invFilterMatches(i, filter));
    const el = $('inv-results');

    if (rows.length === 0) {
      el.innerHTML = `<p class="opportunity-empty">${
        (invScan.items || []).length === 0
          ? 'No active listings came back from eBay for this filter.'
          : 'Nothing in your inventory matches that filter — which is good news.'}</p>`;
      $('inv-bulk-bar')?.classList.add('hidden');
      return;
    }

    el.innerHTML = `
      <table class="inv-table">
        <thead>
          <tr>
            <th class="inv-col-check"></th>
            <th>Listing</th>
            <th class="num">Age</th>
            <th class="num">Your price</th>
            <th class="num">Market</th>
            <th class="num">Gap</th>
            <th class="num">You paid</th>
            <th class="num" title="Break-even is where the sale stops losing money. Your floor is the lowest offer worth taking — set it in Settings → Fees &amp; Costs.">Break-even<div class="inv-th-sub">&amp; your floor</div></th>
            <th class="num">Suggested</th>
            <th>Verdict</th>
          </tr>
        </thead>
        <tbody>${rows.map(invRowHtml).join('')}</tbody>
      </table>`;

    el.querySelectorAll('.inv-check').forEach(cb => cb.addEventListener('change', onRepriceCheck));
    el.querySelectorAll('.inv-cost-input').forEach(inp => {
      inp.addEventListener('change', onCostBasisEdit);
      inp.addEventListener('keydown', e => { if (e.key === 'Enter') inp.blur(); });
    });
    el.querySelectorAll('.inv-price-input').forEach(inp => inp.addEventListener('change', onSuggestedOverride));

    $('inv-bulk-bar')?.classList.remove('hidden');
    refreshSelectionNote();
  }

  function invRowHtml(item) {
    const v = INV_VERDICTS[item.verdict] || INV_VERDICTS.no_data;
    const gap = item.priceGapPercent;
    const gapCls = gap == null ? '' : gap >= 15 ? 'inv-gap-bad' : gap <= -10 ? 'inv-gap-good' : 'inv-gap-ok';
    // Only markdowns are tickable. A raise is a per-item judgement call (see SuggestPrice), and a
    // row with no recommendation has nothing to apply.
    const selectable = item.hasRecommendation && !item.requiresReview && item.listingId;
    const checked = invSelected.has(item.listingId) ? 'checked' : '';
    const cost = item.costBasis;

    const suggestedCell = item.suggestedPrice != null
      ? `<input class="inv-price-input" type="number" step="0.01" min="0.01"
                value="${Number(item.suggestedPrice).toFixed(2)}" data-id="${esc(item.listingId)}"
                title="Edit to override the suggested price" />
         <div class="inv-change ${item.suggestedChangePercent < 0 ? 'down' : 'up'}">${
           item.suggestedChangePercent > 0 ? '+' : ''}${Number(item.suggestedChangePercent ?? 0).toFixed(1)}%${
           item.requiresReview ? ' · review' : ''}${item.floorLimited ? ' · at floor' : ''}</div>`
      : '<span class="inv-dash">—</span>';

    return `
      <tr class="${item.verdict === 'dead_capital' ? 'inv-row-dead' : ''}">
        <td class="inv-col-check">${selectable
          ? `<input class="inv-check" type="checkbox" data-id="${esc(item.listingId)}" ${checked} />`
          : ''}</td>
        <td class="inv-cell-title">
          <div class="inv-title-row">
            ${item.imageUrl ? `<img class="inv-thumb" src="${esc(item.imageUrl)}" alt="" loading="lazy" />` : ''}
            <div>
              ${item.url
                ? `<a href="${esc(item.url)}" target="_blank" rel="noopener">${esc(item.title)}</a>`
                : esc(item.title)}
              <div class="inv-sub">${esc(item.sku || item.listingId)}${item.quantity > 1 ? ` · qty ${item.quantity}` : ''}${
                item.watchCount ? ` · ${item.watchCount} 👁` : ''}${
                item.pricedAs && item.pricedAs !== item.title ? ` · priced as "${esc(item.pricedAs)}"` : ''}</div>
            </div>
          </div>
        </td>
        <td class="num">${item.daysListed == null ? '<span class="inv-dash" title="eBay reported no start date">—</span>' : item.daysListed + 'd'}</td>
        <td class="num">${moneyExact(item.listPrice)}</td>
        <td class="num">${item.marketPrice != null
            ? `${moneyExact(item.marketPrice)}<div class="inv-sub">${item.soldCompCount + item.terapeakCompCount} comps${
                item.marketComparable === false ? ' · per unit' : ''}</div>`
            : '<span class="inv-dash">—</span>'}</td>
        <!-- A gap computed from a comparison that failed is not a fact about the listing, so it is
             struck through rather than shown as a finding. -->
        <td class="num ${item.marketComparable === false ? 'inv-gap-void' : gapCls}">${
          gap == null ? '<span class="inv-dash">—</span>'
          : item.marketComparable === false
            ? `<span title="Not comparable — see the verdict column">n/a</span>`
            : (gap > 0 ? '+' : '') + gap.toFixed(0) + '%'}</td>
        <td class="num">
          <input class="inv-cost-input" type="number" step="0.01" min="0" placeholder="cost"
                 value="${cost != null ? Number(cost).toFixed(2) : ''}"
                 data-id="${esc(item.listingId)}" data-sku="${esc(item.sku || '')}"
                 title="What you paid for this unit, all in. Sets the break-even floor." />
        </td>
        <!-- Break-even alone answers "am I losing money"; the floor answers "is this worth doing".
             A Best Offer arrives against the second number, so it belongs next to the first. -->
        <td class="num">${item.breakEvenPrice != null
            ? `${moneyExact(item.breakEvenPrice)}${item.minimumOfferPrice != null
                 && item.minimumOfferPrice > item.breakEvenPrice
                 ? `<div class="inv-sub inv-floor" title="Lowest offer worth accepting — ${
                      esc(invFloorBasis(item))}">floor ${moneyExact(item.minimumOfferPrice)}</div>`
                 : ''}`
            : '<span class="inv-dash">—</span>'}</td>
        <td class="num inv-cell-suggested">${suggestedCell}</td>
        <td class="inv-cell-verdict">
          <span class="inv-verdict ${v.cls}">${v.label}</span>
          <div class="inv-note">${esc(item.verdictNote)}</div>
          ${(item.signals || []).length ? `<div class="inv-signals">${item.signals.map(s => esc(s)).join(' ')}</div>` : ''}
        </td>
      </tr>`;
  }

  function invFloorBasis(item) {
    if (item.minimumOfferBasis === 'margin_target') return 'your minimum margin, from Fees & Costs';
    if (item.minimumOfferBasis === 'profit_target')
      return `keeps ${moneyExact(item.netProfitAtMinimumOffer ?? 0)} of profit, from Fees & Costs`;
    return 'break-even — set a minimum profit in Fees & Costs to raise it';
  }

  function invItemById(listingId) {
    return (invScan?.items || []).find(i => i.listingId === listingId) || null;
  }

  function onRepriceCheck(e) {
    const id = e.target.dataset.id;
    if (e.target.checked) invSelected.add(id); else invSelected.delete(id);
    refreshSelectionNote();
  }

  function toggleSelectAllReprice(e) {
    const filter = $('inv-verdict-filter')?.value || 'all';
    const on = e.target.checked;
    // Only what is actually on screen — a select-all that silently ticks rows behind a filter is
    // how someone reprices a listing they never looked at.
    (invScan?.items || [])
      .filter(i => invFilterMatches(i, filter) && i.hasRecommendation && !i.requiresReview && i.listingId)
      .forEach(i => { if (on) invSelected.add(i.listingId); else invSelected.delete(i.listingId); });
    document.querySelectorAll('.inv-check').forEach(cb => { cb.checked = invSelected.has(cb.dataset.id); });
    refreshSelectionNote();
  }

  function refreshSelectionNote() {
    const picked = [...invSelected].map(invItemById).filter(Boolean);
    const note = $('inv-selection-note');
    if (note) {
      note.textContent = picked.length === 0
        ? 'Nothing selected.'
        : `${picked.length} listing${picked.length === 1 ? '' : 's'} selected · average change ${
            (picked.reduce((sum, i) => sum + (i.suggestedChangePercent || 0), 0) / picked.length).toFixed(1)}%`;
    }
    const disabled = picked.length === 0;
    if ($('inv-preview-btn')) $('inv-preview-btn').disabled = disabled;
    if ($('inv-apply-btn'))   $('inv-apply-btn').disabled   = disabled;
  }

  // An overridden price is the seller's call, so it replaces the suggestion on the row it came
  // from rather than being validated away here — the server re-checks it against the break-even
  // either way, which is the check that actually matters.
  function onSuggestedOverride(e) {
    const item = invItemById(e.target.dataset.id);
    if (!item) return;
    const value = parseFloat(e.target.value);
    if (!isFinite(value) || value <= 0) {
      e.target.value = Number(item.suggestedPrice ?? item.listPrice).toFixed(2);
      return;
    }
    item.suggestedPrice = value;
    item.suggestedChangePercent = item.listPrice > 0
      ? Math.round((value - item.listPrice) / item.listPrice * 1000) / 10 : 0;
    const changeEl = e.target.parentElement?.querySelector('.inv-change');
    if (changeEl) {
      changeEl.textContent = `${item.suggestedChangePercent > 0 ? '+' : ''}${item.suggestedChangePercent.toFixed(1)}% · edited`;
      changeEl.className = `inv-change ${item.suggestedChangePercent < 0 ? 'down' : 'up'}`;
    }
    refreshSelectionNote();
  }

  async function onCostBasisEdit(e) {
    const listingId = e.target.dataset.id;
    const sku = e.target.dataset.sku || '';
    const raw = e.target.value.trim();

    try {
      if (raw === '') {
        await fetch(`/api/inventory/cost-basis?listingId=${encodeURIComponent(listingId)}&sku=${encodeURIComponent(sku)}`,
          { method: 'DELETE' });
        invCosts.delete(listingId);
      } else {
        const unitCost = parseFloat(raw);
        if (!isFinite(unitCost) || unitCost < 0) return;
        const entry = { listingId, sku, unitCost, inboundShipping: 0, note: '' };
        const res = await fetch('/api/inventory/cost-basis', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify([entry]),
        });
        if (!res.ok) throw new Error(await res.text());
        invCosts.set(listingId, entry);
      }
      // A cost basis changes the break-even, which changes the floor, which can change the
      // recommendation — so the row is re-derived by the server rather than patched here.
      setInvStatus('Cost saved — re-scan to recalculate break-even and suggested prices.');
    } catch (err) {
      setInvStatus(`Could not save that cost: ${err.message || err}`);
    }
  }

  function openRepriceConfirm() {
    const picked = [...invSelected].map(invItemById).filter(Boolean);
    if (picked.length === 0) return;

    $('inv-confirm-list').innerHTML = picked.map(i => `
      <div class="inv-confirm-row">
        <span class="inv-confirm-title">${esc(i.title)}</span>
        <span class="inv-confirm-prices">${moneyExact(i.listPrice)} → <strong>${moneyExact(i.suggestedPrice)}</strong></span>
      </div>`).join('');
    const loss = $('inv-allow-loss');
    if (loss) loss.checked = false;
    $('inv-confirm-overlay')?.classList.remove('hidden');
  }

  async function submitReprice(dryRun) {
    const picked = [...invSelected].map(invItemById).filter(Boolean);
    if (picked.length === 0) return;

    const body = {
      items: picked.map(i => ({
        listingId: i.listingId, sku: i.sku, title: i.title,
        newPrice: i.suggestedPrice, currentPrice: i.listPrice,
        quantity: i.quantity, breakEvenPrice: i.breakEvenPrice,
      })),
      dryRun,
      confirmed: !dryRun,
      allowBelowBreakEven: !dryRun && !!$('inv-allow-loss')?.checked,
    };

    setInvStatus(dryRun ? 'Previewing…' : 'Repricing on eBay…');
    try {
      const res  = await fetch('/api/inventory/reprice', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'Repricing failed.');
      renderRepriceOutcome(data);
      if (!dryRun && data.applied > 0) {
        addActivity(`${data.applied} listing${data.applied === 1 ? '' : 's'} repriced on eBay`,
          'Prices updated from the Inventory Health scan.');
        // The scan is now stale for those rows by definition, so re-run it rather than leaving
        // the table showing prices that are no longer live.
        await runInventoryScan();
      }
    } catch (err) {
      setInvStatus(`Repricing failed: ${err.message || err}`);
    }
  }

  function renderRepriceOutcome(data) {
    const parts = [];
    if (data.dryRun) parts.push(`Preview only — nothing sent to eBay. ${data.items.length} change${data.items.length === 1 ? '' : 's'} ready.`);
    else parts.push(`${data.applied} applied`);
    if (data.skipped) parts.push(`${data.skipped} skipped`);
    if (data.failed)  parts.push(`${data.failed} failed`);

    const problems = (data.items || []).filter(i => i.status === 'skipped' || i.status === 'failed');
    setInvStatus(parts.join(' · ') + (problems.length
      ? ' — ' + problems.map(p => `${p.title}: ${p.message}`).join('; ')
      : ''));
  }

  function setInvStatus(text) {
    const el = $('inv-status');
    if (el) el.textContent = text || '';
  }

  // ── Offers to Watchers ───────────────────────────────────────────────────
  // The warmest audience a seller gets for free: people who already found the item and hesitated.
  // eBay's Send Offer to Interested Buyers puts a private, time-limited discount in front of
  // exactly them, without moving the public price — so an offer nobody accepts costs nothing.
  // See WatcherOfferAdvisor.cs for how deep each offer goes and what stops it.
  let woScan     = null;   // last WatcherOfferResult, kept so filtering never re-scans
  let woSelected = new Set();

  function showOffersSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('offers-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'offers'));
  }

  function closeOffersSection() {
    $('offers-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindWatcherOffers() {
    on('wo-scan-btn', 'click', runOfferScan);
    on('wo-close', 'click', closeOffersSection);
    on('wo-home', 'click', closeOffersSection);
    on('inv-to-offers', 'click', () => { location.hash = 'offers'; });
    // Filtering is a pure view over the scan already in hand — it must never re-run eBay calls.
    on('wo-filter', 'change', renderOfferRows);
    on('wo-select-all', 'change', toggleSelectAllOffers);
    on('wo-preview-btn', 'click', () => submitOffers(true));
    on('wo-send-btn', 'click', openOfferConfirm);
    on('wo-confirm-cancel', 'click', () => $('wo-confirm-overlay')?.classList.add('hidden'));
    on('wo-confirm-go', 'click', () => {
      $('wo-confirm-overlay')?.classList.add('hidden');
      submitOffers(false);
    });
    on('wo-message', 'input', updateOfferMessageCount);
  }

  async function runOfferScan() {
    const btn = $('wo-scan-btn');
    const minWatchers = $('wo-min-watchers')?.value || '1';
    const maxItems    = $('wo-max-items')?.value || '120';
    const minProfit   = Math.max(0, parseFloat($('wo-min-profit')?.value || '0') || 0);

    if (btn) { btn.disabled = true; btn.textContent = 'Scanning…'; }
    setWoStatus('Reading your live listings and their watchers…');
    $('wo-results').innerHTML = '<p class="opportunity-empty">Counting watchers and pricing each listing against sold comps — this takes a moment on a large inventory.</p>';

    try {
      const res  = await fetch(`/api/offers/watchers?minWatchers=${minWatchers}&maxItems=${maxItems}&minProfit=${minProfit}`);
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'The scan failed.');
      woScan = data;
      woSelected.clear();
      renderOfferScan();
    } catch (err) {
      woScan = null;
      $('wo-summary')?.classList.add('hidden');
      $('wo-bulk-bar')?.classList.add('hidden');
      $('wo-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The scan failed.')}</p>`;
      setWoStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '👁 Find My Watchers'; }
    }
  }

  function renderOfferScan() {
    const warn = $('wo-warning');

    if (woScan.status === 'ebay_unavailable') {
      $('wo-summary')?.classList.add('hidden');
      $('wo-bulk-bar')?.classList.add('hidden');
      warn?.classList.add('hidden');
      $('wo-results').innerHTML =
        '<p class="opportunity-empty">Your eBay account isn\'t connected, or the token has expired. Reconnect it in Settings, then scan again.</p>';
      setWoStatus('');
      return;
    }

    // A missing permission is not a failure of the scan: the watcher counts and the offers below
    // are all real, and only sending is blocked — so the board still renders behind the banner.
    const notes = [];
    if (woScan.needsReconnect) notes.push(woScan.eligibilityNote || 'Reconnect eBay to send offers.');
    else if (woScan.eligibilityNote) notes.push(woScan.eligibilityNote);
    if (woScan.dataWarning) notes.push(woScan.dataWarning);

    if (notes.length) {
      warn.textContent = notes.join(' ');
      warn.classList.remove('hidden');
    } else {
      warn?.classList.add('hidden');
    }

    renderOfferSummary(woScan.summary || {});
    renderOfferRows();

    const s = woScan.summary || {};
    setWoStatus(
      `${woScan.itemsAnalyzed} watched listing${woScan.itemsAnalyzed === 1 ? '' : 's'} of ${woScan.activeListings} active · ` +
      `${s.totalWatchers} watcher${s.totalWatchers === 1 ? '' : 's'} in total` +
      (woScan.eligibilityChecked ? ' · eligibility confirmed with eBay' : ' · eligibility not confirmed'));
  }

  // Leads with the audience, not the listing count: "9 listings" is a statistic, "47 people are
  // watching your listings right now" is a reason to click send.
  function renderOfferSummary(s) {
    const el = $('wo-summary');
    if (!el) return;

    const tiles = [
      { label: 'People watching', value: String(s.totalWatchers || 0),
        sub: `${s.listingsWithWatchers || 0} listing${s.listingsWithWatchers === 1 ? '' : 's'} with an audience`,
        tone: s.totalWatchers > 0 ? 'good' : '' },
      { label: 'Offers ready', value: String(s.readyToSend || 0),
        sub: s.readyToSend ? `reaching ${s.watchersReachable} watcher${s.watchersReachable === 1 ? '' : 's'}` : 'Nothing to send yet',
        tone: s.readyToSend > 0 ? 'good' : '' },
      { label: 'Average discount', value: `${Number(s.averageDiscountPercent || 0).toFixed(1)}%`,
        sub: 'Sized per listing, never below your floor', tone: '' },
      { label: 'If one of each is taken', value: money(s.revenueIfOneEachAccepts),
        sub: s.netIfOneEachAccepts ? `${money(s.netIfOneEachAccepts)} net after fees` : 'Record cost basis to see net profit',
        tone: s.revenueIfOneEachAccepts > 0 ? 'good' : '' },
      { label: 'Margin you\'d give up', value: money(s.marginGivenUpIfAllAccept),
        sub: 'Only if every offer is accepted — otherwise nothing', tone: '' },
      { label: 'Held back', value: String((s.blockedByFloor || 0) + (s.notEligible || 0)),
        sub: `${s.blockedByFloor || 0} under your floor · ${s.notEligible || 0} not eligible on eBay`,
        tone: (s.blockedByFloor || 0) > 0 ? 'warn' : '' },
    ];

    el.innerHTML = tiles.map(t => `
      <div class="inv-tile ${t.tone ? 'inv-tile-' + t.tone : ''}">
        <div class="inv-tile-label">${esc(t.label)}</div>
        <div class="inv-tile-value">${esc(t.value)}</div>
        <div class="inv-tile-sub">${esc(t.sub)}</div>
      </div>`).join('');
    el.classList.remove('hidden');
  }

  const WO_VERDICTS = {
    ready:        { label: '✉️ Ready to send', cls: 'wo-v-ready' },
    no_room:      { label: '🛑 Under my floor', cls: 'inv-v-underwater' },
    not_eligible: { label: '⛔ Not eligible',   cls: 'inv-v-nodata' },
    no_watchers:  { label: '👤 No watchers',   cls: 'inv-v-nodata' },
    not_ready:    { label: '? No price',       cls: 'inv-v-nodata' },
  };

  function woFilterMatches(item, filter) {
    switch (filter) {
      case 'ready':        return item.canSend;
      case 'no_room':      return item.verdict === 'no_room';
      case 'not_eligible': return item.verdict === 'not_eligible';
      default:             return true;
    }
  }

  function renderOfferRows() {
    if (!woScan) return;
    const filter = $('wo-filter')?.value || 'ready';
    const rows = (woScan.items || []).filter(i => woFilterMatches(i, filter));
    const el = $('wo-results');

    if (rows.length === 0) {
      const total = (woScan.items || []).length;
      el.innerHTML = `<p class="opportunity-empty">${
        total === 0
          ? 'No listings with watchers came back from eBay. Watchers build up as a listing gets views — check again in a few days, or lower the watcher filter.'
          : 'Nothing matches that filter. Switch to "Everything" to see why each listing was held back.'}</p>`;
      $('wo-bulk-bar')?.classList.add('hidden');
      return;
    }

    el.innerHTML = `
      <table class="inv-table">
        <thead>
          <tr>
            <th class="inv-col-check"></th>
            <th>Listing</th>
            <th class="num">Watching</th>
            <th class="num">Age</th>
            <th class="num">Your price</th>
            <th class="num">Market</th>
            <th class="num">Offer %</th>
            <th class="num">They pay</th>
            <th class="num">Your net</th>
            <th>Verdict</th>
          </tr>
        </thead>
        <tbody>${rows.map(woRowHtml).join('')}</tbody>
      </table>`;

    el.querySelectorAll('.wo-check').forEach(cb => cb.addEventListener('change', onOfferCheck));
    el.querySelectorAll('.wo-discount-input').forEach(inp => {
      inp.addEventListener('change', onOfferDiscountEdit);
      inp.addEventListener('keydown', e => { if (e.key === 'Enter') inp.blur(); });
    });

    $('wo-bulk-bar')?.classList.remove('hidden');
    refreshOfferSelectionNote();
  }

  function woRowHtml(item) {
    const v = WO_VERDICTS[item.verdict] || WO_VERDICTS.no_watchers;
    const checked = woSelected.has(item.listingId) ? 'checked' : '';

    const discountCell = item.discountPercent != null
      ? `<input class="wo-discount-input" type="number" step="1" min="5" max="25"
                value="${Number(item.discountPercent)}" data-id="${esc(item.listingId)}"
                title="Edit to override the suggested discount. eBay's minimum is 5%." />
         <div class="inv-change down">${item.floorLimited ? 'at floor' : 'suggested'}</div>`
      : '<span class="inv-dash">—</span>';

    return `
      <tr class="${item.canSend ? '' : 'wo-row-muted'}">
        <td class="inv-col-check">${item.canSend
          ? `<input class="wo-check" type="checkbox" data-id="${esc(item.listingId)}" ${checked} />`
          : ''}</td>
        <td class="inv-cell-title">
          <div class="inv-title-row">
            ${item.imageUrl ? `<img class="inv-thumb" src="${esc(item.imageUrl)}" alt="" loading="lazy" />` : ''}
            <div>
              ${item.url
                ? `<a href="${esc(item.url)}" target="_blank" rel="noopener">${esc(item.title)}</a>`
                : esc(item.title)}
              <div class="inv-sub">${esc(item.sku || item.listingId)}${item.quantity > 1 ? ` · qty ${item.quantity}` : ''}${
                item.costBasis != null ? ` · paid ${moneyExact(item.costBasis)}` : ' · no cost recorded'}</div>
            </div>
          </div>
        </td>
        <td class="num"><span class="wo-watchers ${item.watchCount >= 10 ? 'hot' : ''}">${item.watchCount} 👁</span></td>
        <td class="num">${item.daysListed == null ? '<span class="inv-dash" title="eBay reported no start date">—</span>' : item.daysListed + 'd'}</td>
        <td class="num">${moneyExact(item.listPrice)}</td>
        <td class="num">${item.marketPrice != null && item.marketComparable !== false
            ? `${moneyExact(item.marketPrice)}<div class="inv-sub">${item.soldCompCount + item.terapeakCompCount} comps</div>`
            : '<span class="inv-dash">—</span>'}</td>
        <td class="num inv-cell-suggested">${discountCell}</td>
        <td class="num">${item.offerPrice != null
            ? `<strong>${moneyExact(item.offerPrice)}</strong><div class="inv-sub">${item.marginGivenUp != null ? `−${moneyExact(item.marginGivenUp)}` : ''}</div>`
            : '<span class="inv-dash">—</span>'}</td>
        <td class="num">${item.netProfitAtOffer != null
            ? `<span class="${item.netProfitAtOffer > 0 ? 'inv-gap-good' : 'inv-gap-bad'}">${moneyExact(item.netProfitAtOffer)}</span>${
                item.netProfitAtListPrice != null ? `<div class="inv-sub">${moneyExact(item.netProfitAtListPrice)} at full price</div>` : ''}`
            : '<span class="inv-dash" title="Record what you paid in Inventory Health to see net profit">—</span>'}</td>
        <td class="inv-cell-verdict">
          <span class="inv-verdict ${v.cls}">${v.label}</span>
          <div class="inv-note">${esc(item.verdictNote)}</div>
          ${(item.signals || []).length ? `<div class="inv-signals">${item.signals.map(s => esc(s)).join(' ')}</div>` : ''}
        </td>
      </tr>`;
  }

  function woItemById(listingId) {
    return (woScan?.items || []).find(i => i.listingId === listingId) || null;
  }

  function onOfferCheck(e) {
    const id = e.target.dataset.id;
    if (e.target.checked) woSelected.add(id); else woSelected.delete(id);
    refreshOfferSelectionNote();
  }

  function toggleSelectAllOffers(e) {
    const filter = $('wo-filter')?.value || 'ready';
    const on = e.target.checked;
    // Only what is on screen — a select-all that ticks rows hidden behind a filter is how someone
    // sends a discount on a listing they never looked at.
    (woScan?.items || [])
      .filter(i => woFilterMatches(i, filter) && i.canSend)
      .forEach(i => { if (on) woSelected.add(i.listingId); else woSelected.delete(i.listingId); });
    document.querySelectorAll('.wo-check').forEach(cb => { cb.checked = woSelected.has(cb.dataset.id); });
    refreshOfferSelectionNote();
  }

  function refreshOfferSelectionNote() {
    const picked = [...woSelected].map(woItemById).filter(Boolean);
    const note = $('wo-selection-note');
    if (note) {
      if (picked.length === 0) note.textContent = 'Nothing selected.';
      else {
        const watchers = picked.reduce((sum, i) => sum + (i.watchCount || 0), 0);
        const avg = picked.reduce((sum, i) => sum + (i.discountPercent || 0), 0) / picked.length;
        note.textContent = `${picked.length} offer${picked.length === 1 ? '' : 's'} selected · ` +
          `${watchers} watcher${watchers === 1 ? '' : 's'} reached · average ${avg.toFixed(1)}% off`;
      }
    }
    const disabled = picked.length === 0;
    if ($('wo-preview-btn')) $('wo-preview-btn').disabled = disabled;
    if ($('wo-send-btn'))    $('wo-send-btn').disabled    = disabled;
  }

  // An edited discount is the seller's call. It is clamped to what eBay will carry so a typo can't
  // produce a rejected send, and the server re-checks it against the profit floor either way.
  function onOfferDiscountEdit(e) {
    const item = woItemById(e.target.dataset.id);
    if (!item) return;
    const raw = parseInt(e.target.value, 10);
    const value = Math.min(25, Math.max(5, isFinite(raw) ? raw : (item.discountPercent || 5)));
    e.target.value = String(value);
    item.discountPercent = value;
    item.offerPrice = Math.round(item.listPrice * (100 - value)) / 100;
    item.marginGivenUp = Math.round((item.listPrice - item.offerPrice) * 100) / 100;
    const label = e.target.parentElement?.querySelector('.inv-change');
    if (label) label.textContent = 'edited';
    refreshOfferSelectionNote();
  }

  function updateOfferMessageCount() {
    const box = $('wo-message');
    const counter = $('wo-message-count');
    if (box && counter) counter.textContent = `${box.value.length}/250`;
  }

  function openOfferConfirm() {
    const picked = [...woSelected].map(woItemById).filter(Boolean);
    if (picked.length === 0) return;

    // The scale of what is about to go out, above the list — the list scrolls, and "31 offers to
    // 1,362 people" is the number the seller is actually deciding on.
    const watchers = picked.reduce((sum, i) => sum + (i.watchCount || 0), 0);
    const givenUp  = picked.reduce((sum, i) => sum + (i.marginGivenUp || 0), 0);
    $('wo-confirm-total').textContent =
      `${picked.length} offer${picked.length === 1 ? '' : 's'} to ${watchers} watcher${watchers === 1 ? '' : 's'} · ` +
      `${moneyExact(givenUp)} of margin if every one is accepted`;

    $('wo-confirm-list').innerHTML = picked.map(i => `
      <div class="inv-confirm-row">
        <span class="inv-confirm-title">${esc(i.title)}</span>
        <span class="inv-confirm-prices">${i.watchCount} 👁 &nbsp; ${moneyExact(i.listPrice)} → <strong>${moneyExact(i.offerPrice)}</strong> (−${i.discountPercent}%)</span>
      </div>`).join('');

    const box = $('wo-message');
    if (box && !box.value) box.value = woScan?.defaultMessage || '';
    updateOfferMessageCount();
    const loss = $('wo-allow-loss');
    if (loss) loss.checked = false;
    $('wo-confirm-overlay')?.classList.remove('hidden');
  }

  async function submitOffers(dryRun) {
    const picked = [...woSelected].map(woItemById).filter(Boolean);
    if (picked.length === 0) return;

    const body = {
      items: picked.map(i => ({
        listingId: i.listingId, sku: i.sku, title: i.title,
        listPrice: i.listPrice, discountPercent: i.discountPercent,
        watchCount: i.watchCount, quantity: 1,
      })),
      message: $('wo-message')?.value || '',
      allowCounterOffer: $('wo-allow-counter')?.checked !== false,
      minNetProfit: Math.max(0, parseFloat($('wo-min-profit')?.value || '0') || 0),
      dryRun,
      confirmed: !dryRun,
      allowBelowFloor: !dryRun && !!$('wo-allow-loss')?.checked,
    };

    setWoStatus(dryRun ? 'Previewing…' : 'Sending offers on eBay…');
    try {
      const res  = await fetch('/api/offers/send', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'Sending failed.');
      renderOfferOutcome(data);
      if (!dryRun && data.sent > 0) {
        addActivity(`${data.sent} offer${data.sent === 1 ? '' : 's'} sent to ${data.watchersReached} watcher${data.watchersReached === 1 ? '' : 's'}`,
          'Private discounts sent from Offers to Watchers — your public prices did not change.');
        // Those listings can't carry another offer for a while, so the board is stale for them.
        await runOfferScan();
      }
    } catch (err) {
      setWoStatus(`Sending failed: ${err.message || err}`);
    }
  }

  function renderOfferOutcome(data) {
    const parts = [];
    if (data.dryRun) parts.push(`Preview only — nothing sent. ${data.items.length} offer${data.items.length === 1 ? '' : 's'} ready.`);
    else parts.push(`${data.sent} sent to ${data.watchersReached} watcher${data.watchersReached === 1 ? '' : 's'}`);
    if (data.skipped) parts.push(`${data.skipped} skipped`);
    if (data.failed)  parts.push(`${data.failed} failed`);

    const problems = (data.items || []).filter(i => i.status === 'skipped' || i.status === 'failed');
    setWoStatus(parts.join(' · ') + (problems.length
      ? ' — ' + problems.map(p => `${p.title}: ${p.message}`).join('; ')
      : ''));
  }

  function setWoStatus(text) {
    const el = $('wo-status');
    if (el) el.textContent = text || '';
  }

  // ── Aging-Inventory Rescue ───────────────────────────────────────────────
  // Inventory Health says what a listing should cost today and caps the cut at one revision, which
  // is right for a repricer and useless for dead stock: it depends on the seller coming back, and
  // the entire failure mode of aging inventory is that nobody comes back. This board decides the
  // whole ladder up front — dated drops, floors included — and pairs what will not move on its own
  // with something that already sells. See AgingInventoryRescuer.cs for the money rules.
  let rescueScan     = null;   // last RescueResult, kept so filtering never re-scans
  let rescueSelected = new Set();

  function showRescueSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('rescue-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'rescue'));
  }

  function closeRescueSection() {
    $('rescue-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindRescue() {
    on('rsc-scan-btn', 'click', runRescueScan);
    on('rsc-close', 'click', closeRescueSection);
    on('rsc-home', 'click', closeRescueSection);
    on('inv-to-rescue', 'click', () => { location.hash = 'rescue'; });
    // Filtering is a pure view over the scan already in hand — it must never re-run eBay calls.
    on('rsc-filter', 'change', renderRescuePlans);
    on('rsc-select-all', 'change', toggleSelectAllRescue);
    on('rsc-preview-btn', 'click', () => submitRescueDrops(true));
    on('rsc-apply-btn', 'click', openRescueConfirm);
    on('rsc-confirm-cancel', 'click', () => $('rsc-confirm-overlay')?.classList.add('hidden'));
    on('rsc-confirm-go', 'click', () => {
      $('rsc-confirm-overlay')?.classList.add('hidden');
      submitRescueDrops(false);
    });
  }

  async function runRescueScan() {
    const btn       = $('rsc-scan-btn');
    const staleDays = $('rsc-stale-days')?.value || '90';
    const maxItems  = $('rsc-max-items')?.value || '120';

    if (btn) { btn.disabled = true; btn.textContent = 'Scanning…'; }
    setRescueStatus('Reading your live listings and pricing each one against sold comps…');
    $('rsc-results').innerHTML = '<p class="opportunity-empty">Finding what has stopped moving, and what still sells — this takes a moment on a large inventory.</p>';

    try {
      const res  = await fetch(`/api/inventory/rescue?staleAfterDays=${staleDays}&maxItems=${maxItems}`);
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'The scan failed.');
      rescueScan = data;
      rescueSelected.clear();
      renderRescueScan();
    } catch (err) {
      rescueScan = null;
      $('rsc-summary')?.classList.add('hidden');
      $('rsc-bulk-bar')?.classList.add('hidden');
      $('rsc-bundles')?.classList.add('hidden');
      $('rsc-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The scan failed.')}</p>`;
      setRescueStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '🛟 Rescue My Aging Stock'; }
    }
  }

  function renderRescueScan() {
    const warn = $('rsc-warning');

    if (rescueScan.status === 'ebay_unavailable') {
      $('rsc-summary')?.classList.add('hidden');
      $('rsc-bulk-bar')?.classList.add('hidden');
      $('rsc-bundles')?.classList.add('hidden');
      warn?.classList.add('hidden');
      $('rsc-results').innerHTML =
        '<p class="opportunity-empty">Your eBay account isn\'t connected, or the token has expired. Reconnect it in Settings, then scan again.</p>';
      setRescueStatus('');
      return;
    }

    if (rescueScan.dataWarning) {
      warn.textContent = rescueScan.dataWarning;
      warn.classList.remove('hidden');
    } else {
      warn?.classList.add('hidden');
    }

    renderRescueSummary(rescueScan.summary || {});
    renderRescuePlans();
    renderRescueBundles();

    const s = rescueScan.summary || {};
    setRescueStatus(
      `${rescueScan.itemsAnalyzed} of ${rescueScan.activeListings} active listings checked · ` +
      `${s.staleListings || 0} sitting ${rescueScan.staleAfterDays}+ days` +
      (s.medianDaysListed ? ` · median ${s.medianDaysListed} days old` : ''));
  }

  // Leads with the money that is stuck, not with a listing count: "6 stale listings" is a
  // statistic, "$4,200 of your money has been sitting on a shelf for four months" is a reason to act.
  function renderRescueSummary(s) {
    const el = $('rsc-summary');
    if (!el) return;

    const tiles = [
      { label: 'Money stuck on the shelf', value: money(s.trappedCapital),
        sub: `${s.staleListings || 0} listing${s.staleListings === 1 ? '' : 's'} past ${rescueScan.staleAfterDays} days`,
        tone: s.trappedCapital > 0 ? 'warn' : '' },
      { label: 'Oldest one', value: s.oldestDaysListed ? `${s.oldestDaysListed} days` : '—',
        sub: s.medianDaysListed ? `half of them are ${s.medianDaysListed}+ days old` : 'Nothing stale yet',
        tone: s.oldestDaysListed >= 180 ? 'warn' : '' },
      { label: 'Drops to make today', value: String(s.stepsDueNow || 0),
        sub: s.plansReady ? `${s.plansReady} plan${s.plansReady === 1 ? '' : 's'} ready to run` : 'No plan needed',
        tone: s.stepsDueNow > 0 ? 'good' : '' },
      { label: 'Cash back if the plans clear', value: money(s.cashIfEveryPlanClears),
        sub: s.profitGivenUpIfEveryPlanClears
          ? `after giving up ${money(s.profitGivenUpIfEveryPlanClears)} of asking-price profit`
          : 'Record cost basis to see your take-home',
        tone: s.cashIfEveryPlanClears > 0 ? 'good' : '' },
      { label: 'Bundles found', value: String(s.bundlesFound || 0),
        sub: s.bundlesFound
          ? `freeing ${money(s.capitalFreedByBundles)} of stuck stock`
          : 'No fast mover to pair with yet',
        tone: s.bundlesFound > 0 ? 'good' : '' },
      { label: 'Bundles add', value: s.incrementalNetFromBundles
          ? money(s.incrementalNetFromBundles) : money(s.addedRevenueFromBundles),
        sub: s.incrementalNetFromBundles
          ? 'net, over selling the fast item alone'
          : 'revenue, over selling the fast item alone',
        tone: (s.incrementalNetFromBundles || s.addedRevenueFromBundles) > 0 ? 'good' : '' },
    ];

    el.innerHTML = tiles.map(t => `
      <div class="inv-tile ${t.tone ? 'inv-tile-' + t.tone : ''}">
        <div class="inv-tile-label">${esc(t.label)}</div>
        <div class="inv-tile-value">${esc(t.value)}</div>
        <div class="inv-tile-sub">${esc(t.sub)}</div>
      </div>`).join('');
    el.classList.remove('hidden');
  }

  const RSC_URGENCY = {
    critical: { label: '🔴 Dead capital', cls: 'inv-v-dead' },
    high:     { label: '🟠 Going stale',  cls: 'inv-v-stale' },
    watch:    { label: '🟡 Watch',        cls: 'inv-v-over' },
  };

  function rescueFilterMatches(plan, filter) {
    switch (filter) {
      case 'ready':    return plan.hasPlan;
      case 'critical': return plan.urgency === 'critical';
      case 'no_plan':  return !plan.hasPlan;
      default:         return true;
    }
  }

  function renderRescuePlans() {
    if (!rescueScan) return;
    const filter = $('rsc-filter')?.value || 'ready';
    const plans  = (rescueScan.plans || []).filter(p => rescueFilterMatches(p, filter));
    const el     = $('rsc-results');

    if (plans.length === 0) {
      const total = (rescueScan.plans || []).length;
      el.innerHTML = `<p class="opportunity-empty">${
        total === 0
          ? `Nothing has been sitting longer than ${rescueScan.staleAfterDays} days — your money is turning over. Lower the age filter to look further back.`
          : 'Nothing matches that filter. Switch to "Everything" to see the listings no plan could be built for.'}</p>`;
      $('rsc-bulk-bar')?.classList.add('hidden');
      return;
    }

    el.innerHTML = plans.map(renderRescueCard).join('');

    plans.forEach(p => {
      const box = $(`rsc-check-${p.listingId}`);
      if (box) box.addEventListener('change', () => {
        if (box.checked) rescueSelected.add(p.listingId); else rescueSelected.delete(p.listingId);
        updateRescueSelection();
      });
    });

    $('rsc-bulk-bar')?.classList.remove('hidden');
    updateRescueSelection();
  }

  function renderRescueCard(p) {
    const urgency = RSC_URGENCY[p.urgency] || RSC_URGENCY.watch;
    const age     = p.daysListed == null ? 'age unknown' : `${p.daysListed} days live`;

    // The ladder itself. Dates are the point: a drop the seller has already decided on is one they
    // will actually make, and the schedule is what turns "reprice it again sometime" into a plan.
    const steps = (p.steps || []).map(s => `
      <tr class="${s.daysFromNow === 0 ? 'rsc-step-now' : ''}">
        <td>${s.daysFromNow === 0 ? '<strong>Today</strong>' : esc(shortDate(s.onUtc))}</td>
        <td class="num"><strong>${moneyExact(s.price)}</strong></td>
        <td class="num">−${Number(s.percentOffListPrice || 0).toFixed(0)}%</td>
        <td class="num">${s.netProfit == null ? '—' : moneyExact(s.netProfit)}</td>
        <td>${esc(s.note)}${s.isFloor ? ' <span class="rsc-floor-tag">floor</span>' : ''}</td>
      </tr>`).join('');

    const ladder = p.hasPlan ? `
      <table class="inv-table rsc-ladder">
        <thead>
          <tr><th>When</th><th class="num">Price</th><th class="num">Off</th><th class="num">You keep</th><th>Why</th></tr>
        </thead>
        <tbody>${steps}</tbody>
      </table>` : '';

    const signals = (p.signals || []).length
      ? `<ul class="rsc-signals">${p.signals.map(s => `<li>${esc(s)}</li>`).join('')}</ul>` : '';

    return `
      <div class="rsc-card">
        <div class="rsc-card-head">
          ${p.hasPlan
            ? `<input id="rsc-check-${esc(p.listingId)}" type="checkbox" class="rsc-check" ${rescueSelected.has(p.listingId) ? 'checked' : ''} />`
            : '<span class="rsc-check-spacer"></span>'}
          <div class="rsc-card-title">
            <a href="${esc(p.url)}" target="_blank" rel="noopener">${esc(p.title)}</a>
            <div class="rsc-card-meta">
              <span class="inv-verdict ${urgency.cls}">${esc(urgency.label)}</span>
              <span>${esc(age)}</span>
              <span>${money(p.capitalTiedUp)} tied up</span>
              <span>listed at ${moneyExact(p.listPrice)}</span>
              ${p.marketPrice ? `<span>market ${moneyExact(p.marketPrice)}</span>` : ''}
            </div>
          </div>
        </div>
        <p class="rsc-headline">${esc(p.headline)}</p>
        <p class="rsc-why">${esc(p.why)}</p>
        ${ladder}
        ${signals}
      </div>`;
  }

  function toggleSelectAllRescue(e) {
    const on = !!e.target.checked;
    rescueSelected.clear();
    if (on) (rescueScan?.plans || []).filter(p => p.hasPlan).forEach(p => rescueSelected.add(p.listingId));
    document.querySelectorAll('.rsc-check').forEach(box => { box.checked = on; });
    updateRescueSelection();
  }

  function updateRescueSelection() {
    const chosen = selectedRescuePlans();
    const note   = $('rsc-selection-note');

    if (note) {
      note.textContent = chosen.length === 0
        ? 'Nothing selected.'
        : `${chosen.length} listing${chosen.length === 1 ? '' : 's'} selected — ` +
          `${money(chosen.reduce((sum, p) => sum + (p.capitalTiedUp || 0), 0))} of stuck stock.`;
    }
    const preview = $('rsc-preview-btn');
    const apply   = $('rsc-apply-btn');
    if (preview) preview.disabled = chosen.length === 0;
    if (apply)   apply.disabled   = chosen.length === 0;
  }

  function selectedRescuePlans() {
    return (rescueScan?.plans || []).filter(p => p.hasPlan && rescueSelected.has(p.listingId));
  }

  function openRescueConfirm() {
    const chosen = selectedRescuePlans();
    if (chosen.length === 0) return;

    // Only the drop due today goes to eBay. The later rungs are a plan, not a scheduled job — this
    // app has no server running while it is closed, and quietly promising to cut a price in two
    // weeks would be a promise it cannot keep.
    const total = $('rsc-confirm-total');
    if (total) total.textContent =
      `${chosen.length} price${chosen.length === 1 ? '' : 's'} change now. The later steps stay here as your plan — come back and run this again when each date comes round.`;

    const list = $('rsc-confirm-list');
    if (list) list.innerHTML = chosen.map(p => `
      <div class="inv-confirm-row">
        <span class="inv-confirm-title">${esc(p.title)}</span>
        <span class="inv-confirm-prices">${moneyExact(p.listPrice)} → <strong>${moneyExact(p.firstStep.price)}</strong></span>
      </div>`).join('');

    $('rsc-confirm-overlay')?.classList.remove('hidden');
  }

  async function submitRescueDrops(dryRun) {
    const chosen = selectedRescuePlans();
    if (chosen.length === 0) return;

    // Reuses the repricer endpoint rather than adding a second way to change a live price: every
    // brake on buyer-visible changes — preview by default, explicit confirm, server-side break-even
    // re-check — stays in exactly one place.
    const body = {
      items: chosen.map(p => ({
        listingId: p.listingId, sku: p.sku, title: p.title,
        newPrice: p.firstStep.price, currentPrice: p.listPrice,
        quantity: p.quantity || 1, breakEvenPrice: p.floorPrice,
      })),
      dryRun, confirmed: !dryRun,
      allowBelowBreakEven: false,
    };

    setRescueStatus(dryRun ? 'Previewing…' : 'Dropping prices on eBay…');
    try {
      const res  = await fetch('/api/inventory/reprice', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'The price drop failed.');

      const parts = [];
      if (data.dryRun) parts.push(`Preview only — nothing sent to eBay. ${data.items.length} drop${data.items.length === 1 ? '' : 's'} ready.`);
      else parts.push(`${data.applied} price${data.applied === 1 ? '' : 's'} dropped`);
      if (data.skipped) parts.push(`${data.skipped} skipped`);
      if (data.failed)  parts.push(`${data.failed} failed`);

      const problems = (data.items || []).filter(i => i.status === 'skipped' || i.status === 'failed');
      setRescueStatus(parts.join(' · ') + (problems.length
        ? ' — ' + problems.map(p => `${p.title}: ${p.message}`).join('; ') : ''));

      if (!dryRun && data.applied > 0) {
        addActivity(`${data.applied} aging listing${data.applied === 1 ? '' : 's'} marked down`,
          'Step one of the rescue plan applied from Aging-Inventory Rescue.');
        // Those rows are stale by definition now, so re-scan rather than leave the board showing
        // prices that are no longer live.
        await runRescueScan();
      }
    } catch (err) {
      setRescueStatus(`The price drop failed: ${err.message || err}`);
    }
  }

  // The other way out: stop discounting the thing nobody is searching for, and attach it to the
  // thing they already are.
  function renderRescueBundles() {
    const el = $('rsc-bundles');
    if (!el) return;

    const bundles = rescueScan?.bundles || [];
    if (bundles.length === 0) { el.classList.add('hidden'); el.innerHTML = ''; return; }

    el.innerHTML = `
      <h3 class="rsc-bundle-heading">Bundle it out instead</h3>
      <p class="opp-hint">
        Each pair puts a slow mover in the same box as something that already sells. The slow half
        goes out at its clearance price without you publicly cutting its standalone listing, and one
        order pays the per-order costs instead of two. List these by hand — nothing here is published for you.
      </p>
      ${bundles.map(renderBundleCard).join('')}`;
    el.classList.remove('hidden');
  }

  function renderBundleCard(b) {
    const gain = b.incrementalNet != null
      ? `<strong>${money(b.incrementalNet)} more net</strong> than selling the fast one alone`
      : `<strong>${money(b.addedRevenue)} more revenue</strong> than selling the fast one alone`;

    const signals = (b.signals || []).length
      ? `<ul class="rsc-signals">${b.signals.map(s => `<li>${esc(s)}</li>`).join('')}</ul>` : '';

    return `
      <div class="rsc-card rsc-bundle-card">
        <div class="rsc-bundle-pair">
          <div class="rsc-bundle-half rsc-bundle-slow">
            <div class="rsc-bundle-tag">🐢 Stuck ${b.slowDaysListed == null ? '' : `${b.slowDaysListed} days`}</div>
            <div class="rsc-bundle-name">${esc(b.slowTitle)}</div>
            <div class="rsc-bundle-price">${moneyExact(b.slowPrice)} → <strong>${moneyExact(b.slowContribution)}</strong> in the bundle</div>
          </div>
          <div class="rsc-bundle-plus">+</div>
          <div class="rsc-bundle-half rsc-bundle-fast">
            <div class="rsc-bundle-tag">⚡ Sells — ${esc(b.fastEvidence)}</div>
            <div class="rsc-bundle-name">${esc(b.fastTitle)}</div>
            <div class="rsc-bundle-price">${moneyExact(b.fastPrice)}</div>
          </div>
        </div>
        <div class="rsc-bundle-money">
          <span>List the pair at <strong>${moneyExact(b.bundlePrice)}</strong></span>
          <span>${Number(b.discountPercent || 0).toFixed(0)}% off the two asking prices</span>
          <span>${gain}</span>
          <span>frees ${money(b.capitalFreed)} of stuck stock</span>
        </div>
        <p class="rsc-why">${esc(b.rationale)}</p>
        <div class="rsc-bundle-title">Suggested title: <code>${esc(b.suggestedTitle)}</code></div>
        ${signals}
      </div>`;
  }

  function setRescueStatus(text) {
    const el = $('rsc-status');
    if (el) el.textContent = text || '';
  }

  // ── Spend My Budget — the sourcing basket ────────────────────────────────
  // Every other sourcing board ranks deals; this one spends the money. The candidates are whatever
  // the seller is already looking at — the local scan in hand plus anything tracked at Sourced —
  // and the server solves an exact knapsack over them. Nothing is re-priced here or on the way:
  // each candidate carries the profit figure it was given on the board it came from, so the basket
  // can never quote a number the table beside it doesn't.
  let budgetPlan = null;

  function showBudgetSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('budget-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'budget'));
    renderBudgetPool();
  }

  function closeBudgetSection() {
    $('budget-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindBudget() {
    on('bud-plan-btn', 'click', runBudgetPlan);
    on('bud-close', 'click', closeBudgetSection);
    on('bud-home', 'click', closeBudgetSection);
    // The step out of the ranked table and into an allocation, from the board itself.
    on('fb-arb-budget-btn', 'click', () => { location.hash = 'budget'; });
    // Switching the definition of "best" re-solves from the same pool. It is one cheap POST over
    // numbers already computed, not a re-scan, so it can be a plain control change.
    on('bud-objective', 'change', () => { if (budgetPlan) runBudgetPlan(); });
    ['bud-amount', 'bud-reserve'].forEach(id => on(id, 'input', renderBudgetPool));
    on('bud-include-tracked', 'change', renderBudgetPool);
  }

  // One board row → one buyable thing. Everything the allocation needs and nothing it doesn't:
  // what it costs, what it nets, how long the money is gone, and what that rests on.
  function budgetCandidateFromArbRow(row) {
    return {
      id: row.itemId || row.url || row.title,
      title: row.title,
      source: row.source,
      sourceLabel: row.sourceLabel,
      url: row.url,
      imageUrl: row.imageUrl,
      location: row.location,
      distanceMiles: row.distanceMiles,
      buyPrice: row.localAsk,
      quantity: 1,
      netProfit: row.netProfit || 0,
      maxBuyPrice: row.maxBuyPrice,
      // The drafted opening offer, so the basket can say what haggling would free up. A ceiling,
      // never counted as money in any total.
      targetOffer: row.negotiation?.openingOffer ?? null,
      daysToCash: row.daysToCash,
      compCount: (row.soldCompCount || 0) + (row.terapeakCompCount || 0),
      confidenceScore: row.confidenceScore,
      verdict: row.verdict,
      origin: 'scan',
    };
  }

  function budgetCandidates() {
    return (arbitrageData?.items || []).map(budgetCandidateFromArbRow);
  }

  // What this plan is about to be built from, said before the button is pressed — an allocation
  // over an empty pool is a confusing answer, and "you have no deals loaded" is a clear one.
  function renderBudgetPool() {
    const el = $('bud-pool');
    if (!el) return;

    const scanned = budgetCandidates().length;
    const tracked = !!$('bud-include-tracked')?.checked;
    const bits = [];

    bits.push(scanned
      ? `<strong>${scanned}</strong> deal${scanned === 1 ? '' : 's'} from your last local scan`
      : 'No local scan loaded — run one in the Opportunity Finder to allocate across fresh deals');
    if (tracked) bits.push('plus everything you\'ve tracked at <strong>Sourced</strong>');

    el.innerHTML = `Spending across: ${bits.join(', ')}.`;
  }

  async function runBudgetPlan() {
    const btn = $('bud-plan-btn');
    const budget = parseFloat($('bud-amount')?.value || '0') || 0;
    const reserve = parseFloat($('bud-reserve')?.value || '0') || 0;
    const includeTracked = !!$('bud-include-tracked')?.checked;
    const candidates = budgetCandidates();

    if (budget <= 0) return setBudgetStatus('Type what you have to spend and I\'ll work out where it goes furthest.');
    if (!candidates.length && !includeTracked)
      return setBudgetStatus('There are no deals to spend it on. Run a local scan, or tick "include deals I\'ve tracked".');

    if (btn) { btn.disabled = true; btn.textContent = 'Planning…'; }
    setBudgetStatus('Working out the best basket…');

    try {
      const res = await fetch('/api/sourcing/budget', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          budget,
          reserve,
          maxDaysToCash: parseInt($('bud-horizon')?.value || '0', 10) || 0,
          includeThin: !!$('bud-include-thin')?.checked,
          includeTrackedDeals: includeTracked,
          objective: $('bud-objective')?.value || 'profit',
          candidates,
        }),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'The budget couldn\'t be planned.');
      budgetPlan = data;
      renderBudgetPlan(data);
    } catch (err) {
      budgetPlan = null;
      $('bud-summary')?.classList.add('hidden');
      $('bud-lift')?.classList.add('hidden');
      $('bud-alternatives')?.classList.add('hidden');
      $('bud-leftout')?.classList.add('hidden');
      $('bud-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The budget couldn\'t be planned.')}</p>`;
      setBudgetStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '💵 Plan My Basket'; }
    }
  }

  function renderBudgetPlan(data) {
    setBudgetStatus('');

    // Nothing to allocate: say which of the several reasons it is, and stop. A basket of zero
    // deals rendered as a table reads like a failure; a sentence reads like an answer.
    if (data.status !== 'ok' || !data.plan?.picks?.length) {
      $('bud-summary')?.classList.add('hidden');
      $('bud-lift')?.classList.add('hidden');
      $('bud-alternatives')?.classList.add('hidden');
      $('bud-results').innerHTML =
        `<p class="opportunity-empty">${esc(data.message || 'There is no basket to build from these.')}</p>`;
      renderBudgetLeftOut(data.leftOut || []);
      return;
    }

    renderBudgetSummary(data);
    renderBudgetLift(data);
    renderBudgetBasket(data.plan);
    renderBudgetAlternatives(data);
    renderBudgetLeftOut(data.leftOut || []);
  }

  function renderBudgetSummary(data) {
    const el = $('bud-summary');
    if (!el) return;
    const plan = data.plan;

    const tiles = [
      { label: 'Buy these', value: String(plan.picks.length),
        sub: `${plan.objectiveLabel} · ${data.eligibleCount} deal${data.eligibleCount === 1 ? '' : 's'} were in play`,
        tone: 'good' },
      { label: 'Cash deployed', value: money(plan.capitalDeployed),
        sub: plan.leftover > 0 ? `${money(plan.leftover)} stays in your pocket` : 'Every dollar of it working',
        tone: '' },
      { label: 'Net profit', value: money(plan.totalNetProfit),
        sub: plan.blendedRoiPercent != null ? `${Math.round(plan.blendedRoiPercent)}% on the cash you put in` : 'after eBay fees and shipping',
        tone: 'good' },
      { label: 'All your money back', value: plan.allCashBackBy || '—',
        sub: plan.allCashBackBy
          ? `first of it around ${plan.firstCashBackBy}`
          : `${plan.unknownSpeedCount} pick${plan.unknownSpeedCount === 1 ? '' : 's'} with no measured speed — no honest date`,
        tone: plan.allCashBackBy ? 'good' : 'warn' },
      { label: 'Tied up for', value: plan.weightedDaysToCash != null ? `${plan.weightedDaysToCash} days` : '—',
        sub: plan.capitalTurnsPerYear != null ? `about ${plan.capitalTurnsPerYear} turns of this cash a year` : 'weighted by what each one costs',
        tone: '' },
      { label: 'Earning per day', value: plan.profitPerDay != null ? perDay(plan.profitPerDay) : '—',
        sub: plan.annualizedRoiPercent != null ? `${Math.round(plan.annualizedRoiPercent)}% a year at this pace` : 'while the money is out',
        tone: '' },
    ];

    el.innerHTML = tiles.map(t => `
      <div class="inv-tile ${t.tone ? 'inv-tile-' + t.tone : ''}">
        <div class="inv-tile-label">${esc(t.label)}</div>
        <div class="inv-tile-value">${esc(t.value)}</div>
        <div class="inv-tile-sub">${esc(t.sub)}</div>
      </div>`).join('');
    el.classList.remove('hidden');
  }

  // The claim the feature stands on. Rendered from the server's own comparison — including when
  // the answer is that buying down the list would have done just as well.
  function renderBudgetLift(data) {
    const el = $('bud-lift');
    if (!el) return;
    const c = data.comparison || {};
    const won = c.extraProfit > 0;

    const stretch = data.stretch
      ? `<p class="bud-lift-sub">${esc(data.stretch.note)}</p>` : '';
    const haggle = data.plan.negotiationUpside > 0
      ? `<p class="bud-lift-sub">And ${money(data.plan.negotiationUpside)} more still if all ${data.plan.negotiableCount}
         sellers took your opening offer — that's the buy side, and it costs you nothing but the asking.</p>`
      : '';

    el.className = `bud-lift ${won ? 'bud-lift-won' : 'bud-lift-tied'}`;
    el.innerHTML = `
      <div class="bud-lift-head">
        ${won
          ? `<span class="bud-lift-amount">+${money(c.extraProfit)}</span>
             <span class="bud-lift-label">more than buying straight down the list${c.extraProfitPercent ? ` — ${Math.round(c.extraProfitPercent)}% better` : ''}</span>`
          : `<span class="bud-lift-label">Buying straight down the list lands on the same money here. No basket beats it.</span>`}
      </div>
      <p class="bud-lift-sub">${esc(c.note || '')}</p>
      ${stretch}${haggle}`;
    el.classList.remove('hidden');
  }

  function renderBudgetBasket(plan) {
    const wrap = $('bud-results');
    if (!wrap) return;

    wrap.innerHTML = `
      <p class="bud-headline">${esc(plan.headline)}</p>
      ${plan.note ? `<p class="bud-note">${esc(plan.note)}</p>` : ''}
      <div class="fb-arb-table-wrap">
        <table class="fb-arb-table bud-table">
          <thead>
            <tr>
              <th class="fb-arb-th-rank">#</th>
              <th>Buy this</th>
              <th class="num">Pay</th>
              <th class="num">Net profit</th>
              <th class="num">ROI</th>
              <th class="num">Days to cash</th>
              <th class="num">Spent so far</th>
              <th class="num">Profit so far</th>
            </tr>
          </thead>
          <tbody>${plan.picks.map(budgetPickRowHtml).join('')}</tbody>
          <tfoot>
            <tr>
              <td colspan="2">${plan.picks.length} deal${plan.picks.length === 1 ? '' : 's'}</td>
              <td class="num"><strong>${money(plan.capitalDeployed)}</strong></td>
              <td class="num"><strong>${money(plan.totalNetProfit)}</strong></td>
              <td class="num">${plan.blendedRoiPercent != null ? `${Math.round(plan.blendedRoiPercent)}%` : '—'}</td>
              <td class="num">${plan.weightedDaysToCash != null ? `${plan.weightedDaysToCash}d avg` : '—'}</td>
              <td class="num">${money(plan.leftover)} left</td>
              <td class="num"></td>
            </tr>
          </tfoot>
        </table>
      </div>`;
    wrap.classList.remove('hidden');
  }

  function budgetPickRowHtml(pick) {
    const tier = SPEED_TIERS[pick.speedTier] || SPEED_TIERS.unknown;
    const meta = [
      pick.sourceLabel ? esc(pick.sourceLabel) : '',
      pick.distanceMiles != null ? `${pick.distanceMiles} mi` : '',
      pick.location ? esc(pick.location) : '',
      // A frozen forecast is a weaker claim than a scan run five minutes ago, and the row says so.
      pick.origin === 'tracked' ? '<span class="bud-origin">tracked — frozen forecast</span>' : '',
    ].filter(Boolean).join(' · ');

    const title = pick.url
      ? `<a href="${esc(pick.url)}" target="_blank" rel="noopener noreferrer">${esc(pick.title)}</a>`
      : esc(pick.title);

    return `
      <tr>
        <td class="fb-arb-th-rank">${pick.rank}</td>
        <td>
          <div class="bud-pick-title">${title}${pick.quantity > 1 ? ` <span class="bud-qty">×${pick.quantity}</span>` : ''}</div>
          <div class="fb-arb-meta">${meta}</div>
          <div class="bud-why">${esc(pick.why)}</div>
        </td>
        <td class="num">${money(pick.spend)}${pick.targetOffer ? `<div class="bud-sub">offer ${money(pick.targetOffer)}</div>` : ''}</td>
        <td class="num fb-arb-profit">${money(pick.totalNetProfit)}</td>
        <td class="num">${pick.roiPercent != null ? `${Math.round(pick.roiPercent)}%` : '—'}</td>
        <td class="num">${pick.daysToCash != null
          ? `<span class="speed-days speed-${tier.cls}">${pick.daysToCash}d</span>${pick.profitPerDay ? `<span class="speed-rate">${perDay(pick.profitPerDay)}</span>` : ''}`
          : '<span class="fb-arb-muted">—</span>'}</td>
        <td class="num">${money(pick.cumulativeSpend)}</td>
        <td class="num">${money(pick.cumulativeProfit)}</td>
      </tr>`;
  }

  // The same money under the other two definitions of best. Shown side by side rather than hidden
  // behind the control, because "less total money, back three weeks sooner" is a real trade the
  // seller is entitled to make with their own cash.
  function renderBudgetAlternatives(data) {
    const el = $('bud-alternatives');
    if (!el) return;

    const alts = (data.alternatives || []).filter(p => p.picks?.length);
    if (!alts.length) { el.classList.add('hidden'); return; }

    el.innerHTML = `
      <h3 class="bud-section-title">The same money, spent for something else</h3>
      <div class="bud-alt-grid">
        ${alts.map(p => `
          <div class="bud-alt">
            <div class="bud-alt-head">
              <span class="bud-alt-label">${esc(p.objectiveLabel)}</span>
              <button class="btn btn-secondary small bud-alt-btn" type="button" data-objective="${esc(p.objective)}">Use this instead</button>
            </div>
            <div class="bud-alt-money">
              <span class="bud-alt-profit">${money(p.totalNetProfit)}</span>
              <span class="bud-alt-sub">net from ${money(p.capitalDeployed)} across ${p.picks.length} deal${p.picks.length === 1 ? '' : 's'}</span>
            </div>
            <div class="bud-alt-sub">${p.allCashBackBy
              ? `All of it back by ${esc(p.allCashBackBy)}`
              : `${p.unknownSpeedCount} with no measured speed — no date on the last of it`}</div>
            <p class="bud-alt-note">${esc(p.objectiveNote)}</p>
          </div>`).join('')}
      </div>`;
    el.classList.remove('hidden');

    el.querySelectorAll('.bud-alt-btn').forEach(btn => btn.addEventListener('click', () => {
      const select = $('bud-objective');
      if (select) select.value = btn.dataset.objective;
      runBudgetPlan();
    }));
  }

  // What didn't make it, and why. A sourcing screen that silently drops deals is how a real one
  // gets missed — and "you're $30 short of this one" is the most actionable line on the page.
  function renderBudgetLeftOut(leftOut) {
    const el = $('bud-leftout');
    if (!el) return;
    if (!leftOut.length) { el.classList.add('hidden'); return; }

    el.innerHTML = `
      <h3 class="bud-section-title">Left out, and why</h3>
      <ul class="bud-leftout-list">
        ${leftOut.map(s => `
          <li class="bud-leftout-item bud-reason-${esc(s.reasonCode)}">
            <span class="bud-leftout-title">${s.url
              ? `<a href="${esc(s.url)}" target="_blank" rel="noopener noreferrer">${esc(s.title)}</a>`
              : esc(s.title)}</span>
            <span class="bud-leftout-money">${money(s.buyPrice)}${s.netProfit != null ? ` · ${money(s.netProfit)} net` : ''}</span>
            <span class="bud-leftout-reason">${esc(s.reason)}</span>
          </li>`).join('')}
      </ul>`;
    el.classList.remove('hidden');
  }

  function setBudgetStatus(text) {
    const el = $('bud-status');
    if (el) el.textContent = text || '';
  }

  // ── Recover Lost Sales — relist + Second Chance Offers ────────────────────
  // The third view of the same inventory, and the only one that can see the listings that ENDED.
  // Two different kinds of money live on this board and they are kept visually apart, because
  // they are worth very different amounts: a relist is a second run at a maybe, and a Second
  // Chance Offer goes to a named person who publicly bid a specific number on this exact item.
  // See RelistAnalyzer.cs for what sets each price and what stops it.
  let rlScan     = null;   // last RelistRecoveryResult, kept so filtering never re-scans
  let rlSelected = new Set();   // listing IDs picked for relist
  let rlBidders  = new Set();   // "listingId||userId" picked for a Second Chance Offer

  function showRelistSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('relist-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'relist'));
  }

  function closeRelistSection() {
    $('relist-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindRelist() {
    on('rl-scan-btn', 'click', runRelistScan);
    on('rl-close', 'click', closeRelistSection);
    on('rl-home', 'click', closeRelistSection);
    // Filtering is a pure view over the scan already in hand — it must never re-run eBay calls.
    on('rl-filter', 'change', renderRelistRows);
    on('rl-select-all', 'change', toggleSelectAllRelists);
    on('rl-preview-btn', 'click', () => submitRelists(true));
    on('rl-relist-btn', 'click', openRelistConfirm);
    on('rl-confirm-cancel', 'click', () => $('rl-confirm-overlay')?.classList.add('hidden'));
    on('rl-confirm-go', 'click', () => {
      $('rl-confirm-overlay')?.classList.add('hidden');
      submitRelists(false);
    });
    on('rl-sc-cancel', 'click', () => $('rl-sc-overlay')?.classList.add('hidden'));
    on('rl-sc-go', 'click', () => {
      $('rl-sc-overlay')?.classList.add('hidden');
      submitSecondChance(false);
    });
    on('rl-sc-message', 'input', updateScMessageCount);
  }

  async function runRelistScan() {
    const btn = $('rl-scan-btn');
    const days      = $('rl-days')?.value || '45';
    const maxItems  = $('rl-max-items')?.value || '120';
    const minProfit = Math.max(0, parseFloat($('rl-min-profit')?.value || '0') || 0);

    if (btn) { btn.disabled = true; btn.textContent = 'Scanning…'; }
    setRlStatus('Reading the listings that ended without selling…');
    $('rl-results').innerHTML = '<p class="opportunity-empty">Pricing each ended listing against sold comps and checking who bid on the auctions — this takes a moment.</p>';

    try {
      const res  = await fetch(`/api/relist/recover?days=${days}&maxItems=${maxItems}&minProfit=${minProfit}`);
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'The scan failed.');
      rlScan = data;
      rlSelected.clear();
      rlBidders.clear();
      renderRelistScan();
    } catch (err) {
      rlScan = null;
      $('rl-summary')?.classList.add('hidden');
      $('rl-bulk-bar')?.classList.add('hidden');
      $('rl-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The scan failed.')}</p>`;
      setRlStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '♻️ Find My Lost Sales'; }
    }
  }

  function renderRelistScan() {
    const warn = $('rl-warning');

    if (rlScan.status === 'ebay_unavailable') {
      $('rl-summary')?.classList.add('hidden');
      $('rl-bulk-bar')?.classList.add('hidden');
      warn?.classList.add('hidden');
      $('rl-results').innerHTML =
        '<p class="opportunity-empty">Your eBay account isn\'t connected, or the token has expired. Reconnect it in Settings, then scan again.</p>';
      setRlStatus('');
      return;
    }

    if (rlScan.dataWarning) {
      warn.textContent = rlScan.dataWarning;
      warn.classList.remove('hidden');
    } else {
      warn?.classList.add('hidden');
    }

    renderRelistSummary(rlScan.summary || {});
    renderRelistRows();

    const s = rlScan.summary || {};
    setRlStatus(
      `${s.analyzed || 0} of ${s.endedListings || 0} ended listing${s.endedListings === 1 ? '' : 's'} in the last ${rlScan.lookbackDays} days · ` +
      `${s.readyToRelist || 0} worth putting back up` +
      (s.secondChanceBidders ? ` · ${s.secondChanceBidders} lost bidder${s.secondChanceBidders === 1 ? '' : 's'} reachable` : ''));
  }

  // Leads with the size of the pile and the cash already sunk in it, because those are facts.
  // Everything conditional on a future sale is labelled as conditional, in the tile itself.
  function renderRelistSummary(s) {
    const el = $('rl-summary');
    if (!el) return;

    const tiles = [
      { label: 'Asked and never sold', value: money(s.askedAndUnsold),
        sub: `${s.analyzed || 0} listing${s.analyzed === 1 ? '' : 's'} that ended without a buyer`,
        tone: s.askedAndUnsold > 0 ? 'warn' : '' },
      { label: 'Your cash sitting in it', value: money(s.cashSunk),
        sub: s.withCostBasis ? `what you paid, across ${s.withCostBasis} recorded item${s.withCostBasis === 1 ? '' : 's'}` : 'Record what you paid to see this',
        tone: s.cashSunk > 0 ? 'warn' : '' },
      { label: 'Worth putting back up', value: String(s.readyToRelist || 0),
        sub: s.relistValue ? `${money(s.relistValue)} back on the site` : 'Nothing to relist yet',
        tone: s.readyToRelist > 0 ? 'good' : '' },
      { label: 'If those sell this time', value: money(s.netIfAllSell),
        sub: s.netIfAllSell ? 'net after fees — only on the ones that sell' : 'Record cost basis to see net profit',
        tone: s.netIfAllSell > 0 ? 'good' : '' },
      { label: 'Bidders who already said yes', value: String(s.secondChanceBidders || 0),
        sub: s.secondChanceValue
          ? `${money(s.secondChanceValue)} of offers at prices they bid`
          : 'No reachable losing bidders',
        tone: s.secondChanceBidders > 0 ? 'good' : '' },
      { label: 'Held back', value: String((s.underwater || 0) + (s.alreadyRelisted || 0)),
        sub: `${s.underwater || 0} with no profitable price · ${s.alreadyRelisted || 0} eBay already relisted`,
        tone: (s.underwater || 0) > 0 ? 'warn' : '' },
    ];

    el.innerHTML = tiles.map(t => `
      <div class="inv-tile ${t.tone ? 'inv-tile-' + t.tone : ''}">
        <div class="inv-tile-label">${esc(t.label)}</div>
        <div class="inv-tile-value">${esc(t.value)}</div>
        <div class="inv-tile-sub">${esc(t.sub)}</div>
      </div>`).join('');
    el.classList.remove('hidden');
  }

  const RL_VERDICTS = {
    second_chance:   { label: '🎯 Bidders waiting',   cls: 'rl-v-second' },
    relist_cheaper:  { label: '♻️ Relist cheaper',    cls: 'wo-v-ready' },
    relist_as_is:    { label: '♻️ Relist as-is',      cls: 'rl-v-asis' },
    relist:          { label: '♻️ Relist',            cls: 'wo-v-ready' },
    underwater:      { label: '🛑 No price that pays', cls: 'inv-v-underwater' },
    already_relisted:{ label: '✅ Already back up',    cls: 'inv-v-nodata' },
    ended_by_seller: { label: '⏹ You ended this',     cls: 'inv-v-nodata' },
    no_price:        { label: '? No price',           cls: 'inv-v-nodata' },
    no_data:         { label: '? No data',            cls: 'inv-v-nodata' },
  };

  function rlFilterMatches(item, filter) {
    switch (filter) {
      case 'ready':         return item.canRelist;
      case 'second_chance': return (item.sendableBidders || 0) > 0;
      case 'blocked':       return !item.canRelist;
      default:              return true;
    }
  }

  function renderRelistRows() {
    if (!rlScan) return;
    const filter = $('rl-filter')?.value || 'ready';
    const rows = (rlScan.items || []).filter(i => rlFilterMatches(i, filter));
    const el = $('rl-results');

    if (rows.length === 0) {
      const total = (rlScan.items || []).length;
      el.innerHTML = `<p class="opportunity-empty">${
        total === 0
          ? 'Nothing of yours ended unsold in that window — which is the good version of this screen being empty. Widen the window to look further back.'
          : 'Nothing matches that filter. Switch to "Everything" to see why each one was held back.'}</p>`;
      $('rl-bulk-bar')?.classList.add('hidden');
      return;
    }

    el.innerHTML = `
      <table class="inv-table">
        <thead>
          <tr>
            <th class="inv-col-check"></th>
            <th>Listing</th>
            <th class="num">Ended</th>
            <th class="num">Interest</th>
            <th class="num">Asked</th>
            <th class="num">Market</th>
            <th class="num">Put back at</th>
            <th class="num">Your net</th>
            <th>What happened</th>
          </tr>
        </thead>
        <tbody>${rows.map(rlRowHtml).join('')}</tbody>
      </table>`;

    el.querySelectorAll('.rl-check').forEach(cb => cb.addEventListener('change', onRelistCheck));
    el.querySelectorAll('.rl-price-input').forEach(inp => {
      inp.addEventListener('change', onRelistPriceEdit);
      inp.addEventListener('keydown', e => { if (e.key === 'Enter') inp.blur(); });
    });
    el.querySelectorAll('.rl-bidder-check').forEach(cb => cb.addEventListener('change', onBidderCheck));
    el.querySelectorAll('.rl-sc-send').forEach(btn => btn.addEventListener('click', openSecondChanceConfirm));

    $('rl-bulk-bar')?.classList.remove('hidden');
    refreshRelistSelectionNote();
  }

  // "eBay didn't say" and "nobody looked" are different findings and must not render the same.
  function rlInterestHtml(item) {
    const watchers = item.watchCount;
    const views = item.hitCount;
    if (watchers == null && views == null)
      return '<span class="inv-dash" title="eBay reported no watcher or view counts for this listing">—</span>';

    const parts = [];
    if (watchers != null) parts.push(`<span class="wo-watchers ${watchers >= 5 ? 'hot' : ''}">${watchers} 👁</span>`);
    if (views != null) parts.push(`<div class="inv-sub">${views} view${views === 1 ? '' : 's'}</div>`);
    if (item.bidCount > 0) parts.push(`<div class="inv-sub rl-bids">${item.bidCount} bid${item.bidCount === 1 ? '' : 's'}</div>`);
    return parts.join('');
  }

  function rlRowHtml(item) {
    const v = RL_VERDICTS[item.verdict] || RL_VERDICTS.no_data;
    const checked = rlSelected.has(item.listingId) ? 'checked' : '';

    const priceCell = item.relistPrice != null
      ? `<input class="rl-price-input" type="number" step="0.01" min="0.01"
                value="${Number(item.relistPrice).toFixed(2)}" data-id="${esc(item.listingId)}"
                title="Edit to override the suggested relist price. The server re-checks it against your floor." />
         <div class="inv-change ${item.samePrice ? '' : 'down'}">${
           item.floorLimited ? 'at your floor' : item.samePrice ? 'unchanged' : `${Number(item.relistChangePercent || 0).toFixed(0)}%`}</div>`
      : '<span class="inv-dash">—</span>';

    return `
      <tr class="${item.canRelist ? '' : 'wo-row-muted'}">
        <td class="inv-col-check">${item.canRelist
          ? `<input class="rl-check" type="checkbox" data-id="${esc(item.listingId)}" ${checked} />`
          : ''}</td>
        <td class="inv-cell-title">
          <div class="inv-title-row">
            ${item.imageUrl ? `<img class="inv-thumb" src="${esc(item.imageUrl)}" alt="" loading="lazy" />` : ''}
            <div>
              ${item.url
                ? `<a href="${esc(item.url)}" target="_blank" rel="noopener">${esc(item.title)}</a>`
                : esc(item.title)}
              <div class="inv-sub">${esc(item.sku || item.listingId)}${item.isAuction ? ' · auction' : ''}${
                item.quantity > 1 ? ` · ${item.quantity} unsold` : ''}${
                item.costBasis != null ? ` · paid ${moneyExact(item.costBasis)}` : ' · no cost recorded'}</div>
            </div>
          </div>
        </td>
        <td class="num">${item.daysSinceEnded == null
            ? '<span class="inv-dash" title="eBay reported no end date">—</span>'
            : `${item.daysSinceEnded}d ago${item.daysListed != null ? `<div class="inv-sub">ran ${item.daysListed}d</div>` : ''}`}</td>
        <td class="num">${rlInterestHtml(item)}</td>
        <td class="num">${moneyExact(item.endPrice)}${item.quantity > 1 ? `<div class="inv-sub">×${item.quantity}</div>` : ''}</td>
        <td class="num">${item.marketPrice != null && item.marketComparable !== false
            ? `${moneyExact(item.marketPrice)}<div class="inv-sub">${item.soldCompCount + item.terapeakCompCount} comps</div>`
            : '<span class="inv-dash">—</span>'}</td>
        <td class="num inv-cell-suggested">${priceCell}</td>
        <td class="num">${item.netProfitAtRelist != null
            ? `<span class="${item.netProfitAtRelist > 0 ? 'inv-gap-good' : 'inv-gap-bad'}">${moneyExact(item.netProfitAtRelist)}</span>${
                item.netProfitAtEndPrice != null ? `<div class="inv-sub">${moneyExact(item.netProfitAtEndPrice)} at the old price</div>` : ''}`
            : '<span class="inv-dash" title="Record what you paid in Inventory Health to see net profit">—</span>'}</td>
        <td class="inv-cell-verdict">
          <span class="inv-verdict ${v.cls}">${v.label}</span>
          <div class="inv-note">${esc(item.verdictNote)}</div>
          ${(item.signals || []).length ? `<div class="inv-signals">${item.signals.map(s => esc(s)).join(' ')}</div>` : ''}
          ${item.bidderNote ? `<div class="inv-signals">${esc(item.bidderNote)}</div>` : ''}
          ${rlBiddersHtml(item)}
        </td>
      </tr>`;
  }

  // The bidders live inside the row rather than on their own screen: a lost bidder is a fact about
  // this listing, and splitting them apart is how a seller relists an item they could have just sold.
  function rlBiddersHtml(item) {
    const bidders = item.bidders || [];
    if (bidders.length === 0) return '';

    const sendable = bidders.filter(b => b.canSend);
    const rows = bidders.map(b => {
      const key = `${item.listingId}||${b.userId}`;
      return `
        <label class="rl-bidder ${b.canSend ? '' : 'rl-bidder-muted'}">
          ${b.canSend
            ? `<input class="rl-bidder-check" type="checkbox" data-key="${esc(key)}" ${rlBidders.has(key) ? 'checked' : ''} />`
            : '<span class="rl-bidder-spacer"></span>'}
          <span class="rl-bidder-id">${esc(b.userId || 'hidden bidder')}</span>
          <span class="rl-bidder-price">${b.offerPrice > 0 ? moneyExact(b.offerPrice) : '—'}</span>
          <span class="rl-bidder-note">${esc(b.note)}</span>
        </label>`;
    }).join('');

    return `
      <div class="rl-bidders">
        <div class="rl-bidders-head">
          <strong>${sendable.length} of ${bidders.length} losing bidder${bidders.length === 1 ? '' : 's'} reachable</strong>
          ${sendable.length ? `<button class="btn btn-secondary small rl-sc-send" type="button" data-id="${esc(item.listingId)}">Send Second Chance…</button>` : ''}
        </div>
        ${rows}
      </div>`;
  }

  function rlItemById(listingId) {
    return (rlScan?.items || []).find(i => i.listingId === listingId) || null;
  }

  function onRelistCheck(e) {
    const id = e.target.dataset.id;
    if (e.target.checked) rlSelected.add(id); else rlSelected.delete(id);
    refreshRelistSelectionNote();
  }

  function onBidderCheck(e) {
    const key = e.target.dataset.key;
    if (e.target.checked) rlBidders.add(key); else rlBidders.delete(key);
  }

  function toggleSelectAllRelists(e) {
    const filter = $('rl-filter')?.value || 'ready';
    const isOn = e.target.checked;
    // Only what is on screen — a select-all that ticks rows hidden behind a filter is how someone
    // relists an item they never looked at.
    (rlScan?.items || [])
      .filter(i => rlFilterMatches(i, filter) && i.canRelist)
      .forEach(i => { if (isOn) rlSelected.add(i.listingId); else rlSelected.delete(i.listingId); });
    document.querySelectorAll('.rl-check').forEach(cb => { cb.checked = rlSelected.has(cb.dataset.id); });
    refreshRelistSelectionNote();
  }

  function refreshRelistSelectionNote() {
    const picked = [...rlSelected].map(rlItemById).filter(Boolean);
    const note = $('rl-selection-note');
    if (note) {
      if (picked.length === 0) note.textContent = 'Nothing selected.';
      else {
        const value = picked.reduce((sum, i) => sum + (i.relistPrice || 0) * (i.quantity || 1), 0);
        const net = picked.filter(i => i.netProfitAtRelist != null)
                          .reduce((sum, i) => sum + i.netProfitAtRelist * (i.quantity || 1), 0);
        note.textContent = `${picked.length} listing${picked.length === 1 ? '' : 's'} selected · ` +
          `${moneyExact(value)} back on the site` +
          (net ? ` · ${moneyExact(net)} net if they all sell` : '');
      }
    }
    const disabled = picked.length === 0;
    if ($('rl-preview-btn')) $('rl-preview-btn').disabled = disabled;
    if ($('rl-relist-btn'))  $('rl-relist-btn').disabled  = disabled;
  }

  // An edited price is the seller's call, and the server re-checks it against the floor either way.
  function onRelistPriceEdit(e) {
    const item = rlItemById(e.target.dataset.id);
    if (!item) return;
    const raw = parseFloat(e.target.value);
    const value = isFinite(raw) && raw > 0 ? Math.round(raw * 100) / 100 : (item.relistPrice || item.endPrice);
    e.target.value = value.toFixed(2);
    item.relistPrice = value;
    item.samePrice = Math.abs(value - item.endPrice) < 0.01;
    item.relistChangePercent = item.endPrice > 0 ? ((value - item.endPrice) / item.endPrice) * 100 : 0;
    // The net shown next to it was computed for the old number, so it is cleared rather than left
    // to say something that is no longer true. The confirm screen shows the price, which is exact.
    item.netProfitAtRelist = null;
    const label = e.target.parentElement?.querySelector('.inv-change');
    if (label) label.textContent = 'edited';
    refreshRelistSelectionNote();
  }

  function openRelistConfirm() {
    const picked = [...rlSelected].map(rlItemById).filter(Boolean);
    if (picked.length === 0) return;

    const value = picked.reduce((sum, i) => sum + (i.relistPrice || 0) * (i.quantity || 1), 0);
    const cut   = picked.reduce((sum, i) => sum + Math.max(0, (i.endPrice - (i.relistPrice || 0))) * (i.quantity || 1), 0);
    $('rl-confirm-total').textContent =
      `${picked.length} listing${picked.length === 1 ? '' : 's'} back on eBay · ${moneyExact(value)} of stock` +
      (cut > 0 ? ` · ${moneyExact(cut)} off the prices that already failed` : ' · at the same prices');

    $('rl-confirm-list').innerHTML = picked.map(i => `
      <div class="inv-confirm-row">
        <span class="inv-confirm-title">${esc(i.title)}</span>
        <span class="inv-confirm-prices">${moneyExact(i.endPrice)} → <strong>${moneyExact(i.relistPrice)}</strong>${
          i.samePrice ? ' (unchanged)' : ''}</span>
      </div>`).join('');

    const loss = $('rl-allow-loss');
    if (loss) loss.checked = false;
    $('rl-confirm-overlay')?.classList.remove('hidden');
  }

  async function submitRelists(dryRun) {
    const picked = [...rlSelected].map(rlItemById).filter(Boolean);
    if (picked.length === 0) return;

    const body = {
      items: picked.map(i => ({
        listingId: i.listingId, sku: i.sku, title: i.title,
        newPrice: i.relistPrice, endPrice: i.endPrice,
        quantity: i.quantity || 1, isAuction: !!i.isAuction,
      })),
      minNetProfit: Math.max(0, parseFloat($('rl-min-profit')?.value || '0') || 0),
      dryRun,
      confirmed: !dryRun,
      allowBelowFloor: !dryRun && !!$('rl-allow-loss')?.checked,
    };

    setRlStatus(dryRun ? 'Previewing…' : 'Relisting on eBay…');
    try {
      const res = await fetch('/api/relist/run', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'Relisting failed.');
      renderRelistOutcome(data);
      if (!dryRun && data.relisted > 0) {
        addActivity(`${data.relisted} listing${data.relisted === 1 ? '' : 's'} back on eBay`,
          `${moneyExact(data.listedValue)} of stock relisted from Recover Lost Sales.`);
        // Those listings now have new item numbers, so the board is stale for them.
        await runRelistScan();
      }
    } catch (err) {
      setRlStatus(`Relisting failed: ${err.message || err}`);
    }
  }

  function renderRelistOutcome(data) {
    const parts = [];
    if (data.dryRun) parts.push(`Preview only — nothing relisted. ${data.items.length} listing${data.items.length === 1 ? '' : 's'} ready, ${moneyExact(data.listedValue)} of stock.`);
    else parts.push(`${data.relisted} back on eBay · ${moneyExact(data.listedValue)} of stock` +
      (data.totalFees > 0 ? ` · ${moneyExact(data.totalFees)} in insertion fees` : ''));
    if (data.skipped) parts.push(`${data.skipped} skipped`);
    if (data.failed)  parts.push(`${data.failed} failed`);

    const problems = (data.items || []).filter(i => i.status === 'skipped' || i.status === 'failed');
    setRlStatus(parts.join(' · ') + (problems.length
      ? ' — ' + problems.map(p => `${p.title}: ${p.message}`).join('; ')
      : ''));
  }

  // ── Second Chance Offers ─────────────────────────────────────────────────
  // Priced at what the bidder already bid, so there is nothing to size and no slider to move. The
  // only decision is whether to send it.

  function selectedBidders() {
    const out = [];
    for (const key of rlBidders) {
      const [listingId, userId] = key.split('||');
      const item = rlItemById(listingId);
      const bidder = (item?.bidders || []).find(b => b.userId === userId);
      if (item && bidder && bidder.canSend) out.push({ item, bidder });
    }
    return out;
  }

  function openSecondChanceConfirm(e) {
    // The button belongs to one listing, so tick that listing's reachable bidders if the seller
    // hasn't ticked any of them — clicking "send" on a row should never open an empty confirm.
    const listingId = e.currentTarget.dataset.id;
    const item = rlItemById(listingId);
    if (item && !(item.bidders || []).some(b => b.canSend && rlBidders.has(`${listingId}||${b.userId}`)))
      (item.bidders || []).filter(b => b.canSend).forEach(b => rlBidders.add(`${listingId}||${b.userId}`));
    document.querySelectorAll('.rl-bidder-check').forEach(cb => { cb.checked = rlBidders.has(cb.dataset.key); });

    const picked = selectedBidders();
    if (picked.length === 0) return;

    const total = picked.reduce((sum, p) => sum + p.bidder.offerPrice, 0);
    $('rl-sc-total').textContent =
      `${picked.length} offer${picked.length === 1 ? '' : 's'} · ${moneyExact(total)} if every one is taken`;

    $('rl-sc-list').innerHTML = picked.map(p => `
      <div class="inv-confirm-row">
        <span class="inv-confirm-title">${esc(p.item.title)}</span>
        <span class="inv-confirm-prices">${esc(p.bidder.userId)} &nbsp; <strong>${moneyExact(p.bidder.offerPrice)}</strong> (their bid)</span>
      </div>`).join('');

    const box = $('rl-sc-message');
    if (box && !box.value) box.value = rlScan?.defaultSellerMessage || '';
    updateScMessageCount();
    const loss = $('rl-sc-allow-loss');
    if (loss) loss.checked = false;
    $('rl-sc-overlay')?.classList.remove('hidden');
  }

  function updateScMessageCount() {
    const box = $('rl-sc-message');
    const counter = $('rl-sc-message-count');
    if (box && counter) counter.textContent = `${box.value.length}/250`;
  }

  async function submitSecondChance(dryRun) {
    const picked = selectedBidders();
    if (picked.length === 0) return;

    const body = {
      items: picked.map(p => ({
        listingId: p.item.listingId, sku: p.item.sku, title: p.item.title,
        bidderUserId: p.bidder.userId, offerPrice: p.bidder.offerPrice,
      })),
      message: $('rl-sc-message')?.value || '',
      durationDays: parseInt($('rl-sc-duration')?.value || '3', 10),
      minNetProfit: Math.max(0, parseFloat($('rl-min-profit')?.value || '0') || 0),
      dryRun,
      confirmed: !dryRun,
      allowBelowFloor: !dryRun && !!$('rl-sc-allow-loss')?.checked,
    };

    setRlStatus(dryRun ? 'Previewing offers…' : 'Sending Second Chance Offers on eBay…');
    try {
      const res = await fetch('/api/relist/second-chance', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'Sending failed.');

      const parts = [];
      if (data.dryRun) parts.push(`Preview only — nothing sent. ${data.items.length} offer${data.items.length === 1 ? '' : 's'} ready.`);
      else parts.push(`${data.sent} Second Chance Offer${data.sent === 1 ? '' : 's'} sent · ${moneyExact(data.offeredValue)} if all are taken`);
      if (data.skipped) parts.push(`${data.skipped} skipped`);
      if (data.failed)  parts.push(`${data.failed} failed`);

      const problems = (data.items || []).filter(i => i.status === 'skipped' || i.status === 'failed');
      setRlStatus(parts.join(' · ') + (problems.length
        ? ' — ' + problems.map(p => `${p.bidderUserId}: ${p.message}`).join('; ')
        : ''));

      if (!dryRun && data.sent > 0) {
        addActivity(`${data.sent} Second Chance Offer${data.sent === 1 ? '' : 's'} sent`,
          `${moneyExact(data.offeredValue)} offered to bidders who lost your ended auctions.`);
        rlBidders.clear();
        await runRelistScan();
      }
    } catch (err) {
      setRlStatus(`Sending failed: ${err.message || err}`);
    }
  }

  function setRlStatus(text) {
    const el = $('rl-status');
    if (el) el.textContent = text || '';
  }

  // ── Money Made — the earnings tracker ─────────────────────────────────────
  // Everything else in this file renders a forecast. This renders the past, and the difference
  // shows up in three rules the UI has to keep:
  //   * The headline is net profit, never proceeds. Sales with no recorded cost are shown in their
  //     own block with what they WOULD add — the number only ever grows by being made true.
  //   * Every assumption that flatters a row is printed next to the row, not in a footnote.
  //   * Nothing on this page is a placeholder. A seller with no sales gets an empty state, never
  //     a "$0.00 earned" hero, which reads as the app failing rather than as nothing having sold.
  let earnings = null;

  function showEarningsSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('earnings-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'earnings'));
    if (!earnings) loadEarnings();
  }

  function closeEarningsSection() {
    $('earnings-section')?.classList.add('hidden');
    $('er-log-modal')?.classList.add('hidden');
    showDashboard();
  }

  function bindEarnings() {
    on('er-close', 'click', closeEarningsSection);
    on('er-home', 'click', closeEarningsSection);
    on('er-import-btn', 'click', importEarnings);
    on('er-log-btn', 'click', openFlipLogger);
    on('er-log-cancel', 'click', closeFlipLogger);
    on('er-log-cancel-2', 'click', closeFlipLogger);
    on('er-log-save', 'click', saveManualFlip);
    on('dash-earnings-open', 'click', () => { location.hash = 'earnings'; });
    on('er-chart-table-toggle', 'click', () => toggleDisclosure('er-chart-table', 'er-chart-table-toggle', 'Show as table', 'Hide table'));
    on('er-ledger-toggle', 'click', () => toggleDisclosure('er-ledger', 'er-ledger-toggle', 'Show all sales', 'Hide all sales'));

    // Live net as the seller types, so the flip they're logging is priced before they commit to it.
    ['er-f-price', 'er-f-cost', 'er-f-shipcharged', 'er-f-shipcost', 'er-f-fee', 'er-f-other', 'er-f-qty']
      .forEach(id => on(id, 'input', updateFlipPreview));

    $('earnings-section')?.addEventListener('click', onEarningsClick);
    $('earnings-section')?.addEventListener('keydown', e => {
      if (e.key === 'Enter' && e.target?.classList?.contains('er-cost-input')) {
        e.preventDefault();
        saveFlipCost(e.target.dataset.id, e.target.value);
      }
      if (e.key === 'Escape' && !$('er-log-modal')?.classList.contains('hidden')) closeFlipLogger();
    });
  }

  function toggleDisclosure(panelId, buttonId, closedLabel, openLabel) {
    const panel = $(panelId);
    const btn = $(buttonId);
    if (!panel || !btn) return;
    const nowHidden = panel.classList.toggle('hidden');
    btn.textContent = nowHidden ? closedLabel : openLabel;
    btn.setAttribute('aria-expanded', nowHidden ? 'false' : 'true');
  }

  function onEarningsClick(e) {
    const save = e.target.closest?.('.er-cost-save');
    if (save) {
      const input = $('earnings-section').querySelector(`.er-cost-input[data-id="${save.dataset.id}"]`);
      saveFlipCost(save.dataset.id, input?.value);
      return;
    }
    const del = e.target.closest?.('.er-flip-delete');
    if (del) deleteFlip(del.dataset.id, del.dataset.title);
  }

  function setEarningsStatus(text) {
    const el = $('er-status');
    if (el) el.textContent = text || '';
  }

  function showEarningsNotice(text, tone) {
    const el = $('er-notice');
    if (!el) return;
    if (!text) { el.classList.add('hidden'); return; }
    el.textContent = text;
    el.classList.toggle('er-notice-good', tone === 'good');
    el.classList.remove('hidden');
  }

  async function loadEarnings(quiet) {
    try {
      const res = await fetch('/api/earnings');
      if (!res.ok) throw new Error('Could not read your earnings.');
      earnings = await res.json();
      renderEarnings();
      renderDashboardEarnings();
    } catch (err) {
      if (!quiet) setEarningsStatus(err.message || 'Could not read your earnings.');
    }
  }

  async function importEarnings() {
    const btn = $('er-import-btn');
    const days = $('er-days')?.value || '90';
    if (btn) { btn.disabled = true; btn.textContent = 'Reading eBay…'; }
    setEarningsStatus('Reading your completed orders from eBay…');
    showEarningsNotice('');

    try {
      const { res, body } = await safePost(`/api/earnings/import?days=${days}`, {});
      if (!res.ok) throw new Error(typeof body === 'string' ? body : (body.error || 'The import failed.'));

      const imp = body.import || {};
      if (imp.status !== 'ok') {
        // not_connected / reconnect / error all mean "we couldn't ask", which is a different thing
        // from "you made nothing" and must never be rendered as a zeroed total.
        showEarningsNotice(imp.message || 'eBay could not be reached.');
        setEarningsStatus('');
        return;
      }

      earnings = body.earnings;
      renderEarnings();
      renderDashboardEarnings();

      const bits = [`${imp.linesImported} sold item${imp.linesImported === 1 ? '' : 's'} from ${imp.ordersRead} order${imp.ordersRead === 1 ? '' : 's'}`];
      if (imp.linesAdded) bits.push(`${imp.linesAdded} new`);
      if (imp.linesUpdated) bits.push(`${imp.linesUpdated} already tracked`);
      setEarningsStatus(bits.join(' · '));

      showEarningsNotice(
        imp.feesReportedByEbay
          ? 'eBay reported its actual fees on these orders, so the profit below is measured, not estimated.'
          : 'eBay did not report fees on these orders, so fees are estimated from your Fees & Costs settings.',
        imp.feesReportedByEbay ? 'good' : undefined);
    } catch (err) {
      showEarningsNotice(err.message || 'The import failed.');
      setEarningsStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '⬇ Import eBay Sales'; }
    }
  }

  function renderEarnings() {
    if (!earnings) return;
    const s = earnings.summary || {};
    const hasSales = (s.salesAllTime || 0) > 0;

    $('er-results')?.classList.toggle('hidden', hasSales);
    $('er-stats')?.classList.toggle('hidden', !hasSales);
    $('er-chart-card')?.classList.toggle('hidden', !hasSales);
    $('er-ledger-card')?.classList.toggle('hidden', !hasSales);
    $('er-honesty')?.classList.toggle('hidden', !hasSales);

    renderEarningsHero(s, hasSales);
    renderEarningsAwaiting();
    if (!hasSales) {
      ['er-best', 'er-returns'].forEach(id => { const el = $(id); if (el) el.innerHTML = ''; });
      return;
    }

    renderEarningsStats(s);
    renderEarningsChart();
    renderEarningsLeaders();
    renderEarningsLedger();

    const honesty = $('er-honesty');
    if (honesty) honesty.innerHTML = (earnings.honesty || []).map(line => `<p>${esc(line)}</p>`).join('');
  }

  function renderEarningsHero(s, hasSales) {
    const figure = $('er-hero-month');
    if (figure) {
      figure.textContent = moneyExact(s.netProfitThisMonth || 0);
      figure.classList.toggle('er-negative', (s.netProfitThisMonth || 0) < 0);
    }
    setText('er-hero-alltime', moneyExact(s.netProfitAllTime || 0));
    setText('er-hero-30', moneyExact(s.netProfitLast30Days || 0));
    setText('er-hero-best', s.bestMonthLabel ? `${money(s.bestMonthProfit)} · ${s.bestMonthLabel}` : '—');

    const sub = $('er-hero-sub');
    if (!sub) return;
    if (!hasSales) {
      sub.textContent = 'this month — import your sales to start the count';
      return;
    }

    const parts = [`this month, across ${s.salesThisMonth || 0} sale${s.salesThisMonth === 1 ? '' : 's'}`];
    if (s.monthOverMonthPercent != null) {
      const up = s.monthOverMonthPercent >= 0;
      parts.push(`${up ? '▲' : '▼'} ${Math.abs(s.monthOverMonthPercent).toFixed(1)}% vs ${moneyExact(s.netProfitLastMonth)} last month`);
    } else if ((s.netProfitLastMonth || 0) === 0 && (s.salesAllTime || 0) > (s.salesThisMonth || 0)) {
      parts.push('nothing recorded last month');
    }
    sub.textContent = parts.join(' · ');
  }

  function renderEarningsStats(s) {
    const el = $('er-stats');
    if (!el) return;

    // Deliberately not a stat tile: "gross sales" next to "net profit" is how a seller ends up
    // quoting the bigger number to themselves. Gross is here as context for the net, sized down.
    const tiles = [
      ['Net profit, all time', moneyExact(s.netProfitAllTime || 0), `${s.salesAllTime || 0} sale${s.salesAllTime === 1 ? '' : 's'} · ${s.unitsAllTime || 0} unit${s.unitsAllTime === 1 ? '' : 's'}`, (s.netProfitAllTime || 0) < 0],
      ['Average per sale', s.averageProfitPerSale != null ? moneyExact(s.averageProfitPerSale) : '—', 'across every sale with a recorded cost', (s.averageProfitPerSale || 0) < 0],
      ['Return on money spent', s.averageRoiPercent != null ? `${s.averageRoiPercent.toFixed(1)}%` : '—', `${moneyExact(s.costOfGoodsAllTime || 0)} of buying turned into profit`, (s.averageRoiPercent || 0) < 0],
      ['Margin', s.averageMarginPercent != null ? `${s.averageMarginPercent.toFixed(1)}%` : '—', 'of every dollar taken in that you kept', (s.averageMarginPercent || 0) < 0],
      ['Gross sales', moneyExact(s.grossRevenueAllTime || 0), 'before fees, shipping and what you paid', false],
      ['eBay fees paid', moneyExact(s.feesAllTime || 0), s.profitFromEstimatedFees ? 'part measured, part estimated' : 'as charged by eBay', false],
    ];

    el.innerHTML = tiles.map(([label, value, note, negative]) => `
      <div class="er-stat">
        <span class="er-stat-label">${esc(label)}</span>
        <span class="er-stat-value${negative ? ' er-negative' : ''}">${esc(value)}</span>
        <span class="er-stat-note">${esc(note)}</span>
      </div>`).join('');
  }

  // Monthly net profit. One series, so no legend — the card title names what is plotted. Columns
  // grow from a single zero baseline and go below it when a month lost money, which is the one
  // thing a "money made" chart must be able to show without arguing about it.
  function renderEarningsChart() {
    const host = $('er-chart');
    const months = earnings?.months || [];
    if (!host || !months.length) return;

    const W = 760, H = 240;
    const padL = 62, padR = 16, padT = 16, padB = 44;
    const plotW = W - padL - padR, plotH = H - padT - padB;

    const values = months.map(m => m.netProfit || 0);
    const maxV = Math.max(0, ...values);
    const minV = Math.min(0, ...values);
    const span = (maxV - minV) || 1;
    const y = v => padT + plotH - ((v - minV) / span) * plotH;
    const zeroY = y(0);

    const band = plotW / months.length;
    const barW = Math.min(24, band - 2); // capped, and the leftover band stays as air

    const ticks = niceTicks(minV, maxV, 4);
    const grid = ticks.map(t => `
      <line x1="${padL}" y1="${y(t).toFixed(1)}" x2="${W - padR}" y2="${y(t).toFixed(1)}" class="er-grid" />
      <text x="${padL - 10}" y="${(y(t) + 4).toFixed(1)}" class="er-axis er-axis-y">${money(t)}</text>`).join('');

    const best = Math.max(...values);
    const bars = months.map((m, i) => {
      const v = m.netProfit || 0;
      const x = padL + i * band + (band - barW) / 2;
      const top = v >= 0 ? y(v) : zeroY;
      const h = Math.max(v === 0 ? 0 : 1.5, Math.abs(y(v) - zeroY));
      const cls = v < 0 ? 'er-bar er-bar-loss' : 'er-bar';
      // 4px rounded data-end, square at the baseline — rounding both ends makes a short bar read
      // as a pill rather than as a quantity.
      const r = Math.min(4, h);
      const path = v >= 0
        ? `M${x} ${top + h} L${x} ${top + r} Q${x} ${top} ${x + r} ${top} L${x + barW - r} ${top} Q${x + barW} ${top} ${x + barW} ${top + r} L${x + barW} ${top + h} Z`
        : `M${x} ${top} L${x} ${top + h - r} Q${x} ${top + h} ${x + r} ${top + h} L${x + barW - r} ${top + h} Q${x + barW} ${top + h} ${x + barW} ${top + h - r} L${x + barW} ${top} Z`;

      const tip = `${m.label}: ${moneyExact(v)} net from ${m.sales} sale${m.sales === 1 ? '' : 's'}`
        + (m.salesAwaitingCost ? ` (${m.salesAwaitingCost} still waiting on a cost)` : '');

      // Only the peak is directly labelled. A value on every column is noise, and the tooltip and
      // the table view carry the rest.
      const label = (v === best && v > 0)
        ? `<text x="${(x + barW / 2).toFixed(1)}" y="${(y(v) - 8).toFixed(1)}" class="er-bar-label">${money(v)}</text>`
        : '';

      return `<g class="er-bar-group"><title>${esc(tip)}</title>
        <rect x="${(padL + i * band).toFixed(1)}" y="${padT}" width="${band.toFixed(1)}" height="${plotH}" class="er-bar-hit" />
        <path d="${path}" class="${cls}" />${label}
        <text x="${(x + barW / 2).toFixed(1)}" y="${H - 26}" class="er-axis er-axis-x">${esc(m.label.split(' ')[0])}</text>
        <text x="${(x + barW / 2).toFixed(1)}" y="${H - 14}" class="er-axis er-axis-x er-axis-year">${esc(m.label.split(' ')[1] || '')}</text>
      </g>`;
    }).join('');

    host.innerHTML = `<svg viewBox="0 0 ${W} ${H}" class="er-chart-svg" role="img"
        aria-label="Net profit by month. Use Show as table for the values.">
      ${grid}
      ${bars}
      <line x1="${padL}" y1="${zeroY.toFixed(1)}" x2="${W - padR}" y2="${zeroY.toFixed(1)}" class="er-zero" />
    </svg>`;

    const table = $('er-chart-table');
    if (table) table.innerHTML = `
      <table class="er-table">
        <thead><tr><th>Month</th><th class="num">Net profit</th><th class="num">Gross sales</th><th class="num">Sales</th><th class="num">Awaiting cost</th></tr></thead>
        <tbody>${months.map(m => `<tr>
          <td>${esc(m.label)}${m.isCurrentMonth ? ' <span class="er-chip">so far</span>' : ''}</td>
          <td class="num${(m.netProfit || 0) < 0 ? ' er-negative' : ''}">${moneyExact(m.netProfit || 0)}</td>
          <td class="num">${moneyExact(m.grossRevenue || 0)}</td>
          <td class="num">${m.sales || 0}</td>
          <td class="num">${m.salesAwaitingCost || 0}</td>
        </tr>`).join('')}</tbody>
      </table>`;
  }

  // Round tick values so the axis reads 0 / 500 / 1,000 rather than 0 / 487 / 974.
  function niceTicks(min, max, count) {
    const span = (max - min) || 1;
    const raw = span / Math.max(1, count);
    const mag = Math.pow(10, Math.floor(Math.log10(raw)));
    const step = [1, 2, 2.5, 5, 10].map(m => m * mag).find(s => s >= raw) || mag * 10;
    const ticks = [];
    for (let t = Math.ceil(min / step) * step; t <= max + step / 2; t += step) ticks.push(Math.round(t * 100) / 100);
    if (!ticks.includes(0) && min <= 0 && max >= 0) ticks.push(0);
    return ticks;
  }

  function renderEarningsLeaders() {
    const losses = earnings.worstFlips || [];

    renderFlipList('er-best', earnings.bestFlips || [], f => moneyExact(f.netProfit || 0),
      (earnings.summary?.salesAwaitingCost || 0) > 0
        ? 'Nothing with a recorded cost has turned a profit yet. Enter costs above and the winners show up here.'
        : 'No sale has turned a profit yet.');

    // The second column changes job when there is nothing to celebrate. A "best returns" list is
    // meaningless with no profitable sales, and the losses are the rows worth reading instead.
    const showLosses = !(earnings.bestReturns || []).length && losses.length > 0;
    setText('er-returns-title', showLosses ? '🩹 Sold below cost' : '📈 Best returns');

    if (showLosses) {
      renderFlipList('er-returns', losses, f => moneyExact(f.netProfit || 0), '');
      return;
    }

    renderFlipList('er-returns', earnings.bestReturns || [], f => `${(f.roiPercent || 0).toFixed(0)}%`,
      'No sale yet clears $10 profit with a recorded cost — that is the bar for a return worth repeating.');
  }

  function renderFlipList(hostId, flips, valueOf, emptyText) {
    const el = $(hostId);
    if (!el) return;
    if (!flips.length) { el.innerHTML = `<p class="er-empty">${esc(emptyText)}</p>`; return; }

    el.innerHTML = flips.map(f => `
      <div class="er-row">
        <div class="er-row-main">
          <span class="er-row-title" title="${esc(f.title)}">${esc(f.title)}</span>
          <span class="er-row-meta">${esc(shortDate(f.soldUtc))} · sold ${moneyExact(f.grossRevenue || 0)}${f.costOfGoods != null ? ` · paid ${moneyExact(f.costOfGoods)}` : ''}${f.source === 'manual' ? ' · logged by you' : ''}</span>
        </div>
        <span class="er-row-value${(f.netProfit || 0) < 0 ? ' er-negative' : ''}">${esc(valueOf(f))}</span>
      </div>`).join('');
  }

  // Every sale, checkable against the seller's own eBay statement. A running total nobody can audit
  // is a marketing claim; this is what turns it into a record. The "Fee" column says whether eBay
  // reported the number or the app estimated it, per row, because that is exactly the distinction
  // someone reconciling against a payout will want.
  function renderEarningsLedger() {
    const el = $('er-ledger');
    const flips = earnings?.flips || [];
    if (!el) return;

    const title = $('er-ledger-title');
    if (title) title.textContent = `${flips.length} sale${flips.length === 1 ? '' : 's'} on record`;

    el.innerHTML = `
      <table class="er-table">
        <thead><tr>
          <th>Sold</th><th>Item</th><th class="num">Took in</th><th class="num">Fee</th>
          <th class="num">You paid</th><th class="num">Net</th><th>Source</th><th></th>
        </tr></thead>
        <tbody>${flips.map(f => {
          const caveats = (f.caveats || []).join(' ');
          const netCell = f.netProfit == null
            ? '<span class="er-muted">needs a cost</span>'
            : `${moneyExact(f.netProfit)}${f.roiPercent != null ? ` <span class="er-muted">${f.roiPercent.toFixed(0)}%</span>` : ''}`;
          return `<tr${f.status !== 'paid' ? ' class="er-row-void"' : ''}>
            <td>${esc(shortDate(f.soldUtc))}</td>
            <td class="er-cell-item" title="${esc(f.title)}${caveats ? ' — ' + esc(caveats) : ''}">${esc(f.title)}${f.quantity > 1 ? ` <span class="er-muted">×${f.quantity}</span>` : ''}${f.status !== 'paid' ? ` <span class="er-chip">${esc(f.status)}</span>` : ''}</td>
            <td class="num">${moneyExact(f.grossRevenue || 0)}</td>
            <td class="num" title="${f.feesAreActual ? "eBay's own figure for this sale" : 'Estimated from your fee settings — eBay did not report a fee'}">${moneyExact(f.fees || 0)}${f.feesAreActual ? '' : ' <span class="er-muted">est</span>'}</td>
            <td class="num">${f.costOfGoods != null ? moneyExact(f.costOfGoods) : '<span class="er-muted">—</span>'}</td>
            <td class="num${(f.netProfit || 0) < 0 ? ' er-negative' : ''}">${netCell}</td>
            <td>${f.source === 'ebay' ? 'eBay' : 'You'}</td>
            <td class="num"><button class="er-flip-delete" data-id="${f.id}" data-title="${esc(f.title)}" type="button" title="Stop counting this sale" aria-label="Remove ${esc(f.title)} from earnings">✕</button></td>
          </tr>`;
        }).join('')}</tbody>
      </table>`;
  }

  // The growth path, and the only ask this page makes of the seller. Framed as money they have
  // already earned but haven't proved, because that is exactly what it is.
  function renderEarningsAwaiting() {
    const el = $('er-awaiting');
    if (!el) return;
    const s = earnings?.summary || {};
    const pending = earnings?.awaitingCost || [];

    if (!pending.length) { el.classList.add('hidden'); el.innerHTML = ''; return; }

    el.classList.remove('hidden');
    el.innerHTML = `
      <div class="er-awaiting-head">
        <div>
          <p class="er-awaiting-title">${moneyExact(s.proceedsAwaitingCost || 0)} after fees isn't counted above</p>
          <p class="er-awaiting-sub">${s.salesAwaitingCost} sale${s.salesAwaitingCost === 1 ? '' : 's'} with no record of what you paid. Type the cost and the profit lands in your total — it also gives Inventory Health a real break-even floor on the next one.${pending.length < s.salesAwaitingCost ? ` Showing the ${pending.length} biggest; the rest appear as you work through these.` : ''}</p>
        </div>
      </div>
      <div class="er-awaiting-list">
        ${pending.map(f => `
          <div class="er-awaiting-row">
            <div class="er-row-main">
              <span class="er-row-title" title="${esc(f.title)}">${esc(f.title)}</span>
              <span class="er-row-meta">${esc(shortDate(f.soldUtc))} · sold ${moneyExact(f.grossRevenue || 0)} · ${moneyExact(f.netProceeds || 0)} after fees${f.quantity > 1 ? ` · ${f.quantity} units` : ''}</span>
            </div>
            <div class="er-cost-entry">
              <label class="er-cost-label" for="er-cost-${f.id}">You paid ($ each)</label>
              <input id="er-cost-${f.id}" class="er-cost-input" data-id="${f.id}" type="number" min="0" step="0.01" placeholder="0.00" />
              <button class="btn btn-secondary small er-cost-save" data-id="${f.id}" type="button">Save</button>
            </div>
          </div>`).join('')}
      </div>`;
  }

  async function saveFlipCost(id, raw) {
    const value = parseFloat(raw);
    if (!id || !isFinite(value) || value < 0) { setEarningsStatus('Enter what you paid for it — zero or more.'); return; }

    try {
      const { res, body } = await safePost('/api/earnings/cost', { id: Number(id), unitCost: value });
      if (!res.ok) throw new Error(typeof body === 'string' ? body : (body.error || 'That cost could not be saved.'));
      const before = earnings?.summary?.netProfitAllTime || 0;
      earnings = body.earnings;
      renderEarnings();
      renderDashboardEarnings();

      // One cost basis can price several sales of the same listing at once. That is the right
      // answer — but moving thousands of dollars off one typed number without saying how many
      // sales it landed on is how a seller decides the total is made up.
      const also = body.alsoAffected || 0;
      const scope = also
        ? ` It also priced ${also} other sale${also === 1 ? '' : 's'} of the same item.`
        : '';
      const delta = (earnings.summary?.netProfitAllTime || 0) - before;
      setEarningsStatus((delta >= 0
        ? `Cost saved — ${moneyExact(delta)} of real profit added to your total.`
        : `Cost saved — that works out to a ${moneyExact(Math.abs(delta))} loss, and the total now says so.`) + scope);
    } catch (err) {
      setEarningsStatus(err.message || 'That cost could not be saved.');
    }
  }

  async function deleteFlip(id, title) {
    if (!id) return;
    if (!confirm(`Remove "${title || 'this sale'}" from your earnings? It stays on eBay — this only stops counting it here.`)) return;
    try {
      const res = await fetch(`/api/earnings/flips/${id}`, { method: 'DELETE' });
      if (!res.ok) throw new Error('That sale could not be removed.');
      earnings = await res.json();
      renderEarnings();
      renderDashboardEarnings();
      setEarningsStatus('Removed.');
    } catch (err) {
      setEarningsStatus(err.message || 'That sale could not be removed.');
    }
  }

  function openFlipLogger() {
    const modal = $('er-log-modal');
    if (!modal) return;
    ['er-f-title', 'er-f-price', 'er-f-cost', 'er-f-shipcharged', 'er-f-shipcost', 'er-f-fee', 'er-f-other']
      .forEach(id => { const el = $(id); if (el) el.value = ''; });
    setVal('er-f-qty', '1');
    const today = new Date();
    setVal('er-f-date', `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`);
    $('er-log-error')?.classList.add('hidden');
    updateFlipPreview();
    modal.classList.remove('hidden');
    $('er-f-title')?.focus();
  }

  function closeFlipLogger() { $('er-log-modal')?.classList.add('hidden'); }

  function updateFlipPreview() {
    const el = $('er-log-preview');
    if (!el) return;
    const num = id => parseFloat($(id)?.value) || 0;
    const price = num('er-f-price'), cost = num('er-f-cost');
    if (price <= 0) { el.textContent = 'Fill in what it sold for and what you paid to see the net.'; el.classList.remove('er-negative'); return; }

    const qty = Math.max(1, parseInt($('er-f-qty')?.value, 10) || 1);
    const shipCharged = num('er-f-shipcharged'), shipCost = num('er-f-shipcost');
    const feeRaw = $('er-f-fee')?.value;
    // Mirrors the server's fee estimate closely enough to be useful while typing; the saved figure
    // is always the server's, computed from the seller's real Fees & Costs settings.
    const fee = feeRaw !== '' && feeRaw != null ? (parseFloat(feeRaw) || 0) : (price + shipCharged) * 0.1325 + 0.40;
    const net = (price + shipCharged) - fee - shipCost - num('er-f-other') - cost * qty;

    el.classList.toggle('er-negative', net < 0);
    el.textContent = cost > 0
      ? `Net ${moneyExact(net)}${cost > 0 ? ` · ${((net / (cost * qty)) * 100).toFixed(0)}% return on the ${moneyExact(cost * qty)} you paid` : ''}${feeRaw ? '' : ` (fees estimated at ${moneyExact(fee)})`}`
      : `${moneyExact(net + 0)} after fees — add what you paid to turn this into profit.`;
  }

  async function saveManualFlip() {
    const err = $('er-log-error');
    const num = id => { const v = $(id)?.value; return v === '' || v == null ? null : (parseFloat(v) || 0); };
    const title = ($('er-f-title')?.value || '').trim();

    if (!title) { showFlipError('What did you sell?'); return; }
    const price = num('er-f-price');
    if (price == null || price < 0) { showFlipError('What did it sell for?'); return; }

    const dateVal = $('er-f-date')?.value;
    const payload = {
      title,
      // Parsed as local midday rather than midnight UTC: a sale on the 1st logged from a western
      // timezone would otherwise land in the previous month and vanish from "this month".
      soldUtc: dateVal ? new Date(`${dateVal}T12:00:00`).toISOString() : new Date().toISOString(),
      quantity: Math.max(1, parseInt($('er-f-qty')?.value, 10) || 1),
      salePrice: price,
      shippingCharged: num('er-f-shipcharged') || 0,
      shippingCost: num('er-f-shipcost'),
      marketplaceFee: num('er-f-fee'),
      otherCosts: num('er-f-other') || 0,
      unitCost: num('er-f-cost'),
      status: 'paid',
    };

    const btn = $('er-log-save');
    if (btn) { btn.disabled = true; btn.textContent = 'Saving…'; }
    try {
      const { res, body } = await safePost('/api/earnings/flips', payload);
      if (!res.ok) throw new Error(typeof body === 'string' ? body : (body.error || 'That flip could not be saved.'));
      earnings = body;
      renderEarnings();
      renderDashboardEarnings();
      closeFlipLogger();
      setEarningsStatus(`"${title}" added to your earnings.`);
    } catch (e) {
      showFlipError(e.message || 'That flip could not be saved.');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'Save this flip'; }
    }
    if (err) { /* keeps the error visible until the next attempt */ }
  }

  function showFlipError(message) {
    const el = $('er-log-error');
    if (!el) return;
    el.textContent = message;
    el.classList.remove('hidden');
  }

  // The front-page band. Only ever rendered when there is a real, non-zero figure behind it.
  function renderDashboardEarnings() {
    const band = $('dash-earnings');
    if (!band) return;
    const s = earnings?.summary;
    if (!s || (s.salesAllTime || 0) === 0 || (s.netProfitAllTime || 0) === 0) { band.classList.add('hidden'); return; }

    band.classList.remove('hidden');
    const figure = $('dash-earnings-figure');
    if (figure) {
      figure.textContent = moneyExact(s.netProfitAllTime);
      figure.classList.toggle('er-negative', s.netProfitAllTime < 0);
    }

    const bits = [`${moneyExact(s.netProfitThisMonth || 0)} this month`, `${s.salesAllTime} sale${s.salesAllTime === 1 ? '' : 's'} tracked`];
    if (s.salesAwaitingCost) bits.push(`${moneyExact(s.proceedsAwaitingCost || 0)} still waiting on a cost`);
    setText('dash-earnings-sub', bits.join(' · '));

    renderEarningsSparkline();
  }

  // 12-point sparkline in the stat-tile idiom: de-emphasised history, the current month in the
  // accent. No axes — it carries shape, and the page it links to carries the numbers.
  function renderEarningsSparkline() {
    const host = $('dash-earnings-spark');
    const months = earnings?.months || [];
    if (!host || months.length < 2) { if (host) host.innerHTML = ''; return; }

    const W = 180, H = 44, pad = 4;
    const values = months.map(m => m.netProfit || 0);
    const max = Math.max(0, ...values), min = Math.min(0, ...values);
    const span = (max - min) || 1;
    const band = (W - pad * 2) / months.length;
    const barW = Math.max(2, band - 3);

    const zeroY = pad + (H - pad * 2) * (1 - (0 - min) / span);
    const bars = months.map((m, i) => {
      const v = values[i];
      const vy = pad + (H - pad * 2) * (1 - (v - min) / span);
      const top = Math.min(vy, zeroY), h = Math.max(1, Math.abs(vy - zeroY));
      const cls = m.isCurrentMonth ? 'er-spark-bar er-spark-now' : (v < 0 ? 'er-spark-bar er-spark-loss' : 'er-spark-bar');
      return `<rect x="${(pad + i * band).toFixed(1)}" y="${top.toFixed(1)}" width="${barW.toFixed(1)}" height="${h.toFixed(1)}" rx="1" class="${cls}" />`;
    }).join('');

    host.innerHTML = `<svg viewBox="0 0 ${W} ${H}" class="er-spark-svg" aria-hidden="true">${bars}</svg>`;
  }

  function shortDate(value) {
    if (!value) return '';
    const d = new Date(value);
    return isNaN(d) ? '' : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
  }

  // ── Deal Pipeline ─────────────────────────────────────────────────────────
  // Sourced → Bought → Listed → Sold. The thread between every other screen: the forecast that
  // justified the buy, the cash that actually left, the listing it went into, and the sale that
  // settled it, all on one card.
  //
  // Three rules this UI must never break, all inherited from DealPipelineCalculator:
  //   * A projection is never money. Projected and realized profit live in different places, wear
  //     different colours, and are never summed into one figure.
  //   * The hero is capital at RISK — real money already spent — not the projected upside, which
  //     is the number a dishonest version of this page would lead with.
  //   * A card that has nothing wrong with it gets no prompt. A board that nags about everything
  //     is a board where the genuinely stuck $1,200 goes unnoticed.
  let pipeline = null;

  // The seller's own fee rates, for the rough net this page shows while a hand-entered deal is
  // being typed. Fetched once; the published defaults stand in if the call fails, and the preview
  // says out loud that it's an estimate either way.
  let dealFeeProfile = null;

  async function loadDealFeeProfile() {
    if (dealFeeProfile) return;
    try { dealFeeProfile = await fetch('/api/fees/profile').then(r => r.json()); }
    catch { /* the defaults below are the server's defaults too */ }
  }

  const DP_STAGES = {
    sourced: { label: 'Sourced', icon: '🔍', blurb: 'Found it. Haven\'t paid yet.' },
    bought:  { label: 'Bought',  icon: '💵', blurb: 'Your money is in it.' },
    listed:  { label: 'Listed',  icon: '🏷️', blurb: 'Live and working.' },
    sold:    { label: 'Sold',    icon: '✅', blurb: 'Money came back.' },
  };

  const DP_NEXT_STAGE = { sourced: 'bought', bought: 'listed', listed: 'sold' };

  function showPipelineSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('pipeline-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'pipeline'));
    if (!pipeline) loadPipeline(); else renderPipeline();
  }

  function closePipelineSection() {
    $('pipeline-section')?.classList.add('hidden');
    closeDealForm();
    closeStageForm();
    showDashboard();
  }

  function bindPipeline() {
    on('dp-close', 'click', closePipelineSection);
    on('dp-home', 'click', closePipelineSection);
    on('dp-refresh-btn', 'click', () => loadPipeline());
    on('dp-add-btn', 'click', () => openDealForm(null));
    on('dp-add-cancel', 'click', closeDealForm);
    on('dp-add-cancel-2', 'click', closeDealForm);
    on('dp-add-save', 'click', saveDealForm);
    on('dp-stage-cancel', 'click', closeStageForm);
    on('dp-stage-cancel-2', 'click', closeStageForm);
    on('dp-stage-save', 'click', saveStageForm);
    on('dash-pipeline-open', 'click', () => { location.hash = 'pipeline'; });
    on('dp-actions-toggle', 'click', () => toggleDisclosure('dp-actions', 'dp-actions-toggle', 'Show', 'Hide'));

    // Live projected profit as the seller types, so a deal is priced before it's committed to.
    ['dp-f-ask', 'dp-f-paid', 'dp-f-extra', 'dp-f-resale', 'dp-f-qty']
      .forEach(id => on(id, 'input', updateDealPreview));
    on('dp-f-stage', 'change', updateDealPreview);

    $('pipeline-section')?.addEventListener('click', onPipelineClick);
  }

  async function loadPipeline(quiet) {
    try {
      const res = await fetch('/api/deals');
      if (!res.ok) throw new Error(await res.text());
      pipeline = await res.json();
      if (!quiet) renderPipeline();
      renderDashboardPipeline();
    } catch (err) {
      renderDashboardPipeline();
      if (!quiet) setPipelineNotice(`Couldn't load the pipeline: ${err.message}`);
    }
  }

  function setPipelineNotice(message) {
    const el = $('dp-notice');
    if (!el) return;
    if (!message) { el.classList.add('hidden'); el.textContent = ''; return; }
    el.textContent = message;
    el.classList.remove('hidden');
  }

  function renderPipeline() {
    const s = pipeline?.summary;
    const deals = pipeline?.deals || [];
    const empty = deals.length === 0;

    $('dp-results')?.classList.toggle('hidden', !empty);
    $('dp-hero')?.classList.toggle('hidden', empty);
    $('dp-board')?.classList.toggle('hidden', empty);
    $('dp-stats')?.classList.toggle('hidden', empty);
    $('dp-honesty')?.classList.toggle('hidden', empty);
    $('dp-actions-card')?.classList.toggle('hidden', empty || !(pipeline?.nextActions || []).length);
    if (empty) return;

    renderPipelineHero(s);
    renderPipelineStats(s);
    renderPipelineActions(pipeline.nextActions || []);
    renderPipelineBoard(pipeline.stages || [], deals);

    const honesty = $('dp-honesty');
    if (honesty) honesty.innerHTML = (pipeline.honesty || []).map(h => `<p>${esc(h)}</p>`).join('');
  }

  function renderPipelineHero(s) {
    setText('dp-hero-figure', moneyExact(s.capitalAtRisk || 0));

    // The subline is what the hero figure is made of, because "$4,180 at risk" only means
    // something once you can see it's four deals and which stage they're stuck in.
    const bits = [];
    if (s.activeDeals) bits.push(`${s.activeDeals} deal${s.activeDeals === 1 ? '' : 's'} in play`);
    if (s.stalledCapital > 0) bits.push(`${moneyExact(s.stalledCapital)} bought but not listed`);
    if (s.overdueCapital > 0) bits.push(`${moneyExact(s.overdueCapital)} listed longer than forecast`);
    if (!bits.length) bits.push('nothing is tying up cash right now');
    setText('dp-hero-sub', bits.join(' · '));

    setText('dp-hero-projected', moneyExact(s.projectedProfitInMotion || 0));
    setText('dp-hero-realized', moneyExact(s.realizedProfit || 0));
    setText('dp-hero-realized-note',
      s.dealsClosed ? `${s.dealsClosed} deal${s.dealsClosed === 1 ? '' : 's'} closed` : 'from completed sales');

    const acc = $('dp-hero-accuracy');
    if (acc) {
      if (s.forecastAccuracyPercent != null) {
        acc.textContent = `${Math.round(s.forecastAccuracyPercent)}%`;
        // Green only for a forecast that came in at or above what it promised. A projection that
        // overshot the outcome is the failure mode this figure exists to expose.
        acc.className = `dp-hero-stat-value ${s.forecastAccuracyPercent >= 95 ? 'dp-realized' : 'dp-under'}`;
        setText('dp-hero-accuracy-note',
          `${moneyExact(s.gradedRealizedProfit)} made vs ${moneyExact(s.gradedForecastProfit)} projected`);
      } else {
        acc.textContent = '—';
        acc.className = 'dp-hero-stat-value';
        setText('dp-hero-accuracy-note', 'needs one closed deal with a cost');
      }
    }
  }

  function renderPipelineStats(s) {
    const tiles = [
      ['Capital deployed', moneyExact(s.capitalDeployedAllTime || 0), 'everything ever put into tracked deals'],
      ['Sales revenue back', moneyExact(s.realizedRevenue || 0), `${s.dealsClosed || 0} closed`],
      ['Won / lost', `${s.dealsProfitable || 0} / ${s.dealsAtALoss || 0}`, 'closed deals that made money, and that didn\'t'],
      ['Median cash cycle', s.medianDaysToCash != null ? `${s.medianDaysToCash} days` : '—',
        s.medianDaysToCash != null ? 'money out to money back' : 'needs three closed deals'],
      ['Projected on the shelf', moneyExact(s.projectedProfitSourced || 0), 'forecast on deals not bought yet'],
      // Scoped to this board on purpose — Money Made owns the count across every sale, and two
      // different numbers under the same label in two places is how a seller stops trusting both.
      ['Board sales missing a cost', String(s.salesAwaitingCost || 0), 'sales on these deals whose profit isn\'t counted yet'],
    ];

    const host = $('dp-stats');
    if (host) host.innerHTML = tiles.map(([label, value, note]) => `
      <div class="er-stat">
        <span class="er-stat-label">${esc(label)}</span>
        <span class="er-stat-value">${esc(value)}</span>
        <span class="er-stat-note">${esc(note)}</span>
      </div>`).join('');
  }

  function renderPipelineActions(actions) {
    const host = $('dp-actions');
    if (!host) return;

    setText('dp-actions-title', actions.length
      ? `${actions.length} thing${actions.length === 1 ? '' : 's'} your money is waiting on`
      : 'Nothing is waiting on you');

    host.innerHTML = actions.map(a => `
      <div class="dp-action dp-action-${esc(a.urgency)}">
        <span class="dp-action-money">${a.amountAtStake > 0 ? money(a.amountAtStake) : ''}</span>
        <span class="dp-action-text">
          <span class="dp-action-label">${esc(a.label)} — ${esc(a.title)}</span>
          <span class="dp-action-detail">${esc(a.detail)}</span>
        </span>
        <span class="dp-action-go">${actionButtonHtml(a)}</span>
      </div>`).join('');
  }

  // The prompt has to land somewhere useful. A repricing nudge belongs in Inventory Health, an
  // unlisted buy belongs in the listing form; only the pipeline's own fixes stay on this page.
  function actionButtonHtml(a) {
    if (a.target === 'inventory')
      return `<button class="btn btn-secondary small dp-go-inventory" type="button">Open Inventory Health</button>`;
    if (a.label === 'Apply what you paid')
      return `<button class="btn btn-primary small dp-apply-cost" type="button" data-id="${a.dealId}">Apply it</button>`;
    const next = DP_NEXT_STAGE[a.stage];
    if (next)
      return `<button class="btn btn-secondary small dp-move" type="button" data-id="${a.dealId}" data-stage="${next}">Mark ${esc(DP_STAGES[next].label.toLowerCase())}</button>`;
    return `<button class="btn btn-ghost small dp-edit" type="button" data-id="${a.dealId}">Open</button>`;
  }

  function renderPipelineBoard(stages, deals) {
    const host = $('dp-board');
    if (!host) return;

    host.innerHTML = stages.map(col => {
      const cards = deals.filter(d => d.stage === col.stage);
      // Money means a different thing per column, and one shared label would be wrong in three of
      // the four: what's at risk on the way in, what came back at the end.
      // Blank on an empty column: the dashed placeholder below already carries the blurb, and
      // printing it twice makes an empty column look like a rendering fault.
      const moneyLine = !col.count ? ''
        : col.stage === 'sold'
        ? (col.realizedProfit ? `${moneyExact(col.realizedProfit)} realized` : `${moneyExact(col.capital)} deployed`)
        : col.capital > 0 ? `${moneyExact(col.capital)} at risk`
        : col.projectedProfit > 0 ? `${moneyExact(col.projectedProfit)} projected`
        : DP_STAGES[col.stage]?.blurb || '';

      return `
        <div class="dp-col dp-col-${esc(col.stage)}">
          <div class="dp-col-head">
            <span class="dp-col-title">${DP_STAGES[col.stage]?.icon || ''} ${esc(col.label)}</span>
            <span class="dp-col-count">${col.count}</span>
          </div>
          <p class="dp-col-money">${esc(moneyLine)}</p>
          <div class="dp-col-body">
            ${cards.length ? cards.map(dealCardHtml).join('')
              : `<p class="dp-col-empty">${esc(DP_STAGES[col.stage]?.blurb || '')}</p>`}
          </div>
        </div>`;
    }).join('');
  }

  function dealCardHtml(c) {
    const d = c.deal || {};
    const next = DP_NEXT_STAGE[c.stage];

    // Projected and realized never share a row and never share a colour. On a closed deal the
    // realized figure leads and the forecast becomes context for it.
    const moneyRows = [];
    if (c.realizedProfit != null)
      moneyRows.push(`<span class="dp-money-row"><span>Made</span><b class="${c.realizedProfit < 0 ? 'dp-loss' : 'dp-realized'}">${moneyExact(c.realizedProfit)}</b></span>`);
    if (c.expectedProfit != null && c.realizedProfit == null)
      moneyRows.push(`<span class="dp-money-row"><span>Projected</span><b class="dp-projected">${moneyExact(c.expectedProfit)}</b></span>`);
    else if (c.forecastProfit != null && c.realizedProfit == null && c.expectedProfit == null)
      moneyRows.push(`<span class="dp-money-row"><span>Projected</span><b class="dp-projected">${moneyExact(c.forecastProfit)}</b></span>`);
    if (c.realizedProfit != null && c.expectedProfit != null)
      moneyRows.push(`<span class="dp-money-row dp-money-quiet"><span>Was projected</span><b>${moneyExact(c.expectedProfit)}</b></span>`);
    if (c.capitalAtRisk > 0)
      moneyRows.push(`<span class="dp-money-row dp-money-quiet"><span>Your cash in it</span><b>${moneyExact(c.capitalAtRisk)}</b></span>`);

    const meta = [
      d.quantity > 1 ? `×${d.quantity}` : '',
      d.sourceLabel || (d.source && d.source !== 'manual' ? d.source : ''),
      c.daysInStage > 0 ? `${c.daysInStage}d in ${esc(c.stageLabel.toLowerCase())}` : 'today',
      c.daysToCashActual != null ? `${c.daysToCashActual}d to cash` : '',
    ].filter(Boolean).join(' · ');

    return `
      <article class="dp-card${c.nextAction ? ` dp-card-${esc(c.nextAction.urgency)}` : ''}" data-id="${d.id}">
        <header class="dp-card-head">
          ${d.sourceUrl
            ? `<a class="dp-card-title" href="${esc(d.sourceUrl)}" target="_blank" rel="noopener">${esc(d.title)} ↗</a>`
            : `<span class="dp-card-title">${esc(d.title)}</span>`}
          <button class="btn btn-ghost small dp-del" type="button" data-id="${d.id}" title="Remove this deal from the board">✕</button>
        </header>
        <p class="dp-card-meta">${esc(meta)}${c.stageAutoDerived ? ' · <span class="dp-auto">moved by an imported sale</span>' : ''}</p>
        <div class="dp-card-money">${moneyRows.join('')}</div>
        ${(c.flags || []).map(f => `<p class="dp-card-flag">${esc(f)}</p>`).join('')}
        ${d.projectedBasis ? `<p class="dp-card-basis">Forecast from ${esc(d.projectedBasis)}</p>` : ''}
        <footer class="dp-card-foot">
          ${next ? `<button class="btn btn-secondary small dp-move" type="button" data-id="${d.id}" data-stage="${next}">${esc(dpMoveLabel(next))}</button>` : ''}
          <button class="btn btn-ghost small dp-edit" type="button" data-id="${d.id}">Edit</button>
          ${c.stage !== 'sold' ? `<button class="btn btn-ghost small dp-drop" type="button" data-id="${d.id}" title="Didn't happen, or written off">Drop</button>` : ''}
        </footer>
      </article>`;
  }

  function dpMoveLabel(stage) {
    return { bought: 'I bought it', listed: 'It\'s listed', sold: 'It sold' }[stage] || 'Move';
  }

  function onPipelineClick(e) {
    const move = e.target.closest?.('.dp-move');
    if (move) { openStageForm(Number(move.dataset.id), move.dataset.stage); return; }

    const drop = e.target.closest?.('.dp-drop');
    if (drop) { openStageForm(Number(drop.dataset.id), 'dropped'); return; }

    const edit = e.target.closest?.('.dp-edit');
    if (edit) { openDealForm(Number(edit.dataset.id)); return; }

    const del = e.target.closest?.('.dp-del');
    if (del) { deleteDeal(Number(del.dataset.id)); return; }

    const apply = e.target.closest?.('.dp-apply-cost');
    if (apply) { applyDealCost(Number(apply.dataset.id)); return; }

    if (e.target.closest?.('.dp-go-inventory')) { location.hash = 'inventory'; return; }
  }

  // ── Add / edit ────────────────────────────────────────────────────────────

  let dealFormId = null;

  function openDealForm(id) {
    dealFormId = id || null;
    const c = id ? (pipeline?.deals || []).find(x => x.id === id) : null;
    const d = c?.deal;

    setText('dp-add-title', d ? 'Edit this deal' : 'Add a deal');
    const save = $('dp-add-save');
    if (save) save.textContent = d ? 'Save changes' : 'Track this deal';

    setVal('dp-f-title', d?.title || '');
    setVal('dp-f-stage', c?.stage === 'sold' || c?.stage === 'dropped' ? 'listed' : (c?.stage || 'sourced'));
    setVal('dp-f-qty', d?.quantity || 1);
    setVal('dp-f-ask', d?.askPrice ?? '');
    setVal('dp-f-paid', d?.purchasePrice ?? '');
    setVal('dp-f-extra', d?.purchaseExtraCost || '');
    setVal('dp-f-resale', d?.projectedSalePrice ?? '');
    setVal('dp-f-listing', d?.listingId || '');
    setVal('dp-f-sku', d?.sku || '');
    setVal('dp-f-url', d?.sourceUrl || '');
    setVal('dp-f-note', d?.note || '');

    // The stage picker is disabled on an existing deal: moving a card is the stage modal's job,
    // which asks for what that move actually needs. Two ways to do it is how one of them ends up
    // recording a purchase with no price.
    const stage = $('dp-f-stage');
    if (stage) stage.disabled = !!d;

    $('dp-add-error')?.classList.add('hidden');
    updateDealPreview();
    loadDealFeeProfile().then(updateDealPreview);
    $('dp-add-modal')?.classList.remove('hidden');
    $('dp-f-title')?.focus();
  }

  function closeDealForm() {
    $('dp-add-modal')?.classList.add('hidden');
    dealFormId = null;
  }

  // The same one-dollar-per-dollar identity the server uses, so the number the seller sees while
  // typing is the number the board will show.
  function updateDealPreview() {
    const qty = Math.max(1, num('dp-f-qty') || 1);
    const ask = num('dp-f-ask');
    const paid = num('dp-f-paid');
    const resale = num('dp-f-resale');
    const extra = num('dp-f-extra') || 0;
    const cost = paid != null ? paid : ask;
    const el = $('dp-add-preview');
    if (!el) return;

    if (resale == null || cost == null) {
      el.textContent = 'Enter what you\'d pay and what it should sell for to see the projected profit.';
      el.className = 'er-log-preview';
      return;
    }

    // Deliberately a rough net, and labelled as one: this form has no comps behind it, so it uses
    // the fee rate from the seller's own Fees & Costs settings and says the number is an estimate.
    const fees = resale * (dealFeeProfile?.ebayFinalValueFeePercent ?? 13.25) / 100
      + (dealFeeProfile?.ebayFinalValueFeeFixed ?? 0.4);
    const net = (resale - cost - fees) * qty - extra;
    el.textContent = `About ${moneyExact(net)} net across ${qty} unit${qty === 1 ? '' : 's'} — ${moneyExact(resale)} sale less ${moneyExact(cost)} cost and roughly ${moneyExact(fees)} of eBay fees${extra ? `, less ${moneyExact(extra)} of extras` : ''}. Estimated from your fee settings, not from sold comps.`;
    el.className = `er-log-preview ${net > 0 ? 'er-preview-good' : 'er-preview-bad'}`;
  }

  async function saveDealForm() {
    const title = ($('dp-f-title')?.value || '').trim();
    if (!title) { showDealError('Give it a name so you can find it on the board.'); return; }

    const stage = dealFormId ? undefined : ($('dp-f-stage')?.value || 'sourced');
    const paid = num('dp-f-paid');
    if (!dealFormId && (stage === 'bought' || stage === 'listed') && paid == null) {
      showDealError('Record what you paid — that\'s the number that tells you what\'s at risk.');
      return;
    }

    const resale = num('dp-f-resale');
    const ask = num('dp-f-ask');
    const qty = Math.max(1, num('dp-f-qty') || 1);
    const cost = paid != null ? paid : ask;
    const fees = resale != null
      ? resale * (dealFeeProfile?.ebayFinalValueFeePercent ?? 13.25) / 100 + (dealFeeProfile?.ebayFinalValueFeeFixed ?? 0.4)
      : null;

    const payload = {
      id: dealFormId || undefined,
      stage,
      title,
      quantity: qty,
      askPrice: ask ?? undefined,
      purchasePrice: paid ?? undefined,
      purchaseExtraCost: num('dp-f-extra') ?? undefined,
      projectedSalePrice: resale ?? undefined,
      // A hand-entered deal has no comps behind it, so the basis says exactly that rather than
      // borrowing the credibility of the sourced-from-comps forecasts on the same board.
      projectedNetProfit: (resale != null && cost != null) ? round2(resale - cost - fees) : undefined,
      projectedBasis: (resale != null && cost != null) ? 'your own estimate, at your fee settings' : undefined,
      listingId: ($('dp-f-listing')?.value || '').trim(),
      sku: ($('dp-f-sku')?.value || '').trim(),
      sourceUrl: ($('dp-f-url')?.value || '').trim(),
      note: ($('dp-f-note')?.value || '').trim(),
    };

    try {
      const res = await fetch('/api/deals', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload),
      });
      if (!res.ok) throw new Error(await res.text());
      pipeline = await res.json();
      closeDealForm();
      renderPipeline();
      renderDashboardPipeline();
      setPipelineNotice('');
      addActivity(dealFormId ? 'Deal updated' : 'Deal tracked', title);
    } catch (e) {
      showDealError(e.message);
    }
  }

  function showDealError(message) {
    const el = $('dp-add-error');
    if (!el) return;
    el.textContent = message;
    el.classList.remove('hidden');
  }

  // ── Moving a card ─────────────────────────────────────────────────────────

  let stageFormId = null;
  let stageFormTarget = null;

  function openStageForm(id, stage) {
    const c = (pipeline?.deals || []).find(x => x.id === id);
    if (!c) return;
    stageFormId = id;
    stageFormTarget = stage;
    const d = c.deal;

    setText('dp-stage-title', `${d.title} → ${DP_STAGES[stage]?.label || 'Dropped'}`);

    const hints = {
      bought: 'What you actually paid is the number everything else on this board is measured against — it sets your capital at risk, your real profit when it sells, and the break-even floor Inventory Health uses.',
      listed: 'The eBay listing ID is what lets the sale find its way back to this deal. Adding it also records what you paid as the listing\'s cost basis, so the profit gets counted in Money Made without you typing the price twice.',
      sold: 'Only for a sale this app can\'t see — a local cash deal, or another marketplace. eBay sales move this card by themselves once they\'re imported into Money Made.',
      dropped: 'Takes it off the board. If you already paid for it, the money stays counted as spent — a write-off is a real loss, not a deal that never happened.',
    };
    setText('dp-stage-hint', hints[stage] || '');

    document.querySelectorAll('.dp-stage-buy').forEach(el => el.classList.toggle('hidden', stage !== 'bought'));
    document.querySelectorAll('.dp-stage-list').forEach(el => el.classList.toggle('hidden', stage !== 'listed'));
    document.querySelectorAll('.dp-stage-sold').forEach(el => el.classList.toggle('hidden', stage !== 'sold'));
    document.querySelectorAll('.dp-stage-drop').forEach(el => el.classList.toggle('hidden', stage !== 'dropped'));

    // Pre-filled from what the deal already knows: the ask is the obvious opening guess at what
    // was paid, and the projected sale price at what it'll be listed for.
    setVal('dp-s-paid', d.purchasePrice ?? d.askPrice ?? '');
    setVal('dp-s-extra', d.purchaseExtraCost || '');
    setVal('dp-s-bought', todayInputValue());
    setVal('dp-s-listing', d.listingId || '');
    setVal('dp-s-sku', d.sku || '');
    setVal('dp-s-price', d.listedPrice ?? d.projectedSalePrice ?? '');
    setVal('dp-s-sold', todayInputValue());
    setVal('dp-s-note', d.note || '');

    const preview = $('dp-stage-preview');
    if (preview) {
      // Says out loud what pressing the button will do to a number somewhere else in the app.
      const willWrite = stage === 'listed' && (d.purchasePrice != null);
      preview.textContent = willWrite
        ? `This will also record ${moneyExact(d.purchasePrice)} as what this listing cost you, which is what makes any sale of it count as real profit.`
        : '';
      preview.classList.toggle('hidden', !willWrite);
    }

    $('dp-stage-error')?.classList.add('hidden');
    $('dp-stage-modal')?.classList.remove('hidden');
  }

  function closeStageForm() {
    $('dp-stage-modal')?.classList.add('hidden');
    stageFormId = null;
    stageFormTarget = null;
  }

  async function saveStageForm() {
    if (!stageFormId || !stageFormTarget) return;
    const payload = { stage: stageFormTarget };

    if (stageFormTarget === 'bought') {
      const paid = num('dp-s-paid');
      if (paid == null) { showStageError('Enter what you paid — that\'s what puts a number on your risk.'); return; }
      payload.purchasePrice = paid;
      payload.purchaseExtraCost = num('dp-s-extra') ?? 0;
      payload.boughtUtc = dateInputToUtc('dp-s-bought');
    } else if (stageFormTarget === 'listed') {
      payload.listingId = ($('dp-s-listing')?.value || '').trim();
      payload.sku = ($('dp-s-sku')?.value || '').trim();
      payload.listedPrice = num('dp-s-price') ?? undefined;
      payload.listedUtc = new Date().toISOString();
    } else if (stageFormTarget === 'sold') {
      payload.soldUtc = dateInputToUtc('dp-s-sold');
    } else if (stageFormTarget === 'dropped') {
      payload.note = ($('dp-s-note')?.value || '').trim();
    }

    try {
      const res = await fetch(`/api/deals/${stageFormId}/stage`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload),
      });
      if (!res.ok) throw new Error(await res.text());
      const result = await res.json();
      pipeline = result.pipeline;
      closeStageForm();
      renderPipeline();
      renderDashboardPipeline();
      setPipelineNotice(result.message || '');
    } catch (e) {
      showStageError(e.message);
    }
  }

  function showStageError(message) {
    const el = $('dp-stage-error');
    if (!el) return;
    el.textContent = message;
    el.classList.remove('hidden');
  }

  async function applyDealCost(id) {
    try {
      const res = await fetch(`/api/deals/${id}/apply-cost`, { method: 'POST' });
      if (!res.ok) throw new Error(await res.text());
      const result = await res.json();
      pipeline = result.pipeline;
      renderPipeline();
      renderDashboardPipeline();
      setPipelineNotice(result.message || '');
      // The money it just unlocked lives on the other page, so that page's copy is now stale.
      loadEarnings(true);
    } catch (e) {
      setPipelineNotice(`Couldn't apply that cost: ${e.message}`);
    }
  }

  async function deleteDeal(id) {
    const c = (pipeline?.deals || []).find(x => x.id === id);
    if (!confirm(`Remove "${c?.deal?.title || 'this deal'}" from the board? Any completed sales stay in Money Made.`)) return;
    try {
      const res = await fetch(`/api/deals/${id}`, { method: 'DELETE' });
      if (!res.ok) throw new Error(await res.text());
      pipeline = await res.json();
      renderPipeline();
      renderDashboardPipeline();
    } catch (e) {
      setPipelineNotice(`Couldn't remove that deal: ${e.message}`);
    }
  }

  // The front-page band. Same rule as the earnings band: hidden until there is real money behind
  // it, because "$0 in motion" on a fresh install reads as the app being broken.
  function renderDashboardPipeline() {
    const band = $('dash-pipeline');
    if (!band) return;
    const s = pipeline?.summary;
    if (!s || (s.capitalAtRisk || 0) <= 0) { band.classList.add('hidden'); return; }

    band.classList.remove('hidden');
    setText('dash-pipeline-figure', moneyExact(s.capitalAtRisk));

    const bits = [`${s.activeDeals} deal${s.activeDeals === 1 ? '' : 's'} in play`];
    if (s.projectedProfitInMotion > 0) bits.push(`${moneyExact(s.projectedProfitInMotion)} projected on it`);
    if (s.realizedProfit > 0) bits.push(`${moneyExact(s.realizedProfit)} already realized`);
    setText('dash-pipeline-sub', bits.join(' · '));

    const top = (pipeline.nextActions || [])[0];
    const host = $('dash-pipeline-next');
    if (host) {
      host.innerHTML = top
        ? `<span class="dash-pipeline-next-label">Next</span><span class="dash-pipeline-next-text">${esc(top.label)} — ${esc(top.title)}</span>`
        : '';
    }
  }

  function num(id) {
    const raw = ($(id)?.value ?? '').trim();
    if (raw === '') return null;
    const value = parseFloat(raw);
    return isNaN(value) ? null : value;
  }

  function round2(value) {
    return Math.round(value * 100) / 100;
  }

  function todayInputValue() {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  // A date input is a local calendar day. Sent as local noon so a UTC conversion can never move it
  // to the day before, which would silently misdate a purchase and skew every day-count on the board.
  function dateInputToUtc(id) {
    const raw = ($(id)?.value || '').trim();
    if (!raw) return undefined;
    const [y, m, d] = raw.split('-').map(Number);
    if (!y || !m || !d) return undefined;
    return new Date(y, m - 1, d, 12, 0, 0).toISOString();
  }

  // ── Rising-Demand / Price-Trend Radar ─────────────────────────────────────
  // Every other screen in this app prices the present tense. This one reads the sold-comps database
  // as a time series and answers "what is on its way up?" — because a sourcer's cheapest margin is
  // buying at last month's price. Everything below comes from /api/trends/radar, which measures the
  // trend itself and then prices the survivors through the same pipeline the rest of the app uses.
  //
  // Two things this UI must never do, both of which would turn it into a horoscope:
  //   * show a trend without showing how much of it was actually measured (the corpus banner, the
  //     sold counts and the tentative badge are all load-bearing, not decoration), and
  //   * let the projected price sit anywhere near the "max to pay" column. What to pay comes from
  //     today's price; the climb is reported separately, as upside.
  let trendScan = null;

  function showTrendsSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('trends-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'trends'));
  }

  function closeTrendsSection() {
    $('trends-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindTrendRadar() {
    on('tr-scan-btn', 'click', () => runTrendScan({ nextSeed: false }));
    on('tr-scan-again', 'click', () => runTrendScan({ nextSeed: true }));
    on('tr-close', 'click', closeTrendsSection);
    on('tr-home', 'click', closeTrendsSection);
    // Direction is a pure view over the scan in hand when we already have everything; it only
    // re-runs when the previous scan was narrowed server-side and the seller now wants it all.
    on('tr-direction', 'change', () => {
      if (!trendScan) return;
      // A scan that already fetched everything can be re-filtered in the browser. Widening one that
      // was narrowed server-side has to go back — but on the SAME seed, so the seller gets the same
      // categories with more of them shown, not a different scan wearing the same question.
      if (trendScan.direction === 'all') renderTrendRows();
      else runTrendScan({ seed: trendScan.seed });
    });
  }

  function setTrendStatus(text) {
    const el = $('tr-status');
    if (el) el.textContent = text || '';
  }

  async function runTrendScan({ nextSeed = false, seed = null } = {}) {
    const btn = $('tr-scan-btn');
    const again = $('tr-scan-again');
    const windowDays = $('tr-window')?.value || '45';
    const niches = $('tr-niches')?.value || '5';
    const direction = $('tr-direction')?.value || 'rising';
    // "Scan different categories" is the seed advancing — the same rotation Roll the Dice uses, so
    // a second scan digs categories the first one never touched instead of repeating itself.
    const useSeed = nextSeed ? trendScan?.nextSeed : seed;
    const seedArg = useSeed != null ? `&seed=${useSeed}` : '';

    if (btn) { btn.disabled = true; btn.textContent = 'Scanning…'; }
    if (again) again.disabled = true;
    setTrendStatus('Reading sold history across several categories…');
    $('tr-results').innerHTML = '<p class="opportunity-empty">Sweeping sold comps, splitting each product\'s sales into two windows, then pricing only the ones that moved. This takes a moment.</p>';

    try {
      const res = await fetch(`/api/trends/radar?window=${windowDays}&niches=${niches}&direction=${direction}${seedArg}`);
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'The scan failed.');
      trendScan = data;
      renderTrendScan();
    } catch (err) {
      trendScan = null;
      $('tr-summary')?.classList.add('hidden');
      $('tr-corpus')?.classList.add('hidden');
      $('tr-warning')?.classList.add('hidden');
      $('tr-more-bar')?.classList.add('hidden');
      $('tr-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The scan failed.')}</p>`;
      setTrendStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '📈 Find What\'s Climbing'; }
      if (again) again.disabled = false;
    }
  }

  function renderTrendScan() {
    if (trendScan.status === 'error') {
      $('tr-summary')?.classList.add('hidden');
      $('tr-corpus')?.classList.add('hidden');
      $('tr-more-bar')?.classList.add('hidden');
      $('tr-results').innerHTML = `<p class="opportunity-empty">${esc(trendScan.error || 'The scan failed.')}</p>`;
      setTrendStatus('');
      return;
    }

    renderTrendCorpus();

    const warn = $('tr-warning');
    if (trendScan.dataWarning && trendScan.status === 'ok') {
      warn.textContent = trendScan.dataWarning;
      warn.classList.remove('hidden');
    } else {
      warn?.classList.add('hidden');
    }

    // A refused scan has no rows and no summary — the banner above IS the answer, and padding it
    // out with zeroed tiles would read as "we looked and found nothing" rather than "we couldn't look".
    if (trendScan.status !== 'ok') {
      $('tr-summary')?.classList.add('hidden');
      $('tr-results').innerHTML = '<p class="opportunity-empty">Nothing can be read as a trend until the sold-comps data above is sorted out.</p>';
      renderTrendMoreBar();
      setTrendStatus('');
      return;
    }

    renderTrendSummary();
    renderTrendRows();
    renderTrendMoreBar();

    const w = trendScan.windowDays;
    setTrendStatus(
      `${trendScan.compsScanned.toLocaleString()} sold comps · ${trendScan.productsMeasured} product${trendScan.productsMeasured === 1 ? '' : 's'} measured · ` +
      `${trendScan.productsRising} climbing · last ${w} days vs the ${w} before`);
  }

  // The scan reporting on its own data. This is the banner that stops the feature lying: if the
  // comps database stopped being updated, every product looks like demand collapsed, and that
  // reads as market news unless it is said plainly and first.
  function renderTrendCorpus() {
    const el = $('tr-corpus');
    const c = trendScan.corpus || {};
    if (!el) return;

    if (!c.isReadable) {
      el.innerHTML = `<strong>This scan can't be read as a trend.</strong> ${esc(trendScan.dataWarning || c.note || '')}`;
      el.classList.remove('hidden');
      return;
    }

    // Only worth the space when the database's own volume moved enough to distort every product's
    // velocity — otherwise it's noise about noise.
    if (c.note) {
      el.innerHTML = `<strong>Baseline:</strong> ${esc(c.note)}`;
      el.classList.remove('hidden');
      return;
    }

    el.classList.add('hidden');
  }

  function renderTrendSummary() {
    const el = $('tr-summary');
    if (!el) return;

    const c = trendScan.corpus || {};
    const rows = trendScan.rows || [];
    const buys = rows.filter(r => r.verdict === 'buy_now');
    const topClimb = rows.reduce((best, r) => {
      const pct = Number(r.trend?.priceChangePercent);
      return Number.isFinite(pct) && pct > best ? pct : best;
    }, 0);

    const tiles = [
      { label: 'Worth buying now', value: String(trendScan.buyNowCount || 0),
        sub: buys.length ? `Climbing, and the money already works at today's price` : 'Nothing cleared both bars this scan',
        tone: trendScan.buyNowCount > 0 ? 'good' : '' },
      { label: 'What the climb is worth', value: money(trendScan.totalTrendHeadroom),
        sub: 'Extra per unit across those buys, if the move holds one more window',
        tone: trendScan.totalTrendHeadroom > 0 ? 'good' : '' },
      { label: 'Biggest move', value: topClimb > 0 ? `+${topClimb.toFixed(1)}%` : '—',
        sub: topClimb > 0 ? `Median sold price, last ${trendScan.windowDays} days vs the ${trendScan.windowDays} before` : 'No product on this board is climbing',
        tone: topClimb >= 15 ? 'good' : '' },
      { label: 'Products climbing', value: `${trendScan.productsRising} of ${trendScan.productsMeasured}`,
        sub: 'Measured across the categories this scan swept' },
      { label: 'Evidence behind it', value: `${(c.datedComps || 0).toLocaleString()} dated sales`,
        sub: `${c.datedCoveragePercent || 0}% of the comps scanned carry a sale date`,
        tone: (c.datedCoveragePercent || 0) < 60 ? 'warn' : '' },
      { label: 'Freshest sale', value: c.newestCompAgeDays == null ? '—' : `${c.newestCompAgeDays}d ago`,
        sub: 'How current the comps database is — everything above rests on this',
        tone: (c.newestCompAgeDays ?? 99) > 14 ? 'warn' : '' },
    ];

    el.innerHTML = tiles.map(t => `
      <div class="inv-tile ${t.tone ? 'inv-tile-' + t.tone : ''}">
        <div class="inv-tile-label">${esc(t.label)}</div>
        <div class="inv-tile-value">${esc(t.value)}</div>
        <div class="inv-tile-sub">${esc(t.sub)}</div>
      </div>`).join('');
    el.classList.remove('hidden');
  }

  function renderTrendMoreBar() {
    const bar = $('tr-more-bar');
    const note = $('tr-more-note');
    if (!bar || !note) return;

    const names = (trendScan.niches || []).map(n => n.label).join(', ');
    note.textContent = names
      ? `Swept ${names}. ${trendScan.rollsToCoverEverything} scan${trendScan.rollsToCoverEverything === 1 ? '' : 's'} covers all ${trendScan.nichesInUniverse} categories.`
      : 'Scan again to sweep different categories.';
    bar.classList.remove('hidden');
  }

  const TREND_VERDICTS = {
    buy_now:      { label: '🔥 Buy now',      cls: 'tr-v-buy' },
    get_in_early: { label: '🌱 Get in early', cls: 'tr-v-early' },
    watch:        { label: '👀 Watch',        cls: 'tr-v-watch' },
    thin:         { label: '⚠️ Thin data',    cls: 'tr-v-thin' },
    pass:         { label: '✕ Pass',          cls: 'tr-v-pass' },
  };

  const TREND_SIGNALS = {
    rising_demand:   { label: 'Rising demand',   cls: 'tr-s-up' },
    price_climbing:  { label: 'Price climbing',  cls: 'tr-s-up' },
    demand_building: { label: 'Volume building', cls: 'tr-s-early' },
    supply_squeeze:  { label: 'Supply squeeze',  cls: 'tr-s-squeeze' },
    steady:          { label: 'Flat',            cls: 'tr-s-flat' },
    cooling:         { label: 'Cooling',         cls: 'tr-s-down' },
    unreadable:      { label: 'Not readable',    cls: 'tr-s-flat' },
  };

  function renderTrendRows() {
    if (!trendScan) return;
    const showAll = ($('tr-direction')?.value || 'rising') === 'all';
    const rows = (trendScan.rows || []).filter(r => showAll || r.trend?.isRising);
    const el = $('tr-results');

    if (rows.length === 0) {
      el.innerHTML = `<p class="opportunity-empty">${esc(
        trendScan.dataWarning ||
        'Nothing in these categories is climbing right now. That is a real answer — scan again to sweep different ones.')}</p>`;
      return;
    }

    el.innerHTML = `
      <table class="inv-table tr-table">
        <thead>
          <tr>
            <th>Product</th>
            <th>Trend</th>
            <th class="num">Sold price</th>
            <th class="num">Sales</th>
            <th class="num">Max to pay today</th>
            <th class="num">Buy under</th>
            <th class="num">Upside if it holds</th>
            <th>Verdict</th>
          </tr>
        </thead>
        <tbody>${rows.map(trendRowHtml).join('')}</tbody>
      </table>`;

    el.querySelectorAll('.tr-hunt-btn').forEach(btn =>
      btn.addEventListener('click', () => huntTrendProduct(btn.dataset.query)));
  }

  function trendRowHtml(row) {
    const t = row.trend || {};
    const v = TREND_VERDICTS[row.verdict] || TREND_VERDICTS.watch;
    const s = TREND_SIGNALS[t.signal] || TREND_SIGNALS.unreadable;
    const pct = Number(t.priceChangePercent);
    const pctText = Number.isFinite(pct) ? `${pct > 0 ? '+' : ''}${pct.toFixed(1)}%` : '—';
    const rel = Number(t.relativeVelocityChangePercent);

    // "Tentative" is shown, never hidden — it is the difference between a measurement and a guess,
    // and the seller is about to spend money on which one this is.
    const tentative = t.reliability === 'tentative'
      ? '<span class="tr-tentative" title="Measured, but on thin or noisy evidence — see the note below.">tentative</span>' : '';

    const projected = t.projectedPrice != null
      ? `<div class="tr-sub">${money(t.projectedPrice)} if it holds</div>` : '';

    return `
      <tr class="tr-row ${row.verdict === 'buy_now' ? 'tr-row-buy' : ''}">
        <td class="tr-product">
          <div class="tr-name">${esc(row.product)}</div>
          <div class="tr-sub">${esc(row.nicheLabel)} · ${esc(row.resaleSource)} · ${row.soldCompCount + row.terapeakCompCount} comps · ${esc(row.confidenceLevel)}</div>
          <div class="tr-note">${esc(row.verdictNote)}</div>
          <div class="tr-note tr-note-quiet">${esc(t.note || '')}</div>
        </td>
        <td class="tr-trend">
          <span class="tr-signal ${s.cls}">${esc(s.label)}</span>${tentative}
          ${trendSparkline(t)}
          <div class="tr-sub">${esc(pctText)} median${t.slopePerMonth ? ` · ${Number(t.slopePerMonth) > 0 ? '+' : ''}${money(t.slopePerMonth)}/mo trend` : ''}</div>
        </td>
        <td class="num">
          <div class="tr-strong">${money(row.ebayExpectedSale ?? row.ebayMedian)}</div>
          ${projected}
        </td>
        <td class="num">
          <div>${t.prior?.soldCount ?? 0} → ${t.recent?.soldCount ?? 0}</div>
          <div class="tr-sub">${Number.isFinite(rel) ? `${rel > 0 ? '+' : ''}${rel.toFixed(0)}% vs scan` : 'no baseline'}</div>
        </td>
        <td class="num"><div class="tr-strong">${money(row.maxBuyToday)}</div><div class="tr-sub">break-even today</div></td>
        <td class="num">${row.targetBuyPrice > 0
          ? `<div class="tr-strong">${money(row.targetBuyPrice)}</div><div class="tr-sub">${money(row.profitAtTarget)} net · ${Number(row.marginAtTargetPercent).toFixed(0)}%</div>` +
            // A climbing price is worth less if the money is stuck for five months getting it.
            (row.daysToCash != null
              ? `<div class="tr-sub"><span class="speed-days speed-${(SPEED_TIERS[row.speedTier] || SPEED_TIERS.unknown).cls}" title="${esc(row.speedNote || '')}">${row.daysToCash}d to cash</span>` +
                `${row.profitPerDay > 0 ? ` <span class="speed-rate">${perDay(row.profitPerDay)}</span>` : ''}</div>`
              : '')
          : '<span class="tr-sub">no price clears the bar</span>'}</td>
        <td class="num">${row.trendHeadroom > 0
          ? `<div class="tr-strong tr-up">+${money(row.trendHeadroom)}</div><div class="tr-sub">per unit</div>`
          : '<span class="tr-sub">—</span>'}</td>
        <td>
          <span class="tr-verdict ${v.cls}">${esc(v.label)}</span>
          <button class="btn btn-secondary small tr-hunt-btn" type="button" data-query="${esc(row.searchQuery)}">Find one</button>
        </td>
      </tr>`;
  }

  // Weekly medians, oldest left. Weeks with no sale are gaps in the line rather than points on it —
  // joining straight through them would draw a steady seller out of an intermittent one.
  function trendSparkline(trend) {
    const points = Array.isArray(trend?.series) ? trend.series : [];
    const priced = points.filter(p => p.soldCount > 0 && p.medianPrice > 0);
    if (priced.length < 2) return '<div class="tr-spark-empty">not enough weeks to draw</div>';

    const w = 108, h = 28, pad = 2;
    const values = priced.map(p => Number(p.medianPrice));
    const min = Math.min(...values), max = Math.max(...values);
    const span = max - min || 1;
    const step = points.length > 1 ? (w - pad * 2) / (points.length - 1) : 0;

    const xy = p => {
      const i = points.indexOf(p);
      const x = pad + i * step;
      const y = h - pad - ((Number(p.medianPrice) - min) / span) * (h - pad * 2);
      return [x.toFixed(1), y.toFixed(1)];
    };

    // One polyline per unbroken run of weeks that had sales.
    const runs = [];
    let run = [];
    points.forEach(p => {
      if (p.soldCount > 0 && p.medianPrice > 0) run.push(p);
      else { if (run.length) runs.push(run); run = []; }
    });
    if (run.length) runs.push(run);

    const lines = runs.filter(r => r.length > 1)
      .map(r => `<polyline points="${r.map(p => xy(p).join(',')).join(' ')}" />`).join('');
    const dots = priced.map(p => { const [x, y] = xy(p); return `<circle cx="${x}" cy="${y}" r="1.6" />`; }).join('');
    // Coloured from the server's signal, not re-derived from first-vs-last here — the picture has
    // to agree with the badge sitting beside it. A supply squeeze drawn in "rising green" because
    // its newest week happened to be high is the picture arguing with the sentence.
    const tone = (TREND_SIGNALS[trend.signal] || TREND_SIGNALS.unreadable).cls.replace('tr-s-', 'tr-spark-');

    return `<svg class="tr-spark ${tone}" viewBox="0 0 ${w} ${h}" width="${w}" height="${h}" role="img"
      aria-label="Weekly median sold price, ${money(min)} to ${money(max)}">${lines}${dots}</svg>`;
  }

  // Hands the product straight to Local Deals — the radar says WHAT to buy, that screen finds WHERE.
  // The keyword is the same short query the server built, so the two screens search the same thing.
  function huntTrendProduct(query) {
    if (!query) return;
    closeTrendsSection();
    showOpportunitySection();
    const box = $('fb-query-input');
    if (box) {
      box.value = query;
      box.scrollIntoView({ behavior: 'smooth', block: 'center' });
      box.focus();
    }
  }

  // ── Where to Sell Highest ─────────────────────────────────────────────────
  // Off /api/where-to-sell. The whole screen is one comparison, so the rules it renders by are
  // about not overstating it:
  //   * the evidence word ("sold" vs "asking") is on every price, never a footnote — they are not
  //     the same kind of number and the seller is deciding on the difference;
  //   * a venue with thin evidence is drawn as thin even when its number is the biggest on screen;
  //   * a venue that could not be searched shows why, rather than an empty column that reads as a
  //     venue which lost;
  //   * "the price it has to fetch to beat eBay" is shown everywhere, including where there is no
  //     price data at all — it is fee arithmetic and it is always true.
  let wtsReport = null;

  function showWhereToSellSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('wts-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'wheretosell'));
  }

  function closeWhereToSellSection() {
    $('wts-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindWhereToSell() {
    on('wts-run-btn', 'click', runWhereToSell);
    on('wts-close', 'click', closeWhereToSellSection);
    on('wts-home', 'click', closeWhereToSellSection);
    on('wts-query', 'keydown', e => { if (e.key === 'Enter') runWhereToSell(); });
    on('wts-zip', 'keydown', e => { if (e.key === 'Enter') runWhereToSell(); });

    // The same remembered zip and radius every other local search uses — a seller types their zip
    // code once for the whole app, not once per screen.
    const zip = localStorage.getItem('fbZip');
    const radius = localStorage.getItem('fbRadius');
    if (zip && $('wts-zip')) $('wts-zip').value = zip;
    if (radius && $('wts-radius')) $('wts-radius').value = radius;
  }

  function setWtsStatus(text) {
    const el = $('wts-status');
    if (el) el.textContent = text || '';
  }

  async function runWhereToSell() {
    const query = $('wts-query')?.value.trim() || '';
    const zip = $('wts-zip')?.value.trim() || '';
    const radius = $('wts-radius')?.value || '40';
    const cost = $('wts-cost')?.value.trim() || '';
    const btn = $('wts-run-btn');

    if (!query) {
      setWtsStatus('Tell me what the item is first.');
      $('wts-query')?.focus();
      return;
    }
    if (zip) localStorage.setItem('fbZip', zip);
    localStorage.setItem('fbRadius', radius);

    const qs = `q=${encodeURIComponent(query)}&zip=${encodeURIComponent(zip)}&radius=${encodeURIComponent(radius)}` +
      (cost ? `&cost=${encodeURIComponent(cost)}` : '');

    if (btn) { btn.disabled = true; btn.textContent = 'Comparing…'; }
    setWtsStatus(zip ? `Pricing on eBay, then searching within ${radius} miles of ${zip}…`
                     : 'Pricing on eBay… add a zip code to price the local venues too.');
    $('wts-banner')?.classList.add('hidden');
    $('wts-warnings')?.classList.add('hidden');
    $('wts-results').innerHTML = '<p class="opportunity-empty">Reading sold history, then what people near you are asking. The local sites are searched one at a time, so give it a moment.</p>';

    try {
      const res = await fetch(`/api/where-to-sell?${qs}`);
      const data = await res.json();
      if (!res.ok) throw new Error(data?.error || 'The comparison failed.');
      wtsReport = data;
      renderWhereToSell();
    } catch (err) {
      wtsReport = null;
      $('wts-banner')?.classList.add('hidden');
      $('wts-warnings')?.classList.add('hidden');
      $('wts-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The comparison failed.')}</p>`;
      setWtsStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '🧭 Compare Venues'; }
    }
  }

  const WTS_VERDICTS = {
    best:        { label: '🏆 Sell it here', cls: 'wts-v-best' },
    close:       { label: 'About the same',  cls: 'wts-v-close' },
    lower:       { label: 'Pays less',       cls: 'wts-v-lower' },
    thin:        { label: '⚠️ Thin data',    cls: 'wts-v-thin' },
    no_data:     { label: 'No price data',   cls: 'wts-v-none' },
    unavailable: { label: 'Not searched',    cls: 'wts-v-none' },
  };

  const WTS_EVIDENCE = {
    sold:   { label: 'what buyers PAID',   cls: 'wts-e-sold' },
    asking: { label: 'what sellers ASK',   cls: 'wts-e-ask' },
    none:   { label: 'no price data',      cls: 'wts-e-none' },
  };

  function renderWhereToSell() {
    if (!wtsReport) return;

    if (wtsReport.status === 'error') {
      $('wts-banner')?.classList.add('hidden');
      $('wts-results').innerHTML = `<p class="opportunity-empty">${esc(wtsReport.error || 'The comparison failed.')}</p>`;
      setWtsStatus('');
      return;
    }

    renderWtsBanner();
    renderWtsWarnings();
    renderWtsVenues();

    const priced = (wtsReport.venues || []).filter(v => v.netProceeds != null).length;
    setWtsStatus(`${priced} of ${(wtsReport.venues || []).length} venues priced` +
      (wtsReport.zipCode ? ` · within ${wtsReport.radiusMiles} miles of ${wtsReport.zipCode}` : ' · no zip, so eBay only'));
  }

  // The one sentence the screen exists for, with the money in it.
  function renderWtsBanner() {
    const el = $('wts-banner');
    if (!el) return;

    const tone = { move: 'wts-banner-move', stay_on_ebay: 'wts-banner-stay',
                   too_close: 'wts-banner-close', no_data: 'wts-banner-none' }[wtsReport.verdict] || '';

    // Only a real, material gap gets the money strip — a two-dollar edge dressed up as a headline
    // figure is exactly how this feature would start lying.
    const extra = wtsReport.verdict === 'move' && wtsReport.extraVsEbay > 0
      ? `<div class="wts-banner-extra"><span class="wts-extra-value">+${moneyExact(wtsReport.extraVsEbay)}</span>
           <span class="wts-extra-label">more on this one item</span></div>`
      : '';

    el.className = `wts-banner ${tone}`;
    el.innerHTML = `
      <div class="wts-banner-text">
        <div class="wts-banner-head">${esc(wtsReport.headline || '')}</div>
        <div class="wts-banner-sub">${esc(wtsReport.subhead || '')}</div>
      </div>
      ${extra}`;
    el.classList.remove('hidden');
  }

  function renderWtsWarnings() {
    const el = $('wts-warnings');
    if (!el) return;
    const warnings = wtsReport.warnings || [];
    if (warnings.length === 0) { el.classList.add('hidden'); return; }

    el.innerHTML = warnings.map(w => `<div>${esc(w)}</div>`).join('');
    el.classList.remove('hidden');
  }

  function renderWtsVenues() {
    const el = $('wts-results');
    const venues = wtsReport.venues || [];
    if (venues.length === 0) {
      el.innerHTML = '<p class="opportunity-empty">Nothing to compare.</p>';
      return;
    }
    el.innerHTML = `<div class="wts-grid">${venues.map(wtsVenueHtml).join('')}</div>`;
  }

  function wtsVenueHtml(v) {
    const verdict = WTS_VERDICTS[v.verdict] || WTS_VERDICTS.no_data;
    const evidence = WTS_EVIDENCE[v.evidenceKind] || WTS_EVIDENCE.none;

    // The headline number is the take-home, never the price. The price is shown under it, as the
    // input it is.
    const takeHome = v.netProceeds != null
      ? `<div class="wts-take">${moneyExact(v.netProceeds)}</div>
         <div class="wts-take-label">${wtsReport.hasCostBasis && v.netProfit != null
            ? `${moneyExact(v.netProfit)} profit after what you paid`
            : 'in your pocket'}</div>`
      : `<div class="wts-take wts-take-none">—</div><div class="wts-take-label">not priced here</div>`;

    const gap = v.venue !== 'ebay' && v.netVsEbay != null
      ? `<div class="wts-gap ${v.netVsEbay > 0 ? 'wts-gap-up' : 'wts-gap-down'}">${v.netVsEbay > 0 ? '+' : ''}${moneyExact(v.netVsEbay)} vs eBay</div>`
      : '';

    const price = v.expectedPrice != null
      ? `<div class="wts-line"><span>Likely sale price</span><strong>${moneyExact(v.expectedPrice)}</strong></div>` +
        (v.buyerPaidShipping > 0
          ? `<div class="wts-line wts-line-quiet"><span>Buyer also pays shipping</span><span>${moneyExact(v.buyerPaidShipping)}</span></div>` : '')
      : '';

    const costs = (v.costs || []).filter(c => c.amount > 0);
    const costLines = v.netProceeds != null
      ? (costs.length
          ? costs.map(c => `<div class="wts-line wts-line-cost" title="${esc(c.detail)}"><span>${esc(c.label)}</span><span>−${moneyExact(c.amount)}</span></div>`).join('')
          : '<div class="wts-line wts-line-free"><span>Taken out of the sale</span><strong>nothing</strong></div>')
      : '';

    const beat = v.priceToBeatEbay != null
      ? `<div class="wts-beat" title="${esc(v.priceToBeatEbayNote || '')}">Beat eBay here at <strong>${moneyExact(v.priceToBeatEbay)}</strong></div>`
      : '';

    // A venue with no price has no speed either — an "unknown" clock under an empty column is
    // noise pretending to be a measurement.
    const speed = v.netProceeds == null ? ''
      : v.daysToCash != null
        ? `<div class="wts-speed" title="${esc(v.speedNote || '')}">${v.daysToCash}d to cash${v.netPerDay > 0 ? ` · ${perDay(v.netPerDay)}` : ''}</div>`
        : `<div class="wts-speed wts-speed-quiet" title="${esc(v.speedNote || '')}">${esc(v.speedLabel || 'Speed unknown')}</div>`;

    const evidenceBits = [
      v.sampleCount > 0 ? `${v.sampleCount} ${v.evidenceKind === 'sold' ? 'sold' : 'listed'}` : null,
      v.confidenceLevel && v.sampleCount > 0 ? v.confidenceLevel : null,
    ].filter(Boolean).join(' · ');

    return `
      <div class="wts-card ${v.verdict === 'best' ? 'wts-card-best' : ''} ${v.netProceeds == null ? 'wts-card-quiet' : ''}">
        <div class="wts-card-head">
          <div>
            <div class="wts-venue">${esc(v.venueLabel)}</div>
            <div class="wts-mode">${esc(v.saleModeLabel)}</div>
          </div>
          <span class="wts-verdict ${verdict.cls}">${esc(verdict.label)}</span>
        </div>

        <div class="wts-money">
          ${takeHome}
          ${gap}
        </div>

        <div class="wts-evidence">
          <span class="wts-ev ${evidence.cls}">${esc(evidence.label)}</span>
          ${evidenceBits ? `<span class="wts-ev-sub">${esc(evidenceBits)}</span>` : ''}
        </div>

        <div class="wts-lines">
          ${price}
          ${costLines}
        </div>

        ${beat}
        <div class="wts-note">${esc(v.priceBasis || v.note || '')}</div>
        ${speed}
        <div class="wts-fee-note">${esc(v.feeNote || '')}</div>
        ${v.createUrl ? `<a class="btn btn-secondary small wts-go" href="${esc(v.createUrl)}" target="_blank" rel="noopener noreferrer">List it on ${esc(v.venueLabel)} ↗</a>` : ''}
      </div>`;
  }

  // ── The Undervalued-Auction Sniper ────────────────────────────────────────
  // Every other sourcing screen sends the seller somewhere else to buy. This one buys on eBay and
  // sells on eBay, hours apart, off /api/snipes — which prices live listings against the same sold
  // comps the rest of the app uses and returns the one number a bidder needs: the most to bid.
  //
  // Three things this UI must never do, all of which would make it a slot machine:
  //   * show profit at a current bid as though it were profit. The board leads with the ceiling and
  //     what winning THERE pays, because that is the worst case of a bid you'd actually place;
  //   * let a "too early" row look like a live one. Their prices aren't real yet, they carry no
  //     money into any total, and the badge says so;
  //   * hide the reasons something is cheap. The risks list is rendered in full on every row.
  let snipeScan = null;
  let snipeTicker = null;

  function showSnipeSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('snipe-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'snipe'));
    startSnipeTicker();
  }

  function closeSnipeSection({ back = true } = {}) {
    $('snipe-section')?.classList.add('hidden');
    stopSnipeTicker();
    if (back) showDashboard();
  }

  function bindSniper() {
    on('sn-scan-btn', 'click', () => runSnipeScan());
    on('sn-close', 'click', () => closeSnipeSection());
    on('sn-home', 'click', () => closeSnipeSection());
    on('dash-snipe-open', 'click', () => { location.hash = 'snipe'; });
    // Ranking is the server's, so re-ordering means asking it again rather than re-implementing the
    // rules here — two sort orders that disagree is two answers to "what should I bid on first".
    on('sn-sort', 'change', () => { if (snipeScan) runSnipeScan(); });
    on('sn-mode', 'change', () => { if (snipeScan) runSnipeScan(); });
    on('sn-terms', 'keydown', e => { if (e.key === 'Enter') runSnipeScan(); });
  }

  function setSnipeStatus(text) {
    const el = $('sn-status');
    if (el) el.textContent = text || '';
  }

  async function runSnipeScan() {
    const btn = $('sn-scan-btn');
    const terms = ($('sn-terms')?.value || '').trim();
    const mode = $('sn-mode')?.value || 'auctions';
    const sort = $('sn-sort')?.value || 'urgency';

    if (btn) { btn.disabled = true; btn.textContent = 'Scanning…'; }
    setSnipeStatus(terms ? 'Searching eBay and pricing what comes back…' : 'Working out what you already sell, then searching eBay…');
    $('sn-results').innerHTML = '<p class="opportunity-empty">Searching live listings, then pricing each one against real sold comps. This takes a moment.</p>';

    try {
      const q = terms ? `&q=${encodeURIComponent(terms)}` : '';
      const res = await fetch(`/api/snipes?mode=${encodeURIComponent(mode)}&sort=${encodeURIComponent(sort)}${q}`);
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'The scan failed.');
      snipeScan = data;
      renderSnipeScan();
    } catch (err) {
      snipeScan = null;
      ['sn-summary', 'sn-warning', 'sn-terms-bar', 'sn-honesty'].forEach(id => $(id)?.classList.add('hidden'));
      $('sn-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The scan failed.')}</p>`;
      setSnipeStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '🎯 Find Underpriced Auctions'; }
    }
  }

  function renderSnipeScan() {
    if (!snipeScan) return;

    if (snipeScan.status === 'error') {
      ['sn-summary', 'sn-terms-bar'].forEach(id => $(id)?.classList.add('hidden'));
      $('sn-results').innerHTML = `<p class="opportunity-empty">${esc(snipeScan.error || 'The scan failed.')}</p>`;
      renderSnipeHonesty();
      setSnipeStatus('');
      return;
    }

    const warn = $('sn-warning');
    if (snipeScan.dataWarning) {
      warn.textContent = snipeScan.dataWarning;
      warn.classList.remove('hidden');
    } else {
      warn?.classList.add('hidden');
    }

    // Nothing to hunt for is not an empty board — it is a board that hasn't been told what to look
    // at, and the fix is one sentence long. Zeroed tiles above it would read as "we looked".
    if (snipeScan.status === 'no_terms') {
      $('sn-summary')?.classList.add('hidden');
      $('sn-terms-bar')?.classList.add('hidden');
      $('sn-results').innerHTML = '<p class="opportunity-empty">Type what you\'re hunting for above, or import your eBay sales in Money Made — this hunts the products you\'ve already sold.</p>';
      renderSnipeHonesty();
      setSnipeStatus('');
      return;
    }

    renderSnipeSummary();
    renderSnipeTerms();
    renderSnipeRows();
    renderSnipeHonesty();
    renderDashboardSnipes();
    startSnipeTicker();

    const s = snipeScan.summary || {};
    setSnipeStatus(
      `${s.listingsScanned || 0} live listing${s.listingsScanned === 1 ? '' : 's'} across ${s.termsScanned || 0} ` +
      `search${s.termsScanned === 1 ? '' : 'es'} · ${s.listingsRejected || 0} dropped as not the same item · ` +
      `${s.snipeCount || 0} worth bidding on`);
  }

  function renderSnipeSummary() {
    const el = $('sn-summary');
    if (!el) return;
    const s = snipeScan.summary || {};

    const tiles = [
      { label: 'Worth bidding on now', value: String(s.snipeCount || 0),
        sub: s.snipeCount ? 'Priced under market, and close enough to the end that the price is real' : 'Nothing live cleared the bar this scan',
        tone: s.snipeCount > 0 ? 'good' : '' },
      { label: 'If you won them all at your ceiling', value: money(s.profitAtCeilings),
        sub: 'An upper bound on what is on the board right now — it falls every time somebody bids',
        tone: s.profitAtCeilings > 0 ? 'good' : '' },
      { label: 'Best single row', value: money(s.bestProfit),
        sub: 'Net profit at that row\'s max bid, after every fee' },
      { label: 'Cash to win all of them', value: money(s.capitalToWinAll),
        sub: 'What bidding to every ceiling would cost, shipping included' },
      { label: 'Closing within the hour', value: String(s.closingWithinTheHour || 0),
        sub: s.nextEndUtc ? `Soonest closes ${snipeClock(s.nextEndUtc)}` : 'Nothing on the clock right now',
        tone: s.closingWithinTheHour > 0 ? 'warn' : '' },
      { label: 'Too early to price', value: String(s.tooEarlyCount || 0),
        sub: `Auctions more than ${snipeScan.priceIsRealHours}h out — their prices aren't real yet` },
    ];

    el.innerHTML = tiles.map(t => `
      <div class="inv-tile ${t.tone ? 'inv-tile-' + t.tone : ''}">
        <div class="inv-tile-label">${esc(t.label)}</div>
        <div class="inv-tile-value">${esc(t.value)}</div>
        <div class="inv-tile-sub">${esc(t.sub)}</div>
      </div>`).join('');
    el.classList.remove('hidden');
  }

  // What was actually searched, and why. When the terms came from the seller's own sales this is
  // the most persuasive line on the page — the board is hunting things they have already sold.
  function renderSnipeTerms() {
    const el = $('sn-terms-bar');
    if (!el) return;
    const terms = snipeScan.terms || [];
    if (terms.length === 0) { el.classList.add('hidden'); return; }

    const chips = terms.map(t => {
      const detail = t.error
        ? `couldn't be searched: ${t.error}`
        : !t.priced ? 'no sold history to price it'
        : `${t.kept} kept${t.listingsRejected ? `, ${t.listingsRejected} dropped` : ''}`;
      return `<span class="sn-chip ${t.error || !t.priced ? 'sn-chip-quiet' : ''}" title="${esc(detail)}">
        <strong>${esc(t.term)}</strong><span class="sn-chip-sub">${esc(t.reason)} · ${esc(detail)}</span></span>`;
    }).join('');

    el.innerHTML = `<span class="sn-terms-label">${snipeScan.termsWereTyped ? 'Searched' : 'Hunting what you\'ve already sold'}</span>${chips}`;
    el.classList.remove('hidden');
  }

  function renderSnipeHonesty() {
    const el = $('sn-honesty');
    if (!el) return;
    const lines = snipeScan?.honesty || [];
    if (lines.length === 0) { el.classList.add('hidden'); return; }
    el.innerHTML = lines.map(l => `<li>${esc(l)}</li>`).join('');
    el.classList.remove('hidden');
  }

  const SNIPE_VERDICTS = {
    snipe:     { label: '🎯 Bid on it',   cls: 'sn-v-snipe' },
    too_early: { label: '⏳ Too early',   cls: 'sn-v-early' },
    watch:     { label: '👀 Watch',       cls: 'sn-v-watch' },
    thin:      { label: '⚠️ Thin data',   cls: 'sn-v-thin' },
    pass:      { label: '✕ Pass',         cls: 'sn-v-pass' },
    ended:     { label: '· Ended',        cls: 'sn-v-pass' },
    no_data:   { label: '? No comps',     cls: 'sn-v-pass' },
  };

  function renderSnipeRows() {
    const el = $('sn-results');
    const rows = snipeScan.candidates || [];

    if (rows.length === 0) {
      el.innerHTML = `<p class="opportunity-empty">${esc(
        snipeScan.dataWarning ||
        'Nothing live is priced under what these sell for right now. That is the normal answer most of the time.')}</p>`;
      return;
    }

    el.innerHTML = `
      <table class="inv-table sn-table">
        <thead>
          <tr>
            <th>Listing</th>
            <th>Closes</th>
            <th class="num">Price now</th>
            <th class="num">Sells for</th>
            <th class="num">Your max bid</th>
            <th class="num">If you win there</th>
            <th>Verdict</th>
            <th>Track</th>
          </tr>
        </thead>
        <tbody>${rows.map(snipeRowHtml).join('')}</tbody>
      </table>`;

    el.querySelectorAll('.sn-track-btn').forEach(btn =>
      btn.addEventListener('click', () => trackSnipeRow(btn.dataset.itemId, btn)));
    updateSnipeClocks();
  }

  function snipeRowHtml(row) {
    const v = SNIPE_VERDICTS[row.verdict] || SNIPE_VERDICTS.no_data;
    const comps = (row.soldCompCount || 0) + (row.terapeakCompCount || 0);
    const speed = SPEED_TIERS[row.speedTier] || SPEED_TIERS.unknown;

    const risks = (row.risks || []).length
      ? `<ul class="sn-risks">${row.risks.map(r => `<li>${esc(r)}</li>`).join('')}</ul>` : '';

    // The countdown is the only thing on this board that changes while you look at it, so it ticks
    // in the browser rather than being frozen at whatever the server said a minute ago.
    const clock = row.endUtc
      ? `<div class="sn-clock sn-clock-${esc(row.timeTier)}" data-ends="${esc(row.endUtc)}">${esc(snipeClock(row.endUtc))}</div>`
      : '<div class="sn-clock sn-clock-none">Buy It Now</div>';

    const bids = row.buyingOption === 'AUCTION'
      ? `<div class="sn-sub">${row.bidCount || 0} bid${row.bidCount === 1 ? '' : 's'}</div>` : '';

    const snipeAt = row.snipeAtUtc && row.verdict === 'snipe'
      ? `<div class="sn-sub sn-snipe-at">bid at ${esc(snipeLocalTime(row.snipeAtUtc))}</div>` : '';

    const headroom = row.bidHeadroom != null && row.buyingOption === 'AUCTION'
      ? `<div class="sn-sub ${row.bidHeadroom >= 0 ? '' : 'sn-over'}">${row.bidHeadroom >= 0
          ? `${money(row.bidHeadroom)} of room left`
          : `${money(Math.abs(row.bidHeadroom))} past it`}</div>`
      : '';

    const discount = row.discountPercent != null
      ? `<div class="sn-sub">${row.discountPercent > 0
          ? `${Number(row.discountPercent).toFixed(0)}% under market`
          : `${Number(Math.abs(row.discountPercent)).toFixed(0)}% over market`}</div>`
      : '';

    const shipping = row.shippingCost > 0
      ? `<div class="sn-sub">+ ${money(row.shippingCost)} shipping</div>`
      : row.shippingStated ? '<div class="sn-sub">free shipping</div>' : '<div class="sn-sub sn-over">shipping not stated</div>';

    // Only rows with a real price and real evidence are worth freezing a forecast against — see
    // the Deal Pipeline, which grades every forecast it is given against what actually happened.
    const track = row.verdict === 'snipe' || row.verdict === 'too_early'
      ? `<button class="btn btn-secondary small sn-track-btn" type="button" data-item-id="${esc(row.itemId)}">＋ Track</button>`
      : '<span class="sn-sub">—</span>';

    return `
      <tr class="sn-row ${row.verdict === 'snipe' ? 'sn-row-snipe' : ''}">
        <td class="sn-item">
          <a class="sn-name" href="${esc(row.url)}" target="_blank" rel="noopener">${esc(row.title)}</a>
          <div class="sn-sub">${esc(row.condition || 'condition not stated')} · seller ${esc(row.sellerUsername || '—')} (${row.sellerFeedbackScore || 0})</div>
          <div class="sn-sub">priced as “${esc(row.pricedAs)}” · ${comps} sold comp${comps === 1 ? '' : 's'} · ${esc(row.confidenceLevel)}${row.pricedPerItem ? ' · <span class="sn-tag">priced off this listing</span>' : ''}</div>
          <div class="sn-note">${esc(row.verdictNote)}</div>
          ${risks}
        </td>
        <td class="sn-time">${clock}${bids}${snipeAt}</td>
        <td class="num">
          <div class="sn-strong">${money(row.currentPrice)}</div>
          ${shipping}
        </td>
        <td class="num">
          <div class="sn-strong">${money(row.expectedSale ?? row.marketMedian)}</div>
          ${discount}
        </td>
        <td class="num">
          ${row.maxBid > 0
            // To the cent, deliberately. money() rounds to whole dollars, which would print a
            // $133.65 ceiling as $134 — a number above the ceiling, in the one column somebody
            // copies straight into a bid box. The server truncates for the same reason.
            ? `<div class="sn-strong sn-max">${moneyExact(row.maxBid)}</div>${headroom}`
            : '<span class="sn-sub">—</span>'}
        </td>
        <td class="num">
          ${row.profitAtMaxBid > 0
            ? `<div class="sn-strong sn-profit">${money(row.profitAtMaxBid)}</div>` +
              (row.daysToCash != null
                ? `<div class="sn-sub"><span class="speed-days speed-${speed.cls}" title="${esc(row.speedNote || '')}">${row.daysToCash}d to cash</span></div>`
                : '<div class="sn-sub">speed unknown</div>')
            : '<span class="sn-sub">—</span>'}
        </td>
        <td><span class="sn-verdict ${v.cls}">${esc(v.label)}</span></td>
        <td>${track}</td>
      </tr>`;
  }

  // ── The clock ─────────────────────────────────────────────────────────────

  function snipeClock(endUtc) {
    const ms = new Date(endUtc).getTime() - Date.now();
    if (!Number.isFinite(ms)) return '—';
    if (ms <= 0) return 'ended';

    const s = Math.floor(ms / 1000);
    const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600);
    const m = Math.floor((s % 3600) / 60), sec = s % 60;

    if (d > 0) return `${d}d ${h}h`;
    if (h > 0) return `${h}h ${String(m).padStart(2, '0')}m`;
    // Inside the hour the seconds matter — that is the whole window a snipe happens in.
    return `${m}m ${String(sec).padStart(2, '0')}s`;
  }

  function snipeLocalTime(utc) {
    const d = new Date(utc);
    return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', second: '2-digit' });
  }

  function updateSnipeClocks() {
    document.querySelectorAll('#sn-results [data-ends]').forEach(el => {
      const text = snipeClock(el.dataset.ends);
      el.textContent = text;
      const ms = new Date(el.dataset.ends).getTime() - Date.now();
      // A row that runs out under the seller's eyes says so, rather than sitting at "0m 00s"
      // looking winnable.
      el.classList.toggle('sn-clock-ended', ms <= 0);
      if (ms > 0 && ms <= 60 * 60 * 1000) {
        el.classList.remove('sn-clock-today', 'sn-clock-open');
        el.classList.add('sn-clock-closing');
      }
    });
    renderDashboardSnipes();
  }

  function startSnipeTicker() {
    stopSnipeTicker();
    if (!snipeScan || $('snipe-section')?.classList.contains('hidden')) return;
    snipeTicker = setInterval(() => {
      // Self-cancelling: navigating away hides the section without going through the close button,
      // and a timer left running against a hidden board is a leak nobody would ever notice.
      if ($('snipe-section')?.classList.contains('hidden')) { stopSnipeTicker(); return; }
      updateSnipeClocks();
    }, 1000);
  }

  function stopSnipeTicker() {
    if (snipeTicker) { clearInterval(snipeTicker); snipeTicker = null; }
  }

  // ── Tracking a snipe ──────────────────────────────────────────────────────
  // Freezes the forecast that justified bidding, at the ASK that matters — the ceiling, not the
  // current bid, because the ceiling is what the seller is actually deciding to spend. The Deal
  // Pipeline grades it against what the flip really made.
  async function trackSnipeRow(itemId, btn) {
    const row = (snipeScan?.candidates || []).find(r => r.itemId === itemId);
    if (!row || !btn) return;

    const original = btn.textContent;
    btn.disabled = true;
    btn.textContent = 'Tracking…';

    try {
      const res = await fetch('/api/deals', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title: row.title,
          stage: 'sourced',
          source: 'ebay_auction',
          sourceLabel: row.buyingOption === 'AUCTION' ? 'eBay auction' : 'eBay Buy It Now',
          sourceUrl: row.url,
          sourceItemId: row.itemId,
          // The ask is the ceiling: bidding to it is the decision being tracked, and the pipeline
          // rebases the projection on whatever is actually paid the moment it's entered.
          askPrice: row.maxBid,
          maxBuyPrice: row.maxBid,
          projectedSalePrice: row.expectedSale ?? row.marketMedian,
          projectedNetProfit: row.profitAtMaxBid,
          projectedRoiPercent: row.maxBid > 0 ? round2(row.profitAtMaxBid / (row.maxBid + row.shippingCost) * 100) : null,
          projectedDaysToCash: row.daysToCash,
          projectedBasis: [
            `${(row.soldCompCount || 0) + (row.terapeakCompCount || 0)} sold comps`,
            row.confidenceLevel,
            row.speedLabel,
            row.buyingOption === 'AUCTION' ? 'at the max bid' : 'Buy It Now',
          ].filter(Boolean).join(' · '),
          note: row.endUtc ? `Closes ${new Date(row.endUtc).toLocaleString()}` : '',
        }),
      });

      if (!res.ok) throw new Error(await res.text());
      pipeline = await res.json();
      renderDashboardPipeline();
      btn.textContent = '✓ Tracked';
      btn.classList.add('fb-arb-tracked');
      setSnipeStatus(`Tracking "${row.title}" — it's on the Deal Pipeline with ${moneyExact(row.maxBid)} as the ceiling you decided on.`);
      addActivity('Snipe tracked', `${row.title} — ${money(row.profitAtMaxBid || 0)} projected at the ceiling`);
    } catch (err) {
      btn.disabled = false;
      btn.textContent = original;
      setSnipeStatus(`Couldn't track that one: ${err.message}`);
    }
  }

  // The dashboard band. Driven entirely by the last scan held in memory — a countdown on the front
  // page must never mean an eBay search on every page load, and a stale clock would be worse than
  // no clock at all.
  function renderDashboardSnipes() {
    const band = $('dash-snipe');
    if (!band) return;

    const s = snipeScan?.summary;
    const live = (snipeScan?.candidates || []).filter(r => r.verdict === 'snipe' && (!r.endUtc || new Date(r.endUtc) > new Date()));
    if (!s || live.length === 0) { band.classList.add('hidden'); return; }

    band.classList.remove('hidden');
    setText('dash-snipe-figure', moneyExact(live.reduce((sum, r) => sum + (r.profitAtMaxBid || 0), 0)));
    setText('dash-snipe-sub',
      `${live.length} listing${live.length === 1 ? '' : 's'} under market · if you won each at your ceiling`);

    const soonest = live.filter(r => r.endUtc).sort((a, b) => new Date(a.endUtc) - new Date(b.endUtc))[0];
    const host = $('dash-snipe-next');
    if (host) {
      host.innerHTML = soonest
        ? `<span class="dash-pipeline-next-label">Closes in ${esc(snipeClock(soonest.endUtc))}</span><span class="dash-pipeline-next-text">${esc(soonest.title)}</span>`
        : '';
    }
  }

  // ── Promoted Listings: the ad rate that keeps the most money ──────────────
  // eBay's suggested ad rate is worked out from what the rest of the category pays — it has never
  // seen what this seller paid for the item, so on a thin margin it will suggest a rate bigger than
  // the profit and call it a recommendation. Everything below comes from /api/promoted/board, which
  // costs each listing with the same ProfitCalculator + FeeProfile the editor and the sourcing
  // screens use, so the margin an ad rate is judged against is the margin the rest of the app shows.
  let adScan = null;

  function showPromotedSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('promoted-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'promoted'));
    prefillAdRateFromFeeProfile();
  }

  function closePromotedSection() {
    $('promoted-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindPromoted() {
    on('ad-scan-btn', 'click', runAdRateScan);
    on('ad-close', 'click', closePromotedSection);
    on('ad-home', 'click', closePromotedSection);
    // Filtering is a pure view over the scan already in hand — changing it must never re-run a
    // multi-minute inventory scan.
    on('ad-filter', 'change', renderAdRows);
    on('ad-apply-default', 'click', applyBlendedAdRate);
    on('ad-ladder-close', 'click', () => $('ad-ladder-overlay')?.classList.add('hidden'));
    $('ad-ladder-overlay')?.addEventListener('click', e => {
      if (e.target.id === 'ad-ladder-overlay') $('ad-ladder-overlay').classList.add('hidden');
    });
  }

  // The rate the app already assumes on every net figure it prints, so the board opens comparing
  // against something real rather than against zero.
  async function prefillAdRateFromFeeProfile() {
    const box = $('ad-current-rate');
    if (!box || box.value !== '') return;
    try {
      const fees = await fetch('/api/fees/profile').then(r => r.json());
      box.value = String(Number(fees.promotedListingRatePercent) || 0);
    } catch { /* leave it blank — the server falls back to the same profile anyway */ }
  }

  async function runAdRateScan() {
    const btn = $('ad-scan-btn');
    const maxItems = $('ad-max-items')?.value || '120';
    const rateBox  = $('ad-current-rate')?.value.trim();
    const rateArg  = rateBox === '' ? '' : `&currentRate=${Math.max(0, parseFloat(rateBox) || 0)}`;

    if (btn) { btn.disabled = true; btn.textContent = 'Checking…'; }
    setAdStatus('Reading your live listings and costing each one…');
    $('ad-results').innerHTML = '<p class="opportunity-empty">Pricing every listing against sold comps, then testing each ad rate against its margin — this takes a moment on a large inventory.</p>';

    try {
      const res  = await fetch(`/api/promoted/board?maxItems=${maxItems}${rateArg}`);
      const data = await res.json();
      if (!res.ok) throw new Error(typeof data === 'string' ? data : 'The scan failed.');
      adScan = data;
      renderAdScan();
    } catch (err) {
      adScan = null;
      $('ad-summary')?.classList.add('hidden');
      $('ad-blend-bar')?.classList.add('hidden');
      $('ad-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The scan failed.')}</p>`;
      setAdStatus('');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '📣 Check My Ad Rates'; }
    }
  }

  function renderAdScan() {
    const warn = $('ad-warning');

    if (adScan.status === 'ebay_unavailable') {
      $('ad-summary')?.classList.add('hidden');
      $('ad-blend-bar')?.classList.add('hidden');
      warn?.classList.add('hidden');
      $('ad-results').innerHTML =
        '<p class="opportunity-empty">Your eBay account isn\'t connected, or the token has expired. Reconnect it in Settings, then check again.</p>';
      setAdStatus('');
      return;
    }

    const s = adScan.summary || {};
    const notes = [];
    if (adScan.dataWarning) notes.push(adScan.dataWarning);
    // A rate can only be sized against a margin, and the margin needs a cost basis. Saying which
    // listings are missing one beats silently advising on a subset.
    const missing = (s.listingsAnalyzed || 0) - (s.withCostBasis || 0);
    if (missing > 0) notes.push(`${missing} listing${missing === 1 ? ' has' : 's have'} no recorded cost, so no ad rate can be sized for ${missing === 1 ? 'it' : 'them'} — add what you paid in Inventory Health or the listing editor.`);

    if (notes.length) { warn.textContent = notes.join(' '); warn.classList.remove('hidden'); }
    else warn?.classList.add('hidden');

    renderAdSummary(s);
    // With nothing sizeable there is nothing in the "worth changing" view, and an empty table over a
    // board full of listings reads as a broken scan. Show the working instead.
    if (!s.advised && $('ad-filter')) $('ad-filter').value = 'all';
    renderAdRows();
    renderAdBlendBar(s);

    setAdStatus(
      `${adScan.itemsAnalyzed} listing${adScan.itemsAnalyzed === 1 ? '' : 's'} of ${adScan.activeListings} active · ` +
      `judged against the ${Number(adScan.comparedRatePercent ?? adScan.defaultRatePercent ?? 0).toFixed(1)}% you said you run today`);
  }

  // Leads with the bill, not the listing count. "You pay $312 in ad fees on one sale of each" is a
  // number a seller reacts to; "48 listings analyzed" is not.
  function renderAdSummary(s) {
    const el = $('ad-summary');
    if (!el) return;

    const delta = Number(s.adFeePerRoundAtRecommended || 0) - Number(s.adFeePerRoundAtCurrent || 0);
    const tiles = [
      { label: 'Ad fees, one sale of each', value: money(s.adFeePerRoundAtCurrent),
        sub: 'At the rate you run today, charged on the whole sale',
        tone: s.adFeePerRoundAtCurrent > 0 ? 'warn' : '' },
      { label: 'At the recommended rates', value: money(s.adFeePerRoundAtRecommended),
        sub: delta === 0 ? 'No change' : `${delta < 0 ? money(Math.abs(delta)) + ' less' : money(delta) + ' more'} per round of sales`,
        tone: delta < 0 ? 'good' : '' },
      { label: 'Spent past what it earns', value: money(s.overspendPerRound),
        sub: `${s.overPromoted || 0} over-promoted · ${s.shouldNotPromote || 0} shouldn't run ads at all`,
        tone: s.overspendPerRound > 0 ? 'bad' : '' },
      { label: 'Extra take-home', value: money(s.netGainPer100),
        sub: 'Per 100 sales of each listing, at the recommended rates',
        tone: s.netGainPer100 > 0 ? 'good' : '' },
      { label: 'Under-promoted', value: String(s.underPromoted || 0),
        sub: 'Margin that can carry a higher rate than it runs', tone: s.underPromoted > 0 ? 'warn' : '' },
      { label: 'Monthly, where known', value: money(s.extraProfitPerMonth),
        sub: s.withSalesHistory ? `Across the ${s.withSalesHistory} listing${s.withSalesHistory === 1 ? '' : 's'} with a sales history` : 'No listing has sold yet, so no monthly figure is honest',
        tone: s.extraProfitPerMonth > 0 ? 'good' : '' },
    ];

    el.innerHTML = tiles.map(t => `
      <div class="inv-tile ${t.tone ? 'inv-tile-' + t.tone : ''}">
        <div class="inv-tile-label">${esc(t.label)}</div>
        <div class="inv-tile-value">${esc(t.value)}</div>
        <div class="inv-tile-sub">${esc(t.sub)}</div>
      </div>`).join('');
    el.classList.remove('hidden');
  }

  // The blended rate is revenue-weighted, so a $1,400 miner's rate counts for more than a $9
  // cable's. Applying it only changes what THIS APP assumes — eBay campaigns are set in Seller Hub,
  // and the button says so rather than implying it pushed anything.
  function renderAdBlendBar(s) {
    const bar = $('ad-blend-bar');
    const note = $('ad-blend-note');
    if (!bar || !note) return;

    if (s.blendedRecommendedPercent == null || !s.advised) { bar.classList.add('hidden'); return; }

    note.textContent =
      `Across the ${s.advised} listing${s.advised === 1 ? '' : 's'} that could be sized, the revenue-weighted answer is ` +
      `${Number(s.blendedRecommendedPercent).toFixed(1)}%. Saving it updates what this app assumes on every net figure — ` +
      `your eBay campaigns are set in Seller Hub → Marketing.`;
    $('ad-apply-default').textContent = `Assume ${Number(s.blendedRecommendedPercent).toFixed(1)}% everywhere`;
    bar.classList.remove('hidden');
  }

  async function applyBlendedAdRate() {
    const rate = Number(adScan?.summary?.blendedRecommendedPercent);
    if (!Number.isFinite(rate)) return;

    const btn = $('ad-apply-default');
    if (btn) { btn.disabled = true; btn.textContent = 'Saving…'; }
    try {
      // Read-modify-write the whole profile: the endpoint takes a full profile, and posting a bare
      // ad rate would silently zero the shipping, packaging and floor settings alongside it.
      const fees = await fetch('/api/fees/profile').then(r => r.json());
      fees.promotedListingRatePercent = rate;
      const { res, body } = await safePost('/api/fees/profile', fees);
      if (!res.ok) throw new Error(body?.error || 'Save failed.');

      setAdStatus(`Every net figure in the app now assumes ${Number(body.promotedListingRatePercent).toFixed(1)}% of ad spend.`);
      addActivity('Default ad rate updated', `${Number(body.promotedListingRatePercent).toFixed(1)}% — set your real campaign rate in eBay Seller Hub`);
      loadFeeProfile();
      ['nl', 'f'].forEach(p => { if ($(`${p}-th-panel`)) scheduleTakeHome(p); });
    } catch (err) {
      setAdStatus(`Could not save the default rate: ${err.message || err}`);
    } finally {
      if (btn) { btn.disabled = false; renderAdBlendBar(adScan?.summary || {}); }
    }
  }

  const AD_VERDICTS = {
    under_promoted:  { label: '📈 Raise the rate',   cls: 'ad-v-under' },
    over_promoted:   { label: '💸 Overpaying',       cls: 'ad-v-over' },
    on_target:       { label: '✓ Right where it is', cls: 'ad-v-ok' },
    dont_promote:    { label: '🚫 Do not promote',   cls: 'ad-v-none' },
    no_margin:       { label: '🛑 No margin',        cls: 'inv-v-underwater' },
    fix_price_first: { label: '⚠️ Price first',      cls: 'ad-v-price' },
    no_cost_basis:   { label: '? No cost recorded',  cls: 'inv-v-nodata' },
    no_price:        { label: '? No price',          cls: 'inv-v-nodata' },
  };

  function adFilterMatches(item, filter) {
    switch (filter) {
      case 'change': return item.needsChange || item.verdict === 'no_margin';
      case 'all':    return true;
      default:       return item.verdict === filter;
    }
  }

  function renderAdRows() {
    if (!adScan) return;
    const filter = $('ad-filter')?.value || 'change';
    const rows = (adScan.items || []).filter(i => adFilterMatches(i, filter));
    const el = $('ad-results');

    if (rows.length === 0) {
      const total = (adScan.items || []).length;
      el.innerHTML = `<p class="opportunity-empty">${
        total === 0
          ? 'No live listings came back from eBay to check.'
          : !(adScan.summary?.advised)
            ? 'None of these listings has a recorded cost, so there is no margin to size an ad rate against. Add what you paid — in the listing editor or Inventory Health — and check again.'
          : filter === 'change'
            ? 'Nothing to change — every listing that could be sized is already at the rate its margin supports. Switch to "Everything" to see the working.'
            : 'Nothing matches that filter. Switch to "Everything" to see every listing.'}</p>`;
      return;
    }

    el.innerHTML = `
      <table class="inv-table">
        <thead>
          <tr>
            <th>Listing</th>
            <th class="num">Price</th>
            <th class="num">Net / sale</th>
            <th class="num">Category pays</th>
            <th class="num">You run</th>
            <th class="num">Should run</th>
            <th class="num">Ad fee / sale</th>
            <th class="num">Needs / expects</th>
            <th>Verdict</th>
          </tr>
        </thead>
        <tbody>${rows.map(adRowHtml).join('')}</tbody>
      </table>`;

    el.querySelectorAll('.ad-ladder-btn').forEach(btn =>
      btn.addEventListener('click', () => openAdLadder(btn.dataset.id)));
  }

  function adRowHtml(item) {
    const v = AD_VERDICTS[item.verdict] || AD_VERDICTS.no_price;
    const rec = item.recommendedRatePercent;
    const pct = n => `${Number(n).toFixed(1)}%`;

    // "Needs / expects" is the honest heart of the row: the first number is arithmetic (extra sales
    // the rate must buy to pay for itself), the second is a model. Shown side by side so the seller
    // can see whether the recommendation depends on the model or survives without it.
    const liftCell = rec == null || rec <= 0
      ? '<span class="inv-dash">—</span>'
      : item.breakEvenLiftAtRecommendedPercent == null
        ? '<span class="ad-lift-bad" title="The ad fee is bigger than the profit on the sale">impossible</span>'
        : `<strong>+${pct(item.breakEvenLiftAtRecommendedPercent)}</strong>
           <div class="inv-sub">model expects +${pct(item.modeledLiftAtRecommendedPercent)}</div>`;

    return `
      <tr class="${item.needsChange ? '' : 'wo-row-muted'}">
        <td class="inv-cell-title">
          <div class="inv-title-row">
            ${item.imageUrl ? `<img class="inv-thumb" src="${esc(item.imageUrl)}" alt="" loading="lazy" />` : ''}
            <div>
              ${item.url ? `<a href="${esc(item.url)}" target="_blank" rel="noopener">${esc(item.title)}</a>` : esc(item.title)}
              <div class="inv-sub">${esc(item.categoryLabel || item.category || 'Uncategorised')}${
                item.quantitySold > 0 ? ` · ${item.quantitySold} sold` : ''}${
                item.daysListed != null ? ` · ${item.daysListed}d live` : ''}</div>
            </div>
          </div>
        </td>
        <td class="num">${moneyExact(item.listPrice)}</td>
        <td class="num">${item.netPerSaleNoAds != null
            ? `<span class="${item.netPerSaleNoAds > 0 ? 'inv-gap-good' : 'inv-gap-bad'}">${moneyExact(item.netPerSaleNoAds)}</span>${
                item.marginPercent != null ? `<div class="inv-sub">${Number(item.marginPercent).toFixed(1)}% margin</div>` : ''}`
            : '<span class="inv-dash" title="Record what you paid to size an ad rate against it">—</span>'}</td>
        <td class="num">${pct(item.categoryRatePercent)}<div class="inv-sub">${esc(item.categoryCompetition)}</div></td>
        <td class="num">${pct(item.currentRatePercent)}</td>
        <td class="num inv-cell-suggested">${rec == null
            ? '<span class="inv-dash">—</span>'
            : `<strong class="${rec > item.currentRatePercent ? 'inv-gap-good' : rec < item.currentRatePercent ? 'ad-rate-down' : ''}">${rec <= 0 ? 'none' : pct(rec)}</strong>${
                item.maxSustainableRatePercent != null
                  ? `<div class="inv-sub" title="Above this the ad fee is bigger than the whole profit on the sale">ceiling ${pct(item.maxSustainableRatePercent)}</div>` : ''}`}</td>
        <td class="num">${moneyExact(item.adFeeAtCurrent)}${
            item.adFeeAtRecommended != null && item.adFeeChangePerSale !== 0
              ? `<div class="inv-sub">→ ${moneyExact(item.adFeeAtRecommended)}</div>` : ''}</td>
        <td class="num">${liftCell}</td>
        <td class="inv-cell-verdict">
          <span class="inv-verdict ${v.cls}">${v.label}</span>
          <div class="inv-note">${esc(item.note)}</div>
          ${(item.signals || []).length ? `<div class="inv-signals">${item.signals.map(sig => esc(sig)).join(' ')}</div>` : ''}
          ${(item.ladder || []).length
            ? `<button class="btn btn-secondary small ad-ladder-btn" type="button" data-id="${esc(item.listingId || item.title)}">See the tradeoff</button>` : ''}
        </td>
      </tr>`;
  }

  function adItemById(id) {
    return (adScan?.items || []).find(i => (i.listingId || i.title) === id) || null;
  }

  // Every rate, side by side. This is the screen eBay does not have: what the fee costs, what it
  // has to buy back, and what it is actually worth once both are counted.
  function openAdLadder(id) {
    const item = adItemById(id);
    if (!item) return;

    $('ad-ladder-title').textContent = item.title;
    $('ad-ladder-sub').textContent =
      `${moneyExact(item.grossPerSale)} sale` +
      (item.netPerSaleNoAds != null ? ` · ${moneyExact(item.netPerSaleNoAds)} net before ads` : '') +
      ` · ${item.categoryLabel} typically pays ${Number(item.categoryRatePercent).toFixed(1)}% ` +
      `(${item.categoryCompetition} competition)`;

    const a = item.assumptions || {};
    $('ad-ladder-body').innerHTML = `
      <table class="inv-table ad-ladder-table">
        <thead>
          <tr>
            <th class="num">Ad rate</th>
            <th class="num">Fee per sale</th>
            <th class="num">You keep</th>
            <th class="num">Sales lift needed</th>
            <th class="num">Model expects</th>
            <th class="num">Per 100 sales</th>
            <th class="num">vs no ads</th>
          </tr>
        </thead>
        <tbody>${(item.ladder || []).map(p => adLadderRowHtml(p)).join('')}</tbody>
      </table>
      <p class="ad-assumptions">
        <strong>Sales lift needed</strong> is arithmetic: at this rate you also pay the fee on the
        ${Number(a.cannibalizationPercent || 0).toFixed(0)}% of sales you would have made anyway, so the ads have to
        replace that before they add anything. <strong>Model expects</strong> is an estimate — up to
        ${Number(a.maxLiftPercent || 0).toFixed(0)}% more sales, half of it bought by the
        ${Number(a.halfLiftRatePercent || 0).toFixed(1)}% category rate, with diminishing returns above that.
        ${esc(a.basis || '')} Where the two columns cross is where the rate stops paying for itself.
      </p>`;

    $('ad-ladder-overlay')?.classList.remove('hidden');
  }

  function adLadderRowHtml(p) {
    const cls = [p.isRecommended ? 'ad-rung-best' : '', p.isCurrent ? 'ad-rung-current' : '',
                 p.aboveCeiling ? 'ad-rung-over' : ''].filter(Boolean).join(' ');
    const tag = [p.isRecommended ? '<span class="ad-tag ad-tag-best">best</span>' : '',
                 p.isCurrent ? '<span class="ad-tag">you</span>' : ''].join('');

    return `
      <tr class="${cls}">
        <td class="num"><strong>${p.ratePercent === 0 ? 'No ads' : Number(p.ratePercent).toFixed(1) + '%'}</strong>${tag}</td>
        <td class="num">${moneyExact(p.adFeePerSale)}</td>
        <td class="num">${p.netPerSale != null ? moneyExact(p.netPerSale) : '<span class="inv-dash">—</span>'}</td>
        <td class="num">${p.breakEvenLiftPercent == null
            ? (p.netPerSale == null ? '<span class="inv-dash">—</span>'
               : '<span class="ad-lift-bad">impossible</span>')
            : `+${Number(p.breakEvenLiftPercent).toFixed(1)}%`}</td>
        <td class="num">+${Number(p.modeledLiftPercent).toFixed(1)}%</td>
        <td class="num">${p.netPer100Sales != null ? moneyExact(p.netPer100Sales) : '<span class="inv-dash">—</span>'}</td>
        <td class="num">${p.netChangePer100 == null ? '<span class="inv-dash">—</span>'
            : `<span class="${p.netChangePer100 > 0 ? 'inv-gap-good' : p.netChangePer100 < 0 ? 'inv-gap-bad' : ''}">${
                p.netChangePer100 > 0 ? '+' : ''}${moneyExact(p.netChangePer100)}</span>`}</td>
      </tr>`;
  }

  function setAdStatus(text) {
    const el = $('ad-status');
    if (el) el.textContent = text || '';
  }

  // ── Representative Photo Library ─────────────────────────────────────────
  // Manages photos/<model>/ — the seller's own shots of each used model, taken once and reused
  // (with disclosure) on every identical unit they list. See PhotoLibrary.cs for the server side.
  let plFolders  = [];   // [{ modelKey, imageCount, photos: [url] }] as returned by the API
  let plSelected = '';   // modelKey of the folder currently shown on the right

  function showPhotoLibrarySection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('photo-library-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'photos'));
    setPlUploadMsg('');
    loadPhotoLibrary();
  }

  function closePhotoLibrarySection() {
    $('photo-library-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindPhotoLibrary() {
    const dropZone  = $('pl-drop-zone');
    const fileInput = $('pl-file-input');

    dropZone?.addEventListener('click', e => {
      if (e.target !== fileInput) fileInput?.click();
    });
    dropZone?.addEventListener('dragover', e => {
      e.preventDefault();
      dropZone.classList.add('drag-over');
    });
    dropZone?.addEventListener('dragleave', e => {
      if (!dropZone.contains(e.relatedTarget)) dropZone.classList.remove('drag-over');
    });
    dropZone?.addEventListener('drop', e => {
      e.preventDefault();
      dropZone.classList.remove('drag-over');
      plUploadFiles(e.dataTransfer.files);
    });
    fileInput?.addEventListener('change', () => {
      plUploadFiles(fileInput.files);
      fileInput.value = '';
    });
    // The zone is contenteditable purely so it can receive a paste — never let it be typed into.
    dropZone?.addEventListener('beforeinput', e => e.preventDefault());
    dropZone?.addEventListener('paste', e => {
      e.preventDefault();
      e.stopPropagation();
      const files = [...(e.clipboardData?.items || [])]
        .filter(i => i.kind === 'file' && i.type.startsWith('image/'))
        .map(i => i.getAsFile())
        .filter(Boolean);
      if (files.length) plUploadFiles(files);
    });

    on('pl-create-btn', 'click', createPhotoFolder);
    on('pl-new-model', 'keydown', e => { if (e.key === 'Enter') createPhotoFolder(); });
    on('pl-refresh', 'click', () => loadPhotoLibrary());
    on('pl-close', 'click', closePhotoLibrarySection);
    document.addEventListener('keydown', e => {
      if (e.key === 'Escape' && !$('photo-library-section')?.classList.contains('hidden')) closePhotoLibrarySection();
    });
  }

  async function loadPhotoLibrary() {
    const list = $('pl-folder-list');
    try {
      plFolders = await fetch('/api/photos/library').then(r => r.json());
    } catch (err) {
      if (list) list.innerHTML = `<p class="opportunity-empty">Could not load the photo library: ${esc(err.message)}</p>`;
      return;
    }
    // Keep the seller on the folder they were working in; fall back to the first one.
    if (!plFolders.some(f => f.modelKey === plSelected)) plSelected = plFolders[0]?.modelKey || '';
    renderPhotoFolders();
    renderPhotoFolderDetail();
  }

  function renderPhotoFolders() {
    const list = $('pl-folder-list');
    if (!list) return;
    if (!plFolders.length) {
      list.innerHTML = '<p class="opportunity-empty">No model folders yet — create one above.</p>';
      return;
    }
    list.innerHTML = plFolders.map(f => `
      <button class="pl-folder${f.modelKey === plSelected ? ' active' : ''}" type="button" data-model="${esc(f.modelKey)}">
        <span class="pl-folder-name">${esc(f.modelKey)}</span>
        <span class="pl-folder-count${f.imageCount ? '' : ' empty'}">${f.imageCount}</span>
      </button>`).join('');
    list.querySelectorAll('.pl-folder').forEach(btn => btn.addEventListener('click', () => {
      plSelected = btn.dataset.model;
      setPlUploadMsg('');
      renderPhotoFolders();
      renderPhotoFolderDetail();
    }));
  }

  function renderPhotoFolderDetail() {
    const folder = plFolders.find(f => f.modelKey === plSelected);
    const title  = $('pl-detail-title');
    const grid   = $('pl-photo-grid');

    $('pl-drop-zone')?.classList.toggle('hidden', !folder);
    if (title) title.textContent = folder ? folder.modelKey : 'Select a model';
    if (!grid) return;

    if (!folder) {
      grid.innerHTML = '<p class="opportunity-empty">Pick a model on the left, or create a new one above.</p>';
      return;
    }
    const photos = folder.photos || [];
    grid.innerHTML = photos.length
      ? photos.map(url => `
        <figure class="pl-photo">
          <img src="${esc(url)}" alt="${esc(folder.modelKey)} representative photo" loading="lazy" data-view="${esc(url)}" />
          <figcaption class="pl-photo-actions">
            <span class="pl-photo-name">${esc(url.split('/').pop())}</span>
            <button class="btn btn-ghost small pl-photo-delete" type="button" data-delete="${esc(url)}">Delete</button>
          </figcaption>
        </figure>`).join('')
      // Borderless: it sits directly under a dashed drop zone, and two dashed
      // rectangles in a column read as two drop targets.
      : stateBlockHtml({
          compact: true,
          inline: true,
          icon: 'i-photos',
          title: 'No photos in this model yet',
          body: 'Drop your own photos of this model above. Every used listing that matches it reuses them, with the representative-photo disclosure, instead of a per-unit shoot.'
        });

    grid.querySelectorAll('[data-view]').forEach(img => img.addEventListener('click', () => showLightbox(img.dataset.view)));
    grid.querySelectorAll('[data-delete]').forEach(btn => btn.addEventListener('click', () => deleteLibraryPhoto(btn.dataset.delete)));
  }

  function setPlUploadMsg(text, kind = '') {
    const el = $('pl-upload-msg');
    if (!el) return;
    el.textContent = text;
    el.className = `sd-test-msg${kind ? ' ' + kind : ''}`;
  }

  async function createPhotoFolder() {
    const input = $('pl-new-model');
    const msg   = $('pl-create-msg');
    const name  = input?.value.trim();
    if (!name) {
      if (msg) { msg.textContent = 'Enter a model name first.'; msg.className = 'sd-test-msg error'; }
      return;
    }
    try {
      const res  = await fetch('/api/photos/library/create', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ modelKey: name })
      });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(body.error || 'Could not create the folder');
      // The server sanitizes the name, so select whatever key it actually created.
      plSelected = body.modelKey;
      if (input) input.value = '';
      if (msg) { msg.textContent = `Created ${body.modelKey}`; msg.className = 'sd-test-msg ok'; }
      addActivity('Photo library folder created', body.modelKey);
      await loadPhotoLibrary();
    } catch (err) {
      if (msg) { msg.textContent = err.message; msg.className = 'sd-test-msg error'; }
    }
  }

  // /api/photos/remove-bg writes the cutout to /generated-photos and hands back a URL, but the
  // library upload takes base64 — so pull the cleaned bytes back before saving them to the model.
  async function plRemoveBackground(imageBase64, mimeType) {
    const res  = await fetch('/api/photos/remove-bg', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ imageBase64, mimeType })
    });
    const body = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(body.error || 'Background removal failed');
    const blob = await fetch(body.url).then(r => r.blob());
    return { imageBase64: await blobToBase64(blob), mimeType: blob.type || 'image/png' };
  }

  async function plUploadFiles(fileList) {
    const images = [...(fileList || [])].filter(f => (f.type || '').startsWith('image/'));
    if (!images.length) return;
    if (!plSelected) { setPlUploadMsg('Pick or create a model folder first.', 'error'); return; }

    const model    = plSelected;
    const removeBg = !!$('pl-remove-bg')?.checked;
    let saved = 0;
    const failures = [];

    for (const [i, file] of images.entries()) {
      const label = file.name || 'Pasted photo';
      setPlUploadMsg(`Saving ${i + 1} of ${images.length}${removeBg ? ' — removing background, this takes a few seconds…' : '…'}`);
      try {
        let payload = { imageBase64: await blobToBase64(file), mimeType: file.type || 'image/jpeg' };
        if (removeBg) payload = await plRemoveBackground(payload.imageBase64, payload.mimeType);

        const res  = await fetch('/api/photos/library/upload', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ modelKey: model, ...payload })
        });
        const body = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(body.error || 'Upload failed');
        saved++;
      } catch (err) {
        failures.push(`${label}: ${err.message}`);
      }
    }

    if (saved) addActivity('Representative photos added', `${saved} photo(s) saved to ${model}`);
    await loadPhotoLibrary();
    setPlUploadMsg(
      failures.length ? `Saved ${saved} of ${images.length}. ${failures.join(' · ')}` : `Saved ${saved} photo(s) to ${model}.`,
      failures.length ? 'error' : 'ok');
  }

  async function deleteLibraryPhoto(url) {
    const fileName = url.split('/').pop();
    if (!confirm(`Remove ${fileName} from ${plSelected}?\n\nFuture listings of this model stop using it; already-published listings keep their copy.`)) return;
    try {
      const res  = await fetch('/api/photos/library/delete', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ modelKey: plSelected, fileName })
      });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(body.error || 'Delete failed');
      addActivity('Representative photo deleted', `${plSelected}/${fileName}`);
      await loadPhotoLibrary();
    } catch (err) {
      setPlUploadMsg(`Could not delete ${fileName}: ${err.message}`, 'error');
    }
  }

  function formatEndsIn(iso) {
    if (!iso) return 'Unknown end time';
    const ms = new Date(iso).getTime() - Date.now();
    if (ms <= 0) return 'Ending now';
    const mins = Math.floor(ms / 60000);
    if (mins < 60) return `Ends in ${mins}m`;
    const hrs = Math.floor(mins / 60);
    if (hrs < 48) return `Ends in ${hrs}h ${mins % 60}m`;
    return `Ends in ${Math.floor(hrs / 24)}d`;
  }

  function renderOpportunityStatsCards(data) {
    const grid = $('opp-stats-grid');
    if (!grid) return;

    const pct = (v, withSign) => v == null ? '—' : `${withSign && v > 0 ? '+' : ''}${v}%`;
    const pctClass = v => v == null ? '' : v > 0 ? 'good' : v < 0 ? 'bad' : '';
    const sourceLabel = data.soldSource === 'local_market_data' ? 'Local market research'
      : data.soldSource === 'terapeak' ? 'Terapeak sold comps'
      : data.soldSource === 'marketplace_insights' ? 'eBay sold comps' : 'No sold-comp data';
    const listingLabel = data.listingType === 'FIXED_PRICE' ? 'Fixed price' : data.listingType === 'BOTH' ? 'Auctions + fixed price' : 'Auctions ending soon';

    const best = data.bestOpportunity;
    const bestTag  = best ? 'a' : 'div';
    const bestAttr = best ? `href="${esc(best.url)}" target="_blank" rel="noopener"` : '';
    const bestPrefix = !best ? ''
      : best.sellThroughUnverified ? '⚠ Low confidence — thin data — '
      : best.isVerified ? '✓ Terapeak-matched — ' : '(rough estimate) ';
    const bestNote = best ? `${bestPrefix}${esc(best.title)}` : 'No priced opportunities';

    grid.innerHTML = `
      <${bestTag} class="stat-card" ${bestAttr}>
        <span class="stat-label">Best Opportunity</span>
        <strong class="${pctClass(best?.profitPercent)}">${best ? pct(best.profitPercent, true) : '—'}</strong>
        <span class="stat-note">${bestNote}</span>
      </${bestTag}>
      <div class="stat-card">
        <span class="stat-label">Average Market Price</span>
        <strong>${data.averagePrice > 0 ? `$${data.averagePrice.toFixed(2)}` : '—'}</strong>
        <span class="stat-note">${sourceLabel}</span>
      </div>
      <div class="stat-card">
        <span class="stat-label">Lowest Total Cost</span>
        <strong>${data.lowestPrice != null ? `$${data.lowestPrice.toFixed(2)}` : '—'}</strong>
        <span class="stat-note">Price + shipping, among ${data.count}</span>
      </div>
      <div class="stat-card">
        <span class="stat-label">Active Listings</span>
        <strong>${data.count}</strong>
        <span class="stat-note">${listingLabel}</span>
      </div>
      <div class="stat-card">
        <span class="stat-label">Est. Sell-Through</span>
        <strong>${data.sellThroughPercent != null ? `${data.sellThroughPercent}%` : '—'}</strong>
        <span class="stat-note">${data.sellThroughPercent != null ? 'eBay sell-through rate' : 'Not available for this search'}</span>
      </div>
      <div class="stat-card">
        <span class="stat-label">Avg. Profit Potential</span>
        <strong class="${pctClass(data.avgProfitPercent)}">${pct(data.avgProfitPercent, true)}</strong>
        <span class="stat-note">${data.avgProfitPercent != null ? 'Across priced listings' : 'No sold-comp data'}</span>
      </div>`;
  }

  let lastOpportunityData = null;

  // Shown wherever a row's sell-through couldn't be verified — sold comps with no active listings
  // to divide by, so the rate (and the profit built on it) is a guess, not a measurement.
  const THIN_DATA_TITLE = 'Sell-through could not be verified for this item — too few comparable sold and active listings to measure. Treat the profit estimate as unproven.';

  const OPP_FILTERS = [
    ['opp-filter-underpriced',  it => it.isUnderpriced],
    ['opp-filter-ending-soon',  it => it.isEndingSoon],
    ['opp-filter-poor-titles',  it => it.hasPoorTitle],
    ['opp-filter-misspelled',   it => it.hasMisspelledTitle],
    ['opp-filter-poor-photos',  it => it.hasPoorPhoto],
    ['opp-filter-high-demand',  it => it.isHighDemand],
    ['opp-filter-high-profit',  it => it.isHighProfitMargin],
    ['opp-filter-high-throughput', it => it.isHighThroughput],
    ['opp-filter-newly-listed', it => it.isNewlyListed],
    ['opp-filter-low-competition', it => it.competitionLevel === 'Low'],
    ['opp-filter-exclude-low-confidence', it => (it.confidenceScore ?? 0) >= 40],
    ['opp-filter-exclude-parts-only', it => !(it.warnings || []).some(w => /parts|broken|accessor/i.test(w))],
    ['opp-filter-exclude-no-exact-model', it => (it.scoreReasons || []).some(r => /model|identifier/i.test(r)) || (it.confidenceScore ?? 0) >= 65],
  ];

  // Numeric "at least / at most" filters — a separate array from OPP_FILTERS since these read
  // from number inputs instead of checkboxes.
  const OPP_RANGE_FILTERS = [
    ['opp-min-roi', 'min', it => it.roiPercent],
    ['opp-min-net-profit', 'min', it => it.estimatedProfit],
    ['opp-min-confidence', 'min', it => it.confidenceScore],
    ['opp-min-sell-through', 'min', it => it.sellThroughPercent],
    ['opp-max-days-to-sell', 'max', it => it.estimatedDaysToSell],
  ];

  const OPP_SORTERS = {
    opportunityScore: it => it.opportunityScore ?? -999,
    confidenceScore:  it => it.confidenceScore ?? -1,
    totalProfit:      it => it.estimatedProfit ?? -999999,
    netProfit:        it => it.estimatedProfit ?? -999999,
    roiPercent:       it => it.roiPercent ?? -999,
    sellThroughPercent: it => it.sellThroughPercent ?? -1,
    velocity:         it => it.estimatedMonthlySales ?? -1,
    totalCost:        it => -(it.totalCost ?? 0),
    expectedSalePrice: it => it.estimatedResalePrice ?? -1,
    daysToSell:       it => it.estimatedDaysToSell ?? 999999,
  };

  // Default composite sort: Opportunity Score -> Confidence -> Total profit -> Net profit/unit -> ROI.
  function defaultOpportunitySort(items) {
    return [...items].sort((a, b) =>
      (b.opportunityScore ?? -999) - (a.opportunityScore ?? -999) ||
      (b.confidenceScore ?? -1) - (a.confidenceScore ?? -1) ||
      (b.estimatedProfit ?? -999999) - (a.estimatedProfit ?? -999999) ||
      (b.roiPercent ?? -999) - (a.roiPercent ?? -999));
  }

  function applyOpportunityFilters() {
    if (!lastOpportunityData) return;
    const active = OPP_FILTERS.filter(([id]) => $(id)?.checked);
    let items = active.length
      ? lastOpportunityData.items.filter(it => active.every(([, test]) => test(it)))
      : lastOpportunityData.items;

    for (const [id, kind, getter] of OPP_RANGE_FILTERS) {
      const raw = $(id)?.value;
      if (raw === '' || raw == null) continue;
      const bound = parseFloat(raw);
      if (Number.isNaN(bound)) continue;
      items = items.filter(it => {
        const v = getter(it);
        if (v == null) return false;
        return kind === 'min' ? v >= bound : v <= bound;
      });
    }

    const sortKey = $('opp-sort-select')?.value;
    items = sortKey && OPP_SORTERS[sortKey]
      ? [...items].sort((a, b) => OPP_SORTERS[sortKey](b) - OPP_SORTERS[sortKey](a))
      : defaultOpportunitySort(items);

    renderOpportunityList(items, lastOpportunityData.items.length);
  }

  function renderOpportunityList(items, totalCount) {
    const list = $('opp-results-list');
    if (!list) return;

    if (!items.length) {
      list.innerHTML = `<div class="opp-results-empty">No matching listings right now — try a broader keyword or looser filters.</div>`;
      return;
    }

    const filterNote = items.length < totalCount
      ? `<div class="opp-results-filtered-note">Showing ${items.length} of ${totalCount} listings</div>` : '';

    list.innerHTML = filterNote + items.map(buildOpportunityRowHtml).join('');
    list.querySelectorAll('.opp-details-toggle').forEach(btn =>
      btn.addEventListener('click', () => btn.closest('.opp-result-row')?.querySelector('.opp-result-details')?.classList.toggle('hidden')));
  }

  function confidenceBadgeClass(level) {
    if (!level) return '';
    if (level.startsWith('High')) return 'good';
    if (level.startsWith('Good')) return 'mid-good';
    if (level.startsWith('Limited')) return 'mid';
    return 'bad';
  }

  function buildOpportunityRowHtml(it) {
    const profitClass = it.profitPercent == null ? 'flat' : it.profitPercent > 0 ? 'good' : 'bad';
    const profitPct    = it.profitPercent == null ? '—' : `${it.profitPercent > 0 ? '+' : ''}${it.profitPercent}%`;
    const profitAmount = it.estimatedProfit == null ? '' : ` (${it.estimatedProfit > 0 ? '+' : ''}$${Math.abs(it.estimatedProfit).toFixed(2)})`;
    const isAuction    = it.buyingOption === 'AUCTION';
    const listingLabel = isAuction ? 'Auction' : 'Fixed Price';
    const bidsText     = isAuction ? `${it.bidCount} bid${it.bidCount === 1 ? '' : 's'}` : '—';
    const timeText     = isAuction ? formatEndsIn(it.endDate) : '—';
    // A row whose sell-through couldn't be measured (no active comps to divide by) never gets the
    // green "matched" badge — the profit number rests on thin data, so say so instead of letting it
    // read as a guaranteed flip.
    const verifiedTag  = it.profitPercent == null ? ''
      : it.sellThroughUnverified ? `<span class="opp-verified-tag opp-thin-data" title="${esc(THIN_DATA_TITLE)}">⚠ Low confidence — thin data</span>`
      : it.isVerified ? '<span class="opp-verified-tag opp-verified">✓ Terapeak-matched</span>'
      : '<span class="opp-verified-tag opp-estimate">rough estimate</span>';
    const scoreClass = it.opportunityScore == null ? '' : it.opportunityScore >= 60 ? 'good' : it.opportunityScore >= 35 ? 'mid' : 'bad';
    const scoreText  = it.opportunityScore == null ? '—' : it.opportunityScore;
    const confClass  = confidenceBadgeClass(it.confidenceLevel);
    const money = v => v != null ? `$${v.toFixed(2)}` : '—';

    const warnings = (it.warnings || []).map(w => `<li class="opp-warning">⚠ ${esc(w)}</li>`).join('');
    const reasons  = (it.scoreReasons || []).map(r => `<li class="opp-reason">✓ ${esc(r)}</li>`).join('');
    const disagreement = it.marketDataDisagreement
      ? `<div class="opp-disagreement">⚠ ${esc(it.disagreementMessage || 'Local and Terapeak pricing disagree — treat with caution.')}</div>` : '';

    return `<div class="opp-result-row">
      <img class="opp-result-thumb" src="${esc(it.imageUrl || '')}" alt="" onerror="this.style.visibility='hidden'">
      <div class="opp-result-info">
        <div class="opp-result-title"><a href="${esc(it.url)}" target="_blank" rel="noopener">${esc(it.title)}</a></div>
        <div class="opp-result-meta">
          <span class="opp-result-listing-type">${listingLabel}</span>
          ${esc(it.sellerUsername || 'Unknown seller')} · ${it.sellerFeedbackScore.toLocaleString()} feedback · ${bidsText} · ${timeText}
        </div>
        <button type="button" class="opp-details-toggle link">Show scoring details ▾</button>
      </div>
      <div class="opp-result-costs">
        <div class="opp-cost-line"><span>Price</span><strong>$${it.price.toFixed(2)}</strong></div>
        <div class="opp-cost-line"><span>Shipping</span><strong>${it.shippingCost > 0 ? `$${it.shippingCost.toFixed(2)}` : 'Free'}</strong></div>
        <div class="opp-cost-line total"><span>Total cost</span><strong>$${it.totalCost.toFixed(2)}</strong></div>
      </div>
      <div class="opp-result-value">
        <div class="opp-cost-line"><span>Market avg</span><strong>${it.marketAverage != null ? `$${it.marketAverage.toFixed(2)}` : '—'}</strong></div>
        <div class="opp-cost-line"><span>Recommended price</span><strong>${money(it.recommendedListingPrice)}</strong></div>
        <div class="opp-cost-line"><span>Net resale</span><strong>${it.estimatedResalePrice != null ? `$${it.estimatedResalePrice.toFixed(2)}` : '—'}</strong></div>
        <div class="opp-cost-line"><span>Sell-through</span><strong>${it.sellThroughPercent != null ? `${it.sellThroughPercent}%` : '—'}</strong></div>
        <div class="opp-cost-line"><span>ROI</span><strong>${it.roiPercent != null ? `${it.roiPercent}%` : '—'}</strong></div>
        <div class="opp-cost-line profit ${profitClass}"><span>Est. profit</span><strong>${profitPct}${profitAmount}</strong></div>
        ${verifiedTag}
      </div>
      <div class="opp-result-score">
        <div class="opp-score-badge ${scoreClass}">${scoreText}</div>
        <span class="opp-result-score-label">Score</span>
        ${it.confidenceLevel ? `<div class="opp-confidence-badge ${confClass}" title="${esc(it.confidenceLevel)}">${it.confidenceScore}</div><span class="opp-result-score-label">Confidence</span>` : ''}
      </div>
      <div class="opp-result-details hidden">
        ${disagreement}
        <div class="opp-details-grid">
          <div><span>Quick-sale price</span><strong>${money(it.quickSalePrice)}</strong></div>
          <div><span>Expected sale price</span><strong>${money(it.estimatedResalePrice)}</strong></div>
          <div><span>High-price target</span><strong>${money(it.highPriceTarget)}</strong></div>
          <div><span>Break-even price</span><strong>${money(it.breakEvenSalePrice)}</strong></div>
          <div><span>Margin</span><strong>${it.marginPercent != null ? `${it.marginPercent}%` : '—'}</strong></div>
          <div><span>Est. monthly sales</span><strong>${it.estimatedMonthlySales != null ? it.estimatedMonthlySales.toFixed(1) : '—'}</strong></div>
          <div><span>Est. days to sell</span><strong>${it.estimatedDaysToSell ?? '—'}</strong></div>
          <div><span>Price stability</span><strong>${it.priceStabilityScore ?? '—'}/100 (${esc(it.priceTrend || 'Unknown')})</strong></div>
          <div><span>Competition</span><strong>${esc(it.competitionLevel || 'Unknown')} (${it.closeActiveComparableCount ?? 0})</strong></div>
          <div><span>Local / Terapeak comps</span><strong>${it.localComparableCount ?? 0} / ${it.terapeakComparableCount ?? 0}</strong></div>
          <div><span>Source weighting</span><strong>${(it.localWeightPercent ?? 0).toFixed(0)}% local / ${(it.terapeakWeightPercent ?? 0).toFixed(0)}% Terapeak</strong></div>
        </div>
        ${reasons || warnings ? `<ul class="opp-score-explanation">${reasons}${warnings}</ul>` : ''}
      </div>
    </div>`;
  }

  function renderOpportunityResults(data) {
    const summary = $('opp-results-summary');
    if (!summary) return;

    lastOpportunityData = data;
    renderOpportunityStatsCards(data);

    const valueNote = data.marketValue > 0
      ? `Estimated market value <strong>$${data.marketValue.toFixed(2)}</strong> (${data.soldSource === 'local_market_data' ? 'local market research' : data.soldSource === 'terapeak' ? 'Terapeak sold comps' : 'recent sold comps'})`
      : `No sold-comp data found for this keyword yet — profit % isn't available, but listings are still shown below.`;
    const listingLabel = data.listingType === 'FIXED_PRICE' ? 'fixed-price listings' : data.listingType === 'BOTH' ? 'listings' : 'auctions ending soon';
    const queryLabel = data.query.startsWith('seller:') ? `seller "${esc(data.query.slice(7))}"` : `"${esc(data.query)}"`;
    const illiquidNote = data.excludedIlliquidCount > 0
      ? ` <span class="opp-results-filtered-note">(${data.excludedIlliquidCount} slow/stale-moving result${data.excludedIlliquidCount === 1 ? '' : 's'} hidden — check "Include slow/stale-moving results" to see ${data.excludedIlliquidCount === 1 ? 'it' : 'them'})</span>`
      : '';
    summary.innerHTML = `Found <strong>${data.count}</strong> ${listingLabel} for ${queryLabel}. ${valueNote}${illiquidNote}`;

    applyOpportunityFilters();
    renderUnderpricedCard(data.items || []);
  }

  async function findOpportunities() {
    const q      = $('opp-search-input')?.value.trim();
    const seller = $('opp-seller-input')?.value.trim();
    if (!q && !seller) { $('opp-search-input')?.focus(); return; }
    const category    = $('opp-category-input')?.value.trim();
    const condition   = $('opp-condition-select')?.value;
    const minPrice    = $('opp-min-price-input')?.value;
    const maxPrice    = $('opp-max-price-input')?.value;
    const listingType = $('opp-listing-type-select')?.value || 'AUCTION';
    const includeIlliquid = $('opp-include-illiquid')?.checked;

    const btn   = $('opp-find-btn');
    const results = $('opp-results');
    const summary = $('opp-results-summary');
    const list    = $('opp-results-list');
    const stats   = $('opp-stats-grid');
    if (btn) { btn.disabled = true; btn.textContent = 'Searching…'; }
    results?.classList.remove('hidden');
    if (summary) summary.innerHTML = seller
      ? 'Pulling this seller\'s listings and checking sold comps — verifying the top candidates can take up to a minute…'
      : 'Searching live listings and checking sold comps — verifying the top candidates can take up to a minute…';
    if (list) list.innerHTML = '';
    if (stats) stats.innerHTML = '';
    OPP_FILTERS.forEach(([id]) => { const el = $(id); if (el) el.checked = false; });
    OPP_RANGE_FILTERS.forEach(([id]) => { const el = $(id); if (el) el.value = ''; });

    try {
      const params = new URLSearchParams({ listingType });
      if (q)         params.set('q', q);
      if (seller)    params.set('seller', seller);
      if (category)  params.set('category', category);
      if (condition) params.set('condition', condition);
      if (minPrice)  params.set('minPrice', minPrice);
      if (maxPrice)  params.set('maxPrice', maxPrice);
      if (includeIlliquid) params.set('includeIlliquid', 'true');

      const res = await guardedFetch(`/api/opportunities/search?${params.toString()}`);
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.error || 'Search failed');
      renderOpportunityResults(data);
    } catch (err) {
      if (summary) summary.textContent = `Search failed: ${err.message}`;
      if (list) list.innerHTML = '';
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '🔍 Search'; }
    }
  }

  function bindOpportunitySearch() {
    on('opp-find-btn', 'click', findOpportunities);
    on('opp-search-input',    'keydown', e => { if (e.key === 'Enter') findOpportunities(); });
    on('opp-seller-input',    'keydown', e => { if (e.key === 'Enter') findOpportunities(); });
    on('opp-category-input',  'keydown', e => { if (e.key === 'Enter') findOpportunities(); });
    on('opp-min-price-input', 'keydown', e => { if (e.key === 'Enter') findOpportunities(); });
    on('opp-max-price-input', 'keydown', e => { if (e.key === 'Enter') findOpportunities(); });
    OPP_FILTERS.forEach(([id]) => on(id, 'change', applyOpportunityFilters));
    OPP_RANGE_FILTERS.forEach(([id]) => on(id, 'input', applyOpportunityFilters));
    on('opp-sort-select', 'change', applyOpportunityFilters);
    on('opp-close', 'click', closeOpportunitySection);
    on('opp-terapeak-connect', 'click', terapeakConnect);
    on('opp-terapeak-disconnect', 'click', terapeakDisconnect);
    document.addEventListener('keydown', e => {
      if (e.key === 'Escape' && !$('opportunity-section')?.classList.contains('hidden')) closeOpportunitySection();
    });
  }

  // ── Supplier File Analyzer (dropship profit calculator) ─────────────────
  let oppSupplierImageBase64 = '';
  let oppSupplierMimeType = 'image/jpeg';

  // ── Liquidation Lot / Manifest Analyzer ────────────────────────────────────────────────────
  // Paste a manifest, give the ask, get a buy/skip call. See LotAnalyzer.cs for the recovery
  // assumptions, the exact max-bid solve and every case where it refuses to make a call.
  let lotGrades       = [];
  let lotImageBase64  = '';
  let lotImageMime    = '';
  let lotScan         = null;   // last LotAnalysisResult, kept so re-sorting never re-analyzes

  const LOT_SAMPLE = [
    'Description,Qty,Unit Retail,Condition',
    'DeWalt DCD771C2 20V MAX Cordless Drill Driver Kit,4,169.00,Customer Return',
    'Ninja BL610 Professional 72oz Countertop Blender,6,89.99,Shelf Pull',
    'Sony WH-1000XM4 Wireless Noise Cancelling Headphones,2,348.00,Open Box',
    'Instant Pot Duo 7-in-1 6 Quart Pressure Cooker,5,99.95,Customer Return',
    'Anker PowerCore 10000 Portable Charger,20,25.99,New',
    'Assorted phone cases mixed models,40,19.99,New',
    'Keurig K-Classic K55 Single Serve Coffee Maker,3,129.99,Customer Return',
    'TOTAL,80,4021.55,',
  ].join('\n');

  function showLotsSection() {
    hideOverlaySections();
    $('new-listing-overlay')?.classList.add('hidden');
    $('lots-section')?.classList.remove('hidden');
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'lots'));
    loadLotGrades();
  }

  function closeLotsSection() {
    $('lots-section')?.classList.add('hidden');
    showDashboard();
  }

  function bindLotAnalyzer() {
    on('lot-close', 'click', closeLotsSection);
    on('lot-home',  'click', closeLotsSection);
    on('lot-analyze-btn', 'click', runLotAnalysis);
    on('lot-run-btn',     'click', runLotAnalysis);
    on('lot-sample-btn',  'click', () => {
      const box = $('lot-manifest');
      if (box) { box.value = LOT_SAMPLE; box.focus(); }
      if ($('lot-ask') && !parseFloat($('lot-ask').value)) $('lot-ask').value = '650';
      refreshLotCostLine();
    });
    on('lot-grade', 'change', () => applyLotGrade($('lot-grade').value, true));
    on('lot-clear-image', 'click', lotClearImage);

    ['lot-ask', 'lot-premium', 'lot-freight', 'lot-tax'].forEach(id => on(id, 'input', refreshLotCostLine));
    ['lot-grade', 'lot-handling', 'lot-target-roi'].forEach(id => on(id, 'change', saveLotPrefs));

    const zone  = $('lot-drop-zone');
    const input = $('lot-file-input');
    zone?.addEventListener('click', e => { if (e.target !== input) input?.click(); });
    zone?.addEventListener('dragover', e => { e.preventDefault(); zone.classList.add('drag-over'); });
    zone?.addEventListener('dragleave', e => { if (!zone.contains(e.relatedTarget)) zone.classList.remove('drag-over'); });
    zone?.addEventListener('drop', e => {
      e.preventDefault();
      zone.classList.remove('drag-over');
      const file = e.dataTransfer?.files?.[0];
      if (file) lotLoadFile(file);
    });
    input?.addEventListener('change', () => { if (input.files[0]) lotLoadFile(input.files[0]); });

    restoreLotPrefs();
    refreshLotCostLine();
  }

  // A CSV or text file is read as text and dropped straight into the paste box, because the
  // deterministic column parser is both free and unable to invent a line that wasn't on the
  // pallet. Only a photo needs the AI path.
  function lotLoadFile(file) {
    const mime = file.type || '';
    const name = (file.name || '').toLowerCase();
    const isText = mime.startsWith('text/') || /\.(csv|tsv|txt)$/.test(name);

    const reader = new FileReader();
    if (isText) {
      reader.onload = ev => {
        const box = $('lot-manifest');
        if (box) box.value = String(ev.target.result || '');
        addActivity('Manifest loaded', file.name || 'Manifest file');
        setLotStatus('Manifest file loaded — set the ask price and analyze.');
      };
      reader.readAsText(file);
      return;
    }
    if (!mime.startsWith('image/')) {
      setLotStatus('That file type can\'t be read. Use a CSV, a text file, or a photo of the manifest.');
      return;
    }
    lotImageMime = mime;
    reader.onload = ev => {
      lotImageBase64 = String(ev.target.result).split(',')[1];
      $('lot-preview-img').src = ev.target.result;
      $('lot-drop-zone')?.classList.add('hidden');
      $('lot-preview-wrap')?.classList.remove('hidden');
      addActivity('Manifest photo loaded', file.name || 'Manifest photo');
    };
    reader.readAsDataURL(file);
  }

  function lotClearImage() {
    lotImageBase64 = '';
    lotImageMime = '';
    $('lot-drop-zone')?.classList.remove('hidden');
    $('lot-preview-wrap')?.classList.add('hidden');
    if ($('lot-file-input')) $('lot-file-input').value = '';
  }

  async function loadLotGrades() {
    if (lotGrades.length) return;
    try {
      const res = await fetch('/api/lots/grades');
      const data = await res.json();
      lotGrades = data.grades || [];
    } catch { lotGrades = []; }

    const sel = $('lot-grade');
    if (!sel || !lotGrades.length) return;
    sel.innerHTML = lotGrades.map(g => `<option value="${esc(g.id)}">${esc(g.label)}</option>`).join('');
    // The grades are listed best-recovery first, so the first option is also the rosiest set of
    // assumptions in the app. Defaulting a pallet tool to that is how it flatters a bad lot, so
    // the default matches the server's: tested customer returns, the honest middle.
    const saved = localStorage.getItem('lotGrade');
    sel.value = saved && lotGrades.some(g => g.id === saved) ? saved
      : (lotGrades.some(g => g.id === 'customer_returns') ? 'customer_returns' : sel.value);
    applyLotGrade(sel.value, true);
  }

  // The two recovery numbers are shown, not hidden in the server — a buyer who has opened forty
  // of these pallets knows their own rate. Changing the grade resets them to that grade's default.
  function applyLotGrade(id, resetOverrides) {
    const grade = lotGrades.find(g => g.id === id);
    if (!grade) return;
    if (resetOverrides) {
      if ($('lot-sellable'))     $('lot-sellable').value     = grade.sellableRatePercent;
      if ($('lot-price-factor')) $('lot-price-factor').value = grade.priceFactorPercent;
    }
    const note = $('lot-grade-note');
    if (note) note.textContent = `${grade.note} Defaults: ${grade.sellableRatePercent}% of units sellable, at ${grade.priceFactorPercent}% of the sold-comp price. Edit either if you know better.`;
  }

  function saveLotPrefs() {
    localStorage.setItem('lotGrade', $('lot-grade')?.value || '');
    localStorage.setItem('lotHandling', $('lot-handling')?.value || '');
    localStorage.setItem('lotTargetRoi', $('lot-target-roi')?.value || '');
  }

  function restoreLotPrefs() {
    const handling = localStorage.getItem('lotHandling');
    const roi = localStorage.getItem('lotTargetRoi');
    if (handling && $('lot-handling')) $('lot-handling').value = handling;
    if (roi && $('lot-target-roi'))    $('lot-target-roi').value = roi;
  }

  function lotNum(id, fallback = 0) {
    const value = parseFloat($(id)?.value);
    return isFinite(value) ? value : fallback;
  }

  // Mirrors LotAnalyzer.CostOf exactly: tax follows the hammer plus the premium (which is how
  // auction houses bill it), freight is invoiced separately.
  function refreshLotCostLine() {
    const ask = Math.max(0, lotNum('lot-ask'));
    const premium = Math.round(ask * Math.max(0, lotNum('lot-premium')) / 100 * 100) / 100;
    const tax = Math.round((ask + premium) * Math.max(0, lotNum('lot-tax')) / 100 * 100) / 100;
    const freight = Math.max(0, lotNum('lot-freight'));
    const total = ask + premium + tax + freight;
    const el = $('lot-cost-line');
    if (!el) return;
    const parts = [];
    if (premium > 0) parts.push(`${moneyExact(premium)} premium`);
    if (tax > 0)     parts.push(`${moneyExact(tax)} tax`);
    if (freight > 0) parts.push(`${moneyExact(freight)} freight`);
    el.innerHTML = `All-in cost: <strong>${moneyExact(total)}</strong>${parts.length ? ` <span class="lot-cost-parts">(${parts.join(' · ')})</span>` : ''}`;
  }

  function setLotStatus(text) {
    const el = $('lot-status');
    if (el) el.textContent = text || '';
  }

  async function runLotAnalysis() {
    const manifest = ($('lot-manifest')?.value || '').trim();
    if (!manifest && !lotImageBase64) {
      setLotStatus('Paste the manifest, or drop a photo of it, first.');
      $('lot-manifest')?.focus();
      return;
    }

    const buttons = ['lot-analyze-btn', 'lot-run-btn'].map(id => $(id)).filter(Boolean);
    buttons.forEach(b => { b.disabled = true; b.textContent = 'Analyzing…'; });
    setLotStatus('Reading the manifest and pricing every line against sold comps…');
    $('lot-results').innerHTML = '<p class="opportunity-empty">Pricing each product against real sold listings. A long manifest takes a minute.</p>';

    const body = {
      manifestText: manifest,
      imageBase64: lotImageBase64 || null,
      mimeType: lotImageMime || 'image/jpeg',
      askPrice: Math.max(0, lotNum('lot-ask')),
      buyerPremiumPercent: Math.max(0, lotNum('lot-premium')),
      freightCost: Math.max(0, lotNum('lot-freight')),
      salesTaxPercent: Math.max(0, lotNum('lot-tax')),
      conditionGrade: $('lot-grade')?.value || 'customer_returns',
      sellableRatePercent: lotNum('lot-sellable', 0) || null,
      conditionPriceFactorPercent: lotNum('lot-price-factor', 0) || null,
      perUnitHandlingCost: Math.max(0, lotNum('lot-handling', 8)),
      targetRoiPercent: Math.max(0, lotNum('lot-target-roi', 40)),
      maxLines: parseInt($('lot-max-lines')?.value || '60', 10),
    };

    try {
      const res = await guardedFetch('/api/lots/analyze', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.error || 'The analysis failed.');
      lotScan = data;
      renderLot(data);
    } catch (err) {
      lotScan = null;
      ['lot-verdict', 'lot-summary', 'lot-concentration'].forEach(id => $(id)?.classList.add('hidden'));
      $('lot-results').innerHTML = `<p class="opportunity-empty">${esc(err.message || 'The analysis failed.')}</p>`;
      setLotStatus('');
    } finally {
      buttons.forEach(b => { b.disabled = false; });
      if ($('lot-analyze-btn')) $('lot-analyze-btn').textContent = '📦 Analyze This Lot';
      if ($('lot-run-btn'))     $('lot-run-btn').textContent     = '📦 Analyze This Lot';
    }
  }

  const LOT_VERDICTS = {
    buy:       { label: '✅ BUY IT',        cls: 'lot-v-buy' },
    buy_below: { label: '💰 BUY IT LOWER',  cls: 'lot-v-below' },
    thin:      { label: '⚠️ THIN',          cls: 'lot-v-thin' },
    skip:      { label: '⛔ SKIP',          cls: 'lot-v-skip' },
    dead:      { label: '☠️ DEAD LOT',      cls: 'lot-v-skip' },
    no_ask:    { label: '💵 ADD THE ASK',   cls: 'lot-v-thin' },
    no_data:   { label: '❓ CAN’T CALL IT', cls: 'lot-v-nodata' },
  };

  function renderLot(data) {
    const note = $('lot-source-note');
    if (note) note.textContent = data.sourceNote || '';

    const notes = [];
    if (data.error) notes.push(data.error);
    if (data.dataWarning) notes.push(data.dataWarning);
    (data.warnings || []).forEach(w => notes.push(w));
    const warn = $('lot-warning');
    if (notes.length) { warn.textContent = notes.join(' '); warn.classList.remove('hidden'); }
    else warn?.classList.add('hidden');

    renderLotVerdict(data);
    renderLotSummary(data);
    renderLotConcentration(data);
    renderLotRows(data);

    setLotStatus(
      `${data.linesExtracted} line${data.linesExtracted === 1 ? '' : 's'} read · ${data.linesPriced} priced · ` +
      `${data.productsPriced} distinct product${data.productsPriced === 1 ? '' : 's'} looked up` +
      (data.linesExcluded ? ` · ${data.linesExcluded} held out of the totals` : '') +
      (data.terapeakScrapesUsed ? ` · ${data.terapeakScrapesUsed} Terapeak check${data.terapeakScrapesUsed === 1 ? '' : 's'}` : ''));
  }

  function renderLotVerdict(data) {
    const el = $('lot-verdict');
    if (!el) return;
    const v = LOT_VERDICTS[data.verdict] || LOT_VERDICTS.no_data;
    const t = data.totals || {};

    // The two prices worth walking into a negotiation with. Both are exact arithmetic on the
    // manifest, not a rule of thumb — see LotAnalyzer.MaxAsk.
    const chips = [];
    if (data.maxBid != null)
      chips.push(`<span class="lot-chip lot-chip-strong">Pay up to <strong>${moneyExact(data.maxBid)}</strong> for ${Number(data.targetRoiPercent).toFixed(0)}% ROI</span>`);
    if (data.breakEvenAsk != null)
      chips.push(`<span class="lot-chip">Break-even at <strong>${moneyExact(data.breakEvenAsk)}</strong></span>`);
    if (t.totalCost > 0)
      chips.push(`<span class="lot-chip">You'd pay <strong>${moneyExact(t.totalCost)}</strong> all-in</span>`);
    chips.push(`<span class="lot-chip">${Number(data.coveragePercent || 0).toFixed(0)}% of the manifest's value priced</span>`);

    el.className = `lot-verdict ${v.cls}`;
    el.innerHTML = `
      <div class="lot-verdict-head">
        <span class="lot-verdict-badge">${v.label}</span>
        <span class="lot-verdict-net">${t.netProfit != null && (t.totalCost > 0) ? moneyExact(t.netProfit) + ' net' : ''}</span>
      </div>
      <p class="lot-verdict-note">${esc(data.verdictNote || '')}</p>
      <div class="lot-chips">${chips.join('')}</div>`;
    el.classList.remove('hidden');
  }

  function renderLotSummary(data) {
    const el = $('lot-summary');
    if (!el) return;
    const t = data.totals || {};
    const a = data.assumptions || {};

    const tiles = [
      { label: 'Net profit on the lot', value: t.totalCost > 0 ? moneyExact(t.netProfit) : '—',
        sub: t.roiPercent != null ? `${Number(t.roiPercent).toFixed(1)}% ROI · ${t.marginPercent != null ? Number(t.marginPercent).toFixed(0) + '% margin' : ''}` : 'Enter the ask price',
        tone: t.totalCost > 0 ? (t.netProfit > 0 ? 'good' : 'bad') : '' },
      { label: 'What it resells for', value: money(t.grossResale),
        sub: `${Number(t.sellableUnits || 0).toFixed(0)} sellable of ${t.manifestUnits || 0} units on the manifest`, tone: '' },
      { label: 'eBay fees + shipping', value: money((t.estimatedFees || 0) + (t.estimatedShipCost || 0)),
        sub: `${money(t.estimatedFees)} fees · ${money(t.estimatedShipCost)} to ship every unit`, tone: 'warn' },
      { label: 'Clears after selling costs', value: money(t.netRecovery),
        sub: 'Before you pay for the lot', tone: '' },
      { label: 'Cost per sellable unit', value: t.costPerSellableUnit > 0 ? moneyExact(t.costPerSellableUnit) : '—',
        sub: 'What each usable item costs you', tone: '' },
      { label: 'Manifest "retail value"', value: t.manifestRetailTotal > 0 ? money(t.manifestRetailTotal) : '—',
        sub: t.resalePercentOfRetail != null
          ? `Really worth ${Number(t.resalePercentOfRetail).toFixed(0)}% of that on eBay`
          : 'No retail column on this manifest',
        tone: t.resalePercentOfRetail != null && t.resalePercentOfRetail < 40 ? 'warn' : '' },
      { label: 'Time to clear the lot', value: t.daysToSellSlowestLine != null ? `${t.daysToSellSlowestLine}d` : '—',
        sub: t.medianDaysToSell != null ? `Typical line ${t.medianDaysToSell}d · slowest line sets the date` : 'No velocity data',
        tone: t.daysToSellSlowestLine != null && t.daysToSellSlowestLine > 180 ? 'warn' : '' },
      { label: 'Recovery assumed', value: `${Number(a.sellableRatePercent || 0).toFixed(0)}%`,
        sub: `${esc(a.label || '')} · sells at ${Number(a.priceFactorPercent || 0).toFixed(0)}% of comps`, tone: '' },
    ];

    el.innerHTML = tiles.map(t2 => `
      <div class="inv-tile ${t2.tone ? 'inv-tile-' + t2.tone : ''}">
        <div class="inv-tile-label">${esc(t2.label)}</div>
        <div class="inv-tile-value">${esc(t2.value)}</div>
        <div class="inv-tile-sub">${t2.sub}</div>
      </div>`).join('');
    el.classList.remove('hidden');
  }

  function renderLotConcentration(data) {
    const el = $('lot-concentration');
    if (!el) return;
    const c = data.concentration || {};
    if (!c.linesForEightyPercent) { el.classList.add('hidden'); return; }

    const key = (data.items || []).filter(i => i.carriesTheValue).slice(0, 6);
    el.innerHTML = `
      <div class="lot-conc-head">💎 ${c.linesForEightyPercent} line${c.linesForEightyPercent === 1 ? '' : 's'} carry 80% of this lot's value</div>
      ${c.warning ? `<p class="lot-conc-warn">${esc(c.warning)}</p>` : ''}
      <ol class="lot-conc-list">${key.map(i => `
        <li><span class="lot-conc-name">${esc(i.description)}</span>
            <span class="lot-conc-share">${Number(i.valueSharePercent).toFixed(1)}% · ${money(i.netRecovery)}</span></li>`).join('')}</ol>
      <p class="lot-conc-foot">These are the items to physically check before you pay. Everything else on the manifest is padding by value.</p>`;
    el.classList.remove('hidden');
  }

  const LOT_STATUS = {
    priced:   { label: 'Priced',   cls: 'lot-s-priced' },
    thin:     { label: 'Thin data', cls: 'lot-s-thin' },
    excluded: { label: 'Held out', cls: 'lot-s-excluded' },
    no_data:  { label: 'No comps', cls: 'lot-s-nodata' },
  };

  function renderLotRows(data) {
    const el = $('lot-results');
    const rows = data.items || [];
    if (!rows.length) {
      el.innerHTML = '<p class="opportunity-empty">No product lines could be read from that. Paste it as a table (description, quantity, retail), or drop a photo of the manifest.</p>';
      return;
    }

    el.innerHTML = `
      <table class="inv-table lot-table">
        <thead>
          <tr>
            <th>Item</th>
            <th class="num">Qty</th>
            <th class="num">Retail ea.</th>
            <th class="num">Resale ea.</th>
            <th class="num">Sellable</th>
            <th class="num">Clears</th>
            <th class="num">Cost share</th>
            <th class="num">Net</th>
            <th class="num">% of lot</th>
            <th>Evidence</th>
          </tr>
        </thead>
        <tbody>${rows.map(lotRowHtml).join('')}</tbody>
      </table>`;
  }

  function lotRowHtml(item) {
    const s = LOT_STATUS[item.status] || LOT_STATUS.no_data;
    const priced = item.status === 'priced' || item.status === 'thin';
    // "Priced as" is only worth saying when the lookup ran on different words than the line's own —
    // otherwise it implies a match that wasn't made against this exact wording.
    const pricedAs = item.pricedAs && item.pricedAs !== item.description
      ? `<div class="inv-sub">priced as “${esc(item.pricedAs)}”</div>` : '';

    return `
      <tr class="${item.carriesTheValue ? 'lot-row-key' : ''} ${priced ? '' : 'wo-row-muted'}">
        <td class="inv-cell-title">
          <div>${esc(item.description)}${item.carriesTheValue ? ' <span class="lot-key-flag">carries the value</span>' : ''}</div>
          ${pricedAs}
          ${item.condition ? `<div class="inv-sub">${esc(item.condition)}</div>` : ''}
        </td>
        <td class="num">${item.quantity}</td>
        <td class="num">${item.unitRetail != null ? moneyExact(item.unitRetail) : '<span class="inv-dash">—</span>'}</td>
        <td class="num">${item.unitResale != null
            ? `${moneyExact(item.unitResale)}${item.compUnitPrice != null && item.compUnitPrice !== item.unitResale
                ? `<div class="inv-sub">comps ${moneyExact(item.compUnitPrice)}</div>` : ''}`
            : '<span class="inv-dash">—</span>'}</td>
        <td class="num">${priced ? Number(item.sellableUnits).toFixed(1) : '<span class="inv-dash">—</span>'}</td>
        <td class="num">${priced ? money(item.netRecovery) : '<span class="inv-dash">—</span>'}</td>
        <td class="num">${item.allocatedCost > 0 ? money(item.allocatedCost) : '<span class="inv-dash">—</span>'}</td>
        <td class="num">${item.allocatedCost > 0
            ? `<span class="${item.netProfit > 0 ? 'inv-gap-good' : 'inv-gap-bad'}">${money(item.netProfit)}</span>${
                item.roiPercent != null ? `<div class="inv-sub">${Number(item.roiPercent).toFixed(0)}% ROI</div>` : ''}`
            : '<span class="inv-dash">—</span>'}</td>
        <td class="num">${item.valueSharePercent > 0 ? Number(item.valueSharePercent).toFixed(1) + '%' : '<span class="inv-dash">—</span>'}</td>
        <td class="inv-cell-verdict">
          <span class="inv-verdict ${s.cls}">${s.label}</span>
          <div class="inv-note">${esc(item.statusNote || '')}</div>
          ${item.estimatedDaysToSell != null && priced
            ? `<div class="inv-signals">~${item.estimatedDaysToSell}d to clear this line</div>` : ''}
        </td>
      </tr>`;
  }

  function bindSupplierAnalyzer() {
    const dropZone  = $('opp-supplier-drop-zone');
    const fileInput = $('opp-supplier-file-input');

    dropZone?.addEventListener('click', e => {
      if (e.target !== fileInput) fileInput?.click();
    });
    dropZone?.addEventListener('dragover', e => {
      e.preventDefault();
      dropZone.classList.add('drag-over');
    });
    dropZone?.addEventListener('dragleave', e => {
      if (!dropZone.contains(e.relatedTarget)) dropZone.classList.remove('drag-over');
    });
    dropZone?.addEventListener('drop', e => {
      e.preventDefault();
      dropZone.classList.remove('drag-over');
      const file = e.dataTransfer.files[0] ||
        [...(e.dataTransfer.items || [])].find(i => i.kind === 'file' && i.type.startsWith('image/'))?.getAsFile();
      if (file) oppSupplierLoadFile(file);
    });
    fileInput?.addEventListener('change', () => {
      if (fileInput.files[0]) oppSupplierLoadFile(fileInput.files[0]);
    });
    dropZone?.addEventListener('beforeinput', e => e.preventDefault());
    dropZone?.addEventListener('paste', e => {
      e.preventDefault();
      e.stopPropagation();
      const imageItem = [...(e.clipboardData?.items || [])].find(i => i.type.startsWith('image/'));
      const file = imageItem?.getAsFile();
      if (file) oppSupplierLoadFile(file, 'Pasted supplier file');
    });

    on('opp-supplier-btn-clear', 'click', oppSupplierClear);
    on('opp-supplier-btn-reanalyze', 'click', () => oppSupplierAnalyze());
  }

  function oppSupplierLoadFile(file, label = file.name || 'Supplier file') {
    const mime = file.type || 'image/png';
    if (mime && !mime.startsWith('image/')) return;
    oppSupplierMimeType = mime;
    const reader = new FileReader();
    reader.onload = ev => {
      oppSupplierImageBase64 = ev.target.result.split(',')[1];
      $('opp-supplier-preview-img').src = ev.target.result;
      $('opp-supplier-drop-zone')?.classList.add('hidden');
      $('opp-supplier-preview-wrap')?.classList.remove('hidden');
      addActivity('Supplier file loaded', label);
      oppSupplierAnalyze();
    };
    reader.readAsDataURL(file);
  }

  function oppSupplierClear() {
    oppSupplierImageBase64 = '';
    $('opp-supplier-drop-zone')?.classList.remove('hidden');
    $('opp-supplier-preview-wrap')?.classList.add('hidden');
    $('opp-supplier-results')?.classList.add('hidden');
    $('opp-supplier-btn-reanalyze')?.classList.add('hidden');
    if ($('opp-supplier-file-input')) $('opp-supplier-file-input').value = '';
  }

  async function oppSupplierAnalyze() {
    if (!oppSupplierImageBase64) return;
    const results = $('opp-supplier-results');
    const summary = $('opp-supplier-summary');
    const list    = $('opp-supplier-list');
    const reanalyzeBtn = $('opp-supplier-btn-reanalyze');

    results?.classList.remove('hidden');
    if (summary) summary.innerHTML = 'Reading the file and checking sold comps for each product — this can take a minute…';
    if (list) list.innerHTML = '';
    if (reanalyzeBtn) reanalyzeBtn.classList.add('hidden');

    try {
      const res = await guardedFetch('/api/opportunities/analyze-supplier-file', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageBase64: oppSupplierImageBase64, mimeType: oppSupplierMimeType })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.error || 'Analysis failed');
      renderSupplierResults(data);
    } catch (err) {
      if (summary) summary.textContent = `Analysis failed: ${err.message}`;
    } finally {
      reanalyzeBtn?.classList.remove('hidden');
    }
  }

  function confidenceLabel(score) {
    if (score == null) return null;
    if (score >= 70) return 'High';
    if (score >= 40) return 'Medium';
    return 'Low';
  }

  function buildComparableRowHtml(c) {
    const titleCell = c.itemUrl
      ? `<a href="${esc(c.itemUrl)}" target="_blank" rel="noopener">${esc(c.title)}</a>`
      : esc(c.title);
    return `<tr>
      <td>${titleCell}</td>
      <td>$${(c.soldPrice ?? 0).toFixed(2)}</td>
      <td>${c.shipping != null ? `$${c.shipping.toFixed(2)}` : '—'}</td>
      <td>${esc(c.condition || '—')}</td>
      <td>${c.soldDate ? esc(new Date(c.soldDate).toLocaleDateString()) : '—'}</td>
      <td>${c.matchScore ?? '—'}</td>
    </tr>`;
  }

  function buildSupplierRowHtml(it, idx) {
    const hasPricing  = it.estimatedProfitPercent != null;
    const profitClass = !hasPricing ? 'flat' : it.estimatedProfitPercent > 0 ? 'good' : 'bad';
    const profitPct    = hasPricing ? `${it.estimatedProfitPercent > 0 ? '+' : ''}${it.estimatedProfitPercent}%` : '—';
    const profitAmount = it.estimatedProfit == null ? '' : ` (${it.estimatedProfit > 0 ? '+' : ''}$${Math.abs(it.estimatedProfit).toFixed(2)})`;
    const resalePrice = it.estimatedResalePrice ?? it.ebaySoldMedian ?? it.ebaySoldAverage;

    // Generic labels only — never mention the underlying database/table/provider.
    let sourceTag;
    if (it.localDataAvailable) {
      const conf = confidenceLabel(it.confidenceScore);
      sourceTag = `<span class="opp-verified-tag opp-verified">✓ Local market research match${conf ? ` · ${conf} confidence` : ''}</span>`;
    } else if (it.isVerified) {
      sourceTag = '<span class="opp-verified-tag opp-verified">✓ Sold-history match</span>';
    } else {
      sourceTag = `<span class="opp-verified-tag opp-estimate">${esc(it.localDataMessage || 'No reliable sold-history matches found.')}</span>`;
    }

    const comparables = it.comparableListings || [];
    const compToggle = comparables.length > 0
      ? `<button type="button" class="btn btn-ghost small opp-comp-toggle" data-comp-idx="${idx}">View comparable sold listings (${comparables.length})</button>`
      : '';
    const compPanel = comparables.length > 0
      ? `<div class="opp-comp-panel hidden" id="opp-comp-panel-${idx}">
          <table class="opp-comp-table">
            <thead><tr><th>Title</th><th>Sold price</th><th>Shipping</th><th>Condition</th><th>Sold date</th><th>Match</th></tr></thead>
            <tbody>${comparables.map(buildComparableRowHtml).join('')}</tbody>
          </table>
        </div>`
      : '';

    return `<div class="opp-result-row opp-supplier-row">
      <div class="opp-result-info">
        <div class="opp-result-title" title="${esc(it.productName)}">${esc(it.productName)}</div>
        <div class="opp-result-meta">${esc(it.notes || it.searchQuery)}</div>
        ${it.localDataAvailable ? `<div class="opp-result-meta">${it.comparableCount} comparable sold listing${it.comparableCount === 1 ? '' : 's'} found in local market research</div>` : ''}
        ${compToggle}
        ${compPanel}
      </div>
      <div class="opp-result-costs">
        <div class="opp-cost-line"><span>Wholesale cost</span><strong>${it.wholesaleCostUsd > 0 ? `$${it.wholesaleCostUsd.toFixed(2)}` : '—'}</strong></div>
        <div class="opp-cost-line"><span>Estimated resale price</span><strong>${resalePrice != null ? `$${resalePrice.toFixed(2)}` : '—'}</strong></div>
        <div class="opp-cost-line"><span>Average sold price</span><strong>${it.ebaySoldAverage != null ? `$${it.ebaySoldAverage.toFixed(2)}` : '—'}</strong></div>
        <div class="opp-cost-line"><span>Median sold price</span><strong>${it.ebaySoldMedian != null ? `$${it.ebaySoldMedian.toFixed(2)}` : '—'}</strong></div>
        ${it.sellThroughPercent != null ? `<div class="opp-cost-line"><span>Sell-through</span><strong>${it.sellThroughPercent}%</strong></div>` : ''}
        ${it.liquidityLevel != null
          ? `<div class="opp-cost-line"><span>Est. time to sell</span><strong>${it.liquidityLevel}${it.estimatedDaysToSell != null ? ` · ~${it.estimatedDaysToSell}d` : ''}</strong></div>`
          : it.liquidityMessage ? `<div class="opp-cost-line"><span>Est. time to sell</span><strong class="opp-liquidity-unknown">${esc(it.liquidityMessage)}</strong></div>` : ''}
      </div>
      <div class="opp-result-value">
        <div class="opp-cost-line"><span>eBay fees (est.)</span><strong>${it.estimatedFees != null ? `$${it.estimatedFees.toFixed(2)}` : '—'}</strong></div>
        <div class="opp-cost-line"><span>Shipping (est.)</span><strong>${it.avgShipping != null ? `$${it.avgShipping.toFixed(2)}` : '—'}</strong></div>
        <div class="opp-cost-line"><span>Quick-sale / High-price target</span><strong>${it.quickSalePrice != null ? `$${it.quickSalePrice.toFixed(2)}` : '—'} / ${it.highPriceTarget != null ? `$${it.highPriceTarget.toFixed(2)}` : '—'}</strong></div>
        <div class="opp-cost-line"><span>ROI / Margin</span><strong>${it.roiPercent != null ? `${it.roiPercent}%` : '—'} / ${it.marginPercent != null ? `${it.marginPercent}%` : '—'}</strong></div>
        <div class="opp-cost-line profit ${profitClass}"><span>Est. net profit / ROI</span><strong>${profitPct}${profitAmount}</strong></div>
        ${sourceTag}
        ${(it.warnings || []).length > 0 ? `<ul class="opp-score-explanation">${it.warnings.map(w => `<li class="opp-warning">⚠ ${esc(w)}</li>`).join('')}</ul>` : ''}
      </div>
      <div class="opp-result-score">
        ${it.opportunityScore != null ? `<div class="opp-score-badge ${it.opportunityScore >= 60 ? 'good' : it.opportunityScore >= 35 ? 'mid' : 'bad'}">${it.opportunityScore}</div><span class="opp-result-score-label">Score</span>` : ''}
        <a href="${esc(it.terapeakUrl)}" target="_blank" rel="noopener" class="btn btn-ghost small">Research ↗</a>
        <button type="button" class="btn btn-primary small" onclick="window.__oppListSupplierItem(${JSON.stringify(it.productName).replace(/"/g, '&quot;')})">List this</button>
      </div>
    </div>`;
  }

  function renderSupplierResults(data) {
    const summary = $('opp-supplier-summary');
    const list = $('opp-supplier-list');
    if (!summary || !list) return;

    const items = data.items || [];
    if (items.length === 0) {
      summary.innerHTML = 'No products could be extracted from that file. Try a clearer photo of a price list or product.';
      list.innerHTML = '';
      return;
    }

    const priced = data.productsPriced || 0;
    summary.innerHTML = `Extracted <strong>${data.productsExtracted}</strong> product${data.productsExtracted === 1 ? '' : 's'} — ` +
      `<strong>${priced}</strong> priced against real sold comps. Ranked by estimated profit.`;
    list.innerHTML = items.map(buildSupplierRowHtml).join('');

    list.querySelectorAll('.opp-comp-toggle').forEach(btn => {
      btn.addEventListener('click', () => {
        const panel = $(`opp-comp-panel-${btn.dataset.compIdx}`);
        panel?.classList.toggle('hidden');
      });
    });
  }

  // Jumps to AI Listing and reuses the existing quick-fill-by-name pipeline so a profitable
  // supplier find goes straight to a drafted listing without any new listing-creation code.
  window.__oppListSupplierItem = function (productName) {
    location.hash = 'ai';
    setTimeout(() => {
      const input = $('nl-quickfill-input');
      if (input) {
        input.value = productName;
        nlQuickFillByName();
      }
    }, 150);
  };

  function goHome() { location.hash = 'dashboard'; }

  function bindHomeButtons() {
    on('nl-home',  'click', goHome);
    on('opp-home', 'click', goHome);
    on('pl-home',  'click', goHome);
  }

  async function activateLicensePage() {
    const keyInput = $('lp-key-input');
    const msg      = $('lp-activate-msg');
    const btn      = $('lp-activate-btn');
    const key      = keyInput?.value.trim();
    if (!key) { if (msg) { msg.textContent = 'Enter a license key first.'; msg.className = 'sd-test-msg error'; } return; }

    if (btn) { btn.disabled = true; btn.textContent = 'Checking…'; }
    if (msg) { msg.textContent = 'Contacting license server…'; msg.className = 'sd-test-msg'; }

    try {
      await fetch('/api/setup/save', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ licenseKey: key })
      });
      const status = await fetch('/api/license/activate', { method: 'POST' }).then(r => r.json());
      updateLicenseUI(status);
      addActivity('License activated', status.message || status.tier);
      if (status.valid && keyInput) { keyInput.value = ''; keyInput.placeholder = '(saved — leave blank to keep)'; }
    } catch (err) {
      if (msg) { msg.textContent = 'Activation failed: ' + err.message; msg.className = 'sd-test-msg error'; }
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'Activate'; }
    }
  }

  async function buyProLicense(annual = false) {
    const btn = annual ? $('lp-buy-annual-btn') : $('lp-buy-pro-btn');
    const msg = $('lp-buy-msg');
    const endpoint = annual ? '/api/stripe/checkout/annual' : '/api/stripe/checkout';
    const label    = 'Get License Key';
    if (btn) { btn.disabled = true; btn.textContent = 'Opening checkout…'; }
    if (msg) { msg.textContent = ''; }
    try {
      const res = await fetch(endpoint, { method: 'POST' }).then(r => r.json());
      if (res.url) {
        window.open(res.url, '_blank');
        if (msg) { msg.textContent = 'Stripe checkout opened. After payment, check your email for your Pro license key.'; msg.className = 'sd-test-msg ok'; }
      } else {
        if (msg) { msg.textContent = res.error || 'Could not start checkout.'; msg.className = 'sd-test-msg error'; }
      }
    } catch (err) {
      if (msg) { msg.textContent = 'Checkout failed: ' + err.message; msg.className = 'sd-test-msg error'; }
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = label; }
    }
  }

  function setViewMode(mode) {
    viewMode = mode;
    localStorage.setItem('ingListingViewMode', mode);
    $('btn-card-view')?.classList.toggle('active', mode === 'cards');
    $('btn-table-view')?.classList.toggle('active', mode === 'table');
    $('btn-card-view')?.setAttribute('aria-pressed', String(mode === 'cards'));
    $('btn-table-view')?.setAttribute('aria-pressed', String(mode === 'table'));
    $('listings-section')?.classList.toggle('table-mode', mode === 'table');
    // The table needs ~1,000px to show twelve columns without a horizontal
    // scrollbar, and the dashboard's two-column grid leaves it ~880. Table
    // view drops the activity rail below the table instead of beside it.
    $('listings-section')?.closest('.content-grid')?.classList.toggle('table-mode', mode === 'table');
  }

  function restoreListingViewMode() {
    const saved = localStorage.getItem('ingListingViewMode');
    setViewMode(saved === 'table' ? 'table' : 'cards');
  }

  async function checkLicenseStatus() {
    try {
      const status = await fetch('/api/license/status').then(r => r.json());
      updateLicenseUI(status);
    } catch { /* non-fatal */ }
  }

  async function checkTrialStatus() {
    // Freeware — always active, hide trial badge, show Freeware badge
    const trialBadge   = $('trial-badge');
    const licenseBadge = $('license-badge');
    if (trialBadge) trialBadge.classList.add('hidden');
    if (licenseBadge) { licenseBadge.textContent = 'Freeware'; licenseBadge.className = 'badge badge-on'; }
    $('license-nag')?.classList.add('hidden');
  }

  function updateLicenseUI(status) {
    const badge = $('license-badge');
    if (!status?.checked) return;

    const dot = $('nav-license-dot');
    if (status.valid) {
      const label = status.tier === 'pro' ? 'Pro License'
                  : status.tier === 'free' ? 'Free License'
                  : 'Licensed';
      if (badge) { badge.textContent = label; badge.className = 'badge badge-on'; }
      if (dot) dot.className = `nav-license-dot ${status.tier === 'unverified' ? 'dot-warn' : 'dot-on'}`;
      $('license-nag')?.classList.add('hidden');
    } else {
      if (badge) { badge.textContent = 'Freeware'; badge.className = 'badge badge-on'; }
      if (dot) dot.className = 'nav-license-dot dot-on';
      $('license-nag')?.classList.add('hidden');
    }
    const activateMsg = $('license-activate-msg');
    if (activateMsg && status.message) {
      activateMsg.textContent = status.message;
      activateMsg.className = 'sd-test-msg ' + (status.valid ? 'ok' : 'error');
    }

    // Update the license page status banner
    const banner    = $('lp-status-banner');
    const tierLabel = $('lp-tier-label');
    const statusMsg = $('lp-status-msg');
    const pageMsg   = $('lp-activate-msg');
    if (banner) {
      banner.className = 'lp-status-banner ' +
        (status.valid ? (status.tier === 'unverified' ? 'lp-unverified' : 'lp-licensed') : 'lp-unlicensed');
    }
    if (tierLabel) {
      tierLabel.textContent = status.valid
        ? (status.tier === 'pro' ? 'Pro License Active' : status.tier === 'free' ? 'Free License Active' : 'License Active (offline)')
        : 'Freeware';
    }
    if (statusMsg) statusMsg.textContent = status.message || (status.valid ? 'Your license is valid.' : 'ING Listing Engine™ is Freeware — use key ING-BETA-2025.');
    if (pageMsg && status.message) {
      pageMsg.textContent = status.message;
      pageMsg.className = 'sd-test-msg ' + (status.valid ? 'ok' : 'error');
    }
  }

  async function activateLicense() {
    const keyInput = $('s-license-key');
    const msg      = $('license-activate-msg');
    const btn      = $('btn-activate-license');
    const key      = keyInput?.value.trim();
    if (!key) { if (msg) { msg.textContent = 'Enter a license key first.'; msg.className = 'sd-test-msg error'; } return; }

    if (btn) { btn.disabled = true; btn.textContent = 'Checking…'; }
    if (msg) { msg.textContent = 'Contacting license server…'; msg.className = 'sd-test-msg'; }

    try {
      // Save key first, then activate
      await fetch('/api/setup/save', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ licenseKey: key })
      });
      const status = await fetch('/api/license/activate', { method: 'POST' }).then(r => r.json());
      updateLicenseUI(status);
      addActivity('License activated', status.message || status.tier);
      if (status.valid) { if (keyInput) { keyInput.value = ''; keyInput.placeholder = '(saved — leave blank to keep)'; } }
    } catch (err) {
      if (msg) { msg.textContent = 'Activation failed: ' + err.message; msg.className = 'sd-test-msg error'; }
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'Activate'; }
    }
  }

  async function checkSetupOnLoad() {
    // If returning from the eBay OAuth server relay, show connected state
    const params = new URLSearchParams(location.search);
    if (params.get('ebay_connected') === '1') {
      history.replaceState({}, '', '/');
      updateAuthUI(true);
      addActivity('eBay connected', 'OAuth login completed successfully.');
      showResult('ok', '✓ Connected to eBay successfully!');
    } else if (params.get('ebay_error')) {
      history.replaceState({}, '', '/');
      showResult('error', `eBay login failed: ${params.get('ebay_error')}`);
    }

    try {
      const status = await fetch('/api/setup/status').then(r => r.json());
      await populateSetupFields(status);
      updateSetupChecklist(!!status.hasAnthropicKey, isConnected, !!status.hasOpenAiKey);
      if (!status.isComplete) {
        addActivity('Finish setup', 'Complete the 2 steps on the home page to activate.');
      }
      // No startup pop-up: the home-page "2 steps to activate" checklist handles
      // entering the Claude key and connecting eBay directly on the dashboard.
    } catch {
      addActivity('Setup status unavailable', 'Could not read local credential status.');
    }
  }

  async function populateSetupFields(status) {
    if ($('s-sandbox')) $('s-sandbox').checked = status?.ebaySandbox ?? true;
    try {
      const f = await fetch('/api/setup/fields').then(r => r.json());
      $('s-sandbox').checked = f.ebaySandbox ?? true;
      setValue('s-client-id', f.ebayClientId);
      setValue('s-dev-id',    f.ebayDevId);
      setValue('s-runame',    f.ebayRuName);
      setValue('s-fulfillment', f.ebayFulfillmentPolicyId);
      setValue('s-payment', f.ebayPaymentPolicyId);
      setValue('s-return', f.ebayReturnPolicyId);
      if ($('s-license-key')) $('s-license-key').placeholder = f.hasLicenseKey ? `(saved: ${f.licenseKeyPreview} — leave blank to keep)` : 'ING-FREE-XXXX or ING-PRO-XXXX';
      $('s-anthropic-key').placeholder = f.hasAnthropicKey ? '(saved - leave blank to keep)' : 'sk-ant-...';
      if ($('s-openai-key')) $('s-openai-key').placeholder = f.hasOpenAiKey ? '(saved - leave blank to keep)' : 'sk-...';
      // Image generation settings — API Credentials modal
      setVal('s-image-gen-mode', f.imageGenMode || 'disabled');
      setVal('s-local-sd-endpoint', f.localSdEndpoint || 'http://127.0.0.1:7860');
      setVal('s-local-sd-backend', f.localSdBackend || 'automatic1111');
      setModelSelect('s-local-sd-model', f.localSdModelName || '');
      setValue('s-image-prompt', f.imagePromptTemplate || '');
      applyImageGenModeVisibility(f.imageGenMode || 'disabled');
      applyComfyUiModelVisibility(f.localSdBackend || 'automatic1111');
      // Image generation settings — Settings page (pg- fields)
      const pgMode = computePgImggenMode(f.imageGenMode, f.localSdBackend);
      setVal('pg-imggen-mode', pgMode);
      applyPgImggenVisibility(pgMode);
      setVal('pg-imggen-endpoint', f.localSdEndpoint || 'http://127.0.0.1:7860');
      setModelSelect('pg-imggen-model', f.localSdModelName || '');
      $('s-client-secret').placeholder = f.hasEbayClientSecret ? '(saved - leave blank to keep)' : 'PRD-abc123...';
      $('s-user-token').placeholder = f.hasEbayUserToken ? '(saved - leave blank to keep)' : 'AgAAAA...';
      // eBay developer section hides when eBay app creds are pre-configured.
      // AI provider section stays visible until the user has their own Anthropic key.
      const ebayPreconfigured = f.hasEbayClientId && f.hasEbayClientSecret;
      const fullyPreconfigured = f.hasAnthropicKey && ebayPreconfigured;
      document.getElementById('setup-ai-provider')?.classList.toggle('hidden', fullyPreconfigured);
      document.getElementById('setup-ebay-developer')?.classList.toggle('hidden', ebayPreconfigured);
      $('btn-paste-token')?.classList.toggle('hidden', ebayPreconfigured);
      const notice = document.getElementById('setup-preconfigured-notice');
      const modalDesc = document.getElementById('setup-modal-desc');
      if (fullyPreconfigured) {
        notice?.classList.remove('hidden');
        if (notice) notice.innerHTML = '<strong>✓ All credentials configured.</strong> Click <strong>Save and Connect eBay</strong> below to link your eBay account.';
        if (modalDesc) modalDesc.textContent = 'All credentials are configured. Connect your eBay account to get started.';
      } else if (ebayPreconfigured) {
        notice?.classList.remove('hidden');
        if (notice) notice.innerHTML = '<strong>✓ eBay is pre-configured.</strong> Enter your Anthropic API key above to enable AI listing analysis (<a href="https://console.anthropic.com/settings/keys" target="_blank" style="color:#0369a1">get one at console.anthropic.com</a>), then click Save and Connect eBay.';
        if (modalDesc) modalDesc.textContent = 'Enter your Anthropic API key, then connect your eBay account to get started.';
      } else {
        notice?.classList.add('hidden');
      }
      // Listing defaults — Settings page
      setVal('pg-default-zip',          f.defaultPostalCode      || '');
      setVal('pg-default-country',      f.defaultCountry         || 'US');
      setVal('pg-default-package-type', f.defaultPackageType     || 'PACKAGE_THICK_ENVELOPE');
      setVal('pg-default-handling',     String(f.defaultHandlingTimeDays || 1));
      setVal('pg-default-weight-lbs',   String(f.defaultWeightLbs  || 0));
      setVal('pg-default-weight-oz',    String(f.defaultWeightOz   || 0));
      setVal('pg-default-length',       String(f.defaultLengthIn   || 0));
      setVal('pg-default-width',        String(f.defaultWidthIn    || 0));
      setVal('pg-default-height',       String(f.defaultHeightIn   || 0));
      setVal('pg-default-fulfillment',  f.defaultFulfillmentPolicyId || '');
      if ($('pg-default-best-offer')) $('pg-default-best-offer').checked = !!f.defaultBestOffer;
      // Show the policy name next to the ID
      if (f.defaultFulfillmentPolicyId) {
        const nameEl = $('pg-default-fulfillment-name');
        if (nameEl) {
          const match = window._ebayPolicies?.fulfillmentPolicies?.find(p => p.id === f.defaultFulfillmentPolicyId);
          if (match) nameEl.textContent = match.name;
        }
      }
    } catch {}
  }

  // ── Fees & Costs (Settings) ────────────────────────────────────────────────
  // These eleven numbers are the difference between a net profit figure and a guess. They live on
  // the server in one FeeProfile, so saving them re-prices every screen at once — no re-scan, no
  // restart.
  const FEE_FIELDS = {
    'pg-fee-fvf':        'ebayFinalValueFeePercent',
    'pg-fee-fixed':      'ebayFinalValueFeeFixed',
    'pg-fee-promoted':   'promotedListingRatePercent',
    'pg-fee-payment':    'paymentProcessingPercent',
    'pg-fee-shipping':   'defaultShippingCost',
    'pg-fee-packaging':  'defaultPackagingCost',
    'pg-fee-labor':      'defaultLaborCost',
    'pg-fee-returns':    'returnReservePercent',
    'pg-fee-testing':    'testingReservePercent',
    'pg-fee-min-profit': 'minimumNetProfit',
    'pg-fee-min-margin': 'minimumMarginPercent',
  };

  async function loadFeeProfile() {
    if (!$('pg-fee-fvf')) return;
    try {
      const fees = await fetch('/api/fees/profile').then(r => r.json());
      Object.entries(FEE_FIELDS).forEach(([id, key]) => setVal(id, String(fees[key] ?? 0)));
      renderFeeSummary(fees);
    } catch { /* the form keeps its defaults, which are the server's defaults too */ }
  }

  async function saveFeeProfile() {
    const msg = $('pg-fees-msg');
    if (msg) { msg.textContent = 'Saving…'; msg.className = 'sd-test-msg'; }
    try {
      const body = {};
      Object.entries(FEE_FIELDS).forEach(([id, key]) => { body[key] = parseFloat($(id)?.value) || 0; });

      const { res, body: saved } = await safePost('/api/fees/profile', body);
      if (!res.ok) throw new Error(saved?.error || 'Save failed.');

      // Echo back what was stored, not what was typed — the server clamps rates the math can't
      // survive, and the form must show the number actually in force.
      Object.entries(FEE_FIELDS).forEach(([id, key]) => setVal(id, String(saved[key] ?? 0)));
      renderFeeSummary(saved);
      if (msg) { msg.textContent = 'Saved — every price in the app now uses these.'; msg.className = 'sd-test-msg ok'; }
      addActivity('Fees & costs saved', `${Number(saved.revenueFeePercent).toFixed(2)}% of each sale plus fixed costs`);

      // Anything currently open is now quoting stale numbers.
      ['nl', 'f'].forEach(p => { if ($(`${p}-th-panel`)) scheduleTakeHome(p); });
    } catch (err) {
      if (msg) { msg.textContent = 'Save failed: ' + err.message; msg.className = 'sd-test-msg error'; }
    }
  }

  function renderFeeSummary(fees) {
    const el = $('pg-fees-summary');
    if (!el) return;
    const fixed = (Number(fees.defaultShippingCost) || 0) + (Number(fees.defaultPackagingCost) || 0)
                + (Number(fees.defaultLaborCost) || 0) + (Number(fees.ebayFinalValueFeeFixed) || 0);
    const floor = Number(fees.minimumNetProfit) || 0;
    const margin = Number(fees.minimumMarginPercent) || 0;
    el.innerHTML =
      `<strong>${Number(fees.revenueFeePercent || 0).toFixed(2)}%</strong> of every sale, plus
       <strong>${moneyExact(fixed)}</strong> per order. On a ${moneyExact(100)} sale that is
       <strong>${moneyExact((Number(fees.revenueFeePercent) || 0) + fixed)}</strong> gone before
       you count what the item cost. ` +
      (floor > 0 || margin > 0
        ? `You won't be shown an offer worth less than ${floor > 0 ? moneyExact(floor) : ''}${
             floor > 0 && margin > 0 ? ' or ' : ''}${margin > 0 ? margin.toFixed(1) + '% margin' : ''}.`
        : `No profit floor set — the app will only stop you at break-even.`);
  }

  async function saveListingDefaults() {
    const msg = $('pg-defaults-msg');
    if (msg) { msg.textContent = 'Saving…'; msg.className = 'sd-test-msg'; }
    try {
      const body = {
        defaultPostalCode:         $('pg-default-zip')?.value.trim()             || '',
        defaultCountry:            $('pg-default-country')?.value                || 'US',
        defaultPackageType:        $('pg-default-package-type')?.value           || 'PACKAGE_THICK_ENVELOPE',
        defaultHandlingTimeDays:   parseInt($('pg-default-handling')?.value)     || 1,
        defaultWeightLbs:          parseFloat($('pg-default-weight-lbs')?.value) || 0,
        defaultWeightOz:           parseFloat($('pg-default-weight-oz')?.value)  || 0,
        defaultLengthIn:           parseFloat($('pg-default-length')?.value)     || 0,
        defaultWidthIn:            parseFloat($('pg-default-width')?.value)      || 0,
        defaultHeightIn:           parseFloat($('pg-default-height')?.value)     || 0,
        defaultFulfillmentPolicyId: $('pg-default-fulfillment')?.value.trim()   || '',
        defaultBestOffer:           !!$('pg-default-best-offer')?.checked,
      };
      const res = await fetch('/api/setup/save', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
      });
      if (!res.ok) throw new Error(await res.text());
      if (msg) { msg.textContent = 'Defaults saved.'; msg.className = 'sd-test-msg ok'; }
      addActivity('Listing defaults saved', `ZIP: ${body.defaultPostalCode || '(none)'}`);
    } catch (err) {
      if (msg) { msg.textContent = 'Save failed: ' + err.message; msg.className = 'sd-test-msg error'; }
    }
  }

  function bindSetup() {
    // Token paste modal
    on('btn-paste-token', 'click', () => $('token-overlay')?.classList.remove('hidden'));
    on('btn-close-token',  'click', () => $('token-overlay')?.classList.add('hidden'));
    on('btn-save-token', 'click', async () => {
      const token = $('token-input')?.value.trim();
      const msg   = $('token-msg');
      if (!token) { if (msg) { msg.textContent = 'Paste a token first.'; msg.style.color = 'var(--danger)'; } return; }

      // Detect OAuth redirect URL (contains a code= parameter from inglisting.com)
      // and route to the exchange endpoint instead of saving the raw URL as a token
      const isOAuthRedirect = token.startsWith('https://') && token.includes('code=');
      if (isOAuthRedirect) {
        if (msg) { msg.textContent = 'OAuth redirect URL detected — exchanging for access token...'; msg.style.color = ''; }
        try {
          const res = await fetch('/api/ebay/exchange-redirect-url', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ redirectUrl: token })
          });
          if (!res.ok) {
            let errorText;
            try { const b = await res.json(); errorText = b.error || JSON.stringify(b); } catch { errorText = await res.text(); }
            throw new Error(errorText);
          }
          const result = await res.json();
          updateAuthUI(true);
          $('token-overlay')?.classList.add('hidden');
          $('token-input').value = '';
          addActivity('eBay OAuth connected', result.hasRefreshToken
            ? 'Access token and refresh token saved.'
            : (result.message || 'Access token saved.'));
          loadPolicies(false);
          await loadListings('eBay OAuth connected');
        } catch (err) {
          if (msg) { msg.textContent = `OAuth exchange failed: ${err.message}`; msg.style.color = 'var(--danger)'; }
          addActivity('OAuth exchange failed', err.message);
        }
        return;
      }

      // Raw bearer token (e.g. from eBay developer portal)
      if (msg) { msg.textContent = 'Saving…'; msg.style.color = ''; }
      try {
        await fetch('/api/setup/save', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ebayUserToken: token })
        });
        updateAuthUI(true);
        $('token-overlay')?.classList.add('hidden');
        $('token-input').value = '';
        addActivity('eBay connected', 'Bearer token saved successfully.');
        await loadListings('eBay token saved');
      } catch (err) {
        if (msg) { msg.textContent = `Error: ${err.message}`; msg.style.color = 'var(--danger)'; }
      }
    });

    on('s-oauth-redirect-url', 'input', previewOAuthRedirectUrl);
    on('btn-exchange-oauth-redirect', 'click', exchangeOAuthRedirectUrl);
    on('btn-paste-and-connect', 'click', pasteAndConnect);
    on('btn-settings', 'click', () => openSetupWithPolicies(null));
    on('btn-open-credentials', 'click', () => openSetupWithPolicies(null));
    on('btn-activate-license', 'click', activateLicense);
    on('lp-activate-btn',     'click', activateLicensePage);
    on('lp-buy-pro-btn',      'click', () => buyProLicense(false));
    on('lp-buy-annual-btn',   'click', () => buyProLicense(true));
    on('btn-close-setup', 'click', () => $('setup-overlay')?.classList.add('hidden'));
    on('setup-overlay', 'click', e => {
      if (e.target === $('setup-overlay')) $('setup-overlay')?.classList.add('hidden');
    });

    on('btn-connect', 'click', async () => {
      try {
        const status = await fetch('/api/setup/status').then(r => r.json());
        const hasEbayCreds = status.hasEbayClientId && status.hasEbayClientSecret;
        if (!hasEbayCreds) {
          openSetup(status);
          showResult('error', 'Add your eBay Client ID and Client Secret in Settings first.');
          return;
        }
        const res = await fetch('/api/ebay/auth-url');
        if (!res.ok) throw new Error(await res.text());
        const { url } = await res.json();
        window.location.href = url;
      } catch (err) {
        openSetup(null);
        showResult('error', `eBay login failed: ${esc(err.message)}`);
      }
    });

    on('btn-disconnect', 'click', async () => {
      ebayToken = '';
      await fetch('/api/ebay/disconnect', { method: 'POST' });
      cachedListings = [];
      updateAuthUI(false);
      renderListings();
      updateStats();
      addActivity('eBay disconnected', 'Local user token was cleared.');
    });

    on('btn-save-setup', 'click', saveSetup);
    on('btn-load-policies', 'click', () => loadPolicies(true));

    // Sync policy selects → text inputs
    on('s-fulfillment-sel', 'change', () => { const v = $('s-fulfillment-sel')?.value; if (v) set('s-fulfillment', v); });
    on('s-payment-sel',     'change', () => { const v = $('s-payment-sel')?.value;     if (v) set('s-payment',     v); });
    on('s-return-sel',      'change', () => { const v = $('s-return-sel')?.value;      if (v) set('s-return',      v); });

    on('s-image-gen-mode', 'change', e => applyImageGenModeVisibility(e.target.value));
    on('s-local-sd-backend', 'change', e => applyComfyUiModelVisibility(e.target.value));
    on('btn-test-sd', 'click', async () => {
      const msg = $('sd-test-msg');
      if (msg) { msg.textContent = 'Testing...'; msg.className = 'sd-test-msg'; }
      try {
        const res = await fetch('/api/image-gen/test').then(r => r.json());
        if (msg) { msg.textContent = res.message; msg.className = 'sd-test-msg ' + (res.online ? 'ok' : 'error'); }
      } catch (err) {
        if (msg) { msg.textContent = `Error: ${err.message}`; msg.className = 'sd-test-msg error'; }
      }
    });
  }

  function openSetup(status) {
    $('setup-overlay')?.classList.remove('hidden');
    if (status) populateSetupFields(status);
  }

  async function saveSetup() {
    const msg = $('setup-status-msg');
    msg.textContent = 'Saving...';
    msg.className = '';

    const body = {
      licenseKey: $('s-license-key')?.value.trim() || '',
      anthropicApiKey: $('s-anthropic-key').value.trim(),
      openAiApiKey: $('s-openai-key')?.value.trim() || '',
      ebayClientId: $('s-client-id').value.trim(),
      ebayDevId:    $('s-dev-id').value.trim(),
      ebayClientSecret: $('s-client-secret').value.trim(),
      ebayRuName: $('s-runame').value.trim(),
      ebaySandbox: $('s-sandbox').checked,
      ebayFulfillmentPolicyId: $('s-fulfillment').value.trim(),
      ebayPaymentPolicyId: $('s-payment').value.trim(),
      ebayReturnPolicyId: $('s-return').value.trim(),
      ebayUserToken: $('s-user-token').value.trim(),
      imageGenMode: $('s-image-gen-mode')?.value || 'disabled',
      localSdEndpoint: $('s-local-sd-endpoint')?.value.trim() || '',
      localSdBackend: $('s-local-sd-backend')?.value || 'automatic1111',
      localSdModelName: $('s-local-sd-model')?.value.trim() || '',
      imagePromptTemplate: $('s-image-prompt')?.value.trim() || '',
    };

    // If the user pasted an OAuth redirect URL into the token field, exchange it first
    const rawToken = body.ebayUserToken || '';
    if (rawToken.startsWith('https://') && rawToken.includes('code=')) {
      msg.textContent = 'OAuth redirect URL detected in token field — exchanging...';
      msg.className = '';
      try {
        const res = await fetch('/api/ebay/exchange-redirect-url', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ redirectUrl: rawToken })
        });
        if (!res.ok) {
          let errText;
          try { const b = await res.json(); errText = b.error || JSON.stringify(b); } catch { errText = await res.text(); }
          throw new Error(errText);
        }
        const result = await res.json();
        msg.textContent = result.hasRefreshToken ? 'Access and refresh tokens saved.' : (result.message || 'Access token saved.');
        msg.className = 'ok';
        updateAuthUI(true);
        addActivity('eBay OAuth connected', msg.textContent);
        setTimeout(() => $('setup-overlay')?.classList.add('hidden'), 700);
        await loadListings('eBay OAuth connected');
      } catch (err) {
        msg.textContent = `OAuth exchange failed: ${err.message}`;
        msg.className = 'error';
        addActivity('OAuth exchange failed', err.message);
      }
      return;
    }

    try {
      const res = await fetch('/api/setup/save', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
      if (!res.ok) throw new Error(await res.text());
      const status = await res.json();

      const hasEbayCreds = status.hasEbayClientId && status.hasEbayClientSecret;
      if (!hasEbayCreds) {
        const missing = [];
        if (!status.hasEbayClientId) missing.push('eBay Client ID');
        if (!status.hasEbayClientSecret) missing.push('eBay Client Secret');
        if (status.ebaySandbox && !status.hasEbayRuName) missing.push('eBay RuName');
        msg.textContent = `Still missing: ${missing.join(', ')}`;
        msg.className = 'error';
        addActivity('Settings incomplete', msg.textContent);
        return;
      }

      const tokenStatus = await fetch('/api/ebay/token-status').then(r => r.json());
      if (tokenStatus.hasToken) {
        msg.textContent = tokenStatus.hasRefreshToken
          ? 'Saved. eBay access and refresh tokens are active.'
          : 'Saved. eBay access token is active.';
        msg.className = 'ok';
        updateAuthUI(true);
        addActivity('Settings saved', 'eBay token is active.');
        setTimeout(() => $('setup-overlay')?.classList.add('hidden'), 700);
      } else {
        msg.textContent = 'Saved. Redirecting to eBay login...';
        msg.className = 'ok';
        addActivity('Settings saved', 'Starting eBay OAuth login.');
        setTimeout(async () => {
          $('setup-overlay')?.classList.add('hidden');
          const { url } = await fetch('/api/ebay/auth-url').then(r => r.json());
          window.location.href = url;
        }, 700);
      }
    } catch (err) {
      msg.textContent = `Error: ${err.message}`;
      msg.className = 'error';
      addActivity('Settings error', err.message);
    }
  }

  async function loadPolicies(userTriggered = false) {
    const msg = $('policy-load-msg');
    const btn = $('btn-load-policies');
    if (msg) { msg.textContent = 'Loading…'; msg.className = 'sd-test-msg'; }
    if (btn) btn.disabled = true;

    try {
      const res  = await fetch('/api/ebay/policies');
      const data = await res.json();

      if (!res.ok) {
        const errText = data.error || 'Failed to load policies';
        if (msg) { msg.textContent = errText; msg.className = 'sd-test-msg error'; }
        if (userTriggered) addActivity('Policy load failed', errText);
        return;
      }

      const { fulfillmentPolicies = [], paymentPolicies = [], returnPolicies = [], error } = data;
      const total = fulfillmentPolicies.length + paymentPolicies.length + returnPolicies.length;

      if (total === 0) {
        const noPolMsg = 'No eBay business policies found. Create them in Seller Hub first.';
        if (msg) { msg.textContent = noPolMsg; msg.className = 'sd-test-msg error'; }
        return;
      }

      cachedPolicies = { fulfillmentPolicies, paymentPolicies, returnPolicies };

      populatePolicySelect('s-fulfillment-sel', fulfillmentPolicies, $('s-fulfillment')?.value);
      populatePolicySelect('s-payment-sel',     paymentPolicies,     $('s-payment')?.value);
      populatePolicySelect('s-return-sel',      returnPolicies,      $('s-return')?.value);

      $('policy-selects')?.classList.remove('hidden');
      fillNlPolicySelects();

      const warnText = error ? ' (some policies may be missing: ' + error + ')' : '';
      if (msg) {
        msg.textContent = fulfillmentPolicies.length + ' fulfillment, ' + paymentPolicies.length + ' payment, ' + returnPolicies.length + ' return policies loaded' + warnText;
        msg.className = 'sd-test-msg ok';
      }
      if (userTriggered) addActivity('eBay policies loaded', total + ' policies found');
      const nlMsg = $('nl-policy-msg');
      if (nlMsg) { nlMsg.textContent = ''; nlMsg.className = 'sd-test-msg'; }
    } catch (err) {
      if (msg) { msg.textContent = 'Error: ' + err.message; msg.className = 'sd-test-msg error'; }
      const nlMsg = $('nl-policy-msg');
      if (nlMsg) { nlMsg.textContent = 'Error: ' + err.message; nlMsg.className = 'sd-test-msg error'; }
      if (userTriggered) addActivity('Policy load error', err.message);
    } finally {
      if (btn) btn.disabled = false;
    }
  }

  function populatePolicySelect(selectId, policies, currentId) {
    const sel = $(selectId);
    if (!sel) return;

    sel.innerHTML = '<option value="">— Select policy —</option>';
    let matched = false;
    policies.forEach(p => {
      const opt = document.createElement('option');
      opt.value = p.id;
      opt.textContent = p.name + ' (' + p.id + ')';
      if (p.id === currentId) { opt.selected = true; matched = true; }
      sel.appendChild(opt);
    });

    // If current saved ID isn't in the list, add it as a placeholder so it's not lost
    if (currentId && !matched) {
      const opt = document.createElement('option');
      opt.value = currentId;
      opt.textContent = '(saved: ' + currentId + ')';
      opt.selected = true;
      sel.insertBefore(opt, sel.children[1]);
    }

    // Sync the text input to match whatever is selected
    const selVal = sel.value;
    const inputMap = { 's-fulfillment-sel': 's-fulfillment', 's-payment-sel': 's-payment', 's-return-sel': 's-return' };
    if (selVal && inputMap[selectId]) set(inputMap[selectId], selVal);
  }

  function fillNlPolicySelects() {
    if (!cachedPolicies) return;
    fillNlPolicySelect('nl-fulfillment-sel', cachedPolicies.fulfillmentPolicies, $('s-fulfillment')?.value || '');
    fillNlPolicySelect('nl-payment-sel',     cachedPolicies.paymentPolicies,     $('s-payment')?.value     || '');
    fillNlPolicySelect('nl-return-sel',      cachedPolicies.returnPolicies,      $('s-return')?.value      || '');
    const msg = $('nl-policy-msg');
    if (msg) { msg.textContent = ''; msg.className = 'sd-test-msg'; }
  }

  function fillNlPolicySelect(selectId, policies, savedId) {
    const sel = $(selectId);
    if (!sel) return;
    const current = sel.value; // preserve user's current pick across refreshes
    const activeId = current || savedId;
    sel.innerHTML = '<option value="">— Select policy —</option>';
    let matched = false;
    policies.forEach(p => {
      const opt = document.createElement('option');
      opt.value = p.id;
      opt.textContent = p.name + ' (' + p.id + ')';
      if (p.id === activeId) { opt.selected = true; matched = true; }
      sel.appendChild(opt);
    });
    if (activeId && !matched) {
      const opt = document.createElement('option');
      opt.value = activeId;
      opt.textContent = '(saved: ' + activeId + ')';
      opt.selected = true;
      sel.insertBefore(opt, sel.children[1]);
    }
  }

  function previewOAuthRedirectUrl() {
    const msg = $('oauth-redirect-msg');
    if (!msg) return;

    const raw = $('s-oauth-redirect-url')?.value.trim();
    if (!raw) {
      msg.textContent = '';
      msg.className = '';
      return;
    }

    try {
      const details = parseOAuthRedirectUrl(raw);
      msg.textContent = `Code detected${details.state ? `; state: ${details.state}` : ''}.`;
      msg.className = 'ok';
    } catch (err) {
      msg.textContent = err.message;
      msg.className = 'error';
    }
  }

  async function exchangeOAuthRedirectUrl() {
    const msg = $('oauth-redirect-msg');
    const raw = $('s-oauth-redirect-url')?.value.trim();
    if (!raw) {
      if (msg) {
        msg.textContent = 'Paste the full eBay OAuth redirect URL first.';
        msg.className = 'error';
      }
      return;
    }

    let details;
    try {
      details = parseOAuthRedirectUrl(raw);
    } catch (err) {
      if (msg) {
        msg.textContent = err.message;
        msg.className = 'error';
      }
      return;
    }

    const btn = $('btn-exchange-oauth-redirect');
    if (btn) {
      btn.disabled = true;
      btn.textContent = 'Exchanging...';
    }
    if (msg) {
      msg.textContent = `Exchanging production OAuth code${details.state ? ` with state ${details.state}` : ''}...`;
      msg.className = '';
    }

    try {
      const res = await fetch('/api/ebay/exchange-redirect-url', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ redirectUrl: raw })
      });
      if (!res.ok) throw new Error(await res.text());
      const result = await res.json();
      if (msg) {
        msg.textContent = result.hasRefreshToken
          ? 'Production eBay OAuth access and refresh tokens saved locally.'
          : (result.message || 'Production eBay OAuth access token saved locally.');
        msg.className = 'ok';
      }
      $('s-oauth-redirect-url').value = '';
      updateAuthUI(true);
      addActivity('Production eBay connected', `OAuth redirect exchanged${result.state ? `; state ${result.state}` : ''}.`);
      loadPolicies(false);
      await loadListings('Production eBay connected');
    } catch (err) {
      if (msg) {
        msg.textContent = `OAuth exchange failed: ${err.message}`;
        msg.className = 'error';
      }
      addActivity('Production OAuth failed', err.message);
    } finally {
      if (btn) {
        btn.disabled = false;
        btn.textContent = 'Exchange Production OAuth URL';
      }
    }
  }

  let ebayCallbackWatcherActive = false;

  async function pasteAndConnect() {
    const msg = $('oauth-redirect-msg');
    const btn = $('btn-paste-and-connect');
    if (btn) { btn.disabled = true; btn.textContent = 'Connecting…'; }
    try {
      let text = '';
      try { text = await navigator.clipboard.readText(); } catch { /* permission denied */ }
      if (!text || !text.includes('code=')) {
        text = $('s-oauth-redirect-url')?.value.trim() || '';
      }
      if (!text || !text.includes('code=')) {
        if (msg) { msg.textContent = 'No eBay code found. Make sure you copied the URL from your browser after logging in.'; msg.className = 'error'; }
        return;
      }
      const ta = $('s-oauth-redirect-url');
      if (ta) ta.value = text;
      await exchangeOAuthRedirectUrl();
      ebayCallbackWatcherActive = false;
      $('ebay-oauth-step')?.classList.add('hidden');
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = '📋 I\'ve Logged In — Paste & Connect'; }
    }
  }

  function startEbayCallbackWatcher() {
    if (ebayCallbackWatcherActive) return;
    ebayCallbackWatcherActive = true;

    async function tryAutoConnect() {
      if (!ebayCallbackWatcherActive) return;
      try {
        const text = await navigator.clipboard.readText();
        if (text && text.includes('code=') && text.includes('state=')) {
          ebayCallbackWatcherActive = false;
          document.removeEventListener('visibilitychange', onVisible);
          const ta = $('s-oauth-redirect-url');
          if (ta) ta.value = text;
          await exchangeOAuthRedirectUrl();
          $('ebay-oauth-step')?.classList.add('hidden');
        }
      } catch { /* clipboard permission denied — user clicks the button manually */ }
    }

    function onVisible() {
      if (document.visibilityState === 'visible') tryAutoConnect();
    }

    document.addEventListener('visibilitychange', onVisible);
  }

  function parseOAuthRedirectUrl(raw) {
    const url = new URL(raw);
    if (url.protocol !== 'https:') throw new Error('Redirect URL must be an https:// URL.');
    const code = url.searchParams.get('code') || '';
    const state = url.searchParams.get('state') || '';
    if (!code) throw new Error('Redirect URL is missing the code= parameter. Complete the eBay login first, then paste the full URL you were redirected to.');
    return { code, state };
  }

  async function loadSettingsStatus() {
    const el = $('settings-status');
    if (!el) return;
    el.innerHTML = settingRow('Status', 'Loading local settings...');

    try {
      const [db, folders] = await Promise.all([
        fetch('/api/local-db/status').then(r => r.json()),
        fetch('/api/photos/default-folders').then(r => r.json())
      ]);
      const folderSummary = (Array.isArray(folders) ? folders : [])
        .map(f => `${f.modelKey}: ${f.imageCount} image${f.imageCount === 1 ? '' : 's'}`)
        .join(' | ');

      el.innerHTML = [
        settingRow('Local Database', db.databasePath || '-'),
        settingRow('Saved Local Edits', db.listingCount ?? 0),
        settingRow('Photo Folders', folderSummary || 'No folders found'),
        settingRow('Safety Mode', 'Drafts and revisions require manual action')
      ].join('');
    } catch (err) {
      el.innerHTML = settingRow('Status', `Unable to load settings: ${err.message}`);
    }
  }

  function settingRow(label, value) {
    return `<div class="settings-row"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;
  }

  async function loadLogs() {
    const el = $('logs-list');
    if (!el) return;
    // A log row saying "Loading" is indistinguishable from a log entry that
    // says Loading. Skeletons can't be mistaken for content.
    el.innerHTML = skeletonRowsHtml(5);

    try {
      const entries = await fetch('/api/logs/recent').then(r => r.json());
      if (!Array.isArray(entries) || entries.length === 0) {
        renderState(el, {
          compact: true,
          icon: 'i-logs',
          title: 'No activity logged yet',
          body: 'Every import, AI analysis, price change and publish is recorded here with its full request detail — useful when something goes wrong.'
        });
        return;
      }

      el.innerHTML = entries.map(logRow).join('');
    } catch (err) {
      renderState(el, {
        variant: 'error',
        compact: true,
        title: "Couldn't read the log",
        body: 'The app is running but the log endpoint did not answer.',
        detail: err.message,
        actions: [{ label: 'Try again', id: 'retry', kind: 'btn-primary' }]
      }, { retry: () => loadLogs() });
    }
  }

  function logRow(entry) {
    const level = entry.level || 'Info';
    const time = entry.timestamp ? new Date(entry.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';
    return `
      <div class="log-row">
        <span class="log-level ${esc(level.toLowerCase())}">${esc(level)}</span>
        <strong class="log-title">${esc(entry.title || 'Action')}<br><small>${esc(time)}</small></strong>
        <span class="log-detail">${esc(entry.detail || '')}</span>
      </div>`;
  }

  async function loadListings(activityTitle = 'Listings imported') {
    setListingsFeedback('Loading active eBay listings…', 'busy');
    showListingsSkeleton();

    try {
      const res = await fetch('/api/ebay/listings');
      if (!res.ok) {
        let errorText;
        try { const body = await res.json(); errorText = body.error || JSON.stringify(body); }
        catch { errorText = await res.text(); }
        throw new Error(`HTTP ${res.status}: ${errorText}`);
      }
      const listings = await res.json();
      cachedListings = Array.isArray(listings) ? listings : [];
      renderListings();
      updateStats();
      addActivity(activityTitle, `${cachedListings.length} listing${cachedListings.length === 1 ? '' : 's'} loaded from eBay.`);
    } catch (err) {
      const errorDetail = err.message || 'Unknown eBay API error.';
      if (isConnected) {
        // Connected but import failed — show the real error, never fall back to samples
        cachedListings = [];
        updateStats();
        const grid = $('listings-grid');
        const tbody = $('listings-table-body');
        if (grid) grid.innerHTML = '';
        if (tbody) tbody.innerHTML = '';
        setListingsFeedback('Import failed.', 'error');
        // The failure gets the whole panel rather than one line of red text:
        // it is the only thing on this screen the seller can act on, and the
        // two things worth trying are right there.
        renderState('listings-state', {
          variant: 'error',
          title: "Couldn't import your eBay listings",
          body: 'eBay accepted the connection but refused the request. This is usually an expired token — reconnecting fixes it.',
          detail: errorDetail,
          actions: [
            { label: 'Try again', id: 'retry', kind: 'btn-primary' },
            { label: 'Open logs', id: 'logs' }
          ]
        }, {
          retry: () => loadListings(),
          logs: () => showLogsSection()
        });
        addActivity('eBay import failed', errorDetail);
        showResult('error', `eBay import failed: ${esc(errorDetail)} — Check the Logs section for details.`);
        return;
      }
      // Not yet connected — fall back to sample listings
      if (await loadPlaceholderListings('Sample listings loaded')) {
        addActivity('eBay not connected', 'Showing sample listings. Connect eBay to import real listings.');
        return;
      }
      setListingsFeedback(errorDetail, 'error');
      addActivity('Import failed', errorDetail);
    }
  }

  function showListingsSkeleton() {
    const grid = $('listings-grid');
    const tbody = $('listings-table-body');
    clearListingsState();
    if (grid) grid.innerHTML = skeletonCardsHtml(8);
    if (tbody) tbody.innerHTML = `<tr><td colspan="12" class="skeleton-cell">${skeletonRowsHtml(6)}</td></tr>`;
  }

  function clearListingsState() {
    const el = $('listings-state');
    if (el) el.innerHTML = '';
  }

  async function loadPlaceholderListings(activityTitle = 'Sample listings loaded') {
    setListingsFeedback('Loading sample listings…', 'busy');

    try {
      const listings = await fetch('/api/local-listings/placeholder').then(r => {
        if (!r.ok) return r.text().then(t => { throw new Error(t); });
        return r.json();
      });
      cachedListings = Array.isArray(listings) ? listings : [];
      renderListings();
      updateStats();
      setListingsFeedback(`${cachedListings.length} sample listing${cachedListings.length === 1 ? '' : 's'} shown until eBay is connected.`);
      addActivity(activityTitle, `${cachedListings.length} local sample listing${cachedListings.length === 1 ? '' : 's'} loaded.`);
      return true;
    } catch (err) {
      setListingsFeedback('Connect eBay, then import listings.');
      addActivity('Sample listings unavailable', err.message || 'Unable to load local sample listings.');
      return false;
    }
  }

  function renderListings() {
    const grid = $('listings-grid');
    const tbody = $('listings-table-body');
    if (!grid || !tbody) return;

    const search = ($('global-search')?.value || '').trim().toLowerCase();
    const listings = cachedListings.filter(l => listingSearchText(l).includes(search));

    grid.innerHTML = '';
    tbody.innerHTML = '';
    clearListingsState();

    // Nothing loaded at all. Whether that is a first run or a genuinely empty
    // store changes what the seller should do next, so the two say so
    // differently and each offers the step that moves them forward.
    if (!cachedListings.length) {
      setListingsFeedback(isConnected
        ? 'No active listings found on this account.'
        : 'Not connected to eBay yet.');
      renderState('listings-state', isConnected ? {
        icon: 'i-inbox',
        title: 'No active listings on this account',
        body: 'Nothing is live on eBay right now. Create one with AI from a photo or a product name — the app writes the title, description, item specifics and price from real sold comps.',
        actions: [
          { label: 'Create an AI listing', id: 'ai', kind: 'btn-primary' },
          { label: 'Import again', id: 'import' }
        ],
        hint: 'Listings you create elsewhere show up here after an import.'
      } : {
        icon: 'i-plug',
        title: 'Connect eBay to see your listings',
        body: 'Log in once and the app imports your live listings, then keeps prices, quantities and photos in sync from here.',
        actions: [
          { label: 'Log into eBay', id: 'connect', kind: 'btn-primary' },
          { label: 'Create an AI listing', id: 'ai' }
        ],
        hint: 'Nothing is published to eBay without your confirmation.'
      }, {
        connect: () => $('btn-connect')?.click(),
        ai: () => $('btn-new-ai-listing')?.click(),
        import: () => loadListings()
      });
      return;
    }

    // Loaded fine, filtered to nothing — a search result, not a problem.
    if (!listings.length) {
      setListingsFeedback(`No listings match “${search}”.`);
      renderState('listings-state', {
        compact: true,
        icon: 'i-search',
        title: `No listings match “${search}”`,
        body: `Searched title, SKU, listing ID and category across ${cachedListings.length} listing${cachedListings.length === 1 ? '' : 's'}.`,
        actions: [{ label: 'Clear search', id: 'clear', kind: 'btn-secondary' }]
      }, {
        clear: () => {
          const box = $('global-search');
          if (box) { box.value = ''; box.focus(); }
          renderListings();
        }
      });
      return;
    }

    const sampleOnly = listings.every(l => (l.status || '').toUpperCase() === 'SAMPLE');
    setListingsFeedback(sampleOnly
      ? `${listings.length} sample listing${listings.length === 1 ? '' : 's'} shown until eBay is connected.`
      : `${listings.length} listing${listings.length === 1 ? '' : 's'} shown.`);

    listings.forEach(listing => {
      grid.appendChild(renderListingCard(listing));
      tbody.appendChild(renderListingRow(listing));
    });
  }

  function renderListingCard(listing) {
    const card = document.createElement('article');
    card.className = 'listing-card';
    card.dataset.offerId = listing.offerId || '';

    const img = listing.thumbnailUrl
      ? `<img src="${esc(listing.thumbnailUrl)}" alt="" loading="lazy" />`
      : '<div class="listing-media"><strong>ING Mining</strong><span>No photo</span></div>';

    // Watchers ride on the photo rather than in the footer. Four items on one
    // footer line wrapped at card width, and a watch count is a property of
    // the listing, not an action — it belongs with the picture.
    const watchBadge = listing.watchCount > 0
      ? `<span class="watch-badge" title="${listing.watchCount} watcher${listing.watchCount === 1 ? '' : 's'}">👁 ${listing.watchCount}</span>`
      : '';
    const viewLink = listing.listingUrl
      ? `<a class="view-ebay-link" href="${esc(listing.listingUrl)}" target="_blank" rel="noopener noreferrer" title="Open this listing on eBay">eBay ↗</a>`
      : '';

    card.innerHTML = `
      <div class="listing-thumb">${img}${watchBadge}</div>
      <div class="listing-title">${esc(listing.title || 'Untitled listing')}</div>
      <div class="listing-meta">
        <span>Price<strong>${money(listing.price)}</strong></span>
        <span>Quantity<strong>${listing.quantity ?? 0}</strong></span>
        <span>SKU<strong>${esc(listing.sku || '-')}</strong></span>
        <span>Listing ID<strong>${esc(listing.listingId || '-')}</strong></span>
        <span>Category<strong>${esc(listingCategory(listing))}</strong></span>
        <span>Updated<strong>${esc(listingUpdated(listing))}</strong></span>
      </div>
      <div class="listing-footer">
        <span class="${statusClass(listing.status)}">${esc(displayStatus(listing.status))}</span>
        <span class="listing-footer-actions">
          ${viewLink}
          <button class="btn btn-secondary small" type="button">Edit</button>
        </span>
      </div>`;

    card.querySelector('button')?.addEventListener('click', event => {
      event.stopPropagation();
      loadListingIntoForm(listing, card);
    });
    card.addEventListener('dblclick', () => loadListingIntoForm(listing, card));
    return card;
  }

  function renderListingRow(listing) {
    const row = document.createElement('tr');
    const img = listing.thumbnailUrl
      ? `<img class="table-img" src="${esc(listing.thumbnailUrl)}" alt="" loading="lazy" />`
      : '<div class="table-img"></div>';

    const rowViewLink = listing.listingUrl
      ? `<a href="${esc(listing.listingUrl)}" target="_blank" rel="noopener noreferrer">View</a>`
      : '-';

    row.innerHTML = `
      <td>${img}</td>
      <td><strong>${esc(listing.title || 'Untitled listing')}</strong></td>
      <td>${money(listing.price)}</td>
      <td>${listing.quantity ?? 0}</td>
      <td>${esc(listing.sku || '-')}</td>
      <td>${esc(listing.listingId || '-')}</td>
      <td><span class="${statusClass(listing.status)}">${esc(displayStatus(listing.status))}</span></td>
      <td>${esc(listingCategory(listing))}</td>
      <td>${esc(listingUpdated(listing))}</td>
      <td>${listing.watchCount > 0 ? listing.watchCount : '-'}</td>
      <td>${rowViewLink}</td>
      <td><button class="btn btn-secondary small" type="button">Edit</button></td>`;

    row.querySelector('button')?.addEventListener('click', () => loadListingIntoForm(listing, row));
    row.addEventListener('dblclick', () => loadListingIntoForm(listing, row));
    return row;
  }

  // A placeholder/SAMPLE listing (see PlaceholderListings.cs) has no real offerId and a
  // fabricated listingId — it was never actually published to eBay, so it must never be
  // eligible for a live revision call (EbayService.UpdateListingAsync would otherwise send
  // ReviseInventoryStatus for a listingId that doesn't exist on eBay).
  function canReviseOnEbay(listing) {
    if ((listing.status || '').toUpperCase() === 'SAMPLE') return false;
    return !!(listing.offerId || (listing.listingId && listing.sku));
  }

  // ── Take-home: all-in net, break-even and the offer floor ──────────────────
  // The listing editor used to show a price and nothing else, so a seller could type $120 and bank
  // $91 without ever seeing the $29. This panel puts the real number next to the price field in
  // both editors, and it is not a second opinion: every figure comes from /api/pricing/net-quote,
  // which runs the same NetProceedsCalculator that costs local arbitrage, lot analysis, inventory
  // health and watcher offers. One calculation, one answer, every screen.

  const TAKE_HOME_DEBOUNCE_MS = 350;
  const takeHomeTimers = {};     // prefix -> debounce handle
  const takeHomeState  = {};     // prefix -> last response, so the floor buttons have something to apply
  let   costBasisCache = null;   // /api/inventory/cost-basis, fetched once per drawer session

  const thNum = id => { const v = parseFloat($(id)?.value); return Number.isFinite(v) && v >= 0 ? v : 0; };

  function bindTakeHome(prefix) {
    if (!$(`${prefix}-th-panel`)) return;

    // Anything that moves the money re-costs the sale. Quantity is included because the panel
    // reports the total across the run as well as the per-unit profit.
    ['price', 'quantity', 'unit-cost', 'ship-cost', 'buyer-shipping']
      .forEach(field => on(`${prefix}-${field}`, 'input', () => scheduleTakeHome(prefix)));

    // A cost typed against a live listing is the same cost Inventory Health and Watcher Offers
    // need, so it is written through to the shared store. On blur rather than per keystroke.
    if (prefix === 'f') on('f-unit-cost', 'change', () => saveCostBasisFromDrawer(drawerListing));

    $(`${prefix}-th-panel`)?.addEventListener('click', e => {
      if (e.target.closest('[data-th-fees]')) { handleNav('settings'); $('fees-costs')?.scrollIntoView({ behavior: 'smooth' }); return; }

      // Turning the floor into eBay's own auto-decline rule is the point of computing it: below
      // this price the seller never has to see the offer, let alone be tempted by it.
      const declineBtn = e.target.closest('[data-th-decline]');
      if (declineBtn) {
        const floor = Number(declineBtn.dataset.floor);
        if (!Number.isFinite(floor) || floor <= 0) return;
        if ($(`${prefix}-best-offer`) && !$(`${prefix}-best-offer`).checked) {
          $(`${prefix}-best-offer`).checked = true;
          $(`${prefix}-best-offer`).dispatchEvent(new Event('change', { bubbles: true }));
        }
        set(`${prefix}-auto-decline`, floor.toFixed(2));
        $(`${prefix}-auto-decline`)?.dispatchEvent(new Event('input', { bubbles: true }));
        addActivity('Auto-decline set to your floor', moneyExact(floor));
      }
    });

    renderTakeHome(prefix, null);
    refreshAdRateStrip(prefix, 0, 0);
  }

  function scheduleTakeHome(prefix) {
    clearTimeout(takeHomeTimers[prefix]);
    takeHomeTimers[prefix] = setTimeout(() => refreshTakeHome(prefix), TAKE_HOME_DEBOUNCE_MS);
  }

  async function refreshTakeHome(prefix) {
    const panel = $(`${prefix}-th-panel`);
    if (!panel) return;

    const price = thNum(`${prefix}-price`);
    const cost  = thNum(`${prefix}-unit-cost`);
    const ship  = $(`${prefix}-ship-cost`)?.value.trim();

    try {
      const { res, body } = await safePost('/api/pricing/net-quote', {
        prices: [price],
        unitCost: cost > 0 ? cost : null,
        buyerPaidShipping: thNum(`${prefix}-buyer-shipping`),
        quantity: Math.max(1, parseInt($(`${prefix}-quantity`)?.value, 10) || 1),
        shippingCost: ship ? (parseFloat(ship) || 0) : null,
      });
      if (!res.ok) throw new Error(body?.error || 'Could not price this sale.');
      takeHomeState[prefix] = body;
      renderTakeHome(prefix, body);
      refreshAdRateStrip(prefix, price, cost);
    } catch (err) {
      // A pricing panel that silently shows nothing is worse than one that says it is stale:
      // the seller would read the blank space as "no fees on this sale".
      $(`${prefix}-th-body`).innerHTML =
        `<div class="th-verdict th-error"><strong class="th-headline">Take-home unavailable</strong>
         <span class="th-note">${esc(err?.message || 'The fee calculation could not be reached.')}
         Your price is unchanged — but it has not been checked against your costs.</span></div>`;
    }
  }

  function renderTakeHome(prefix, data) {
    const body = $(`${prefix}-th-body`);
    if (!body) return;

    const q = data?.quotes?.[0];
    if (!q) {
      body.innerHTML = `<div class="th-verdict th-idle"><strong class="th-headline">Enter a price</strong>
        <span class="th-note">Type an asking price and what you paid, and this shows exactly what
        reaches your bank after eBay's cut, shipping and every other fee.</span></div>`;
      return;
    }

    const qty = Number(q.quantity) || 1;
    const floors = q.hasCostBasis ? `
      <div class="th-floors">
        <div class="th-floor">
          <span>Break even at</span><strong>${moneyExact(q.breakEvenPrice)}</strong>
          <em>Sell for less and this listing costs you money.</em>
        </div>
        <div class="th-floor th-floor-min">
          <span>Never accept less than</span><strong>${moneyExact(q.minimumOfferPrice)}</strong>
          <em>${esc(floorBasisText(q))}</em>
          <button type="button" class="th-floor-btn" data-th-decline data-floor="${q.minimumOfferPrice}">
            Use as auto-decline
          </button>
        </div>
      </div>` : '';

    const lines = (q.lines || []).map(l => `
      <div class="th-line${Number(l.amount) > 0 ? '' : ' th-line-zero'}" title="${esc(l.detail)}">
        <span>${esc(l.label)}</span><span>−${moneyExact(l.amount)}</span>
      </div>`).join('');

    body.innerHTML = `
      <div class="th-verdict th-${esc(q.verdict)}">
        <strong class="th-headline">${esc(q.headline)}</strong>
        <span class="th-note">${esc(q.note)}</span>
      </div>
      ${floors}
      ${qty > 1 && q.hasCostBasis
        ? `<div class="th-total">${moneyExact(q.totalNetProfit)} across all ${qty} units</div>` : ''}
      <details class="th-lines">
        <summary>Where ${moneyExact(q.totalDeductions)} of a ${moneyExact(q.grossRevenue)} sale goes
          (${Number(q.feeLoadPercent).toFixed(1)}%)</summary>
        <div class="th-line th-line-gross"><span>Sale price + buyer shipping</span><span>${moneyExact(q.grossRevenue)}</span></div>
        ${lines}
        <div class="th-line th-line-total"><span>Lands in your account</span><span>${moneyExact(q.netProceeds)}</span></div>
        ${q.hasCostBasis
          ? `<div class="th-line th-line-cost"><span>What you paid</span><span>−${moneyExact(q.unitCost)}</span></div>
             <div class="th-line th-line-net"><span>Net profit${q.marginPercent != null
                ? ` · ${Number(q.marginPercent).toFixed(1)}% margin` : ''}${q.roiPercent != null
                ? ` · ${Number(q.roiPercent).toFixed(0)}% ROI` : ''}</span><span>${moneyExact(q.netProfit)}</span></div>`
          : ''}
      </details>`;
  }

  // ── The ad rate this margin can carry, in the editor ──────────────────────
  // Promoted Listings is the one cost a seller opts into after the price is set, and eBay's own
  // suggested rate is computed with no knowledge of what the item cost. Sizing it here — against
  // the net just calculated above, live as the price changes — is the difference between choosing a
  // rate and accepting one. Same endpoint and same math as the Ad Rate Advisor page.
  async function refreshAdRateStrip(prefix, price, cost) {
    const el = $(`${prefix}-th-ads`);
    if (!el) return;

    if (!(price > 0) || !(cost > 0)) {
      el.innerHTML = '<div class="th-ads-idle">Add what you paid to see the Promoted Listings ad rate this margin can carry.</div>';
      return;
    }

    try {
      const { res, body } = await safePost('/api/promoted/advise', {
        title: $(`${prefix}-title`)?.value || '',
        category: $(`${prefix}-category`)?.value || '',
        price,
        unitCost: cost,
        buyerPaidShipping: thNum(`${prefix}-buyer-shipping`),
        shippingCost: $(`${prefix}-ship-cost`)?.value.trim() ? thNum(`${prefix}-ship-cost`) : null,
      });
      if (!res.ok) throw new Error(body?.error || 'Could not size an ad rate.');
      renderAdRateStrip(el, body);
    } catch {
      // A blank strip would read as "ads are free on this one". Say what actually happened.
      el.innerHTML = '<div class="th-ads-idle">The ad-rate check could not be reached — your price and take-home above are unaffected.</div>';
    }
  }

  function renderAdRateStrip(el, a) {
    const rec = a.recommendedRatePercent;
    const pct = n => `${Number(n).toFixed(1)}%`;

    if (rec == null) {
      el.innerHTML = `<div class="th-ads-idle">${esc(a.note || '')}</div>`;
      return;
    }

    // Below zero the server's own copy is the right copy — "this sale already loses money" and
    // "the margin is too thin to carry ads" are different problems and must not read the same.
    const headline = rec > 0
      ? `Run ads at ${pct(rec)} — ${moneyExact(a.adFeeAtRecommended)} a sale`
      : a.verdict === 'no_margin' ? 'No margin to advertise' : 'Don\'t promote this one';

    const detail = rec <= 0
      ? (a.note || '')
      : `Leaves ${moneyExact(a.netPerSaleAtRecommended)} of your ${moneyExact(a.netPerSaleNoAds)}. ` +
        (a.breakEvenLiftAtRecommendedPercent != null
          ? `It has to lift sales ${pct(a.breakEvenLiftAtRecommendedPercent)} to pay for itself.`
          : '');

    el.innerHTML = `
      <div class="th-ads-row th-ads-${rec > 0 ? 'on' : 'off'}">
        <div>
          <strong class="th-ads-headline">${esc(headline)}</strong>
          <span class="th-ads-note">${esc(detail)}</span>
        </div>
        <div class="th-ads-meta">
          <span title="Published typical ad rate for this category — override it with what your Seller Hub shows">${esc(a.categoryLabel)} pays ${pct(a.categoryRatePercent)}</span>
          ${a.maxSustainableRatePercent != null
            ? `<span class="th-ads-ceiling" title="Above this the ad fee is bigger than the whole profit on the sale">ceiling ${pct(a.maxSustainableRatePercent)}</span>` : ''}
        </div>
      </div>`;
  }

  function floorBasisText(q) {
    if (q.minimumOfferBasis === 'margin_target')
      return `Your ${Number(q.minimumMarginPercent).toFixed(1)}% minimum margin. Set it in Fees & Costs.`;
    if (q.minimumOfferBasis === 'profit_target')
      return `Keeps the ${moneyExact(q.minimumNetProfit)} per sale you said you won't go under.`;
    return 'Your floor is break-even — set a minimum profit in Fees & Costs to raise it.';
  }

  // The cost the seller types in the drawer is the same cost Inventory Health and Watcher Offers
  // need, so it is written to the shared cost-basis store rather than living in the form. Entering
  // it once in the place they are already looking at the price is the whole reason those screens
  // ever have a break-even to work with.
  async function loadCostBasisInto(prefix, listing) {
    if (!$(`${prefix}-unit-cost`)) return;
    set(`${prefix}-unit-cost`, '');

    const listingId = listing?.listingId || '';
    const sku = listing?.sku || '';
    if (!listingId && !sku) return scheduleTakeHome(prefix);

    try {
      costBasisCache ??= await fetch('/api/inventory/cost-basis').then(r => r.json());
      const match = (costBasisCache || []).find(e =>
        (listingId && e.listingId === listingId) || (sku && e.sku && e.sku === sku));
      if (match) set(`${prefix}-unit-cost`, Number(match.unitCost + match.inboundShipping).toFixed(2));
    } catch { /* no saved cost — the panel just asks for one */ }

    scheduleTakeHome(prefix);
  }

  async function saveCostBasisFromDrawer(listing) {
    const cost = thNum('f-unit-cost');
    const listingId = listing?.listingId || '';
    const sku = listing?.sku || '';
    if (cost <= 0 || (!listingId && !sku)) return;

    try {
      await safePost('/api/inventory/cost-basis',
        [{ listingId, sku, unitCost: cost, inboundShipping: 0, note: 'Entered in the listing editor' }]);
      costBasisCache = null;   // next drawer open re-reads it
    } catch { /* saving the cost is a convenience; never block the listing save on it */ }
  }

  // ── Market Research (inside the Edit Listing drawer) ───────────────────────
  // Reuses the existing /api/sold-comps endpoint, which already layers Terapeak
  // (when the seller's session is connected) over Marketplace Insights and falls
  // back to eBay research deep links. Nothing is duplicated here.

  let lastResearch = null;   // most recent /api/sold-comps payload

  // Strongest available identifier wins — a UPC/EAN/ISBN/MPN match is far more
  // precise than a title match, which is mostly marketing words.
  function buildResearchQuery() {
    const v = id => ($(id)?.value || '').trim();
    const upc = v('f-upc'), ean = v('f-ean'), isbn = v('f-isbn'), mpn = v('f-mpn');
    if (upc)  return { q: upc,  basis: 'UPC' };
    if (ean)  return { q: ean,  basis: 'EAN' };
    if (isbn) return { q: isbn, basis: 'ISBN' };

    const brand = v('f-brand');
    if (mpn)          return { q: (brand ? brand + ' ' : '') + mpn, basis: brand ? 'Brand + MPN' : 'MPN' };
    const title = v('f-title');
    if (brand && title) return { q: `${brand} ${title}`.slice(0, 120), basis: 'Brand + Title' };
    if (title)          return { q: title.slice(0, 120), basis: 'Title' };
    return { q: '', basis: '' };
  }

  function bindMarketResearch() {
    if (!$('mr-panel')) return;

    on('btn-mr-sold', 'click', runSoldResearch);

    on('btn-mr-terapeak', 'click', () => {
      const { q } = buildResearchQuery();
      if (!q) return setResearchStatus('Add a title, brand, MPN or UPC first.', 'empty');
      const url = lastResearch?.terapeakUrl ||
        'https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&dayRange=60&keywords=' + encodeURIComponent(q);
      window.open(url, '_blank', 'noopener');
    });

    on('btn-mr-active', 'click', () => {
      const { q } = buildResearchQuery();
      if (!q) return setResearchStatus('Add a title, brand, MPN or UPC first.', 'empty');
      window.open('https://www.ebay.com/sch/i.html?_nkw=' + encodeURIComponent(q) + '&_sop=12',
                  '_blank', 'noopener');
    });

    on('btn-mr-finder', 'click', () => {
      const { q } = buildResearchQuery();
      if (q) sessionStorage.setItem('opportunityPrefill', q);
      handleNav('opportunity');
    });

    on('btn-mr-apply', 'click', () => {
      const price = recommendedPrice();
      if (price == null) return;
      set('f-price', price.toFixed(2));
      $('f-price')?.dispatchEvent(new Event('input', { bubbles: true }));
      setResearchStatus(`Price set to ${money(price)} locally — not sent to eBay until you update the listing.`, '');
      addActivity('Recommended price applied', money(price));
    });

    on('btn-mr-copy', 'click', async () => {
      const avg = lastResearch?.average;
      if (!avg) return;
      try { await navigator.clipboard.writeText(Number(avg).toFixed(2)); setResearchStatus('Average sold price copied.', ''); }
      catch { setResearchStatus('Could not access the clipboard.', 'error'); }
    });
  }

  function setResearchStatus(msg, kind) {
    const el = $('mr-status');
    if (!el) return;
    el.textContent = msg || '';
    el.className = 'mr-status' + (kind ? ' ' + kind : '');
  }

  // Median is the anchor rather than the mean: a couple of parts-only or
  // mislabelled comps skew an average badly on low sold counts.
  function recommendedPrice() {
    if (!lastResearch || !lastResearch.count) return null;
    const median = Number(lastResearch.median) || 0;
    const avg    = Number(lastResearch.average) || 0;
    const base   = median > 0 ? median : avg;
    return base > 0 ? Math.round(base * 100) / 100 : null;
  }

  async function runSoldResearch() {
    const { q, basis } = buildResearchQuery();
    const results = $('mr-results');

    if (!q) {
      results?.classList.add('hidden');
      return setResearchStatus('Add a title, brand, MPN or UPC before researching.', 'empty');
    }

    setText('mr-query', '');
    $('mr-query').innerHTML = `Searching by <strong>${esc(basis)}</strong>: ${esc(q)}`;
    setResearchStatus('Searching sold listings…', 'working');
    $('btn-mr-sold')?.setAttribute('disabled', 'disabled');

    try {
      // Cost and current ask ride along so the answer comes back already costed — a median is a
      // gross number, and the seller pricing off it needs to see what is left of it.
      const params = new URLSearchParams({ q });
      const cost = thNum('f-unit-cost');
      const ask  = thNum('f-price');
      if (cost > 0) params.set('cost', String(cost));
      if (ask  > 0) params.set('ask',  String(ask));
      const buyerShipping = thNum('f-buyer-shipping');
      if (buyerShipping > 0) params.set('buyerShipping', String(buyerShipping));

      const res = await fetch('/api/sold-comps?' + params.toString());
      const data = await res.json();
      if (!res.ok) throw new Error(data?.error || `Request failed (${res.status})`);
      lastResearch = data;
      renderResearch(data);
    } catch (err) {
      results?.classList.add('hidden');
      setResearchStatus('Research failed: ' + (err?.message || 'unknown error') +
                        ' — you can still open Terapeak or eBay search above.', 'error');
    } finally {
      $('btn-mr-sold')?.removeAttribute('disabled');
    }
  }

  function renderResearch(d) {
    const results = $('mr-results');
    const count = Number(d.count) || 0;

    if (!count) {
      results?.classList.add('hidden');
      setResearchStatus(
        d.source === 'none'
          ? 'No comparable sales available. Terapeak may not be connected (Settings → Terapeak), or eBay has not approved sold-data access for this account. Use the buttons above to research manually.'
          : 'No comparable sold listings found for this query.',
        'empty');
      return;
    }

    results?.classList.remove('hidden');
    setResearchStatus('');

    const srcLabel = { terapeak: 'Terapeak', marketplace_insights: 'eBay Insights', none: 'Links only' }[d.source] || d.source || '—';
    setText('mr-avg',    money(d.average));
    setText('mr-median', money(d.median));
    setText('mr-min',    money(d.min));
    setText('mr-max',    money(d.max));
    setText('mr-count',  String(count));
    setText('mr-source', srcLabel);

    const reco = recommendedPrice();
    setText('mr-reco-price', reco == null ? '—' : money(reco));

    // The recommendation is a gross price; what matters is what survives it. The server already
    // costed the median against the seller's fee profile, so say the net here rather than leaving
    // "Recommended Price $120" to be read as $120 of income.
    const confidence = count < 3
      ? `Low confidence — only ${count} comparable sale${count === 1 ? '' : 's'}.`
      : `Based on the median of ${count} sold listings.`;
    const netAtMedian = d.pricing?.quotes?.median;
    setText('mr-reco-note', netAtMedian
      ? `${confidence} ${netAtMedian.hasCostBasis
          ? `Sell at this and you keep ${moneyExact(netAtMedian.netProfit)} after fees`
            + `${d.pricing.minimumOfferPrice ? `; don't accept under ${moneyExact(d.pricing.minimumOfferPrice)}` : ''}.`
          : `${moneyExact(netAtMedian.netProceeds)} of it reaches your account after fees and shipping.`}`
      : confidence);

    // Flag comps far from the median so a skewed sample is visible rather than silent.
    const median = Number(d.median) || 0;
    const items = Array.isArray(d.items) ? d.items.slice(0, 12) : [];
    const box = $('mr-comps');
    if (!box) return;

    if (!items.length) { box.innerHTML = ''; return; }

    let outliers = 0;
    box.innerHTML = items.map(it => {
      const price = Number(it.price ?? it.Price) || 0;
      const title = it.title ?? it.Title ?? 'Comparable sale';
      const url   = it.url ?? it.Url ?? it.itemUrl ?? '';
      const isOutlier = median > 0 && price > 0 && (price > median * 2 || price < median * 0.5);
      if (isOutlier) outliers++;
      const label = esc(String(title)).slice(0, 140);
      return `<div class="mr-comp${isOutlier ? ' outlier' : ''}">
        <span class="mr-comp-title">${url ? `<a href="${esc(url)}" target="_blank" rel="noopener noreferrer">${label}</a>` : label}</span>
        <span class="mr-comp-price">${money(price)}</span>
      </div>`;
    }).join('') + (outliers
      ? `<div class="mr-outlier-note">${outliers} comparable${outliers === 1 ? '' : 's'} priced far from the median — highlighted above.</div>`
      : '');
  }

  // ── Cross-List to other marketplaces ───────────────────────────────────────
  // Reformats the open draft for Facebook Marketplace, Mercari and Amazon via
  // /api/crosslist/export. Everything here is local text and CSV — no request
  // ever reaches those sites, and the eBay listing is never touched.

  let lastCrossList = null;   // most recent /api/crosslist/export payload
  let xlActiveTab   = '';

  function bindCrossListing() {
    if (!$('xl-panel')) return;

    on('btn-xl-generate', 'click', runCrossListing);

    $('xl-tabs')?.addEventListener('click', e => {
      const tab = e.target.closest('.xl-tab');
      if (!tab) return;
      xlActiveTab = tab.dataset.market;
      renderCrossListTabs();
      renderCrossListCard();
    });

    $('xl-body')?.addEventListener('click', e => {
      const btn = e.target.closest('[data-xl-action]');
      if (!btn) return;
      const listing = xlActiveListing();
      if (!listing) return;

      if (btn.dataset.xlAction === 'copy-title')  xlCopy(listing.title, 'Title');
      if (btn.dataset.xlAction === 'copy-desc')   xlCopy(listing.description, 'Description');
      if (btn.dataset.xlAction === 'copy-price')  xlCopy(Number(listing.netParityPrice || 0).toFixed(2), 'Price');
      if (btn.dataset.xlAction === 'download')    xlDownloadCsv(listing);
      if (btn.dataset.xlAction === 'open')        window.open(listing.createUrl, '_blank', 'noopener');
    });
  }

  function xlActiveListing() {
    return lastCrossList?.listings?.find(l => l.marketplace === xlActiveTab) || null;
  }

  function setCrossListStatus(msg, kind) {
    const el = $('xl-status');
    if (!el) return;
    el.textContent = msg || '';
    el.className = 'xl-status' + (kind ? ' ' + kind : '');
  }

  async function runCrossListing() {
    const targets = Array.from(document.querySelectorAll('[data-xl-target]'))
      .filter(cb => cb.checked)
      .map(cb => cb.dataset.xlTarget);

    if (!targets.length) {
      $('xl-results')?.classList.add('hidden');
      return setCrossListStatus('Pick at least one marketplace to export to.', 'empty');
    }

    setCrossListStatus('Building marketplace listings…', 'working');
    $('btn-xl-generate')?.setAttribute('disabled', 'disabled');

    try {
      const res = await fetch('/api/crosslist/export', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...buildPayload(), targets }),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data?.error || `Request failed (${res.status})`);

      lastCrossList = data;
      if (!data.listings?.some(l => l.marketplace === xlActiveTab))
        xlActiveTab = data.listings?.[0]?.marketplace || '';

      renderCrossList();
      addActivity('Cross-listings generated', `${data.listings?.length || 0} marketplace(s)`);
    } catch (err) {
      $('xl-results')?.classList.add('hidden');
      setCrossListStatus('Could not build the cross-listings: ' + (err?.message || 'unknown error'), 'error');
    } finally {
      $('btn-xl-generate')?.removeAttribute('disabled');
    }
  }

  function renderCrossList() {
    const results = $('xl-results');
    if (!lastCrossList?.listings?.length) {
      results?.classList.add('hidden');
      return setCrossListStatus('Nothing to export from this draft yet.', 'empty');
    }

    results?.classList.remove('hidden');
    setCrossListStatus(
      `Ready. Your eBay listing at ${moneyExact(lastCrossList.ebayPrice)} nets you ` +
      `${moneyExact(lastCrossList.ebayNet)} after ${moneyExact(lastCrossList.ebayFees)} in eBay fees — ` +
      `each marketplace below is priced to match that.`, '');

    $('xl-warnings').innerHTML = (lastCrossList.warnings || [])
      .map(w => `<div class="xl-warning">${esc(w)}</div>`).join('');

    renderCrossListTabs();
    renderCrossListCard();
  }

  function renderCrossListTabs() {
    const box = $('xl-tabs');
    if (!box) return;
    box.innerHTML = (lastCrossList?.listings || []).map(l => {
      const count = (l.warnings || []).length;
      return `<button type="button" class="xl-tab${l.marketplace === xlActiveTab ? ' active' : ''}"
        role="tab" aria-selected="${l.marketplace === xlActiveTab}" data-market="${esc(l.marketplace)}">
        ${esc(l.displayName)}${count ? `<span class="xl-tab-flag" title="${count} thing${count === 1 ? '' : 's'} to check">${count}</span>` : ''}
      </button>`;
    }).join('');
  }

  // The number that makes this worth doing: what the seller actually takes home
  // on each site, and how far the price has to move to keep it level with eBay.
  function crossListPriceNote(l) {
    const ebayPrice = Number(lastCrossList?.ebayPrice) || 0;
    const ebayNet   = Number(lastCrossList?.ebayNet) || 0;
    const parity    = Number(l.netParityPrice) || 0;
    const atSame    = Number(l.netAtSamePrice) || 0;

    if (ebayPrice <= 0) return 'Set a price on this draft to see fee-adjusted pricing.';

    const gap = Math.round((parity - ebayPrice) * 100) / 100;

    if (Math.abs(gap) < 0.01)
      return `${l.displayName}'s fees land within a cent of eBay's — list at the same ${moneyExact(ebayPrice)}.`;

    if (gap < 0)
      return `${l.displayName} takes ${moneyExact(ebayPrice - parity)} less than eBay in fees on this item. ` +
             `You can undercut your own eBay price and list at ${moneyExact(parity)} while still taking home ` +
             `${moneyExact(ebayNet)} — or list at ${moneyExact(ebayPrice)} and pocket ${moneyExact(atSame - ebayNet)} more.`;

    return `Listing at your eBay price of ${moneyExact(ebayPrice)} here would only net ${moneyExact(atSame)} — ` +
           `${moneyExact(ebayNet - atSame)} less than eBay, because of ${l.feePercent}% fees. ` +
           `List at ${moneyExact(parity)} to take home the same ${moneyExact(ebayNet)}.`;
  }

  function renderCrossListCard() {
    const box = $('xl-body');
    const l = xlActiveListing();
    if (!box) return;
    if (!l) { box.innerHTML = ''; return; }

    const titleOver = l.titleTruncated ? ' over' : '';
    const descOver  = l.descriptionTruncated ? ' over' : '';
    const importLabel = l.importSupport === 'manual' ? 'Download Worksheet CSV' : 'Download Import CSV';

    box.innerHTML = `
      <div class="xl-card">
        <div class="xl-card-head">
          <h4>${esc(l.displayName)}</h4>
          <div class="xl-card-actions">
            <button type="button" class="btn btn-secondary small" data-xl-action="open">Open ${esc(l.displayName)}</button>
            <button type="button" class="btn btn-ghost small" data-xl-action="download">${esc(importLabel)}</button>
          </div>
        </div>

        <div class="xl-price">
          <div class="xl-price-cell">
            <span>List at your eBay price</span><strong>${moneyExact(l.samePrice)}</strong>
          </div>
          <div class="xl-price-cell">
            <span>You'd net</span><strong>${moneyExact(l.netAtSamePrice)}</strong>
          </div>
          <div class="xl-price-cell headline">
            <span>Price to match eBay take-home</span><strong>${moneyExact(l.netParityPrice)}</strong>
          </div>
          <div class="xl-price-note">${esc(crossListPriceNote(l))}</div>
          <div class="xl-price-note">${esc(l.feeNote)}</div>
        </div>

        <div class="xl-block">
          <div class="xl-block-head">
            <label>Title</label>
            <span class="xl-count${titleOver}">${l.title.length} / ${l.titleLimit}</span>
          </div>
          <textarea rows="2" readonly>${esc(l.title)}</textarea>
          <div class="xl-card-actions">
            <button type="button" class="btn btn-primary small" data-xl-action="copy-title">Copy Title</button>
            <button type="button" class="btn btn-ghost small" data-xl-action="copy-price">Copy Price</button>
          </div>
        </div>

        <div class="xl-block">
          <div class="xl-block-head">
            <label>Description</label>
            <span class="xl-count${descOver}">${l.description.length} / ${l.descriptionLimit}</span>
          </div>
          <textarea rows="9" readonly>${esc(l.description)}</textarea>
          <div class="xl-card-actions">
            <button type="button" class="btn btn-primary small" data-xl-action="copy-desc">Copy Description</button>
          </div>
        </div>

        <div class="xl-fields">
          ${(l.fields || []).map(f => `
            <div class="xl-field${f.required && !f.value ? ' missing' : ''}">
              <span>${esc(f.name)}${f.required ? ' *' : ''}</span>
              <strong title="${esc(f.value || 'Missing')}">${esc(f.value || 'Missing')}</strong>
            </div>`).join('')}
        </div>

        ${(l.warnings || []).length
          ? `<div class="xl-warnings">${l.warnings.map(w => `<div class="xl-warning">${esc(w)}</div>`).join('')}</div>`
          : ''}

        <p class="xl-import-note">${esc(l.importNote)}</p>
      </div>`;
  }

  async function xlCopy(text, label) {
    try {
      await navigator.clipboard.writeText(text || '');
      setCrossListStatus(`${label} copied — paste it straight into ${xlActiveListing()?.displayName || 'the marketplace'}.`, '');
    } catch {
      setCrossListStatus('Could not access the clipboard. Select the text and copy it manually.', 'error');
    }
  }

  function xlDownloadCsv(listing) {
    // BOM so Excel opens the UTF-8 text with accents and symbols intact.
    const blob = new Blob(['﻿' + (listing.csv || '')], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = listing.csvFilename || 'cross-listing.csv';
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    setCrossListStatus(`${listing.csvFilename} downloaded.`, '');
  }

  // ── Edit Listing drawer ────────────────────────────────────────────────────
  // Rather than duplicating the (large, working) listing form, the existing
  // #form-section node is relocated into the drawer body once at startup. Every
  // field id, collector and save handler therefore keeps working unchanged —
  // the drawer only controls visibility, focus and unsaved-change safety.

  let drawerReturnFocusEl = null;   // element that had focus before opening
  let drawerScrollY       = 0;      // page scroll to restore on close
  let drawerListing       = null;   // listing being edited, so its cost basis can be written back
  let drawerBaseline      = '';     // serialised form state at open, for dirty check

  function initEditDrawer() {
    const body = $('edit-drawer-body');
    const form = $('form-section');
    if (!body || !form) return;     // markup missing — leave legacy inline behaviour

    body.appendChild(form);         // move, not clone: preserves all live listeners

    on('edit-drawer-close', 'click', () => closeEditDrawer());
    $('edit-drawer-overlay')?.addEventListener('click', () => closeEditDrawer());

    document.addEventListener('keydown', e => {
      if (!isEditDrawerOpen()) return;
      // Let nested overlays (photo editor, modals) consume keys first.
      if (document.querySelector('.modal-overlay:not(.hidden), .photo-editor-overlay')) return;

      if (e.key === 'Escape') { closeEditDrawer(); return; }

      // Ctrl/Cmd+S saves the draft — deliberately bound to the draft-preview
      // action, never to the live eBay revision, which must stay an explicit,
      // confirmed click.
      if ((e.ctrlKey || e.metaKey) && !e.shiftKey && (e.key === 's' || e.key === 'S')) {
        e.preventDefault();
        const btn = $('btn-post');
        if (btn && !btn.classList.contains('hidden') && !btn.disabled) btn.click();
        else setResearchStatus?.('', '');
        return;
      }

      // Ctrl/Cmd+R runs sold-price research without reaching for the mouse.
      if ((e.ctrlKey || e.metaKey) && e.shiftKey && (e.key === 'r' || e.key === 'R')) {
        e.preventDefault();
        $('mr-panel')?.setAttribute('open', 'open');
        $('btn-mr-sold')?.click();
      }
    });

    // Keep focus inside the drawer while it is modal.
    document.addEventListener('focusin', e => {
      if (!isEditDrawerOpen()) return;
      const drawer = $('edit-drawer');
      if (drawer && !drawer.contains(e.target)) drawer.focus();
    });

    // Any edit inside the drawer marks it dirty.
    body.addEventListener('input',  () => refreshDrawerDirty());
    body.addEventListener('change', () => refreshDrawerDirty());
  }

  function isEditDrawerOpen() {
    return !!$('edit-drawer')?.classList.contains('open');
  }

  // A cheap, order-stable snapshot of every control in the drawer. Used only to
  // detect "has the user touched anything", never to persist listing data.
  function snapshotDrawerState() {
    const body = $('edit-drawer-body');
    if (!body) return '';
    return [...body.querySelectorAll('input, select, textarea')]
      .map(el => (el.type === 'checkbox' || el.type === 'radio') ? (el.checked ? '1' : '0') : (el.value ?? ''))
      .join('');
  }

  function refreshDrawerDirty() {
    const drawer = $('edit-drawer');
    if (!drawer) return;
    drawer.classList.toggle('dirty', snapshotDrawerState() !== drawerBaseline);
  }

  function markDrawerClean() {
    drawerBaseline = snapshotDrawerState();
    $('edit-drawer')?.classList.remove('dirty');
  }

  function openEditDrawer(listing) {
    const drawer  = $('edit-drawer');
    const overlay = $('edit-drawer-overlay');
    if (!drawer || !overlay) return;

    drawerReturnFocusEl = document.activeElement;
    drawerScrollY = window.scrollY;

    const title = listing?.title || listing?.sku || listing?.listingId || 'Listing';
    setText('edit-drawer-title', title);
    const bits = [];
    if (listing?.sku)       bits.push(`SKU ${listing.sku}`);
    if (listing?.listingId) bits.push(`ID ${listing.listingId}`);
    if (listing?.status)    bits.push(String(listing.status).toUpperCase());
    setText('edit-drawer-sub', bits.join('  ·  '));

    overlay.classList.add('open');
    drawer.classList.add('open');
    drawer.setAttribute('aria-hidden', 'false');
    drawer.setAttribute('tabindex', '-1');
    document.body.classList.add('drawer-open');

    $('edit-drawer-body').scrollTop = 0;
    // Pull the cost the seller already recorded for this listing (Inventory Health, or a previous
    // visit here) so the take-home panel opens with a real break-even instead of asking again.
    drawerListing = listing || null;
    loadCostBasisInto('f', listing);
    markDrawerClean();               // baseline AFTER the form has been filled
    setTimeout(() => drawer.focus(), 30);
  }

  function closeEditDrawer(force) {
    const drawer = $('edit-drawer');
    if (!drawer || !isEditDrawerOpen()) return;

    if (!force && drawer.classList.contains('dirty') &&
        !confirm('You have unsaved changes to this listing.\n\nClose the editor and discard them?')) return;

    drawer.classList.remove('open', 'dirty');
    drawer.setAttribute('aria-hidden', 'true');
    $('edit-drawer-overlay')?.classList.remove('open');
    document.body.classList.remove('drawer-open');

    // Restore the caller's scroll position and keyboard focus.
    window.scrollTo({ top: drawerScrollY, behavior: 'auto' });
    if (drawerReturnFocusEl?.isConnected) {
      try { drawerReturnFocusEl.focus({ preventScroll: true }); } catch { /* non-focusable */ }
    }
    drawerReturnFocusEl = null;
  }

  function loadListingIntoForm(listing, sourceEl) {
    document.querySelectorAll('.listing-card.active, .listings-table tr.active').forEach(c => c.classList.remove('active'));
    sourceEl?.classList?.add('active');

    activeOfferId = listing.offerId || '';
    activeListingId = listing.listingId || '';
    activeSku = listing.sku || '';
    activeListingStatus = listing.status || '';
    pendingDraftPayload = null;
    hideDraftPreview();

    $('btn-post')?.classList.add('hidden');
    $('btn-create-ebay-draft')?.classList.add('hidden');
    $('btn-update')?.classList.toggle('hidden', !canReviseOnEbay(listing));
    $('btn-new-listing')?.classList.remove('hidden');

    const d = listing.data || {};
    fillForm(d);
    set('f-format', d.listingFormat || 'FIXED_PRICE');
    $('form-section')?.classList.remove('hidden');

    // Present the form in the right-side drawer when that markup is present;
    // fall back to the original inline scroll behaviour if it is not.
    if ($('edit-drawer')) openEditDrawer(listing);
    else $('form-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });

    addActivity('Edit opened', listing.title || listing.sku || listing.listingId || 'Listing selected');
  }

  function updateStats() {
    const active = cachedListings.filter(l => ['ACTIVE', 'PUBLISHED'].includes((l.status || '').toUpperCase())).length;
    const qty = cachedListings.reduce((sum, l) => sum + (parseInt(l.quantity, 10) || 0), 0);
    const value = cachedListings.reduce((sum, l) => sum + ((parseFloat(l.price) || 0) * (parseInt(l.quantity, 10) || 0)), 0);
    const review = cachedListings.filter(l => !l.thumbnailUrl || !l.title || (l.status || '').toUpperCase() !== 'PUBLISHED').length;

    setText('stat-active', active);
    setText('stat-quantity', qty);
    setText('stat-value', money(value));
    setText('stat-review', review);
  }

  function addActivity(title, detail) {
    const list = $('activity-list');
    if (!list) return;
    // The "no activity yet" block is markup, not a row — the first real entry
    // retires it.
    list.querySelector('.state')?.remove();
    const item = document.createElement('div');
    item.className = 'activity-item';
    item.innerHTML = `<strong>${esc(title)}</strong><span>${esc(detail)} - ${new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>`;
    list.prepend(item);
    while (list.children.length > 6) list.lastElementChild?.remove();
  }

  function updateAuthUI(connected) {
    isConnected = connected;
    if (connected) {
      $('auth-status')?.classList.remove('hidden');
      $('btn-connect')?.classList.add('hidden');
      $('btn-disconnect')?.classList.remove('hidden');
    } else {
      $('auth-status')?.classList.add('hidden');
      $('btn-connect')?.classList.remove('hidden');
      $('btn-disconnect')?.classList.add('hidden');
    }
    updateSetupChecklist(null, connected, null);
  }

  function updateSetupChecklist(hasAiKey, hasEbay, hasOpenAi) {
    const checklist = $('setup-checklist');
    if (!checklist) return;

    // A partial refresh (e.g. eBay-only, which passes null for the key steps)
    // must not wipe a step it knows nothing about, so an omitted argument keeps
    // that step's current state instead of collapsing to false.
    const step1Done = hasAiKey  !== null && hasAiKey  !== undefined ? !!hasAiKey  : isSetupStepDone('step1');
    const step2Done = hasEbay   !== null && hasEbay   !== undefined ? !!hasEbay   : isConnected;
    const step3Done = hasOpenAi !== null && hasOpenAi !== undefined ? !!hasOpenAi : isSetupStepDone('step3');

    // The done look lives in .setup-step.is-done, so a step can go back to
    // pending if the key is later cleared — the old inline styles were
    // one-way and left a green tick on a step that no longer applied.
    markSetupStep('step1', step1Done, 'Key saved');
    markSetupStep('step2', step2Done, 'Connected');
    markSetupStep('step3', step3Done, 'Key saved');

    // Hide checklist once the two required steps are done (step 3 is optional)
    if (step1Done && step2Done) {
      checklist.classList.add('hidden');
    } else {
      checklist.classList.remove('hidden');
    }
  }

  function isSetupStepDone(prefix) {
    return $(`${prefix}-row`)?.classList.contains('is-done') === true;
  }

  function markSetupStep(prefix, done, doneLabel) {
    const row  = $(`${prefix}-row`);
    const icon = $(`${prefix}-icon`);
    const btn  = $(`${prefix}-btn`);
    const step = prefix.replace('step', '');

    row?.classList.toggle('is-done', done);
    if (icon) icon.textContent = done ? '✓' : step;
    if (btn) {
      if (done) {
        if (!btn.dataset.pendingLabel) btn.dataset.pendingLabel = btn.textContent;
        btn.textContent = `✓ ${doneLabel}`;
        btn.disabled = true;
      } else if (btn.dataset.pendingLabel) {
        btn.textContent = btn.dataset.pendingLabel;
        btn.disabled = false;
      }
    }
  }

  // ── Draft Tabs ────────────────────────────────────────────────

  let draftTabs = [];
  let activeDraftTabId = null;
  let draftTabCounter = 0;

  function newDraftTab(title, filename, data, imageBase64, mimeType, visualDesc) {
    draftTabCounter++;
    const tab = {
      id: draftTabCounter,
      title: title || 'New Draft',
      filename: filename || null,
      saved: !!filename,
      data: data || {},
      imageBase64: imageBase64 || '',
      mimeType: mimeType || 'image/jpeg',
      visualDescription: visualDesc || ''
    };
    draftTabs.push(tab);
    return tab;
  }

  function captureCurrentTab() {
    const tab = draftTabs.find(t => t.id === activeDraftTabId);
    if (!tab) return;
    tab.data = buildNlPayload();
    tab.title = tab.data.title || 'New Draft';
    tab.imageBase64 = nlImageBase64 || '';
    tab.mimeType = nlMimeType || 'image/jpeg';
    tab.visualDescription = window._nlVisualDescription || '';
    tab.saved = false;
  }

  function loadTabIntoForm(tab) {
    nlClearForm();
    nlImageBase64 = tab.imageBase64 || '';
    nlMimeType    = tab.mimeType || 'image/jpeg';
    window._nlVisualDescription = tab.visualDescription || '';

    if (nlImageBase64) {
      const img = $('nl-preview-img');
      if (img) img.src = 'data:' + nlMimeType + ';base64,' + nlImageBase64;
      $('nl-drop-zone')?.classList.add('hidden');
      $('nl-preview-wrap')?.classList.remove('hidden');
    } else {
      $('nl-drop-zone')?.classList.remove('hidden');
      $('nl-preview-wrap')?.classList.add('hidden');
    }

    if (tab.data && tab.data.title) fillNlForm(tab.data);
  }

  function switchDraftTab(id) {
    if (activeDraftTabId === id) return;
    captureCurrentTab();
    activeDraftTabId = id;
    const tab = draftTabs.find(t => t.id === id);
    if (tab) loadTabIntoForm(tab);
    renderDraftTabs();
  }

  function renderDraftTabs() {
    const bar = $('nl-tab-bar');
    if (!bar) return;
    const newBtn = $('nl-tab-new-btn');

    // Remove old tab buttons
    bar.querySelectorAll('.nl-tab').forEach(el => el.remove());

    draftTabs.forEach(tab => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'nl-tab' + (tab.id === activeDraftTabId ? ' active' : '');
      const displayTitle = tab.title.length > 24 ? tab.title.slice(0, 24) + '…' : tab.title;
      btn.innerHTML =
        '<span class="nl-tab-favicon">' + (tab.saved ? '💾' : '📄') + '</span>' +
        '<span class="nl-tab-title">' + esc(displayTitle) + '</span>' +
        '<span class="nl-tab-close" data-tabid="' + tab.id + '" title="Close">✕</span>';

      btn.addEventListener('click', e => {
        if (e.target.closest('.nl-tab-close')) return;
        switchDraftTab(tab.id);
      });
      bar.insertBefore(btn, newBtn);
    });

    bar.querySelectorAll('.nl-tab-close').forEach(x => {
      x.addEventListener('click', e => { e.stopPropagation(); closeDraftTab(parseInt(x.dataset.tabid)); });
    });
  }

  function closeDraftTab(id) {
    const idx = draftTabs.findIndex(t => t.id === id);
    if (idx === -1) return;
    draftTabs.splice(idx, 1);
    if (activeDraftTabId === id) {
      const next = draftTabs[Math.min(idx, draftTabs.length - 1)];
      if (next) { activeDraftTabId = next.id; loadTabIntoForm(next); }
      else addNewDraftTab();
    }
    renderDraftTabs();
  }

  async function clearAllSavedDrafts() {
    try {
      const r = await fetch('/api/local-drafts/list');
      if (!r.ok) return;
      const list = await r.json();
      for (const summary of list) {
        try {
          await fetch('/api/local-drafts/delete/' + encodeURIComponent(summary.filename), { method: 'DELETE' });
        } catch { /* skip failed deletes */ }
      }
    } catch { /* non-fatal */ }
  }

  async function clearAllDraftsAndTabs() {
    if (!confirm('Delete all saved drafts and clear all tabs? This cannot be undone.')) return;
    await clearAllSavedDrafts();
    draftTabs = [];
    activeDraftTabId = null;
    const tab = newDraftTab();
    activeDraftTabId = tab.id;
    nlClearAll();
    renderDraftTabs();
    addActivity('All drafts cleared', 'Ready for a new import');
  }

  async function loadAllDraftsAsTabs() {
    const btn = $('nl-load-all-drafts-btn');
    if (btn) { btn.disabled = true; btn.textContent = 'Loading…'; }
    try {
      const res   = await fetch('/api/local-drafts/list');
      const list  = await res.json();   // [{filename, title, savedAt}]
      if (!list.length) { alert('No saved drafts found.'); return; }

      // Close any blank tabs first
      draftTabs = draftTabs.filter(t => t.title !== 'New Draft' || t.saved);

      let loaded = 0;
      for (const summary of list) {
        // Skip if already open
        if (draftTabs.some(t => t.filename === summary.filename)) continue;
        try {
          const r2   = await fetch('/api/local-drafts/load/' + encodeURIComponent(summary.filename));
          if (!r2.ok) continue;
          const draft = await r2.json();
          const tab   = newDraftTab(draft.title, draft.filename, draft.data,
                                    draft.imageBase64, draft.mimeType, draft.visualDescription);
          tab.saved = true;
          loaded++;
        } catch { /* skip bad file */ }
      }

      if (loaded === 0) {
        alert('All drafts are already open.');
        return;
      }

      // Switch to the first newly loaded tab
      const firstNew = draftTabs[draftTabs.length - loaded];
      if (firstNew) { activeDraftTabId = firstNew.id; loadTabIntoForm(firstNew); }
      renderDraftTabs();
      $('new-listing-overlay')?.classList.remove('hidden');
      addActivity(`Loaded ${loaded} drafts as tabs`, 'Review and publish each one');
    } catch (e) {
      alert('Failed to load drafts: ' + e.message);
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'Open All Drafts'; }
    }
  }

  function addNewDraftTab() {
    const tab = newDraftTab();
    activeDraftTabId = tab.id;
    nlClearForm();
    nlImageBase64 = ''; nlMimeType = 'image/jpeg'; window._nlVisualDescription = '';
    $('nl-drop-zone')?.classList.remove('hidden');
    $('nl-preview-wrap')?.classList.add('hidden');
    renderDraftTabs();
  }

  // Saves straight to the server-managed Desktop\eBayListing folder via DraftStore (the same
  // endpoint the Bulk Catalog Import already uses) instead of letting the browser's native save
  // dialog put the file wherever the user navigates to. The dialog approach (previously
  // window.showSaveFilePicker with startIn:'desktop') only opens ON the Desktop — it doesn't
  // force saving INTO the eBayListing subfolder DraftStore actually scans, so a draft saved via
  // the dialog could land right next to that folder and still be invisible to "Open All Drafts".
  async function saveDraftLocal() {
    captureCurrentTab();
    const tab = draftTabs.find(t => t.id === activeDraftTabId);
    if (!tab) return;

    const btn = $('nl-btn-save-local');
    if (btn) { btn.disabled = true; btn.textContent = 'Saving…'; }

    const payload = {
      filename: tab.filename || null,
      title: tab.title,
      data: tab.data,
      imageBase64: tab.imageBase64 || null,
      mimeType: tab.mimeType || 'image/jpeg',
      visualDescription: tab.visualDescription || null
    };

    try {
      const res = await fetch('/api/local-drafts/save', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) throw new Error('Save failed (HTTP ' + res.status + ')');
      const { filename } = await res.json();

      tab.filename = filename;
      tab.saved    = true;
      addActivity('Draft saved', filename + ' — Desktop\\eBayListing\\' + filename);
      renderDraftTabs();
      if (btn) { btn.textContent = '✓ Saved'; setTimeout(() => { if (btn) { btn.disabled = false; btn.textContent = '💾 Save Draft'; } }, 1200); }

      // Open a fresh blank tab for the next listing
      addNewDraftTab();

    } catch (err) {
      if (btn) { btn.disabled = false; btn.textContent = '💾 Save Draft'; }
      alert('Save failed: ' + err.message);
    }
  }

  // ── New AI Listing modal ─────────────────────────────────────

  function bindNewListingModal() {
    const dropZone = $('nl-drop-zone');
    const fileInput = $('nl-file-input');

    dropZone?.addEventListener('click', e => {
      if (e.target !== fileInput) fileInput?.click();
    });
    dropZone?.addEventListener('dragover', e => {
      e.preventDefault();
      dropZone.classList.add('drag-over');
    });
    dropZone?.addEventListener('dragleave', e => {
      // Only remove drag-over when leaving the drop zone itself, not a child element
      if (!dropZone.contains(e.relatedTarget)) dropZone.classList.remove('drag-over');
    });
    dropZone?.addEventListener('drop', e => {
      e.preventDefault();
      dropZone.classList.remove('drag-over');
      // Try files first (file system drops), then items fallback (browser drags, screenshot tools)
      const file = e.dataTransfer.files[0] ||
        [...(e.dataTransfer.items || [])].find(i => i.kind === 'file' && i.type.startsWith('image/'))?.getAsFile();
      if (file) nlLoadFile(file);
    });
    fileInput?.addEventListener('change', () => {
      if (fileInput.files[0]) nlLoadFile(fileInput.files[0]);
    });

    // The drop zone is contenteditable=true purely so the browser's native right-click
    // menu offers "Paste" (Chrome only shows that item for editable elements). Block every
    // other editing operation so it never actually behaves like a text field.
    dropZone?.addEventListener('beforeinput', e => e.preventDefault());
    dropZone?.addEventListener('paste', e => {
      e.preventDefault();
      e.stopPropagation(); // handled here — don't let the global paste listener double-fire
      const imageItem = [...(e.clipboardData?.items || [])].find(i => i.type.startsWith('image/'));
      const file = imageItem?.getAsFile();
      if (file) nlLoadFile(file, 'Pasted screenshot');
    });

    // Global paste — load image when clipboard contains an image
    // Only skip if focus is in a text-entry field that legitimately consumes text paste
    const TEXT_PASTE_IDS = new Set(['nl-title','nl-description','nl-desc-text','nl-ai-modify-input','nl-url-input','nl-bulk-input']);
    document.addEventListener('paste', e => {
      const imageItem = [...(e.clipboardData?.items || [])].find(i => i.type.startsWith('image/'));
      if (!imageItem) return;
      const focused = document.activeElement;
      if (focused && TEXT_PASTE_IDS.has(focused.id)) return;
      const file = imageItem.getAsFile();
      if (!file) return;
      e.preventDefault();
      // Route to whichever paste-aware page is actually on screen — pasting while the
      // Opportunity Finder is open should feed the Supplier File Analyzer, not silently
      // jump to the AI Listing modal.
      if (!$('opportunity-section')?.classList.contains('hidden')) {
        oppSupplierLoadFile(file, 'Pasted supplier file');
        return;
      }
      openNewListingModal();
      nlLoadFile(file, 'Pasted screenshot');
    });

    on('nl-btn-clear', 'click', nlClearImage);
    on('nl-btn-reanalyze', 'click', nlAnalyze);
    on('nl-btn-retry', 'click', nlAnalyze);
    on('nl-btn-improve-seo', 'click', nlImproveSeo);

    // Description edit/preview tabs
    document.querySelectorAll('.desc-tab').forEach(tab => {
      tab.addEventListener('click', () => {
        const leaving = document.querySelector('.desc-tab.active')?.dataset.descTab;
        const arriving = tab.dataset.descTab;
        // Sync before switching away from text editor — merge the edited words back into
        // the EXISTING HTML structure (headings, bullets, inline styles) instead of
        // rebuilding fresh <p> tags, so editing text never destroys the SEO template.
        if (leaving === 'text') {
          const plain = $('nl-desc-text')?.value || '';
          const original = $('nl-description')?.value || '';
          if ($('nl-description')) $('nl-description').value = nlMergeTextIntoHtml(original, plain);
        }
        document.querySelectorAll('.desc-tab').forEach(t => t.classList.toggle('active', t === tab));
        $('nl-desc-edit-wrap')?.classList.toggle('hidden', arriving !== 'edit');
        $('nl-desc-text-wrap')?.classList.toggle('hidden', arriving !== 'text');
        $('nl-desc-preview-wrap')?.classList.toggle('hidden', arriving !== 'preview');
        if (arriving === 'text') {
          if ($('nl-desc-text')) $('nl-desc-text').value = nlHtmlToText($('nl-description')?.value || '');
        }
        if (arriving === 'preview') nlSyncDescPreview();
      });
    });
    on('nl-description', 'input', () => { nlSyncDescPreview(); nlUpdateDescCount(); $('nl-description').classList.remove('field-flagged'); $('nl-desc-preview')?.classList.remove('field-flagged'); });
    on('nl-desc-text', 'input', () => { nlUpdateDescCount(); $('nl-desc-text').classList.remove('field-flagged'); });
    on('nl-close', 'click', closeNewListingModal);
    on('nl-btn-cancel', 'click', closeNewListingModal);
    on('nl-btn-save-local', 'click', saveDraftLocal);
    on('nl-tab-new-btn', 'click', addNewDraftTab);
    on('nl-load-all-drafts-btn', 'click', loadAllDraftsAsTabs);
    on('nl-clear-drafts-btn', 'click', clearAllDraftsAndTabs);
    on('nl-btn-load-policies', 'click', () => loadPolicies(true));
    on('nl-btn-add-specific', 'click', () => nlAddSpecificRow('', ''));
    on('nl-best-offer', 'change', e => nlToggleBestOffer(e.target.checked));
    on('nl-format', 'change', e => {
      $('nl-duration-wrap').style.display = e.target.value === 'AUCTION' ? '' : 'none';
    });
    on('nl-title', 'input', () => { nlUpdateCharCount('nl-title', 'nl-title-count', 80); $('nl-title').classList.remove('field-flagged'); });
    on('nl-subtitle', 'input', () => nlUpdateCharCount('nl-subtitle', 'nl-subtitle-count', 55));

    bindCategorySearch('nl-category', 'nl-category-id', 'nl-category-dropdown', 'nl-cat-selected', 'nl-cat-selected-name', 'nl-cat-id-badge', 'nl-cat-clear');
    on('nl-url-go',    'click', nlAnalyzeUrl);
    on('nl-url-input', 'keydown', e => { if (e.key === 'Enter') nlAnalyzeUrl(); });
    $('nl-url-input')?.addEventListener('paste', e => {
      setTimeout(() => {
        const val = $('nl-url-input')?.value.trim() || '';
        if (val.startsWith('http')) nlAnalyzeUrl();
      }, 50);
    });
    on('nl-quickfill-go', 'click', nlQuickFillByName);
    on('nl-quickfill-input', 'keydown', e => { if (e.key === 'Enter') nlQuickFillByName(); });
    on('nl-sold-comps-close', 'click', () => $('nl-sold-comps-strip')?.classList.add('hidden'));
    on('nl-sold-comps-connect-btn', 'click', nlSoldCompsConnect);
    on('nl-bulk-go', 'click', nlBulkImport);
    on('nl-bulk-url-input', 'keydown', e => { if (e.key === 'Enter') nlBulkImport(); });
    on('nl-ai-modify-go', 'click', nlAiModify);
    on('nl-ai-modify-input', 'keydown', e => { if (e.key === 'Enter') nlAiModify(); });

    initListingReadiness();

    on('nl-btn-publish', 'click', () => nlSubmit('publish'));

    on('nl-btn-draft', 'click', () => nlSubmit('draft'));

    on('btn-new-from-edit', 'click', openNewListingModal);

    $('new-listing-overlay')?.addEventListener('keydown', e => {
      if (e.key === 'Escape') closeNewListingModal();
    });
  }

  function openNewListingModal(keepTabs = false) {
    if (!$('nl-photo-grid')?.querySelector('.nl-photo-slot')) initPhotoGrid();
    // Always start fresh unless explicitly told to keep existing tabs (e.g. bulk import)
    if (!keepTabs) {
      draftTabs = [];
      activeDraftTabId = null;
      const tab = newDraftTab();
      activeDraftTabId = tab.id;
      renderDraftTabs();
    } else if (draftTabs.length === 0) {
      const tab = newDraftTab();
      activeDraftTabId = tab.id;
      renderDraftTabs();
    }
    nlClearAll();
    applyListingDefaults();
    $('new-listing-overlay')?.classList.remove('hidden');
    $('new-listing-overlay')?.focus();
    nlRefreshSoldCompsConnect();   // show/hide the Connect-to-Terapeak prompt in the bar
    nlRunReadiness(true);          // score the blank form so the bar is there from the start
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'ai'));
    if (cachedPolicies) {
      fillNlPolicySelects();
    } else if (isConnected) {
      const msg = $('nl-policy-msg');
      if (msg) { msg.textContent = 'Loading policies…'; msg.className = 'sd-test-msg'; }
      loadPolicies(false);
    } else {
      const msg = $('nl-policy-msg');
      if (msg) { msg.textContent = 'Connect eBay to load policies'; msg.className = 'sd-test-msg'; }
    }
  }

  function applyListingDefaults() {
    const zip         = $('pg-default-zip')?.value;
    const country     = $('pg-default-country')?.value || 'US';
    const pkgType     = $('pg-default-package-type')?.value || 'PACKAGE_THICK_ENVELOPE';
    const handling    = $('pg-default-handling')?.value || '1';
    const wLbs        = $('pg-default-weight-lbs')?.value || '0';
    const wOz         = $('pg-default-weight-oz')?.value  || '0';
    const len         = $('pg-default-length')?.value || '0';
    const wid         = $('pg-default-width')?.value  || '0';
    const hgt         = $('pg-default-height')?.value || '0';
    const fulfillment = $('pg-default-fulfillment')?.value.trim() || '';
    const bestOffer   = !!$('pg-default-best-offer')?.checked;

    if (zip)  setVal('nl-location-zip', zip);
    setVal('nl-location-country', country);
    setVal('nl-package-type',     pkgType);
    setVal('nl-handling-time',    handling);
    if (parseFloat(wLbs) > 0) setVal('nl-weight-lbs', wLbs);
    if (parseFloat(wOz)  > 0) setVal('nl-weight-oz',  wOz);
    if (parseFloat(len)  > 0) setVal('nl-length', len);
    if (parseFloat(wid)  > 0) setVal('nl-width',  wid);
    if (parseFloat(hgt)  > 0) setVal('nl-height', hgt);
    if (fulfillment) setVal('nl-fulfillment', fulfillment);
    if (bestOffer) {
      const boEl = $('nl-best-offer');
      if (boEl) { boEl.checked = true; nlToggleBestOffer(true); }
    }
  }

  function closeNewListingModal() {
    $('new-listing-overlay')?.classList.add('hidden');
    if (location.hash === '#ai') location.hash = 'dashboard';
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'dashboard'));
  }

  function nlClearAll() {
    nlClearImage();
    nlClearForm();
    nlSetResult('', '');
    // Keep the sold-comps bar visible (it hosts the Connect-to-Terapeak prompt); just
    // reset its data. nlRefreshSoldCompsConnect() re-evaluates the prompt on open.
    $('nl-sold-comps-stats')?.classList.add('hidden');
    if ($('nl-sold-comp-list')) $('nl-sold-comp-list').innerHTML = '';
    if ($('nl-sold-comps-summary')) $('nl-sold-comps-summary').textContent = '';
    if ($('nl-quickfill-input')) $('nl-quickfill-input').value = '';
  }

  function nlClearImage() {
    nlImageBase64 = '';
    nlMimeType = 'image/jpeg';
    const fi = $('nl-file-input');
    if (fi) fi.value = '';
    if ($('nl-preview-img')) $('nl-preview-img').src = '';
    $('nl-drop-zone')?.classList.remove('hidden');
    $('nl-preview-wrap')?.classList.add('hidden');
    $('nl-cutout-wrap')?.classList.add('hidden');
    $('nl-ai-status')?.classList.add('hidden');
    $('nl-ai-done')?.classList.add('hidden');
    $('nl-ai-error')?.classList.add('hidden');
    $('nl-photos-section')?.classList.add('hidden');
    $('nl-imggen-status-bar')?.classList.add('hidden');
    if ($('nl-imggen-status-text')) $('nl-imggen-status-text').textContent = '';
    $('nl-imggen-setup-link')?.classList.add('hidden');
    $('nl-photos-generating')?.classList.add('hidden');
    if ($('nl-photos-grid')) $('nl-photos-grid').innerHTML = '';
    $('nl-photos-error')?.classList.add('hidden');
  }

  function nlClearForm() {
    const sets = [
      ['nl-title', ''], ['nl-subtitle', ''], ['nl-category', ''], ['nl-category-id', ''],
      ['nl-secondary-category-id', ''], ['nl-condition', 'USED_EXCELLENT'], ['nl-condition-desc', ''],
      ['nl-brand', ''], ['nl-mpn', ''], ['nl-upc', ''], ['nl-ean', ''], ['nl-isbn', ''],
      ['nl-description', ''], ['nl-price', ''], ['nl-quantity', '1'], ['nl-qty-limit', ''],
      ['nl-auto-accept', ''], ['nl-auto-decline', ''], ['nl-package-type', 'PACKAGE_THICK_ENVELOPE'],
      ['nl-handling-time', '1'], ['nl-weight-lbs', '0'], ['nl-weight-oz', '0'],
      ['nl-length', ''], ['nl-width', ''], ['nl-height', ''], ['nl-location-zip', ''],
      ['nl-location-country', 'US'], ['nl-format', 'FIXED_PRICE'], ['nl-duration', '7'],
      ['nl-charity-pct', '0'], ['nl-charity-id', ''],
    ];
    sets.forEach(([id, val]) => set(id, val));
    if ($('nl-best-offer')) $('nl-best-offer').checked = false;
    if ($('nl-private')) $('nl-private').checked = false;
    nlToggleBestOffer(false);
    if ($('nl-duration-wrap')) $('nl-duration-wrap').style.display = 'none';
    if ($('nl-specifics-list')) $('nl-specifics-list').innerHTML = '';
    nlResetReadiness();
    nlClearAllPhotoSlots();
    // Reset description to the text tab (default)
    document.querySelectorAll('.desc-tab').forEach(t => t.classList.toggle('active', t.dataset.descTab === 'text'));
    $('nl-desc-edit-wrap')?.classList.add('hidden');
    $('nl-desc-text-wrap')?.classList.remove('hidden');
    $('nl-desc-preview-wrap')?.classList.add('hidden');
    if ($('nl-desc-text')) $('nl-desc-text').value = '';
    if ($('nl-desc-preview')) $('nl-desc-preview').innerHTML = '';
    nlUpdateCharCount('nl-title', 'nl-title-count', 80);
    nlUpdateCharCount('nl-subtitle', 'nl-subtitle-count', 55);
    nlUpdateDescCount();
    if ($('nl-cat-selected')) $('nl-cat-selected').hidden = true;
  }

  function nlLoadFile(file, label = file.name || 'Product photo') {
    // Accept files with no type (some screenshot tools omit MIME); reject known non-images
    const mime = file.type || 'image/png';
    if (mime && !mime.startsWith('image/')) return;
    nlMimeType = mime;
    const reader = new FileReader();
    reader.onload = ev => {
      nlImageBase64 = ev.target.result.split(',')[1];
      $('nl-preview-img').src = ev.target.result;
      $('nl-drop-zone')?.classList.add('hidden');
      $('nl-preview-wrap')?.classList.remove('hidden');
      $('nl-ai-done')?.classList.add('hidden');
      $('nl-ai-error')?.classList.add('hidden');
      addActivity('Photo loaded', label);
      nlAnalyze();
    };
    reader.readAsDataURL(file);
  }

  // ── Category search autocomplete ─────────────────────────────────────────

  function bindCategorySearch(inputId, hiddenId, dropdownId, selectedId, nameId, badgeId, clearId) {
    const input    = $(inputId);
    const hidden   = $(hiddenId);
    const dropdown = $(dropdownId);
    const selected = $(selectedId);
    const nameEl   = $(nameId);
    const badge    = $(badgeId);
    const clearBtn = $(clearId);
    if (!input || !dropdown) return;

    let debounce = null;
    let activeIdx = -1;
    let currentSuggestions = [];

    // The hidden input is written directly rather than typed into, so it never fires `change`
    // on its own. Announcing it lets anything that depends on the category react — the
    // readiness check needs to, since the category is what decides which Item Specifics exist.
    function announce() {
      hidden.dispatchEvent(new Event('change', { bubbles: true }));
    }

    function showSelected(name, id) {
      input.value = '';
      input.placeholder = 'Search to change category…';
      hidden.value  = id;
      if (nameEl) nameEl.textContent = name;
      if (badge)  badge.textContent  = 'ID: ' + id;
      if (selected) selected.hidden = false;
      closeDropdown();
      announce();
    }

    function clearSelection() {
      hidden.value = '';
      input.value = '';
      input.placeholder = 'Type to search eBay categories…';
      if (selected) selected.hidden = true;
      input.focus();
      announce();
    }

    function closeDropdown() {
      dropdown.hidden = true;
      activeIdx = -1;
    }

    function renderDropdown(items, loading = false) {
      dropdown.innerHTML = '';
      if (loading) {
        dropdown.innerHTML = '<li class="cat-loading">Searching eBay categories…</li>';
        dropdown.hidden = false;
        return;
      }
      if (!items.length) {
        dropdown.innerHTML = '<li class="cat-no-results">No categories found — try different keywords</li>';
        dropdown.hidden = false;
        return;
      }
      currentSuggestions = items;
      items.forEach((item, i) => {
        const li = document.createElement('li');
        li.innerHTML = `<span class="cat-id-tag">${esc(item.id)}</span><span class="cat-name">${esc(item.name)}</span><span class="cat-path">${esc(item.breadcrumb)}</span>`;
        li.addEventListener('mousedown', e => {
          e.preventDefault();
          showSelected(item.name, item.id);
        });
        dropdown.appendChild(li);
      });
      dropdown.hidden = false;
      activeIdx = -1;
    }

    async function fetchSuggestions(q) {
      if (q.length < 2) { closeDropdown(); return; }
      renderDropdown([], true);
      try {
        const res  = await fetch('/api/ebay/category-suggestions?q=' + encodeURIComponent(q));
        const data = await res.json();
        if (Array.isArray(data)) renderDropdown(data);
        else closeDropdown();
      } catch { closeDropdown(); }
    }

    // ── Category tree browser ──────────────────────────────────────────────
    const browseBtn  = $(`${inputId.replace('nl-category','nl-cat-browse-btn').replace('f-category','f-cat-browse-btn')}`);
    const browser    = $(`${inputId.replace('nl-category','nl-cat-browser').replace('f-category','f-cat-browser')}`);
    const browserList  = browser ? browser.querySelector('.cat-browser-list')  : null;
    const browserTrail = browser ? browser.querySelector('.cat-browser-trail') : null;
    let   browserStack = []; // [{id, name}] — breadcrumb trail

    function closeBrowser() { if (browser) browser.hidden = true; }

    async function loadBrowserLevel(catId, catName) {
      if (!browser || !browserList) return;
      browserList.innerHTML = '<li class="cat-loading">Loading categories…</li>';
      browser.hidden = false;
      try {
        const res  = await fetch(`/api/ebay/category-children?id=${encodeURIComponent(catId || '0')}`);
        const cats = await res.json();
        browserList.innerHTML = '';
        // Back button
        if (browserStack.length > 0) {
          const back = document.createElement('li');
          back.className = 'cat-browser-back';
          back.textContent = '← Back';
          back.addEventListener('mousedown', e => {
            e.preventDefault();
            browserStack.pop();
            const prev = browserStack.length > 0 ? browserStack[browserStack.length - 1] : { id: '0', name: '' };
            updateTrail();
            loadBrowserLevel(prev.id, prev.name);
          });
          browserList.appendChild(back);
        }
        cats.forEach(cat => {
          const li = document.createElement('li');
          const isLeaf = cat.breadcrumb === 'leaf';
          li.innerHTML = `<span class="cat-name">${esc(cat.name)}</span><span class="cat-id-tag">${esc(cat.id)}</span>${isLeaf ? '' : '<span class="cat-arrow">›</span>'}`;
          li.addEventListener('mousedown', e => {
            e.preventDefault();
            if (isLeaf) {
              showSelected(cat.name, cat.id);
              closeBrowser();
              browserStack = [];
              updateTrail();
            } else {
              browserStack.push({ id: cat.id, name: cat.name });
              updateTrail();
              loadBrowserLevel(cat.id, cat.name);
            }
          });
          browserList.appendChild(li);
        });
      } catch { browserList.innerHTML = '<li class="cat-no-results">Failed to load — try searching instead</li>'; }
    }

    function updateTrail() {
      if (!browserTrail) return;
      browserTrail.innerHTML = ['All Categories', ...browserStack.map(s => s.name)]
        .map((n, i, arr) => i < arr.length - 1 ? `<span class="trail-crumb">${esc(n)}</span> ›` : `<strong>${esc(n)}</strong>`)
        .join(' ');
    }

    if (browseBtn) {
      browseBtn.addEventListener('click', e => {
        e.preventDefault();
        if (browser && !browser.hidden) { closeBrowser(); return; }
        browserStack = [];
        updateTrail();
        loadBrowserLevel('0', '');
        closeDropdown();
      });
    }

    input.addEventListener('input', () => {
      clearTimeout(debounce);
      closeBrowser();
      const q = input.value.trim();
      if (!q) { closeDropdown(); return; }
      debounce = setTimeout(() => fetchSuggestions(q), 280);
    });

    input.addEventListener('keydown', e => {
      const items = dropdown.querySelectorAll('li:not(.cat-loading):not(.cat-no-results)');
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        activeIdx = Math.min(activeIdx + 1, items.length - 1);
        items.forEach((li, i) => li.classList.toggle('active', i === activeIdx));
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        activeIdx = Math.max(activeIdx - 1, 0);
        items.forEach((li, i) => li.classList.toggle('active', i === activeIdx));
      } else if (e.key === 'Enter' && activeIdx >= 0) {
        e.preventDefault();
        if (currentSuggestions[activeIdx]) {
          const s = currentSuggestions[activeIdx];
          showSelected(s.name, s.id);
        }
      } else if (e.key === 'Escape') {
        closeDropdown();
      }
    });

    input.addEventListener('blur', () => setTimeout(closeDropdown, 150));
    if (browseBtn) browseBtn.addEventListener('blur', () => setTimeout(closeBrowser, 200));

    if (clearBtn) clearBtn.addEventListener('click', clearSelection);
  }

  // ── When AI fills form — sync category display ─────────────────────────

  function nlSyncCategoryDisplay() {
    const id   = $('nl-category-id')?.value || '';
    const name = $('nl-category')?.value || '';
    const selected = $('nl-cat-selected');
    const nameEl   = $('nl-cat-selected-name');
    const badge    = $('nl-cat-id-badge');
    if (!id) { if (selected) selected.hidden = true; return; }
    if (nameEl) nameEl.textContent = name || 'Category ' + id;
    if (badge)  badge.textContent  = 'ID: ' + id;
    if (selected) {
      selected.hidden = false;
      // Clear the search input so it shows the selected bar
      const inp = $('nl-category');
      if (inp) { inp.value = ''; inp.placeholder = 'Search to change category…'; }
    }
  }

  async function nlBulkImport() {
    const input = $('nl-bulk-url-input');
    const btn   = $('nl-bulk-go');
    const url   = input?.value.trim();
    if (!url || !url.startsWith('http')) return;

    if (btn) { btn.disabled = true; btn.textContent = 'Clearing old drafts…'; }

    // Always wipe old drafts and tabs before importing a new collection
    await clearAllSavedDrafts();
    draftTabs = [];
    activeDraftTabId = null;
    const blankTab = newDraftTab();
    activeDraftTabId = blankTab.id;
    nlClearAll();
    renderDraftTabs();

    if (btn) { btn.textContent = 'Scanning…'; }

    try {
      const res2 = await fetch('/api/bulk-import/extract-links', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url })
      });
      if (!res2.ok) throw new Error('Could not extract product links from that page');
      const { links } = await res2.json();
      if (!links?.length) throw new Error('No product links found on that page');

      addActivity(`Bulk import started`, `Found ${links.length} products on page`);
      if (btn) btn.textContent = `Importing 0 / ${links.length}…`;

      let done = 0;
      for (const productUrl of links) {
        try {
          const r = await fetch('/api/analyze-url', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ url: productUrl })
          });
          if (!r.ok) continue;
          const data = await r.json();

          // Image code — same as single-URL browser
          let imageUrls = data.imageUrls || [];
          const firstImgUrl = imageUrls.find(u => u && (u.startsWith('http') || u.startsWith('/')));
          if (firstImgUrl && firstImgUrl.startsWith('http')) {
            try {
              const fr = await fetch('/api/photos/fetch-url', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ url: firstImgUrl })
              });
              if (fr.ok) {
                const { url: localUrl } = await fr.json();
                imageUrls = [window.location.origin + localUrl, ...imageUrls.filter(u => u !== firstImgUrl)];
              }
            } catch { /* non-fatal */ }
          } else if (firstImgUrl && firstImgUrl.startsWith('/')) {
            imageUrls = [window.location.origin + firstImgUrl, ...imageUrls.filter(u => u !== firstImgUrl)];
          }

          // Save as draft
          const draft = {
            title: data.title || productUrl,
            data: { ...data, imageUrls, listingFormat: 'FIXED_PRICE', durationDays: 30,
                    fulfillmentPolicyId: '236920894018', bestOfferEnabled: true,
                    itemLocationCountry: data.itemLocationCountry || 'CN',
                    weightLbs: data.weightLbs || 32 },
            visualDescription: data.visualDescription || ''
          };
          await fetch('/api/local-drafts/save', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(draft)
          });
          done++;
          if (btn) btn.textContent = `Importing ${done} / ${links.length}…`;
          addActivity('Draft saved', data.title || productUrl);
        } catch { /* skip failed product */ }
      }

      addActivity(`Bulk import complete`, `${done} of ${links.length} saved as drafts — opening all as tabs`);
      await loadAllDraftsAsTabs();
    } catch (e) {
      alert('Bulk import failed: ' + e.message);
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'Import All'; }
    }
  }

  async function nlAiModify() {
    const input = $('nl-ai-modify-input');
    const btn   = $('nl-ai-modify-go');
    const instruction = input?.value.trim();
    if (!instruction) return;

    const payload = buildNlPayload();
    if (!payload.title && !payload.description) {
      alert('Fill in a listing first, then ask Claude to modify it.');
      return;
    }

    if (btn) { btn.disabled = true; btn.classList.add('loading'); btn.textContent = 'Applying…'; }
    if (input) input.classList.add('loading');

    try {
      const res = await guardedFetch('/api/ai-modify', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...payload, instruction })
      });
      if (!res.ok) {
        const b = await res.json().catch(() => ({}));
        throw new Error(b.error || 'Modification failed');
      }
      const data = await res.json();
      fillNlForm(data);
      if (input) { input.value = ''; input.placeholder = '✓ Done — ask Claude for another change'; }
      addActivity('AI modification applied', instruction);
    } catch (e) {
      alert('Claude modify failed: ' + e.message);
    } finally {
      if (btn) { btn.disabled = false; btn.classList.remove('loading'); btn.textContent = 'Apply'; }
      if (input) input.classList.remove('loading');
    }
  }

  async function nlAnalyzeUrl() {
    const input = $('nl-url-input');
    const btn   = $('nl-url-go');
    const url   = input?.value.trim();
    if (!url) return;

    // Validate it looks like a URL
    if (!url.startsWith('http://') && !url.startsWith('https://')) {
      input.value = 'https://' + url;
    }

    input?.classList.add('loading');
    if (btn) { btn.disabled = true; btn.textContent = 'Analyzing…'; }
    $('nl-ai-status')?.classList.remove('hidden');
    $('nl-ai-done')?.classList.add('hidden');
    $('nl-ai-error')?.classList.add('hidden');
    if ($('nl-ai-msg')) $('nl-ai-msg').textContent = 'Reading page and analyzing with AI…';

    try {
      const res = await guardedFetch('/api/analyze-url', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url: input.value.trim() })
      });
      if (!res.ok) {
        const b = await res.json().catch(() => ({}));
        throw new Error(b.error || 'URL analysis failed');
      }
      const data = await res.json();

      nlClearAllPhotoSlots();
      fillNlForm(data);
      window._nlVisualDescription = data.visualDescription || '';
      window._nlImageType = 'webpage_screenshot';
      if (data.title) nlLoadSoldComps(data.title);

      // Use only the first product image — show in preview and remove background
      const firstImgUrl = (data.imageUrls || []).find(u => u && (u.startsWith('http') || u.startsWith('/')));
      if (firstImgUrl) {
        const absUrl = firstImgUrl.startsWith('/') ? window.location.origin + firstImgUrl : firstImgUrl;
        // Show in main product photo preview
        const previewImg = $('nl-preview-img');
        if (previewImg) previewImg.src = absUrl;
        $('nl-drop-zone')?.classList.add('hidden');
        $('nl-preview-wrap')?.classList.remove('hidden');
        // Also kick off background removal → Picture 1
        nlAutoRemoveBg(absUrl);
      }

      const activeTab = draftTabs.find(t => t.id === activeDraftTabId);
      if (activeTab) { activeTab.title = data.title || 'New Draft'; renderDraftTabs(); }

      $('nl-ai-status')?.classList.add('hidden');
      $('nl-ai-done')?.classList.remove('hidden');
      addActivity('URL analyzed', data.title || input.value);
    } catch (err) {
      $('nl-ai-status')?.classList.add('hidden');
      $('nl-ai-error')?.classList.remove('hidden');
      if ($('nl-ai-error-msg')) $('nl-ai-error-msg').textContent = `URL analysis failed: ${err.message}`;
      addActivity('URL analysis failed', err.message);
    } finally {
      input?.classList.remove('loading');
      if (btn) { btn.disabled = false; btn.textContent = 'Analyze'; }
    }
  }

  // Show the "Connect to Terapeak" prompt in the sold-comps bar unless a session is
  // already connected. Runs when the AI Listing view opens.
  async function nlRefreshSoldCompsConnect() {
    const connect = $('nl-sold-comps-connect');
    if (!connect) return;
    try {
      const s = await fetch('/api/terapeak/status').then(r => r.json());
      connect.classList.toggle('hidden', !!s.connected);
    } catch { /* leave the prompt visible if status can't be checked */ }
  }

  // Kick off the Terapeak browser login from the bar's Connect button, then poll until
  // the login window closes and hide the prompt once connected.
  async function nlSoldCompsConnect() {
    const btn = $('nl-sold-comps-connect-btn');
    const msg = $('nl-sold-comps-connect-msg');
    if (!btn) return;
    btn.disabled = true;
    if (msg) msg.textContent = 'Opening the eBay login window…';
    try {
      const data = await fetch('/api/terapeak/connect', { method: 'POST' }).then(r => r.json());
      if (msg) msg.textContent = data.message || 'Log in to eBay in the window that opened (Alt+Tab if you don\'t see it), then come back here.';
      const poll = setInterval(async () => {
        const s = await fetch('/api/terapeak/status').then(r => r.json()).catch(() => null);
        if (s && !s.loginInProgress) {
          clearInterval(poll);
          btn.disabled = false;
          if (s.connected) {
            $('nl-sold-comps-connect')?.classList.add('hidden');
            if (msg) msg.textContent = '';
            loadTerapeakStatus();
          } else if (msg) {
            msg.textContent = s.lastError || 'Login was not completed. Try again.';
          }
        }
      }, 3000);
    } catch (err) {
      btn.disabled = false;
      if (msg) msg.textContent = `Connect failed: ${err.message}`;
    }
  }

  async function nlLoadSoldComps(itemName) {
    const strip    = $('nl-sold-comps-strip');
    const summary  = $('nl-sold-comps-summary');
    const link     = $('nl-sold-comps-link');
    const terapeak = $('nl-sold-comps-terapeak');
    const stats    = $('nl-sold-comps-stats');
    const list     = $('nl-sold-comp-list');
    if (!strip || !summary || !link || !terapeak || !stats || !list) return;

    strip.classList.remove('hidden');
    strip.classList.add('loading');
    summary.textContent = '';
    stats.classList.add('hidden');
    list.innerHTML = '';
    link.classList.add('hidden');
    terapeak.classList.add('hidden');

    try {
      // Same costing ride-along as the Market Research panel — see runSoldResearch.
      const params = new URLSearchParams({ q: itemName });
      const nlCost = thNum('nl-unit-cost');
      const nlAsk  = thNum('nl-price');
      if (nlCost > 0) params.set('cost', String(nlCost));
      if (nlAsk  > 0) params.set('ask',  String(nlAsk));
      const nlBuyerShipping = thNum('nl-buyer-shipping');
      if (nlBuyerShipping > 0) params.set('buyerShipping', String(nlBuyerShipping));

      const res  = await fetch(`/api/sold-comps?${params.toString()}`);
      const data = await res.json().catch(() => ({}));
      strip.classList.remove('loading');

      if (data.fallbackUrl) { link.href = data.fallbackUrl; link.classList.remove('hidden'); }
      if (data.terapeakUrl) { terapeak.href = data.terapeakUrl; terapeak.classList.remove('hidden'); }

      if (!res.ok || !data.count) {
        summary.textContent = ''; // links alone are enough — no need to announce the absence of data
        return;
      }

      $('nl-sold-comps-connect')?.classList.add('hidden');   // real data — drop the connect prompt
      // Four gross stats and no net is how a seller talks themselves into the median. Say what the
      // median is actually worth, right where they read it.
      const atMedian = data.pricing?.quotes?.median;
      summary.textContent = atMedian
        ? `Recently sold — "${itemName}" · list at the ${moneyExact(data.median)} median and you `
          + (atMedian.hasCostBasis
              ? `keep ${moneyExact(atMedian.netProfit)} after fees`
                + (data.pricing.minimumOfferPrice ? `, floor ${moneyExact(data.pricing.minimumOfferPrice)}` : '')
              : `bank ${moneyExact(atMedian.netProceeds)} after fees`)
        : `Recently sold — "${itemName}"`;
      $('nl-sold-comps-stat-avg').textContent    = `$${data.average.toFixed(2)}`;
      $('nl-sold-comps-stat-median').textContent = `$${data.median.toFixed(2)}`;
      $('nl-sold-comps-stat-min').textContent     = `$${data.min.toFixed(2)}`;
      $('nl-sold-comps-stat-max').textContent     = `$${data.max.toFixed(2)}`;
      $('nl-sold-comps-stat-count').textContent   = data.count;
      stats.classList.remove('hidden');

      // Show the 7 most recent sold items, newest first, centered in the bar
      const sorted = [...data.items].sort((a, b) => new Date(b.soldDate) - new Date(a.soldDate)).slice(0, 7);
      list.innerHTML = sorted.map(it => `
        <a class="nl-sold-comp-row" href="${it.url}" target="_blank" rel="noopener">
          ${it.imageUrl
            ? `<img class="nl-sold-comp-thumb" src="${it.imageUrl}" alt="" loading="lazy" />`
            : `<span class="nl-sold-comp-thumb nl-sold-comp-thumb-empty">📦</span>`}
          <span class="nl-sold-comp-title">${esc(it.title)}</span>
          <span class="nl-sold-comp-date">${new Date(it.soldDate).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}</span>
          <span class="nl-sold-comp-price">$${it.price.toFixed(2)}</span>
        </a>`).join('');
    } catch (err) {
      strip.classList.remove('loading');
      summary.textContent = `Sold-comp lookup failed: ${err.message}`;
    }
  }

  async function nlQuickFillByName() {
    const input    = $('nl-quickfill-input');
    const btn      = $('nl-quickfill-go');
    const itemName = input?.value.trim();
    if (!itemName) return;

    input?.classList.add('loading');
    if (btn) { btn.disabled = true; btn.textContent = 'Researching…'; }
    $('nl-ai-status')?.classList.remove('hidden');
    $('nl-ai-done')?.classList.add('hidden');
    $('nl-ai-error')?.classList.add('hidden');
    if ($('nl-ai-msg')) $('nl-ai-msg').textContent = 'Researching the product and finding photos online…';

    nlLoadSoldComps(itemName); // runs in parallel — independent of the listing fill below

    hideFailure('nl-failure');
    try {
      const { ok, data, failure } = await callApi('/api/quick-fill', {
        method: 'POST', body: { itemName }, timeoutMs: AI_TIMEOUT_MS,
      });

      if (!ok) {
        $('nl-ai-status')?.classList.add('hidden');
        // The typed name is left in the box on purpose, so Try again costs nothing.
        renderFailure('nl-failure', failure, { onRetry: () => nlQuickFillByName() });
        addActivity('Quick-fill failed', failure?.headline || 'Unknown error');
        return;
      }

      nlClearAllPhotoSlots();
      fillNlForm(data);
      scheduleAutosave();

      // Quick-fill searches the web for a product photo and often finds none. That used to be a log
      // line the seller never saw, leaving them a complete listing with an empty photo grid.
      if (!(data.imageUrls || []).some(Boolean)) {
        nlPhotoNotice('No product photo could be found online for this item. Add one before publishing — '
                    + 'a listing with no picture rarely sells.');
      }
      window._nlVisualDescription = data.visualDescription || '';
      window._nlImageType = 'product_photo';

      // Condition-aware: NEW items may use a found stock photo; USED items must show the seller's
      // REAL unit (representative-photo library or a prompt) — never a stock image.
      const foundUrls = (data.imageUrls || []).filter(u => u && (u.startsWith('http') || u.startsWith('/')));
      const absUrls = foundUrls.map(u => u.startsWith('/') ? window.location.origin + u : u);
      await nlApplyResearchPhotos(data, absUrls, itemName);

      const activeTab = draftTabs.find(t => t.id === activeDraftTabId);
      if (activeTab) { activeTab.title = data.title || 'New Draft'; renderDraftTabs(); }

      $('nl-ai-status')?.classList.add('hidden');
      $('nl-ai-done')?.classList.remove('hidden');
      addActivity('Quick-fill complete', data.title || itemName);
      if (input) { input.value = ''; }
    } catch (err) {
      $('nl-ai-status')?.classList.add('hidden');
      renderFailure('nl-failure', {
        kind: 'Unknown',
        headline: 'The app hit an unexpected error after quick-fill',
        whatHappened: 'The listing may be partly filled in; check the fields before publishing.',
        whatToDo: 'Try again, or fill in what is missing by hand.',
        retryable: true, workPreserved: true, technical: String(err?.message || err),
      }, { onRetry: () => nlQuickFillByName() });
      addActivity('Quick-fill failed', String(err?.message || err));
    } finally {
      input?.classList.remove('loading');
      if (btn) { btn.disabled = false; btn.textContent = 'Auto-Fill'; }
    }
  }

  // Condition-aware photo application for AI research results. NEW items may use a found stock
  // photo; USED items must show the seller's REAL unit — so pull from the representative-photo
  // library for the model, or prompt the seller to add a real photo. Never a stock image on used.
  async function nlApplyResearchPhotos(data, absUrls, nameHint) {
    if (nlIsUsedListing(data, nameHint)) {
      // No library set for this model yet — refuse the stock image, prompt for a real photo.
      const applied = await nlApplyUsedLibraryPhotos(data, nameHint);
      if (!applied) nlPromptUsedPhoto();
      return;
    }

    // NEW item — a found stock photo is acceptable (existing behavior).
    if (absUrls[0]) {
      const previewImg = $('nl-preview-img');
      if (previewImg) previewImg.src = absUrls[0];
      $('nl-drop-zone')?.classList.add('hidden');
      $('nl-preview-wrap')?.classList.remove('hidden');
      nlAutoRemoveBg(absUrls[0]);
      absUrls.slice(1).forEach((u, i) => setPhotoSlotUrl(i + 1, u));
    } else {
      addActivity('No photos found online', 'Drop or paste a photo manually, or try a more specific item name');
    }
  }

  // A listing is treated as USED — stock photos not allowed — when the AI (or the form) says so,
  // or when the item name itself says "used".
  function nlIsUsedListing(data, nameHint) {
    const cond = (data.condition || $('nl-condition')?.value || '').toUpperCase();
    return cond.startsWith('USED') || cond === 'FOR_PARTS' || /\bused\b/i.test(nameHint || data.title || '');
  }

  // Pull the seller's representative photos for this model out of the photo library. Returns true
  // when real photos were applied (preview + slots + disclosure), false when no library exists yet.
  async function nlApplyUsedLibraryPhotos(data, nameHint) {
    try {
      const q = new URLSearchParams({ model: data.model || '', title: data.title || nameHint || '' });
      const lib = await fetch('/api/photos/library/for-listing?' + q).then(r => r.json()).catch(() => ({}));
      if (lib && lib.matched && (lib.photos || []).length) {
        const urls = lib.photos.map(u => u.startsWith('/') ? window.location.origin + u : u);
        const previewImg = $('nl-preview-img');
        if (previewImg) previewImg.src = urls[0];
        $('nl-drop-zone')?.classList.add('hidden');
        $('nl-preview-wrap')?.classList.remove('hidden');
        urls.forEach((u, i) => setPhotoSlotUrl(i, u));
        if (lib.disclosure) nlAppendDisclosure(lib.disclosure);
        addActivity('Used item — pulled your library photos', `${urls.length} real photo(s) for ${lib.modelKey}`);
        return true;
      }
    } catch (e) { /* non-fatal */ }
    return false;
  }

  // Used item with no library photos: clear any stock image, reveal + highlight the drop zone so
  // the seller adds a real photo of their actual unit.
  function nlPromptUsedPhoto() {
    $('nl-preview-wrap')?.classList.add('hidden');
    const drop = $('nl-drop-zone');
    if (drop) {
      drop.classList.remove('hidden');
      drop.classList.add('field-flagged');
      setTimeout(() => drop.classList.remove('field-flagged'), 5000);
    }
    addActivity('Used item — add a real photo',
      "Stock photos aren't allowed for used items. Drop a photo of your actual unit (or add it to this model's photo library).");
  }

  // Append the representative-photo disclosure to the plain-text description once.
  function nlAppendDisclosure(text) {
    const t = $('nl-desc-text');
    if (!t || !text || t.value.includes(text)) return;
    t.value = (t.value ? t.value + '\n\n' : '') + text;
    if (typeof nlUpdateDescCount === 'function') nlUpdateDescCount();
  }

  async function nlAnalyze() {
    if (!nlImageBase64) return;

    $('nl-ai-status')?.classList.remove('hidden');
    $('nl-ai-done')?.classList.add('hidden');
    $('nl-ai-error')?.classList.add('hidden');
    hideFailure('nl-failure');
    if ($('nl-ai-msg')) $('nl-ai-msg').textContent = 'Analyzing with AI…';

    try {
      const { ok, failure, data } = await callApi('/api/analyze', {
        method: 'POST',
        body: { imageBase64: nlImageBase64, mimeType: nlMimeType },
        timeoutMs: AI_TIMEOUT_MS,
      });

      if (!ok) {
        // The uploaded photo is still held in nlImageBase64, so Try again re-runs the analysis
        // without asking the seller to find and drop the file a second time.
        $('nl-ai-status')?.classList.add('hidden');
        renderFailure('nl-failure', failure, { onRetry: () => nlAnalyze() });
        addActivity('AI analysis failed', failure?.headline || 'Unknown error');
        return;
      }

      fillNlForm(data);
      scheduleAutosave();
      window._nlVisualDescription = data.visualDescription || '';
      window._nlImageType = data.imageType || 'webpage_screenshot';
      if (data.title) nlLoadSoldComps(data.title);

      // Reset photo slots before populating from analysis result
      nlClearAllPhotoSlots();

      const isProductPhoto = (data.imageType || '') === 'product_photo';
      // Condition-aware, same gate as the Auto-Fill path: images scraped off a screenshot are
      // stock/online photos. NEW items may use one; USED items must show the seller's REAL unit,
      // so pull from the photo library or prompt for a real photo. An uploaded product photo IS
      // the seller's own unit, so it stays allowed either way.
      const usedNeedsRealPhoto = !isProductPhoto && nlIsUsedListing(data);
      let firstPhotoUrl = null;

      // Every branch below used to swallow its failure as `catch { /* non-fatal */ }`. It is not
      // non-fatal: the analysis carries on, the photo grid stays empty, and the seller finds out when
      // a photoless listing fails to sell. The analysis really has succeeded, so these are notices
      // rather than failure panels — but they are shown, and they do not fade away.
      if (isProductPhoto && nlImageBase64) {
        // Clean product photo — save the uploaded image directly
        const saved = await callApi('/api/photos/save-uploaded', {
          method: 'POST',
          body: { imageBase64: nlImageBase64, mimeType: nlMimeType || 'image/jpeg' },
        });
        if (saved.ok && saved.data?.url) { firstPhotoUrl = saved.data.url; nlAddPhotoRow(saved.data.url); }
        else nlPhotoNotice('Your photo could not be saved — add it again before publishing. '
                         + (saved.failure?.whatToDo || ''));
      } else if (usedNeedsRealPhoto) {
        const applied = await nlApplyUsedLibraryPhotos(data);
        if (!applied) nlPromptUsedPhoto();
      } else if ((data.imageUrls || []).length > 0) {
        // Only use the first product image — one clean photo is all we need
        const firstUrl = (data.imageUrls || []).find(u => u && (u.startsWith('http') || u.startsWith('/')));
        if (firstUrl) {
          if (firstUrl.startsWith('/')) {
            firstPhotoUrl = window.location.origin + firstUrl;
          } else {
            const fetched = await callApi('/api/photos/fetch-url', { method: 'POST', body: { url: firstUrl } });
            if (fetched.ok && fetched.data?.url) firstPhotoUrl = window.location.origin + fetched.data.url;
            else nlPhotoNotice('The product photo from that page could not be downloaded — add a photo '
                             + 'before publishing. ' + (fetched.failure?.whatToDo || ''));
          }
        }
      } else {
        nlPhotoNotice('No photo came back with this analysis. Add at least one before publishing — '
                    + 'listings without pictures rarely sell.');
      }

      // Update the active tab title
      const activeTab = draftTabs.find(t => t.id === activeDraftTabId);
      if (activeTab) { activeTab.title = data.title || 'New Draft'; activeTab.saved = false; renderDraftTabs(); }
      $('nl-ai-status')?.classList.add('hidden');
      $('nl-ai-done')?.classList.remove('hidden');
      addActivity('AI analysis complete', data.title || 'Form filled — review before publishing.');

      // Auto remove background — use first fetched photo, or fall back to the uploaded image
      // directly. Skipped on the used-item path: library photos are already curated, and the
      // uploaded screenshot must never end up as the listing photo.
      if (!usedNeedsRealPhoto) {
        if (firstPhotoUrl) nlAutoRemoveBg(firstPhotoUrl);
        else if (nlImageBase64) nlAutoRemoveBgFromBase64(nlImageBase64, nlMimeType || 'image/jpeg');
      }
    } catch (err) {
      // Reached only on a bug in the code above — callApi itself never throws.
      $('nl-ai-status')?.classList.add('hidden');
      renderFailure('nl-failure', {
        kind: 'Unknown',
        headline: 'The app hit an unexpected error after the analysis',
        whatHappened: 'The listing may be partly filled in; check the fields before publishing.',
        whatToDo: 'Try the analysis again, or fill in what is missing by hand.',
        retryable: true,
        workPreserved: true,
        technical: String(err?.message || err),
      }, { onRetry: () => nlAnalyze() });
      addActivity('AI analysis failed', String(err?.message || err));
    }
  }

  // A photo problem that does not stop the listing but must not be missed either. Deliberately does
  // not auto-hide: a notice that disappears after four seconds is how a listing reaches eBay with no
  // picture and nobody remembers being told.
  function nlPhotoNotice(message, tone = 'warn') {
    const el = $('nl-photo-upload-status');
    if (!el) return;
    el.classList.remove('hidden');
    el.className = 'nl-photo-upload-status ' + tone;
    el.textContent = message;
  }

  async function nlGeneratePhotos(title, description, visualDescription = '', imageType = '') {
    let mode;
    try {
      const modeRes = await fetch('/api/image-gen/mode').then(r => r.json());
      mode = modeRes.mode || 'disabled';
    } catch { return; }

    const section   = $('nl-photos-section');
    const grid      = $('nl-photos-grid');
    const spinner   = $('nl-photos-generating');
    const errorEl   = $('nl-photos-error');
    const errorMsg  = $('nl-photos-error-msg');
    const statusBar = $('nl-imggen-status-bar');
    const statusTxt = $('nl-imggen-status-text');
    const setupLink = $('nl-imggen-setup-link');

    if (!section || !grid) return;

    const setStatus = (text, showSetup = false) => {
      if (statusBar) statusBar.classList.remove('hidden');
      if (statusTxt) statusTxt.textContent = text;
      if (setupLink) setupLink.classList.toggle('hidden', !showSetup);
    };

    if (mode === 'disabled') {
      section.classList.remove('hidden');
      setStatus('Image generation is disabled — enable it in Settings → Image Generation', true);
      return;
    }

    section.classList.remove('hidden');
    spinner?.classList.add('hidden');
    grid.innerHTML = '';
    errorEl?.classList.add('hidden');

    // For local SD, detect server first so we show a clear "not running" status
    if (mode === 'local_sd') {
      setStatus('Checking for local image server...');
      try {
        const detect = await fetch('/api/image-gen/detect').then(r => r.json());
        if (!detect.a1111Online && !detect.comfyOnline) {
          // Neither default port; also test configured endpoint in case it's a custom port
          const test = await fetch('/api/image-gen/test').then(r => r.json());
          if (!test.online) {
            setStatus('Local image server not detected — start your server, then click Regenerate', true);
            addActivity('Image server not detected', 'Start AUTOMATIC1111 or ComfyUI, then click Regenerate.');
            return;
          }
        }
      } catch { /* detection failed — attempt generation anyway */ }
      setStatus('Generating images locally...');
    } else {
      setStatus('Generating images with DALL-E...');
    }

    spinner?.classList.remove('hidden');


    try {
      const isProductPhoto = (imageType || window._nlImageType || '') === 'product_photo';
      const reqBody = { title, description, visualDescription, imageType: imageType || window._nlImageType || '' };
      if (isProductPhoto && nlImageBase64) {
        reqBody.imageBase64 = nlImageBase64;
        reqBody.mimeType    = nlMimeType || 'image/jpeg';
      }
      const res = await fetch('/api/generate-photos', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(reqBody)
      });
      const body = await res.json();
      if (!res.ok) throw new Error(body.error || JSON.stringify(body));

      const labels = ['Front View', 'Angle View', 'Detail View'];
      (body.urls || []).forEach((url, i) => {
        const wrap = document.createElement('div');
        wrap.className = 'nl-photo-thumb-wrap';
        const label = labels[i] || ('Photo ' + (i + 1));
        wrap.title = 'Click to add ' + label + ' to listing';
        const img = document.createElement('img');
        img.src = esc(url); img.alt = 'AI generated ' + esc(label); img.loading = 'lazy';
        img.addEventListener('click', e => { e.stopPropagation(); showLightbox(url); });
        wrap.appendChild(img);
        wrap.insertAdjacentHTML('beforeend',
          '<div class="nl-photo-thumb-badge">✓</div>'
          + '<div class="nl-photo-thumb-label">' + esc(label) + '</div>');

        wrap.addEventListener('click', () => {
          const alreadyAdded = nlCollectPhotoUrls().includes(url);
          if (!alreadyAdded) {
            nlAddPhotoRow(url);
            wrap.classList.add('selected');
          } else {
            // Remove this URL from whichever slot holds it
            const grid = $('nl-photo-grid');
            if (grid) {
              grid.querySelectorAll('.nl-photo-slot.has-image').forEach(slot => {
                if (slot.dataset.url === url) clearPhotoSlot(parseInt(slot.dataset.slotIndex));
              });
            }
            wrap.classList.remove('selected');
          }
        });

        nlAddPhotoRow(url);
        wrap.classList.add('selected');
        grid.appendChild(wrap);
      });

      const count = (body.urls || []).length;
      setStatus(count + ' image' + (count === 1 ? '' : 's') + ' generated');
      addActivity('AI photos generated', count + ' product photos added to listing.');
    } catch (err) {
      spinner?.classList.add('hidden');
      setStatus('Image generation failed — listing can still be submitted');
      errorEl?.classList.remove('hidden');
      if (errorMsg) errorMsg.textContent = err.message;
      addActivity('AI photo generation failed', err.message);
      return;
    }

    spinner?.classList.add('hidden');
  }

  function fillNlForm(d) {
    set('nl-title', d.title || '');
    set('nl-subtitle', d.subtitle || '');
    set('nl-category', d.category || '');
    set('nl-category-id', d.categoryId || '');
    nlSyncCategoryDisplay();
    set('nl-secondary-category-id', d.secondaryCategoryId || '');
    set('nl-condition', d.condition || 'USED_EXCELLENT');
    set('nl-condition-desc', d.conditionDescription || '');
    set('nl-brand', d.brand || '');
    set('nl-mpn', d.mpn || '');
    set('nl-upc', d.upc || '');
    set('nl-ean', d.ean || '');
    set('nl-isbn', d.isbn || '');
    set('nl-description', d.description || '');
    if ($('nl-desc-text')) $('nl-desc-text').value = nlHtmlToText(d.description || ''); // keep the (now-default) text tab in sync
    set('nl-price', d.price || '');
    set('nl-quantity', d.quantity || 1);
    set('nl-qty-limit', d.quantityLimitPerBuyer || '');
    set('nl-package-type', d.packageType || 'PACKAGE_THICK_ENVELOPE');
    set('nl-weight-lbs', d.weightLbs || 0);
    set('nl-weight-oz', d.weightOz || 0);
    set('nl-length', d.packageLengthIn || '');
    set('nl-width', d.packageWidthIn || '');
    set('nl-height', d.packageHeightIn || '');
    set('nl-handling-time', d.handlingTimeBusinessDays || 1);
    if (d.itemLocationPostalCode) set('nl-location-zip', d.itemLocationPostalCode);
    set('nl-location-country', d.itemLocationCountry || 'US');
    set('nl-charity-pct', d.charityDonationPercentage || 0);
    set('nl-charity-id', d.charityId || '');

    const bestOffer = !!d.bestOfferEnabled;
    if ($('nl-best-offer')) $('nl-best-offer').checked = bestOffer;
    nlToggleBestOffer(bestOffer);
    if (bestOffer) {
      set('nl-auto-accept', '');
      set('nl-auto-decline', '');
    }
    if ($('nl-private')) $('nl-private').checked = !!d.privateListing;

    if ($('nl-specifics-list')) $('nl-specifics-list').innerHTML = '';
    if (d.itemSpecifics) Object.entries(d.itemSpecifics).forEach(([k, v]) => nlAddSpecificRow(k, v));

    nlClearAllPhotoSlots();
    // Only use the first product image — skip webpage screenshots (saved-photo paths)
    const firstImg = (d.imageUrls || []).find(u => u && (u.startsWith('http') || u.startsWith('/')));
    if (firstImg) {
      const abs = firstImg.startsWith('/') ? window.location.origin + firstImg : firstImg;
      nlAutoRemoveBg(abs);
    }

    nlUpdateCharCount('nl-title', 'nl-title-count', 80);
    nlUpdateCharCount('nl-subtitle', 'nl-subtitle-count', 55);
    nlSyncDescPreview();
    nlUpdateDescCount();

    // The listing has just been written by AI — this is the moment to say what eBay still needs,
    // while the seller is looking at it, rather than after a failed publish.
    nlRdOverride = false;
    nlRunReadiness(true);
    scheduleTakeHome('nl');   // and what the AI's suggested price is actually worth
  }

  function buildNlPayload() {
    return {
      title: $('nl-title')?.value || '',
      subtitle: $('nl-subtitle')?.value || '',
      category: $('nl-category')?.value || '',
      categoryId: $('nl-category-id')?.value || '',
      secondaryCategoryId: $('nl-secondary-category-id')?.value || '',
      condition: $('nl-condition')?.value || 'USED_EXCELLENT',
      conditionDescription: $('nl-condition-desc')?.value || '',
      brand: $('nl-brand')?.value || '',
      mpn: $('nl-mpn')?.value || '',
      upc: $('nl-upc')?.value || '',
      ean: $('nl-ean')?.value || '',
      isbn: $('nl-isbn')?.value || '',
      description: $('nl-description')?.value || '',
      price: parseFloat($('nl-price')?.value) || 0,
      quantity: parseInt($('nl-quantity')?.value, 10) || 1,
      quantityLimitPerBuyer: parseInt($('nl-qty-limit')?.value, 10) || null,
      bestOfferEnabled: $('nl-best-offer')?.checked || false,
      autoAcceptPrice: $('nl-best-offer')?.checked ? parseFloat($('nl-auto-accept')?.value) || null : null,
      autoDeclinePrice: $('nl-best-offer')?.checked ? parseFloat($('nl-auto-decline')?.value) || null : null,
      packageType: $('nl-package-type')?.value || 'PACKAGE_THICK_ENVELOPE',
      weightLbs: parseFloat($('nl-weight-lbs')?.value) || 0,
      weightOz: parseFloat($('nl-weight-oz')?.value) || 0,
      packageLengthIn: parseFloat($('nl-length')?.value) || 0,
      packageWidthIn: parseFloat($('nl-width')?.value) || 0,
      packageHeightIn: parseFloat($('nl-height')?.value) || 0,
      handlingTimeBusinessDays: parseInt($('nl-handling-time')?.value, 10) || 1,
      itemLocationPostalCode: $('nl-location-zip')?.value || '',
      itemLocationCountry: $('nl-location-country')?.value || 'US',
      privateListing: $('nl-private')?.checked || false,
      charityDonationPercentage: parseInt($('nl-charity-pct')?.value, 10) || 0,
      charityId: $('nl-charity-id')?.value || '',
      listingFormat: $('nl-format')?.value || 'FIXED_PRICE',
      durationDays: parseInt($('nl-duration')?.value, 10) || 7,
      itemSpecifics: nlCollectSpecifics(),
      imageUrls: nlCollectPhotoUrls(),
      fulfillmentPolicyId: $('nl-fulfillment-sel')?.value || null,
      paymentPolicyId:     $('nl-payment-sel')?.value     || null,
      returnPolicyId:      $('nl-return-sel')?.value       || null,
    };
  }

  function nlCollectSpecifics() {
    const out = {};
    // Free-text rows first, so a value that has been matched onto one of eBay's own aspects
    // wins over a stale row the seller typed under a different name for the same field.
    document.querySelectorAll('#nl-specifics-list .specific-row').forEach(row => {
      const [k, v] = row.querySelectorAll('input');
      if (k?.value.trim()) out[k.value.trim()] = v?.value.trim() || '';
    });
    Object.assign(out, nlCollectAspectValues());
    return out;
  }

  // ── Listing Readiness ────────────────────────────────────────────
  //
  // eBay does not say what a category requires until a publish fails. This asks it up front
  // (POST /api/listing/readiness → Taxonomy get_item_aspects_for_category) and turns the answer
  // into fields, so the seller fills in a labelled "Chipset/GPU Model" dropdown instead of
  // guessing at a blank name/value row and finding out minutes later.
  //
  // Everything here is advisory. The seller's own values are never overwritten, suggestions are
  // applied only when clicked, and a blocker warns rather than locks — the app's opinion of a
  // listing is not eBay's, and it is the seller's account.

  let nlRdState    = null;   // last result from the server
  let nlRdTimer    = null;   // debounce handle
  let nlRdSeq      = 0;      // race guard: only the newest response may render
  let nlRdOverride = false;  // seller chose to publish past the blockers

  // Must match ListingReadinessAnalyzer.AspectFieldId — the fix list on one side generates
  // these, the fields on the other side carry them, and a mismatch silently breaks "Fix".
  function nlAspectFieldId(name) {
    let slug = String(name || '').toLowerCase().replace(/[^a-z0-9]/g, '-');
    while (slug.includes('--')) slug = slug.replace(/--/g, '-');
    return 'nl-aspect-' + slug.replace(/^-+/, '').replace(/-+$/, '');
  }

  function nlCollectAspectValues() {
    const out = {};
    document.querySelectorAll('#nl-aspects-panel [data-aspect-name]').forEach(el => {
      const name = el.dataset.aspectName;
      const val  = (el.value || '').trim();
      if (name && val) out[name] = val;
    });
    return out;
  }

  function initListingReadiness() {
    on('nl-rd-toggle', 'click', () => {
      const list = $('nl-rd-list');
      const btn  = $('nl-rd-toggle');
      if (!list || !btn) return;
      const open = list.hidden;
      list.hidden = !open;
      btn.setAttribute('aria-expanded', open ? 'true' : 'false');
      btn.classList.toggle('is-open', open);
    });
    on('nl-aspects-refresh', 'click', () => nlRunReadiness(true));
    on('nl-aspects-autofill', 'click', nlAutofillAspects);

    // Re-check as the listing is written. Debounced, because these fire per keystroke.
    ['nl-title', 'nl-price', 'nl-brand', 'nl-mpn', 'nl-upc', 'nl-ean', 'nl-isbn',
     'nl-desc-text', 'nl-description', 'nl-condition-desc', 'nl-location-zip', 'nl-weight-lbs']
      .forEach(id => on(id, 'input', () => nlRunReadiness()));
    ['nl-condition', 'nl-best-offer'].forEach(id => on(id, 'change', () => nlRunReadiness()));

    // The category picker writes the hidden input directly, so it announces itself (see
    // bindCategorySearch). A category change is the one edit that changes which specifics
    // exist at all, so it re-checks immediately rather than on the debounce.
    on('nl-category-id', 'change', () => nlRunReadiness(true));
  }

  function nlResetReadiness() {
    nlRdState = null;
    nlRdOverride = false;
    if (nlRdTimer) { clearTimeout(nlRdTimer); nlRdTimer = null; }
    ['nl-aspects-required', 'nl-aspects-recommended', 'nl-aspects-optional']
      .forEach(id => { const el = $(id); if (el) el.innerHTML = ''; });
    const optWrap = $('nl-aspects-optional-wrap'); if (optWrap) optWrap.hidden = true;
    const badge = $('nl-aspects-badge'); if (badge) badge.hidden = true;
    const bar = $('nl-readiness'); if (bar) bar.hidden = true;
    const fill = $('nl-aspects-autofill'); if (fill) fill.hidden = true;
    const status = $('nl-aspects-status');
    if (status) {
      status.textContent = 'Pick a category and eBay’s required Item Specifics appear here.';
      status.className = 'asp-status';
    }
  }

  async function nlRunReadiness(immediate = false) {
    if (nlRdTimer) { clearTimeout(nlRdTimer); nlRdTimer = null; }
    if (!immediate) {
      nlRdTimer = setTimeout(() => nlRunReadiness(true), 600);
      return;
    }

    const overlay = $('new-listing-overlay');
    if (!overlay || overlay.classList.contains('hidden')) return;

    const seq = ++nlRdSeq;
    const bar = $('nl-readiness');
    if (bar) bar.hidden = false;

    let payload;
    try { payload = buildNlPayload(); }
    catch { return; }

    // While the plain-text tab is open, the HTML field is only synced on a tab switch, so the
    // payload's description lags behind what the seller is actually typing. The server strips
    // markup for this check anyway, so the live text is the truer input.
    const textTabActive = document.querySelector('.desc-tab[data-desc-tab="text"]')?.classList.contains('active');
    if (textTabActive) {
      const plain = $('nl-desc-text')?.value || '';
      if (plain.trim()) payload.description = plain;
    }

    let data;
    try {
      const res = await fetch('/api/listing/readiness', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (!res.ok) throw new Error('HTTP ' + res.status);
      data = await res.json();
    } catch (err) {
      // A readiness check that fails must never be in the way of listing.
      if (seq !== nlRdSeq) return;
      const grade = $('nl-rd-grade'), headline = $('nl-rd-headline');
      if (grade)    grade.textContent = 'Check unavailable';
      if (headline) headline.textContent = 'Couldn’t reach the app for the pre-publish check — publishing still works.';
      return;
    }

    if (seq !== nlRdSeq) return;   // a newer edit already asked; this answer is stale
    nlRdState = data;
    nlRenderAspects(data);
    nlRenderReadiness(data);
  }

  // ── Rendering: the Item Specifics eBay actually asks for ─────────

  function nlRenderAspects(r) {
    const status = $('nl-aspects-status');
    const badge  = $('nl-aspects-badge');
    const fill   = $('nl-aspects-autofill');

    const groups = {
      required:    $('nl-aspects-required'),
      recommended: $('nl-aspects-recommended'),
      optional:    $('nl-aspects-optional'),
    };
    Object.values(groups).forEach(el => { if (el) el.innerHTML = ''; });

    const aspects = r.aspects || [];

    if (r.aspectStatus !== 'ok') {
      if (status) {
        status.textContent = r.aspectMessage || 'eBay’s Item Specifics couldn’t be checked.';
        status.className = 'asp-status ' + (r.aspectStatus === 'no_category' ? 'is-hint' : 'is-warn');
      }
      if (badge) badge.hidden = true;
      if (fill)  fill.hidden = true;
      const optWrap = $('nl-aspects-optional-wrap'); if (optWrap) optWrap.hidden = true;
      return;
    }

    if (aspects.length === 0) {
      if (status) {
        status.textContent = r.aspectMessage || 'eBay lists no Item Specifics for this category.';
        status.className = 'asp-status is-hint';
      }
      if (badge) badge.hidden = true;
      if (fill)  fill.hidden = true;
      return;
    }

    const missingRequired = aspects.filter(a => a.state === 'missing_required' || a.state === 'invalid_value').length;
    const missingRec      = aspects.filter(a => a.state === 'missing_recommended').length;
    const filled          = aspects.filter(a => a.state === 'filled').length;

    if (status) {
      status.textContent = missingRequired > 0
        ? `${missingRequired} required by eBay still to fill — ${filled} of ${aspects.length} done.`
        : missingRec > 0
          ? `All required specifics done. ${missingRec} more that buyers filter searches by.`
          : `All ${aspects.length} of eBay’s specifics for this category are filled.`;
      status.className = 'asp-status ' + (missingRequired > 0 ? 'is-blocker' : missingRec > 0 ? 'is-warn' : 'is-ok');
    }

    if (badge) {
      if (missingRequired > 0) {
        badge.textContent = missingRequired + ' required missing';
        badge.className = 'asp-summary-badge is-blocker';
        badge.hidden = false;
      } else if (missingRec > 0) {
        badge.textContent = missingRec + ' recommended empty';
        badge.className = 'asp-summary-badge is-warn';
        badge.hidden = false;
      } else {
        badge.textContent = 'complete';
        badge.className = 'asp-summary-badge is-ok';
        badge.hidden = false;
      }
      // Force the panel open the moment eBay says something is missing — a required specific
      // hidden inside a collapsed section is a required specific nobody fills.
      if (missingRequired > 0) $('nl-aspects-panel')?.setAttribute('open', '');
    }

    const fillable = r.autoFillableCount || 0;
    if (fill) {
      fill.hidden = fillable === 0;
      fill.textContent = `Fill ${fillable} from my listing`;
    }

    aspects.forEach(a => {
      const bucket = a.required ? 'required' : a.recommended ? 'recommended' : 'optional';
      groups[bucket]?.appendChild(nlAspectRow(a, bucket));
    });

    const optCount = aspects.filter(a => !a.required && !a.recommended).length;
    const optWrap  = $('nl-aspects-optional-wrap');
    const optLabel = $('nl-aspects-optional-count');
    if (optWrap)  optWrap.hidden = optCount === 0;
    if (optLabel) optLabel.textContent = optCount ? `(${optCount})` : '';

    // A value the seller typed under their own name for an eBay field now lives in that field.
    // Leaving the original row behind would send the same fact twice under two names.
    const claimed = new Set((r.customAspectNames || []).map(n => n.toLowerCase()));
    document.querySelectorAll('#nl-specifics-list .specific-row').forEach(row => {
      const key = row.querySelector('input')?.value.trim();
      if (key && !claimed.has(key.toLowerCase())) row.remove();
    });
  }

  function nlAspectRow(a, bucket) {
    const wrap = document.createElement('div');
    wrap.className = 'asp-row is-' + bucket + (a.state === 'missing_required' || a.state === 'invalid_value' ? ' is-blocker' : '');

    const id = nlAspectFieldId(a.name);
    const pill = a.required
      ? '<span class="asp-pill is-required">Required</span>'
      : a.recommended ? '<span class="asp-pill is-rec">Buyers filter by this</span>' : '';

    const renamed = a.matchedFromKey
      ? `<span class="asp-renamed" title="You typed this as &quot;${esc(a.matchedFromKey)}&quot;">was “${esc(a.matchedFromKey)}”</span>`
      : '';

    // A fixed list gets a dropdown — typing anything else into a SELECTION_ONLY aspect is a
    // publish failure, so the control shouldn't allow it. Everything else is free text with
    // eBay's popular values offered as completions rather than imposed.
    let control;
    if (a.selectionOnly && !a.multiSelect && (a.values || []).length) {
      const opts = ['<option value="">— choose —</option>']
        .concat((a.values || []).map(v =>
          `<option value="${esc(v)}"${v === a.value ? ' selected' : ''}>${esc(v)}</option>`));
      control = `<select id="${id}" data-aspect-name="${esc(a.name)}" class="asp-control">${opts.join('')}</select>`;
    } else {
      const listId = id + '-list';
      const datalist = (a.values || []).length
        ? `<datalist id="${listId}">${(a.values || []).slice(0, 200).map(v => `<option value="${esc(v)}"></option>`).join('')}</datalist>`
        : '';
      const hint = a.multiSelect ? ' placeholder="Separate several with |"' : '';
      const max  = a.maxLength > 0 ? ` maxlength="${a.maxLength}"` : '';
      control = `<input id="${id}" type="text" data-aspect-name="${esc(a.name)}" class="asp-control"
                   value="${esc(a.value || '')}"${(a.values || []).length ? ` list="${listId}"` : ''}${hint}${max} />${datalist}`;
    }

    const note = a.note ? `<span class="asp-note">${esc(a.note)}</span>` : '';

    wrap.innerHTML = `
      <label class="asp-label" for="${id}">${esc(a.name)} ${pill} ${renamed}</label>
      ${control}
      <div class="asp-under">${note}</div>`;

    // The suggestion. Rendered as an offer with its source stated, never written silently —
    // an Item Specific the seller didn't choose is a claim about the item made on their behalf.
    if (a.suggestedValue && a.suggestedValue !== a.value) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'asp-suggest' + (a.suggestionConfidence === 'low' ? ' is-low' : '');
      btn.innerHTML = `Use “${esc(a.suggestedValue)}” <span class="asp-suggest-src">${esc(a.suggestionSource || '')}</span>`;
      btn.addEventListener('click', () => {
        const el = $(id);
        if (el) { el.value = a.suggestedValue; el.classList.add('asp-just-filled'); }
        nlRunReadiness();
      });
      wrap.querySelector('.asp-under')?.appendChild(btn);
    }

    wrap.querySelector('.asp-control')?.addEventListener('change', () => nlRunReadiness());
    return wrap;
  }

  function nlAutofillAspects() {
    const r = nlRdState;
    if (!r || !(r.aspects || []).length) return;
    let applied = 0;
    (r.aspects || []).forEach(a => {
      if (!a.suggestedValue) return;
      if (a.suggestionConfidence !== 'high' && a.suggestionConfidence !== 'medium') return;
      // Never overwrite something the seller supplied — unless eBay would reject it outright.
      if (a.value && a.state !== 'invalid_value') return;
      const el = $(nlAspectFieldId(a.name));
      if (!el) return;
      el.value = a.suggestedValue;
      el.classList.add('asp-just-filled');
      applied++;
    });
    if (applied) {
      addActivity('Item Specifics filled', applied + ' filled from the listing’s own title, description and identifier fields');
      nlRunReadiness(true);
    }
  }

  // ── Rendering: the readiness bar and its fix list ────────────────

  function nlRenderReadiness(r) {
    const bar = $('nl-readiness');
    if (!bar) return;
    bar.hidden = false;

    const blockers = r.blockerCount || 0;
    const tone = blockers > 0 ? 'is-blocker' : r.score >= 90 ? 'is-ok' : r.score >= 70 ? 'is-good' : 'is-warn';
    bar.className = 'rd-bar ' + tone;

    const scoreEl = $('nl-rd-score');
    if (scoreEl) scoreEl.textContent = r.score;
    const gradeEl = $('nl-rd-grade');
    if (gradeEl) gradeEl.textContent = r.grade || '';
    const headEl = $('nl-rd-headline');
    if (headEl) headEl.textContent = r.headline || '';

    const counts = $('nl-rd-counts');
    if (counts) {
      const parts = [];
      if (blockers) parts.push(blockers + ' blocking');
      if (r.warningCount) parts.push(r.warningCount + ' costing sales');
      counts.textContent = parts.join(' · ');
    }

    nlSyncBlockerGate(r);

    const list = $('nl-rd-list');
    if (!list) return;
    list.innerHTML = '';

    const fixes = r.fixes || [];
    if (!fixes.length) {
      list.innerHTML = '<p class="rd-empty">Nothing left to fix. This is as complete as the app knows how to check.</p>';
      return;
    }

    fixes.forEach(f => {
      const row = document.createElement('div');
      row.className = 'rd-fix is-' + f.severity;
      row.innerHTML = `
        <span class="rd-dot" aria-hidden="true"></span>
        <div class="rd-fix-text">
          <span class="rd-fix-label">${esc(f.label)}</span>
          <span class="rd-fix-why">${esc(f.why)}</span>
        </div>`;
      if (f.fieldId) {
        const go = document.createElement('button');
        go.type = 'button';
        go.className = 'btn btn-ghost small rd-fix-go';
        go.textContent = 'Go to it';
        go.addEventListener('click', () => nlFocusField(f.fieldId));
        row.appendChild(go);
      }
      list.appendChild(row);
    });
  }

  // Jump to the field a fix belongs to, opening whatever collapsed panel it lives in — a
  // "Go to it" that scrolls to a closed <details> has sent the seller nowhere.
  function nlFocusField(fieldId) {
    const el = $(fieldId);
    if (!el) return;
    let node = el.parentElement;
    while (node) {
      if (node.tagName === 'DETAILS') node.open = true;
      node = node.parentElement;
    }
    el.scrollIntoView({ block: 'center', behavior: 'smooth' });

    // Some targets aren't form controls — "Add at least one photo" points at the photo grid,
    // which is a div and silently ignores .focus(). A borrowed tabindex makes the jump land
    // somewhere for a keyboard user too, and is removed again so it stays out of the tab order.
    const borrowTabIndex = !el.hasAttribute('tabindex') &&
      !/^(INPUT|SELECT|TEXTAREA|BUTTON|A)$/.test(el.tagName);
    if (borrowTabIndex) el.setAttribute('tabindex', '-1');
    if (typeof el.focus === 'function') el.focus({ preventScroll: true });
    if (borrowTabIndex) el.addEventListener('blur', () => el.removeAttribute('tabindex'), { once: true });

    el.classList.add('asp-just-filled');
    setTimeout(() => el.classList.remove('asp-just-filled'), 1600);
  }

  // ── The pre-publish gate ─────────────────────────────────────────
  //
  // Returns true when the publish should go ahead. A blocker is eBay's rule, not the app's
  // guess, so this is worth stopping for — but it stops once, explains, and offers the override,
  // because the app can be wrong about a category and the account is the seller's.
  function nlBlockersStopPublish() {
    const r = nlRdState;
    if (!r || nlRdOverride) return false;
    const blockers = (r.fixes || []).filter(f => f.severity === 'blocker');
    if (!blockers.length) return false;

    nlRenderBlockerGate(blockers);

    // Open the fix list so the reasons are one click away, not two.
    const rdList = $('nl-rd-list');
    if (rdList && rdList.hidden) $('nl-rd-toggle')?.click();
    return true;
  }

  function nlRenderBlockerGate(blockers) {
    const el = $('nl-result-msg');
    if (!el) return;
    const list = blockers.map(f => `<li>${esc(f.label)}</li>`).join('');
    el.className = 'nl-result-msg error';
    el.dataset.rdGate = '1';   // marks this message as ours, so it can be kept current
    el.innerHTML =
      `<strong>eBay would reject this publish.</strong> ${blockers.length === 1 ? 'One thing needs fixing' : blockers.length + ' things need fixing'} first:` +
      `<ul class="rd-block-list">${list}</ul>` +
      '<button type="button" class="btn btn-primary small" id="nl-rd-fix-first">Take me to the first one</button> ' +
      '<button type="button" class="btn btn-ghost small" id="nl-rd-publish-anyway">Publish anyway</button>';
    $('nl-rd-fix-first')?.addEventListener('click', () => {
      const target = blockers.find(f => f.fieldId);
      if (target) nlFocusField(target.fieldId);
    });
    $('nl-rd-publish-anyway')?.addEventListener('click', () => {
      nlRdOverride = true;
      addActivity('Publishing past the readiness check', blockers.map(f => f.label).join('; '));
      nlSubmit('publish');
    });
  }

  // The gate is a snapshot of the blockers at the moment Publish was pressed. Fixing one of
  // them leaves it on screen still naming it, directly under a bar that now says otherwise —
  // two contradictory answers, and the stale one is the louder. So it tracks, or it goes.
  function nlSyncBlockerGate(r) {
    const el = $('nl-result-msg');
    if (!el || el.dataset.rdGate !== '1') return;

    const blockers = (r.fixes || []).filter(f => f.severity === 'blocker');
    if (!blockers.length) {
      delete el.dataset.rdGate;
      el.className = 'nl-result-msg';
      el.innerHTML = '';
      return;
    }
    nlRenderBlockerGate(blockers);
  }

  function nlAddSpecificRow(key, value) {
    const row = document.createElement('div');
    row.className = 'specific-row';
    row.innerHTML = `
      <input type="text" placeholder="Name, e.g. Model" value="${esc(key)}" />
      <input type="text" placeholder="Value" value="${esc(value)}" />
      <button type="button" title="Remove">X</button>`;
    row.querySelector('button')?.addEventListener('click', () => row.remove());
    $('nl-specifics-list')?.appendChild(row);
  }

  // ── 6-slot Photo Grid ──────────────────────────────────────────

  const PHOTO_SLOT_COUNT = 6;

  function initPhotoGrid() {
    const grid = $('nl-photo-grid');
    if (!grid) return;
    grid.innerHTML = '';
    for (let i = 0; i < PHOTO_SLOT_COUNT; i++) grid.appendChild(createPhotoSlot(i));
  }

  function createPhotoSlot(index) {
    const slot = document.createElement('div');
    slot.className = 'nl-photo-slot';
    slot.dataset.slotIndex = String(index);

    const fileInput = document.createElement('input');
    fileInput.type = 'file';
    fileInput.accept = 'image/*';
    fileInput.hidden = true;
    fileInput.addEventListener('change', () => {
      if (fileInput.files[0]) nlLoadFileIntoSlot(fileInput.files[0], index);
    });

    const ph = document.createElement('div');
    ph.className = 'slot-placeholder';
    ph.innerHTML = `<span class="slot-plus">+</span><span class="slot-num">Picture ${index + 1}</span>`;

    const img = document.createElement('img');
    img.alt = `Picture ${index + 1}`;
    img.style.display = 'none';

    const label = document.createElement('div');
    label.className = 'slot-label';
    label.textContent = `Picture ${index + 1}`;
    label.style.display = 'none';

    const removeBtn = document.createElement('button');
    removeBtn.type = 'button';
    removeBtn.className = 'slot-remove';
    removeBtn.textContent = '✕';
    removeBtn.title = 'Remove';
    removeBtn.addEventListener('click', e => { e.stopPropagation(); clearPhotoSlot(index); });

    const rembgBtn = document.createElement('button');
    rembgBtn.type = 'button';
    rembgBtn.className = 'slot-rembg';
    rembgBtn.textContent = 'Remove BG';
    rembgBtn.addEventListener('click', e => { e.stopPropagation(); nlRemoveBgFromSlot(index); });

    slot.append(fileInput, ph, img, label, removeBtn, rembgBtn);

    slot.addEventListener('click', () => {
      if (slot.classList.contains('has-image')) openPhotoEditor(index);
      else fileInput.click();
    });
    slot.addEventListener('dragover', e => { e.preventDefault(); slot.classList.add('drag-over'); });
    slot.addEventListener('dragleave', () => slot.classList.remove('drag-over'));
    slot.addEventListener('drop', e => {
      e.preventDefault();
      slot.classList.remove('drag-over');
      if (e.dataTransfer.files[0]) nlLoadFileIntoSlot(e.dataTransfer.files[0], index);
    });

    return slot;
  }

  function getPhotoSlot(index) {
    return $('nl-photo-grid')?.querySelector(`[data-slot-index="${index}"]`) || null;
  }

  function setPhotoSlotUrl(index, url, isEbay = false) {
    const slot = getPhotoSlot(index);
    if (!slot) return;
    const img = slot.querySelector('img');
    if (img) { img.src = url; img.style.display = ''; }
    slot.querySelector('.slot-label').style.display = '';
    // Remove old eBay badge if any
    slot.querySelector('.slot-ebay-badge')?.remove();
    if (isEbay) {
      const badge = document.createElement('span');
      badge.className = 'slot-ebay-badge';
      badge.textContent = 'eBay';
      slot.appendChild(badge);
    }
    slot.dataset.url = url;
    slot.classList.add('has-image');
    $('nl-photo-grid')?.closest('details')?.setAttribute('open', '');
    nlRunReadiness();   // photo count is one of the things the readiness bar scores
  }

  function clearPhotoSlot(index) {
    const slot = getPhotoSlot(index);
    if (!slot) return;
    const img = slot.querySelector('img');
    if (img) { img.src = ''; img.style.display = 'none'; }
    slot.querySelector('.slot-label').style.display = 'none';
    slot.querySelector('.slot-ebay-badge')?.remove();
    const fi = slot.querySelector('input[type=file]');
    if (fi) fi.value = '';
    delete slot.dataset.url;
    slot.classList.remove('has-image');
    nlRunReadiness();
  }

  function nlClearAllPhotoSlots() {
    const grid = $('nl-photo-grid');
    if (!grid || !grid.querySelector('.nl-photo-slot')) {
      initPhotoGrid();
      return;
    }
    for (let i = 0; i < PHOTO_SLOT_COUNT; i++) clearPhotoSlot(i);
  }

  function nextEmptySlotIndex() {
    const grid = $('nl-photo-grid');
    if (!grid) return -1;
    const slots = grid.querySelectorAll('.nl-photo-slot');
    for (let i = 0; i < slots.length; i++) {
      if (!slots[i].classList.contains('has-image')) return parseInt(slots[i].dataset.slotIndex);
    }
    return -1;
  }

  function nlAddPhotoRow(url, prepend = false) {
    if (!url) return;
    if (prepend) {
      // Shift existing photos right by one and put new one at slot 0
      const grid = $('nl-photo-grid');
      if (grid) {
        const urls = nlCollectPhotoUrls();
        nlClearAllPhotoSlots();
        setPhotoSlotUrl(0, url);
        urls.slice(0, PHOTO_SLOT_COUNT - 1).forEach((u, i) => setPhotoSlotUrl(i + 1, u));
      }
    } else {
      const idx = nextEmptySlotIndex();
      if (idx !== -1) setPhotoSlotUrl(idx, url);
    }
  }

  async function nlLoadFileIntoSlot(file, index) {
    if (!file.type.startsWith('image/')) return;
    const reader = new FileReader();
    reader.onload = ev => {
      const base64   = ev.target.result.split(',')[1];
      const mimeType = file.type;
      fetch('/api/photos/save-uploaded', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageBase64: base64, mimeType })
      }).then(r => r.ok ? r.json() : null)
        .then(d => setPhotoSlotUrl(index, d?.url || ev.target.result))
        .catch(() => setPhotoSlotUrl(index, ev.target.result));
      addActivity('Photo added', `Picture ${index + 1}`);
    };
    reader.readAsDataURL(file);
  }

  async function nlRemoveBgFromSlot(index) {
    const slot = getPhotoSlot(index);
    if (!slot?.classList.contains('has-image')) return;
    const url = slot.dataset.url;
    if (!url) return;

    const btn = slot.querySelector('.slot-rembg');
    if (btn) { btn.textContent = 'Working…'; btn.disabled = true; }

    try {
      let b64, mimeType;
      if (url.startsWith('data:')) {
        const [header, data] = url.split(',');
        b64 = data; mimeType = header.match(/data:([^;]+)/)?.[1] || 'image/jpeg';
      } else {
        const fetchRes = await fetch(url);
        if (!fetchRes.ok) throw new Error('Could not fetch image');
        const blob = await fetchRes.blob();
        mimeType = blob.type || 'image/jpeg';
        b64 = await blobToBase64(blob);
      }
      const res  = await fetch('/api/photos/remove-bg', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageBase64: b64, mimeType })
      });
      const body = await res.json();
      if (!res.ok) throw new Error(body.error || 'BG removal failed');
      setPhotoSlotUrl(index, body.url);
      addActivity('Background removed', `Picture ${index + 1}`);
    } catch (err) {
      alert('Background removal failed: ' + err.message);
    } finally {
      if (btn) { btn.textContent = 'Remove BG'; btn.disabled = false; }
    }
  }

  async function uploadPhotosToEbay(photoUrls) {
    const statusEl = $('nl-photo-upload-status');
    if (statusEl) { statusEl.classList.remove('hidden'); statusEl.className = 'nl-photo-upload-status'; statusEl.textContent = 'Uploading photos to eBay…'; }

    const results = [];
    for (let i = 0; i < photoUrls.length; i++) {
      const url = photoUrls[i];
      if (!url) { results.push(url); continue; }

      // Already on eBay CDN — skip
      if (url.includes('ebayimg.com') || url.includes('ebay.com/images')) {
        results.push(url);
        continue;
      }

      try {
        if (statusEl) statusEl.textContent = `Uploading picture ${i + 1} of ${photoUrls.length} to eBay…`;

        let b64, mimeType;
        if (url.startsWith('data:')) {
          const [header, data] = url.split(',');
          b64 = data; mimeType = header.match(/data:([^;]+)/)?.[1] || 'image/jpeg';
        } else {
          const fetchRes = await fetch(url);
          if (!fetchRes.ok) throw new Error(`Could not fetch image: ${url}`);
          const blob = await fetchRes.blob();
          mimeType = blob.type || 'image/jpeg';
          b64 = await blobToBase64(blob);
        }

        const uploadRes = await fetch('/api/ebay/upload-picture', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ imageBase64: b64, mimeType })
        });
        const body = await uploadRes.json();
        if (!uploadRes.ok) throw new Error(body.error || 'Upload failed');

        // Update slot to show eBay badge
        setPhotoSlotUrl(i, body.url, true);
        results.push(body.url);
      } catch (err) {
        addActivity(`Picture ${i + 1} eBay upload failed`, err.message);
        // Do NOT fall back to local URL — eBay will reject relative/localhost URLs
        // Leave this slot out of the published listing; image can be added in Seller Hub
        if (statusEl) statusEl.textContent = `Picture ${i + 1} upload failed: ${err.message}`;
      }
    }

    if (statusEl) {
      const uploaded = results.filter(Boolean).length;
      statusEl.className = 'nl-photo-upload-status done';
      statusEl.textContent = uploaded > 0
        ? `${uploaded} photo(s) uploaded to eBay.`
        : 'Photo upload failed — listing will publish without images.';
      setTimeout(() => statusEl.classList.add('hidden'), 4000);
    }
    return results.filter(Boolean);
  }

  function blobToBase64(blob) {
    return new Promise((resolve, reject) => {
      const fr = new FileReader();
      fr.onload  = () => resolve(fr.result.split(',')[1]);
      fr.onerror = reject;
      fr.readAsDataURL(blob);
    });
  }

  function nlCollectPhotoUrls() {
    const grid = $('nl-photo-grid');
    if (!grid) return [];
    return [...grid.querySelectorAll('.nl-photo-slot.has-image')]
      .map(slot => (slot.dataset.url || '').trim())
      .filter(Boolean);
  }

  function nlToggleBestOffer(show) {
    if ($('nl-best-offer-fields')) $('nl-best-offer-fields').style.display = show ? '' : 'none';
    if ($('nl-best-offer-decline')) $('nl-best-offer-decline').style.display = show ? '' : 'none';
  }

  function nlUpdateCharCount(inputId, countId, max) {
    const el = $(inputId);
    const counter = $(countId);
    if (!el || !counter) return;
    const len = el.value.length;
    counter.textContent = `${len} / ${max}`;
    counter.style.color = len > max * .9 ? 'var(--danger)' : '';
  }

  // Terms eBay's own generic "improper words / violation of eBay policy" rejection commonly
  // fires on — mirrors the denylist ClaudeService already avoids when generating descriptions
  // (see HtmlTemplateInstructions / _contactPat in ClaudeService.cs). eBay's error text never
  // names the actual offending word, so this is a best-effort guess at WHERE to look, not a
  // guarantee of finding the exact match — eBay's real filter is broader and undocumented.
  const EBAY_FLAGGED_TERMS = ['guarantee', 'guaranteed', 'warranty', 'best price', 'lowest price', 'click here'];
  const EBAY_CONTACT_PATTERN =
    /(whatsapp\s*[:+]?\s*\+?\d|telegram\s*[:@]|wechat\s*[:@]|[\w.+-]+@[\w-]+\.[a-z]{2,}|\+?1?[\s.-]?\(?\d{3}\)?[\s.-]\d{3}[\s.-]\d{4})/i;
  const EBAY_POLICY_ERROR_PATTERN = /improper words|violation of eBay policy|cannot be listed or modified/i;

  function nlFindFlaggedTerms(text) {
    if (!text) return [];
    const found = [];
    const lower = text.toLowerCase();
    for (const term of EBAY_FLAGGED_TERMS) if (lower.includes(term)) found.push(term);
    const contactMatch = text.match(EBAY_CONTACT_PATTERN);
    if (contactMatch) found.push(contactMatch[0].trim());
    return found;
  }

  function nlClearPolicyHighlights() {
    ['nl-title', 'nl-desc-text', 'nl-description', 'nl-desc-preview'].forEach(id => $(id)?.classList.remove('field-flagged'));
    document.querySelectorAll('#nl-specifics-list .field-flagged').forEach(el => el.classList.remove('field-flagged'));
    document.querySelectorAll('#nl-specifics-list .specific-row.field-flagged').forEach(el => el.classList.remove('field-flagged'));
  }

  // Called after a publish failure — when eBay rejects for a MISSING/INVALID item specific (e.g.
  // "The item specific Chipset/GPU Model is missing"), find that specific's row and highlight it so
  // the seller can jump straight to the box to fix. If the required specific isn't present at all,
  // add an empty row pre-labeled with its name, then highlight + focus its value box.
  function nlHighlightMissingSpecifics(errorText) {
    const text = errorText || '';
    const names = new Set();
    const patterns = [
      /item specific(?:\s+name)?\s+["']?(.+?)["']?\s+is\s+missing/gi,
      /item specific(?:\s+name)?\s+["']?(.+?)["']?\s+is\s+required/gi,
      /missing\s+(?:required\s+)?item specifics?:?\s*["']?([^."'\n]+)["']?/gi,
      /(?:enter|add|provide)\s+a?\s*(?:valid\s+)?value\s+for\s+(?:the\s+)?["']?(.+?)["']?[.,]/gi,
    ];
    for (const re of patterns) {
      let m;
      while ((m = re.exec(text)) !== null) {
        m[1].split(/,|\band\b/).forEach(n => {
          const name = n.replace(/["'.]/g, '').replace(/\s+/g, ' ').trim();
          // Guard against over-capturing whole sentences.
          if (name && name.length <= 60 && !/\bmissing\b|\brequired\b|\blisting\b/i.test(name)) names.add(name);
        });
      }
    }
    if (names.size === 0) return false;

    let firstEl = null;
    names.forEach(name => {
      const norm = name.toLowerCase();
      let row = Array.from(document.querySelectorAll('#nl-specifics-list .specific-row'))
        .find(r => (r.querySelector('input')?.value || '').trim().toLowerCase() === norm);
      if (!row) {
        nlAddSpecificRow(name, '');
        const all = document.querySelectorAll('#nl-specifics-list .specific-row');
        row = all[all.length - 1];
      }
      if (row) {
        row.classList.add('field-flagged');
        const valInput = row.querySelectorAll('input')[1];
        valInput?.classList.add('field-flagged');
        firstEl = firstEl || valInput || row;
      }
    });

    if (firstEl) {
      const note = `\n\n⚠ eBay needs a value for item specific(s): "${[...names].join('", "')}" — highlighted below. Enter a value and republish.`;
      const el = $('nl-result-msg');
      if (el) el.innerHTML += esc(note).replace(/\n/g, '<br>');
      firstEl.scrollIntoView({ behavior: 'smooth', block: 'center' });
      setTimeout(() => firstEl.focus(), 300);
    }
    return true;
  }

  // Called after a publish/save failure — if the error looks like eBay's generic content-policy
  // rejection, scans Title and Description for known-flagged phrases and visually flags whichever
  // field(s) actually contain something suspicious, so the user isn't left guessing which of two
  // fields (or which word in a 4000-character description) to comb through by hand.
  function nlHighlightPolicyIssues(errorText) {
    nlClearPolicyHighlights();
    if (!EBAY_POLICY_ERROR_PATTERN.test(errorText || '')) return;

    const titleText = $('nl-title')?.value || '';
    const descText  = nlHtmlToText($('nl-description')?.value || '') || $('nl-desc-text')?.value || '';
    const titleHits = nlFindFlaggedTerms(titleText);
    const descHits  = nlFindFlaggedTerms(descText);

    let note = '';
    let focusEl = null;

    if (titleHits.length) {
      $('nl-title')?.classList.add('field-flagged');
      focusEl = focusEl || $('nl-title');
      note += `\n\n⚠ Possible flagged text in Title: "${titleHits.join('", "')}"`;
    }
    if (descHits.length) {
      $('nl-desc-text')?.classList.add('field-flagged');
      $('nl-description')?.classList.add('field-flagged');
      $('nl-desc-preview')?.classList.add('field-flagged');
      focusEl = focusEl || $('nl-desc-text');
      note += `\n\n⚠ Possible flagged text in Description: "${descHits.join('", "')}"`;
    }
    if (!titleHits.length && !descHits.length) {
      $('nl-title')?.classList.add('field-flagged');
      $('nl-desc-text')?.classList.add('field-flagged');
      $('nl-description')?.classList.add('field-flagged');
      $('nl-desc-preview')?.classList.add('field-flagged');
      focusEl = $('nl-title');
      note += `\n\n⚠ eBay didn't say which word — Title and Description are both flagged below for review. Common triggers: guarantee/warranty claims, contact info (phone/email/WhatsApp/Telegram), off-eBay sale language.`;
    }

    if (note) {
      const el = $('nl-result-msg');
      if (el) el.innerHTML += esc(note).replace(/\n/g, '<br>');
    }
    focusEl?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    focusEl?.focus();
  }

  function nlSetResult(type, text) {
    const el = $('nl-result-msg');
    if (!el) return;
    delete el.dataset.rdGate;   // a publish result is not the readiness gate; stop tracking it
    el.className = 'nl-result-msg' + (type ? ` ${type}` : '');
    // Use innerHTML with line breaks to show multi-line error details
    el.innerHTML = esc(text).replace(/\n/g, '<br>');
  }

  function nlUpdateDescCount() {
    const activeTab = document.querySelector('.desc-tab.active')?.dataset.descTab;
    const len = activeTab === 'text'
      ? nlTextToHtml($('nl-desc-text')?.value || '').length
      : ($('nl-description')?.value || '').length;
    const counter = $('nl-desc-count');
    if (!counter) return;
    counter.textContent = len.toLocaleString() + ' / 4,000';
    counter.style.color = len > 4000 ? 'var(--danger)' : len > 3600 ? 'var(--warning)' : '';
  }

  function nlSyncDescPreview() {
    const preview = $('nl-desc-preview');
    if (!preview) return;
    const html = $('nl-description')?.value || '';
    preview.innerHTML = html;
  }

  function nlHtmlToText(html) {
    if (!html.trim()) return '';
    const tmp = document.createElement('div');
    tmp.innerHTML = html;
    // Insert newlines at block boundaries before extracting text
    tmp.querySelectorAll('br').forEach(el => el.replaceWith('\n'));
    tmp.querySelectorAll('p, div, h1, h2, h3, h4, h5, h6, li').forEach(el => {
      el.insertAdjacentText('afterend', '\n\n');
    });
    return (tmp.textContent || '').replace(/\n{3,}/g, '\n\n').trim();
  }

  function nlTextToHtml(text) {
    if (!text.trim()) return '';
    return text.trim()
      .split(/\n\n+/)
      .map(p => '<p>' + p.replace(/\n/g, '<br>').trim() + '</p>')
      .filter(p => p !== '<p></p>')
      .join('\n');
  }

  // The leaf block elements nlHtmlToText() walks to build plain text — kept as a single
  // shared definition so extraction and merge-back always agree on the same paragraph order.
  function nlDescBlocks(root) {
    return [...root.querySelectorAll('p, div, h1, h2, h3, h4, h5, h6, li')]
      .filter(el => !el.querySelector('p, div, h1, h2, h3, h4, h5, h6, li'));
  }

  // Writes edited plain text back into the ORIGINAL description's HTML — every heading,
  // bullet list, and inline style stays exactly as Claude generated it; only the wording of
  // each paragraph/heading/list item changes. Falls back to a fresh rebuild only if the
  // original has no recognizable structure to preserve.
  function nlMergeTextIntoHtml(originalHtml, editedText) {
    if (!originalHtml.trim()) return nlTextToHtml(editedText);

    const tmp = document.createElement('div');
    tmp.innerHTML = originalHtml;
    const blocks = nlDescBlocks(tmp);
    if (blocks.length === 0) return nlTextToHtml(editedText);

    const paragraphs = editedText.trim() ? editedText.trim().split(/\n\n+/) : [];

    blocks.forEach((el, i) => {
      const text = paragraphs[i];
      if (text === undefined) { el.remove(); return; } // paragraph deleted — drop that block
      el.innerHTML = esc(text.trim()).replace(/\n/g, '<br>');
    });

    // Extra paragraphs beyond the original block count are new — append as plain <p> tags
    if (paragraphs.length > blocks.length) {
      const extraHtml = paragraphs.slice(blocks.length)
        .map(p => '<p>' + esc(p.trim()).replace(/\n/g, '<br>') + '</p>').join('');
      tmp.insertAdjacentHTML('beforeend', extraHtml);
    }

    return tmp.innerHTML;
  }

  async function nlAutoRemoveBg(localUrl) {
    $('nl-cutout-wrap')?.classList.remove('hidden');
    $('nl-cutout-spinner')?.classList.remove('hidden');
    const cutoutImg = $('nl-cutout-img');
    if (cutoutImg) cutoutImg.src = '';

    try {
      const fetchRes = await fetch(localUrl);
      if (!fetchRes.ok) throw new Error('Could not fetch photo');
      const blob   = await fetchRes.blob();
      const b64    = await blobToBase64(blob);
      const apiRes = await fetch('/api/photos/remove-bg', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageBase64: b64, mimeType: blob.type || 'image/png' })
      });
      const body = await apiRes.json();
      if (!apiRes.ok) throw new Error(body.error || 'Background removal failed');

      if (cutoutImg) cutoutImg.src = body.url;
      setPhotoSlotUrl(0, body.url);
      addActivity('Background removed automatically', 'Set as Picture 1');
    } catch (err) {
      $('nl-cutout-wrap')?.classList.add('hidden');
      addActivity('Auto BG removal failed', err.message);
    } finally {
      $('nl-cutout-spinner')?.classList.add('hidden');
    }
  }

  async function nlAutoRemoveBgFromBase64(b64, mimeType) {
    $('nl-cutout-wrap')?.classList.remove('hidden');
    $('nl-cutout-spinner')?.classList.remove('hidden');
    const cutoutImg = $('nl-cutout-img');
    if (cutoutImg) cutoutImg.src = '';
    try {
      const apiRes = await fetch('/api/photos/remove-bg', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageBase64: b64, mimeType })
      });
      const body = await apiRes.json();
      if (!apiRes.ok) throw new Error(body.error || 'Background removal failed');
      if (cutoutImg) cutoutImg.src = body.url;
      setPhotoSlotUrl(0, body.url);
      addActivity('Background removed automatically', 'Set as Picture 1');
    } catch (err) {
      $('nl-cutout-wrap')?.classList.add('hidden');
      addActivity('Auto BG removal failed', err.message);
    } finally {
      $('nl-cutout-spinner')?.classList.add('hidden');
    }
  }

  async function nlRemoveBg() {
    if (!nlImageBase64) { return; }
    try {
      const res = await fetch('/api/photos/remove-bg', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageBase64: nlImageBase64, mimeType: nlMimeType || 'image/jpeg' })
      });
      const body = await res.json();
      if (!res.ok) throw new Error(body.error || 'Background removal failed');
      nlAddPhotoRow(body.url, true);
      addActivity('Background removed', 'Set as Picture 1');
    } catch (err) {
      addActivity('Background removal failed', err.message);
    }
  }

  async function nlImproveSeo() {
    const btn = $('nl-btn-improve-seo');
    if (btn) { btn.disabled = true; btn.textContent = 'Improving…'; }
    $('nl-ai-done')?.classList.add('hidden');
    $('nl-ai-status')?.classList.remove('hidden');
    if ($('nl-ai-msg')) $('nl-ai-msg').textContent = 'Improving SEO and description…';

    try {
      const payload = {
        title:                   $('nl-title')?.value || '',
        subtitle:                $('nl-subtitle')?.value || '',
        category:                $('nl-category')?.value || '',
        categoryId:              $('nl-category-id')?.value || '',
        condition:               $('nl-condition')?.value || 'USED_EXCELLENT',
        conditionDescription:    $('nl-condition-desc')?.value || '',
        brand:                   $('nl-brand')?.value || '',
        mpn:                     $('nl-mpn')?.value || '',
        description:             $('nl-description')?.value || '',
        price:                   parseFloat($('nl-price')?.value) || 0,
        quantity:                parseInt($('nl-quantity')?.value, 10) || 1,
        weightLbs:               parseFloat($('nl-weight-lbs')?.value) || 0,
        weightOz:                parseFloat($('nl-weight-oz')?.value) || 0,
        packageLengthIn:         parseFloat($('nl-length')?.value) || 0,
        packageWidthIn:          parseFloat($('nl-width')?.value) || 0,
        packageHeightIn:         parseFloat($('nl-height')?.value) || 0,
        handlingTimeBusinessDays: parseInt($('nl-handling-time')?.value, 10) || 1,
        itemLocationPostalCode:  $('nl-location-zip')?.value || '',
        imageUrls:               nlCollectPhotoUrls(),
        itemSpecifics:           nlCollectSpecifics(),
      };

      const { ok, data, failure } = await callApi('/api/improve-seo', {
        method: 'POST', body: payload, timeoutMs: AI_TIMEOUT_MS,
      });

      if (!ok) {
        $('nl-ai-status')?.classList.add('hidden');
        // The listing is untouched on failure — the rewrite is applied only on success — so the
        // original title and description are still exactly as the seller left them.
        renderFailure('nl-failure', failure, { onRetry: () => nlImproveSeo() });
        addActivity('SEO improvement failed', failure?.headline || 'Unknown error');
        return;
      }

      fillNlForm(data);
      scheduleAutosave();
      $('nl-ai-status')?.classList.add('hidden');
      $('nl-ai-done')?.classList.remove('hidden');
      addActivity('SEO improved', data.title || 'Title and description updated');
    } catch (err) {
      $('nl-ai-status')?.classList.add('hidden');
      renderFailure('nl-failure', {
        kind: 'Unknown', headline: 'The app hit an unexpected error during the SEO rewrite',
        whatHappened: 'Your listing has not been changed.',
        whatToDo: 'Try again, or carry on editing by hand.',
        retryable: true, workPreserved: true, technical: String(err?.message || err),
      }, { onRetry: () => nlImproveSeo() });
      addActivity('SEO improvement failed', String(err?.message || err));
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'Improve SEO + Description'; }
    }
  }

  // One publish at a time, enforced here as well as by disabling the buttons. The buttons are not
  // enough on their own: /api/listing/post's success path wires up its own "Publish to eBay Live"
  // button, the readiness gate can re-enter this, and Enter in a text field submits the form — three
  // routes to a second in-flight publish, which is how one item becomes two live listings.
  let publishInFlight = false;

  // Set only by nlSubmitWithoutPhotos, so the "every photo failed" gate can be passed once,
  // deliberately, rather than being silently skipped every time.
  let nlPhotosDeliberatelyEmpty = false;

  async function nlSubmit(mode) {
    const payload = buildNlPayload();
    if (!payload.title.trim()) {
      nlSetResult('error', 'Title is required before submitting.');
      return;
    }
    if (!payload.price || payload.price <= 0) {
      nlSetResult('error', 'Price must be greater than zero.');
      return;
    }

    // Catch what eBay would reject before spending the round trip on it. Drafts are exempt:
    // an unfinished draft is the point of a draft.
    if (mode === 'publish' && nlBlockersStopPublish()) return;

    if (publishInFlight) {
      nlSetResult('', mode === 'publish'
        ? 'Already publishing this listing — waiting for eBay to answer.'
        : 'Already saving.');
      return;
    }

    // Committed to disk before anything is sent. If the app dies mid-publish, this is the row that
    // comes back as "publish outcome unknown" with a Check eBay button beside it.
    payload.workKey = currentWorkKey();
    flushAutosave(false);

    publishInFlight = true;
    const publishBtn = $('nl-btn-publish');
    const draftBtn = $('nl-btn-draft');
    if (publishBtn) publishBtn.disabled = true;
    if (draftBtn) draftBtn.disabled = true;
    hideFailure('nl-failure');

    const endpoint = mode === 'publish' ? '/api/listing/publish' : '/api/listing/post';
    nlSetResult('', mode === 'publish' ? 'Publishing to eBay…' : 'Saving draft…');

    try {
      // Upload photos to eBay EPS before publishing so eBay has accessible URLs
      if (mode === 'publish' && payload.imageUrls && payload.imageUrls.length > 0) {
        const wanted = payload.imageUrls.length;
        payload.imageUrls = await uploadPhotosToEbay(payload.imageUrls);

        // A listing that reaches eBay with no photograph does not sell, and this used to be a
        // four-second status line that then auto-hid — the publish carried on regardless and the
        // seller found out from the live listing. It is now a decision they get to make.
        if (payload.imageUrls.length === 0 && wanted > 0 && !nlPhotosDeliberatelyEmpty) {
          nlSetResult('error', 'None of the photos could be uploaded to eBay.');
          renderFailure('nl-failure', {
            kind: 'Photos',
            headline: `None of the ${wanted} photo${wanted === 1 ? '' : 's'} reached eBay`,
            whatHappened: 'Every photo upload failed, so publishing now would put a listing live with no '
                        + 'pictures at all.',
            whatToDo: 'Try again — photo uploads usually fail on a temporary connection problem. Publishing '
                    + 'without pictures is possible but sells very badly.',
            retryable: true,
            workPreserved: true,
            technical: 'See Logs for the per-photo eBay upload errors.',
          }, {
            onRetry: () => nlSubmit(mode),
            extraButtons: [{
              label: 'Publish without photos',
              run: () => { hideFailure('nl-failure'); nlSubmitWithoutPhotos(mode); },
            }],
          });
          addActivity('Publish stopped', 'No photos could be uploaded to eBay.');
          return;
        }
      }

      const { ok, data: body, failure } = await callApi(endpoint, {
        method: 'POST', body: payload, timeoutMs: PUBLISH_TIMEOUT_MS,
      });

      if (!ok) {
        const short = failure?.headline || body?.error || 'Request failed';
        const details = failure?.technical || body?.details || short;
        nlSetResult('error', (body?.where ? '[' + body.where + '] ' : '') + short);

        // Kept: both of these read eBay's own words to point at the field that needs changing, and
        // eBay's words are still carried through in the technical detail.
        nlHighlightPolicyIssues(short + ' ' + details);
        nlHighlightMissingSpecifics(short + ' ' + details);

        // A publish whose outcome the app cannot vouch for gets a "look, don't resend" button
        // instead of a Retry: pressing Retry on a publish that actually succeeded is what creates
        // the duplicate listing this whole path exists to prevent.
        const uncertain = mode === 'publish'
          && ['Timeout', 'Network', 'UpstreamServerError'].includes(failure?.kind);

        renderFailure('nl-failure', failure || {
          kind: 'Unknown', headline: short, whatHappened: details,
          whatToDo: 'Fix what is named above, then try again.', retryable: false, workPreserved: true,
        }, {
          onRetry: failure?.retryable ? () => nlSubmit(mode) : null,
          extraButtons: uncertain ? [{ label: 'Check eBay', run: () => nlCheckPublished(payload) }] : [],
        });

        addActivity(mode === 'publish' ? 'Publish failed' : 'Save draft failed', short);
        return;
      }

      if (mode === 'publish') {
        const link = body.listingUrl
          ? ' <a href="' + esc(body.listingUrl) + '" target="_blank" rel="noopener noreferrer">View on eBay</a>'
          : '';
        // Three distinct outcomes, said plainly. "Already live" and "just published" are not the same
        // thing, and a seller told the wrong one either goes looking for a listing that does not
        // exist or publishes a duplicate of one that does.
        const headline = body.alreadyPublished
          ? '✓ Already live — no second listing was created. ID: ' + (body.listingId || '-')
          : body.reconciled
            ? '✓ It did go live — the confirmation just got lost on the way back. ID: ' + (body.listingId || '-')
            : '✓ Published live! Listing ID: ' + (body.listingId || '-');
        nlSetResult('success', headline + (link ? ' —' : ''));
        $('nl-result-msg').innerHTML += link;
        if (body.message) {
          $('nl-result-msg').innerHTML += '<span class="nl-result-note">' + esc(body.message) + '</span>';
        }
        addActivity('Listing published live', 'ID: ' + (body.listingId || '-') + '; Offer: ' + (body.offerId || '-'));
        loadListings('Listings refreshed after publish');
        // Published work is no longer unfinished work — stop offering it back.
        loadRecoverableWork();
      } else {
        const offerId = body.offerId || '-';
        const el = $('nl-result-msg');
        if (el) {
          el.className = 'nl-result-msg success';
          el.innerHTML =
            '✓ Draft saved (offer ID: ' + esc(offerId) + '). ' +
            '<a href="https://www.ebay.com/sh/inventory?status=UNPUBLISHED" target="_blank" rel="noopener noreferrer"><strong>View in Seller Hub → Inventory → Unpublished ↗</strong></a> — ' +
            'or publish now: ' +
            '<button type="button" class="btn btn-primary small" id="nl-publish-now-btn" style="margin-left:8px">Publish to eBay Live</button>';
          $('nl-publish-now-btn')?.addEventListener('click', () => nlSubmit('publish'));
        }
        addActivity('Draft saved', 'Offer ID: ' + offerId);
      }
    } catch (err) {
      // callApi does not throw, so anything landing here is a bug in this function rather than a
      // failed request — say so honestly instead of blaming eBay, and never imply work was lost.
      nlSetResult('error', 'The app hit an unexpected error before finishing.');
      renderFailure('nl-failure', {
        kind: 'Unknown',
        headline: 'The app hit an unexpected error',
        whatHappened: 'Something failed inside the app while ' + (mode === 'publish' ? 'publishing' : 'saving') + '.',
        whatToDo: 'Your listing is still on screen and autosaved. Try again, and send the detail below if it '
                + 'keeps happening.',
        retryable: true,
        workPreserved: true,
        technical: String(err?.message || err),
      }, { onRetry: () => nlSubmit(mode), });
      addActivity(mode === 'publish' ? 'Publish failed' : 'Save draft failed', String(err?.message || err));
    } finally {
      publishInFlight = false;
      if (publishBtn) publishBtn.disabled = false;
      if (draftBtn) draftBtn.disabled = false;
    }
  }

  // The seller has decided a photoless listing is better than no listing. Their call to make, so it
  // is offered as a button rather than decided for them in either direction.
  async function nlSubmitWithoutPhotos(mode) {
    nlPhotosDeliberatelyEmpty = true;
    try { await nlSubmit(mode); }
    finally { nlPhotosDeliberatelyEmpty = false; }
  }

  // "Did it actually go live?", asked without sending anything. This is the button offered instead of
  // Retry after a timeout, because a retry there is exactly what produces a duplicate listing.
  async function nlCheckPublished(payload) {
    nlSetResult('', 'Checking your live eBay listings…');
    const { ok, data, failure } = await callApi('/api/listing/check-published', {
      method: 'POST',
      body: { title: payload.title, workKey: payload.workKey },
      timeoutMs: 90000,
    });

    if (!ok) {
      renderFailure('nl-failure', failure, { onRetry: () => nlCheckPublished(payload) });
      nlSetResult('error', 'The check did not complete.');
      return;
    }

    if (data.found) {
      hideFailure('nl-failure');
      const link = data.listingUrl
        ? ' <a href="' + esc(data.listingUrl) + '" target="_blank" rel="noopener noreferrer">View on eBay</a>'
        : '';
      nlSetResult('success', '✓ It is live on eBay — listing ID ' + (data.listingId || '-') + '.' + (link ? ' —' : ''));
      $('nl-result-msg').innerHTML += link +
        '<span class="nl-result-note">' + esc(data.message || '') + '</span>';
      addActivity('Publish reconciled', 'The listing was already live — no duplicate was created.');
      loadListings('Listings refreshed after reconciling a publish');
      loadRecoverableWork();
      return;
    }

    // Not found is good news here, and worth saying so plainly: it makes publishing again safe.
    renderFailure('nl-failure', {
      kind: 'NotFound',
      headline: 'It did not go live',
      whatHappened: data.message || 'No live listing with this title was found on your account.',
      whatToDo: 'Publishing again is safe — there is nothing to duplicate.',
      retryable: true,
      workPreserved: true,
      technical: '',
    }, { onRetry: () => nlSubmit('publish') });
    nlSetResult('', 'Not found on eBay — publishing again is safe.');
  }

  // ── Settings page: Image Generation section ──────────────────

  function computePgImggenMode(imageGenMode, localSdBackend) {
    if (imageGenMode === 'dalle') return 'dalle';
    if (imageGenMode === 'local_sd') return localSdBackend === 'comfyui' ? 'comfyui' : 'a1111';
    return 'disabled';
  }

  function applyPgImggenVisibility(pgMode) {
    const isLocal = pgMode === 'a1111' || pgMode === 'comfyui';
    const isComfy = pgMode === 'comfyui';
    const ep = $('pg-imggen-endpoint-wrap');
    const m  = $('pg-imggen-model-wrap');
    if (ep) ep.style.display = isLocal ? '' : 'none';
    if (m)  m.style.display  = isComfy ? '' : 'none';
    if (pgMode === 'a1111') {
      const cur = $('pg-imggen-endpoint')?.value;
      if (!cur || cur === 'http://127.0.0.1:8188') setVal('pg-imggen-endpoint', 'http://127.0.0.1:7860');
    } else if (pgMode === 'comfyui') {
      const cur = $('pg-imggen-endpoint')?.value;
      if (!cur || cur === 'http://127.0.0.1:7860') setVal('pg-imggen-endpoint', 'http://127.0.0.1:8188');
    }
  }

  async function savePgImggen() {
    const pgMode   = $('pg-imggen-mode')?.value || 'disabled';
    const endpoint = $('pg-imggen-endpoint')?.value.trim() || '';
    const model    = $('pg-imggen-model')?.value.trim() || '';
    const msg      = $('pg-imggen-msg');
    if (msg) { msg.textContent = 'Saving…'; msg.className = 'sd-test-msg'; }

    let imageGenMode, localSdBackend, localSdEndpoint;
    if (pgMode === 'a1111') {
      imageGenMode = 'local_sd'; localSdBackend = 'automatic1111';
      localSdEndpoint = endpoint || 'http://127.0.0.1:7860';
    } else if (pgMode === 'comfyui') {
      imageGenMode = 'local_sd'; localSdBackend = 'comfyui';
      localSdEndpoint = endpoint || 'http://127.0.0.1:8188';
    } else if (pgMode === 'dalle') {
      imageGenMode = 'dalle'; localSdBackend = 'automatic1111'; localSdEndpoint = '';
    } else {
      imageGenMode = 'disabled'; localSdBackend = 'automatic1111'; localSdEndpoint = '';
    }

    const body = { imageGenMode, localSdBackend };
    if (localSdEndpoint) body.localSdEndpoint = localSdEndpoint;
    if (model)           body.localSdModelName = model;

    try {
      const res = await fetch('/api/setup/save', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
      });
      if (!res.ok) throw new Error(await res.text());
      if (msg) { msg.textContent = 'Saved.'; msg.className = 'sd-test-msg ok'; }
      // Sync API Credentials modal fields
      setVal('s-image-gen-mode', imageGenMode);
      setVal('s-local-sd-backend', localSdBackend);
      if (localSdEndpoint) setVal('s-local-sd-endpoint', localSdEndpoint);
      applyImageGenModeVisibility(imageGenMode);
      applyComfyUiModelVisibility(localSdBackend);
      addActivity('Image generation settings saved', pgMode === 'disabled' ? 'Disabled' : pgMode);
    } catch (err) {
      if (msg) { msg.textContent = 'Save failed: ' + err.message; msg.className = 'sd-test-msg error'; }
    }
  }

  async function loadComfyModels(endpoint, selectId, msgId) {
    const sel = $(selectId);
    const msg = msgId ? $(msgId) : null;
    if (!sel) return;
    if (!endpoint) {
      if (msg) { msg.textContent = 'Enter the ComfyUI endpoint first.'; msg.className = 'sd-test-msg error'; }
      return;
    }
    if (msg) { msg.textContent = 'Loading models…'; msg.className = 'sd-test-msg'; }
    try {
      const data = await fetch('/api/image-gen/comfyui-models?endpoint=' + encodeURIComponent(endpoint)).then(r => r.json());
      const models = data.models || [];
      if (models.length === 0) {
        if (msg) { msg.textContent = 'No checkpoints found in ComfyUI.'; msg.className = 'sd-test-msg error'; }
        return;
      }
      const current = sel.value;
      sel.innerHTML = models.map(m =>
        `<option value="${esc(m)}"${m === current ? ' selected' : ''}>${esc(m)}</option>`
      ).join('');
      if (msg) { msg.textContent = models.length + ' model(s) loaded.'; msg.className = 'sd-test-msg ok'; }
    } catch (err) {
      if (msg) { msg.textContent = 'Failed to load models: ' + err.message; msg.className = 'sd-test-msg error'; }
    }
  }

  async function testPgImggenConnection() {
    const pgMode   = $('pg-imggen-mode')?.value || 'disabled';
    const endpoint = $('pg-imggen-endpoint')?.value.trim();
    const backend  = pgMode === 'comfyui' ? 'comfyui' : 'automatic1111';
    const msg      = $('pg-imggen-msg');
    const btn      = $('pg-imggen-test');

    if (pgMode === 'disabled' || pgMode === 'dalle') {
      if (msg) { msg.textContent = 'No local server to test in this mode.'; msg.className = 'sd-test-msg'; }
      return;
    }
    if (!endpoint) {
      if (msg) { msg.textContent = 'Enter a server URL first.'; msg.className = 'sd-test-msg error'; }
      return;
    }
    if (btn) { btn.disabled = true; btn.textContent = 'Testing…'; }
    if (msg) { msg.textContent = ''; msg.className = 'sd-test-msg'; }

    try {
      const res = await fetch('/api/image-gen/test-endpoint?endpoint=' + encodeURIComponent(endpoint) + '&backend=' + encodeURIComponent(backend))
        .then(r => r.json());
      if (msg) { msg.textContent = res.message; msg.className = 'sd-test-msg ' + (res.online ? 'ok' : 'error'); }
      if (res.online && pgMode === 'comfyui') {
        await loadComfyModels(endpoint, 'pg-imggen-model', 'pg-imggen-msg');
      }
    } catch (err) {
      if (msg) { msg.textContent = 'Error: ' + err.message; msg.className = 'sd-test-msg error'; }
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'Test Connection'; }
    }
  }

  function bindPgImggen() {
    on('pg-imggen-mode', 'change', e => applyPgImggenVisibility(e.target.value));
    on('pg-imggen-save', 'click', savePgImggen);
    on('pg-imggen-test', 'click', testPgImggenConnection);
    on('pg-terapeak-connect', 'click', terapeakConnect);
    on('pg-terapeak-disconnect', 'click', terapeakDisconnect);
    on('pg-imggen-guide', 'click', openImageGenSetup);
    on('pg-imggen-load-models', 'click', () => {
      const endpoint = $('pg-imggen-endpoint')?.value.trim();
      loadComfyModels(endpoint, 'pg-imggen-model', 'pg-imggen-msg');
    });
    on('pg-defaults-save', 'click', saveListingDefaults);
    on('pg-fees-save', 'click', saveFeeProfile);
  }

  // ── Image Generator Setup Modal ──────────────────────────────

  function bindImageGenSetup() {
    on('imggen-close',     'click', closeImageGenSetup);
    on('imggen-btn-cancel','click', closeImageGenSetup);
    on('imggen-setup-overlay', 'click', e => {
      if (e.target === $('imggen-setup-overlay')) closeImageGenSetup();
    });
    $('imggen-setup-overlay')?.addEventListener('keydown', e => {
      if (e.key === 'Escape') closeImageGenSetup();
    });

    document.querySelectorAll('.imggen-tab').forEach(tab => {
      tab.addEventListener('click', () => switchImageGenTab(tab.dataset.imggenTab));
    });

    on('imggen-backend', 'change', () => {
      const backend = $('imggen-backend')?.value;
      if (backend === 'comfyui') {
        if ($('imggen-endpoint')?.value === 'http://127.0.0.1:7860') $('imggen-endpoint').value = 'http://127.0.0.1:8188';
        if ($('imggen-model-wrap')) $('imggen-model-wrap').style.display = '';
      } else {
        if ($('imggen-endpoint')?.value === 'http://127.0.0.1:8188') $('imggen-endpoint').value = 'http://127.0.0.1:7860';
        if ($('imggen-model-wrap')) $('imggen-model-wrap').style.display = 'none';
      }
    });

    on('imggen-btn-test', 'click', testImageGenConnection);
    on('imggen-btn-save', 'click', saveImageGenSettings);
    on('btn-open-imggen-setup', 'click', openImageGenSetup);
    on('nl-imggen-setup-link',  'click', openImageGenSetup);
    on('s-btn-load-models', 'click', () => {
      const endpoint = $('s-local-sd-endpoint')?.value.trim();
      loadComfyModels(endpoint, 's-local-sd-model', 'sd-test-msg');
    });
  }

  function openImageGenSetup() {
    // Pre-fill from saved settings
    const endpoint = $('s-local-sd-endpoint')?.value || 'http://127.0.0.1:7860';
    const backend  = $('s-local-sd-backend')?.value  || 'automatic1111';
    const model    = $('s-local-sd-model')?.value    || '';
    if ($('imggen-endpoint')) $('imggen-endpoint').value = endpoint;
    if ($('imggen-backend'))  $('imggen-backend').value  = backend;
    if ($('imggen-model'))    $('imggen-model').value    = model;
    if ($('imggen-model-wrap')) $('imggen-model-wrap').style.display = backend === 'comfyui' ? '' : 'none';

    const msg = $('imggen-test-msg');
    if (msg) { msg.textContent = ''; msg.className = 'sd-test-msg'; }

    $('imggen-setup-overlay')?.classList.remove('hidden');
    $('imggen-setup-overlay')?.focus();
    detectImageServers();
  }

  function closeImageGenSetup() {
    $('imggen-setup-overlay')?.classList.add('hidden');
  }

  function switchImageGenTab(tab) {
    document.querySelectorAll('.imggen-tab').forEach(t => t.classList.toggle('active', t.dataset.imggenTab === tab));
    ['stability','a1111','comfyui'].forEach(id => {
      $('imggen-panel-' + id)?.classList.toggle('hidden', id !== tab);
    });
    if (tab === 'comfyui') {
      if ($('imggen-backend')) $('imggen-backend').value = 'comfyui';
      if ($('imggen-endpoint')?.value === 'http://127.0.0.1:7860') $('imggen-endpoint').value = 'http://127.0.0.1:8188';
      if ($('imggen-model-wrap')) $('imggen-model-wrap').style.display = '';
    } else if (tab === 'a1111') {
      if ($('imggen-backend')) $('imggen-backend').value = 'automatic1111';
      if ($('imggen-endpoint')?.value === 'http://127.0.0.1:8188') $('imggen-endpoint').value = 'http://127.0.0.1:7860';
      if ($('imggen-model-wrap')) $('imggen-model-wrap').style.display = 'none';
    }
    // stability tab: leave backend/endpoint as-is so user can choose
  }

  async function detectImageServers() {
    const banner = $('imggen-detect-banner');
    const text   = $('imggen-detect-text');
    if (banner) banner.className = 'imggen-detect-banner detecting';
    if (text)   text.textContent = 'Checking for running image servers...';

    try {
      const result = await fetch('/api/image-gen/detect').then(r => r.json());
      let msg, cls;

      if (result.a1111Online && result.comfyOnline) {
        msg = 'Both AUTOMATIC1111 (port 7860) and ComfyUI (port 8188) detected. Select which backend to use below.';
        cls = 'detected';
      } else if (result.a1111Online) {
        msg = 'AUTOMATIC1111 detected at ' + result.a1111Endpoint + '. Click Test Connection to confirm, then Enable & Save.';
        cls = 'detected';
        if ($('imggen-backend'))  $('imggen-backend').value  = 'automatic1111';
        if ($('imggen-endpoint')) $('imggen-endpoint').value = result.a1111Endpoint;
        if ($('imggen-model-wrap')) $('imggen-model-wrap').style.display = 'none';
      } else if (result.comfyOnline) {
        msg = 'ComfyUI detected at ' + result.comfyEndpoint + '. Enter the checkpoint name below, then click Test Connection.';
        cls = 'detected';
        if ($('imggen-backend'))  $('imggen-backend').value  = 'comfyui';
        if ($('imggen-endpoint')) $('imggen-endpoint').value = result.comfyEndpoint;
        if ($('imggen-model-wrap')) $('imggen-model-wrap').style.display = '';
      } else {
        msg = 'No local image server detected at ports 7860 or 8188. Follow the setup guide below to install one.';
        cls = 'not-detected';
      }

      if (banner) banner.className = 'imggen-detect-banner ' + cls;
      if (text)   text.textContent = msg;
    } catch (err) {
      if (banner) banner.className = 'imggen-detect-banner not-detected';
      if (text)   text.textContent = 'Detection error: ' + err.message;
    }
  }

  async function testImageGenConnection() {
    const btn      = $('imggen-btn-test');
    const msg      = $('imggen-test-msg');
    const endpoint = $('imggen-endpoint')?.value.trim();
    const backend  = $('imggen-backend')?.value || 'automatic1111';

    if (!endpoint) {
      if (msg) { msg.textContent = 'Enter a server URL first.'; msg.className = 'sd-test-msg error'; }
      return;
    }

    if (btn) { btn.disabled = true; btn.textContent = 'Testing...'; }
    if (msg) { msg.textContent = ''; msg.className = 'sd-test-msg'; }

    try {
      const res = await fetch('/api/image-gen/test-endpoint?endpoint=' + encodeURIComponent(endpoint) + '&backend=' + encodeURIComponent(backend))
        .then(r => r.json());
      if (msg) {
        msg.textContent = res.message;
        msg.className = 'sd-test-msg ' + (res.online ? 'ok' : 'error');
      }
    } catch (err) {
      if (msg) { msg.textContent = 'Error: ' + err.message; msg.className = 'sd-test-msg error'; }
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'Test Connection'; }
    }
  }

  async function saveImageGenSettings() {
    const endpoint = $('imggen-endpoint')?.value.trim() || 'http://127.0.0.1:7860';
    const backend  = $('imggen-backend')?.value || 'automatic1111';
    const model    = $('imggen-model')?.value.trim() || '';
    const msg      = $('imggen-test-msg');

    try {
      const res = await fetch('/api/setup/save', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageGenMode: 'local_sd', localSdEndpoint: endpoint, localSdBackend: backend, localSdModelName: model })
      });
      if (!res.ok) throw new Error(await res.text());

      // Sync settings panel fields
      if ($('s-image-gen-mode'))    $('s-image-gen-mode').value    = 'local_sd';
      if ($('s-local-sd-endpoint')) $('s-local-sd-endpoint').value = endpoint;
      if ($('s-local-sd-backend'))  $('s-local-sd-backend').value  = backend;
      if ($('s-local-sd-model'))    setModelSelect('s-local-sd-model', model);
      applyImageGenModeVisibility('local_sd');
      applyComfyUiModelVisibility(backend);

      addActivity('Local image generation enabled', 'Backend: ' + backend + '; Endpoint: ' + endpoint);
      closeImageGenSetup();
    } catch (err) {
      if (msg) { msg.textContent = 'Save failed: ' + err.message; msg.className = 'sd-test-msg error'; }
    }
  }

  function bindForm() {
    on('btn-new-listing', 'click', () => {
      activeOfferId = '';
      activeListingId = '';
      activeSku = '';
      activeListingStatus = '';
      pendingDraftPayload = null;
      hideDraftPreview();
      document.querySelectorAll('.listing-card.active').forEach(c => c.classList.remove('active'));
      $('btn-post')?.classList.remove('hidden');
      $('btn-create-ebay-draft')?.classList.add('hidden');
      $('btn-update')?.classList.add('hidden');
      $('btn-new-listing')?.classList.add('hidden');
      $('form-section')?.classList.add('hidden');
      closeEditDrawer(true);   // leaving edit mode entirely — nothing to warn about
      showAiSection();
      hideResult();
    });

    // Pushes straight to the live eBay listing — same call the New Listing "Publish to eBay"
    // button uses under the hood (UpdateListingAsync/ReviseInventoryStatusAsync in
    // EbayService.cs), no separate "local only" step first. UpdateListingAsync picks the right
    // eBay API automatically: the Inventory API if this listing has an offerId (created through
    // this app), or the Trading API's ReviseInventoryStatus (price/quantity) if it only has a
    // ListingId (imported from eBay directly, which is most of a seller's existing catalog).
    on('btn-update', 'click', async () => {
      if (!canReviseOnEbay({ offerId: activeOfferId, listingId: activeListingId, sku: activeSku, status: activeListingStatus })) {
        showResult('error', 'This is a sample/placeholder listing — it was never published to eBay, so there is nothing to update there.');
        return;
      }
      if (!confirm('This will push these changes directly to your live eBay listing. Continue?')) return;

      const btn = $('btn-update');
      btn.disabled = true;
      btn.textContent = 'Publishing to eBay…';
      hideResult();

      const payload = buildPayload();
      payload.offerId = activeOfferId;
      payload.listingId = activeListingId;
      payload.sku = activeSku;
      payload.manualRevisionConfirmed = true;

      try {
        const res = await fetch('/api/listing/update', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });
        if (!res.ok) throw new Error(await res.text());

        // Keep the local dashboard cache in sync too — best-effort, doesn't affect the
        // already-successful eBay update if it fails for some reason.
        try {
          const editRes = await fetch('/api/local-listings/save-edit', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
          });
          if (editRes.ok) applyLocalEdit(payload, (await editRes.json()).savedAt);
        } catch { /* non-fatal */ }

        showResult('success', '✓ Published to eBay live.');
        addActivity('eBay listing updated', payload.title || activeSku);
        loadListings('Listings refreshed after eBay update');
      } catch (err) {
        showResult('error', `eBay update failed: ${esc(err.message)}`);
        addActivity('eBay update failed', err.message);
      } finally {
        btn.disabled = false;
        btn.textContent = 'Save Changes';
      }
    });

    on('listing-form', 'submit', e => {
      e.preventDefault();
      pendingDraftPayload = buildPayload();
      renderDraftPreview(pendingDraftPayload);
      showResult('success', 'Draft preview is ready. Live publishing will be added behind a separate confirmation workflow.');
      addActivity('Draft preview prepared', $('f-title')?.value || 'Untitled draft');
      $('btn-create-ebay-draft')?.classList.remove('hidden');
    });

    on('btn-create-ebay-draft', 'click', async () => {
      if (!pendingDraftPayload) pendingDraftPayload = buildPayload();
      const ok = window.confirm('Create an eBay draft offer now? This will not publish the listing.');
      if (!ok) return;

      const btn = $('btn-create-ebay-draft');
      btn.disabled = true;
      btn.textContent = 'Creating draft...';
      hideResult();

      try {
        const { res, body } = await safePost('/api/listing/post', pendingDraftPayload);
        if (!res.ok) {
          const short   = body.error   || 'Request failed';
          const details = body.details || short;
          const where   = body.where   ? ' [' + body.where + ']' : '';
          showResult('error', 'Draft creation failed' + where + ': ' + esc(short)
            + (details !== short ? '<br><small style="opacity:.8">' + esc(details) + '</small>' : ''));
          addActivity('Draft creation failed', 'HTTP ' + res.status + ': ' + short);
        } else {
          showResult('success', 'eBay draft created. Offer ID: ' + esc(body.offerId || '-') + '. It has not been published.');
          addActivity('eBay draft created', body.offerId || pendingDraftPayload.title || 'Draft offer');
        }
      } catch (err) {
        showResult('error', 'Draft creation failed: ' + esc(err.message));
        addActivity('Draft creation failed', err.message);
      } finally {
        btn.disabled = false;
        btn.textContent = 'Create eBay Draft';
      }
    });

    on('btn-new-from-edit', 'click', openNewListingModal);
    on('f-title', 'input', () => updateCharCount('f-title', 'title-count', 80));
    on('f-subtitle', 'input', () => updateCharCount('f-subtitle', 'subtitle-count', 55));
    on('f-best-offer', 'change', e => toggleBestOfferFields(e.target.checked));
    on('f-format', 'change', e => {
      $('duration-wrap').style.display = e.target.value === 'AUCTION' ? '' : 'none';
    });
    if ($('duration-wrap')) $('duration-wrap').style.display = 'none';
    on('btn-add-specific', 'click', () => addSpecificRow('', ''));
    on('btn-add-photo-url', 'click', () => addPhotoRow(''));
  }

  function fillForm(d) {
    set('f-title', d.title || '');
    set('f-subtitle', d.subtitle || '');
    set('f-category', d.category || '');
    set('f-category-id', d.categoryId || '');
    set('f-secondary-category-id', d.secondaryCategoryId || '');
    set('f-condition', d.condition || 'USED_EXCELLENT');
    set('f-condition-desc', d.conditionDescription || '');
    set('f-brand', d.brand || '');
    set('f-mpn', d.mpn || '');
    set('f-upc', d.upc || '');
    set('f-ean', d.ean || '');
    set('f-isbn', d.isbn || '');
    set('f-description', d.description || '');
    set('f-price', d.price || '');
    set('f-quantity', d.quantity || 1);
    set('f-qty-limit', d.quantityLimitPerBuyer || '');
    set('f-package-type', d.packageType || 'PACKAGE_THICK_ENVELOPE');
    set('f-weight-lbs', d.weightLbs || 0);
    set('f-weight-oz', d.weightOz || 0);
    set('f-length', d.packageLengthIn || '');
    set('f-width', d.packageWidthIn || '');
    set('f-height', d.packageHeightIn || '');
    set('f-handling-time', d.handlingTimeBusinessDays || 1);
    set('f-location-zip', d.itemLocationPostalCode || '');
    set('f-location-country', d.itemLocationCountry || 'US');
    set('f-charity-pct', d.charityDonationPercentage || 0);
    set('f-charity-id', d.charityId || '');

    const bestOffer = !!d.bestOfferEnabled;
    if ($('f-best-offer')) $('f-best-offer').checked = bestOffer;
    toggleBestOfferFields(bestOffer);
    if (bestOffer) {
      set('f-auto-accept', '');
      set('f-auto-decline', '');
    }

    if ($('f-private')) $('f-private').checked = !!d.privateListing;

    const list = $('specifics-list');
    if (list) list.innerHTML = '';
    if (d.itemSpecifics) {
      Object.entries(d.itemSpecifics).forEach(([k, v]) => addSpecificRow(k, v));
    }

    const photos = $('photo-url-list');
    if (photos) photos.innerHTML = '';
    (d.imageUrls || []).forEach(url => addPhotoRow(url));

    updateCharCount('f-title', 'title-count', 80);
    updateCharCount('f-subtitle', 'subtitle-count', 55);
    scheduleTakeHome('f');   // a new price means a new take-home
  }

  function toggleBestOfferFields(show) {
    if ($('best-offer-fields')) $('best-offer-fields').style.display = show ? '' : 'none';
    if ($('best-offer-decline')) $('best-offer-decline').style.display = show ? '' : 'none';
  }

  function addSpecificRow(key, value) {
    const row = document.createElement('div');
    row.className = 'specific-row';
    row.innerHTML = `
      <input type="text" placeholder="Name, e.g. Model" value="${esc(key)}" />
      <input type="text" placeholder="Value" value="${esc(value)}" />
      <button type="button" title="Remove">X</button>`;
    row.querySelector('button')?.addEventListener('click', () => row.remove());
    $('specifics-list')?.appendChild(row);
  }

  function addPhotoRow(value) {
    const row = document.createElement('div');
    row.className = 'photo-url-row';
    row.innerHTML = `
      <input type="url" placeholder="https://example.com/photo.jpg" value="${esc(value)}" />
      <button type="button" title="Remove">X</button>`;
    row.querySelector('button')?.addEventListener('click', () => row.remove());
    $('photo-url-list')?.appendChild(row);
  }

  function collectSpecifics() {
    const out = {};
    document.querySelectorAll('.specific-row').forEach(row => {
      const [k, v] = row.querySelectorAll('input');
      if (k.value.trim()) out[k.value.trim()] = v.value.trim();
    });
    return out;
  }

  function collectPhotoUrls() {
    return [...document.querySelectorAll('.photo-url-row input')]
      .map(input => input.value.trim())
      .filter(Boolean);
  }

  function applyLocalEdit(payload, savedAt) {
    const listing = cachedListings.find(l =>
      (payload.listingId && l.listingId === payload.listingId) ||
      (payload.offerId && l.offerId === payload.offerId) ||
      (payload.sku && l.sku === payload.sku)
    );
    if (!listing) return;

    listing.title = payload.title;
    listing.price = payload.price;
    listing.quantity = payload.quantity;
    listing.category = payload.category;
    listing.categoryId = payload.categoryId;
    listing.thumbnailUrl = payload.imageUrls[0] || listing.thumbnailUrl || '';
    listing.condition = payload.condition;
    listing.status = (listing.status || '').toUpperCase() === 'SAMPLE' ? 'SAMPLE' : 'LOCAL_EDIT';
    listing.lastUpdated = savedAt || new Date().toISOString();
    listing.data = { ...payload };
    renderListings();
    updateStats();
    markDrawerClean();   // saved — drop the unsaved-changes guard
  }

  function renderDraftPreview(payload) {
    const panel = $('draft-preview-panel');
    if (!panel) return;

    panel.innerHTML = `
      <h3>Draft Preview</h3>
      <div class="draft-preview-grid">
        ${previewItem('Title', payload.title || 'Untitled draft')}
        ${previewItem('Price', money(payload.price))}
        ${previewItem('Quantity', payload.quantity || 1)}
        ${previewItem('Category', payload.category || payload.categoryId || '-')}
        ${previewItem('Condition', displayStatus(payload.condition || ''))}
        ${previewItem('Photos', `${payload.imageUrls.length} URL${payload.imageUrls.length === 1 ? '' : 's'}`)}
        ${previewItem('Item Specifics', Object.keys(payload.itemSpecifics || {}).length)}
        ${previewItem('Shipping', `${payload.weightLbs || 0} lb ${payload.weightOz || 0} oz`)}
      </div>`;
    panel.classList.remove('hidden');
  }

  function hideDraftPreview() {
    const panel = $('draft-preview-panel');
    if (!panel) return;
    panel.classList.add('hidden');
    panel.innerHTML = '';
  }

  function previewItem(label, value) {
    return `<div class="draft-preview-item"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;
  }

  function buildPayload() {
    return {
      title: $('f-title').value,
      subtitle: $('f-subtitle').value,
      category: $('f-category').value,
      categoryId: $('f-category-id').value,
      secondaryCategoryId: $('f-secondary-category-id').value,
      condition: $('f-condition').value,
      conditionDescription: $('f-condition-desc').value,
      brand: $('f-brand').value,
      mpn: $('f-mpn').value,
      upc: $('f-upc').value,
      ean: $('f-ean').value,
      isbn: $('f-isbn').value,
      description: $('f-description').value,
      price: parseFloat($('f-price').value) || 0,
      quantity: parseInt($('f-quantity').value, 10) || 1,
      quantityLimitPerBuyer: parseInt($('f-qty-limit').value, 10) || null,
      bestOfferEnabled: $('f-best-offer').checked,
      autoAcceptPrice: $('f-best-offer').checked ? parseFloat($('f-auto-accept').value) || null : null,
      autoDeclinePrice: $('f-best-offer').checked ? parseFloat($('f-auto-decline').value) || null : null,
      itemLocationPostalCode: $('f-location-zip').value,
      itemLocationCountry: $('f-location-country').value || 'US',
      packageType: $('f-package-type').value,
      weightLbs: parseFloat($('f-weight-lbs').value) || 0,
      weightOz: parseFloat($('f-weight-oz').value) || 0,
      packageLengthIn: parseFloat($('f-length').value) || 0,
      packageWidthIn: parseFloat($('f-width').value) || 0,
      packageHeightIn: parseFloat($('f-height').value) || 0,
      handlingTimeBusinessDays: parseInt($('f-handling-time').value, 10) || 1,
      privateListing: $('f-private').checked,
      charityDonationPercentage: parseInt($('f-charity-pct').value, 10) || 0,
      charityId: $('f-charity-id').value,
      listingFormat: $('f-format').value,
      durationDays: parseInt($('f-duration').value, 10) || 7,
      itemSpecifics: collectSpecifics(),
      imageUrls: collectPhotoUrls(),
    };
  }

  function listingSearchText(listing) {
    return [
      listing.title,
      listing.sku,
      listing.listingId,
      listing.status,
      listingCategory(listing),
      listingUpdated(listing)
    ].join(' ').toLowerCase();
  }

  function listingCategory(listing) {
    return listing.category || listing.categoryId || listing.data?.category || listing.data?.categoryId || '-';
  }

  function listingUpdated(listing) {
    const value = listing.lastUpdated || listing.updatedAt || listing.data?.lastUpdated || '';
    if (!value) return 'Not synced';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString();
  }

  function displayStatus(status) {
    if (!status) return 'Unknown';
    if (status.toUpperCase() === 'PUBLISHED') return 'Live';
    if (status.toUpperCase() === 'ACTIVE') return 'Active';
    if (status.toUpperCase() === 'SAMPLE') return 'Sample';
    if (status.toUpperCase() === 'LOCAL_EDIT') return 'Local edit';
    return status.replaceAll('_', ' ');
  }

  function statusClass(status) {
    const upper = (status || '').toUpperCase();
    if (upper === 'PUBLISHED' || upper === 'ACTIVE') return 'status-chip live';
    if (upper === 'SAMPLE') return 'status-chip sample';
    if (upper === 'LOCAL_EDIT') return 'status-chip local';
    if (!upper || upper === 'DRAFT') return 'status-chip review';
    return 'status-chip';
  }

  function money(value) {
    const number = parseFloat(value) || 0;
    return number.toLocaleString(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });
  }

  // money() rounds to whole dollars, which is fine for sold comps but would misstate a
  // fee-adjusted cross-listing price by up to 50 cents — the cents are the whole point there.
  function moneyExact(value) {
    const number = parseFloat(value) || 0;
    return number.toLocaleString(undefined, {
      style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2,
    });
  }

  function updateCharCount(inputId, countId, max) {
    if (!$(inputId) || !$(countId)) return;
    const len = $(inputId).value.length;
    $(countId).textContent = `${len} / ${max}`;
    $(countId).style.color = len > max * .9 ? 'var(--danger)' : '';
  }

  function showResult(type, html) {
    const el = $('result');
    if (!el) return;
    el.className = type;
    el.innerHTML = html;
    el.classList.remove('hidden');
    el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  function hideResult() {
    $('result')?.classList.add('hidden');
  }

  function setVal(id, val) {
    const el = $(id);
    if (el) el.value = val;
  }

  function applyImageGenModeVisibility(mode) {
    const fields = $('s-local-sd-fields');
    if (fields) fields.style.display = (mode === 'local_sd') ? '' : 'none';
  }

  function applyComfyUiModelVisibility(backend) {
    const field = $('s-comfyui-model-field');
    if (field) field.style.display = (backend === 'comfyui') ? '' : 'none';
  }

  async function safePost(url, payload) {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (res.status === 402) {
      const b = await res.clone().json().catch(() => ({}));
      if (b.error === 'trial_expired') { throw new Error(b.message || 'Error.'); }
    }
    const text = await res.text();
    let body;
    try { body = JSON.parse(text); }
    catch { body = { ok: false, error: text, details: text }; }
    return { res, body };
  }

  function setValue(id, val) {
    const el = $(id);
    if (el && val) el.value = val;
  }

  // For dynamic <select> elements whose options may not be loaded yet —
  // adds the saved value as an option so it's preserved until the full list loads.
  function setModelSelect(id, val) {
    const sel = $(id);
    if (!sel || !val) return;
    if (![...sel.options].some(o => o.value === val)) {
      const opt = document.createElement('option');
      opt.value = val; opt.textContent = val;
      sel.appendChild(opt);
    }
    sel.value = val;
  }

  function set(id, val) {
    const el = $(id);
    if (el) el.value = val;
  }

  function setText(id, val) {
    const el = $(id);
    if (el) el.textContent = val;
  }

  function on(id, event, handler) {
    $(id)?.addEventListener(event, handler);
  }

  function $(id) {
    return document.getElementById(id);
  }

  function showLightbox(url) {
    let lb = document.getElementById('img-lightbox');
    if (!lb) {
      lb = document.createElement('div');
      lb.id = 'img-lightbox';
      lb.style.cssText = 'position:fixed;inset:0;z-index:9999;background:rgba(0,0,0,.88);display:flex;align-items:center;justify-content:center;cursor:zoom-out';
      lb.innerHTML = '<img id="img-lightbox-img" style="max-width:90vw;max-height:90vh;border-radius:8px;box-shadow:0 8px 40px #000" />';
      lb.addEventListener('click', () => lb.remove());
      document.addEventListener('keydown', e => { if (e.key === 'Escape') lb.remove(); }, { once: true });
      document.body.appendChild(lb);
    }
    document.getElementById('img-lightbox-img').src = url;
    lb.style.display = 'flex';
  }

  function esc(s) {
    return String(s ?? '')
      .replace(/&/g, '&amp;')
      .replace(/"/g, '&quot;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;');
  }

  // ── Empty / loading / error states ──────────────────────────────────────
  // Every data view in the app has the same three non-happy moments, and each
  // one used to be a sentence in a grey bar — which reads as a defect whether
  // it is one or not. These build the one state block the CSS styles, so a
  // view that has nothing to show still looks deliberate and, where an action
  // would fix it, offers that action instead of describing it.

  /**
   * @param {{variant?: 'empty'|'error'|'success', icon?: string, title: string,
   *          body?: string, detail?: string, actions?: {label: string, id?: string,
   *          kind?: string, href?: string}[], hint?: string}} opts
   */
  function stateBlockHtml(opts) {
    const variant = opts.variant || 'empty';
    const cls = variant === 'empty' ? 'state' : `state state--${variant}`;
    const icon = opts.icon
      ? `<svg aria-hidden="true"><use href="#${esc(opts.icon)}"/></svg>`
      : `<svg aria-hidden="true"><use href="#${variant === 'error' ? 'i-alert' : 'i-inbox'}"/></svg>`;

    const actions = (opts.actions || []).map(a => {
      const kind = a.kind || 'btn-secondary';
      return a.href
        ? `<a class="btn ${esc(kind)}" href="${esc(a.href)}" target="_blank" rel="noopener noreferrer">${esc(a.label)}</a>`
        : `<button class="btn ${esc(kind)}" type="button"${a.id ? ` data-state-action="${esc(a.id)}"` : ''}>${esc(a.label)}</button>`;
    }).join('');

    return `
      <div class="${cls}${opts.compact ? ' state--compact' : ''}${opts.inline ? ' state--inline' : ''}">
        <div class="state-icon">${icon}</div>
        <div class="state-title">${esc(opts.title)}</div>
        ${opts.body ? `<p class="state-body">${esc(opts.body)}</p>` : ''}
        ${opts.detail ? `<div class="state-detail">${esc(opts.detail)}</div>` : ''}
        ${actions ? `<div class="state-actions">${actions}</div>` : ''}
        ${opts.hint ? `<p class="state-hint">${esc(opts.hint)}</p>` : ''}
      </div>`;
  }

  /**
   * Renders a state block into a container and wires its buttons. `handlers`
   * maps the action ids used above onto functions — declared next to the
   * state so the copy and the thing it promises can't drift apart.
   */
  function renderState(container, opts, handlers = {}) {
    const el = typeof container === 'string' ? $(container) : container;
    if (!el) return;
    el.innerHTML = stateBlockHtml(opts);
    el.querySelectorAll('[data-state-action]').forEach(btn => {
      const fn = handlers[btn.dataset.stateAction];
      if (fn) btn.addEventListener('click', fn);
    });
  }

  /** Card-shaped placeholders sized like the real listing cards they replace. */
  function skeletonCardsHtml(count = 8) {
    const one = `
      <div class="skeleton-card" aria-hidden="true">
        <div class="skeleton skeleton-media"></div>
        <div class="skeleton skeleton-line tall w-90"></div>
        <div class="skeleton skeleton-line w-50"></div>
        <div class="skeleton-meta">
          <div class="skeleton skeleton-line"></div>
          <div class="skeleton skeleton-line"></div>
          <div class="skeleton skeleton-line w-70"></div>
          <div class="skeleton skeleton-line w-40"></div>
        </div>
      </div>`;
    return one.repeat(count);
  }

  function skeletonRowsHtml(count = 6) {
    const one = `
      <div class="skeleton-row" aria-hidden="true">
        <div class="skeleton skeleton-avatar"></div>
        <div class="skeleton-fill">
          <div class="skeleton skeleton-line w-70" style="margin-bottom:8px"></div>
          <div class="skeleton skeleton-line w-40"></div>
        </div>
      </div>`;
    return one.repeat(count);
  }

  /** The listings bar reports counts, progress and failures; each reads differently. */
  function setListingsFeedback(text, tone = 'info') {
    const el = $('listings-feedback');
    if (!el) return;
    el.textContent = text;
    el.classList.toggle('is-error', tone === 'error');
    el.classList.toggle('is-busy', tone === 'busy');
  }

  // ── Photo Editor (opens in new window) ───────────────────────────────────

  function initPhotoEditorPaste() {
    // Paste image from clipboard into next empty slot
    document.addEventListener('paste', e => {
      const tag = document.activeElement?.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || document.activeElement?.isContentEditable) return;
      const items = e.clipboardData?.items;
      if (!items) return;
      for (const item of items) {
        if (item.type.startsWith('image/')) {
          const file = item.getAsFile();
          if (!file) continue;
          const idx = nextEmptySlotIndex();
          if (idx === -1) return;
          nlLoadFileIntoSlot(file, idx);
          addActivity('Photo pasted', `Picture ${idx + 1}`);
          e.preventDefault();
          return;
        }
      }
    });
    // Receive saved image back from the editor window
    window.addEventListener('message', e => {
      if (e.data?.type === 'photo-editor-save') {
        setPhotoSlotUrl(e.data.slotIndex, e.data.url);
        addActivity('Photo edited', `Picture ${e.data.slotIndex + 1}`);
      }
    });
  }

  function openPhotoEditor(slotIndex) {
    const slot = getPhotoSlot(slotIndex);
    if (!slot?.classList.contains('has-image')) return;
    const imgUrl = slot.dataset.url;
    const label  = `Picture ${slotIndex + 1}`;

    // Full-screen iframe overlay — no popup blocker issues
    const overlay = document.createElement('div');
    overlay.style.cssText = 'position:fixed;inset:0;z-index:9999;background:#0d1117;';

    const iframe = document.createElement('iframe');
    iframe.src = '/editor.html';
    iframe.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;border:none;';
    overlay.appendChild(iframe);
    document.body.appendChild(overlay);

    const handler = e => {
      if (e.data?.type === 'editor-ready') {
        iframe.contentWindow?.postMessage(
          { type: 'load-image', url: imgUrl, slotIndex, label }, '*'
        );
      }
      if (e.data?.type === 'photo-editor-save') {
        window.removeEventListener('message', handler);
        setPhotoSlotUrl(e.data.slotIndex, e.data.url);
        addActivity('Photo edited', `Picture ${e.data.slotIndex + 1}`);
        overlay.remove();
      }
      if (e.data?.type === 'photo-editor-cancel') {
        window.removeEventListener('message', handler);
        overlay.remove();
      }
    };
    window.addEventListener('message', handler);
  }

})();
