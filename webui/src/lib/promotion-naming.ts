export type NamingToken = { kind: "field" | "literal"; value: string };

const patterns: Record<string, string> = {
  "albumartist-fallback": "$if2(%albumartist%,%artist%)",
  "album-year": "$if(%year%, \\(%year%\\))",
  "disc-track": "$if($gt(%totaldiscs%,1),%discnumber%-)$num(%tracknumber%,2)",
  "padded-track": "$num(%tracknumber%,2)",
};
const labels: Record<string, string> = {
  artist: "Artist", albumartist: "Album artist", "albumartist-fallback": "Album artist",
  album: "Album", year: "Year", "album-year": "Year in parentheses", originalyear: "Original year",
  discnumber: "Disc", tracknumber: "Track", "padded-track": "Track (01)", "disc-track": "Disc-track",
  title: "Title", genre: "Genre", label: "Label", catalognumber: "Catalog no.", _quality: "Quality",
  releasetype: "Release type", _extension: "Extension",
};
const variables = new Set(["artist", "albumartist", "album", "title", "date", "originaldate", "year", "originalyear", "tracknumber", "totaltracks", "discnumber", "totaldiscs", "genre", "label", "catalognumber", "releasetype", "releasecountry", "media", "_extension", "_quality"]);
const supported = (value: string) => variables.has(value) || /^musicbrainz_[a-zA-Z0-9_]+id$/.test(value);
export const namingFields = ["artist", "albumartist-fallback", "album", "year", "album-year", "originalyear", "discnumber", "padded-track", "disc-track", "title", "genre", "label", "catalognumber", "_quality", "releasetype", "_extension"].map(value => ({ value, label: labels[value] }));
export const tokenLabel = (token: NamingToken) => token.kind === "literal" ? token.value || "Empty text" : labels[token.value] ?? token.value;

export function compileTokens(tokens: readonly NamingToken[]): string {
  return tokens.map(token => {
    if (token.kind === "literal") return token.value.replace(/[$%(),\\]/g, "\\$&");
    if (patterns[token.value]) return patterns[token.value];
    if (!supported(token.value)) throw new Error("Choose a supported naming field.");
    return `%${token.value}%`;
  }).join("");
}

export function parseScript(script: string): NamingToken[] | null {
  if (!script || script.length > 16384) return null;
  const tokens: NamingToken[] = [];
  let literal = "", position = 0;
  const flush = () => { if (literal) { tokens.push({ kind: "literal", value: literal }); literal = ""; } };
  while (position < script.length) {
    const character = script[position];
    if (character === "\\") {
      if (++position === script.length) return null;
      literal += script[position++];
    } else if (character === "$") {
      const pattern = Object.entries(patterns).find(([, expression]) => script.startsWith(expression, position));
      if (!pattern) return null;
      flush(); tokens.push({ kind: "field", value: pattern[0] }); position += pattern[1].length;
    } else if (character === "%") {
      const end = script.indexOf("%", position + 1);
      if (end < 0) return null;
      const value = script.slice(position + 1, end);
      if (!supported(value)) return null;
      flush(); tokens.push({ kind: "field", value }); position = end + 1;
    } else {
      if ("(),".includes(character)) return null;
      literal += character; position++;
    }
  }
  flush();
  return tokens;
}
