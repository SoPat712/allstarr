#!/usr/bin/env python3
"""Print complementary VSTest filters for six regular backend shards."""

from __future__ import annotations

import argparse


# Run 41303998651 TRX duration sums: 238 / 246 / 247 / 263 / 245 / 246 seconds.
# Split the large protocol class by method prefix; keep other classes together.
# Earlier groups take precedence. The final shard includes every remaining test.
SHARD_GROUPS = (
    (
        "allstarr.Tests.ProtocolRouteFixtureTests.Jellyfin",
        "allstarr.Tests.ExtensionCapabilityAdapterTests.",
        "allstarr.Tests.LegacyEnvMigrationServiceTests.",
        "allstarr.Tests.TasteServiceTests.",
        "allstarr.Tests.StartupConfigurationTests.",
        "allstarr.Tests.FavoriteLinkTests.",
    ),
    (
        "allstarr.Tests.ProtocolRouteFixtureTests.Subsonic",
        "allstarr.Tests.PlaylistOrchestrationIntegrationTests.",
        "allstarr.Tests.ProviderStatusManagerTests.",
        "allstarr.Tests.FavoriteActionPipelineTests.",
        "allstarr.Tests.MixCoverRendererTests.",
        "allstarr.Tests.DurableBackupServiceTests.",
        "allstarr.Tests.TrackMatchRefreshTests.",
        "allstarr.Tests.ExtensionProgressivePlaybackTests.",
    ),
    (
        "allstarr.Tests.ProtocolRouteFixtureTests.",
    ),
    (
        "allstarr.Tests.PersonalMixServiceTests.",
    ),
    (
        "allstarr.Tests.RecordingKeptFilesTests.",
        "allstarr.Tests.MigrationWizardFlowTests.",
        "allstarr.Tests.TrackIdentityServiceTests.",
        "allstarr.Tests.PlaybackSignalPipelineTests.",
        "allstarr.Tests.ProviderAccountCheckRunnerTests.",
        "allstarr.Tests.ListeningHistoryResolutionTests.",
        "allstarr.Tests.ProtocolProviderStreamingGatewayTests.",
        "allstarr.Tests.ExtensionControlPlaneServiceTests.",
        "allstarr.Tests.TrackRematchAllIntegrationTests.",
    ),
)
SHARD_COUNT = len(SHARD_GROUPS) + 1


def test_filter(shard: int) -> str:
    if shard not in range(1, SHARD_COUNT + 1):
        raise ValueError(f"shard must be between 1 and {SHARD_COUNT}")
    predicates = ["(Lane!=ReleaseCritical)"]
    if shard <= len(SHARD_GROUPS):
        predicates.append("(" + "|".join(
            f"FullyQualifiedName~{name}" for name in SHARD_GROUPS[shard - 1]
        ) + ")")
    excluded = [name for group in SHARD_GROUPS[:shard - 1] for name in group]
    if excluded:
        predicates.append("(" + "&".join(
            f"FullyQualifiedName!~{name}" for name in excluded
        ) + ")")
    return "&".join(predicates)


def _self_test() -> None:
    # Evaluate the emitted filter grammar, including overlaps and future methods.
    def matches(expression: str, name: str, lane: str) -> bool:
        if lane == "ReleaseCritical":
            return False
        assert expression.startswith("(Lane!=ReleaseCritical)")
        groups = expression.split(")&(")[1:]
        for group in groups:
            group = group.rstrip(")")
            if "!~" in group:
                if not all(term.split("!~", 1)[1] not in name for term in group.split("&")):
                    return False
            elif not any(term.split("~", 1)[1] in name for term in group.split("|")):
                return False
        return True

    names = [name + suffix for group in SHARD_GROUPS for name in group
             for suffix in ("NewMethod", "Extra.Method")]
    names += ["allstarr.Tests.FutureTests.NewMethod", "Other.Namespace.NewTests.Method"]
    for name in names:
        assert sum(matches(test_filter(shard), name, "")
                   for shard in range(1, SHARD_COUNT + 1)) == 1
        assert not any(matches(test_filter(shard), name, "ReleaseCritical")
                       for shard in range(1, SHARD_COUNT + 1))
    for shard, group in enumerate(SHARD_GROUPS, 1):
        assert matches(test_filter(shard), group[0] + "NewMethod", "")
    for group in SHARD_GROUPS:
        for prefix in group:
            if prefix.endswith("."):
                assert matches(test_filter(SHARD_COUNT), prefix[:-1] + "Extra.Method", "")
    for shard in (0, SHARD_COUNT + 1):
        try:
            test_filter(shard)
        except ValueError:
            pass
        else:
            raise AssertionError("invalid shard accepted")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shard", type=int, choices=range(1, SHARD_COUNT + 1))
    parser.add_argument("--self-test", action="store_true")
    options = parser.parse_args()
    if options.self_test:
        _self_test()
    elif options.shard is not None:
        print(test_filter(options.shard))
    else:
        parser.error("--shard or --self-test is required")


if __name__ == "__main__":
    main()
