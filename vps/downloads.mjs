// Backwards-compatible APK download catalogue.
//
// The gateway now serves a category-ID resource centre (see resources.mjs); the
// historic `apk` kind is gone, but old callers/tests/scripts that import
// `vps/downloads.mjs` keep working:
//
//   DOWNLOAD_PAGE_PATH, APK_FILE_PATTERN, parseApkName,
//   scanDownloads, resolveDownload, summarizeDownloads, hashEntries
//
// `scanDownloads`/`resolveDownload`/`summarizeDownloads` accept a directory
// string and operate on the legacy APK_DIR layout; `summarizeDownloads(config)`
// counts the `.apk` files of the category centre. New code should import from
// resources.mjs.
export {
  APK_FILE_PATTERN,
  DOWNLOAD_PAGE_PATH,
  hashEntries,
  parseApkName,
  resolveDownload,
  scanDownloads,
  summarizeDownloads
} from './resources.mjs'
