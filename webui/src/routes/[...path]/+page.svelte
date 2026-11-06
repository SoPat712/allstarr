<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { onMount, type Component } from "svelte";
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import BackendAccessWarning from "$lib/components/BackendAccessWarning.svelte";
  import { auth, onboarding } from "$lib/access-api";
  import type { OnboardingState, Session } from "$lib/api";
  import { adminUrl } from "$lib/admin-url";
  import { adminOidc, type OidcStatus } from "$lib/admin-oidc";
  import { liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { slideIndicator } from "$lib/slide-indicator";
  import RouteError from "$lib/components/RouteError.svelte";
  import UiIcon from "$lib/components/UiIcon.svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { applyThemeMode, onThemeModeChange, readThemeMode, saveThemeMode, type ThemeMode } from "$lib/theme";
  import { Monitor, Moon, PanelLeftClose, PanelLeftOpen, Sun, X } from "@lucide/svelte";

  const destinations = [
    { href: "#/", label: "Home", mobileLabel: "Home", icon: "home", mobile: true },
    { href: "#/library/playlists", prefix: "/library/", label: "Library", mobileLabel: "Library", icon: "library", mobile: true },
    { href: "#/for-you", label: "For you", mobileLabel: "For you", icon: "headphones", mobile: true },
    { href: "#/sources/services", prefix: "/integrations/", label: "Sources", mobileLabel: "Sources", icon: "sources", mobile: false },
    { href: "#/activity", label: "Activity", mobileLabel: "Activity", icon: "activity", mobile: true },
    { href: "#/settings", label: "Settings", mobileLabel: "Settings", icon: "settings", mobile: false },
    { href: "#/connect", prefix: "/getting-started", label: "Connect apps", mobileLabel: "Connect apps", icon: "guide", mobile: false },
    { href: "#/report", label: "Report a problem", mobileLabel: "Report", icon: "report", mobile: false },
  ];
  const preferencesDestination: (typeof destinations)[number] = {
    href: "#/settings/preferences",
    label: "Listening preferences",
    mobileLabel: "Preferences",
    icon: "headphones",
    mobile: false,
  };
  const allLibrarySections = [
    { id: "playlists", label: "Playlists", href: "#/library/playlists" },
    { id: "mappings", label: "Matches", href: "#/library/matches" },
    { id: "cached", label: "Cached", href: "#/library/cached" },
    { id: "kept", label: "Kept", href: "#/library/kept" },
    { id: "import", label: "Import", href: "#/library/import" },
  ];
  const bootMessages = [
    "Checking your session",
    "Loading account access",
    "Preparing your music control center",
  ];

  let session = $state<Session | null>(null);
  let oidc = $state<OidcStatus>({ enabled: false });
  let loading = $state(true);
  let bootstrapFailed = $state(false);
  let bootMessageIndex = $state(0);
  let error = $state("");
  let username = $state("");
  let password = $state("");
  let rememberMe = $state(true);
  let managePlaylists = $state(false);
  let authBusy = $state(false);
  let authError = $state("");
  let avatarFailed = $state(false);
  let onboardingState = $state<OnboardingState | null>(null);
  let onboardingError = $state("");
  let BackendSetupPanel = $state<Component<any>>();
  let sidebarSlim = $state(false);
  let mobileMenuOpen = $state(false);
  let SignInView = $state<Component<any>>(), signInLoadError = $state(false);
  async function loadSignIn() {
    signInLoadError = false;
    try { SignInView = (await import("$lib/components/SignInView.svelte")).default; }
    catch { signInLoadError = true; }
  }
  $effect(() => { if (!loading && session?.backendConfigured !== false && (!session?.authenticated || oidc.linkPending) && !SignInView && !signInLoadError) void loadSignIn(); });
  let mobileMenuTrigger = $state<HTMLButtonElement>();
  let sidebarExpander = $state<HTMLButtonElement>();
  let themeMode = $state<ThemeMode>("system");
  let ActiveView = $state<Component<any>>();
  let LibraryNavigation = $state<Component<any>>();
  $effect(() => { if (route.startsWith("/library/") && !LibraryNavigation) void import("$lib/components/SegmentedNav.svelte").then(module => { LibraryNavigation = module.default; }).catch(() => { toast.error("Library navigation could not be loaded. Reload to try again."); }); });
  let loadedRoute = $state("");
  let loadedViewKey = $state("");
  let viewError = $state("");
  let viewRequest = 0;

  $effect(() => { if (!session?.authenticated) toast.clear(); });

  const administrator = $derived(session?.user?.isAdministrator === true);
  const developmentIntelligence = $derived(session?.features?.intelligence === true);
  const navigationDestinations = $derived(destinations.filter(item => item.href !== "#/for-you" || !developmentIntelligence));
  const mobileDestinations = $derived(navigationDestinations.filter((item) => item.mobile));
  const moreDestinations = $derived([preferencesDestination, ...["#/sources/services", "#/connect", ...(administrator ? ["#/settings"] : []), "#/report"]
    .map(href => navigationDestinations.find(item => item.href === href)!).filter(Boolean)]);
  const librarySections = $derived(administrator
    ? allLibrarySections
    : allLibrarySections.filter((item) => item.id === "playlists" || item.id === "mappings" || item.id === "import"));

  // Addresses read like the labels people see; the older ones still open the same pages.
  function currentRoute(path: string): string {
    if (path === "/library/matches") return "/library/mappings";
    if (path === "/connect") return "/getting-started";
    if (path.startsWith("/sources/")) return currentRoute("/integrations/" + path.slice("/sources/".length));
    if (path === "/intelligence" && !developmentIntelligence) return "/for-you";
    if (path === "/home") return "/";
    if (["/library", "/library/link", "/library/injected", "/library/external"].includes(path)) {
      return "/library/playlists";
    }
    if (["/library/missing", "/library/migration"].includes(path)) return "/library/mappings";
    if (path === "/sources") return "/integrations/services";
    if (path === "/preferences") return "/settings/preferences";
    if (path === "/settings/accounts") return "/integrations/accounts";
    if (path === "/settings/extensions") return "/integrations/services";
    if (["/settings/routing", "/settings/playback", "/integrations/routing"].includes(path)) return "/settings/quality";
    if (path === "/integrations/extensions") return "/integrations/services";
    return path;
  }

  const route = $derived(currentRoute(`/${page.params.path ?? ""}`));
  const forbiddenRoute = $derived(!administrator && (
    (route.startsWith("/settings") && !["/settings", "/settings/appearance", "/settings/preferences", "/settings/alerts", "/settings/adding-to-library"].includes(route)) ||
    route === "/library/cached" ||
    route === "/library/kept" ||
    (route === "/intelligence" && developmentIntelligence)
  ));
  $effect(() => {
    if (session?.authenticated && page.params.path === "intelligence" && !developmentIntelligence) {
      const query = new URLSearchParams(page.url.hash.split("?", 2)[1] ?? "");
      const old = query.get("section");
      if (old === "stats") query.set("section", "recap");
      if (old === "sounds" || old === "for-you") query.delete("section");
      void goto(`#/for-you${query.size ? `?${query}` : ""}`, { replaceState: true, keepFocus: true, noScroll: true });
    }
  });
  const routeQuery = $derived(new URLSearchParams(page.url.hash.split("?", 2)[1] ?? ""));
  const activeDestination = $derived(
    route === "/settings/alerts" && !administrator ? { ...preferencesDestination, label: "Alerts" } :
    route === "/report" ? { href: "#/report", label: "Report a problem", mobileLabel: "Report", icon: "report", mobile: false } : [...navigationDestinations, ...destinations, preferencesDestination].find((item) =>
      item.href === "#/" ? route === "/" : route.startsWith(item.prefix ?? item.href.slice(1)),
    ) ?? destinations[0],
  );
  const moreDestinationActive = $derived(!activeDestination.mobile);
  const nextTheme = $derived<ThemeMode>(themeMode === "system" ? "light" : themeMode === "light" ? "dark" : "system");
  const ThemeIcon = $derived(themeMode === "system" ? Monitor : themeMode === "light" ? Sun : Moon);
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
      ? { administrator }
      : route === "/preferences"
      ? { administrator }
      : route === "/library/import"
        ? { initialAccount: routeQuery.get("account") ?? "", initialRun: routeQuery.get("run") ?? "", currentUserId: session?.user?.allstarrUserId ?? session?.user?.id ?? "" }
        : route === "/library/playlists"
        ? { initialId: routeQuery.get("playlist") ?? "", currentUserId: session?.user?.allstarrUserId ?? "" }
        : route === "/library/mappings"
          ? {
              initialSearch: routeQuery.get("search") ?? "",
              initialReview: routeQuery.get("review") ?? "",
            }
          : route === "/library/cached"
            ? { storage: "cache" }
            : route === "/library/kept"
              ? { storage: "kept" }
              : route === "/intelligence" || route === "/for-you"
                ? {
                    initialSection: routeQuery.get("section") ?? (developmentIntelligence ? "overview" : "for-you"),
                    administrator,
                    initialMix: routeQuery.get("mix") ?? "",
                    initialImport: routeQuery.get("import") === "1",
                    initialRelease: routeQuery.get("release") ?? "",
                    listenerId: session?.user?.allstarrUserId ?? session?.user?.id ?? "",
                    listenerName: session?.user?.name ?? "",
                  }
                : route.startsWith("/integrations")
                  ? {
                      section: route.split("/")[2] || "services",
                      backend: session?.backend ?? "",
                      developmentIntelligence,
                      currentUserId: session?.user?.allstarrUserId ?? "",
                      administrator,
                      initialSource: routeQuery.get("source") ?? "",
                      initialSection: routeQuery.get("section") ?? "data",
                      initialConnect: routeQuery.get("connect") === "1",
                      serviceId: route.split("/")[2] === "services" ? decodeURIComponent(route.split("/")[3] ?? "") : "",
                      from: routeQuery.get("from") ?? "",
                      focusAccount: routeQuery.get("account") ?? "",
                    }
                  : route.startsWith("/settings")
                    ? {
                        section: route.split("/")[2] || "general",
                        initialPanel: routeQuery.get("provider") ?? "",
                        administrator,
                        onOpenSetup: reopenSetup,
                        onboardingState,
                        onSetupChanged: (value: OnboardingState) => { onboardingState = value; },
                      }
                    : route === "/activity"
                      ? { administrator, currentUserId: session?.user?.allstarrUserId ?? "", initialView: routeQuery.get("view") ?? "history", initialReportId: routeQuery.get("report") ?? "", initialJobId: routeQuery.get("job") ?? "" }
                    : route === "/getting-started"
                      ? { administrator }
                    : route === "/report"
                      ? { query: routeQuery }
                    : {},
  );

  async function loadOnboarding(current: Session) {
    onboardingState = null;
    onboardingError = "";
    if (!current.user?.isAdministrator) return;
    try {
      onboardingState = await onboarding.status();
      if (onboardingState.shouldRedirectToSetup) window.location.hash = "#/settings/setup";
    } catch (cause) {
      onboardingError = cause instanceof Error ? cause.message : "Setup status is unavailable.";
    }
  }

  async function reopenSetup() {
    if (!session?.user?.isAdministrator) return;
    try {
      onboardingState = await onboarding.reopen();
      window.location.hash = "#/settings/setup";
      onboardingError = "";
    } catch (cause) {
      onboardingError = cause instanceof Error ? cause.message : "Setup could not be reopened.";
    }
  }

  function cycleTheme() {
    themeMode = nextTheme;
    saveThemeMode(themeMode);
  }

  // Opening a page feels instant when its code starts loading on hover.
  function prefetch(href: string) {
    void viewLoader(currentRoute(href.slice(1).split("?")[0]))?.catch(() => {});
  }

  const viewLoads = new Map<string, NonNullable<ReturnType<typeof importView>>>();
  function viewLoader(path: string) {
    if (forbiddenRoute) return;
    const key = `${viewKey(path)}:${developmentIntelligence}`;
    const cached = viewLoads.get(key);
    if (cached) return cached;
    const pending = importView(path);
    if (pending) { viewLoads.set(key, pending); void pending.catch(() => viewLoads.delete(key)); }
    return pending;
  }

  function importView(path: string) {
    if (path === "/") return import("$lib/components/HomeView.svelte");
    if (path === "/library/import") return import("$lib/components/ImportView.svelte");
    if (path === "/library/playlists") return import("$lib/components/PlaylistsView.svelte");
    if (path === "/library/mappings") return import("$lib/components/MappingView.svelte");
    if (path === "/library/cached" || path === "/library/kept") {
      return import("$lib/components/DownloadsView.svelte");
    }
    if (path === "/activity") return import("$lib/components/EventLogView.svelte");
    if (path === "/preferences") return import("$lib/components/ListeningPreferencesView.svelte");
    if (path === "/getting-started") return import("$lib/components/GettingStartedView.svelte");
    if (path === "/report") return import("$lib/components/ProblemReportView.svelte");
    if (path === "/for-you") return import("$lib/components/IntelligenceView.svelte");
    if (path === "/intelligence") {
      return developmentIntelligence
        ? import("$lib/shelved/IntelligenceView.svelte")
        : import("$lib/components/IntelligenceView.svelte");
    }
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
    administrator;
    developmentIntelligence;
    if (forbiddenRoute) {
      viewRequest++;
      ActiveView = undefined;
      loadedViewKey = "";
      loadedRoute = path;
      viewError = "";
      return;
    }
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
          try { sessionStorage.removeItem(updateReloadKey); } catch { /* Browser storage is optional. */ }
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
    if (session?.backendConfigured === false && !BackendSetupPanel) {
      void import("$lib/components/BackendSetupPanel.svelte").then(({ default: panel }) => BackendSetupPanel = panel);
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
      oidc = await adminOidc.status().catch(() => ({ enabled: false }));
      if (new URLSearchParams(window.location.search).get("oidc") === "failed") {
        error = "SSO sign-in failed. Try again, or use your media-server account.";
      }
      if (session.authenticated) {
        await loadOnboarding(session);
        liveUpdates.connect(session.user?.isAdministrator ?? false);
      }
    } catch (cause) {
      bootstrapFailed = true;
      error = cause instanceof Error ? cause.message : "Allstarr is unavailable.";
    } finally {
      loading = false;
    }
  }

  // A release replaces the page's files while it stays open, so the next view asks for files that are gone. Reloading
  // once picks up the new release; the saved flag keeps a lasting failure from reloading again and again.
  const updateReloadKey = "allstarr.reloaded-for-update";
  function reloadForUpdate(event: Event) {
    try {
      if (sessionStorage.getItem(updateReloadKey)) return;
      sessionStorage.setItem(updateReloadKey, "1");
    } catch { return; }
    event.preventDefault();
    location.reload();
  }

  onMount(() => {
    window.addEventListener("vite:preloadError", reloadForUpdate);
    const compactSidebar = matchMedia("(min-width: 761px) and (max-width: 900px)");
    const mobileNavigation = matchMedia("(max-width: 760px)");
    const colorScheme = matchMedia("(prefers-color-scheme: dark)");
    themeMode = readThemeMode();
    const applySidebarBreakpoint = () => { sidebarSlim = compactSidebar.matches; };
    const closeDesktopMenu = () => { if (!mobileNavigation.matches) mobileMenuOpen = false; };
    const applySystemTheme = () => { if (themeMode === "system") applyThemeMode(themeMode); };
    const unsubscribeTheme = onThemeModeChange((mode) => { themeMode = mode; });
    applySidebarBreakpoint();
    applyThemeMode(themeMode);
    compactSidebar.addEventListener("change", applySidebarBreakpoint);
    mobileNavigation.addEventListener("change", closeDesktopMenu);
    colorScheme.addEventListener("change", applySystemTheme);

    void bootstrap();
    let mounted = true;
    let removeRipple: (() => void) | undefined;
    void import("$lib/ripple").then(({ installRipple }) => {
      if (mounted) removeRipple = installRipple();
    }).catch(() => {});

    return () => {
      window.removeEventListener("vite:preloadError", reloadForUpdate);
      mounted = false;
      removeRipple?.();
      compactSidebar.removeEventListener("change", applySidebarBreakpoint);
      mobileNavigation.removeEventListener("change", closeDesktopMenu);
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
      session = oidc.linkPending
        ? await adminOidc.link(username, password, rememberMe, oidc.csrfToken ?? "", managePlaylists)
        : await auth.login(username, password, rememberMe, managePlaylists);
      oidc = await adminOidc.status().catch(() => ({ enabled: false }));
      avatarFailed = false;
      password = "";
      managePlaylists = false;
      await loadOnboarding(session);
      liveUpdates.connect(session.user?.isAdministrator ?? false);
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
      session = await auth.session();
      oidc = await adminOidc.status().catch(() => ({ enabled: false }));
    } catch (cause) {
      authError = cause instanceof Error ? cause.message : "You are still signed in. Try again.";
    } finally {
      authBusy = false;
    }
  }

  async function disconnectSso() {
    if (authBusy || !confirm("Disconnect SSO? You can still sign in with your media-server account.")) return;
    authBusy = true;
    authError = "";
    try {
      await adminOidc.unlink(oidc.csrfToken ?? "");
      await bootstrap();
    } catch (cause) {
      authError = cause instanceof Error ? cause.message : "Could not disconnect SSO.";
    } finally {
      authBusy = false;
    }
  }

  async function cancelSsoLink() {
    if (authBusy) return;
    authBusy = true;
    try {
      await adminOidc.cancel(oidc.csrfToken ?? "");
      oidc = await adminOidc.status();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Could not cancel SSO linking.";
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
      <div class="brand-mark login-brand mb-6" aria-hidden="true">A</div>
      <h1 id="bootstrap-error-title" class="mt-2 text-3xl font-semibold tracking-tight">Allstarr could not start.</h1>
      <p class="notice-error mt-4" role="alert">{error}</p>
      <button class="auth-submit mt-6 w-full" type="button" onclick={() => void bootstrap()}>Try again</button>
    </section>
  </main>
{:else if session?.backendConfigured === false}
  {#if BackendSetupPanel}<BackendSetupPanel onReady={bootstrap} />{/if}
{:else if !session?.authenticated || oidc.linkPending}
  {#if SignInView}<SignInView backend={session?.backend ?? "media server"} {oidc} bind:username bind:password bind:rememberMe bind:managePlaylists busy={authBusy} {error} onlogin={login} oncancel={cancelSsoLink} />
  {:else if signInLoadError}<RouteError eyebrow="Sign in unavailable" title="The sign-in form could not be loaded." message="Try loading the form again." onRetry={loadSignIn} />
  {:else}<main class="grid min-h-screen place-items-center p-6" aria-busy="true" aria-label="Loading sign in"><p>Loading sign in…</p></main>{/if}
{:else}
  <a class="skip-link" href="#main-workspace">Skip to main content</a>
  <div class="app-shell" class:slim={sidebarSlim}>
    <aside class="sidebar">
      <a class="brand" href="#/" aria-label="Allstarr home">
        <span class="brand-mark">A</span>
        <span>
          <strong>Allstarr</strong>
          <small>{session.backend}</small>
        </span>
      </a>
      <button
        class="sidebar-expander"
        bind:this={sidebarExpander}
        type="button"
        aria-label={sidebarSlim ? "Expand sidebar" : "Collapse sidebar"}
        aria-expanded={!sidebarSlim}
        onclick={() => sidebarSlim = !sidebarSlim}
      >
        {#if sidebarSlim}<PanelLeftOpen class="menu-icon" size={20} aria-hidden="true" />
        {:else}<PanelLeftClose class="menu-icon" size={20} aria-hidden="true" />{/if}
      </button>

      <nav class="desktop-navigation" aria-label="Primary" use:slideIndicator={"a.active"}>
        {#each [destinations.filter(item => !["#/connect", "#/report"].includes(item.href)), destinations.filter(item => ["#/connect", "#/report"].includes(item.href))] as group, index}
          <div class="navigation-group" role="group" aria-label={index === 0 ? "Main destinations" : "Help and connections"}>
            {#each group as destination}
              {@const active = activeDestination.href === destination.href || destination.href === "#/settings" && (route === "/preferences" || route.startsWith("/settings"))}
              <a
                href={destination.href}
                onpointerenter={() => prefetch(destination.href)}
                onfocus={() => prefetch(destination.href)}
                class:active
                aria-current={active ? "page" : undefined}
              >
                <span class="nav-icon"><UiIcon name={destination.icon} /></span>
                <span class="nav-label">{destination.label}</span>
              </a>
            {/each}
          </div>
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
          bind:this={mobileMenuTrigger}
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
        <span class="avatar" aria-hidden="true">
          {#if session.user?.avatarUrl && !avatarFailed}
            <img
              src={adminUrl(session.user.avatarUrl)}
              alt=""
              onerror={() => {
                avatarFailed = true;
              }}
            />
          {:else}
            <span>{initials}</span>
          {/if}
        </span>
        <div class="min-w-0">
          <a class="block min-w-0 text-inherit no-underline" href="#/settings/preferences" aria-label="Listening preferences" aria-current={route === "/settings/preferences" ? "page" : undefined}>
            <strong>{session.user?.name}</strong>
            <small>{administrator ? "Administrator" : "Listener"}</small>
          </a>
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
      {#if oidc.linked}
        <div class="flex items-center justify-end gap-3 p-2 text-sm">
          <span class="text-ink-muted">{oidc.displayName} connected</span>
          <Button disabled={authBusy} onclick={() => void disconnectSso()}>Disconnect SSO</Button>
        </div>
      {/if}
      <header class="workspace-header">
        <div class="workspace-title">
          <h1>{activeDestination.label}</h1>
        </div>
        <div class="flex items-center gap-2">
          <div class="live-state" data-state={liveUpdates.state.status} role="status" aria-label={`Live updates ${liveUpdates.state.status}`}>
            <span aria-hidden="true"></span>
            {liveUpdates.state.status[0].toUpperCase() + liveUpdates.state.status.slice(1)}
          </div>
        </div>
      </header>

      {#if route.startsWith("/library/") && !forbiddenRoute && LibraryNavigation}
        <LibraryNavigation
          variant="tabs"
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

      {#if administrator && (route === "/" || route.startsWith("/settings"))}<BackendAccessWarning />{/if}

      {#if authError}
        <div class="degraded-banner" role="alert">
          <span aria-hidden="true">!</span>
          <p><strong>Sign out failed.</strong> {authError}</p>
          <button class="icon-button" type="button" aria-label="Dismiss sign out error" onclick={() => authError = ""}>
            <X size={18} aria-hidden="true" />
          </button>
        </div>
      {/if}

      {#if route === "/" && administrator && onboardingState && !onboardingState.completed}
        <section class="panel p-5 m-4 space-y-2" aria-label="Setup progress">
          <h2>Continue setting up Allstarr</h2><p>Your choices are saved. Review sources, imported songs and household settings when you are ready.</p>
          <ul>{#each (onboardingState.steps ?? []).filter((step) => step.state === "pending").slice(0, 3) as step}<li>{step.title} · {step.required ? "Required" : "Optional"}</li>{/each}</ul>
          <Button href="#/settings/setup">Continue setup</Button>
        </section>
      {/if}
      {#if forbiddenRoute}
        <section class="panel empty-state" aria-labelledby="administrator-required-title">
          <span class="empty-orbit" aria-hidden="true">✦</span>
                    <h2 id="administrator-required-title">This workspace is for administrators.</h2>
          <p>Your Home, listening preferences, playlists, accounts, and personal activity remain available.</p>
          <div class="flex flex-wrap justify-center gap-2">
            <Button href="#/">Go to Home</Button>
            <Button href="#/settings/preferences">Listening preferences</Button>
          </div>
        </section>
      {:else if ActiveView && loadedViewKey === viewKey(route)}
        {#key `${loadedViewKey}:${administrator}`}
          <ActiveView {...activeProps} />
        {/key}
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
                    <h2>This Allstarr view does not exist.</h2>
          <p>Use the navigation to return to a current workspace.</p>
        </section>
      {/if}
    </main>
  </div>

  <Dialog.Root bind:open={mobileMenuOpen}>
    <Dialog.Portal>
      <Dialog.Overlay class="dialog-overlay" />
      <Dialog.Content class="source-dialog mobile-menu-sheet" onCloseAutoFocus={(event) => {
        event.preventDefault();
        (mobileMenuTrigger?.offsetParent ? mobileMenuTrigger : sidebarExpander)?.focus();
      }}>
        <header>
          <div>
            <Dialog.Title>More</Dialog.Title>
            <Dialog.Description>{administrator ? "Preferences, sources, settings and your account." : "Preferences, connections and your account."}</Dialog.Description>
          </div>
          <Dialog.Close class="icon-button" aria-label="Close more destinations"><X size={18} aria-hidden="true" /></Dialog.Close>
        </header>

        <div class="mobile-menu-body">
          <section class="mobile-sheet-profile" aria-label="Signed-in account">
            <span class="avatar" aria-hidden="true">
              {#if session.user?.avatarUrl && !avatarFailed}
                <img src={adminUrl(session.user.avatarUrl)} alt="" onerror={() => avatarFailed = true} />
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
                  <small>{destination.href === "#/settings/preferences" ? "Content and track labels for your account" : destination.href === "#/connect" ? "Connect music apps and get help" : destination.href === "#/report" ? "Send a note to your administrator" : destination.label === "Sources" ? (administrator ? "Music services, accounts and extensions" : "Music services and your accounts") : "Deployment and operator controls"}</small>
                </span>
              </a>
            {/each}
          </nav>

          <div class="mobile-menu-actions">
            <button type="button" onclick={cycleTheme}>
              <ThemeIcon aria-hidden="true" />
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

{/if}
