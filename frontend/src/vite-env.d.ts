/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_BASE_URL?: string;
  /**
   * Shared operator key for a `LicensedCommercial` deployment (sent as `X-PseDesk-Key` on writes).
   * Leave unset for the personal loopback desk, where writes need no key.
   */
  readonly VITE_OPERATOR_API_KEY?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
