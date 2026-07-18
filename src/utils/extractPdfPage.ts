import { PDFDocument } from 'pdf-lib';
import type { Task } from '../types';
import { SSV3_FIELD } from './boardroomPackageImport';

/** True when the export is the multi-sheet Spools Combined PDF. */
export function isSpoolsCombinedPdf(fileName: string): boolean {
  return /spools?\s*combined/i.test(fileName);
}

/**
 * Resolve 0-based page index for an assembly inside Spools Combined.pdf.
 * SSv3 plots sheets ordered by SheetNumber, then sheet name.
 */
export function resolveAssemblyPdfPageIndex(
  assemblies: Task[],
  assembly: Task
): number | null {
  if (assemblies.length === 0) return null;

  const ordered = [...assemblies].sort((a, b) => {
    const aNum = (a.customFields?.[SSV3_FIELD.sheetNumber] ?? '').trim();
    const bNum = (b.customFields?.[SSV3_FIELD.sheetNumber] ?? '').trim();
    if (aNum && bNum && aNum !== bNum) {
      return aNum.localeCompare(bNum, undefined, { numeric: true, sensitivity: 'base' });
    }
    const aName = (a.customFields?.[SSV3_FIELD.sheetName] ?? a.title).trim();
    const bName = (b.customFields?.[SSV3_FIELD.sheetName] ?? b.title).trim();
    return aName.localeCompare(bName, undefined, { sensitivity: 'base' });
  });

  const index = ordered.findIndex((task) => task.id === assembly.id);
  return index >= 0 ? index : null;
}

/** Hide Chromium PDF chrome (toolbar + thumbnail pane). */
export function withCleanPdfViewerParams(url: string): string {
  const cleanHash = 'toolbar=0&navpanes=0&scrollbar=0&view=FitH';
  const hashIndex = url.indexOf('#');
  if (hashIndex === -1) return `${url}#${cleanHash}`;
  const base = url.slice(0, hashIndex);
  const existing = url.slice(hashIndex + 1).trim();
  if (!existing) return `${base}#${cleanHash}`;
  return `${base}#${existing}&${cleanHash}`;
}
export async function extractPdfPageBlobUrl(
  pdfBytes: ArrayBuffer,
  pageIndex: number
): Promise<{ url: string; pageCount: number }> {
  const source = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
  const pageCount = source.getPageCount();
  if (pageIndex < 0 || pageIndex >= pageCount) {
    throw new Error(
      `Sheet page ${pageIndex + 1} is outside this PDF (${pageCount} page${pageCount === 1 ? '' : 's'}).`
    );
  }
  const isolated = await PDFDocument.create();
  const [page] = await isolated.copyPages(source, [pageIndex]);
  isolated.addPage(page);
  const bytes = await isolated.save();
  const copy = new Uint8Array(bytes);
  const url = URL.createObjectURL(new Blob([copy], { type: 'application/pdf' }));
  return { url, pageCount };
}
