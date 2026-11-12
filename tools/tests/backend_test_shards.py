#!/usr/bin/env python3
"""Print complementary VSTest filters for six regular backend shards."""

from __future__ import annotations

import argparse


# Run 43248829915 TRX seconds, updated with focused before/after measurements.
# Each entry records estimated test-body seconds. Balance the two xUnit collection
# workers, combining the remaining protocol and mix methods on the fifth shard.
# Estimated seconds by shard: 158.7 / 162.0 / 162.7 / 162.5 / 183.8 / 152.3 (before host setup).
# Earlier groups take precedence. The final shard includes every remaining test.
SHARD_GROUPS = (
    (
        "allstarr.Tests.ProtocolRouteFixtureTests.ClientApi_",  # 158.7 s
        "allstarr.Tests.TasteServiceTests.",  # 12.1 s
        "allstarr.Tests.HealthSmokeServiceTests.",  # 11.1 s
        "allstarr.Tests.DurableBackupServiceTests.",  # 11.0 s
        "allstarr.Tests.LibraryIndexServiceTests.",  # 10.2 s
        "allstarr.Tests.TrackMatchRefreshTests.",  # 10.2 s
        "allstarr.Tests.ProviderStatusManagerTests.",  # 10.0 s
        "allstarr.Tests.PersonalMixServiceTests.Large_library_",  # 7.1 s
    ),
    (
        "allstarr.Tests.ProtocolRouteFixtureTests.Jellyfin",  # 118.7 s
        "allstarr.Tests.ListeningHistoryResolutionTests.",  # 31.2 s
        "allstarr.Tests.ExtensionControlPlaneServiceTests.",  # 28.9 s
        "allstarr.Tests.PlaybackSignalPipelineTests.",  # 26.8 s
        "allstarr.Tests.NewReleasesTests.",  # 24.7 s
        "allstarr.Tests.PersonalMixServiceTests.ServiceFeeds_",  # 22.1 s
        "allstarr.Tests.ProviderAudioVerifierAppleProgressiveTests.",  # 19.5 s
        "allstarr.Tests.AdminUiActivityTests.",  # 15.9 s
        "allstarr.Tests.FavoriteActionPipelineTests.",  # 14.9 s
        "allstarr.Tests.PersonalMixServiceTests.Built_in_",  # 10.8 s
        "allstarr.Tests.ProtocolRouteFixtureTests.PassThrough",  # 9.0 s
    ),
    (
        "allstarr.Tests.TrackIdentityServiceTests.",  # 71.2 s
        "allstarr.Tests.MigrationWizardFlowTests.",  # 57.0 s
        "allstarr.Tests.ProtocolRouteFixtureTests.FavoriteRoundTrip",  # 46.7 s
        "allstarr.Tests.ProtocolProviderStreamingGatewayTests.",  # 37.9 s
        "allstarr.Tests.LegacyEnvMigrationServiceTests.",  # 23.7 s
        "allstarr.Tests.PersonalMixAudioTests.",  # 22.6 s
        "allstarr.Tests.PersonalMixServiceTests.Custom_",  # 11.1 s
        "allstarr.Tests.TrackRematchAllIntegrationTests.",  # 16.0 s
        "allstarr.Tests.ProtocolRouteFixtureTests.OfflineDownload",  # 13.5 s
        "allstarr.Tests.PersonalMixServiceTests.Generation_",  # 8.1 s
        "allstarr.Tests.ProtocolRouteFixtureTests.LyricsClients",  # 6.9 s
    ),
    (
        "allstarr.Tests.RecordingKeptFilesTests.",  # 63.6 s
        "allstarr.Tests.ProtocolRouteFixtureTests.Subsonic",  # 58.6 s
        "allstarr.Tests.PlaylistOrchestrationIntegrationTests.",  # 44.0 s
        "allstarr.Tests.LibraryPromotionTests.",  # 39.5 s
        "allstarr.Tests.ProtocolRouteFixtureTests.ClientApiV2",  # 26.9 s
        "allstarr.Tests.ExtensionCapabilityAdapterTests.",  # 23.1 s
        "allstarr.Tests.ProtocolRouteFixtureTests.UserData",  # 18.7 s
        "allstarr.Tests.ProtocolRouteFixtureTests.OriginalDownload",  # 16.4 s
        "allstarr.Tests.DurableStorageTests.",  # 13.4 s
        "allstarr.Tests.FavoriteLinkTests.",  # 11.3 s
        "allstarr.Tests.ProtocolRouteFixtureTests.SearchNativeWindow",  # 7.6 s
    ),
    (
        "allstarr.Tests.ProtocolRouteFixtureTests.",  # 163.5 s
        "allstarr.Tests.PersonalMixServiceTests.",  # 183.8 s
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
