<script lang="ts">
  import { onMount, type Component } from "svelte";
  import { page } from "$app/state";
  import { auth, type Session } from "$lib/api";
  import { liveUpdates } from "$lib/live-updates.svelte";

  const destinations = [
    { href: "#/", label: "Home", icon: "⌂" },
    { href: "#/library/playlists", prefix: "/library/", label: "Library", icon: "♫" },
    { href: "#/sources", label: "Sources", icon: "◎" },
    { href: "#/activity", label: "Activity", icon: "↗" },
    { href: "#/settings", label: "Settings", icon: "⚙" },
  ];

  let session = $state<Session | null>(null);
  let loading = $state(true);
  let error = $state("");
  let username = $state("");
  let password = $state("");
  let rememberMe = $state(true);
  let avatarFailed = $state(false);
  let ActiveView = $state<Component<any>>();
  let loadedRoute = $state("");
  let viewError = $state("");
  let viewRequest = 0;

  const route = $derived(`/${page.params.path ?? ""}`);
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
          ? { initialSearch: routeQuery.get("search") ?? "" }
          : route === "/library/cached"
            ? { storage: "cache" }
            : route === "/library/kept"
              ? { storage: "kept" }
              : route === "/sources"
                ? { administrator: session?.user?.isAdministrator ?? false }
                : route.startsWith("/settings")
                  ? { section: route.split("/")[2] || "general" }
                  : {},
  );

  function viewLoader(path: string) {
    if (path === "/") return import("$lib/components/HomeView.svelte");
    if (path === "/library/playlists") return import("$lib/components/PlaylistsView.svelte");
    if (path === "/library/mappings") return import("$lib/components/MappingView.svelte");
    if (path === "/library/cached" || path === "/library/kept") {
      return import("$lib/components/DownloadsView.svelte");
    }
    if (path === "/activity") return import("$lib/components/EventLogView.svelte");
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

  onMount(() => {
    void (async () => {
      try {
        session = await auth.session();
        if (session.authenticated) liveUpdates.connect();
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
      password = "";
      liveUpdates.connect();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Sign in failed.";
    }
  }

  async function logout() {
    await auth.logout();
    liveUpdates.close();
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
  <div class="app-shell">
    <aside class="sidebar">
      <a class="brand" href="#/" aria-label="Allstarr home">
        <span class="brand-mark">A</span>
        <span>
          <strong>Allstarr</strong>
          <small>beta {__APP_VERSION__}</small>
        </span>
      </a>

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
        <div class="avatar">
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
        </div>
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
            <nav class="library-tabs" aria-label="Library sections">
              <span
                class={`library-tab-indicator ${route.split("/").at(-1)}`}
                aria-hidden="true"
              ></span>
              <a href="#/library/playlists" aria-current={route === "/library/playlists" ? "page" : undefined}>Playlists</a>
              <a href="#/library/mappings" aria-current={route === "/library/mappings" ? "page" : undefined}>Mappings</a>
              <a href="#/library/cached" aria-current={route === "/library/cached" ? "page" : undefined}>Cached</a>
              <a href="#/library/kept" aria-current={route === "/library/kept" ? "page" : undefined}>Kept</a>
            </nav>
          {/if}
        </div>
        <div class="live-state" data-state={liveUpdates.state.status}>
          <span aria-hidden="true"></span>
          {liveUpdates.state.status}
        </div>
      </header>

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
{/if}
