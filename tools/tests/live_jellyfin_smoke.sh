#!/usr/bin/env bash
set -euo pipefail

: "${JELLYFIN_TOKEN:?Set JELLYFIN_TOKEN to a temporary Jellyfin API key or access token}"

DIRECT_BASE="${DIRECT_BASE:-https://jellyfin.joshpatra.me}"
ALLSTARR_BASE="${ALLSTARR_BASE:-https://jfm.joshpatra.me}"
SAMPLES="${SAMPLES:-3}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-20}"
TEST_EXTERNAL_STREAM="${TEST_EXTERNAL_STREAM:-0}"
TEST_PLAYLIST_WRITES="${TEST_PLAYLIST_WRITES:-0}"
PLAYLIST_WRITE_CONFIRM="${PLAYLIST_WRITE_CONFIRM:-}"

for command in curl jq awk diff cmp od wc; do
    command -v "$command" >/dev/null || { echo "Missing required command: $command" >&2; exit 1; }
done
[[ "$SAMPLES" =~ ^[1-9][0-9]*$ ]] || { echo "SAMPLES must be a positive integer" >&2; exit 1; }
[[ "$TEST_EXTERNAL_STREAM" == 0 || "$TEST_EXTERNAL_STREAM" == 1 ]] ||
    { echo "TEST_EXTERNAL_STREAM must be 0 or 1" >&2; exit 1; }
[[ "$TEST_PLAYLIST_WRITES" == 0 || "$TEST_PLAYLIST_WRITES" == 1 ]] ||
    { echo "TEST_PLAYLIST_WRITES must be 0 or 1" >&2; exit 1; }
if [[ "$TEST_PLAYLIST_WRITES" == 1 &&
      "$PLAYLIST_WRITE_CONFIRM" != create-and-delete-throwaway-playlist ]]; then
    echo "TEST_PLAYLIST_WRITES=1 requires PLAYLIST_WRITE_CONFIRM=create-and-delete-throwaway-playlist" >&2
    exit 1
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
stateful_playlist_id=""
stateful_playlist_name=""
playlist_identity_matches() {
    local playlist_id="$1" playlist_name="$2"
    [[ "$playlist_id" =~ ^[[:alnum:]_-]{1,128}$ ]] || return 1
    curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
        "$DIRECT_BASE/Items/$playlist_id?UserId=$best_user_id" |
        jq -e --arg id "$playlist_id" --arg name "$playlist_name" \
            '.Id == $id and .Name == $name and .Type == "Playlist"' >/dev/null
}
cleanup() {
    if [[ -n "$stateful_playlist_id" ]]; then
        if playlist_identity_matches "$stateful_playlist_id" "$stateful_playlist_name"; then
            curl -sS -X DELETE --max-time "$TIMEOUT_SECONDS" \
                -H "X-Emby-Token: $JELLYFIN_TOKEN" \
                "$DIRECT_BASE/Items/$stateful_playlist_id" -o /dev/null >/dev/null 2>&1 || true
        else
            printf 'WARN refusing cleanup for unverified playlist Id=%s Name=%s\n' \
                "$stateful_playlist_id" "$stateful_playlist_name" >&2
        fi
    fi
    rm -f "$users_file" "$items_file" "$response_file" "$timings_file" \
        "$direct_shape_file" "$allstarr_shape_file" "$metrics_file" \
        "$direct_media_file" "$allstarr_media_file" "$direct_headers_file" \
        "$allstarr_headers_file" "$virtual_items_file" "$direct_virtual_items_file"
}
trap cleanup EXIT

auth=(-H "X-Emby-Token: $JELLYFIN_TOKEN" -H "User-Agent: AllstarrLiveSmoke/$run_id")
echo "live-smoke-start=$started_at samples=$SAMPLES range_bytes=65536 external_stream=$TEST_EXTERNAL_STREAM playlist_writes=$TEST_PLAYLIST_WRITES"

curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$DIRECT_BASE/Users" -o "$users_file"
best_user_id=""
best_audio_count=-1
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
done < <(jq -r '.[].Id' "$users_file")
[[ -n "$best_user_id" && "$best_audio_count" -gt 0 ]] ||
    { echo "No Jellyfin user with audio visible to Allstarr was found" >&2; exit 1; }
auth=(-H "X-Emby-Authorization: MediaBrowser Client=\"AllstarrLiveSmoke\", Device=\"Qualification\", DeviceId=\"$run_id\", Version=\"1\", UserId=\"$best_user_id\", Token=\"$JELLYFIN_TOKEN\"" \
      -H "User-Agent: AllstarrLiveSmoke/$run_id")

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
        { ok += ($1 >= 200 && $1 < 400); bytes += $2; dns += $3; connect += $4; tls += $5; ttfb += $6; total += $7; codes[$1]++ }
        END {
            code_summary = ""
            for (code in codes) code_summary = code_summary (code_summary ? "," : "") code ":" codes[code]
            printf "%-24s ok=%d/%d codes=%s avg_bytes=%.0f dns_ms=%.1f connect_ms=%.1f tls_ms=%.1f ttfb_ms=%.1f total_ms=%.1f\n",
                label, ok, NR, code_summary, bytes / NR, dns * 1000 / NR, connect * 1000 / NR,
                tls * 1000 / NR, ttfb * 1000 / NR, total * 1000 / NR
            printf "%s\t%.3f\t%.3f\n", label, ttfb * 1000 / NR, total * 1000 / NR >> metrics
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

checks=0
failures=0

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

check_stateful_playlist_identity() {
    local label="$1" playlist_id="$2" playlist_name="$3"
    checks=$((checks + 1))
    if playlist_identity_matches "$playlist_id" "$playlist_name"; then
        printf 'PASS %-34s exact-id-name-type\n' "$label"
        return 0
    fi
    printf 'FAIL %-34s unverified Id=%s Name=%s\n' "$label" "$playlist_id" "$playlist_name"
    failures=$((failures + 1))
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
    local direct_code allstarr_code direct_bytes allstarr_bytes direct_range allstarr_range direct_type allstarr_type
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
        printf 'PASS %-34s bytes=65536 type=%s exact-body\n' "$label" "$allstarr_type"
    else
        printf 'FAIL %-34s direct=%s/%s/%s allstarr=%s/%s/%s\n' \
            "$label" "$direct_code" "$direct_bytes" "$direct_range" \
            "$allstarr_code" "$allstarr_bytes" "$allstarr_range"
        failures=$((failures + 1))
    fi
}

run_stateful_playlist_smoke() {
    local playlist_name renamed_name create_payload update_payload other_user_id
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
    if ! check_stateful_playlist_identity \
        "stateful create direct-visible" "$candidate_playlist_id" "$playlist_name"; then
        return
    fi
    stateful_playlist_id="$candidate_playlist_id"
    stateful_playlist_name="$playlist_name"

    update_payload="$(jq -cn --arg name "$renamed_name" '{Name:$name}')"
    if ! stateful_call "stateful playlist rename" "204" POST \
        "$ALLSTARR_BASE/Playlists/$stateful_playlist_id" \
        -H "Content-Type: application/json" --data-binary "$update_payload"; then
        return
    fi
    stateful_playlist_name="$renamed_name"
    if ! check_stateful_playlist_identity \
        "stateful rename direct-visible" "$stateful_playlist_id" "$renamed_name"; then
        return
    fi

    if [[ -n "$second_media_id" ]]; then
        if ! stateful_call "stateful playlist add" "204" POST \
            "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Items?ids=$second_media_id&UserId=$best_user_id"; then
            return
        fi
        check_json "stateful add direct-visible" \
            "$DIRECT_BASE/Playlists/$stateful_playlist_id/Items?UserId=$best_user_id" \
            '([.Items[].Id] | index($first) != null) and
             ([.Items[].Id] | index($second) != null) and
             all(.Items[]; (.PlaylistItemId | type == "string" and length > 0))' \
            --arg first "$media_id" --arg second "$second_media_id"
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
        check_json "stateful reorder direct-visible" \
            "$DIRECT_BASE/Playlists/$stateful_playlist_id/Items?UserId=$best_user_id" \
            '.Items[0].Id == $id' --arg id "$second_media_id"

        if ! stateful_call "stateful playlist remove" "204" DELETE \
            "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Items?entryIds=$first_entry_id"; then
            return
        fi
        check_json "stateful remove direct-visible" \
            "$DIRECT_BASE/Playlists/$stateful_playlist_id/Items?UserId=$best_user_id" \
            '([.Items[].Id] | index($removed) == null) and
             ([.Items[].Id] | index($kept) != null)' \
            --arg removed "$media_id" --arg kept "$second_media_id"
    else
        echo "BLOCKED stateful-add-remove-reorder=no second streamable audio item in first 100"
    fi

    compare_structure "stateful playlist ACL relay" \
        "$DIRECT_BASE/Playlists/$stateful_playlist_id/Users" \
        "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Users"
    other_user_id="$(jq -r --arg owner "$best_user_id" \
        'first(.[] | select(.Id != $owner)) | .Id // empty' "$users_file")"
    if [[ -n "$other_user_id" ]]; then
        if ! stateful_call "stateful playlist share" "204" POST \
            "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/Users/$other_user_id" \
            -H "Content-Type: application/json" --data-binary '{"CanEdit":true}'; then
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
        echo "BLOCKED stateful-share=no second Jellyfin user"
    fi

    compare_structure "stateful playlist mix relay" \
        "$DIRECT_BASE/Playlists/$stateful_playlist_id/InstantMix?Limit=10" \
        "$ALLSTARR_BASE/Playlists/$stateful_playlist_id/InstantMix?Limit=10"

    deleted_playlist_id="$stateful_playlist_id"
    if ! stateful_call "stateful playlist delete" "204" DELETE \
        "$ALLSTARR_BASE/Items/$deleted_playlist_id"; then
        return
    fi
    if stateful_call "stateful delete direct-visible" "404" GET \
        "$DIRECT_BASE/Items/$deleted_playlist_id"; then
        stateful_playlist_id=""
        stateful_playlist_name=""
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
    def client_item:
        (.Id | nonempty) and (.Name | nonempty) and (.Type | nonempty) and
        named_ids and album_ids and genre_ids and media_ids and user_data;
'

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
[[ -z "$artist_id" ]] || check_json "artist detail" "$ALLSTARR_BASE/Artists/$artist_id?UserId=$best_user_id" '.Type == "MusicArtist"'
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
check_json "music library root" "$ALLSTARR_BASE/Items/Root?UserId=$best_user_id" \
    '.Id != null and .CollectionType == "music"'
check_json "music-only counts" "$ALLSTARR_BASE/Items/Counts?UserId=$best_user_id" \
    '.MovieCount == 0 and .SeriesCount == 0 and .EpisodeCount == 0 and .MusicVideoCount == 0'
check_json "playback info" "$ALLSTARR_BASE/Items/$media_id/PlaybackInfo?UserId=$best_user_id" \
    '(.MediaSources | type == "array") and (.MediaSources | length > 0)'
compare_structure "playback info structure parity" \
    "$DIRECT_BASE/Items/$media_id/PlaybackInfo?UserId=$best_user_id" \
    "$ALLSTARR_BASE/Items/$media_id/PlaybackInfo?UserId=$best_user_id"
check_json "similar music" "$ALLSTARR_BASE/Items/$media_id/Similar?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; (.Type == "Audio" or .Type == "MusicAlbum" or .Type == "MusicArtist"))'
check_json "instant mix" "$ALLSTARR_BASE/Songs/$media_id/InstantMix?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'
[[ -z "$album_id" ]] || check_json "album instant mix" "$ALLSTARR_BASE/Albums/$album_id/InstantMix?UserId=$best_user_id&Limit=10" \
    '(.Items | type == "array") and all(.Items[]; .Type == "Audio")'

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
    "/api/admin/health" \
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
compare_structure "audio detail structure parity" \
    "$DIRECT_BASE/Users/$best_user_id/Items/$media_id" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items/$media_id"
compare_projection "audio detail stable data" \
    "$DIRECT_BASE/Users/$best_user_id/Items/$media_id" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items/$media_id" \
    '{Id,Name,Type,AlbumId,Artists,ArtistItems,RunTimeTicks,ImageTags,ProviderIds}'
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

external_song_id=""
if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
    "$ALLSTARR_BASE/Items?UserId=$best_user_id&SearchTerm=$search_term_encoded&IncludeItemTypes=Audio&Limit=50" \
    -o "$response_file"; then
    external_song_id="$(jq -r '
        first(.Items[] | select((.Id // "") | test("^ext-.+-song-"; "i"))) | .Id // empty' "$response_file")"
else
    checks=$((checks + 1))
    failures=$((failures + 1))
    echo "FAIL external item discovery"
fi
if [[ -n "$external_song_id" ]]; then
    external_detail_url="$ALLSTARR_BASE/Items/$external_song_id?UserId=$best_user_id"
    check_json "external item recursive DTO" "$external_detail_url" \
        "$item_contract client_item and ((.MediaSources // []) | length > 0)"
    check_json "external playback identity" \
        "$ALLSTARR_BASE/Items/$external_song_id/PlaybackInfo?UserId=$best_user_id" \
        '(.MediaSources | type == "array" and length > 0) and
         all(.MediaSources[]; .Id == $id and (.DirectStreamUrl | contains($id)))' \
        --arg id "$external_song_id"
    check_code "external artwork route" "200,304,404" GET \
        "$ALLSTARR_BASE/Items/$external_song_id/Images/Primary?UserId=$best_user_id"
    external_art_tag="$(curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" "$external_detail_url" |
        jq -r '.ImageTags.Primary // empty' || true)"
    if [[ -n "$external_art_tag" ]]; then
        check_image "external advertised artwork" \
            "$ALLSTARR_BASE/Items/$external_song_id/Images/Primary?UserId=$best_user_id"
        check_image "external long artwork" \
            "$ALLSTARR_BASE/Items/$external_song_id/Images/Primary/0/$external_art_tag/jpg/300/300/0/0?UserId=$best_user_id" \
            jpg
    fi
    check_code "external lyrics route" "200,404" GET \
        "$ALLSTARR_BASE/Audio/$external_song_id/Lyrics?UserId=$best_user_id"
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
        check_code "external stream HEAD" "200,206" HEAD \
            "$ALLSTARR_BASE/Audio/$external_song_id/stream?static=true&UserId=$best_user_id"
        measure "external stream-64k" \
            "$ALLSTARR_BASE/Audio/$external_song_id/stream?static=true&UserId=$best_user_id" \
            --range 0-65535 --max-filesize 65536
    else
        echo "external stream skipped=set TEST_EXTERNAL_STREAM=1 for provider/cold-cache media"
    fi
else
    echo "external item checks skipped=no provider-backed audio result for search term"
fi

playlist_query="Recursive=true&IncludeItemTypes=Playlist&Limit=100"
direct_playlists=0
allstarr_playlists=0
playlist_id=""
virtual_playlist_id=""
external_playlist_id=""
if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
    "$DIRECT_BASE/Users/$best_user_id/Items?$playlist_query" -o "$response_file"; then
    direct_playlists="$(jq -r '.TotalRecordCount // (.Items | length) // 0' "$response_file")"
    playlist_id="$(jq -r '.Items[0].Id // empty' "$response_file")"
else
    checks=$((checks + 1))
    failures=$((failures + 1))
    echo "FAIL direct playlist discovery"
fi
if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
    "$ALLSTARR_BASE/Users/$best_user_id/Items?$playlist_query" -o "$response_file"; then
    allstarr_playlists="$(jq -r '.TotalRecordCount // (.Items | length) // 0' "$response_file")"
    virtual_playlist_id="$(jq -r '
        (first(.Items[] | select(
            ((.Id // "") | startswith("allstarr-vpl-")) and
            ((.ChildCount // 0) > 0) and .ImageTags.Primary != null)) //
         first(.Items[] | select(
            ((.Id // "") | startswith("allstarr-vpl-")) and
            ((.ChildCount // 0) > 0)))) | .Id // empty' "$response_file")"
    external_playlist_id="$(jq -r '
        first(.Items[] | select((.Id // "") | test("^ext-.+-playlist-"; "i"))) | .Id // empty' "$response_file")"
else
    checks=$((checks + 1))
    failures=$((failures + 1))
    echo "FAIL Allstarr playlist discovery"
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
    '[.Items[] | {Id,Name,Type,ChildCount,ImageTags,ProviderIds}] | sort_by(.Id)' \
    '[.Items[] | select(
        ((((.Id // "") | startswith("allstarr-vpl-")) or
          ((.Id // "") | test("^ext-.+-playlist-"; "i"))) | not)) |
        {Id,Name,Type,ChildCount,ImageTags,ProviderIds}] | sort_by(.Id)'
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
        echo "BLOCKED playlist-definition-upstream=status-$direct_playlist_definition_code"
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
        "$item_contract (.Items | type == \"array\") and all(.Items[]; client_item)"
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
         all(.Items[];
             client_item and
             .ParentId == \$playlist_id and
             (.PlaylistItemId | nonempty) and
             (.Album | nonempty) and
             (.Artists | type == \"array\" and length > 0 and all(.[]; nonempty)) and
             (if (.Id | startswith(\"ext-\"))
              then ((.MediaSources // []) | length > 0)
              else ((.MediaSources // []) | length > 0) and
                   (.ProviderIds.AllstarrSource | nonempty)
              end))" \
        --arg playlist_id "$virtual_playlist_id"
    if curl -fsS --max-time "$TIMEOUT_SECONDS" "${auth[@]}" \
           "$virtual_items_url" -o "$virtual_items_file"; then
        matched_ids="$(jq -r '
            [.Items[] | select(((.Id // "") | startswith("ext-")) | not) | .Id] |
            unique | join(",")' "$virtual_items_file")"
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
                    if type == "string" then sub(" \\[[A-Z]+\\]$"; "") else . end;
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
                [.Items[] | select(((.Id // "") | startswith("ext-")) | not)] as $injected |
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
            echo "BLOCKED virtual-matched-full-dto=no matched Jellyfin entries or source fetch"
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
    echo "BLOCKED virtual-playlist-writes=selected link may be writable; use stateful throwaway coverage"
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
    echo "BLOCKED virtual-playlist-live=no user-bound Jellyfin token or visible injected playlist"
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
    echo "BLOCKED playlist-write-live=create/rename/add/reorder/remove/share/delete require explicit opt-in"
fi
echo "BLOCKED other-stateful-live=favorite/played/rating/display-preference writes require separate exact-state restoration"

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

if [[ "$direct_version" != 12.* ]]; then
    echo "BLOCKED jellyfin-12-live=no 12.x runtime (current=$direct_version); deterministic pinned OpenAPI coverage remains required"
fi
echo "log-correlation since=$started_at user-agent=AllstarrLiveSmoke/$run_id"
echo "live-smoke-end=$(date -u +%Y-%m-%dT%H:%M:%SZ) log-window-start=$started_at checks=$checks failures=$failures"
(( failures == 0 ))
