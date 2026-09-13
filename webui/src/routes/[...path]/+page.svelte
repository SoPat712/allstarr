<script lang="ts">
  import { onMount, type Component } from "svelte";
  import { page } from "$app/state";
  import { auth, onboarding, type OnboardingState, type Session } from "$lib/api";
  import { liveUpdates } from "$lib/live-updates.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import UiIcon from "$lib/components/UiIcon.svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { applyThemeMode, onThemeModeChange, readThemeMode, saveThemeMode, type ThemeMode } from "$lib/theme";
  import { Monitor, Moon, PanelLeftClose, PanelLeftOpen, Sun, X } from "@lucide/svelte";

  const destinations = [
    { href: "#/", label: "Home", mobileLabel: "Home", icon: "home", mobile: true },
    { href: "#/library/playlists", prefix: "/library/", label: "Library", mobileLabel: "Library", icon: "library", mobile: true },
    { href: "#/intelligence", label: "Intelligence", mobileLabel: "Insights", icon: "headphones", mobile: true },
    { href: "#/integrations/services", prefix: "/integrations/", label: "Integrations", mobileLabel: "Integrations", icon: "sources", mobile: false },
    { href: "#/activity", label: "Activity", mobileLabel: "Activity", icon: "activity", mobile: true },
    { href: "#/settings", label: "Settings", mobileLabel: "Settings", icon: "settings", mobile: false },
  ];
  // Keep existing Intelligence deep links usable while deferring its release navigation.
  const navigationDestinations = destinations.filter((item) => item.href !== "#/intelligence");
  const mobileDestinations = navigationDestinations.filter((item) => item.mobile);
  const moreDestinations = navigationDestinations.filter((item) => !item.mobile);
  const librarySections = [
    { id: "playlists", label: "Playlists", href: "#/library/playlists" },
    { id: "mappings", label: "Mappings", href: "#/library/mappings" },
    { id: "cached", label: "Cached", href: "#/library/cached" },
    { id: "kept", label: "Kept", href: "#/library/kept" },
  ];
  const bootMessages = [
    "Checking your session",
    "Loading account access",
    "Preparing your music control center",
  ];

  let session = $state<Session | null>(null);
  let loading = $state(true);
  let bootstrapFailed = $state(false);
  let bootMessageIndex = $state(0);
  let error = $state("");
  let username = $state("");
  let password = $state("");
  let rememberMe = $state(true);
  let authBusy = $state(false);
  let authError = $state("");
  let avatarFailed = $state(false);
  let onboardingState = $state<OnboardingState | null>(null);
  let onboardingOpen = $state(false);
  let onboardingError = $state("");
  let OnboardingDialog = $state<Component<any>>();
  let sidebarSlim = $state(false);
  let mobileMenuOpen = $state(false);
  let themeMode = $state<ThemeMode>("system");
  let ActiveView = $state<Component<any>>();
  let loadedRoute = $state("");
  let loadedViewKey = $state("");
  let viewError = $state("");
  let viewRequest = 0;

  function currentRoute(path: string) {
    if (path === "/home") return "/";
    if (["/library", "/library/link", "/library/injected", "/library/external"].includes(path)) {
      return "/library/playlists";
    }
    if (["/library/missing", "/library/migration"].includes(path)) return "/library/mappings";
    if (path === "/sources") return "/integrations/services";
    if (path === "/settings/accounts") return "/integrations/accounts";
    if (path === "/settings/extensions") return "/integrations/extensions";
    if (path === "/settings/routing") return "/integrations/routing";
    return path;
  }

  const route = $derived(currentRoute(`/${page.params.path ?? ""}`));
  const routeQuery = $derived(new URLSearchParams(page.url.hash.split("?", 2)[1] ?? ""));
  const activeDestination = $derived(
    destinations.find((item) =>
      item.href === "#/" ? route === "/" : route.startsWith(item.prefix ?? item.href.slice(1)),
    ) ?? destinations[0],
  );
  const moreDestinationActive = $derived(!activeDestination.mobile);
  const nextTheme = $derived<ThemeMode>(themeMode === "system" ? "light" : themeMode === "light" ? "dark" : "system");
  const initials = $derived(
    session?.user?.name
      ?.split(/\s+/)
      .map((part) => part[0])
      .join("")
      .slice(0, 2)
      .toUpperCase() ?? "?",
  );
  const activeProps = $derived(
    route === "/"
      ? { administrator: session?.user?.isAdministrator ?? false }
      : route === "/library/playlists"
        ? { initialId: routeQuery.get("playlist") ?? "" }
        : route === "/library/mappings"
          ? {
              initialSearch: routeQuery.get("search") ?? "",
              initialReview: routeQuery.get("review") ?? "",
            }
          : route === "/library/cached"
            ? { storage: "cache" }
            : route === "/library/kept"
              ? { storage: "kept" }
              : route === "/intelligence"
                ? {
                    initialSection: routeQuery.get("section") ?? "overview",
                    administrator: session?.user?.isAdministrator ?? false,
                  }
                : route.startsWith("/integrations")
                  ? {
                      section: route.split("/")[2] || "services",
                      administrator: session?.user?.isAdministrator ?? false,
                      initialSource: routeQuery.get("source") ?? "",
                      initialSection: routeQuery.get("section") ?? "data",
                      initialConnect: routeQuery.get("connect") === "1",
                    }
                  : route.startsWith("/settings")
                    ? {
                        section: route.split("/")[2] || "general",
                        initialPanel: routeQuery.get("provider") ?? "",
                        administrator: session?.user?.isAdministrator ?? false,
                        onOpenSetup: reopenSetup,
                      }
                    : {},
  );

  async function loadOnboarding(current: Session) {
    onboardingState = null;
    onboardingError = "";
    if (!current.user?.isAdministrator) return;
    try {
      onboardingState = await onboarding.status();
      onboardingOpen = onboardingState.shouldRedirectToSetup || onboardingState.setupOpen;
    } catch (cause) {
      onboardingError = cause instanceof Error ? cause.message : "Setup status is unavailable.";
    }
  }

  async function reopenSetup() {
    if (!session?.user?.isAdministrator) return;
    try {
      onboardingState = await onboarding.reopen();
      onboardingOpen = true;
      onboardingError = "";
    } catch (cause) {
      onboardingError = cause instanceof Error ? cause.message : "Setup could not be reopened.";
    }
  }

  function cycleTheme() {
    themeMode = nextTheme;
    saveThemeMode(themeMode);
  }

  function viewLoader(path: string) {
    if (path === "/") return import("$lib/components/HomeView.svelte");
    if (path === "/library/playlists") return import("$lib/components/PlaylistsView.svelte");
    if (path === "/library/mappings") return import("$lib/components/MappingView.svelte");
    if (path === "/library/cached" || path === "/library/kept") {
      return import("$lib/components/DownloadsView.svelte");
    }
    if (path === "/activity") return import("$lib/components/EventLogView.svelte");
    if (path === "/intelligence") return import("$lib/components/IntelligenceView.svelte");
    if (path.startsWith("/integrations")) return import("$lib/components/IntegrationsView.svelte");
    if (path.startsWith("/settings")) return import("$lib/components/SettingsView.svelte");
  }

  const viewKey = (path: string) => path.startsWith("/settings") ? "/settings"
    : path.startsWith("/integrations") ? "/integrations" : path;

  $effect(() => {
    route;
    mobileMenuOpen = false;
  });

  $effect(() => {
    const path = route;
    const loader = viewLoader(path);
    const key = viewKey(path);
    if (ActiveView && loadedViewKey === key) {
      loadedRoute = path;
      return;
    }
    const request = ++viewRequest;
    ActiveView = undefined;
    loadedRoute = loader ? "" : path;
    viewError = "";

    void loader
      ?.then(({ default: view }) => {
        if (request === viewRequest) {
          ActiveView = view;
          loadedViewKey = key;
          loadedRoute = path;
        }
      })
      .catch((cause) => {
        if (request === viewRequest) {
          viewError = cause instanceof Error ? cause.message : "This view could not be loaded.";
          loadedRoute = path;
        }
      });
  });

  $effect(() => {
    if (onboardingOpen && !OnboardingDialog) {
      void import("$lib/components/OnboardingDialog.svelte").then(({ default: dialog }) => {
        OnboardingDialog = dialog;
      });
    }
  });

  $effect(() => {
    if (!loading) return;
    const timer = window.setInterval(() => {
      bootMessageIndex = (bootMessageIndex + 1) % bootMessages.length;
    }, 1_400);
    return () => window.clearInterval(timer);
  });

  async function bootstrap() {
    loading = true;
    bootstrapFailed = false;
    error = "";
    bootMessageIndex = 0;
    try {
      session = await auth.session();
      if (session.authenticated) {
        await loadOnboarding(session);
        liveUpdates.connect();
      }
    } catch (cause) {
      bootstrapFailed = true;
      error = cause instanceof Error ? cause.message : "Allstarr is unavailable.";
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    const compactSidebar = matchMedia("(min-width: 761px) and (max-width: 900px)");
    const colorScheme = matchMedia("(prefers-color-scheme: dark)");
    themeMode = readThemeMode();
    const applySidebarBreakpoint = () => { sidebarSlim = compactSidebar.matches; };
    const applySystemTheme = () => { if (themeMode === "system") applyThemeMode(themeMode); };
    const unsubscribeTheme = onThemeModeChange((mode) => { themeMode = mode; });
    applySidebarBreakpoint();
    applyThemeMode(themeMode);
    compactSidebar.addEventListener("change", applySidebarBreakpoint);
    colorScheme.addEventListener("change", applySystemTheme);

    void bootstrap();

    return () => {
      compactSidebar.removeEventListener("change", applySidebarBreakpoint);
      colorScheme.removeEventListener("change", applySystemTheme);
      unsubscribeTheme();
      liveUpdates.close();
    };
  });

  async function login() {
    if (authBusy) return;
    authBusy = true;
    error = "";
    try {
      session = await auth.login(username, password, rememberMe);
      avatarFailed = false;
      password = "";
      await loadOnboarding(session);
      liveUpdates.connect();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Sign in failed.";
    } finally {
      authBusy = false;
    }
  }

  async function logout() {
    if (authBusy) return;
    authBusy = true;
    authError = "";
    try {
      await auth.logout();
      liveUpdates.close();
      onboardingState = null;
      onboardingOpen = false;
      session = await auth.session();
    } catch (cause) {
      authError = cause instanceof Error ? cause.message : "You are still signed in. Try again.";
    } finally {
      authBusy = false;
    }
  }
