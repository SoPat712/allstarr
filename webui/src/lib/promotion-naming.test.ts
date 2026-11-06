import { describe, expect, it } from "vitest";
import { compileTokens, parseScript, type NamingToken } from "./promotion-naming";

describe("naming builder", () => {
  it("round trips fields, folders and escaped literal punctuation", () => {
    const tokens: NamingToken[] = [{ kind: "field", value: "artist" }, { kind: "literal", value: "/Price $5, (100%)\\/" }, { kind: "field", value: "title" }];
    const script = compileTokens(tokens);
    expect(script).toBe("%artist%/Price \\$5\\, \\(100\\%\\)\\\\/%title%");
    expect(parseScript(script)).toEqual(tokens);
  });

  it("keeps the default album fallback, optional year and disc-track editable", () => {
    const script = "$if2(%albumartist%,%artist%)/%album%$if(%year%, \\(%year%\\))/$if($gt(%totaldiscs%,1),%discnumber%-)$num(%tracknumber%,2) - %title%.%_extension%";
    const tokens = parseScript(script);
    expect(tokens?.filter(token => token.kind === "field").map(token => token.value)).toEqual(["albumartist-fallback", "album", "album-year", "disc-track", "title", "_extension"]);
    expect(compileTokens(tokens!)).toBe(script);
  });

  it("recognizes padded tracks and every supported raw variable", () => {
    const script = "%originaldate%/%releasecountry%/%media%/%musicbrainz_releasegroupid%/$num(%tracknumber%,2).%_extension%";
    expect(compileTokens(parseScript(script)!)).toBe(script);
  });

  it.each(["$upper(%artist%)/%title%", "%unknown%", "%artist", "name\\", "(name)", "name, song", ""])('leaves an unrepresentable script untouched: %s', script => {
    expect(parseScript(script)).toBeNull();
  });

  it("does not interpret escaped variables or functions as fields", () => {
    expect(parseScript("\\%title\\% \\$upper\\(word\\)")).toEqual([{ kind: "literal", value: "%title% $upper(word)" }]);
    expect(() => compileTokens([{ kind: "field", value: "unsupported" }])).toThrow();
  });
});
