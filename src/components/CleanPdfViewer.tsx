import { withCleanPdfViewerParams } from '../utils/extractPdfPage';
import styles from './FabWorkstationView.module.css';

interface CleanPdfViewerProps {
  /** Blob URL for a PDF (single sheet when opened from an assembly). */
  src: string;
  title: string;
  /** Queue Dashboard: hide PDF chrome / download affordances. */
  viewOnly?: boolean;
}

/**
 * Native Chromium PDF view (same quality as Edge), with toolbar/thumbnails hidden.
 */
export function CleanPdfViewer({ src, title, viewOnly = false }: CleanPdfViewerProps) {
  return (
    <div
      className={styles.pdfStage}
      aria-label={title}
      onContextMenu={viewOnly ? (event) => event.preventDefault() : undefined}
    >
      <iframe
        className={styles.previewFrameInteractive}
        title={title}
        src={withCleanPdfViewerParams(src)}
      />
    </div>
  );
}
