/**
 * The operator key a `LicensedCommercial` deployment requires on writes.
 *
 * The API's compliance guard (`ComplianceGuardMiddleware`) asks for `X-PseDesk-Key` on every
 * `POST`/`PUT`/`PATCH`/`DELETE` once `Compliance:OperatorApiKey` is set, and fails closed (`503`)
 * when a licensed deployment has no key at all. Until this module existed the header was only
 * ever written by `curl`, so the documented licensed mode could not be driven from the terminal:
 * every write answered `401`.
 *
 * The key is read once from `VITE_OPERATOR_API_KEY` at build time. It is **not** a user login and
 * it is not a secret the browser can keep — anything shipped in a bundle is readable — so it is
 * only useful for a single-operator deployment behind its own network boundary. A real multi-user
 * deployment needs per-user identity (OIDC), which this project does not have.
 *
 * Reads deliberately carry nothing: the server ignores the header on `GET`, and a secret is not
 * something to send on every list request.
 */

export const OPERATOR_KEY_HEADER = 'X-PseDesk-Key';

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS', 'TRACE']);

/**
 * Header fragment for one request. Returns `{}` for a safe method or a blank key, so callers can
 * spread it into their header object unconditionally.
 */
export function operatorKeyHeaders(method: string, key: string | undefined): Record<string, string> {
  const trimmed = (key ?? '').trim();

  if (trimmed.length === 0) {
    return {};
  }

  if (SAFE_METHODS.has((method ?? '').trim().toUpperCase())) {
    return {};
  }

  return { [OPERATOR_KEY_HEADER]: trimmed };
}
