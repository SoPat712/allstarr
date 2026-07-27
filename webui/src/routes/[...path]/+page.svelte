<script lang="ts">
  import { onMount, type Component } from "svelte";
  import { page } from "$app/state";
  import { auth, onboarding, type OnboardingState, type Session } from "$lib/api";
  import { liveUpdates } from "$lib/live-updates.svelte";
  import HomeView from "$lib/components/HomeView.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";

  const destinations = [
    { href: "#/", label: "Home", icon: "⌂" },
    { href: "#/library/playlists", prefix: "/library/", label: "Library", icon: "♫" },
    { href: "#/intelligence", label: "Intelligence", icon: "✦" },
    { href: "#/sources", label: "Sources", icon: "◎" },
    { href: "#/activity", label: "Activity", icon: "↗" },
    { href: "#/settings", label: "Settings", icon: "⚙" },
  ];
  const librarySections = [
    { id: "playlists", label: "Playlists", href: "#/library/playlists" },
    { id: "mappings", label: "Mappings", href: "#/library/mappings" },
    { id: "cached", label: "Cached", href: "#/library/cached" },
    { id: "kept", label: "Kept", href: "#/library/kept" },
  ];

  let session = $state<Session | null>(null);
  let loading = $state(true);
  let error = $state("");
  let username = $state("");
  let password = $state("");
  let rememberMe = $state(true);
  let avatarFailed = $state(false);
  let onboardingState = $state<OnboardingState | null>(null);
  let onboardingOpen = $state(false);
  let onboardingError = $state("");
  let OnboardingDialog = $state<Component<any>>();
  let sidebarSlim = $state(false);
  let ActiveView = $state<Component<any>>();
  let loadedRoute = $state("");
  let viewError = $state("");
  let viewRequest = 0;

  function currentRoute(path: string) {
    if (path === "/home") return "/";
    if (["/library", "/library/link", "/library/injected", "/library/external"].includes(path)) {
      return "/library/playlists";
    }
    if (["/library/missing", "/library/migration"].includes(path)) return "/library/mappings";
    return path;
  }

  const route = $derived(currentRoute(`/${page.params.path ?? ""}`));
  const routeQuery = $derived(new URLSearchParams(page.url.hash.split("?", 2)[1] ?? ""));
  const activeDestination = $derived(
    destinations.find((item) =>
      item.href === "#/" ? route === "/" : route.startsWith(item.prefix ?? item.href.slice(1)),
    ) ?? destinations[0],
  );
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
              : route === "/sources"
                ? { administrator: session?.user?.isAdministrator ?? false }
                : route.startsWith("/settings")
                  ? {
                      section: route.split("/")[2] || "general",
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

  function viewLoader(path: string) {
    if (path === "/") return Promise.resolve({ default: HomeView });
    if (path === "/library/playlists") return import("$lib/components/PlaylistsView.svelte");
    if (path === "/library/mappings") return import("$lib/components/MappingView.svelte");
    if (path === "/library/cached" || path === "/library/kept") {
      return import("$lib/components/DownloadsView.svelte");
    }
    if (path === "/activity") return import("$lib/components/EventLogView.svelte");
    if (path === "/intelligence") return import("$lib/components/IntelligenceView.svelte");
    if (path === "/sources") return import("$lib/components/SourcesView.svelte");
    if (path.startsWith("/settings")) return import("$lib/components/SettingsView.svelte");
  }

  $effect(() => {
    const path = route;
    const loader = viewLoader(path);
    const request = ++viewRequest;
    ActiveView = undefined;
    loadedRoute = loader ? "" : path;
    viewError = "";

    void loader
      ?.then(({ default: view }) => {
        if (request === viewRequest) {
          ActiveView = view;
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

  onMount(() => {
    void (async () => {
      try {
        session = await auth.session();
        if (session.authenticated) {
          await loadOnboarding(session);
          liveUpdates.connect();
        }
      } catch (cause) {
        error = cause instanceof Error ? cause.message : "Allstarr is unavailable.";
      } finally {
        loading = false;
      }
    })();

    return () => liveUpdates.close();
  });

  async function login() {
    error = "";
    try {
      session = await auth.login(username, password, rememberMe);
      avatarFailed = false;
      password = "";
      await loadOnboarding(session);
      liveUpdates.connect();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Sign in failed.";
    }
  }

  async function logout() {
    await auth.logout();
    liveUpdates.close();
    onboardingState = null;
    onboardingOpen = false;
    session = await auth.session();
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
  <main class="grid min-h-screen place-items-center p-6" aria-busy="true">
    <div class="w-full max-w-sm space-y-3">
      <div class="h-3 w-24 animate-pulse rounded-full bg-white/10"></div>
      <div class="h-16 animate-pulse rounded-2xl bg-white/7"></div>
      <p class="text-sm text-ink-muted">Connecting to Allstarr…</p>
    </div>
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

      <form class="mt-8 space-y-4" onsubmit={(event) => { event.preventDefault(); login(); }}>
        <label class="field">
          <span>Username</span>
          <input bind:value={username} autocomplete="username" required />
        </label>
        <label class="field">
          <span>Password</span>
          <input bind:value={password} type="password" autocomplete="current-password" required />
        </label>
        <label class="flex min-h-11 items-center gap-3 text-sm text-ink-muted">
          <input bind:checked={rememberMe} type="checkbox" class="size-4 accent-signal" />
          Keep me signed in
        </label>
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
        <button class="button-primary w-full" type="submit">Sign in</button>
      </form>
    </section>
  </main>
{:else}
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
      >‹</button>

      <nav aria-label="Primary">
        {#each destinations as destination}
          <a
            href={destination.href}
            class:active={activeDestination.href === destination.href}
            aria-current={activeDestination.href === destination.href ? "page" : undefined}
          >
            <span aria-hidden="true">{destination.icon}</span>
            {destination.label}
          </a>
        {/each}
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
        <button class="icon-button" type="button" onclick={logout} aria-label="Sign out">↪</button>
      </div>
    </aside>

    <main class="workspace">
      <header class="workspace-header">
        <div class="workspace-title">
          <h1>{activeDestination.label}</h1>
          {#if route.startsWith("/library/")}
            <SegmentedNav
              items={librarySections}
              active={route.split("/").at(-1) ?? "playlists"}
              label="Library sections"
              class="library-tabs"
            />
          {/if}
        </div>
        <div class="live-state" data-state={liveUpdates.state.status}>
          <span aria-hidden="true"></span>
          {liveUpdates.state.status}
        </div>
      </header>

      {#if onboardingError || onboardingState?.recoveryNotices.includes("backend_identity_missing")}
        <div class="degraded-banner" role="status">
          <span aria-hidden="true">!</span>
          <p><strong>Media server connection needs attention.</strong> {onboardingError || "The identity saved during setup is no longer available. Sign in again or review the backend connection."}</p>
        </div>
      {/if}

      {#if ActiveView && loadedRoute === route}
        <ActiveView {...activeProps} />
      {:else if viewError && loadedRoute === route}
        <section class="panel empty-state" role="alert">
          <p class="eyebrow">View unavailable</p>
          <h2>{activeDestination.label} could not be loaded.</h2>
          <p>{viewError}</p>
        </section>
      {:else if viewLoader(route)}
        <section class="panel skeleton-panel" aria-busy="true" aria-label={`Loading ${activeDestination.label}`}>
          <div class="skeleton-line short"></div>
          <div class="skeleton-card"></div>
        </section>
      {:else}
        <section class="panel empty-state">
          <span class="empty-orbit" aria-hidden="true">✦</span>
          <p class="eyebrow">Svelte migration preview</p>
          <h2>{activeDestination.label} is next in line.</h2>
          <p>
            This opt-in shell is isolated at <code>/next/#/</code>. The current WebUI remains
            available while complete routes move over one at a time.
          </p>
        </section>
      {/if}
    </main>
  </div>
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
