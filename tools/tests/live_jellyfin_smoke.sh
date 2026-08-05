#!/usr/bin/env bash
set -euo pipefail

: "${JELLYFIN_TOKEN:?Set JELLYFIN_TOKEN to a temporary Jellyfin API key or access token}"

DIRECT_BASE="${DIRECT_BASE:-https://jellyfin.joshpatra.me}"
ALLSTARR_BASE="${ALLSTARR_BASE:-https://jfm.joshpatra.me}"
JELLYFIN_USER_ID="${JELLYFIN_USER_ID:-}"
SAMPLES="${SAMPLES:-3}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-20}"
TEST_EXTERNAL_STREAM="${TEST_EXTERNAL_STREAM:-0}"
TEST_PLAYLIST_WRITES="${TEST_PLAYLIST_WRITES:-0}"
PLAYLIST_WRITE_CONFIRM="${PLAYLIST_WRITE_CONFIRM:-}"
INJECTED_PLAYLIST_ID="${INJECTED_PLAYLIST_ID:-}"
INJECTED_PLAYLIST_EXPECTED_COUNT="${INJECTED_PLAYLIST_EXPECTED_COUNT:-}"
EXTERNAL_SONG_ID="${EXTERNAL_SONG_ID:-}"
EXTERNAL_PROVIDER_CASES="${EXTERNAL_PROVIDER_CASES:-[]}" # [{"provider":"extension-id","songId":"ext-extension-id-song-123"}]
EXPECTED_EXTERNAL_PROVIDERS="${EXPECTED_EXTERNAL_PROVIDERS:-}"
REQUIRE_EXTERNAL="${REQUIRE_EXTERNAL:-0}"
NATIVE_ROUTE_SAMPLES="${NATIVE_ROUTE_SAMPLES:-10}"
MAX_EXTERNAL_METADATA_TTFB_MS="${MAX_EXTERNAL_METADATA_TTFB_MS:-2000}"
MAX_EXTERNAL_ARTWORK_TTFB_MS="${MAX_EXTERNAL_ARTWORK_TTFB_MS:-2000}"
MAX_EXTERNAL_STREAM_TTFB_MS="${MAX_EXTERNAL_STREAM_TTFB_MS:-8000}"

for command in curl jq awk diff cmp head mkfifo od tr wc; do
    command -v "$command" >/dev/null || { echo "Missing required command: $command" >&2; exit 1; }
done
if command -v sha256sum >/dev/null; then
    sha256_command=(sha256sum)
elif command -v shasum >/dev/null; then
    sha256_command=(shasum -a 256)
else
    echo "Missing required command: sha256sum or shasum" >&2
    exit 1
fi
[[ "$SAMPLES" =~ ^[1-9][0-9]*$ ]] || { echo "SAMPLES must be a positive integer" >&2; exit 1; }
[[ "$TEST_EXTERNAL_STREAM" == 0 || "$TEST_EXTERNAL_STREAM" == 1 ]] ||
    { echo "TEST_EXTERNAL_STREAM must be 0 or 1" >&2; exit 1; }
[[ "$TEST_PLAYLIST_WRITES" == 0 || "$TEST_PLAYLIST_WRITES" == 1 ]] ||
    { echo "TEST_PLAYLIST_WRITES must be 0 or 1" >&2; exit 1; }
[[ "$REQUIRE_EXTERNAL" == 0 || "$REQUIRE_EXTERNAL" == 1 ]] ||
    { echo "REQUIRE_EXTERNAL must be 0 or 1" >&2; exit 1; }
[[ "$NATIVE_ROUTE_SAMPLES" =~ ^[1-9][0-9]*$ ]] && (( NATIVE_ROUTE_SAMPLES <= 25 )) ||
    { echo "NATIVE_ROUTE_SAMPLES must be between 1 and 25" >&2; exit 1; }
[[ "$MAX_EXTERNAL_METADATA_TTFB_MS" =~ ^[0-9]+([.][0-9]+)?$ ]] ||
    { echo "MAX_EXTERNAL_METADATA_TTFB_MS must be numeric" >&2; exit 1; }
[[ "$MAX_EXTERNAL_ARTWORK_TTFB_MS" =~ ^[0-9]+([.][0-9]+)?$ ]] ||
    { echo "MAX_EXTERNAL_ARTWORK_TTFB_MS must be numeric" >&2; exit 1; }
[[ "$MAX_EXTERNAL_STREAM_TTFB_MS" =~ ^[0-9]+([.][0-9]+)?$ ]] ||
    { echo "MAX_EXTERNAL_STREAM_TTFB_MS must be numeric" >&2; exit 1; }
if [[ -n "$EXTERNAL_SONG_ID" ]]; then
    [[ "$EXTERNAL_SONG_ID" =~ ^ext-[[:alnum:]_-]+-song-[[:alnum:]_.:-]+$ ]] ||
        { echo "EXTERNAL_SONG_ID must be a typed external song ID" >&2; exit 1; }
fi
jq -e 'type == "array" and all(.[];
    (.provider | type == "string" and length > 0) and
    (.songId | type == "string" and test("^ext-.+-song-.+$")))' \
    <<<"$EXTERNAL_PROVIDER_CASES" >/dev/null ||
    { echo "EXTERNAL_PROVIDER_CASES must be a JSON array of provider/songId objects" >&2; exit 1; }
if [[ -n "$JELLYFIN_USER_ID" ]]; then
    [[ "$JELLYFIN_USER_ID" =~ ^[[:alnum:]_-]{1,128}$ ]] ||
        { echo "JELLYFIN_USER_ID must be a stable Jellyfin user ID" >&2; exit 1; }
fi
if [[ "$TEST_PLAYLIST_WRITES" == 1 &&
      "$PLAYLIST_WRITE_CONFIRM" != create-and-delete-throwaway-playlist ]]; then
    echo "TEST_PLAYLIST_WRITES=1 requires PLAYLIST_WRITE_CONFIRM=create-and-delete-throwaway-playlist" >&2
    exit 1
fi
if [[ -n "$INJECTED_PLAYLIST_ID" || -n "$INJECTED_PLAYLIST_EXPECTED_COUNT" ]]; then
    [[ "$INJECTED_PLAYLIST_ID" =~ ^[[:alnum:]_-]{1,128}$ ]] ||
        { echo "INJECTED_PLAYLIST_ID must be a stable playlist ID" >&2; exit 1; }
    [[ "$INJECTED_PLAYLIST_EXPECTED_COUNT" =~ ^[1-9][0-9]*$ ]] ||
        { echo "INJECTED_PLAYLIST_EXPECTED_COUNT must be a positive integer" >&2; exit 1; }
fi

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
run_id="${started_at//[:T-]/}"
users_file="$(mktemp)"
items_file="$(mktemp)"
response_file="$(mktemp)"
timings_file="$(mktemp)"
direct_shape_file="$(mktemp)"
allstarr_shape_file="$(mktemp)"
metrics_file="$(mktemp)"
direct_media_file="$(mktemp)"
allstarr_media_file="$(mktemp)"
direct_headers_file="$(mktemp)"
allstarr_headers_file="$(mktemp)"
virtual_items_file="$(mktemp)"
direct_virtual_items_file="$(mktemp)"
direct_playlists_file="$(mktemp)"
allstarr_playlists_file="$(mktemp)"
external_search_file="$(mktemp)"
provider_cases_file="$(mktemp)"
stream_pipe="$(mktemp)"
rm -f "$stream_pipe"
mkfifo "$stream_pipe"
stateful_playlist_id=""
stateful_playlist_name=""
stateful_playlist_original_name=""
playlist_identity_matches() {
    local playlist_id="$1" playlist_name="$2"
    [[ "$playlist_id" =~ ^[[:alnum:]_-]{1,128}$ ]] || return 1
    curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$DIRECT_BASE/Items/$playlist_id?UserId=$best_user_id" |
        jq -e --arg id "$playlist_id" --arg name "$playlist_name" \
            '.Id == $id and .Name == $name and .Type == "Playlist"' >/dev/null
}
cleanup() {
    local cleanup_delete_code cleanup_probe_code
    if [[ -n "$stateful_playlist_id" ]]; then
        if playlist_identity_matches "$stateful_playlist_id" "$stateful_playlist_name" ||
           playlist_identity_matches "$stateful_playlist_id" "$stateful_playlist_original_name"; then
            cleanup_delete_code="$(curl -sS -X DELETE --max-time "$TIMEOUT_SECONDS" \
                -H "X-Emby-Token: $JELLYFIN_TOKEN" \
                "$DIRECT_BASE/Items/$stateful_playlist_id" -o /dev/null -w '%{http_code}' 2>/dev/null || true)"
            cleanup_probe_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
                "$DIRECT_BASE/Items/$stateful_playlist_id?UserId=$best_user_id" \
                -o /dev/null -w '%{http_code}' 2>/dev/null || true)"
            if [[ "$cleanup_delete_code" == 204 && "$cleanup_probe_code" == 404 ]]; then
                echo 'PASS cleanup exact throwaway playlist'
            else
                printf 'CLEANUP-BLOCKED exact playlist delete=%s verify=%s\n' \
                    "${cleanup_delete_code:-000}" "${cleanup_probe_code:-000}" >&2
            fi
        else
            echo 'CLEANUP-BLOCKED exact playlist ID/name/type verification failed' >&2
        fi
    fi
    rm -f "$users_file" "$items_file" "$response_file" "$timings_file" \
        "$direct_shape_file" "$allstarr_shape_file" "$metrics_file" \
        "$direct_media_file" "$allstarr_media_file" "$direct_headers_file" \
        "$allstarr_headers_file" "$virtual_items_file" "$direct_virtual_items_file" \
        "$direct_playlists_file" "$allstarr_playlists_file" "$external_search_file"
    rm -f "$provider_cases_file" "$stream_pipe"
}
trap cleanup EXIT

auth=(-H "X-Emby-Token: $JELLYFIN_TOKEN" -H "User-Agent: AllstarrLiveSmoke/$run_id")
echo "live-smoke-start=$started_at samples=$SAMPLES range_bytes=65536 external_stream=$TEST_EXTERNAL_STREAM playlist_writes=$TEST_PLAYLIST_WRITES"

curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$DIRECT_BASE/Users" -o "$users_file"
best_user_id=""
best_audio_count=-1
user_candidates="$(jq -r '.[].Id' "$users_file")"
if [[ -n "$JELLYFIN_USER_ID" ]]; then
    jq -e --arg id "$JELLYFIN_USER_ID" 'any(.[]; .Id == $id)' "$users_file" >/dev/null ||
        { echo "JELLYFIN_USER_ID is not visible to this credential" >&2; exit 1; }
    user_candidates="$JELLYFIN_USER_ID"
fi
while IFS= read -r user_id; do
    if ! curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$DIRECT_BASE/Users/$user_id/Items?Recursive=true&IncludeItemTypes=Audio&Limit=1" -o "$response_file"; then
        continue
    fi
    audio_count="$(jq -r '.TotalRecordCount // 0' "$response_file")"
    first_audio_id="$(jq -r '.Items[0].Id // empty' "$response_file")"
    if [[ -n "$first_audio_id" ]]; then
        probe_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" -o /dev/null -w '%{http_code}' \
            "$ALLSTARR_BASE/Users/$user_id/Items/$first_audio_id" || true)"
    else
        probe_code=0
    fi
    if [[ "$probe_code" == 200 ]] && (( audio_count > best_audio_count )); then
        best_user_id="$user_id"
        best_audio_count="$audio_count"
    fi
done <<<"$user_candidates"
[[ -n "$best_user_id" && "$best_audio_count" -gt 0 ]] ||
    { echo "No Jellyfin user with audio visible to Allstarr was found" >&2; exit 1; }
auth=(-H "X-Emby-Authorization: MediaBrowser Client=\"AllstarrLiveSmoke\", Device=\"Qualification\", DeviceId=\"$run_id\", Version=\"1\", UserId=\"$best_user_id\", Token=\"$JELLYFIN_TOKEN\"" \
      -H "User-Agent: AllstarrLiveSmoke/$run_id")
actor_bound=0
if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
       "$ALLSTARR_BASE/Users/Me" -o "$response_file" 2>/dev/null &&
   jq -e --arg id "$best_user_id" '.Id == $id' "$response_file" >/dev/null; then
    actor_bound=1
fi
echo "jellyfin-user=selected actor_bound=$actor_bound"

full_item_fields="AirTime,CanDelete,CanDownload,ChannelInfo,Chapters,Trickplay,ChildCount,CumulativeRunTimeTicks,CustomRating,DateCreated,DateLastMediaAdded,DisplayPreferencesId,Etag,ExternalUrls,Genres,ItemCounts,MediaSourceCount,MediaSources,OriginalTitle,Overview,ParentId,Path,People,PlayAccess,ProductionLocations,ProviderIds,PrimaryImageAspectRatio,RecursiveItemCount,Settings,SeriesStudio,SortName,SpecialEpisodeNumbers,Studios,Taglines,Tags,RemoteTrailers,MediaStreams,SeasonUserData,DateLastRefreshed,DateLastSaved,RefreshState,ChannelImage,EnableMediaSourceDisplay,Width,Height,ExtraIds,LocalTrailerCount,IsHD,SpecialFeatureCount"
items_query="Recursive=true&IncludeItemTypes=Audio&Limit=100&Fields=PrimaryImageAspectRatio%2CProviderIds%2CMediaSources%2CAlbumId%2CArtistItems%2CGenres"
curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$items_query" -o "$items_file"
media_id="$(jq -r 'first(.Items[] | select(((.MediaSources // []) | length) > 0)) | .Id // empty' "$items_file")"
second_media_id="$(jq -r --arg first "$media_id" \
    'first(.Items[] | select(.Id != $first and ((.MediaSources // []) | length) > 0)) | .Id // empty' \
    "$items_file")"
