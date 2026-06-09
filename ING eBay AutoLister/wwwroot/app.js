(() => {
  let nlImageBase64 = '';
  let nlMimeType = 'image/jpeg';
  let ebayToken = '';
  let activeOfferId = '';
  let activeListingId = '';
  let activeSku = '';
  let pendingDraftPayload = null;
  let cachedListings = [];
  let cachedPolicies = null; // { fulfillmentPolicies, paymentPolicies, returnPolicies }
  let viewMode = 'cards';
  let isConnected = false;

  document.addEventListener('DOMContentLoaded', init);

  async function guardedFetch(url, opts) {
    return fetch(url, opts);
  }

  async function init() {
    initPhotoGrid();          // render 6 photo slots on page load, not just on modal open
    initPhotoEditorPaste();
    bindDashboard();
    bindSetup();
    bindNewListingModal();
    bindImageGenSetup();
    bindPgImggen();
    bindForm();
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

    on('btn-card-view', 'click', () => setViewMode('cards'));
    on('btn-table-view', 'click', () => setViewMode('table'));

    document.querySelectorAll('.nav-item').forEach(btn => {
      btn.addEventListener('click', () => handleNav(btn.dataset.page || 'dashboard'));
    });
  }

  function handleNav(page) {
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === page));
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
    showDashboard();
    if (page === 'listings') $('listings-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    if (page === 'activity') $('activity-list')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }

  function showAiSection() {
    openNewListingModal();
  }

  function showDashboard() {
    $('dashboard-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'dashboard'));
  }

  async function showSettingsSection() {
    $('settings-section')?.classList.remove('hidden');
    $('settings-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'settings'));
    await loadSettingsStatus();
  }

  async function openSetupWithPolicies(status) {
    openSetup(status);
    if (isConnected) loadPolicies(false);
  }

  async function showLogsSection() {
    $('logs-section')?.classList.remove('hidden');
    $('logs-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'logs'));
    await loadLogs();
  }

  async function showLicenseSection() {
    $('license-section')?.classList.remove('hidden');
    $('license-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'license'));
    const status = await fetch('/api/license/status').then(r => r.json()).catch(() => null);
    if (status) updateLicenseUI(status);
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
    try {
      const status = await fetch('/api/setup/status').then(r => r.json());
      populateSetupFields(status);
      if (!status.isComplete) {
        addActivity('Credentials needed', 'Open Settings to finish eBay and AI configuration.');
      }
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

      // Detect OAuth redirect URL (contains a code= parameter from ingmining.com)
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
        if (!status.isComplete) {
          openSetup(status);
          return;
        }
        const res = await fetch('/api/ebay/auth-url');
        if (!res.ok) throw new Error(await res.text());
        const { url } = await res.json();
        window.location.href = url;
      } catch (err) {
        openSetup(null);
        showResult('error', `eBay connection setup needed: ${esc(err.message)}`);
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

      if (!status.isComplete) {
        const missing = [];
        if (!status.hasAnthropicKey) missing.push('AI API Key');
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
    el.innerHTML = '<div class="log-row"><span class="log-level">Info</span><strong class="log-title">Loading</strong><span class="log-detail">Reading recent app actions...</span></div>';

    try {
      const entries = await fetch('/api/logs/recent').then(r => r.json());
      if (!Array.isArray(entries) || entries.length === 0) {
        el.innerHTML = '<div class="log-row"><span class="log-level">Info</span><strong class="log-title">No entries</strong><span class="log-detail">No recent app actions yet.</span></div>';
        return;
      }

      el.innerHTML = entries.map(logRow).join('');
    } catch (err) {
      el.innerHTML = `<div class="log-row"><span class="log-level error">Error</span><strong class="log-title">Logs unavailable</strong><span class="log-detail">${esc(err.message)}</span></div>`;
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
    const feedback = $('listings-feedback');
    if (feedback) feedback.textContent = 'Loading active eBay listings...';

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
        renderListings();
        updateStats();
        if (feedback) feedback.textContent = `Import failed: ${errorDetail}`;
        addActivity('eBay import failed', errorDetail);
        showResult('error', `eBay import failed: ${esc(errorDetail)} — Check the Logs section for details.`);
        return;
      }
      // Not yet connected — fall back to sample listings
      if (await loadPlaceholderListings('Sample listings loaded')) {
        addActivity('eBay not connected', 'Showing sample listings. Connect eBay to import real listings.');
        return;
      }
      if (feedback) feedback.textContent = errorDetail;
      addActivity('Import failed', errorDetail);
    }
  }

  async function loadPlaceholderListings(activityTitle = 'Sample listings loaded') {
    const feedback = $('listings-feedback');
    if (feedback) feedback.textContent = 'Loading sample listings...';

    try {
      const listings = await fetch('/api/local-listings/placeholder').then(r => {
        if (!r.ok) return r.text().then(t => { throw new Error(t); });
        return r.json();
      });
      cachedListings = Array.isArray(listings) ? listings : [];
      renderListings();
      updateStats();
      if (feedback) feedback.textContent = `${cachedListings.length} sample listing${cachedListings.length === 1 ? '' : 's'} shown until eBay is connected.`;
      addActivity(activityTitle, `${cachedListings.length} local sample listing${cachedListings.length === 1 ? '' : 's'} loaded.`);
      return true;
    } catch (err) {
      if (feedback) feedback.textContent = 'Connect eBay, then import listings.';
      addActivity('Sample listings unavailable', err.message || 'Unable to load local sample listings.');
      return false;
    }
  }

  function renderListings() {
    const grid = $('listings-grid');
    const tbody = $('listings-table-body');
    const feedback = $('listings-feedback');
    if (!grid || !tbody) return;

    const search = ($('global-search')?.value || '').trim().toLowerCase();
    const listings = cachedListings.filter(l => listingSearchText(l).includes(search));

    grid.innerHTML = '';
    tbody.innerHTML = '';

    if (!cachedListings.length) {
      if (feedback) feedback.textContent = isConnected
        ? 'Connected, but no active listings found. Check the Logs section for details.'
        : 'Connect eBay, then import listings.';
      return;
    }

    if (!listings.length) {
      if (feedback) feedback.textContent = 'No listings match the current search.';
      return;
    }

    if (feedback) {
      const sampleOnly = listings.every(l => (l.status || '').toUpperCase() === 'SAMPLE');
      feedback.textContent = sampleOnly
        ? `${listings.length} sample listing${listings.length === 1 ? '' : 's'} shown until eBay is connected.`
        : `${listings.length} listing${listings.length === 1 ? '' : 's'} shown.`;
    }

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

    const watchBadge = listing.watchCount > 0
      ? `<span class="watch-badge" title="Watchers">👁 ${listing.watchCount}</span>`
      : '';
    const viewLink = listing.listingUrl
      ? `<a class="view-ebay-link" href="${esc(listing.listingUrl)}" target="_blank" rel="noopener noreferrer">View on eBay</a>`
      : '';

    card.innerHTML = `
      ${img}
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
        ${watchBadge}
        ${viewLink}
        <button class="btn btn-secondary small" type="button">Edit</button>
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

  function loadListingIntoForm(listing, sourceEl) {
    document.querySelectorAll('.listing-card.active, .listings-table tr.active').forEach(c => c.classList.remove('active'));
    sourceEl?.classList?.add('active');

    activeOfferId = listing.offerId || '';
    activeListingId = listing.listingId || '';
    activeSku = listing.sku || '';
    pendingDraftPayload = null;
    hideDraftPreview();

    $('btn-post')?.classList.add('hidden');
    $('btn-create-ebay-draft')?.classList.add('hidden');
    $('btn-update')?.classList.remove('hidden');
    $('btn-revise-ebay')?.classList.toggle('hidden', !canReviseOnEbay(listing));
    $('btn-new-listing')?.classList.remove('hidden');

    const d = listing.data || {};
    fillForm(d);
    set('f-format', d.listingFormat || 'FIXED_PRICE');
    $('form-section')?.classList.remove('hidden');
    $('form-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
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
    const item = document.createElement('div');
    item.className = 'activity-item';
    item.innerHTML = `<strong>${esc(title)}</strong><span>${esc(detail)} - ${new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>`;
    list.prepend(item);
    while (list.children.length > 6) list.lastElementChild?.remove();
  }

  function updateAuthUI(connected) {
    isConnected = connected;
    if (connected) {
      setText('auth-status', 'Connected to eBay');
      $('auth-status')?.classList.remove('badge-off');
      $('auth-status')?.classList.add('badge-on');
      $('btn-paste-token')?.classList.add('hidden');
      $('btn-connect')?.classList.add('hidden');
      $('btn-disconnect')?.classList.remove('hidden');
    } else {
      setText('auth-status', 'Not connected to eBay');
      $('auth-status')?.classList.remove('badge-on');
      $('auth-status')?.classList.add('badge-off');
      $('btn-paste-token')?.classList.remove('hidden');
      $('btn-connect')?.classList.remove('hidden');
      $('btn-disconnect')?.classList.add('hidden');
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

  async function saveDraftLocal() {
    captureCurrentTab();
    const tab = draftTabs.find(t => t.id === activeDraftTabId);
    if (!tab) return;

    const btn = $('nl-btn-save-local');
    if (btn) { btn.disabled = true; btn.textContent = 'Saving…'; }

    // Ensure Desktop\eBayListing folder exists before the dialog opens
    await fetch('/api/local-drafts/ensure-folder').catch(() => {});

    const suggestedName = (tab.title || 'draft')
      .replace(/[^a-zA-Z0-9 _\-]/g, '').trim()
      .replace(/\s+/g, '_').slice(0, 60) + '.json';

    const payload = {
      filename: suggestedName,
      title: tab.title,
      savedAt: new Date().toISOString(),
      data: tab.data,
      imageBase64: tab.imageBase64 || null,
      mimeType: tab.mimeType || 'image/jpeg',
      visualDescription: tab.visualDescription || null
    };

    try {
      const handle = await window.showSaveFilePicker({
        suggestedName,
        startIn: 'desktop',
        types: [{ description: 'eBay Draft (.json)', accept: { 'application/json': ['.json'] } }]
      });

      const writable = await handle.createWritable();
      await writable.write(JSON.stringify(payload, null, 2));
      await writable.close();

      tab.fileHandle = handle;
      tab.filename   = handle.name;
      tab.saved      = true;
      addActivity('Draft saved', handle.name);
      renderDraftTabs();
      if (btn) { btn.textContent = '✓ Saved'; setTimeout(() => { if (btn) { btn.disabled = false; btn.textContent = '💾 Save Draft'; } }, 1200); }

      // Open a fresh blank tab for the next listing
      addNewDraftTab();

    } catch (err) {
      if (err.name === 'AbortError') {
        // User cancelled — just re-enable
        if (btn) { btn.disabled = false; btn.textContent = '💾 Save Draft'; }
        return;
      }
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
    dropZone?.addEventListener('dragleave', () => dropZone.classList.remove('drag-over'));
    dropZone?.addEventListener('drop', e => {
      e.preventDefault();
      dropZone.classList.remove('drag-over');
      if (e.dataTransfer.files[0]) nlLoadFile(e.dataTransfer.files[0]);
    });
    fileInput?.addEventListener('change', () => {
      if (fileInput.files[0]) nlLoadFile(fileInput.files[0]);
    });

    // Global paste — open modal and load image when not in a text field
    document.addEventListener('paste', e => {
      const imageItem = [...(e.clipboardData?.items || [])].find(i => i.type.startsWith('image/'));
      if (!imageItem) return;
      const activeTag = document.activeElement?.tagName?.toLowerCase();
      if (activeTag === 'input' || activeTag === 'textarea') return;
      const file = imageItem.getAsFile();
      if (!file) return;
      e.preventDefault();
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
        // Sync before switching away from text editor
        if (leaving === 'text') {
          const plain = $('nl-desc-text')?.value || '';
          if ($('nl-description')) $('nl-description').value = nlTextToHtml(plain);
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
    on('nl-description', 'input', () => { nlSyncDescPreview(); nlUpdateDescCount(); });
    on('nl-desc-text', 'input', nlUpdateDescCount);
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
    on('nl-title', 'input', () => nlUpdateCharCount('nl-title', 'nl-title-count', 80));
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
    on('nl-bulk-go', 'click', nlBulkImport);
    on('nl-bulk-url-input', 'keydown', e => { if (e.key === 'Enter') nlBulkImport(); });

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
    document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.page === 'dashboard'));
  }

  function nlClearAll() {
    nlClearImage();
    nlClearForm();
    nlSetResult('', '');
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
    nlClearAllPhotoSlots();
    // Reset description to edit tab
    document.querySelectorAll('.desc-tab').forEach(t => t.classList.toggle('active', t.dataset.descTab === 'edit'));
    $('nl-desc-edit-wrap')?.classList.remove('hidden');
    $('nl-desc-text-wrap')?.classList.add('hidden');
    $('nl-desc-preview-wrap')?.classList.add('hidden');
    if ($('nl-desc-text')) $('nl-desc-text').value = '';
    if ($('nl-desc-preview')) $('nl-desc-preview').innerHTML = '';
    nlUpdateCharCount('nl-title', 'nl-title-count', 80);
    nlUpdateCharCount('nl-subtitle', 'nl-subtitle-count', 55);
    nlUpdateDescCount();
    if ($('nl-cat-selected')) $('nl-cat-selected').hidden = true;
  }

  function nlLoadFile(file, label = file.name || 'Product photo') {
    if (!file.type.startsWith('image/')) return;
    nlMimeType = file.type;
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

    function showSelected(name, id) {
      input.value = '';
      input.placeholder = 'Search to change category…';
      hidden.value  = id;
      if (nameEl) nameEl.textContent = name;
      if (badge)  badge.textContent  = 'ID: ' + id;
      if (selected) selected.hidden = false;
      closeDropdown();
    }

    function clearSelection() {
      hidden.value = '';
      input.value = '';
      input.placeholder = 'Type to search eBay categories…';
      if (selected) selected.hidden = true;
      input.focus();
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

  async function nlAnalyze() {
    if (!nlImageBase64) return;

    $('nl-ai-status')?.classList.remove('hidden');
    $('nl-ai-done')?.classList.add('hidden');
    $('nl-ai-error')?.classList.add('hidden');
    if ($('nl-ai-msg')) $('nl-ai-msg').textContent = 'Analyzing with AI…';

    try {
      const res = await guardedFetch('/api/analyze', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageBase64: nlImageBase64, mimeType: nlMimeType })
      });
      if (!res.ok) throw new Error(await res.text());
      const data = await res.json();
      fillNlForm(data);
      window._nlVisualDescription = data.visualDescription || '';
      window._nlImageType = data.imageType || 'webpage_screenshot';

      // Reset photo slots before populating from analysis result
      nlClearAllPhotoSlots();

      const isProductPhoto = (data.imageType || '') === 'product_photo';
      let firstPhotoUrl = null;

      if (isProductPhoto && nlImageBase64) {
        // Clean product photo — save the uploaded image directly
        try {
          const res = await fetch('/api/photos/save-uploaded', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ imageBase64: nlImageBase64, mimeType: nlMimeType || 'image/jpeg' })
          });
          if (res.ok) { const { url } = await res.json(); firstPhotoUrl = url; nlAddPhotoRow(url); }
        } catch (e) { /* non-fatal */ }
      } else if ((data.imageUrls || []).length > 0) {
        // Only use the first product image — one clean photo is all we need
        const firstUrl = (data.imageUrls || []).find(u => u && (u.startsWith('http') || u.startsWith('/')));
        if (firstUrl) {
          if (firstUrl.startsWith('/')) {
            firstPhotoUrl = window.location.origin + firstUrl;
          } else {
            try {
              const res = await fetch('/api/photos/fetch-url', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ url: firstUrl })
              });
              if (res.ok) { const { url } = await res.json(); firstPhotoUrl = window.location.origin + url; }
            } catch (e) { /* non-fatal */ }
          }
        }
      }

      // Update the active tab title
      const activeTab = draftTabs.find(t => t.id === activeDraftTabId);
      if (activeTab) { activeTab.title = data.title || 'New Draft'; activeTab.saved = false; renderDraftTabs(); }
      $('nl-ai-status')?.classList.add('hidden');
      $('nl-ai-done')?.classList.remove('hidden');
      addActivity('AI analysis complete', data.title || 'Form filled — review before publishing.');

      // Auto remove background — use first fetched photo, or fall back to the uploaded image directly
      if (firstPhotoUrl) nlAutoRemoveBg(firstPhotoUrl);
      else if (nlImageBase64) nlAutoRemoveBgFromBase64(nlImageBase64, nlMimeType || 'image/jpeg');
    } catch (err) {
      $('nl-ai-status')?.classList.add('hidden');
      $('nl-ai-error')?.classList.remove('hidden');
      if ($('nl-ai-error-msg')) $('nl-ai-error-msg').textContent = `AI analysis failed: ${err.message}`;
      addActivity('AI analysis failed', err.message);
    }
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
    document.querySelectorAll('#nl-specifics-list .specific-row').forEach(row => {
      const [k, v] = row.querySelectorAll('input');
      if (k?.value.trim()) out[k.value.trim()] = v?.value.trim() || '';
    });
    return out;
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

  function nlSetResult(type, text) {
    const el = $('nl-result-msg');
    if (!el) return;
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

      const res = await fetch('/api/improve-seo', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) {
        const b = await res.json().catch(() => ({}));
        throw new Error(b.error || 'SEO improvement failed');
      }
      const data = await res.json();
      fillNlForm(data);
      $('nl-ai-status')?.classList.add('hidden');
      $('nl-ai-done')?.classList.remove('hidden');
      addActivity('SEO improved', data.title || 'Title and description updated');
    } catch (err) {
      $('nl-ai-status')?.classList.add('hidden');
      $('nl-ai-error')?.classList.remove('hidden');
      if ($('nl-ai-error-msg')) $('nl-ai-error-msg').textContent = 'SEO improvement failed: ' + err.message;
      addActivity('SEO improvement failed', err.message);
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'Improve SEO + Description'; }
    }
  }

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

    const publishBtn = $('nl-btn-publish');
    const draftBtn = $('nl-btn-draft');
    if (publishBtn) publishBtn.disabled = true;
    if (draftBtn) draftBtn.disabled = true;

    const endpoint = mode === 'publish' ? '/api/listing/publish' : '/api/listing/post';
    nlSetResult('', mode === 'publish' ? 'Publishing to eBay…' : 'Saving draft…');

    try {
      // Upload photos to eBay EPS before publishing so eBay has accessible URLs
      if (mode === 'publish' && payload.imageUrls && payload.imageUrls.length > 0) {
        payload.imageUrls = await uploadPhotosToEbay(payload.imageUrls);
      }

      const { res, body } = await safePost(endpoint, payload);

      if (!res.ok) {
        const short   = body.error   || 'Request failed';
        const details = body.details || short;
        const where   = body.where   ? ' [' + body.where + ']' : '';
        nlSetResult('error',
          'Failed' + where + ': ' + short
          + (details !== short ? '\n\nDetails: ' + details : ''));
        addActivity(mode === 'publish' ? 'Publish failed' : 'Save draft failed',
          'HTTP ' + res.status + ': ' + short);
        return;
      }

      if (mode === 'publish') {
        const link = body.listingUrl
          ? ' <a href="' + esc(body.listingUrl) + '" target="_blank" rel="noopener noreferrer">View on eBay</a>'
          : '';
        nlSetResult('success', '✓ Published live! Listing ID: ' + (body.listingId || '-') + (link ? ' —' : ''));
        $('nl-result-msg').innerHTML += link;
        addActivity('Listing published live', 'ID: ' + (body.listingId || '-') + '; Offer: ' + (body.offerId || '-'));
        loadListings('Listings refreshed after publish');
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
      nlSetResult('error', 'Unexpected error: ' + err.message);
      addActivity(mode === 'publish' ? 'Publish failed' : 'Save draft failed', err.message);
    } finally {
      if (publishBtn) publishBtn.disabled = false;
      if (draftBtn) draftBtn.disabled = false;
    }
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
    on('pg-imggen-guide', 'click', openImageGenSetup);
    on('pg-imggen-load-models', 'click', () => {
      const endpoint = $('pg-imggen-endpoint')?.value.trim();
      loadComfyModels(endpoint, 'pg-imggen-model', 'pg-imggen-msg');
    });
    on('pg-defaults-save', 'click', saveListingDefaults);
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
      pendingDraftPayload = null;
      hideDraftPreview();
      document.querySelectorAll('.listing-card.active').forEach(c => c.classList.remove('active'));
      $('btn-post')?.classList.remove('hidden');
      $('btn-create-ebay-draft')?.classList.add('hidden');
      $('btn-update')?.classList.add('hidden');
      $('btn-revise-ebay')?.classList.add('hidden');
      $('btn-new-listing')?.classList.add('hidden');
      $('form-section')?.classList.add('hidden');
      showAiSection();
      hideResult();
    });

    on('btn-update', 'click', async () => {
      const ok = window.confirm('Save these changes locally? This will not revise the live eBay listing.');
      if (!ok) return;

      const btn = $('btn-update');
      btn.disabled = true;
      btn.textContent = 'Saving locally...';
      hideResult();

      const payload = buildPayload();
      payload.offerId = activeOfferId;
      payload.listingId = activeListingId;
      payload.sku = activeSku;

      try {
        const res = await fetch('/api/local-listings/save-edit', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });
        if (!res.ok) throw new Error(await res.text());
        const result = await res.json();
        applyLocalEdit(payload, result.savedAt);
        showResult('success', 'Listing changes saved locally. No live eBay listing was revised.');
        addActivity('Local edit saved', payload.title || activeSku);
      } catch (err) {
        showResult('error', `Local save failed: ${esc(err.message)}`);
        addActivity('Local save failed', err.message);
      } finally {
        btn.disabled = false;
        btn.textContent = 'Save Changes';
      }
    });

    on('btn-revise-ebay', 'click', async () => {
      const ok = window.confirm('Manual confirmation required: revise the live eBay listing now using the current form values?');
      if (!ok) return;

      const btn = $('btn-revise-ebay');
      btn.disabled = true;
      btn.textContent = 'Revising eBay...';
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
        showResult('success', 'eBay listing revised after manual confirmation.');
        addActivity('eBay listing revised', payload.title || activeSku);
        loadListings('Listings refreshed after eBay revision');
      } catch (err) {
        showResult('error', `eBay revision failed: ${esc(err.message)}`);
        addActivity('eBay revision failed', err.message);
      } finally {
        btn.disabled = false;
        btn.textContent = 'Revise eBay';
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

  function canReviseOnEbay(listing) {
    if (!listing.offerId || !listing.sku) return false;
    return (listing.status || '').toUpperCase() !== 'SAMPLE';
  }

  function money(value) {
    const number = parseFloat(value) || 0;
    return number.toLocaleString(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });
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
