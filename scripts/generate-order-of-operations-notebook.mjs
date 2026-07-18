/**
 * Builds an iPad-friendly notebook from ORDER_OF_OPERATIONS.md:
 * - ORDER_OF_OPERATIONS_NOTEBOOK.html (open in browser → Print → PDF)
 * - ORDER_OF_OPERATIONS_NOTEBOOK.pdf (when Chrome/Edge headless is available)
 *
 * PDF + Apple Pencil works in: GoodNotes, Notability, Freeform, Files Markup, Preview.
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { execSync } from 'child_process';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, '..');
const sourcePath = path.join(root, 'ORDER_OF_OPERATIONS.md');
const outDir = path.join(root, 'docs');
const htmlPath = path.join(outDir, 'ORDER_OF_OPERATIONS_NOTEBOOK.html');
const pdfPath = path.join(outDir, 'ORDER_OF_OPERATIONS_NOTEBOOK.pdf');

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
  subsection: 20,
  section: 34,
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
    const trimmed = line.trim();

    if (!trimmed) {
      i += 1;
      continue;
    }

    if (trimmed.startsWith('```')) {
      const fence = trimmed;
      i += 1;
      const codeLines = [];
      while (i < lines.length && !lines[i].trim().startsWith('```')) {
        codeLines.push(lines[i]);
        i += 1;
      }
      if (i < lines.length) i += 1;
      if (fence.includes('mermaid')) {
        html.push(
          '<p class="diagram-note"><em>Flow diagram — see the full ORDER_OF_OPERATIONS.md in the repo for the mermaid chart.</em></p>'
        );
      } else {
        html.push(`<pre><code>${escapeHtml(codeLines.join('\n'))}</code></pre>`);
      }
      continue;
    }

    if (trimmed === '---') {
      html.push('<hr />');
      i += 1;
      continue;
    }

    if (trimmed.startsWith('#### ')) {
      html.push(`<h4>${inlineMarkdown(trimmed.slice(5))}</h4>`);
      i += 1;
      continue;
    }

    if (trimmed.startsWith('### ')) {
      html.push(`<h3>${inlineMarkdown(trimmed.slice(4))}</h3>`);
      i += 1;
      continue;
    }

    if (trimmed.startsWith('## ')) {
      html.push(`<h2>${inlineMarkdown(trimmed.slice(3))}</h2>`);
      i += 1;
      continue;
    }

    if (trimmed.startsWith('# ')) {
      html.push(`<h1>${inlineMarkdown(trimmed.slice(2))}</h1>`);
      i += 1;
      continue;
    }

    if (trimmed.startsWith('|') && i + 1 < lines.length && isTableSeparator(lines[i + 1])) {
      const header = parseTableRow(line);
      i += 2;
      const rows = [];
      while (i < lines.length && lines[i].trim().startsWith('|')) {
        rows.push(parseTableRow(lines[i]));
        i += 1;
      }
      html.push('<table><thead><tr>');
      for (const cell of header) {
        html.push(`<th>${inlineMarkdown(cell)}</th>`);
      }
      html.push('</tr></thead><tbody>');
      for (const row of rows) {
        html.push('<tr>');
        for (const cell of row) {
          html.push(`<td>${inlineMarkdown(cell)}</td>`);
        }
        html.push('</tr>');
      }
      html.push('</tbody></table>');
      continue;
    }

    if (trimmed.startsWith('- ')) {
      html.push('<ul>');
      while (i < lines.length && lines[i].trim().startsWith('- ')) {
        html.push(`<li>${inlineMarkdown(lines[i].trim().slice(2))}</li>`);
        i += 1;
      }
      html.push('</ul>');
      continue;
    }

    const para = [trimmed];
    i += 1;
    while (i < lines.length) {
      const next = lines[i].trim();
      if (
        !next ||
        next.startsWith('#') ||
        next.startsWith('- ') ||
        next.startsWith('|') ||
        next === '---' ||
        next.startsWith('```')
      ) {
        break;
      }
      para.push(next);
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
  return /^Part [A-D] —/.test(title);
}

/** Render full doc with note pads after every ### and generous blanks after every ##. */
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
      html.push(
        noteBlock('subsection', `Notes — ${subsectionTitleText}`, NOTE_LINES.subsection)
      );
    }
  };

  const closeSection = () => {
    flushSubsection();
    if (!inSection) return;

    html.push(noteBlock('section', `Notes — ${sectionTitleText} (summary)`, NOTE_LINES.section));
    html.push(blankPage(`Extra notes — ${sectionTitleText}`, NOTE_LINES.section));

    if (isPartSection(sectionTitleText)) {
      html.push(blankPage(`More notes — ${sectionTitleText}`, NOTE_LINES.part));
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

  html.push(blankPage('General notes', NOTE_LINES.extraPage));
  html.push(blankPage('General notes (continued)', NOTE_LINES.extraPage));
  html.push(blankPage('Scratch pad', NOTE_LINES.extraPage));

  return { body: html.join('\n'), toc };
}

const { body: documentBody, toc } = renderDocumentWithNotes(md);

const html = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Order of Operations — Notebook</title>
  <style>
    :root {
      --ink: #1a1f2e;
      --muted: #5c6478;
      --rule: #c5cad6;
      --note-bg: #fafbfe;
      --accent: #3d5a80;
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
    ul { margin: 0.35rem 0 0.75rem 1.2rem; padding: 0; }
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

    .diagram-note { color: var(--muted); font-style: italic; }

    .note-area,
    .blank-page {
      margin: 1.5rem 0;
      padding: 0.75rem 1rem 1rem;
      background: var(--note-bg);
      border: 1.5px dashed var(--accent);
      border-radius: 8px;
    }

    .note-area-subsection { min-height: 4.5in; }
    .note-area-section { min-height: 7in; }

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
      .note-area-subsection { min-height: 4in; }
      .note-area-section { min-height: 6.5in; }
      .blank-page { min-height: 8.5in; }
      .no-print { display: none; }
    }

    @page {
      margin: 0.75in;
      size: letter;
    }
  </style>
</head>
<body>
  <section class="cover">
    <h1>Order of Operations</h1>
    <p class="subtitle">BIM Boardroom + SSv3 + Fab — personal notebook edition</p>
    <p>Generated from <strong>ORDER_OF_OPERATIONS.md</strong>. Every subsection includes a note pad; each major part adds full blank pages for handwriting.</p>

    <div class="ipad-box">
      <h2>Using this on iPad (Apple Pencil)</h2>
      <ul>
        <li><strong>Best format:</strong> PDF — import into <strong>GoodNotes</strong>, <strong>Notability</strong>, <strong>Freeform</strong>, or open in <strong>Files</strong> and tap Markup.</li>
        <li><strong>Get the PDF:</strong> Open the <code>.html</code> sibling file in Safari or Edge → Share / Print → <strong>Save as PDF</strong> → AirDrop or iCloud to iPad.</li>
        <li><strong>Or:</strong> Copy <code>ORDER_OF_OPERATIONS_NOTEBOOK.pdf</code> from the <code>docs/</code> folder if the build script created it on your PC.</li>
        <li><strong>Write on it:</strong> Use Apple Pencil directly on the PDF; add extra blank pages in GoodNotes/Notability if you need more room.</li>
      </ul>
    </div>

    <div class="toc">
      <h2>Contents</h2>
      <ol>
        ${toc.map((title) => `<li>${escapeHtml(title)}</li>`).join('\n        ')}
      </ol>
    </div>

    <p class="no-print" style="margin-top:2rem;color:var(--muted);font-size:0.9rem;">
      Tip on PC: open this HTML file in a browser and press <strong>Ctrl+P</strong> → Save as PDF.
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
      { stdio: 'pipe', timeout: 120000 }
    );
    if (fs.existsSync(pdfPath)) {
      pdfWritten = true;
      console.log(`Wrote ${pdfPath}`);
      break;
    }
  } catch {
    /* try next browser */
  }
}

if (!pdfWritten) {
  console.log('PDF not auto-generated (Chrome/Edge headless unavailable).');
  console.log('Open the HTML file in a browser and Print → Save as PDF.');
}