art_id="$(jq -r 'first(.Items[] | select(.ImageTags.Primary != null)) | .Id // empty' "$items_file")"
lyrics_id="$(jq -r 'first(.Items[] | select(.HasLyrics == true)) | .Id // empty' "$items_file")"
album_id="$(jq -r 'first(.Items[] | select(.AlbumId != null)) | .AlbumId // empty' "$items_file")"
artist_id="$(jq -r 'first(.Items[] | select(((.ArtistItems // []) | length) > 0)) | .ArtistItems[0].Id // empty' "$items_file")"
search_term="$(jq -r 'first(.Items[] | select(.Name != null)) | .Name // empty' "$items_file")"
search_term_encoded="$(jq -rn --arg value "$search_term" '$value | @uri')"
[[ -n "$media_id" ]] || { echo "No streamable audio item found in the first 100 items" >&2; exit 1; }

measure() {
    local label="$1" url="$2"
    shift 2
    : >"$timings_file"
    for ((sample = 1; sample <= SAMPLES; sample++)); do
        curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$@" -o /dev/null \
            -w '%{http_code}\t%{size_download}\t%{time_namelookup}\t%{time_connect}\t%{time_appconnect}\t%{time_starttransfer}\t%{time_total}\n' \
            "$url" >>"$timings_file" || true
    done
    awk -v label="$label" -v metrics="$metrics_file" '
        {
            ok += ($1 >= 200 && $1 < 400); bytes += $2; dns += $3; connect += $4;
            tls += $5; ttfb += $6; total += $7; codes[$1]++;
            if (NR == 1) { cold_ttfb = $6; cold_total = $7 }
            else { warm_ttfb += $6; warm_total += $7; warm_count++ }
        }
        END {
            code_summary = ""
            for (code in codes) code_summary = code_summary (code_summary ? "," : "") code ":" codes[code]
            if (!warm_count) { warm_ttfb = cold_ttfb; warm_total = cold_total; warm_count = 1 }
            printf "%-24s ok=%d/%d codes=%s avg_bytes=%.0f dns_ms=%.1f connect_ms=%.1f tls_ms=%.1f cold_ttfb_ms=%.1f warm_ttfb_ms=%.1f ttfb_ms=%.1f total_ms=%.1f\n",
                label, ok, NR, code_summary, bytes / NR, dns * 1000 / NR, connect * 1000 / NR,
                tls * 1000 / NR, cold_ttfb * 1000, warm_ttfb * 1000 / warm_count,
                ttfb * 1000 / NR, total * 1000 / NR
            printf "%s\t%.3f\t%.3f\t%d\t%d\n",
                label, ttfb * 1000 / NR, total * 1000 / NR, ok, NR >> metrics
        }' "$timings_file"
}

timing_delta() {
    local label="$1" direct_label="$2" allstarr_label="$3"
    awk -F '\t' -v label="$label" -v direct_label="$direct_label" -v allstarr_label="$allstarr_label" '
        $1 == direct_label { direct_ttfb = $2; direct_total = $3 }
        $1 == allstarr_label { allstarr_ttfb = $2; allstarr_total = $3 }
        END {
            if (direct_ttfb != "" && allstarr_ttfb != "")
                printf "%-24s ttfb_delta_ms=%+.1f total_delta_ms=%+.1f\n",
                    label, allstarr_ttfb - direct_ttfb, allstarr_total - direct_total
        }' "$metrics_file"
}

timing_budget() {
    local label="$1" metric_label="$2" max_ttfb_ms="$3" value successful total
    value="$(awk -F '\t' -v metric_label="$metric_label" '$1 == metric_label { print $2; exit }' "$metrics_file")"
    successful="$(awk -F '\t' -v metric_label="$metric_label" '$1 == metric_label { print $4; exit }' "$metrics_file")"
    total="$(awk -F '\t' -v metric_label="$metric_label" '$1 == metric_label { print $5; exit }' "$metrics_file")"
    checks=$((checks + 1))
    if [[ -n "$value" && -n "$successful" && "$successful" == "$total" ]] &&
       awk -v value="$value" -v max="$max_ttfb_ms" 'BEGIN { exit !(value <= max) }'; then
        printf 'PASS %-34s avg_ttfb_ms=%s max=%s\n' "$label" "$value" "$max_ttfb_ms"
    else
        printf 'FAIL %-34s avg_ttfb_ms=%s max=%s ok=%s/%s\n' \
            "$label" "${value:-missing}" "$max_ttfb_ms" "${successful:-0}" "${total:-0}"
        failures=$((failures + 1))
    fi
}

checks=0
failures=0
blocked=0
last_stream_ranges_supported=0

block() {
    blocked=$((blocked + 1))
    printf 'BLOCKED %s\n' "$1"
}

check_code() {
    local label="$1" expected="$2" method="$3" url="$4" code
    shift 4
    if [[ "$method" == HEAD ]]; then
        code="$(curl -sS --head --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$@" -o /dev/null -w '%{http_code}' "$url" || true)"
    else
        code="$(curl -sS -X "$method" --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$@" -o /dev/null -w '%{http_code}' "$url" || true)"
    fi
    code="${code:-000}"
    checks=$((checks + 1))
    if [[ ",$expected," == *",$code,"* ]]; then
        printf 'PASS %-34s status=%s\n' "$label" "$code"
    else
        printf 'FAIL %-34s expected=%s actual=%s\n' "$label" "$expected" "$code"
        failures=$((failures + 1))
    fi
}

stateful_call() {
    local label="$1" expected="$2" method="$3" url="$4" code
    shift 4
    : >"$response_file"
    code="$(curl -sS -X "$method" --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$@" \
        -o "$response_file" -w '%{http_code}' "$url" || true)"
    code="${code:-000}"
    checks=$((checks + 1))
    if [[ ",$expected," == *",$code,"* ]]; then
        printf 'PASS %-34s status=%s\n' "$label" "$code"
        return 0
    fi
    printf 'FAIL %-34s expected=%s actual=%s\n' "$label" "$expected" "$code"
    failures=$((failures + 1))
    return 1
}

check_public_code() {
    local label="$1" expected="$2" url="$3" code
    code="$(curl -sS --max-time "$TIMEOUT_SECONDS" -H "User-Agent: AllstarrLiveSmoke/$run_id" \
        -o /dev/null -w '%{http_code}' "$url" || true)"
    code="${code:-000}"
    checks=$((checks + 1))
    if [[ ",$expected," == *",$code,"* ]]; then
        printf 'PASS %-34s status=%s\n' "$label" "$code"
    else
        printf 'FAIL %-34s expected=%s actual=%s\n' "$label" "$expected" "$code"
        failures=$((failures + 1))
    fi
}

check_json() {
    local label="$1" url="$2" filter="$3" code
    shift 3
    : >"$response_file"
    code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" -o "$response_file" -w '%{http_code}' "$url" || true)"
    code="${code:-000}"
    checks=$((checks + 1))
    if [[ "$code" == 200 ]] && jq -e "$@" "$filter" "$response_file" >/dev/null; then
        printf 'PASS %-34s json-shape\n' "$label"
    else
        printf 'FAIL %-34s status=%s json-filter=%s\n' "$label" "$code" "$filter"
        failures=$((failures + 1))
    fi
}

wait_json() {
    local label="$1" url="$2" filter="$3" code attempt streak=0
    shift 3
    for ((attempt = 1; attempt <= 20; attempt++)); do
        : >"$response_file"
        code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
            -o "$response_file" -w '%{http_code}' "$url" || true)"
        if [[ "$code" == 200 ]] && jq -e "$@" "$filter" "$response_file" >/dev/null; then
            streak=$((streak + 1))
            if (( streak == 5 )); then
                checks=$((checks + 1))
                printf 'PASS %-34s stable-json\n' "$label"
                return 0
            fi
        else
            streak=0
        fi
        sleep 0.25
    done
    checks=$((checks + 1))
    failures=$((failures + 1))
    printf 'FAIL %-34s status=%s stable-json-filter=%s\n' "$label" "${code:-000}" "$filter"
    return 1
}

check_query_json() {
    local label="$1" url="$2" credential_name="$3" filter="$4" separator="?" code
    shift 4
    [[ "$url" == *\?* ]] && separator="&"
    : >"$response_file"
    code="$(curl -sS --max-time "$TIMEOUT_SECONDS" \
        -H "User-Agent: AllstarrLiveSmoke/$run_id" \
        -o "$response_file" -w '%{http_code}' \
        "$url${separator}${credential_name}=$JELLYFIN_TOKEN" || true)"
    code="${code:-000}"
    checks=$((checks + 1))
    if [[ "$code" == 200 ]] && jq -e "$@" "$filter" "$response_file" >/dev/null; then
        printf 'PASS %-34s json-shape\n' "$label"
    else
        printf 'FAIL %-34s status=%s json-filter=%s\n' "$label" "$code" "$filter"
        failures=$((failures + 1))
    fi
}

check_optional_json() {
    local label="$1" url="$2" filter="$3" code
    shift 3
    : >"$response_file"
    code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        -o "$response_file" -w '%{http_code}' "$url" || true)"
    code="${code:-000}"
    checks=$((checks + 1))
    if [[ "$code" == 404 ]]; then
        printf 'PASS %-34s status=404 genuine-miss\n' "$label"
    elif [[ "$code" == 200 ]] && jq -e "$@" "$filter" "$response_file" >/dev/null; then
        printf 'PASS %-34s status=200 json-shape\n' "$label"
    else
        printf 'FAIL %-34s status=%s json-filter=%s\n' "$label" "$code" "$filter"
        failures=$((failures + 1))
    fi
}

image_signature_matches() {
    local format="$1" signature
    signature="$(od -An -tx1 -N12 "$response_file" |
        awk '{ for (i = 1; i <= NF; i++) printf "%s", $i }')"
    case "$format" in
        jpg|jpeg) [[ "$signature" == ffd8ff* ]] ;;
        png) [[ "$signature" == 89504e470d0a1a0a* ]] ;;
        webp) [[ "$signature" == 52494646????????57454250 ]] ;;
        *) return 1 ;;
    esac
}

