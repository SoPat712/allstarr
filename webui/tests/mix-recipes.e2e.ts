import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou } from "./for-you-fixtures";
import type { MixRecipeEvidence } from "../src/lib/api";

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) for (const scenario of ["historical", "long-lost", "insufficient", "legacy"]) {
  test(`Recipe details ${scenario} ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await page.emulateMedia({ reducedMotion: "reduce" });
    const state = await mockForYou(page);
    const kind = scenario === "historical" ? "on-repeat" : scenario === "long-lost" ? "rewind" : "chill";
    const card = state.cards.find(item => item.kind === kind)!;
    const evidence: MixRecipeEvidence = {
      recipe: { version: "fixture-v1", sentence: scenario === "historical" ? "Your most-played songs in 30 days, filled from 90 days." : scenario === "long-lost" ? "Songs played at least 5 times, with no play in 6 months." : "Songs with chill, mellow, calm or dreamy tags, or qualifying sound analysis.", inputs: "Completed plays and saved tags.", filters: "Leaves out songs you can't play, explicit songs on clean-only accounts, anything you marked Never, songs you skipped early 3 times in 30 days, and repeats. Songs you marked Less come later.", order: "Most plays first.", target: 50, artistLimit: scenario === "long-lost" ? 3 : 2, cadence: "Updates every Monday", shortfall: "Keep eligible songs when fewer meet the rules." },
      livePlays: 4, importedPlays: 62284, listeningFrom: "2024-02-01T00:00:00Z", listeningTo: "2026-10-29T00:00:00Z", historicalWindow: scenario === "historical" ? "01 Oct 2025–31 Oct 2025" : null, primaryPlays: 4, fallbackPlays: 214, candidates: 86, qualified: 62, shown: state.songs.length, fromMusic: 6, fromServices: 2, excluded: { "Three early skips": 3, Never: 1 }, providerReads: 22, providerFailures: scenario === "long-lost" ? 2 : 0, shortfall: scenario === "insufficient" ? "No more songs met this recipe's rules in the available music and services." : null,
    };
    if (scenario !== "legacy") card.evidence = evidence;
    if (scenario === "historical") card.inApps = false;
    if (scenario === "insufficient") { card.readiness = { ready: false, message: evidence.shortfall! }; state.songs = []; evidence.shown = 0; }
    else state.songs[0].reason = scenario === "historical" ? "Played 31 times · imported 01 Oct 2025–31 Oct 2025" : "Played 31 times, last in Feb 2024";
    await page.goto(`/#/for-you?mix=${kind}`);
    const summary = page.getByText("How this mix is made", { exact: true });
    if (scenario === "legacy") {
      await expect(page.getByRole("dialog")).toBeVisible();
      await expect(summary).toHaveCount(0);
      await expect(page.locator("details.recipe")).toHaveCount(0);
      await capture(page, `recipes-${scenario}-collapsed`, width, theme);
      expect(state.calls.filter(call => call.path.endsWith("/refresh"))).toHaveLength(0); expect(await page.pageErrors()).toEqual([]);
      if (width === 1280 && theme === "dark") await page.screenshot({ path: test.info().outputPath("mix-recipe-absent-1280-dark.png") });
      return;
    }
    await expect(summary).toBeVisible(); expect((await summary.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await expect(page.locator("details.recipe")).toHaveClass(/settings-fold/);
    const copy = page.locator("details.recipe").getByText(evidence.recipe.sentence, { exact: true });
    await expect(copy).toBeHidden(); await capture(page, `recipes-${scenario}-collapsed`, width, theme);
    await summary.focus(); await page.keyboard.press("Space"); await expect(copy).toBeVisible();
    if (scenario === "historical") await expect(page.getByText(/Only 4 plays in Allstarr in the last 30 days, so this also uses 214 imported plays from 01 Oct 2025–31 Oct 2025/)).toBeVisible();
    await expect(page.getByText("Left out: 3 skipped early · 1 you marked Never.", { exact: true })).toBeVisible();
    await expect(page.getByText(/^4 plays in Allstarr/)).toBeVisible();
    await expect(page.getByText(`Looked at 86 songs, 62 fit, ${evidence.shown} in this mix.`, { exact: true })).toBeVisible();
    await expect(page.getByText(`6 from your music, 2 from your services, at most ${evidence.recipe.artistLimit} per artist.`, { exact: true })).toBeVisible();
    await expect(page.getByText(/Saved |song identifier|stable song order/)).toHaveCount(0);
    await expect(page.getByText("2 service lookups failed.", { exact: true })).toHaveCount(scenario === "long-lost" ? 1 : 0);
    if (scenario === "insufficient") await expect(page.getByText(evidence.shortfall!, { exact: true })).toHaveCount(1);
    await capture(page, `recipes-${scenario}-expanded`, width, theme);
    if (width === 1280 && theme === "dark") await page.screenshot({ path: test.info().outputPath("mix-recipe-expanded-1280-dark.png") });
    if (scenario === "historical") {
      // The fold lines up with the panel's content and sits right under the apps switch.
      const fold = page.locator("details.recipe"), body = (await page.locator(".panel-body").boundingBox())!;
      await expect(fold).toHaveAttribute("data-placement", "flush");
      expect((await fold.locator("summary").boundingBox())!.x).toBeCloseTo(body.x + 16, 0);
      expect((await fold.boundingBox())!.y - (await page.locator(".mix-summary > :last-child").boundingBox())!.y - (await page.locator(".mix-summary > :last-child").boundingBox())!.height).toBeLessThanOrEqual(0);
      await page.locator(".panel-body").evaluate(element => { element.scrollTop = 0; });
      await page.screenshot({ path: test.info().outputPath(`01-mix-panel-${width}-${theme}.png`) });
    }
    if (scenario !== "insufficient") await expect(page.getByText(state.songs[0].reason, { exact: true })).toBeAttached();
    expect(state.calls.filter(call => call.path.endsWith("/refresh"))).toHaveLength(0); expect(await page.pageErrors()).toEqual([]);
  });
}