</script>

<svelte:head>
  <title>{activeDestination.label} · Allstarr</title>
  <meta
    name="description"
    content="Provider-neutral music streaming and playlist management."
  />
</svelte:head>

{#if loading}
  <main class="signal-boot" aria-busy="true">
    <div class="signal-boot-grid" aria-hidden="true"></div>
    <div class="signal-boot-console">
      <div class="signal-boot-mark" aria-hidden="true">
        <span class="signal-boot-core">A</span>
        <span class="signal-boot-orbit"></span>
        <span class="signal-boot-pulse"></span>
      </div>
      <p class="signal-boot-eyebrow">Allstarr signal boot</p>
      <h1>Bringing your music universe online</h1>
      <div class="signal-boot-meter" aria-hidden="true">
        {#each Array(9) as _}<span></span>{/each}
      </div>
      <p class="signal-boot-status" aria-hidden="true">{bootMessages[bootMessageIndex]}</p>
      <small aria-hidden="true">Providers · Library · Playback</small>
      <p class="sr-only" role="status">Loading Allstarr. Preparing your music control center.</p>
    </div>
  </main>
{:else if bootstrapFailed}
  <main class="grid min-h-screen place-items-center p-6">
    <section class="panel bootstrap-error w-full max-w-sm p-6 sm:p-8" aria-labelledby="bootstrap-error-title">
      <div class="brand-mark mb-6" aria-hidden="true">A</div>
      <p class="eyebrow">Connection interrupted</p>
      <h1 id="bootstrap-error-title" class="mt-2 text-3xl font-semibold tracking-tight">Allstarr could not start.</h1>
      <p class="notice-error mt-4" role="alert">{error}</p>
      <button class="auth-submit mt-6 w-full" type="button" onclick={() => void bootstrap()}>Try again</button>
    </section>
  </main>
{:else if !session?.authenticated}
  <main class="grid min-h-screen place-items-center p-6">
    <section class="panel w-full max-w-sm p-6 sm:p-8" aria-labelledby="login-title">
      <div class="brand-mark mb-6" aria-hidden="true">A</div>
      <p class="eyebrow">Allstarr</p>
      <h1 id="login-title" class="mt-2 text-3xl font-semibold tracking-tight">Your music, connected.</h1>
      <p class="mt-3 text-sm leading-6 text-ink-muted">
        Sign in with your {session?.backend ?? "media server"} account.
      </p>

      <form class="mt-8 space-y-4" aria-busy={authBusy} onsubmit={(event) => { event.preventDefault(); void login(); }}>
        <label class="field">
          <span>Username</span>
          <input bind:value={username} autocomplete="username" required disabled={authBusy} />
        </label>
        <label class="field">
          <span>Password</span>
          <input bind:value={password} type="password" autocomplete="current-password" required disabled={authBusy} />
        </label>
        <label class="flex min-h-11 items-center gap-3 text-sm text-ink-muted">
          <input bind:checked={rememberMe} type="checkbox" class="size-4 accent-signal" disabled={authBusy} />
          Keep me signed in
        </label>
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
        <button class="auth-submit w-full" type="submit" disabled={authBusy}>{authBusy ? "Signing in…" : "Sign in"}</button>
      </form>
    </section>
  </main>
{:else}
  <a class="skip-link" href="#main-workspace">Skip to main content</a>
  <div class="app-shell" class:slim={sidebarSlim}>
    <aside class="sidebar">
      <a class="brand" href="#/" aria-label="Allstarr home">
        <span class="brand-mark">A</span>
        <span>
          <strong>Allstarr</strong>
          <small>beta {__APP_VERSION__}</small>
        </span>
      </a>
      <button
        class="sidebar-expander"
        type="button"
        aria-label={sidebarSlim ? "Expand sidebar" : "Collapse sidebar"}
        aria-expanded={!sidebarSlim}
        onclick={() => sidebarSlim = !sidebarSlim}
      >
        {#if sidebarSlim}<PanelLeftOpen class="menu-icon" size={20} aria-hidden="true" />
        {:else}<PanelLeftClose class="menu-icon" size={20} aria-hidden="true" />{/if}
      </button>

      <nav class="desktop-navigation" aria-label="Primary">
        {#each navigationDestinations as destination}
          <a
            href={destination.href}
            class:active={activeDestination.href === destination.href}
            aria-current={activeDestination.href === destination.href ? "page" : undefined}
          >
            <span class="nav-icon"><UiIcon name={destination.icon} /></span>
            <span class="nav-label">{destination.label}</span>
          </a>
        {/each}
      </nav>

      <nav class="mobile-navigation" aria-label="Primary">
        {#each mobileDestinations as destination}
          <a
            href={destination.href}
            class:active={activeDestination.href === destination.href}
            aria-current={activeDestination.href === destination.href ? "page" : undefined}
          >
            <span class="nav-icon"><UiIcon name={destination.icon} /></span>
            <span class="nav-label">{destination.mobileLabel}</span>
          </a>
        {/each}
        <button
          type="button"
          class:active={moreDestinationActive}
          aria-label="More destinations"
          aria-pressed={moreDestinationActive}
          aria-haspopup="dialog"
          aria-expanded={mobileMenuOpen}
          onclick={() => mobileMenuOpen = true}
        >
          <span class="nav-icon"><UiIcon name="more" /></span>
          <span class="nav-label">More</span>
        </button>
      </nav>

      <div class="profile">
        <a class="avatar" href="#/settings" aria-label={`Settings for ${session.user?.name ?? "current user"}`}>
          {#if session.user?.avatarUrl && !avatarFailed}
            <img
              src={session.user.avatarUrl}
              alt=""
              onerror={() => {
                avatarFailed = true;
              }}
            />
          {:else}
            <span>{initials}</span>
          {/if}
        </a>
        <div class="min-w-0">
          <strong>{session.user?.name}</strong>
          <small>{session.backend}</small>
        </div>
        <button
          class="icon-button"
          type="button"
          disabled={authBusy}
          onclick={() => void logout()}
          aria-label={authBusy ? "Signing out" : "Sign out"}
        ><UiIcon name="logout" /></button>
      </div>
    </aside>

    <main class="workspace" id="main-workspace">
      <header class="workspace-header">
        <div class="workspace-title">
          <h1>{activeDestination.label}</h1>
        </div>
        <div class="flex items-center gap-2">
          <button
            class="icon-button"
            type="button"
            title={`Theme: ${themeMode}. Switch to ${nextTheme}.`}
            aria-label={`Theme: ${themeMode}. Switch to ${nextTheme}.`}
            onclick={cycleTheme}
          >
            {#if themeMode === "system"}<Monitor aria-hidden="true" />
            {:else if themeMode === "light"}<Sun aria-hidden="true" />
            {:else}<Moon aria-hidden="true" />{/if}
          </button>
          <div class="live-state" data-state={liveUpdates.state.status} role="status" aria-label={`Live updates ${liveUpdates.state.status}`}>
            <span aria-hidden="true"></span>
            {liveUpdates.state.status[0].toUpperCase() + liveUpdates.state.status.slice(1)}
          </div>
        </div>
      </header>

      {#if route.startsWith("/library/")}
        <SegmentedNav
          items={librarySections}
          active={route.split("/").at(-1) ?? "playlists"}
          label="Library sections"
          class="route-tabs library-tabs"
        />
      {/if}

      {#if onboardingError || onboardingState?.recoveryNotices.includes("backend_identity_missing")}
        <div class="degraded-banner" role="status">
          <span aria-hidden="true">!</span>
          <p><strong>Media server connection needs attention.</strong> {onboardingError || "The identity saved during setup is no longer available. Sign in again or review the media server connection."}</p>
        </div>
      {/if}

      {#if authError}
        <div class="degraded-banner" role="alert">
          <span aria-hidden="true">!</span>
          <p><strong>Sign out failed.</strong> {authError}</p>
          <button class="icon-button" type="button" aria-label="Dismiss sign out error" onclick={() => authError = ""}>
            <X size={18} aria-hidden="true" />
          </button>
        </div>
      {/if}

      {#if ActiveView && loadedViewKey === viewKey(route)}
        <ActiveView {...activeProps} />
      {:else if viewError && loadedRoute === route}
        <RouteError
          eyebrow="View unavailable"
          title={`${activeDestination.label} could not be loaded.`}
          message={viewError}
          onRetry={() => window.location.reload()}
        />
      {:else if viewLoader(route)}
        <section class="panel skeleton-panel" aria-busy="true" aria-label={`Loading ${activeDestination.label}`}>
          <div class="skeleton-line short"></div>
          <div class="skeleton-card"></div>
        </section>
      {:else}
        <section class="panel empty-state">
          <span class="empty-orbit" aria-hidden="true">✦</span>
          <p class="eyebrow">Page not found</p>
          <h2>This Allstarr view does not exist.</h2>
          <p>Use the navigation to return to a current workspace.</p>
        </section>
      {/if}
    </main>
  </div>

  <Dialog.Root bind:open={mobileMenuOpen}>
    <Dialog.Portal>
      <Dialog.Overlay class="dialog-overlay" />
      <Dialog.Content class="source-dialog mobile-menu-sheet">
        <header>
          <div>
            <p class="eyebrow">Account and administration</p>
            <Dialog.Title>More</Dialog.Title>
            <Dialog.Description>Connections, deployment settings, appearance, and your session.</Dialog.Description>
          </div>
          <Dialog.Close class="icon-button" aria-label="Close more destinations"><X size={18} aria-hidden="true" /></Dialog.Close>
        </header>

        <div class="mobile-menu-body">
          <section class="mobile-sheet-profile" aria-label="Signed-in account">
            <span class="avatar" aria-hidden="true">
              {#if session.user?.avatarUrl && !avatarFailed}
                <img src={session.user.avatarUrl} alt="" onerror={() => avatarFailed = true} />
              {:else}
                <span>{initials}</span>
              {/if}
            </span>
            <span>
              <strong>{session.user?.name}</strong>
              <small>{session.backend}</small>
            </span>
          </section>

          <nav class="mobile-menu-destinations" aria-label="More destinations">
            {#each moreDestinations as destination}
              <a
                href={destination.href}
                class:active={activeDestination.href === destination.href}
                aria-current={activeDestination.href === destination.href ? "page" : undefined}
                onclick={() => mobileMenuOpen = false}
              >
                <span class="nav-icon"><UiIcon name={destination.icon} /></span>
                <span>
                  <strong>{destination.label}</strong>
                  <small>{destination.label === "Integrations" ? "Services, accounts, extensions, and routing" : "Deployment and operator controls"}</small>
                </span>
              </a>
            {/each}
          </nav>

          <div class="mobile-menu-actions">
            <button type="button" onclick={cycleTheme}>
              {#if themeMode === "system"}<Monitor aria-hidden="true" />
              {:else if themeMode === "light"}<Sun aria-hidden="true" />
              {:else}<Moon aria-hidden="true" />{/if}
              <span><strong>Appearance</strong><small>{themeMode[0].toUpperCase() + themeMode.slice(1)} · switch to {nextTheme}</small></span>
            </button>
            <button type="button" disabled={authBusy} onclick={() => void logout()}>
              <UiIcon name="logout" />
              <span><strong>{authBusy ? "Signing out…" : "Sign out"}</strong><small>End this Allstarr session</small></span>
            </button>
          </div>
        </div>
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>

  {#if onboardingState && OnboardingDialog}
    <OnboardingDialog
      bind:open={onboardingOpen}
      state={onboardingState}
      onComplete={onboarding.complete}
      onChanged={(next: OnboardingState) => {
        onboardingState = next;
        onboardingOpen = next.shouldRedirectToSetup || next.setupOpen;
      }}
    />
  {/if}
{/if}