check_image() {
    local label="$1" url="$2" expected_format="${3:-}" result code content_type bytes
    local expected_type="" format_ok=1
    : >"$response_file"
    result="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" -o "$response_file" \
        -w '%{http_code}\t%{content_type}\t%{size_download}' "$url" || true)"
    IFS=$'\t' read -r code content_type bytes <<<"$result"
    code="${code:-000}"
    bytes="${bytes:-0}"
    case "$expected_format" in
        "") ;;
        jpg|jpeg) expected_type="image/jpeg" ;;
        png) expected_type="image/png" ;;
        webp) expected_type="image/webp" ;;
        *) format_ok=0 ;;
    esac
    if [[ -n "$expected_type" ]] &&
       { [[ "${content_type%%;*}" != "$expected_type" ]] ||
         ! image_signature_matches "$expected_format"; }; then
        format_ok=0
    fi
    checks=$((checks + 1))
    if [[ "$code" == 200 && "$content_type" == image/* &&
          "${bytes%.*}" -ge 256 && "$format_ok" -eq 1 ]]; then
        printf 'PASS %-34s type=%s bytes=%s\n' "$label" "$content_type" "$bytes"
    else
        printf 'FAIL %-34s status=%s type=%s expected_format=%s bytes=%s\n' \
            "$label" "$code" "$content_type" "${expected_format:-any}" "$bytes"
        failures=$((failures + 1))
    fi
}

check_external_stream() {
    local label="$1" url="$2" range="${3:-0-65535}" result code content_type bytes ttfb total reader_pid
    local content_range accept_ranges saved_bytes timely=0
    : >"$response_file"
    : >"$direct_headers_file"
    head -c 65536 <"$stream_pipe" >"$response_file" &
    reader_pid=$!
    result="$(curl -s --max-time "$TIMEOUT_SECONDS" "${auth[@]}" --range "$range" \
        -D "$direct_headers_file" \
        -o "$stream_pipe" \
        -w '%{http_code}\t%{content_type}\t%{size_download}\t%{time_starttransfer}\t%{time_total}' \
        "$url" || true)"
    wait "$reader_pid" || true
    IFS=$'\t' read -r code content_type bytes ttfb total <<<"$result"
    code="${code:-000}"
    bytes="${bytes:-0}"
    checks=$((checks + 1))
    saved_bytes="$(wc -c <"$response_file" | tr -d ' ')"
    content_range="$(awk 'tolower($1) == "content-range:" { print tolower($2) }' "$direct_headers_file" | tail -n 1 | tr -d '\r')"
    accept_ranges="$(awk 'tolower($1) == "accept-ranges:" { print tolower($2) }' "$direct_headers_file" | tail -n 1 | tr -d '\r')"
    if awk -v value="${ttfb:-0}" -v max="$MAX_EXTERNAL_STREAM_TTFB_MS" \
        'BEGIN { exit !((value * 1000) <= max) }'; then
        timely=1
    fi
    if [[ "$code" == 206 ]] &&
       [[ "$content_range" == bytes && "$timely" -eq 1 ]] &&
       (( saved_bytes > 0 && saved_bytes <= 65536 && bytes == saved_bytes )); then
        last_stream_ranges_supported=1
        printf 'PASS %-34s status=%s bytes=%s ttfb_ms=%.1f total_ms=%.1f\n' \
            "$label" "$code" "$saved_bytes" "$(awk -v value="${ttfb:-0}" 'BEGIN { print value * 1000 }')" \
            "$(awk -v value="${total:-0}" 'BEGIN { print value * 1000 }')"
    elif [[ "$code" == 200 && -z "$content_range" && "$accept_ranges" != bytes && "$timely" -eq 1 ]] &&
         (( saved_bytes == 65536 && bytes >= saved_bytes )); then
        last_stream_ranges_supported=0
        block "$label=range unsupported; bounded progressive close passed status=200 retained_bytes=$saved_bytes transport_bytes=$bytes"
    else
        last_stream_ranges_supported=0
        printf 'FAIL %-34s status=%s type=%s saved_bytes=%s response_bytes=%s ttfb_ms=%.1f content_range=%s accept_ranges=%s timely=%s\n' \
            "$label" "$code" "$content_type" "$saved_bytes" "$bytes" \
            "$(awk -v value="${ttfb:-0}" 'BEGIN { print value * 1000 }')" \
            "${content_range:-none}" "${accept_ranges:-none}" "$timely"
        failures=$((failures + 1))
    fi
}

check_stateful_playlist_identity() {
    local label="$1" playlist_id="$2" playlist_name="$3"
    checks=$((checks + 1))
    if playlist_identity_matches "$playlist_id" "$playlist_name"; then
        printf 'PASS %-34s exact-id-name-type\n' "$label"
        return 0
    fi
    printf 'FAIL %-34s run-owned playlist identity mismatch\n' "$label"
    failures=$((failures + 1))
    return 1
}

wait_stateful_playlist_identity() {
    local label="$1" playlist_id="$2" playlist_name="$3" attempt streak=0
    for ((attempt = 1; attempt <= 20; attempt++)); do
        if playlist_identity_matches "$playlist_id" "$playlist_name"; then
            streak=$((streak + 1))
            if (( streak == 5 )); then
                checks=$((checks + 1))
                printf 'PASS %-34s stable-id-name-type\n' "$label"
                return 0
            fi
        else
            streak=0
        fi
        sleep 0.25
    done
    checks=$((checks + 1))
    failures=$((failures + 1))
    printf 'FAIL %-34s stable run-owned playlist identity mismatch\n' "$label"
    return 1
}

compare_structure() {
    local label="$1" direct_url="$2" allstarr_url="$3"
    local direct_filter="${4:-.}" allstarr_filter="${5:-.}"
    local shape='def shape:
        if type == "object" then with_entries(.value |= shape)
        elif type == "array" then map(shape) | unique
        else type
        end;
        shape'
    if ! curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$direct_url" |
        jq -S "($direct_filter) | $shape" >"$direct_shape_file"; then
        checks=$((checks + 1))
        failures=$((failures + 1))
        printf 'FAIL %-34s direct-fetch-or-json\n' "$label"
        return
    fi
    if ! curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$allstarr_url" |
        jq -S "($allstarr_filter) | $shape" >"$allstarr_shape_file"; then
        checks=$((checks + 1))
        failures=$((failures + 1))
        printf 'FAIL %-34s allstarr-fetch-or-json\n' "$label"
        return
    fi
    checks=$((checks + 1))
    if diff -u "$direct_shape_file" "$allstarr_shape_file" >/dev/null; then
        printf 'PASS %-34s structural-parity\n' "$label"
    else
        printf 'FAIL %-34s structural-diff\n' "$label"
        diff -u "$direct_shape_file" "$allstarr_shape_file" | sed -n '1,80p' || true
        failures=$((failures + 1))
    fi
}

compare_projection() {
    local label="$1" direct_url="$2" allstarr_url="$3"
    local direct_filter="$4" allstarr_filter="${5:-$4}"
    if ! curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$direct_url" |
        jq -S "$direct_filter" >"$direct_shape_file"; then
        checks=$((checks + 1))
        failures=$((failures + 1))
        printf 'FAIL %-34s direct-fetch-or-json\n' "$label"
        return
    fi
    if ! curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$allstarr_url" |
        jq -S "$allstarr_filter" >"$allstarr_shape_file"; then
        checks=$((checks + 1))
        failures=$((failures + 1))
        printf 'FAIL %-34s allstarr-fetch-or-json\n' "$label"
        return
    fi
    checks=$((checks + 1))
    if diff -u "$direct_shape_file" "$allstarr_shape_file" >/dev/null; then
        printf 'PASS %-34s stable-data-parity\n' "$label"
    else
        printf 'FAIL %-34s stable-data-diff\n' "$label"
        diff -u "$direct_shape_file" "$allstarr_shape_file" | sed -n '1,80p' || true
        failures=$((failures + 1))
    fi
}

compare_native_collection_items() {
    local label="$1" allstarr_url="$2" filter="${3:-.Items}"
    local item item_id item_count=0 invalid_count=0
    if ! curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$allstarr_url" |
        jq -c "$filter | .[]" >"$allstarr_shape_file"; then
        checks=$((checks + 1))
        failures=$((failures + 1))
        printf 'FAIL %-34s allstarr-fetch-or-json\n' "$label"
        return
    fi
    while IFS= read -r item; do
        item_count=$((item_count + 1))
        item_id="$(jq -r '.Id // empty' <<<"$item")"
        if [[ -z "$item_id" || "$item_id" == ext-* || "$item_id" == allstarr-unresolved-* ]] ||
           ! curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
                "$DIRECT_BASE/Users/$best_user_id/Items?Ids=$item_id&Limit=1" |
                jq -S '.Items[0]' >"$direct_shape_file"; then
            invalid_count=$((invalid_count + 1))
            continue
        fi
        jq -S . <<<"$item" >"$response_file"
        if ! jq -ne --slurpfile actual "$response_file" --slurpfile direct "$direct_shape_file" '
            ($actual[0] | keys_unsorted) as $keys |
            ($direct[0] | with_entries(select(.key as $key | $keys | index($key)))) == $actual[0]
            ' >/dev/null; then
            invalid_count=$((invalid_count + 1))
        fi
    done <"$allstarr_shape_file"
    checks=$((checks + 1))
    if (( item_count > 0 && invalid_count == 0 )); then
        printf 'PASS %-34s items=%s exact-native-projection\n' "$label" "$item_count"
    else
        printf 'FAIL %-34s items=%s invalid=%s\n' "$label" "$item_count" "$invalid_count"
        failures=$((failures + 1))
    fi
}

check_public_parity() {
    local label="$1" method="$2" path="$3" direct_code allstarr_code
    if [[ "$method" == HEAD ]]; then
        direct_code="$(curl -sS --head --max-time "$TIMEOUT_SECONDS" -o /dev/null -w '%{http_code}' "$DIRECT_BASE$path" || true)"
        allstarr_code="$(curl -sS --head --max-time "$TIMEOUT_SECONDS" -o /dev/null -w '%{http_code}' "$ALLSTARR_BASE$path" || true)"
    else
        direct_code="$(curl -sS -X "$method" --max-time "$TIMEOUT_SECONDS" -o /dev/null -w '%{http_code}' "$DIRECT_BASE$path" || true)"
        allstarr_code="$(curl -sS -X "$method" --max-time "$TIMEOUT_SECONDS" -o /dev/null -w '%{http_code}' "$ALLSTARR_BASE$path" || true)"
    fi
    direct_code="${direct_code:-000}"
    allstarr_code="${allstarr_code:-000}"
    checks=$((checks + 1))
    if [[ "$direct_code" == "$allstarr_code" && "$allstarr_code" -ge 200 && "$allstarr_code" -lt 400 ]]; then
        printf 'PASS %-34s status=%s\n' "$label" "$allstarr_code"
    else
        printf 'FAIL %-34s direct=%s allstarr=%s\n' "$label" "$direct_code" "$allstarr_code"
        failures=$((failures + 1))
    fi
}

check_public_image() {
    local label="$1" url="$2" result code content_type bytes
    : >"$response_file"
    result="$(curl -sS --max-time "$TIMEOUT_SECONDS" -o "$response_file" \
        -w '%{http_code}\t%{content_type}\t%{size_download}' "$url" || true)"
    IFS=$'\t' read -r code content_type bytes <<<"$result"
    code="${code:-000}"
    bytes="${bytes:-0}"
    checks=$((checks + 1))
    if [[ "$code" == 200 && "$content_type" == image/* && "${bytes%.*}" -ge 256 ]]; then
        printf 'PASS %-34s type=%s bytes=%s\n' "$label" "$content_type" "$bytes"
    else
        printf 'FAIL %-34s status=%s type=%s bytes=%s\n' "$label" "$code" "$content_type" "$bytes"
        failures=$((failures + 1))
    fi
}

check_range_parity() {
    local label="$1" direct_url="$2" allstarr_url="$3" credential_mode="${4:-header}"
    local direct_code allstarr_code direct_bytes allstarr_bytes direct_range allstarr_range direct_type allstarr_type signature
    local -a request_auth=("${auth[@]}")
    if [[ "$credential_mode" == query-api-key ]]; then
        request_auth=(-H "User-Agent: AllstarrLiveSmoke/$run_id")
        direct_url="$direct_url?ApiKey=$JELLYFIN_TOKEN"
        allstarr_url="$allstarr_url?ApiKey=$JELLYFIN_TOKEN"
    fi
    : >"$direct_media_file"
    : >"$allstarr_media_file"
    : >"$direct_headers_file"
    : >"$allstarr_headers_file"
    direct_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${request_auth[@]}" --range 0-65535 --max-filesize 65536 \
        -D "$direct_headers_file" -o "$direct_media_file" -w '%{http_code}' "$direct_url" || true)"
    allstarr_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${request_auth[@]}" --range 0-65535 --max-filesize 65536 \
        -D "$allstarr_headers_file" -o "$allstarr_media_file" -w '%{http_code}' "$allstarr_url" || true)"
    direct_code="${direct_code:-000}"
    allstarr_code="${allstarr_code:-000}"
    direct_bytes="$(wc -c <"$direct_media_file" | awk '{print $1}')"
    allstarr_bytes="$(wc -c <"$allstarr_media_file" | awk '{print $1}')"
    direct_range="$(awk 'tolower($1) == "content-range:" { sub(/\r$/, ""); $1=""; sub(/^ /, ""); print; exit }' "$direct_headers_file")"
    allstarr_range="$(awk 'tolower($1) == "content-range:" { sub(/\r$/, ""); $1=""; sub(/^ /, ""); print; exit }' "$allstarr_headers_file")"
    direct_type="$(awk 'tolower($1) == "content-type:" { sub(/\r$/, ""); $1=""; sub(/^ /, ""); print; exit }' "$direct_headers_file")"
    allstarr_type="$(awk 'tolower($1) == "content-type:" { sub(/\r$/, ""); $1=""; sub(/^ /, ""); print; exit }' "$allstarr_headers_file")"
    checks=$((checks + 1))
    if [[ "$direct_code" == 206 && "$allstarr_code" == 206 &&
          "$direct_bytes" == 65536 && "$allstarr_bytes" == 65536 &&
          "$direct_range" == bytes\ 0-65535/* && "$allstarr_range" == bytes\ 0-65535/* &&
          "$direct_range" == "$allstarr_range" &&
          "$direct_type" == "$allstarr_type" ]] &&
       cmp -s "$direct_media_file" "$allstarr_media_file"; then
        signature="$("${sha256_command[@]}" "$allstarr_media_file" | awk '{print substr($1, 1, 12)}')"
        printf 'PASS %-34s bytes=65536 type=%s sha256=%s exact-body\n' \
            "$label" "$allstarr_type" "$signature"
    else
        printf 'FAIL %-34s direct=%s/%s/%s allstarr=%s/%s/%s\n' \
            "$label" "$direct_code" "$direct_bytes" "$direct_range" \
            "$allstarr_code" "$allstarr_bytes" "$allstarr_range"
        failures=$((failures + 1))
    fi
}

check_playback_quality_parity() {
    local label="$1" direct_url="$2" allstarr_url="$3" direct_bitrate allstarr_bitrate
    curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$direct_url" -o "$direct_shape_file"
    curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$allstarr_url" -o "$allstarr_shape_file"
    direct_bitrate="$(jq -r 'first(.MediaSources[]? | .Bitrate // empty) // empty' "$direct_shape_file")"
    allstarr_bitrate="$(jq -r 'first(.MediaSources[]? | .Bitrate // empty) // empty' "$allstarr_shape_file")"
    checks=$((checks + 1))
    if [[ "$direct_bitrate" =~ ^[1-9][0-9]*$ && "$direct_bitrate" == "$allstarr_bitrate" ]]; then
        printf 'PASS %-34s bitrate_bps=%s exact-native\n' "$label" "$allstarr_bitrate"
    else
        printf 'FAIL %-34s direct_bitrate=%s allstarr_bitrate=%s\n' \
            "$label" "${direct_bitrate:-missing}" "${allstarr_bitrate:-missing}"
        failures=$((failures + 1))
    fi
}

check_concurrent_range_parity() {
    local label="$1" url="$2" sample body status code bytes failures_seen=0
    local -a bodies=() statuses=() pids=()
    for sample in 1 2 3; do
        body="$(mktemp)"
        status="$(mktemp)"
        bodies+=("$body")
        statuses+=("$status")
        (curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" --range 0-65535 \
            --max-filesize 65536 -o "$body" -w '%{http_code}' "$url" >"$status" || true) &
        pids+=("$!")
    done
    for sample in 0 1 2; do
        wait "${pids[$sample]}" || true
        code="$(<"${statuses[$sample]}")"
        bytes="$(wc -c <"${bodies[$sample]}" | tr -d ' ')"
        if [[ "$code" != 206 || "$bytes" != 65536 ]] ||
           ! cmp -s "$direct_media_file" "${bodies[$sample]}"; then
            failures_seen=1
        fi
    done
    checks=$((checks + 1))
    if [[ "$failures_seen" -eq 0 ]]; then
        printf 'PASS %-34s requests=3 bytes_each=65536 exact-body\n' "$label"
    else
        printf 'FAIL %-34s concurrent range/body mismatch\n' "$label"
        failures=$((failures + 1))
    fi
    rm -f "${bodies[@]}" "${statuses[@]}"
}

check_stream_cancellation() {
    local label="$1" url="$2" health_url="$3" retained curl_status health_code
    : >"$response_file"
    set +e
    curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" --range 0-65535 "$url" 2>/dev/null |
        head -c 1 >"$response_file"
    curl_status="${PIPESTATUS[0]}"
    set -e
    retained="$(wc -c <"$response_file" | tr -d ' ')"
    health_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        -o /dev/null -w '%{http_code}' "$health_url" || true)"
    checks=$((checks + 1))
    if [[ "$retained" == 1 && "$health_code" == 200 &&
          ( "$curl_status" == 0 || "$curl_status" == 23 || "$curl_status" == 56 ) ]]; then
        printf 'PASS %-34s retained_bytes=1 curl=%s post_close=%s\n' \
            "$label" "$curl_status" "$health_code"
    else
        printf 'FAIL %-34s retained_bytes=%s curl=%s post_close=%s\n' \
            "$label" "$retained" "$curl_status" "$health_code"
        failures=$((failures + 1))
    fi
}

run_stateful_playlist_smoke() {
    local playlist_name renamed_name create_payload other_user_id
    local first_entry_id second_entry_id deleted_playlist_id candidate_playlist_id

    playlist_name="Allstarr smoke $run_id ${response_file##*/}"
    renamed_name="$playlist_name renamed"
    create_payload="$(jq -cn \
        --arg name "$playlist_name" \
        --arg id "$media_id" \
        --arg user "$best_user_id" \
        '{Name:$name,Ids:[$id],UserId:$user,MediaType:"Audio",IsPublic:false}')"
    if ! stateful_call "stateful playlist create" "200" POST \
        "$ALLSTARR_BASE/Playlists" \
        -H "Content-Type: application/json" --data-binary "$create_payload"; then
        return
    fi

    candidate_playlist_id="$(jq -r '.Id // empty' "$response_file" 2>/dev/null || true)"
    if ! wait_stateful_playlist_identity \
        "stateful create direct-visible" "$candidate_playlist_id" "$playlist_name"; then
        return
    fi
    stateful_playlist_id="$candidate_playlist_id"
    stateful_playlist_name="$playlist_name"
    stateful_playlist_original_name="$playlist_name"

    jq -cn --arg name "$renamed_name" '{Name:$name}' >"$direct_shape_file"
    if ! stateful_call "stateful playlist rename" "204" POST \
        "$ALLSTARR_BASE/Playlists/$stateful_playlist_id" \
        -H "Content-Type: application/json" --data-binary "@$direct_shape_file"; then
        return
    fi
    stateful_playlist_name="$renamed_name"
    if ! wait_stateful_playlist_identity \
        "stateful rename direct-visible" "$stateful_playlist_id" "$renamed_name"; then
        return
    fi

    if [[ -n "$second_media_id" ]]; then
        if ! stateful_call "stateful playlist add" "204" POST \
            "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Items?ids=$second_media_id&UserId=$best_user_id"; then
            return
        fi
        wait_json "stateful add direct-visible" \
            "$DIRECT_BASE/Playlists/$stateful_playlist_id/Items?UserId=$best_user_id" \
            '([.Items[].Id] | index($first) != null) and
             ([.Items[].Id] | index($second) != null) and
             all(.Items[]; (.PlaylistItemId | type == "string" and length > 0))' \
            --arg first "$media_id" --arg second "$second_media_id"
        if ! wait_stateful_playlist_identity \
            "stateful add definition visible" "$stateful_playlist_id" "$renamed_name"; then
            return
        fi
        first_entry_id="$(jq -r --arg id "$media_id" \
            'first(.Items[] | select(.Id == $id)) | .PlaylistItemId // empty' "$response_file")"
        second_entry_id="$(jq -r --arg id "$second_media_id" \
            'first(.Items[] | select(.Id == $id)) | .PlaylistItemId // empty' "$response_file")"
        if [[ -z "$first_entry_id" || -z "$second_entry_id" ]]; then
            checks=$((checks + 1))
            failures=$((failures + 1))
            printf 'FAIL %-34s missing PlaylistItemId\n' "stateful entry identity"
            return
        fi

        if ! stateful_call "stateful playlist reorder" "204" POST \
            "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Items/$second_entry_id/Move/0"; then
            return
        fi
        wait_json "stateful reorder direct-visible" \
            "$DIRECT_BASE/Playlists/$stateful_playlist_id/Items?UserId=$best_user_id" \
            '.Items[0].Id == $id' --arg id "$second_media_id"

        if ! stateful_call "stateful playlist remove" "204" DELETE \
            "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Items?entryIds=$first_entry_id"; then
            return
        fi
        wait_json "stateful remove direct-visible" \
            "$DIRECT_BASE/Playlists/$stateful_playlist_id/Items?UserId=$best_user_id" \
            '([.Items[].Id] | index($removed) == null) and
             ([.Items[].Id] | index($kept) != null)' \
            --arg removed "$media_id" --arg kept "$second_media_id"
    else
        block "stateful-add-remove-reorder=no second streamable audio item in first 100"
    fi

    compare_structure "stateful playlist ACL relay" \
        "$DIRECT_BASE/Playlists/$stateful_playlist_id/Users" \
        "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Users"
    other_user_id="$(jq -r --arg owner "$best_user_id" \
        'first(.[] | select(.Id != $owner)) | .Id // empty' "$users_file")"
    if [[ -n "$other_user_id" ]]; then
        jq -cn --arg user "$other_user_id" \
            '{Users:[{UserId:$user,CanEdit:true}]}' >"$direct_shape_file"
        if ! stateful_call "stateful playlist share" "204" POST \
            "$ALLSTARR_BASE/Playlists/$stateful_playlist_id" \
            -H "Content-Type: application/json" --data-binary "@$direct_shape_file"; then
            return
        fi
        compare_structure "stateful playlist user relay" \
            "$DIRECT_BASE/Playlists/$stateful_playlist_id/Users/$other_user_id" \
            "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Users/$other_user_id"
        if ! stateful_call "stateful playlist unshare" "204" DELETE \
            "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Users/$other_user_id"; then
            return
        fi
    else
        block "stateful-share=no second Jellyfin user"
    fi

    check_json "stateful playlist mix direct" \
        "$DIRECT_BASE/Playlists/$stateful_playlist_id/InstantMix?Limit=10" \
        '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'
    check_json "stateful playlist mix proxy" \
        "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/InstantMix?Limit=10" \
        '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'

    deleted_playlist_id="$stateful_playlist_id"
    if ! stateful_call "stateful playlist delete" "204" DELETE \
        "$ALLSTARR_BASE/Items/$deleted_playlist_id"; then
        return
    fi
    if stateful_call "stateful delete direct-visible" "404" GET \
        "$DIRECT_BASE/Items/$deleted_playlist_id"; then
        stateful_playlist_id=""
        stateful_playlist_name=""
        stateful_playlist_original_name=""
    fi
}

