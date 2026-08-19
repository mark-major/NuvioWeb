using System;
using System.Threading.Tasks;

namespace NuvioTV.Core.Networking
{
    public static class SafeApiCall
    {
        public static async Task<NetworkResult<T>> ExecuteAsync<T>(Func<Task<T>> apiCall)
        {
            try
            {
                var response = await apiCall();
                return NetworkResult<T>.Success(response);
            }
            catch (NuvioHttpException httpEx)
            {
                return NetworkResult<T>.Error(httpEx.Detail ?? "HTTP error", httpEx.Code);
            }
            catch (Exception ex)
            {
                return NetworkResult<T>.Error(ex.Message ?? "Unknown error occurred");
            }
        }
    }
}