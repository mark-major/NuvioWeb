import { httpRequest } from "../../../core/network/httpClient.js";
import { addonResponseCache } from "../../../core/network/addonResponseCache.js";

export const MetaApi = {
  async getMeta(url) {
    return addonResponseCache.wrap(url, () =>
      httpRequest(url, {
        includeSessionAuth: false
      })
    );
  }
};