item_contract='
    def nonempty: type == "string" and length > 0;
    def named_ids:
        (.ArtistItems // []) | all(.[]; (.Id | nonempty) and (.Name | nonempty));
    def album_ids:
        (.AlbumArtists // []) | all(.[]; (.Id | nonempty) and (.Name | nonempty));
    def genre_ids:
        (.GenreItems // []) | all(.[]; (.Id | nonempty) and (.Name | nonempty));
    def media_ids:
        (.MediaSources // []) | all(.[];
            (.Id | nonempty) and
            ((.MediaStreams // []) | all(.[]; (.Index | type == "number"))));
    def user_data:
        (.UserData | type == "object") and
        (.UserData.Key | nonempty) and
        (.UserData.ItemId == .Id);
    def provider_labeled:
        type == "string" and
        test(" \\[[A-Z][A-Za-z0-9]{0,15}\\]( \\[E\\])?$");
    def client_item:
        (.Id | nonempty) and (.Name | nonempty) and (.Type | nonempty) and
        named_ids and album_ids and genre_ids and media_ids and user_data;
    def external_audio:
        client_item and .Type == "Audio" and .MediaType == "Audio" and
        (.Name | provider_labeled) and
        (.Album | type == "string") and
        ((.Album | length) == 0 or (.Album | provider_labeled)) and
        (.AlbumId == null or (.AlbumId | nonempty)) and
        (.Artists | type == "array" and length > 0 and all(.[]; provider_labeled)) and
        (.ArtistItems | type == "array") and
        (.AlbumArtists | type == "array") and
        (.RunTimeTicks | type == "number" and . >= 0) and
        (.ImageTags.Primary | nonempty) and
        (.ProviderIds | type == "object" and length > 0) and
        (.CanDownload | type == "boolean") and
        (.RunTimeTicks as $runtime |
            .MediaSources | type == "array" and length > 0 and
            all(.[];
                (.Id | nonempty) and
                (.DirectStreamUrl | nonempty) and
                (.RunTimeTicks == $runtime) and
                (.Bitrate | type == "number" and . > 0) and
                (.Bitrate as $bitrate |
                    (.MediaStreams | type == "array" and length > 0 and
                        all(.[]; .Type == "Audio" and .BitRate == $bitrate)) and
                    (if $runtime > 0
                     then .Size == (($runtime / 10000000 | floor) * ($bitrate / 8 | floor))
                     else .Size == null
                     end)) and
                (.SupportsDirectPlay | type == "boolean") and
                (.SupportsDirectStream | type == "boolean") and
                (.MediaStreams | type == "array" and length > 0)));
'

check_external_provider_case() {
    local provider="$1" song_id="$2" detail_url artist_id album_id
    detail_url="$ALLSTARR_BASE/Items/$song_id?UserId=$best_user_id"
    check_json "$provider external detail" "$detail_url" \
        "$item_contract external_audio and .Id == \$id" --arg id "$song_id"
    artist_id="$(jq -r '.ArtistItems[0].Id // empty' "$response_file")"
    album_id="$(jq -r '.AlbumId // empty' "$response_file")"
    check_json "$provider user item detail" \
        "$ALLSTARR_BASE/Users/$best_user_id/Items/$song_id" \
        "$item_contract external_audio and .Id == \$id" --arg id "$song_id"

    if [[ -n "$artist_id" ]]; then
        check_json "$provider artist detail" \
            "$ALLSTARR_BASE/Artists/$artist_id?UserId=$best_user_id" \
            '.Id == $id and .Type == "MusicArtist"' --arg id "$artist_id"
        check_json "$provider artist albums" \
            "$ALLSTARR_BASE/Items?UserId=$best_user_id&ParentId=$artist_id&IncludeItemTypes=MusicAlbum&Limit=200" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "MusicAlbum" and (.ArtistItems | length > 0)) and
             ((.Items | length) == 0 or any(.Items[]; any(.ArtistItems[]; .Id == $id)))' \
            --arg id "$artist_id"
        check_json "$provider artist tracks" \
            "$ALLSTARR_BASE/Items?UserId=$best_user_id&ParentId=$artist_id&IncludeItemTypes=Audio&Limit=200" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "Audio" and .RunTimeTicks > 0 and (.ArtistItems | length > 0)) and
             ((.Items | length) == 0 or any(.Items[]; any(.ArtistItems[]; .Id == $id)))' \
            --arg id "$artist_id"
        check_json "$provider artist combined" \
            "$ALLSTARR_BASE/Items?UserId=$best_user_id&ParentId=$artist_id&IncludeItemTypes=MusicAlbum,Audio&Limit=400" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "MusicAlbum" or .Type == "Audio")' \
            --arg id "$artist_id"
        check_json "$provider artist instant mix" \
            "$ALLSTARR_BASE/Artists/$artist_id/InstantMix?UserId=$best_user_id&Limit=10" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "Audio" and .RunTimeTicks > 0 and
                 (.Id | startswith($prefix)))' \
            --arg prefix "ext-$provider-song-"
    else
        block "$provider artist routes=provider omitted artist relationship ID"
    fi
    if [[ -n "$album_id" ]]; then
        check_json "$provider album detail" \
            "$ALLSTARR_BASE/Items/$album_id?UserId=$best_user_id" \
            '.Id == $id and .Type == "MusicAlbum"' --arg id "$album_id"
        check_json "$provider album tracks" \
            "$ALLSTARR_BASE/Items?UserId=$best_user_id&ParentId=$album_id&IncludeItemTypes=Audio&Limit=200" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "Audio" and .AlbumId == $id and .RunTimeTicks > 0)' \
            --arg id "$album_id"
        check_json "$provider album instant mix" \
            "$ALLSTARR_BASE/Albums/$album_id/InstantMix?UserId=$best_user_id&Limit=10" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "Audio" and .RunTimeTicks > 0 and
                 (.Id | startswith($prefix)))' \
            --arg prefix "ext-$provider-song-"
    else
        block "$provider album routes=provider omitted album relationship ID"
    fi
    check_json "$provider external playback identity" \
        "$ALLSTARR_BASE/Items/$song_id/PlaybackInfo?UserId=$best_user_id" \
        '(.MediaSources | type == "array" and length > 0) and
         all(.MediaSources[]; .Id == $id and (.DirectStreamUrl | contains($id)))' \
        --arg id "$song_id"
    check_json "$provider similar songs" \
        "$ALLSTARR_BASE/Items/$song_id/Similar?UserId=$best_user_id&Limit=10" \
        '(.Items | type == "array") and
         (.TotalRecordCount | type == "number") and
         all(.Items[]; .Type == "Audio" and .Id != $id and .RunTimeTicks > 0 and
             (.Id | startswith($prefix)))' \
        --arg id "$song_id" --arg prefix "ext-$provider-song-"
    check_json "$provider song instant mix" \
        "$ALLSTARR_BASE/Songs/$song_id/InstantMix?UserId=$best_user_id&Limit=10" \
        '(.Items | type == "array") and
         (.TotalRecordCount | type == "number") and
         all(.Items[]; .Type == "Audio" and .RunTimeTicks > 0 and
             (.Id | startswith($prefix)))' \
        --arg prefix "ext-$provider-song-"
    check_image "$provider advertised artwork" \
        "$ALLSTARR_BASE/Items/$song_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    check_code "$provider artwork HEAD" "200,304" HEAD \
        "$ALLSTARR_BASE/Items/$song_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    check_optional_json "$provider lyrics" \
        "$ALLSTARR_BASE/Audio/$song_id/Lyrics?UserId=$best_user_id" \
        '(.Lyrics | type == "array") and
         all(.Lyrics[]; (.Text | type == "string") and
             ((has("Start") | not) or (.Start | type == "number")))'
    if [[ "$TEST_EXTERNAL_STREAM" == 1 ]]; then
        check_external_stream "$provider external stream" \
            "$ALLSTARR_BASE/Audio/$song_id/stream?static=true&UserId=$best_user_id"
    fi
}

