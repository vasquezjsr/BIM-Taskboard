/**
 * Builds a note-heavy instructions notebook from
 * docs/BIM_BOARDROOM_COMPLETE_INSTRUCTIONS.md:
 * - docs/BIM_BOARDROOM_COMPLETE_INSTRUCTIONS_NOTEBOOK.html
 * - docs/BIM_BOARDROOM_COMPLETE_INSTRUCTIONS_NOTEBOOK.pdf (Chrome/Edge headless)
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { execSync } from 'child_process';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, '..');
const sourcePath = path.join(root, 'docs', 'BIM_BOARDROOM_COMPLETE_INSTRUCTIONS.md');
const outDir = path.join(root, 'docs');
const htmlPath = path.join(outDir, 'BIM_BOARDROOM_COMPLETE_INSTRUCTIONS_NOTEBOOK.html');
const pdfPath = path.join(outDir, 'BIM_BOARDROOM_COMPLETE_INSTRUCTIONS_NOTEBOOK.pdf');

const md = fs.readFileSync(sourcePath, 'utf8');

function escapeHtml(text) {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function inlineMarkdown(text) {
  return escapeHtml(text)
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/`([^`]+)`/g, '<code>$1</code>');
}

function parseTableRow(line) {
  return line
    .trim()
    .replace(/^\|/, '')
    .replace(/\|$/, '')
    .split('|')
    .map((cell) => cell.trim());
}

function isTableSeparator(line) {
  return /^\|[\s:|-]+\|$/.test(line.trim());
}

const NOTE_LINES = {
  subsection: 18,
  section: 30,
  part: 34,
  extraPage: 34,
};

function noteLines(count) {
  return Array.from({ length: count }, () => '<div class="note-line"></div>').join('');
}

function noteBlock(kind, label, lineCount) {
  return `
<section class="note-area note-area-${kind}">
  <div class="note-label">${escapeHtml(label)}</div>
  <div class="note-lines">${noteLines(lineCount)}</div>
</section>`;
}

function blankPage(label, lineCount = NOTE_LINES.extraPage) {
  return `
<section class="blank-page">
  <div class="note-label">${escapeHtml(label)}</div>
  <div class="note-lines">${noteLines(lineCount)}</div>
</section>`;
}

function renderMarkdownBlock(lines) {
  const html = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];

    if (!line.trim()) {
      i += 1;
      continue;
    }

    if (line.startsWith('```')) {
      const fence = [];
      i += 1;
      while (i < lines.length && !lines[i].startsWith('```')) {
        fence.push(escapeHtml(lines[i]));
        i += 1;
      }
      i += 1;
      html.push(`<pre><code>${fence.join('\n')}</code></pre>`);
      continue;
    }

    if (line.startsWith('|') && i + 1 < lines.length && isTableSeparator(lines[i + 1])) {
      const header = parseTableRow(line);
      i += 2;
      const rows = [];
      while (i < lines.length && lines[i].startsWith('|')) {
        rows.push(parseTableRow(lines[i]));
        i += 1;
      }
      html.push('<table><thead><tr>');
      header.forEach((cell) => html.push(`<th>${inlineMarkdown(cell)}</th>`));
      html.push('</tr></thead><tbody>');
      rows.forEach((row) => {
        html.push('<tr>');
        row.forEach((cell) => html.push(`<td>${inlineMarkdown(cell)}</td>`));
        html.push('</tr>');
      });
      html.push('</tbody></table>');
      continue;
    }

    if (line.startsWith('#### ')) {
      html.push(`<h4>${inlineMarkdown(line.slice(5))}</h4>`);
      i += 1;
      continue;
    }
    if (line.startsWith('### ')) {
      html.push(`<h3>${inlineMarkdown(line.slice(4))}</h3>`);
      i += 1;
      continue;
    }
    if (line.startsWith('## ')) {
      html.push(`<h2>${inlineMarkdown(line.slice(3))}</h2>`);
      i += 1;
      continue;
    }
    if (line.startsWith('# ')) {
      html.push(`<h1>${inlineMarkdown(line.slice(2))}</h1>`);
      i += 1;
      continue;
    }

    if (line.startsWith('- ') || line.startsWith('* ')) {
      html.push('<ul>');
      while (i < lines.length && (lines[i].startsWith('- ') || lines[i].startsWith('* '))) {
        html.push(`<li>${inlineMarkdown(lines[i].slice(2))}</li>`);
        i += 1;
      }
      html.push('</ul>');
      continue;
    }

    if (/^\d+\.\s/.test(line)) {
      html.push('<ol>');
      while (i < lines.length && /^\d+\.\s/.test(lines[i])) {
        html.push(`<li>${inlineMarkdown(lines[i].replace(/^\d+\.\s/, ''))}</li>`);
        i += 1;
      }
      html.push('</ol>');
      continue;
    }

    if (line.startsWith('---')) {
      html.push('<hr />');
      i += 1;
      continue;
    }

    const para = [line];
    i += 1;
    while (
      i < lines.length &&
      lines[i].trim() &&
      !lines[i].startsWith('#') &&
      !lines[i].startsWith('- ') &&
      !lines[i].startsWith('* ') &&
      !lines[i].startsWith('|') &&
      !lines[i].startsWith('```') &&
      !lines[i].startsWith('---') &&
      !/^\d+\.\s/.test(lines[i])
    ) {
      para.push(lines[i]);
      i += 1;
    }
    html.push(`<p>${inlineMarkdown(para.join(' '))}</p>`);
  }

  return html.join('\n');
}

function subsectionTitle(line) {
  return line.startsWith('### ') ? line.slice(4).trim() : '';
}

function sectionTitle(line) {
  return line.startsWith('## ') ? line.slice(3).trim() : '';
}

function isPartSection(title) {
  return /^Part [A-Z] —/.test(title) || title.startsWith('Appendix');
}

function renderDocumentWithNotes(mdText) {
  const lines = mdText.split(/\r?\n/);
  const html = [];
  const toc = [];

  let sectionTitleText = 'Introduction';
  let subsectionTitleText = '';
  let buffer = [];
  let inSection = true;

  const flushSubsection = () => {
    if (!buffer.length) return;
    html.push(renderMarkdownBlock(buffer));
    buffer = [];
    if (subsectionTitleText) {
      // Skip redundant "Notes — … holes" subsections doubling note pads —
      // still give every ### a writing area, including dedicated Notes ones.
      html.push(
        noteBlock('subsection', `Write here — ${subsectionTitleText}`, NOTE_LINES.subsection)
      );
    }
  };

  const closeSection = () => {
    flushSubsection();
    if (!inSection) return;

    html.push(
      noteBlock('section', `Chapter notes — ${sectionTitleText}`, NOTE_LINES.section)
    );
    html.push(blankPage(`Extra notes — ${sectionTitleText}`, NOTE_LINES.section));

    if (isPartSection(sectionTitleText)) {
      html.push(blankPage(`Ideas / holes — ${sectionTitleText}`, NOTE_LINES.part));
      html.push(blankPage(`Scratch pad — ${sectionTitleText}`, NOTE_LINES.part));
    }

    toc.push(sectionTitleText);
    inSection = false;
  };

  for (let i = 0; i < lines.length; i += 1) {
    const line = lines[i];

    if (line.startsWith('## ')) {
      closeSection();
      sectionTitleText = sectionTitle(line);
      subsectionTitleText = '';
      inSection = true;
      buffer = [line];
      continue;
    }

    if (line.startsWith('### ')) {
      flushSubsection();
      subsectionTitleText = subsectionTitle(line);
      buffer = [line];
      continue;
    }

    buffer.push(line);
  }

  closeSection();

  html.push(blankPage('General notes — open questions', NOTE_LINES.extraPage));
  html.push(blankPage('General notes — feature requests', NOTE_LINES.extraPage));
  html.push(blankPage('General notes — training script', NOTE_LINES.extraPage));
  html.push(blankPage('Scratch pad', NOTE_LINES.extraPage));
  html.push(blankPage('Scratch pad (continued)', NOTE_LINES.extraPage));

  return { body: html.join('\n'), toc };
}

const { body: documentBody, toc } = renderDocumentWithNotes(md);

const html = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>BIM Boardroom — Complete Instructions Notebook</title>
  <style>
    :root {
      --ink: #1a1f2e;
      --muted: #5c6478;
      --rule: #c5cad6;
      --note-bg: #fafbfe;
      --accent: #2f5d50;
    }

    * { box-sizing: border-box; }

    body {
      margin: 0;
      font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
      font-size: 11pt;
      line-height: 1.45;
      color: var(--ink);
      background: #fff;
    }

    .cover {
      min-height: 100vh;
      padding: 2.5rem 2rem;
      page-break-after: always;
      border-bottom: 4px solid var(--accent);
    }

    .cover h1 {
      font-size: 2rem;
      margin: 0 0 0.5rem;
      color: var(--accent);
    }

    .cover .subtitle {
      font-size: 1.1rem;
      color: var(--muted);
      margin: 0 0 2rem;
    }

    .ipad-box {
      background: var(--note-bg);
      border: 1px solid var(--rule);
      border-radius: 10px;
      padding: 1.25rem 1.5rem;
      margin: 1.5rem 0;
    }

    .ipad-box h2 {
      margin: 0 0 0.75rem;
      font-size: 1rem;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--accent);
    }

    .ipad-box ul { margin: 0.5rem 0 0 1.2rem; padding: 0; }
    .ipad-box li { margin: 0.35rem 0; }

    .toc {
      margin-top: 2rem;
      columns: 2;
      column-gap: 2rem;
    }

    .toc h2 { font-size: 1rem; margin: 0 0 0.75rem; }
    .toc ol { margin: 0; padding-left: 1.25rem; }
    .toc li { margin: 0.25rem 0; break-inside: avoid; }

    main {
      padding: 1.5rem 2rem 3rem;
      max-width: 8.5in;
      margin: 0 auto;
    }

    h1 { font-size: 1.6rem; margin: 1.5rem 0 0.75rem; color: var(--accent); }
    h2 {
      font-size: 1.25rem;
      margin: 2rem 0 0.75rem;
      padding-top: 0.5rem;
      border-top: 2px solid var(--accent);
      color: var(--accent);
      page-break-after: avoid;
    }
    h3 { font-size: 1.05rem; margin: 1.25rem 0 0.5rem; page-break-after: avoid; }
    h4 { font-size: 0.95rem; margin: 1rem 0 0.35rem; color: var(--muted); }

    p { margin: 0.5rem 0; }
    ul, ol { margin: 0.35rem 0 0.75rem 1.2rem; padding: 0; }
    li { margin: 0.2rem 0; }
    hr { border: none; border-top: 1px solid var(--rule); margin: 1rem 0; }
    code {
      font-family: Consolas, "SF Mono", monospace;
      font-size: 0.9em;
      background: #f0f2f8;
      padding: 0.1em 0.35em;
      border-radius: 3px;
    }
    pre {
      background: #f0f2f8;
      padding: 0.75rem 1rem;
      border-radius: 6px;
      overflow-x: auto;
      font-size: 0.85rem;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      margin: 0.75rem 0 1rem;
      font-size: 0.88rem;
      page-break-inside: avoid;
    }
    th, td {
      border: 1px solid var(--rule);
      padding: 0.35rem 0.5rem;
      text-align: left;
      vertical-align: top;
    }
    th { background: #eef1f8; font-weight: 600; }

    .note-area,
    .blank-page {
      margin: 1.5rem 0;
      padding: 0.75rem 1rem 1rem;
      background: var(--note-bg);
      border: 1.5px dashed var(--accent);
      border-radius: 8px;
    }

    .note-area-subsection { min-height: 4.25in; }
    .note-area-section { min-height: 6.5in; }

    .blank-page {
      page-break-before: always;
      min-height: 9in;
      border-style: solid;
      border-color: #d8deea;
    }

    .note-label {
      font-size: 0.75rem;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: var(--accent);
      margin-bottom: 0.65rem;
    }

    .note-lines { display: flex; flex-direction: column; gap: 1.5rem; }
    .note-line {
      border-bottom: 1px solid var(--rule);
      min-height: 1.5rem;
    }

    @media print {
      body { font-size: 10pt; }
      .cover { min-height: auto; }
      .note-area-subsection { min-height: 3.75in; }
      .note-area-section { min-height: 6in; }
      .blank-page { min-height: 8.5in; }
      .no-print { display: none; }
    }

    @page {
      margin: 0.7in;
      size: letter;
    }
  </style>
</head>
<body>
  <section class="cover">
    <h1>BIM Boardroom</h1>
    <p class="subtitle">Complete Instructions — notebook edition with writing space</p>
    <p>
      Full-product reference generated from
      <strong>docs/BIM_BOARDROOM_COMPLETE_INSTRUCTIONS.md</strong>.
      Every subsection includes a lined note pad. Every chapter includes summary notes plus
      blank pages for holes, redesigns, and new ideas.
    </p>

    <div class="ipad-box">
      <h2>Using this on iPad (Apple Pencil)</h2>
      <ul>
        <li><strong>Best format:</strong> PDF — import into GoodNotes, Notability, Freeform, or Files Markup.</li>
        <li><strong>From HTML:</strong> open the sibling <code>.html</code> → Print → Save as PDF.</li>
        <li><strong>From docs folder:</strong> use <code>BIM_BOARDROOM_COMPLETE_INSTRUCTIONS_NOTEBOOK.pdf</code> when generated.</li>
      </ul>
    </div>

    <div class="toc">
      <h2>Contents</h2>
      <ol>
        ${toc.map((title) => `<li>${escapeHtml(title)}</li>`).join('\n        ')}
      </ol>
    </div>

    <p class="no-print" style="margin-top:2rem;color:var(--muted);font-size:0.9rem;">
      Tip on PC: open this HTML in Edge/Chrome and press <strong>Ctrl+P</strong> → Save as PDF.
    </p>
  </section>

  <main>
    ${documentBody}
  </main>
</body>
</html>`;

fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(htmlPath, html, 'utf8');
console.log(`Wrote ${htmlPath}`);

const browsers = [
  process.env.CHROME_PATH,
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
].filter(Boolean);

let pdfWritten = false;
for (const browser of browsers) {
  if (!fs.existsSync(browser)) continue;
  try {
    const fileUrl = `file:///${htmlPath.replace(/\\/g, '/')}`;
    execSync(
      `"${browser}" --headless=new --disable-gpu --no-pdf-header-footer --print-to-pdf="${pdfPath}" "${fileUrl}"`,
      { stdio: 'pipe', timeout: 180000 }
    );
    if (fs.existsSync(pdfPath)) {
      pdfWritten = true;
      console.log(`Wrote ${pdfPath}`);
      break;
    }
  } catch (err) {
    console.warn(`PDF attempt failed for ${browser}:`, err?.message ?? err);
  }
}

if (!pdfWritten) {
  console.log('PDF not auto-generated (Chrome/Edge headless unavailable).');
  console.log('Open the HTML file in a browser and Print → Save as PDF.');
}
