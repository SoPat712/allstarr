export function layoutLabel(S: number, x: number, y: number, width: number, source: string[], minimum: number,
  measure: (text: string, size: number, bold: boolean) => number, plain = false) {
  const size = Math.max(minimum, S * 0.042), padding = S * 0.025, lineHeight = size * 1.2;
  width = Math.min(S * 0.9, Math.max(width, ...source.map((text, index) => measure(text, size, index === 0 && !plain) + padding * 2)));
  const lines: { text: string; bold: boolean }[] = [];
  source.forEach((text, index) => {
    const bold = index === 0 && !plain;
    let line = "";
    for (const word of text.split(/\s+/)) {
      if (line && measure(`${line} ${word}`, size, bold) > width - padding * 2) { lines.push({ text: line, bold }); line = ""; }
      for (const letter of [...(line ? ` ${word}` : word)]) {
        if (line && measure(line + letter, size, bold) > width - padding * 2) { lines.push({ text: line, bold }); line = ""; }
        line += letter;
      }
    }
    lines.push({ text: line, bold });
  });
  const maxLines = Math.max(1, Math.floor((S * 0.9 - padding * 2) / lineHeight));
  if (lines.length > maxLines) {
    lines.length = maxLines;
    const last = lines[maxLines - 1];
    while (measure(last.text + "…", size, last.bold) > width - padding * 2) last.text = last.text.slice(0, -1);
    last.text += "…";
  }
  const height = lineHeight * lines.length + padding * 2;
  return { size, lines, width, height, lineHeight, x: Math.max(S * 0.03, Math.min(x, S * 0.97 - width)), y: Math.max(S * 0.03, Math.min(y, S * 0.97 - height)) };
}