echo "functional-and-security-checks"
check_public_code "public bootstrap" "200" "$ALLSTARR_BASE/System/Info/Public"
direct_version="$(curl -fsS --max-time "$TIMEOUT_SECONDS" "$DIRECT_BASE/System/Info/Public" |
    jq -r '.Version // empty' || true)"
allstarr_version="$(curl -fsS --max-time "$TIMEOUT_SECONDS" "$ALLSTARR_BASE/System/Info/Public" |
    jq -r '.Version // empty' || true)"
echo "runtime-versions direct=$direct_version allstarr=$allstarr_version pinned_openapi=12.0.0"
checks=$((checks + 1))
if [[ -n "$direct_version" && "$direct_version" == "$allstarr_version" ]]; then
    printf 'PASS %-34s version=%s\n' "backend version parity" "$allstarr_version"
else
    printf 'FAIL %-34s direct=%s allstarr=%s\n' "backend version parity" "$direct_version" "$allstarr_version"
    failures=$((failures + 1))
fi
check_public_parity "public GET ping" GET "/System/Ping"
check_public_parity "public POST ping" POST "/System/Ping"
check_public_parity "public UTC time" GET "/GetUtcTime"
check_public_parity "public user discovery" GET "/Users/Public"
check_public_parity "public Quick Connect state" GET "/QuickConnect/Enabled"
check_public_code "protected route needs auth" "401" "$ALLSTARR_BASE/Items"
invalid_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" -H 'X-Emby-Token: invalid-live-smoke-token' \
    -H "User-Agent: AllstarrLiveSmoke/$run_id" -o /dev/null -w '%{http_code}' "$ALLSTARR_BASE/Items" || true)"
invalid_code="${invalid_code:-000}"
checks=$((checks + 1))
if [[ "$invalid_code" == 401 || "$invalid_code" == 403 ]]; then
    printf 'PASS %-34s status=%s\n' "invalid credential rejected" "$invalid_code"
else
    printf 'FAIL %-34s expected=401,403 actual=%s\n' "invalid credential rejected" "$invalid_code"
    failures=$((failures + 1))
fi

check_json "current user profile" "$ALLSTARR_BASE/Users/$best_user_id" \
    '.Id != null and (.Policy.IsDisabled | type == "boolean")'
check_json "authenticated system info" "$ALLSTARR_BASE/System/Info" \
    '(.Id | type == "string" and length > 0) and (.Version | type == "string" and length > 0)'
check_json "request endpoint info" "$ALLSTARR_BASE/System/Endpoint" 'type == "object"'
check_json "view grouping options" "$ALLSTARR_BASE/UserViews/GroupingOptions?UserId=$best_user_id" \
    'type == "array"'
check_json "audio browse shape" "$ALLSTARR_BASE/Users/$best_user_id/Items?$items_query" \
    '(.Items | type == "array") and (.TotalRecordCount | type == "number") and all(.Items[]; .Type == "Audio")'
check_json "audio recursive DTO IDs" "$ALLSTARR_BASE/Users/$best_user_id/Items?$items_query" \
    "$item_contract (.Items | type == \"array\") and all(.Items[]; client_item)"
check_json "generic music constraint" "$ALLSTARR_BASE/Items?UserId=$best_user_id&Recursive=true&Limit=25" \
    '(.Items | type == "array") and all(.Items[]; (.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist" or .Type == "Playlist" or .Type == "MusicGenre"))'
check_json "search hints shape" "$ALLSTARR_BASE/Users/$best_user_id/Search/Hints?SearchTerm=$search_term_encoded&IncludeItemTypes=Audio&Limit=10" \
    '(.SearchHints | type == "array" and length <= 10) and all(.SearchHints[];
        .Type == "Audio" and (.Id | type == "string" and length > 0) and
        (((.ItemId // .Id) | type == "string" and length > 0)))'
check_json "audio item detail" "$ALLSTARR_BASE/Users/$best_user_id/Items/$media_id" '.Id != null and .Type == "Audio"'
check_json "artists browse" "$ALLSTARR_BASE/Artists?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "MusicArtist")'
check_json "album artists browse" "$ALLSTARR_BASE/Artists/AlbumArtists?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "MusicArtist")'
if [[ -n "$artist_id" ]]; then
    check_json "artist detail" "$ALLSTARR_BASE/Artists/$artist_id?UserId=$best_user_id" '.Type == "MusicArtist"'
    compare_projection "artist detail full object" \
        "$DIRECT_BASE/Items/$artist_id?UserId=$best_user_id" \
        "$ALLSTARR_BASE/Artists/$artist_id?UserId=$best_user_id" \
        '.'
fi
check_json "item filters" "$ALLSTARR_BASE/Items/Filters?UserId=$best_user_id" 'type == "object"'
check_json "item filters2" "$ALLSTARR_BASE/Items/Filters2?UserId=$best_user_id" 'type == "object"'
check_json "genres browse" "$ALLSTARR_BASE/Genres?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Genre")'
check_json "latest music only" "$ALLSTARR_BASE/Items/Latest?UserId=$best_user_id&Limit=10" \
    'type == "array" and all(.[]; (.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist" or .Type == "Playlist" or .Type == "MusicGenre"))'
check_json "suggestions music only" "$ALLSTARR_BASE/Items/Suggestions?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; (.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist" or .Type == "Playlist" or .Type == "MusicGenre"))'
check_json "resume music only" "$ALLSTARR_BASE/UserItems/Resume?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'
check_json "media folders music only" "$ALLSTARR_BASE/Library/MediaFolders?UserId=$best_user_id" \
    '(.Items | type == "array") and all(.Items[]; .CollectionType == "music")'
check_json "user views music only" "$ALLSTARR_BASE/UserViews?UserId=$best_user_id" \
    '(.Items | type == "array") and all(.Items[]; .CollectionType == "music")'
check_json "legacy user views music only" "$ALLSTARR_BASE/Users/$best_user_id/Views" \
    '(.Items | type == "array" and length > 0) and
     (.TotalRecordCount == (.Items | length)) and
     all(.Items[]; .CollectionType == "music")'
check_json "music library root" "$ALLSTARR_BASE/Items/Root?UserId=$best_user_id" \
    '.Id != null and .CollectionType == "music"'
check_json "music-only counts" "$ALLSTARR_BASE/Items/Counts?UserId=$best_user_id" \
    '.MovieCount == 0 and .SeriesCount == 0 and .EpisodeCount == 0 and .MusicVideoCount == 0'
check_json "playback info" "$ALLSTARR_BASE/Items/$media_id/PlaybackInfo?UserId=$best_user_id" \
    '(.MediaSources | type == "array") and (.MediaSources | length > 0)'
compare_structure "playback info structure parity" \
    "$DIRECT_BASE/Items/$media_id/PlaybackInfo?UserId=$best_user_id" \
    "$ALLSTARR_BASE/Items/$media_id/PlaybackInfo?UserId=$best_user_id"
check_playback_quality_parity "playback quality parity" \
    "$DIRECT_BASE/Items/$media_id/PlaybackInfo?UserId=$best_user_id" \
    "$ALLSTARR_BASE/Items/$media_id/PlaybackInfo?UserId=$best_user_id"
