import { httpRequest } from "../../../core/network/httpClient.js";
import { addonResponseCache } from "../../../core/network/addonResponseCache.js";

export const CatalogApi = {
  async getCatalog(url, options = {}) {
    return addonResponseCache.wrap(url, () =>
      httpRequest(url, {
        ...options,
        includeSessionAuth: false
      })
    );
  }
};
