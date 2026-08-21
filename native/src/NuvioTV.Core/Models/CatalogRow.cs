using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents a catalog row of items in the UI.
    /// Source: js/domain/model/catalogRow.js createCatalogRow
    /// </summary>
    public sealed class CatalogRow
    {
        [JsonPropertyName("addonId")]
        public string AddonId { get; }

        [JsonPropertyName("addonName")]
        public string AddonName { get; }

        [JsonPropertyName("addonBaseUrl")]
        public string AddonBaseUrl { get; }

        [JsonPropertyName("catalogId")]
        public string CatalogId { get; }

        [JsonPropertyName("catalogName")]
        public string CatalogName { get; }

        [JsonPropertyName("apiType")]
        public string ApiType { get; }

        [JsonPropertyName("items")]
        public IReadOnlyList<Meta> Items { get; }

        [JsonPropertyName("isLoading")]
        public bool IsLoading { get; }

        [JsonPropertyName("hasMore")]
        public bool HasMore { get; }

        [JsonPropertyName("currentPage")]
        public int CurrentPage { get; }

        [JsonPropertyName("supportsSkip")]
        public bool SupportsSkip { get; }

        public CatalogRow(
            string addonId,
            string addonName,
            string addonBaseUrl,
            string catalogId,
            string catalogName,
            string apiType,
            IReadOnlyList<Meta> items,
            bool isLoading,
            bool hasMore,
            int currentPage,
            bool supportsSkip
        )
        {
            AddonId = addonId;
            AddonName = addonName;
            AddonBaseUrl = addonBaseUrl;
            CatalogId = catalogId;
            CatalogName = catalogName;
            ApiType = apiType;
            Items = items;
            IsLoading = isLoading;
            HasMore = hasMore;
            CurrentPage = currentPage;
            SupportsSkip = supportsSkip;
        }
    }
}