check_json "similar music" "$ALLSTARR_BASE/Items/$media_id/Similar?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and
     (.TotalRecordCount | type == "number") and
     .StartIndex == 0 and
     all(.Items[]; (.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist"))'
compare_native_collection_items "similar full native objects" \
    "$ALLSTARR_BASE/Items/$media_id/Similar?UserId=$best_user_id&Limit=10" \
    '[.Items[] | select(.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist")]'
check_json "instant mix" "$ALLSTARR_BASE/Songs/$media_id/InstantMix?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and
     (.TotalRecordCount | type == "number") and
     .StartIndex == 0 and
     all(.Items[]; .Type == "Audio")'
compare_native_collection_items "instant mix full objects" \
    "$ALLSTARR_BASE/Songs/$media_id/InstantMix?UserId=$best_user_id&Limit=10"
if [[ -n "$album_id" ]]; then
    check_json "album detail" "$ALLSTARR_BASE/Items/$album_id?UserId=$best_user_id" '.Type == "MusicAlbum"'
    compare_projection "album detail full object" \
        "$DIRECT_BASE/Items/$album_id?UserId=$best_user_id" \
        "$ALLSTARR_BASE/Items/$album_id?UserId=$best_user_id" \
        '.'
    check_json "album instant mix" "$ALLSTARR_BASE/Albums/$album_id/InstantMix?UserId=$best_user_id&Limit=10" \
        '(.Items | type == "array") and
         (.TotalRecordCount | type == "number") and
         .StartIndex == 0 and
         all(.Items[]; .Type == "Audio")'
    compare_native_collection_items "album mix full objects" \
        "$ALLSTARR_BASE/Albums/$album_id/InstantMix?UserId=$best_user_id&Limit=10"
fi

for denied_path in \
    "/Videos/security-probe/stream" \
    "/Movies/Recommendations" \
    "/Shows/NextUp" \
    "/LiveTv/Channels" \
    "/Channels" \
    "/SyncPlay/List" \
    "/Plugins" \
    "/ScheduledTasks" \
    "/System/Logs" \
    "/System/ActivityLog/Entries" \
    "/Library/VirtualFolders" \
    "/Users/New" \
    "/api/admin/providers/status" \
    "/Notifications" \
    "/Branding/Configuration" \
    "/Items/Latest?IncludeItemTypes=Movie,Audio" \
    "/Items/Suggestions?Type=Audio,Movie" \
    "/Genres?IncludeItemTypes=Audio,Series" \
    "/Search/Hints?IncludeItemTypes=Movie&SearchTerm=test"; do
    check_code "deny ${denied_path%%\?*}" "403" GET "$ALLSTARR_BASE$denied_path"
done
check_code "deny generic item deletion" "403" DELETE "$ALLSTARR_BASE/Items/allstarr-security-probe"
check_code "deny lyric upload" "403" POST "$ALLSTARR_BASE/Audio/$media_id/Lyrics"
check_code "deny lyric deletion" "403" DELETE "$ALLSTARR_BASE/Audio/$media_id/Lyrics"
check_code "deny fake item ancestors" "403" GET \
    "$ALLSTARR_BASE/Items/ext-security-song-never-resolve/Ancestors"
check_code "deny fake item user-data" "403" POST \
    "$ALLSTARR_BASE/UserItems/ext-security-song-never-resolve/UserData"

measure "direct public-info" "$DIRECT_BASE/System/Info/Public"
measure "allstarr public-info" "$ALLSTARR_BASE/System/Info/Public"
timing_delta "public info proxy delta" "direct public-info" "allstarr public-info"
measure "direct audio-list" "$DIRECT_BASE/Users/$best_user_id/Items?$items_query"
measure "allstarr audio-list" "$ALLSTARR_BASE/Users/$best_user_id/Items?$items_query"
timing_delta "audio browse proxy delta" "direct audio-list" "allstarr audio-list"
compare_structure "audio browse structure parity" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$items_query" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items?$items_query"
compare_projection "audio browse stable data" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$items_query" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items?$items_query" \
    '[.Items[] | {Id,Name,Type,AlbumId,Artists,ArtistItems,RunTimeTicks,ImageTags}] | sort_by(.Id)'
compare_projection "audio browse full objects" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$items_query" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items?$items_query" \
    '[.Items[]] | sort_by(.Id)'
compare_structure "audio detail structure parity" \
    "$DIRECT_BASE/Users/$best_user_id/Items/$media_id" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items/$media_id"
compare_projection "audio detail stable data" \
    "$DIRECT_BASE/Users/$best_user_id/Items/$media_id" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items/$media_id" \
    '{Id,Name,Type,AlbumId,Artists,ArtistItems,RunTimeTicks,ImageTags,ProviderIds}'
compare_projection "audio detail full object" \
    "$DIRECT_BASE/Users/$best_user_id/Items/$media_id" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items/$media_id" \
    '.'
search_hint_path="/Search/Hints?UserId=$best_user_id&SearchTerm=$search_term_encoded&IncludeItemTypes=Audio&Limit=10"
compare_structure "native search hint structure" \
    "$DIRECT_BASE$search_hint_path" \
    "$ALLSTARR_BASE$search_hint_path" \
    '.SearchHints' \
    '[.SearchHints[] | select((((.Id // .ItemId) // "") | startswith("ext-")) | not)]'
compare_projection "native search hint stable data" \
    "$DIRECT_BASE$search_hint_path" \
    "$ALLSTARR_BASE$search_hint_path" \
    '[.SearchHints[] | {Id,ItemId,Name,Type}]' \
    '[.SearchHints[] | select((((.Id // .ItemId) // "") | startswith("ext-")) | not) |
        {Id,ItemId,Name,Type}]'
compare_projection "native search hint full objects" \
    "$DIRECT_BASE$search_hint_path" \
    "$ALLSTARR_BASE$search_hint_path" \
    '[.SearchHints[] | .Id = (.Id // .ItemId)] | sort_by(.Id)' \
    '[.SearchHints[] | select((((.Id // .ItemId) // "") | startswith("ext-")) | not) |
        .Id = (.Id // .ItemId)] | sort_by(.Id)'

external_song_id="$EXTERNAL_SONG_ID"
if [[ -z "$external_song_id" ]]; then
    if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$ALLSTARR_BASE/Items?UserId=$best_user_id&SearchTerm=$search_term_encoded&IncludeItemTypes=Audio&StartIndex=0&Limit=50" \
        -o "$response_file"; then
        checks=$((checks + 1))
        if jq -e "$item_contract
            (.Items | type == \"array\") and
            (.StartIndex == 0) and
            (.TotalRecordCount | type == \"number\") and
            all(.Items[] | select((.Id // \"\") | startswith(\"ext-\")); external_audio)" \
            "$response_file" >/dev/null; then
            printf 'PASS %-34s json-shape\n' "integrated search contract"
        else
            printf 'FAIL %-34s invalid-envelope-or-external-dto\n' "integrated search contract"
            failures=$((failures + 1))
        fi
        external_song_id="$(jq -r '
            first(.Items[] | select((.Id // "") | test("^ext-.+-song-"; "i"))) | .Id // empty' "$response_file")"
    else
        checks=$((checks + 1))
        failures=$((failures + 1))
        echo "FAIL external item discovery"
    fi
fi
if [[ -n "$external_song_id" ]]; then
    external_detail_url="$ALLSTARR_BASE/Items/$external_song_id?UserId=$best_user_id"
    check_json "external item recursive DTO" "$external_detail_url" \
        "$item_contract external_audio and .Id == \$id" \
        --arg id "$external_song_id"
    external_provider="$(jq -r '
        first((.ProviderIds // {}) | keys[] |
            select(. != "ISRC" and . != "AllstarrSource")) // empty' "$response_file")"
    external_artist_id="$(jq -r '.ArtistItems[0].Id // empty' "$response_file")"
    external_album_id="$(jq -r '.AlbumId // empty' "$response_file")"
    external_search_term="$(jq -r '
        .Name |
        sub(" \\[[A-Z][A-Za-z0-9]{0,15}\\]( \\[E\\])?$"; "")' "$response_file" 2>/dev/null || true)"
    external_search_term="${external_search_term:-$search_term}"
    if [[ -n "$external_artist_id" ]]; then
        check_json "external artist detail" \
            "$ALLSTARR_BASE/Artists/$external_artist_id?UserId=$best_user_id" \
            '.Id == $id and .Type == "MusicArtist"' \
            --arg id "$external_artist_id"
        check_json "external artist discography" \
            "$ALLSTARR_BASE/Items?UserId=$best_user_id&ParentId=$external_artist_id&IncludeItemTypes=MusicAlbum&Limit=200" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "MusicAlbum" and (.ArtistItems | length > 0)) and
             ((.Items | length) == 0 or any(.Items[]; any(.ArtistItems[]; .Id == $id)))' \
            --arg id "$external_artist_id"
        check_json "external artist tracks" \
            "$ALLSTARR_BASE/Items?UserId=$best_user_id&ParentId=$external_artist_id&IncludeItemTypes=Audio&Limit=200" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "Audio" and .RunTimeTicks > 0 and (.ArtistItems | length > 0)) and
             ((.Items | length) == 0 or any(.Items[]; any(.ArtistItems[]; .Id == $id)))' \
            --arg id "$external_artist_id"
        check_json "external artist combined" \
            "$ALLSTARR_BASE/Items?UserId=$best_user_id&ParentId=$external_artist_id&IncludeItemTypes=MusicAlbum,Audio&Limit=400" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "MusicAlbum" or .Type == "Audio")'
    else
        block "external artist routes=provider omitted artist relationship ID"
    fi
    if [[ -n "$external_album_id" ]]; then
        check_json "external album detail" \
            "$ALLSTARR_BASE/Items/$external_album_id?UserId=$best_user_id" \
            '.Id == $id and .Type == "MusicAlbum"' \
            --arg id "$external_album_id"
        check_json "external album tracks" \
            "$ALLSTARR_BASE/Items?UserId=$best_user_id&ParentId=$external_album_id&IncludeItemTypes=Audio&Limit=200" \
            '(.Items | type == "array") and
             (.TotalRecordCount | type == "number") and
             all(.Items[]; .Type == "Audio" and .AlbumId == $id and (.ArtistItems | length > 0))' \
            --arg id "$external_album_id"
    else
        block "external album routes=provider omitted album relationship ID"
    fi
    external_search_term_encoded="$(jq -rn --arg value "$external_search_term" '$value | @uri')"
    check_query_json "external detail ApiKey auth" "$external_detail_url" "ApiKey" \
        "$item_contract external_audio and .Id == \$id" \
        --arg id "$external_song_id"
    check_query_json "external detail api_key auth" "$external_detail_url" "api_key" \
        "$item_contract external_audio and .Id == \$id" \
        --arg id "$external_song_id"
    measure "external metadata" "$external_detail_url"
    timing_budget "external metadata latency" "external metadata" "$MAX_EXTERNAL_METADATA_TTFB_MS"

    external_search_url="$ALLSTARR_BASE/Items?UserId=$best_user_id&SearchTerm=$external_search_term_encoded&IncludeItemTypes=Audio&StartIndex=0&Limit=20"
    check_json "external exact search flow" "$external_search_url" \
        "$item_contract
         (.Items | type == \"array\" and length > 0) and
         .StartIndex == 0 and
         (.TotalRecordCount | type == \"number\") and
         any(.Items[]; .Id == \$id) and
         all(.Items[] | select((.Id // \"\") | startswith(\"ext-\")); external_audio)" \
        --arg id "$external_song_id"
    cp "$response_file" "$external_search_file"
    checks=$((checks + 1))
    if jq -e '
        [.Items[] |
         select((.Id // "") | test("^ext-.+-song-"; "i")) |
         (first((.ProviderIds // {}) | keys[] |
             select(. != "ISRC" and . != "AllstarrSource")) // "")] as $providers |
        ($providers | map(select(. != ""))) as $providers |
        ($providers | length) > 0 and
        (($providers | unique | length) as $count |
            $count < 2 or ($providers[0:$count] | unique | length) == $count)
        ' "$external_search_file" >/dev/null; then
        printf 'PASS %-34s provider-order-interleaved\n' "external provider ordering"
    else
        printf 'FAIL %-34s provider-order-not-interleaved\n' "external provider ordering"
        failures=$((failures + 1))
    fi
    check_json "external search second page" \
        "$ALLSTARR_BASE/Items?UserId=$best_user_id&SearchTerm=$external_search_term_encoded&IncludeItemTypes=Audio&StartIndex=5&Limit=5" \
        '.StartIndex == 5 and
         .TotalRecordCount == $first[0].TotalRecordCount and
         [.Items[].Id] == [$first[0].Items[5:10][].Id]' \
        --slurpfile first "$external_search_file"
    check_json "external search hints flow" \
        "$ALLSTARR_BASE/Users/$best_user_id/Search/Hints?SearchTerm=$external_search_term_encoded&IncludeItemTypes=Audio&Limit=50" \
        '(.SearchHints | type == "array" and length > 0) and
         (.TotalRecordCount | type == "number") and
         any(.SearchHints[]; ((.Id // .ItemId) == $id)) and
         all(.SearchHints[] |
             select((((.Id // .ItemId) // "") | startswith("ext-")));
             .Type == "Audio" and
             ((.Id // .ItemId) | type == "string" and length > 0) and
             (.Name | type == "string" and
                 test(" \\[[A-Z][A-Za-z0-9]{0,15}\\]( \\[E\\])?$")))' \
        --arg id "$external_song_id"
    check_json "external playback identity" \
        "$ALLSTARR_BASE/Items/$external_song_id/PlaybackInfo?UserId=$best_user_id" \
        '(.MediaSources | type == "array" and length > 0) and
         all(.MediaSources[]; .Id == $id and (.DirectStreamUrl | contains($id)))' \
        --arg id "$external_song_id"
    check_json "external similar envelope" \
        "$ALLSTARR_BASE/Items/$external_song_id/Similar?UserId=$best_user_id&Limit=10" \
        '(.Items | type == "array" and length > 0) and (.TotalRecordCount | type == "number") and
         .StartIndex == 0 and
         all(.Items[]; .Type == "Audio" and .Id != $id and .RunTimeTicks > 0 and
             (.Id | startswith($prefix)))' \
        --arg id "$external_song_id" --arg prefix "ext-$external_provider-song-"
    check_json "external instant mix envelope" \
        "$ALLSTARR_BASE/Songs/$external_song_id/InstantMix?UserId=$best_user_id&Limit=10" \
        '(.Items | type == "array") and (.TotalRecordCount | type == "number") and
         .StartIndex == 0 and
         all(.Items[]; .Type == "Audio" and (.Id | startswith($prefix)))' \
        --arg prefix "ext-$external_provider-song-"
    external_art_url="$ALLSTARR_BASE/Items/$external_song_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    measure "external artwork" "$external_art_url"
    timing_budget "external artwork latency" "external artwork" "$MAX_EXTERNAL_ARTWORK_TTFB_MS"
    check_code "external artwork route" "200,304,404" GET \
        "$external_art_url"
    external_art_tag="$(curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$external_detail_url" |
        jq -r '.ImageTags.Primary // empty' || true)"
    if [[ -n "$external_art_tag" ]]; then
        check_image "external advertised artwork" \
            "$external_art_url"
        check_image "external long artwork" \
            "$ALLSTARR_BASE/Items/$external_song_id/Images/Primary/0/$external_art_tag/jpg/300/300/0/0?UserId=$best_user_id" \
            jpg
        external_art_etag="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
            -D "$allstarr_headers_file" -o /dev/null "$external_art_url" &&
            awk 'tolower($1) == "etag:" { sub(/\r$/, ""); $1=""; sub(/^ /, ""); print; exit }' \
                "$allstarr_headers_file" || true)"
        if [[ -n "$external_art_etag" ]]; then
            check_code "external artwork conditional" "304" GET "$external_art_url" \
                -H "If-None-Match: $external_art_etag"
        else
            checks=$((checks + 1))
            failures=$((failures + 1))
            printf 'FAIL %-34s missing-etag\n' "external artwork conditional"
        fi
    fi
    check_optional_json "external lyrics contract" \
        "$ALLSTARR_BASE/Audio/$external_song_id/Lyrics?UserId=$best_user_id" \
        '(.Metadata | type == "object") and
         (.Lyrics | type == "array") and
         all(.Lyrics[]; (.Text | type == "string"))'
    check_code "deny external ancestors" "403" GET \
        "$ALLSTARR_BASE/Items/$external_song_id/Ancestors?UserId=$best_user_id"
    if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$DIRECT_BASE/Users/$best_user_id/Items/$media_id" |
        jq -S 'to_entries | map({key, type:(.value | type)})' >"$direct_shape_file" &&
       curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$external_detail_url" |
        jq -S 'to_entries | map({key, type:(.value | type)})' >"$allstarr_shape_file"; then
        echo "declared-diff native-vs-external-audio field-types"
        diff -u "$direct_shape_file" "$allstarr_shape_file" | sed -n '1,80p' || true
    else
        echo "declared-diff native-vs-external-audio unavailable"
    fi
    if [[ "$TEST_EXTERNAL_STREAM" == 1 ]]; then
        check_external_stream "external stream-64k" \
            "$ALLSTARR_BASE/Audio/$external_song_id/stream?static=true&UserId=$best_user_id"
        if [[ "$last_stream_ranges_supported" -eq 1 ]]; then
            check_external_stream "external suffix stream-64k" \
                "$ALLSTARR_BASE/Audio/$external_song_id/stream?static=true&UserId=$best_user_id" \
                -65536
        else
            block "external suffix stream-64k=not executed because provider ranges are unsupported"
        fi
    else
        echo "external stream skipped=set TEST_EXTERNAL_STREAM=1 for provider/cold-cache media"
    fi

    jq --argjson seeds "$EXTERNAL_PROVIDER_CASES" --arg primary "$external_song_id" \
        --arg primaryProvider "$external_provider" '
        $seeds +
        [{provider: $primaryProvider, songId: $primary}] +
        [.Items[] |
         select((.Id // "") | test("^ext-.+-song-.+$")) |
         {provider: (first((.ProviderIds // {}) | keys[] |
             select(. != "ISRC" and . != "AllstarrSource")) // ""), songId: .Id}] |
        map(select(.provider != "")) |
        unique_by(.provider)
    ' "$external_search_file" >"$provider_cases_file"
    echo "external-provider-matrix=$(jq -r '[.[].provider] | join(",")' "$provider_cases_file")"
    if [[ -n "$EXPECTED_EXTERNAL_PROVIDERS" ]]; then
        while IFS= read -r expected_provider; do
            expected_provider="${expected_provider//[[:space:]]/}"
            [[ -z "$expected_provider" ]] && continue
            checks=$((checks + 1))
            if jq -e --arg provider "$expected_provider" \
                'any(.[]; .provider == $provider)' "$provider_cases_file" >/dev/null; then
                printf 'PASS %-34s discovered\n' "provider $expected_provider coverage"
            else
                printf 'FAIL %-34s not-discovered\n' "provider $expected_provider coverage"
                failures=$((failures + 1))
            fi
        done < <(tr ',' '\n' <<<"$EXPECTED_EXTERNAL_PROVIDERS")
    fi
    while IFS=$'\t' read -r provider_case provider_song_id; do
        [[ "$provider_song_id" == "$external_song_id" ]] && continue
        check_external_provider_case "$provider_case" "$provider_song_id"
    done < <(jq -r '.[] | [.provider, .songId] | @tsv' "$provider_cases_file")
else
    if [[ "$REQUIRE_EXTERNAL" == 1 ]]; then
        checks=$((checks + 1))
        failures=$((failures + 1))
        echo "FAIL external item checks required but no provider-backed audio result was found"
    else
        block "external-item-live=no provider-backed audio result; set EXTERNAL_SONG_ID and REQUIRE_EXTERNAL=1"
    fi
fi

playlist_query="Recursive=true&IncludeItemTypes=Playlist&Limit=100"
direct_playlists=0
allstarr_playlists=0
playlist_id=""
virtual_playlist_id=""
external_playlist_id=""
if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$playlist_query" -o "$direct_playlists_file"; then
    direct_playlists="$(jq -r '.TotalRecordCount // (.Items | length) // 0' "$direct_playlists_file")"
else
    checks=$((checks + 1))
    failures=$((failures + 1))
    echo "FAIL direct playlist discovery"
fi
if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query" -o "$allstarr_playlists_file"; then
    allstarr_playlists="$(jq -r '.TotalRecordCount // (.Items | length) // 0' "$allstarr_playlists_file")"
    virtual_playlist_id="$(jq -r '
        (first(.Items[] | select(
            ((.Id // "") | startswith("allstarr-vpl-")) and
            ((.ChildCount // 0) > 0) and .ImageTags.Primary != null)) //
         first(.Items[] | select(
            ((.Id // "") | startswith("allstarr-vpl-")) and
            ((.ChildCount // 0) > 0)))) | .Id // empty' "$allstarr_playlists_file")"
    external_playlist_id="$(jq -r '
        first(.Items[] | select((.Id // "") | test("^ext-.+-playlist-"; "i"))) | .Id // empty' "$allstarr_playlists_file")"
else
    checks=$((checks + 1))
    failures=$((failures + 1))
    echo "FAIL Allstarr playlist discovery"
fi
if [[ -s "$direct_playlists_file" && -s "$allstarr_playlists_file" ]]; then
    playlist_id="$(jq -r --slurpfile proxy "$allstarr_playlists_file" '
        first(.Items[] as $direct |
            select(any($proxy[0].Items[];
                .Id == $direct.Id and
                (.ChildCount // 0) == ($direct.ChildCount // 0))) |
            $direct.Id) // empty' "$direct_playlists_file")"
fi
if { [[ -z "$virtual_playlist_id" ]] || [[ -z "$external_playlist_id" ]]; } &&
   (( allstarr_playlists > direct_playlists )) &&
   curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
       "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query&StartIndex=$direct_playlists" \
       -o "$response_file"; then
    [[ -n "$virtual_playlist_id" ]] || virtual_playlist_id="$(jq -r '
        (first(.Items[] | select(
            ((.Id // "") | startswith("allstarr-vpl-")) and
            ((.ChildCount // 0) > 0) and .ImageTags.Primary != null)) //
         first(.Items[] | select(
            ((.Id // "") | startswith("allstarr-vpl-")) and
            ((.ChildCount // 0) > 0)))) | .Id // empty' "$response_file")"
    [[ -n "$external_playlist_id" ]] || external_playlist_id="$(jq -r '
        first(.Items[] | select((.Id // "") | test("^ext-.+-playlist-"; "i"))) | .Id // empty' "$response_file")"
fi
echo "playlist-counts direct=$direct_playlists allstarr=$allstarr_playlists"
check_json "playlist browse shape" "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Playlist")'
if [[ -n "$INJECTED_PLAYLIST_ID" ]]; then
    direct_injected_count="$(curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$DIRECT_BASE/Playlists/$INJECTED_PLAYLIST_ID/Items?UserId=$best_user_id&Limit=200" |
        jq -r '.TotalRecordCount // (.Items | length) // 0' || true)"
    allstarr_visible_count="$(curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$ALLSTARR_BASE/Playlists/$INJECTED_PLAYLIST_ID/Items?UserId=$best_user_id&Limit=200" |
        jq -r '.TotalRecordCount // (.Items | length) // 0' || true)"
    echo "configured-injected-counts id=$INJECTED_PLAYLIST_ID direct=${direct_injected_count:-unavailable} allstarr=${allstarr_visible_count:-unavailable} expected=$INJECTED_PLAYLIST_EXPECTED_COUNT actor_bound=$actor_bound"
fi
if [[ -n "$INJECTED_PLAYLIST_ID" && "$actor_bound" != 1 ]]; then
    checks=$((checks + 1))
    failures=$((failures + 1))
    echo "FAIL configured alias precondition      requires a user-bound Jellyfin access token; server API keys cannot authorize provider-owned projections"
elif [[ -n "$INJECTED_PLAYLIST_ID" ]]; then
    injected_items_url="$ALLSTARR_BASE/Playlists/$INJECTED_PLAYLIST_ID/Items?fields=SortName%2CCanDelete%2CMediaSources%2CDateCreated%2CCanDelete&userId=$best_user_id&startIndex=0&limit=200"
    check_json "configured alias browse count" \
        "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query" \
        '([.Items[] | select(.Id == $playlist_id)] | length) == 1 and
         (first(.Items[] | select(.Id == $playlist_id)).ChildCount == $expected)' \
        --arg playlist_id "$INJECTED_PLAYLIST_ID" \
        --argjson expected "$INJECTED_PLAYLIST_EXPECTED_COUNT"
    check_json "configured alias exact entries" "$injected_items_url" \
        "$item_contract
         .StartIndex == 0 and
         .TotalRecordCount == \$expected and
         (.Items | length) == \$expected and
         all(.Items[];
             client_item and
             .ParentId == \$playlist_id and
             (.PlaylistItemId | nonempty))" \
        --arg playlist_id "$INJECTED_PLAYLIST_ID" \
        --argjson expected "$INJECTED_PLAYLIST_EXPECTED_COUNT"
    cp "$response_file" "$external_search_file"
    check_json "configured alias first page" \
        "$ALLSTARR_BASE/Playlists/$INJECTED_PLAYLIST_ID/Items?fields=SortName%2CCanDelete%2CMediaSources%2CDateCreated&userId=$best_user_id&startIndex=0&limit=5" \
        '.StartIndex == 0 and
         .TotalRecordCount == $expected and
         [.Items[].Id] == [$full[0].Items[0:5][].Id]' \
        --argjson expected "$INJECTED_PLAYLIST_EXPECTED_COUNT" \
        --slurpfile full "$external_search_file"
    check_json "configured alias second page" \
        "$ALLSTARR_BASE/Playlists/$INJECTED_PLAYLIST_ID/Items?fields=SortName%2CCanDelete%2CMediaSources%2CDateCreated&userId=$best_user_id&startIndex=5&limit=5" \
        '.StartIndex == 5 and
         .TotalRecordCount == $expected and
         [.Items[].Id] == [$full[0].Items[5:10][].Id]' \
        --argjson expected "$INJECTED_PLAYLIST_EXPECTED_COUNT" \
        --slurpfile full "$external_search_file"
    [[ -n "$virtual_playlist_id" ]] || virtual_playlist_id="$INJECTED_PLAYLIST_ID"
fi
compare_structure "native playlist structure parity" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$playlist_query" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query" \
    '.Items' \
    '[.Items[] | select(
        ((((.Id // "") | startswith("allstarr-vpl-")) or
          ((.Id // "") | test("^ext-.+-playlist-"; "i"))) | not))]'
compare_projection "native playlist stable data" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$playlist_query" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query" \
    '[.Items[] | {Id,Name,Type,ImageTags,ProviderIds}] | sort_by(.Id)' \
    '[.Items[] | select(
        ((((.Id // "") | startswith("allstarr-vpl-")) or
          ((.Id // "") | test("^ext-.+-playlist-"; "i"))) | not)) |
        {Id,Name,Type,ImageTags,ProviderIds}] | sort_by(.Id)'
if [[ -n "$playlist_id" ]]; then
    check_json "playlist entries music only" "$ALLSTARR_BASE/Playlists/$playlist_id/Items?UserId=$best_user_id&Limit=100" \
        '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'
    direct_playlist_definition_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        -o /dev/null -w '%{http_code}' \
        "$DIRECT_BASE/Playlists/$playlist_id?UserId=$best_user_id" || true)"
    if [[ "$direct_playlist_definition_code" == 200 ]]; then
        check_json "playlist definition required DTO" "$ALLSTARR_BASE/Playlists/$playlist_id?UserId=$best_user_id" \
            '(.Shares | type == "array") and (.ItemIds | type == "array")'
        compare_structure "playlist definition structure" \
            "$DIRECT_BASE/Playlists/$playlist_id?UserId=$best_user_id" \
            "$ALLSTARR_BASE/Playlists/$playlist_id?UserId=$best_user_id"
        compare_projection "playlist definition stable data" \
            "$DIRECT_BASE/Playlists/$playlist_id?UserId=$best_user_id" \
            "$ALLSTARR_BASE/Playlists/$playlist_id?UserId=$best_user_id" \
            '{OpenAccess,Shares,ItemIds}'
    else
        block "playlist-definition-upstream=status-$direct_playlist_definition_code"
        check_code "playlist definition status parity" \
            "$direct_playlist_definition_code" GET \
            "$ALLSTARR_BASE/Playlists/$playlist_id?UserId=$best_user_id"
    fi
    compare_structure "playlist entries structure" \
        "$DIRECT_BASE/Playlists/$playlist_id/Items?UserId=$best_user_id&Limit=100" \
        "$ALLSTARR_BASE/Playlists/$playlist_id/Items?UserId=$best_user_id&Limit=100"
    compare_projection "playlist entries stable data" \
        "$DIRECT_BASE/Playlists/$playlist_id/Items?UserId=$best_user_id&Limit=100" \
        "$ALLSTARR_BASE/Playlists/$playlist_id/Items?UserId=$best_user_id&Limit=100" \
        '[.Items[] | {Id,PlaylistItemId,ParentId,AlbumId,Name,Type,Artists,ArtistItems,RunTimeTicks}]'
fi
if [[ -n "$external_playlist_id" ]]; then
    check_json "external playlist DTO IDs" \
        "$ALLSTARR_BASE/Playlists/$external_playlist_id/Items?UserId=$best_user_id&Limit=100" \
        "$item_contract
         (.Items | type == \"array\") and
         (.TotalRecordCount | type == \"number\") and
         .StartIndex == 0 and
         all(.Items[];
             client_item and
             (if (.Id | startswith(\"ext-\")) then external_audio else true end))"
    check_json "external playlist definition" \
        "$ALLSTARR_BASE/Playlists/$external_playlist_id?UserId=$best_user_id" \
        '(.Shares | type == "array") and (.ItemIds | type == "array") and
         all(.ItemIds[]; type == "string" and length > 0)'
    check_code "external playlist artwork" "200,304,404" GET \
        "$ALLSTARR_BASE/Items/$external_playlist_id/Images/Primary?UserId=$best_user_id"
    external_playlist_art_tag="$(curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$ALLSTARR_BASE/Items/$external_playlist_id?UserId=$best_user_id" |
        jq -r '.ImageTags.Primary // empty' || true)"
    if [[ -n "$external_playlist_art_tag" ]]; then
        check_image "external playlist advertised art" \
            "$ALLSTARR_BASE/Items/$external_playlist_id/Images/Primary?UserId=$best_user_id"
        check_image "external playlist long art" \
            "$ALLSTARR_BASE/Items/$external_playlist_id/Images/Primary/0/$external_playlist_art_tag/jpg/300/300/0/0?UserId=$best_user_id" \
            jpg
    fi
    check_code "read-only external playlist add" "409" POST \
        "$ALLSTARR_BASE/Playlists/$external_playlist_id/Items?ids=$media_id&UserId=$best_user_id"
    check_code "read-only external playlist remove" "409" DELETE \
        "$ALLSTARR_BASE/Playlists/$external_playlist_id/Items?entryIds=fixture&UserId=$best_user_id"
    check_code "read-only external playlist update" "409" POST \
        "$ALLSTARR_BASE/Playlists/$external_playlist_id?UserId=$best_user_id"
    check_code "read-only external playlist ACL" "409" GET \
        "$ALLSTARR_BASE/Playlists/$external_playlist_id/Users?UserId=$best_user_id"
    check_code "read-only external playlist mix" "409" GET \
        "$ALLSTARR_BASE/Playlists/$external_playlist_id/InstantMix?UserId=$best_user_id"
else
    echo "external playlist checks skipped=no provider-backed playlist result"
fi
if [[ -n "$virtual_playlist_id" ]]; then
    virtual_items_url="$ALLSTARR_BASE/Playlists/$virtual_playlist_id/Items?UserId=$best_user_id&StartIndex=0&Limit=100&Fields=$full_item_fields"
    check_json "virtual injected entries/index data" \
        "$virtual_items_url" \
        "$item_contract
         (.Items | type == \"array\" and length > 0) and
         (.TotalRecordCount >= (.Items | length)) and
         ([.Items[].PlaylistItemId] | unique | length) == (.Items | length) and
         all(.Items[];
             client_item and
             .ParentId == \$playlist_id and
             (.PlaylistItemId | nonempty) and
             (.Album | nonempty) and
             (.Artists | type == \"array\" and length > 0 and all(.[]; nonempty)) and
             (if (.Id | startswith(\"allstarr-unresolved-\"))
              then .PlayAccess == \"None\" and .CanDownload == false and
                   ((.MediaSources // []) | length == 0)
              elif (.Id | startswith(\"ext-\"))
              then external_audio
              else ((.MediaSources // []) | length > 0) and
                   (.ProviderIds.AllstarrSource | nonempty)
              end))" \
        --arg playlist_id "$virtual_playlist_id"
    if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
           "$virtual_items_url" -o "$virtual_items_file"; then
        compare_projection "virtual playlist stable projection" \
            "$virtual_items_url" \
            "$virtual_items_url" \
            '[.Items[] |
              {Id,PlaylistItemId,ParentId,Name,Album,Artists,AlbumId,RunTimeTicks,
               ProviderIds,ImageTags,MediaSources}]'
        while IFS=$'\t' read -r injected_provider injected_song_id; do
            check_json "injected $injected_provider detail" \
                "$ALLSTARR_BASE/Items/$injected_song_id?UserId=$best_user_id" \
                "$item_contract external_audio and .Id == \$id" --arg id "$injected_song_id"
            if [[ "$TEST_EXTERNAL_STREAM" == 1 ]]; then
                check_external_stream "injected $injected_provider stream" \
                    "$ALLSTARR_BASE/Audio/$injected_song_id/stream?static=true&UserId=$best_user_id"
            fi
        done < <(jq -r '
            [.Items[] |
             select((.Id // "") | test("^ext-.+-song-.+$")) |
             {provider: (first((.ProviderIds // {}) | keys[] |
                 select(. != "ISRC" and . != "AllstarrSource")) // ""), id: .Id}] |
            map(select(.provider != "")) | unique_by(.provider)[] |
            [.provider, .id] | @tsv' "$virtual_items_file")
        sampled_native_index=0
        while IFS= read -r sampled_native_id; do
            sampled_native_index=$((sampled_native_index + 1))
            check_json "virtual native detail $sampled_native_index" \
                "$ALLSTARR_BASE/Users/$best_user_id/Items/$sampled_native_id" \
                '.Id == $id and .Type == "Audio"' \
                --arg id "$sampled_native_id"
            check_code "virtual native stream $sampled_native_index" "200,206" HEAD \
                "$ALLSTARR_BASE/Audio/$sampled_native_id/stream?static=true&UserId=$best_user_id"
        done < <(jq -r --argjson count "$NATIVE_ROUTE_SAMPLES" '
            [.Items[] |
             select(((.Id // "") | startswith("ext-")) | not) |
             select(((.Id // "") | startswith("allstarr-unresolved-")) | not)] as $items |
            if ($items | length) == 0 then empty
            elif ($items | length) <= $count then $items[].Id
            elif $count == 1 then $items[0].Id
            else
                range(0; $count) as $index |
                $items[(($index * (($items | length) - 1) / ($count - 1)) | floor)].Id
            end
            ' "$virtual_items_file")
        matched_ids="$(jq -r '
            [.Items[] |
             select(((.Id // "") | startswith("ext-")) | not) |
             select(((.Id // "") | startswith("allstarr-unresolved-")) | not) |
             .Id] |
            unique | join(",")' "$virtual_items_file")"
        unresolved_id="$(jq -r '
            first(.Items[] |
                select((.Id // "") | startswith("allstarr-unresolved-"))) |
            .Id // empty' "$virtual_items_file")"
        if [[ -n "$unresolved_id" ]]; then
            check_code "unresolved playlist file safety" "404" GET \
                "$ALLSTARR_BASE/Items/$unresolved_id/File?UserId=$best_user_id"
            check_code "unresolved playlist stream safety" "404" GET \
                "$ALLSTARR_BASE/Audio/$unresolved_id/stream?UserId=$best_user_id"
            check_code "unresolved playlist universal safety" "404" GET \
                "$ALLSTARR_BASE/Audio/$unresolved_id/universal?UserId=$best_user_id"
            check_code "unresolved playlist playback safety" "404" GET \
                "$ALLSTARR_BASE/Items/$unresolved_id/PlaybackInfo?UserId=$best_user_id"
        fi
        if [[ -n "$matched_ids" ]] &&
           curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" --get \
               --data-urlencode "Ids=$matched_ids" \
               --data-urlencode "UserId=$best_user_id" \
               --data-urlencode "Recursive=true" \
               --data-urlencode "EnableImages=true" \
               --data-urlencode "EnableUserData=true" \
               --data-urlencode "Fields=$full_item_fields" \
               --data-urlencode "Limit=100" \
               "$DIRECT_BASE/Items" -o "$direct_virtual_items_file"; then
            checks=$((checks + 1))
            if jq -e --slurpfile source "$direct_virtual_items_file" '
                def unlabel:
                    if type == "string" then sub(" \\[[A-Z][A-Za-z0-9]{0,15}\\]$"; "") else . end;
                def source_item:
                    del(.ParentId, .PlaylistItemId, .ProviderIds.AllstarrSource);
                def injected_item:
                    del(.ParentId, .PlaylistItemId, .ProviderIds.AllstarrSource) |
                    (if .Name? then .Name |= unlabel else . end) |
                    (if .Album? then .Album |= unlabel else . end) |
                    (if .AlbumArtist? then .AlbumArtist |= unlabel else . end) |
                    (if .Artists? then .Artists |= map(unlabel) else . end) |
                    (if .ArtistItems? then
                        .ArtistItems |= map(if .Name? then .Name |= unlabel else . end)
                     else . end) |
                    (if .AlbumArtists? then
                        .AlbumArtists |= map(if .Name? then .Name |= unlabel else . end)
                     else . end);
                ($source[0].Items |
                    map({key: .Id, value: (. | source_item)}) | from_entries) as $originals |
                [.Items[] |
                 select(((.Id // "") | startswith("ext-")) | not) |
                 select(((.Id // "") | startswith("allstarr-unresolved-")) | not)] as $injected |
                ($injected | length > 0) and
                all($injected[]; $originals[.Id] != null and
                    ((. | injected_item) == $originals[.Id]))
                ' "$virtual_items_file" >/dev/null; then
                printf 'PASS %-34s exact full-object parity\n' \
                    "virtual matched source DTO"
            else
                printf 'FAIL %-34s dropped-or-changed-field\n' \
                    "virtual matched source DTO"
                failures=$((failures + 1))
            fi
        else
            block "virtual-matched-full-dto=no matched Jellyfin entries or source fetch"
        fi
    else
        checks=$((checks + 1))
        failures=$((failures + 1))
        echo "FAIL virtual matched source DTO fetch"
    fi
    check_json "virtual playlist definition" \
        "$ALLSTARR_BASE/Playlists/$virtual_playlist_id?UserId=$best_user_id" \
        '(.Shares | type == "array") and (.ItemIds | type == "array") and
         all(.ItemIds[]; type == "string" and length > 0)'
    check_json "virtual playlist item detail" \
        "$ALLSTARR_BASE/Items/$virtual_playlist_id?UserId=$best_user_id" \
        '(.Id | type == "string" and length > 0) and .Type == "Playlist" and
         (.ImageTags | type == "object") and
         (.UserData.Key | type == "string" and length > 0) and
         (.UserData.ItemId == .Id)'
    virtual_art_tag="$(curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$ALLSTARR_BASE/Items/$virtual_playlist_id?UserId=$best_user_id" |
        jq -r '.ImageTags.Primary // empty' || true)"
    if [[ -n "$virtual_art_tag" ]]; then
        check_image "virtual playlist artwork" \
            "$ALLSTARR_BASE/Items/$virtual_playlist_id/Images/Primary?UserId=$best_user_id"
        check_public_image "virtual artwork without token" \
            "$ALLSTARR_BASE/Items/$virtual_playlist_id/Images/Primary?UserId=$best_user_id"
        check_image "virtual long artwork" \
            "$ALLSTARR_BASE/Items/$virtual_playlist_id/Images/Primary/0/$virtual_art_tag/jpg/300/300/0/0?UserId=$best_user_id" \
            jpg
    else
        echo "virtual artwork skipped=selected playlist does not advertise Primary"
    fi
    check_code "virtual playlist ACL mode" "200,409" GET \
        "$ALLSTARR_BASE/Playlists/$virtual_playlist_id/Users?UserId=$best_user_id"
    check_code "virtual playlist mix mode" "200,409" GET \
        "$ALLSTARR_BASE/Playlists/$virtual_playlist_id/InstantMix?UserId=$best_user_id"
    block "virtual-playlist-writes=selected link may be writable; use stateful throwaway coverage"
    if [[ -n "$playlist_id" ]]; then
        if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
            "$DIRECT_BASE/Playlists/$playlist_id/Items?UserId=$best_user_id&Limit=1" |
            jq -S '.Items[0] | keys' >"$direct_shape_file" &&
           curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
            "$ALLSTARR_BASE/Playlists/$virtual_playlist_id/Items?UserId=$best_user_id&Limit=1" |
            jq -S '.Items[0] | keys' >"$allstarr_shape_file"; then
            echo "declared-diff native-vs-virtual-playlist-item keys"
            diff -u "$direct_shape_file" "$allstarr_shape_file" | sed -n '1,80p' || true
        else
            echo "declared-diff native-vs-virtual-playlist-item unavailable"
        fi
    fi
else
    block "virtual-playlist-live=no visible injected playlist"
fi
measure "direct playlist-list" "$DIRECT_BASE/Users/$best_user_id/Items?$playlist_query"
measure "allstarr playlist-list" "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query"
timing_delta "playlist proxy delta" "direct playlist-list" "allstarr playlist-list"

if [[ -n "$art_id" ]]; then
    check_code "artwork retrieval" "200,304" GET \
        "$ALLSTARR_BASE/Items/$art_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    check_code "artwork HEAD" "200,304" HEAD \
        "$ALLSTARR_BASE/Items/$art_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    check_public_image "artwork without token" \
        "$ALLSTARR_BASE/Items/$art_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    measure "direct artwork" "$DIRECT_BASE/Items/$art_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    measure "allstarr artwork" "$ALLSTARR_BASE/Items/$art_id/Images/Primary?maxWidth=300&maxHeight=300&UserId=$best_user_id"
    timing_delta "artwork proxy delta" "direct artwork" "allstarr artwork"
else
    echo "artwork skipped=no candidate in first 100 audio items"
fi

lyrics_id="${lyrics_id:-$media_id}"
check_code "lyrics response" "200,404" GET "$ALLSTARR_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id"
direct_lyrics_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" -o /dev/null -w '%{http_code}' \
    "$DIRECT_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id" || true)"
allstarr_lyrics_code="$(curl -sS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" -o /dev/null -w '%{http_code}' \
    "$ALLSTARR_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id" || true)"
direct_lyrics_code="${direct_lyrics_code:-000}"
allstarr_lyrics_code="${allstarr_lyrics_code:-000}"
if [[ "$direct_lyrics_code" == 200 && "$allstarr_lyrics_code" == 200 ]]; then
    compare_structure "lyrics structure parity" \
        "$DIRECT_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id" \
        "$ALLSTARR_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id"
else
    echo "lyrics structure skipped=direct:$direct_lyrics_code allstarr:$allstarr_lyrics_code"
fi
measure "direct lyrics" "$DIRECT_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id"
measure "allstarr lyrics" "$ALLSTARR_BASE/Audio/$lyrics_id/Lyrics?UserId=$best_user_id"
timing_delta "lyrics proxy delta" "direct lyrics" "allstarr lyrics"
check_code "download HEAD" "200,206" HEAD "$ALLSTARR_BASE/Items/$media_id/Download?UserId=$best_user_id"
check_code "file HEAD" "200,206" HEAD "$ALLSTARR_BASE/Items/$media_id/File?UserId=$best_user_id"
check_code "stream HEAD" "200,206" HEAD "$ALLSTARR_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id"
check_code "universal audio HEAD" "200,206,302" HEAD "$ALLSTARR_BASE/Audio/$media_id/universal?UserId=$best_user_id"
check_code "stream bounded range" "206" GET \
    "$ALLSTARR_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id" \
    --range 0-65535 --max-filesize 65536
check_range_parity "stream exact range parity" \
    "$DIRECT_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id" \
    "$ALLSTARR_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id"
check_concurrent_range_parity "stream concurrent range parity" \
    "$ALLSTARR_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id"
check_stream_cancellation "direct stream cancellation" \
    "$DIRECT_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id" \
    "$DIRECT_BASE/System/Info/Public"
check_stream_cancellation "allstarr stream cancellation" \
    "$ALLSTARR_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id" \
    "$ALLSTARR_BASE/health/live"
check_range_parity "Finer file ApiKey range parity" \
    "$DIRECT_BASE/Items/$media_id/File" \
    "$ALLSTARR_BASE/Items/$media_id/File" \
    query-api-key
measure "direct stream-64k" "$DIRECT_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id" \
    --range 0-65535 --max-filesize 65536
measure "allstarr stream-64k" "$ALLSTARR_BASE/Audio/$media_id/stream?static=true&UserId=$best_user_id" \
    --range 0-65535 --max-filesize 65536
timing_delta "stream startup delta" "direct stream-64k" "allstarr stream-64k"

if [[ "$TEST_PLAYLIST_WRITES" == 1 ]]; then
    echo "stateful-throwaway-playlist-checks"
    run_stateful_playlist_smoke
else
    block "playlist-write-live=create/rename/add/reorder/remove/share/delete require explicit opt-in"
fi
block "other-stateful-live=favorite/played/rating/display-preference writes require separate exact-state restoration"

nonmusic_id=""
while IFS= read -r user_id && [[ -z "$nonmusic_id" ]]; do
    nonmusic_id="$(
        curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
            "$DIRECT_BASE/Users/$user_id/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode,MusicVideo&Limit=1" |
            jq -r '.Items[0].Id // empty' || true
    )"
done < <(jq -r '.[].Id' "$users_file")
if [[ -n "$nonmusic_id" ]]; then
    check_code "deny real non-music item" "403" GET \
        "$ALLSTARR_BASE/Items/$nonmusic_id?UserId=$best_user_id"
    check_code "deny real non-music playback" "403" GET \
        "$ALLSTARR_BASE/Items/$nonmusic_id/PlaybackInfo?UserId=$best_user_id"
    check_code "deny real non-music artwork" "403" GET \
        "$ALLSTARR_BASE/Items/$nonmusic_id/Images/Primary?UserId=$best_user_id"
else
    echo "real non-music item checks skipped=no backend candidate"
fi

if command -v ping >/dev/null; then
    for host in "${DIRECT_BASE#*://}" "${ALLSTARR_BASE#*://}"; do
        echo -n "ping $host "
        ping -c "$SAMPLES" "$host" 2>/dev/null | tail -n 1 || echo "unavailable"
    done
fi

echo "log-correlation since=$started_at user-agent=AllstarrLiveSmoke/$run_id"
echo "live-smoke-end=$(date -u +%Y-%m-%dT%H:%M:%SZ) log-window-start=$started_at checks=$checks failures=$failures blocked=$blocked"
(( failures == 0 ))
