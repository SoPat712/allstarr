#!/usr/bin/env python3
"""Print complementary VSTest filters for the two regular backend shards."""

from __future__ import annotations

import argparse


# Class-duration sums from successful CI TRX artifacts, in seconds:
# run 40988954860: 474.864 / 468.953; run 40985401635: 607.578 / 579.226.
# Keep classes together so shared fixtures retain their normal lifetime.
FIRST_SHARD_CLASSES = (
    "allstarr.Tests.ProtocolRouteFixtureTests",
    "allstarr.Tests.PersonalMixServiceTests",
    "allstarr.Tests.PlaylistOrchestrationIntegrationTests",
    "allstarr.Tests.DurableBackupServiceTests",
)


def test_filter(shard: int) -> str:
    if shard not in (1, 2):
        raise ValueError("shard must be 1 or 2")
    # The trailing dot is the class/method boundary. The second shard is the
    # exact complement, so new classes and methods are included automatically.
    operator, separator = ("~", "|") if shard == 1 else ("!~", "&")
    predicates = separator.join(
        f"FullyQualifiedName{operator}{name}." for name in FIRST_SHARD_CLASSES
    )
    return f"(Lane!=ReleaseCritical)&({predicates})"


def _self_test() -> None:
    # Evaluate the emitted filter subset against representative discovered names.
    # This catches boundary mistakes and proves that the two predicates partition
    # both existing classes and future regular tests while excluding criticals.
    def matches(expression: str, name: str, lane: str) -> bool:
        regular, predicates = expression.split("&(", 1)
        assert regular == "(Lane!=ReleaseCritical)"
        predicates = predicates[:-1]
        if lane == "ReleaseCritical":
            return False
        if "!~" in predicates:
            return all(term.split("!~", 1)[1] not in name for term in predicates.split("&"))
        return any(term.split("~", 1)[1] in name for term in predicates.split("|"))

    names = [f"{name}.Method" for name in FIRST_SHARD_CLASSES]
    names += [f"{name}Extra.Method" for name in FIRST_SHARD_CLASSES]
    names += ["allstarr.Tests.FutureTests.NewMethod", "Other.Namespace.NewTests.Method"]
    for name in names:
        assert sum(matches(test_filter(shard), name, "") for shard in (1, 2)) == 1
        assert not any(matches(test_filter(shard), name, "ReleaseCritical") for shard in (1, 2))
    for name in FIRST_SHARD_CLASSES:
        assert matches(test_filter(1), f"{name}.NewMethod", "")
        assert matches(test_filter(2), f"{name}Extra.Method", "")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shard", type=int, choices=(1, 2))
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
