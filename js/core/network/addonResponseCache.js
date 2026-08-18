const SUCCESS_TTL_MS = 60_000;
const NEGATIVE_TTL_MS = 15_000;
const MAX_ENTRIES = 500;

function isCacheableHttpError(error) {
  return Boolean(error && typeof error === "object" && Number.isFinite(error?.status));
}

class AddonResponseCache {
  constructor() {
    this.entries = new Map();
  }

  get(key) {
    const entry = this.entries.get(key);
    if (!entry) {
      return undefined;
    }
    if (Date.now() >= entry.expiresAt) {
      this.entries.delete(key);
      return undefined;
    }
    // Refresh recency so the eviction sweep below keeps this entry alive.
    this.entries.delete(key);
    this.entries.set(key, entry);
    return entry;
  }

  set(key, value, isError, ttlMs) {
    const entry = { value, isError, expiresAt: Date.now() + ttlMs };
    this.entries.delete(key);
    this.entries.set(key, entry);
    while (this.entries.size > MAX_ENTRIES) {
      const oldestKey = this.entries.keys().next().value;
      this.entries.delete(oldestKey);
    }
    return entry;
  }

  async wrap(key, fetchFn) {
    const cacheKey = String(key || "");
    const cached = this.get(cacheKey);
    if (cached) {
      if (cached.isError) {
        throw cached.value;
      }
      return cached.value;
    }

    try {
      const value = await fetchFn();
      this.set(cacheKey, value, false, SUCCESS_TTL_MS);
      return value;
    } catch (error) {
      // Cache definitive HTTP failures (they carry a status) briefly so a
      // missing meta does not re-hit the network on every navigation.
      // Network/abort errors carry no status and are never cached.
      if (isCacheableHttpError(error)) {
        this.set(cacheKey, error, true, NEGATIVE_TTL_MS);
      }
      throw error;
    }
  }

  clear() {
    this.entries.clear();
  }
}

export const addonResponseCache = new AddonResponseCache();
